using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Vision;

/// <summary>
/// Outcome of <see cref="TooltipRecognizer"/>: the inspected reading and reconciled rarity. When
/// comparison mode is requested, <see cref="EquippedReading"/> is a separately refined right-side
/// reading; <see cref="Reading"/> remains the left inspected item for UI/comparison output.
/// </summary>
public sealed record TooltipRecognition(
    TooltipReading Reading,
    string? Rarity,
    IReadOnlyList<TooltipReading>? AllReadings = null,
    TooltipRecognitionTiming? Timing = null,
    TooltipReading? EquippedReading = null,
    string? EquippedRarity = null)
{
    /// <summary>
    /// All left-to-right tooltip readings when comparison mode was requested; otherwise the inspected
    /// reading only. The right-most entry is the current-equipped comparison tooltip.
    /// </summary>
    public IReadOnlyList<TooltipReading> Readings => AllReadings ?? new[] { Reading };
}

/// <summary>Stage timings emitted with one recognition result for live latency diagnostics.</summary>
public sealed record TooltipRecognitionTiming(
    double TotalMs,
    double LocatorMs,
    double CropMs,
    double RarityMs,
    double QualityMs,
    double SlotMs,
    double StatsNameMs,
    double StatsValueMs,
    double WindowsOcrMs,
    double OnnxOcrMs,
    double ItemNameOcrMs,
    double PostprocessingMs,
    double LabelOcrMs,
    bool LocatorFound,
    bool FastPathUsed,
    bool FallbackUsed,
    string? FallbackReason);

public enum TooltipRecognitionMode
{
    /// <summary>Use the complete hybrid OCR path when the fast path cannot resolve all fields.</summary>
    Balanced,

    /// <summary>Return the pixel-located single-tooltip result without waiting for value OCR.</summary>
    Immediate,
}

/// <summary>Recognition seam consumed by the live app loop and testable without native OCR.</summary>
public interface ITooltipRecognizer
{
    Task<TooltipRecognition> RecognizeAsync(
        byte[] imageBytes, (int X, int Y)? cursor = null,
        CancellationToken ct = default, bool includeComparison = false,
        TooltipRecognitionMode mode = TooltipRecognitionMode.Balanced,
        bool includeItemName = true);
}


/// <summary>
/// Two recognition modes are available. <see cref="TooltipRecognitionMode.Immediate"/> locates one
/// tooltip and performs a reduced Windows OCR pass for the first live result. The default balanced mode
/// adds cropped label OCR and ONNX value OCR, falling back to the complete strip path when necessary.
/// Rarity still comes from label/name color and is cross-checked with OCR for the warm legendary/epic pair.
/// </summary>
public sealed class TooltipRecognizer : ITooltipRecognizer
{
    private readonly IOcrService _ocr;
    private readonly ITooltipParser _parser;
    private readonly IRarityColorReader _color;
    private readonly IOcrService? _valueOcr;

    private sealed class TimingBuilder
    {
        public double LocatorMs;
        public double CropMs;
        public double RarityMs;
        public double QualityMs;
        public double SlotMs;
        public double StatsNameMs;
        public double StatsValueMs;
        public double WindowsOcrMs;
        public double OnnxOcrMs;
        public double PostprocessingMs;
        public double LabelOcrMs;
        public bool LocatorFound;
        public bool FastPathUsed;
        public bool FallbackUsed;
        public string? FallbackReason;

        public TooltipRecognitionTiming Build(double totalMs) =>
            new(totalMs, LocatorMs, CropMs, RarityMs, QualityMs, SlotMs, StatsNameMs, StatsValueMs,
                WindowsOcrMs, OnnxOcrMs, 0, PostprocessingMs, LabelOcrMs,
                LocatorFound, FastPathUsed, FallbackUsed, FallbackReason);
    }

    /// <param name="valueOcr">Optional second engine (PP-OCRv5 ONNX) used only to read the small stat
    /// digits + quality% the primary engine drops; slot/grade/rarity stay with the primary pass. It runs
    /// on the tooltip crop (not the full capture), so it stays fast and reads the small pixel-font digits
    /// at the same scale the benchmark crops use.</param>
    public TooltipRecognizer(IOcrService ocr, ITooltipParser parser, IRarityColorReader color,
        IOcrService? valueOcr = null)
    {
        _ocr = ocr;
        _parser = parser;
        _color = color;
        _valueOcr = valueOcr;
    }

    private const double FirstPassScale = 1.6;
    private const double FirstPassRetryScale = 2.0;

    // Pass-1 upscales, measured on the 40-image live-capture bench: x1.6 finds the grade line in
    // 36/40 at ~395ms vs x2.0's 34/40 at ~431ms (over-zoom blurs some grade rows). The rare miss
    // that still *looks* like a tooltip (rarity/stat lines present) retries once at x2.0.
    public async Task<TooltipRecognition> RecognizeAsync(byte[] imageBytes, (int X, int Y)? cursor = null,
        CancellationToken ct = default, bool includeComparison = false,
        TooltipRecognitionMode mode = TooltipRecognitionMode.Balanced,
        bool includeItemName = true)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var timing = new TimingBuilder();

        // Immediate mode is deliberately single-tooltip and never enters the full-strip OCR path.
        // The app can optionally request the balanced path later for a missing numeric value; the
        // first UI result must not wait for ONNX or an API request.
        if (!includeComparison && mode == TooltipRecognitionMode.Immediate)
        {
            var immediate = await TryImmediatePathAsync(
                imageBytes, cursor, timing, ct).ConfigureAwait(false);
            return immediate is not null
                ? StripItemName(immediate with { Timing = timing.Build(totalSw.Elapsed.TotalMilliseconds) }, includeItemName)
                : EmptyRecognition(timing.Build(totalSw.Elapsed.TotalMilliseconds));
        }

        if (!includeComparison
            && _valueOcr is OnnxOcrService { SupportsLineRecognition: true } lineOcr)
        {
            var fast = await TryFastPathAsync(
                imageBytes, cursor, lineOcr, timing, ct).ConfigureAwait(false);
            if (fast is not null)
                return StripItemName(fast with { Timing = timing.Build(totalSw.Elapsed.TotalMilliseconds) }, includeItemName);
        }

        var first = await RecognizeWindowsTimedAsync(imageBytes, ct, timing, FirstPassScale).ConfigureAwait(false);
        var tooltips = ParseAllTimed(first.Lines, timing);
        var basePass = tooltips[0];          // leftmost grade = the inspected (hovered) item

        // Grade not found but the capture reads like a tooltip → one escalated retry. An empty
        // screen/background yields none of these signals, so idle polls stay single-pass.
        if (basePass.GradeBox is null
            && (basePass.RarityLabel is not null || basePass.MainStatValues.Count > 0 || basePass.BareMainStat is not null))
        {
            var retry = await RecognizeWindowsTimedAsync(imageBytes, ct, timing, FirstPassRetryScale).ConfigureAwait(false);
            var retryTips = ParseAllTimed(retry.Lines, timing);
            if (retryTips[0].GradeBox is not null)
            {
                first = retry;
                tooltips = retryTips;
                basePass = tooltips[0];
            }
        }
        bool isComparison = tooltips.Count >= 2;

        var refined = await RefineReadingAsync(
            imageBytes, basePass, first.Lines, isComparison, false, timing, ct).ConfigureAwait(false);

        IReadOnlyList<TooltipReading>? allReadings = null;
        TooltipReading? equippedReading = null;
        string? equippedRarity = null;
        if (includeComparison)
        {
            // Keep the public list aligned with the primary result even when only one tooltip was
            // found; callers can rely on Readings[0] == Reading.
            var refinedReadings = tooltips.ToArray();
            refinedReadings[0] = refined.Reading;
            if (tooltips.Count >= 2)
            {
                // Comparison mode still reports the left inspected item to the UI, but save mode needs
                // the right equipped item. Refine that grade anchor independently so the left crop's
                // coordinates and values cannot leak into the saved candidate.
                var equipped = await RefineReadingAsync(
                    imageBytes, tooltips[^1], first.Lines, true, true, timing, ct).ConfigureAwait(false);
                equippedReading = equipped.Reading;
                equippedRarity = equipped.Rarity;
                refinedReadings[^1] = equippedReading;
            }
            allReadings = refinedReadings;
        }

        var result = new TooltipRecognition(
            refined.Reading, refined.Rarity, allReadings,
            timing.Build(totalSw.Elapsed.TotalMilliseconds),
            equippedReading, equippedRarity);
        return StripItemName(result, includeItemName);
    }

    /// <summary>
    /// Runs the existing crop/label/value correction pipeline for one grade anchor. The same routine
    /// handles the left inspected tooltip and the right equipped comparison tooltip; only the anchor
    /// and the optional shared-slot fallback differ.
    /// </summary>
    private async Task<(TooltipReading Reading, string? Rarity)> RefineReadingAsync(
        byte[] imageBytes, TooltipReading basePass, IReadOnlyList<OcrLine> contextLines,
        bool allowComparisonSlotFallback, bool forceGradeAnchor, TimingBuilder timing, CancellationToken ct)
    {
        var reading = basePass;
        byte[] colorSource = imageBytes;

        // Pass 2/3 + ONNX crops from a single decode of the capture strip.
        byte[]? crop = null, labels = null, valueCrop = null;
        if (basePass.GradeBox is { } grade)
        {
            var cropSw = System.Diagnostics.Stopwatch.StartNew();
            (crop, labels, valueCrop) = TooltipCropper.CropAll(imageBytes, grade, forceGradeAnchor);
            cropSw.Stop();
            timing.CropMs += cropSw.Elapsed.TotalMilliseconds;
        }
        System.Diagnostics.Stopwatch? valueSw = null;
        Task<OcrResult>? valueTask = null;
        if (_valueOcr is { IsAvailable: true } vo && !ReferenceEquals(vo, _ocr))
        {
            valueSw = System.Diagnostics.Stopwatch.StartNew();
            valueTask = vo.RecognizeAsync(valueCrop ?? crop ?? imageBytes, ct);
        }

        // Pass 2: re-OCR the cropped tooltip upscaled for cleaner stats/name. Adopt it only when it
        // still resolves a grade (the crop landed on the requested tooltip).
        string? labelSlot = null, labelRarity = null;
        var windowsTask = RefineWindowsAsync();
        if (valueTask is not null)
            await Task.WhenAll(valueTask, windowsTask).ConfigureAwait(false);
        else
            await windowsTask.ConfigureAwait(false);

        async Task RefineWindowsAsync()
        {
            if (crop is not null)
            {
                var second = await RecognizeWindowsTimedAsync(crop, ct, timing).ConfigureAwait(false);
                var refined = ParseTimed(second.Lines, timing);
                if (refined.GradeBox is not null)
                {
                    reading = TooltipStructuredReader.Apply(refined, second.Lines);
                    colorSource = crop;
                }
            }

            // Pass 3: re-OCR just the right-aligned label column. Isolated and upscaled, OCR reads the tiny
            // 2-char 부위/등급 labels that the full-tooltip passes drop entirely.
            if (labels is not null)
            {
                var third = await RecognizeWindowsTimedAsync(
                    labels, ct, timing, 2.0, labelPass: true).ConfigureAwait(false);
                var lines = third.Lines.Select(l => l.Text).ToList();
                labelSlot = TooltipParser.ResolveSlotLabels(lines);
                labelRarity = TooltipParser.ResolveRarity(lines);
            }
        }

        // Await the parallel ONNX pass; it reads small stat digits/quality and can restore a dropped
        // slot or rarity label without changing the geometry selected by the primary pass.
        TooltipReading? valuePass = null;
        if (valueTask is not null)
        {
            var vres = await valueTask.ConfigureAwait(false);
            valueSw?.Stop();
            if (valueSw is not null) timing.OnnxOcrMs += valueSw.Elapsed.TotalMilliseconds;
            var vtips = ParseAllTimed(vres.Lines, timing);
            if (vtips.Count > 0) valuePass = vtips[0];
        }

        // Keep the full-tooltip/crop slot when available; only use focused labels when they agree.
        // A comparison pair shares one equipment slot, so the other grade's global slot token is a
        // safe final fallback for the right reading.
        var slotSw = System.Diagnostics.Stopwatch.StartNew();
        string? slot = reading.Slot ?? basePass.Slot ?? valuePass?.Slot;
        if (labelSlot is not null && (slot is null || string.Equals(labelSlot, slot, StringComparison.Ordinal)))
            slot = labelSlot;
        if (slot is null && allowComparisonSlotFallback)
            slot = TooltipParser.ResolveSlot(contextLines.Select(l => l.Text));
        slotSw.Stop();
        timing.SlotMs += slotSw.Elapsed.TotalMilliseconds;

        var raritySw = System.Diagnostics.Stopwatch.StartNew();
        string? label = labelRarity ?? reading.RarityLabel ?? basePass.RarityLabel;
        string? color = crop is not null && _color is RarityColorReader concreteColor
            ? TooltipFiniteClassReader.ReadRarity(crop, concreteColor)
            : null;
        color ??= _color.DetectRarity(colorSource, reading.RarityBox, reading.NameBox);
        if (color is null && basePass.GradeBox is { } gradeAnchor)
        {
            // A wrapped comparison name may leave ParseColumn's last pre-grade line on the set-name
            // row. Sample colored pre-grade lines near this anchor, excluding the comparison header;
            // never reuse the left tooltip's rarity for the equipped item.
            var nameBoxes = contextLines
                .Where(line => line.Top < gradeAnchor.Top
                    && line.Top >= gradeAnchor.Top - 75
                    && line.Left >= gradeAnchor.Left - 30
                    && line.Left <= gradeAnchor.Left + 200)
                .OrderBy(line => line.Top)
                .Cast<OcrLine?>()
                .ToArray();
            color = _color.DetectRarity(imageBytes, nameBoxes);
        }
        color ??= _color.DetectRarity(imageBytes, basePass.RarityBox, basePass.NameBox);
        raritySw.Stop();
        timing.RarityMs += raritySw.Elapsed.TotalMilliseconds;

        // Stats OCR per-pass too: the tooltip crop sometimes drops a stat keyword the full pass read.
        var statsSw = System.Diagnostics.Stopwatch.StartNew();
        var stats = new Dictionary<string, int>(reading.MainStatValues);
        foreach (var kv in basePass.MainStatValues) stats.TryAdd(kv.Key, kv.Value);
        statsSw.Stop();
        timing.StatsNameMs += statsSw.Elapsed.TotalMilliseconds;
        timing.StatsValueMs += statsSw.Elapsed.TotalMilliseconds;

        // Grade tier/quality% vary per pass too: the crop can lose the quality's second digit.
        var qualitySw = System.Diagnostics.Stopwatch.StartNew();
        string? gradeTier = reading.GradeTier ?? basePass.GradeTier ?? valuePass?.GradeTier;
        int? gradePercent = (reading.GradePercent, basePass.GradePercent) switch
        {
            (int a, int b) => Math.Max(a, b),
            (int a, null) => a,
            (null, int b) => b,
            _ => null,
        };
        int? bareMainStat = reading.BareMainStat ?? basePass.BareMainStat;
        reading = reading with
        {
            Slot = slot, GradeTier = gradeTier, GradePercent = gradePercent,
            MainStatValues = stats, BareMainStat = bareMainStat,
        };
        qualitySw.Stop();
        timing.QualityMs += qualitySw.Elapsed.TotalMilliseconds;

        // The secondary ONNX pass supplies missing numeric fields; preserve a primary value when it
        // agrees with the peer-stat cluster and the secondary candidate is an isolated outlier.
        string? valueRarity = null;
        if (valuePass is { } v)
        {
            var valueStatsSw = System.Diagnostics.Stopwatch.StartNew();
            valueRarity = v.RarityLabel;
            var mergedStats = new Dictionary<string, int>(reading.MainStatValues);
            MergeValueStats(mergedStats, v.MainStatValues);
            valueStatsSw.Stop();
            timing.StatsNameMs += valueStatsSw.Elapsed.TotalMilliseconds;
            timing.StatsValueMs += valueStatsSw.Elapsed.TotalMilliseconds;
            var valueQualitySw = System.Diagnostics.Stopwatch.StartNew();
            reading = reading with
            {
                MainStatValues = mergedStats,
                BareMainStat = v.BareMainStat ?? reading.BareMainStat,
                GradePercent = (reading.GradePercent, v.GradePercent) switch
                {
                    (int a, int b) => Math.Max(a, b),
                    (int a, null) => a,
                    (null, int b) => b,
                    _ => null,
                },
            };
            valueQualitySw.Stop();
            timing.QualityMs += valueQualitySw.Elapsed.TotalMilliseconds;
        }

        string? rarity = ReconcileRarity(color, label ?? valueRarity);

        return (reading, rarity);
    }

    private static TooltipRecognition StripItemName(
        TooltipRecognition result, bool includeItemName)
    {
        if (includeItemName) return result;
        var reading = result.Reading with { ItemName = null };
        var readings = result.AllReadings?.Select(x => x with { ItemName = null }).ToArray();
        var equipped = result.EquippedReading is { } e ? e with { ItemName = null } : null;
        return result with { Reading = reading, AllReadings = readings, EquippedReading = equipped };
    }

    private static void MergeValueStats(
        Dictionary<string, int> target, IReadOnlyDictionary<string, int> candidate)
    {
        foreach (var pair in candidate)
        {
            if (target.TryGetValue(pair.Key, out int existing) && target.Count >= 3)
            {
                var peers = target
                    .Where(existingPair => !string.Equals(existingPair.Key, pair.Key, StringComparison.Ordinal))
                    .Select(existingPair => existingPair.Value)
                    .OrderBy(value => value)
                    .ToArray();
                if (peers.Length >= 2)
                {
                    int median = peers[peers.Length / 2];
                    bool peersFormCluster = peers[^1] - peers[0] <= 20;
                    bool existingFitsCluster = Math.Abs(existing - median) <= 20;
                    bool candidateIsOutlier = Math.Abs(pair.Value - median) >= 35;
                    if (peersFormCluster && existingFitsCluster && candidateIsOutlier)
                        continue;
                }
            }
            target[pair.Key] = pair.Value;
        }
    }

    private TooltipReading ParseTimed(IReadOnlyList<OcrLine> lines, TimingBuilder timing)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var reading = _parser.Parse(lines);
        sw.Stop();
        timing.PostprocessingMs += sw.Elapsed.TotalMilliseconds;
        return reading;
    }

    private IReadOnlyList<TooltipReading> ParseAllTimed(
        IReadOnlyList<OcrLine> lines, TimingBuilder timing)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var readings = _parser.ParseAll(lines);
        sw.Stop();
        timing.PostprocessingMs += sw.Elapsed.TotalMilliseconds;
        return readings;
    }

    private static TooltipRecognition EmptyRecognition(TooltipRecognitionTiming timing) =>
        new(new TooltipReading(
            Array.Empty<string>(), null, null, null, null,
            new Dictionary<string, int>(), null, null, null, null, null), null,
            null, timing);

    private async Task<OcrResult> RecognizeWindowsTimedAsync(byte[] imageBytes, CancellationToken ct,
        TimingBuilder timing, double maxScale = 4.0, bool labelPass = false)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await _ocr.RecognizeAsync(imageBytes, ct, maxScale).ConfigureAwait(false);
        sw.Stop();
        timing.WindowsOcrMs += sw.Elapsed.TotalMilliseconds;
        if (labelPass) timing.LabelOcrMs += sw.Elapsed.TotalMilliseconds;
        return result;
    }

    /// <summary>
    /// Low-latency single-tooltip recognition. Pixel geometry selects the tooltip first, then one
    /// reduced-scale Windows OCR pass reads the visible item. The label column is only OCR'd when the
    /// primary pass did not resolve slot or rarity; numeric ONNX OCR is intentionally deferred to the
    /// caller so the first live result never waits on the slowest engine.
    /// </summary>
    private async Task<TooltipRecognition?> TryImmediatePathAsync(
        byte[] imageBytes, (int X, int Y)? cursor, TimingBuilder timing, CancellationToken ct)
    {
        var locateSw = System.Diagnostics.Stopwatch.StartNew();
        var located = await Task.Run(() => TooltipLocator.Locate(imageBytes, cursor), ct).ConfigureAwait(false);
        locateSw.Stop();
        timing.LocatorMs += locateSw.Elapsed.TotalMilliseconds;
        timing.LocatorFound = located is not null;
        if (located is not { } rect)
        {
            timing.FallbackUsed = true;
            timing.FallbackReason = "locator-miss";
            return null;
        }

        var cropSw = System.Diagnostics.Stopwatch.StartNew();
        var crop = TooltipCropper.CropRect(imageBytes, rect);
        cropSw.Stop();
        timing.CropMs += cropSw.Elapsed.TotalMilliseconds;
        if (crop is null)
        {
            timing.FallbackUsed = true;
            timing.FallbackReason = "crop-failed";
            return null;
        }

        // The crop is already normalized to the production tooltip width. A 2x cap is sufficient for
        // the first result and avoids the 3.5–4x bitmap that made the live path feel delayed.
        var primary = await RecognizeWindowsTimedAsync(crop, ct, timing, maxScale: 2.0).ConfigureAwait(false);
        var reading = ParseTimed(primary.Lines, timing);

        string? labelSlot = null, labelRarity = null;
        if (reading.GradeBox is { } anchor
            && (reading.Slot is null || reading.RarityLabel is null)
            && TooltipCropper.TryCropLabels(crop, anchor, out var labels, normalized: true))
        {
            var labelPass = await RecognizeWindowsTimedAsync(
                labels, ct, timing, maxScale: 2.0, labelPass: true).ConfigureAwait(false);
            var lines = labelPass.Lines.Select(l => l.Text).ToList();
            labelSlot = TooltipParser.ResolveSlotLabels(lines);
            labelRarity = TooltipParser.ResolveRarity(lines);
        }

        string? slot = labelSlot ?? reading.Slot;
        string? label = labelRarity ?? reading.RarityLabel;
        string? color = _color.DetectRarity(crop, reading.RarityBox, reading.NameBox);
        var merged = reading with { Slot = slot, RarityLabel = label };
        string? rarity = ReconcileRarity(color, label);

        bool hasEvidence = merged.GradeTier is not null
                        || merged.MainStatValues.Count > 0
                        || merged.BareMainStat is not null
                        || slot is not null
                        || rarity is not null;
        if (!hasEvidence)
        {
            timing.FallbackUsed = true;
            timing.FallbackReason = "immediate-empty";
            return null;
        }

        timing.FastPathUsed = true;
        return new TooltipRecognition(merged, rarity);
    }

    /// <summary>
    /// Locator-anchored recognition: crop the pixel-located tooltip, then run the WinRT crop OCR and
    /// the detection-free ONNX line recognition concurrently (the two slowest steps overlap; neither
    /// depends on the other). Returns null — meaning "use the slow path" — when no tooltip is on
    /// screen, or when the crop resolves neither a grade anchor, the slot, nor the rarity: those are
    /// exactly the cases where the slow path's full-strip passes (comparison-partner fallback, strip
    /// color sampling) can still add information.
    /// </summary>
    private async Task<TooltipRecognition?> TryFastPathAsync(byte[] imageBytes, (int X, int Y)? cursor,
        OnnxOcrService lineOcr, TimingBuilder timing, CancellationToken ct)
    {
        var locateSw = System.Diagnostics.Stopwatch.StartNew();
        var located = await Task.Run(() => TooltipLocator.Locate(imageBytes, cursor), ct).ConfigureAwait(false);
        locateSw.Stop();
        timing.LocatorMs += locateSw.Elapsed.TotalMilliseconds;
        timing.LocatorFound = located is not null;
        if (located is not { } rect)
        {
            timing.FallbackUsed = true;
            timing.FallbackReason = "locator-miss";
            return null;
        }

        var cropSw = System.Diagnostics.Stopwatch.StartNew();
        var crop = TooltipCropper.CropRect(imageBytes, rect);
        cropSw.Stop();
        timing.CropMs += cropSw.Elapsed.TotalMilliseconds;
        if (crop is null)
        {
            timing.FallbackUsed = true;
            timing.FallbackReason = "crop-failed";
            return null;
        }

        // Fields of interest all sit above grade-row-max(110) + value-region(342) in the crop; the
        // help/actions text below is dead CRNN weight.
        const int RecMaxTop = 452;
        var onnxSw = System.Diagnostics.Stopwatch.StartNew();
        var valueTask = lineOcr.RecognizeLinesAsync(crop, RecMaxTop, ct);   // det-free ONNX, overlaps the WinRT OCR
        var windowsTask = RecognizeWindowsTimedAsync(crop, ct, timing);
        // Observe both native operations even if one fails or is cancelled.
        await Task.WhenAll(valueTask, windowsTask).ConfigureAwait(false);
        var second = await windowsTask.ConfigureAwait(false);
        var reading = ParseTimed(second.Lines, timing);

        TooltipReading? valuePass = null;
        var vtips = ParseAllTimed((await valueTask.ConfigureAwait(false)).Lines, timing);
        onnxSw.Stop();
        timing.OnnxOcrMs += onnxSw.Elapsed.TotalMilliseconds;
        if (vtips.Count > 0) valuePass = vtips[0];

        var anchor = reading.GradeBox ?? valuePass?.GradeBox;
        if (anchor is null)
        {
            timing.FallbackUsed = true;
            timing.FallbackReason = "crop-anchor-miss";
            return null;   // crop unreadable → not a (usable) tooltip
        }

        // Label-column pass, same as slow-path pass 3. The input is already normalized by CropRect, so
        // do not apply a second width/line-height scale.
        string? labelSlot = null, labelRarity = null;
        if (TooltipCropper.TryCropLabels(crop, anchor, out var labels, normalized: true))
        {
            var third = await RecognizeWindowsTimedAsync(labels, ct, timing, 2.0, labelPass: true).ConfigureAwait(false);
            var lines3 = third.Lines.Select(l => l.Text).ToList();
            labelSlot = TooltipParser.ResolveSlotLabels(lines3);
            labelRarity = TooltipParser.ResolveRarity(lines3);
        }

        // Prefer an exact slot token from the segmented ONNX lines. Windows OCR can fuzzy-match a
        // background "세트" fragment as "벨트"; the detection-free value pass retains each line's
        // geometry and is the safer source when it has an exact token.
        // The recognition-only pass is intentionally not used as an unqualified slot fallback: its
        // fuzzy parser can turn a garbled label such as "판짜" into the wrong accessory. A missing
        // geometry/evidence-backed slot falls through to the full-strip path, which has the original
        // label context and can retry the OCR.
        var slotSw = System.Diagnostics.Stopwatch.StartNew();
        string? slot = labelSlot ?? ExactSlot(valuePass) ?? ExactSlot(reading) ?? reading.Slot;
        if (labelSlot is not null && (slot is null || string.Equals(labelSlot, slot, StringComparison.Ordinal)))
            slot = labelSlot;
        slotSw.Stop();
        timing.SlotMs += slotSw.Elapsed.TotalMilliseconds;
        string? label = labelRarity ?? reading.RarityLabel;
        // reading's boxes are in normalized-crop coordinates; map them back to the original capture
        // before color sampling. Sampling a resampled crop can average the small teal/gold glyph into
        // a neighbouring hue, and the native crop has a different local origin.
        var raritySw = System.Diagnostics.Stopwatch.StartNew();
        int nativePadX = Math.Max(4, (int)Math.Round(rect.Width * 0.025));
        int nativePadY = Math.Max(4, (int)Math.Round(rect.Width * 0.018));
        double nativeScale = rect.Width / 320.0;
        OcrLine? MapBox(OcrLine? box) => box is null ? null : box with
        {
            Left = rect.Left - nativePadX + box.Left * nativeScale,
            Top = rect.Top - nativePadY + box.Top * nativeScale,
            Width = box.Width * nativeScale,
            Height = box.Height * nativeScale,
        };
        string? color = _color.DetectRarity(imageBytes, MapBox(reading.RarityBox), MapBox(reading.NameBox));
        raritySw.Stop();
        timing.RarityMs += raritySw.Elapsed.TotalMilliseconds;
        var qualitySw = System.Diagnostics.Stopwatch.StartNew();
        string? gradeTier = reading.GradeTier ?? valuePass?.GradeTier;
        qualitySw.Stop();
        timing.QualityMs += qualitySw.Elapsed.TotalMilliseconds;

        var merged = reading with { Slot = slot, GradeTier = gradeTier };
        string? valueRarity = null;
        if (valuePass is { } v)
        {
            var valueStatsSw = System.Diagnostics.Stopwatch.StartNew();
            valueRarity = v.RarityLabel;
            var mergedStats = new Dictionary<string, int>(merged.MainStatValues);
            MergeValueStats(mergedStats, v.MainStatValues);
            valueStatsSw.Stop();
            timing.StatsNameMs += valueStatsSw.Elapsed.TotalMilliseconds;
            timing.StatsValueMs += valueStatsSw.Elapsed.TotalMilliseconds;
            var valueQualitySw = System.Diagnostics.Stopwatch.StartNew();
            merged = merged with
            {
                MainStatValues = mergedStats,
                BareMainStat = v.BareMainStat ?? merged.BareMainStat,
                GradePercent = (merged.GradePercent, v.GradePercent) switch
                {
                    (int a, int b) => Math.Max(a, b),
                    (int a, null) => a,
                    (null, int b) => b,
                    _ => null,
                },
            };
            valueQualitySw.Stop();
            timing.QualityMs += valueQualitySw.Elapsed.TotalMilliseconds;
        }
        var reconcileSw = System.Diagnostics.Stopwatch.StartNew();
        var rarity = ReconcileRarity(color, label ?? valueRarity);
        reconcileSw.Stop();
        timing.RarityMs += reconcileSw.Elapsed.TotalMilliseconds;

        // Anything still missing — including the main-stat evidence a slot/grade-only crop can drop —
        // means the slow path's extra passes may add information, so do not lock in a partial reading.
        if (rarity is null || slot is null || merged.GradeTier is null
            || (merged.MainStatValues.Count == 0 && merged.BareMainStat is null))
        {
            timing.FallbackUsed = true;
            timing.FallbackReason = rarity is null ? "rarity-miss"
                : slot is null ? "slot-miss"
                : merged.GradeTier is null ? "grade-miss"
                : "stat-miss";
            return null;
        }
        timing.FastPathUsed = true;
        return new TooltipRecognition(merged, rarity);
    }

    /// <summary>
    /// The OCR'd rarity word is the game's literal label; with jamo-aware fuzzy matching it resolves
    /// reliably (even "메픽"→에픽) and is trusted over the name/label <em>color</em>, which a set-name
    /// line or upgrade prefix can contaminate. Color is the fallback only when no label was read.
    /// </summary>
    public static string? ReconcileRarity(string? color, string? label) => label ?? color;
    private static string? ExactSlot(TooltipReading? reading)
    {
        if (reading is null) return null;

        // RawLines has no coordinates, so never accept an arbitrary substring: item names and the
        // "세트" explanation can contain characters similar to a slot. Accept only a standalone label
        // or a slot at the end of a name after a separator, matching TooltipParser's geometry-aware
        // below-label/name-suffix rules.
        foreach (var line in reading.RawLines)
        {
            var text = (line ?? string.Empty).Trim().TrimEnd(',', '.', ':', ';', '|', ')', ']', '}', '>');
            foreach (var slot in EquipmentSlots.All)
            {
                if (string.Equals(text, slot, StringComparison.Ordinal)
                    || (text.Length > slot.Length
                        && text.EndsWith(slot, StringComparison.Ordinal)
                        && char.IsWhiteSpace(text[text.Length - slot.Length - 1])))
                    return slot;
            }
        }
        return null;
    }
}
