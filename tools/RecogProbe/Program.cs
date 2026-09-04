// Self-test harness for the tab3 recognition pipeline against tests/pic.
// Filename = ground truth: "<rarity>_..._<slot>" (segment before '+' is the inspected item).
// Usage: ... [filterSubstring] [--dump] | --validate-live [dir] [--profiles file] [--min-trials N]
//        [--require-dpi] [--window-title text] [--report file] [--failure-report file] [--failure-crops dir]
using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;
using DnfItemChecker.Vision;
using System.Text;
using System.Text.Json;

Console.OutputEncoding = System.Text.Encoding.UTF8;
string root = FindProjectRoot();
if (args.Contains("--validate-live", StringComparer.Ordinal))
{
    string inputPath = ResolvePath(root, Option(args, "--validate-live") ?? "debug_captures");
    string? profilesArg = Option(args, "--profiles");
    var profiles = profilesArg is null
        ? LiveCaptureValidator.DefaultProfiles
        : LiveCaptureValidator.LoadProfiles(ResolvePath(root, profilesArg));
    int minimumTrials = IntOption(args, "--min-trials", 30);
    bool requireDpi = args.Contains("--require-dpi", StringComparer.Ordinal);
    string? requiredWindowTitle = Option(args, "--window-title");
    var report = LiveCaptureValidator.Validate(inputPath, profiles, minimumTrials,
        requireDpi, requiredWindowTitle);
    string reportJson = JsonSerializer.Serialize(report, LiveCaptureValidator.JsonOptions);
    Console.WriteLine(reportJson);
    string? reportPath = Option(args, "--report");
    if (reportPath is not null)
        File.WriteAllText(ResolvePath(root, reportPath), reportJson);
    Environment.ExitCode = report.Status switch
    {
        "PASS" => 0,
        "INCONCLUSIVE" => 2,
        _ => 1,
    };
    return;
}
string dir = Path.Combine(root, "tests", "pic");

int ci = Array.IndexOf(args, "--catalog");
string? catalogPath = ci >= 0 && ci + 1 < args.Length ? args[ci + 1] : null;
if (catalogPath is not null && !Path.IsPathRooted(catalogPath))
    catalogPath = Path.Combine(root, catalogPath);
int di = Array.IndexOf(args, "--dir");
if (di >= 0 && di + 1 < args.Length)
    dir = Path.IsPathRooted(args[di + 1]) ? args[di + 1] : Path.Combine(root, args[di + 1]);
int li = Array.IndexOf(args, "--labels");
string? labelsPath = li >= 0 && li + 1 < args.Length ? args[li + 1] : null;
if (labelsPath is not null && !Path.IsPathRooted(labelsPath))
    labelsPath = Path.Combine(root, labelsPath);
int lmi = Array.IndexOf(args, "--limit"); int limSkip = lmi >= 0 ? lmi + 1 : -1;
int ri = Array.IndexOf(args, "--report"); int reportSkip = ri >= 0 ? ri + 1 : -1;
int fci = Array.IndexOf(args, "--failure-crops"); int failureCropSkip = fci >= 0 ? fci + 1 : -1;
int fri = Array.IndexOf(args, "--failure-report"); int failureReportSkip = fri >= 0 ? fri + 1 : -1;
string? failureCropsPath = Option(args, "--failure-crops");
if (failureCropsPath is not null) failureCropsPath = ResolvePath(root, failureCropsPath);
string? failureReportPath = Option(args, "--failure-report");
if (failureReportPath is not null) failureReportPath = ResolvePath(root, failureReportPath);

int lci = Array.IndexOf(args, "--locator-crops");
string? locatorCropsPath = lci >= 0 && lci + 1 < args.Length ? args[lci + 1] : null;
if (locatorCropsPath is not null)
    locatorCropsPath = ResolvePath(root, locatorCropsPath);
int aci = Array.IndexOf(args, "--anchor-crops");
string? anchorCropsPath = aci >= 0 && aci + 1 < args.Length ? args[aci + 1] : null;
if (anchorCropsPath is not null)
    anchorCropsPath = ResolvePath(root, anchorCropsPath);
int catSkip = ci >= 0 ? ci + 1 : -1, dirSkip = di >= 0 ? di + 1 : -1, labSkip = li >= 0 ? li + 1 : -1;
int locatorCropSkip = lci >= 0 ? lci + 1 : -1, anchorCropSkip = aci >= 0 ? aci + 1 : -1;
string? filter = args.Where((a, i) => !a.StartsWith("--") && i != catSkip && i != dirSkip && i != labSkip
    && i != limSkip && i != reportSkip && i != failureCropSkip && i != failureReportSkip
    && i != locatorCropSkip && i != anchorCropSkip).FirstOrDefault();
bool dump = args.Contains("--dump");
bool immediate = args.Contains("--immediate");
bool includeItemName = args.Contains("--include-item-name");

var parser = new TooltipParser();
int limIdx = Array.IndexOf(args, "--limit");
int limit = limIdx >= 0 && limIdx + 1 < args.Length && int.TryParse(args[limIdx + 1], out var lv) ? lv : 560;
var onnx = new OnnxOcrService(Path.Combine(root, "src", "DnfItemChecker.Vision", "models"), limit);
// Default = production hybrid: WinRT reads slot/grade/rarity + locates the tooltip, ONNX reads the
// stat values + quality% on the tooltip crop. --winrt = WinRT 3-pass only. --onnx-only = ONNX only.
bool winrtOnly = args.Contains("--winrt"), onnxOnly = args.Contains("--onnx-only");
IOcrService ocr = onnxOnly ? onnx : new WindowsOcrService();
IOcrService? valueOcr = (winrtOnly || onnxOnly) ? null : onnx;
var reader = new RarityColorReader();
var recognizer = new TooltipRecognizer(ocr, parser, reader, valueOcr);
Console.WriteLine($"OCR mode={(onnxOnly ? "ONNX-only" : winrtOnly ? "WinRT" : "hybrid")}" +
    $"{(immediate ? " / immediate" : string.Empty)}\n");

// --profile: per-pass timing + a pass1 maxScale sweep (grade-line detection rate vs OCR cost at each
// upscale). Informs the pass1 fast path: less upscale = quadratically less OCR area.
if (args.Contains("--profile"))
{
    var win = (WindowsOcrService)ocr;
    double[] scales = [1.0, 1.3, 1.6, 2.0];
    var t1 = new double[scales.Length];
    var grade1 = new int[scales.Length];
    double t2 = 0, t3 = 0, tv = 0;
    var fullMs = new List<double>();
    var fullTimings = new List<TooltipRecognitionTiming>();
    int n = 0;
    foreach (var path in Directory.GetFiles(dir, "*.png").OrderBy(p => p))
    {
        if (filter is not null && !Path.GetFileName(path).Contains(filter)) continue;
        var png = File.ReadAllBytes(path);
        if (n == 0)
        {
            await win.RecognizeAsync(png);
            await onnx.RecognizeAsync(png);
            await recognizer.RecognizeAsync(png); // warm the production hybrid path
        }
        var fullSw = System.Diagnostics.Stopwatch.StartNew();
        var full = await recognizer.RecognizeAsync(png);
        fullSw.Stop();
        fullMs.Add(fullSw.Elapsed.TotalMilliseconds);
        if (full.Timing is { } fullTiming) fullTimings.Add(fullTiming);
        n++;
        var sw = new System.Diagnostics.Stopwatch();
        TooltipReading? best = null;
        for (int si = 0; si < scales.Length; si++)
        {
            sw.Restart();
            var res = await win.RecognizeAsync(png, default, maxScale: scales[si]);
            t1[si] += sw.Elapsed.TotalMilliseconds;
            var p = parser.Parse(res.Lines);
            if (p.GradeBox is not null) { grade1[si]++; best = p; }
        }
        if (best?.GradeBox is { } anchor)
        {
            var (crop, labels, value) = TooltipCropper.CropAll(png, anchor);
            sw.Restart();
            if (crop is not null) await win.RecognizeAsync(crop);
            t2 += sw.Elapsed.TotalMilliseconds; sw.Restart();
            if (labels is not null) await win.RecognizeAsync(labels, default, 2.0);
            t3 += sw.Elapsed.TotalMilliseconds; sw.Restart();
            if (value is not null) await onnx.RecognizeAsync(value);
            tv += sw.Elapsed.TotalMilliseconds;
        }
    }

    // Codec micro-benchmark: the strip is PNG-encoded by the capture service and PNG-decoded by
    // every consumer. BMP trades size for near-zero codec cost — measure the actual delta.
    {
        var png0 = File.ReadAllBytes(Directory.GetFiles(dir, "*.png").OrderBy(p => p).First());
        using var ms0 = new MemoryStream(png0);
        using var bmp0 = new System.Drawing.Bitmap(ms0);
        var sw2 = new System.Diagnostics.Stopwatch();
        byte[] pngBytes = [], bmpBytes = [];
        sw2.Restart();
        for (int i = 0; i < 5; i++) { using var m = new MemoryStream(); bmp0.Save(m, System.Drawing.Imaging.ImageFormat.Png); pngBytes = m.ToArray(); }
        double encPng = sw2.Elapsed.TotalMilliseconds / 5;
        sw2.Restart();
        for (int i = 0; i < 5; i++) { using var m = new MemoryStream(); bmp0.Save(m, System.Drawing.Imaging.ImageFormat.Bmp); bmpBytes = m.ToArray(); }
        double encBmp = sw2.Elapsed.TotalMilliseconds / 5;
        sw2.Restart();
        for (int i = 0; i < 5; i++) { using var m = new MemoryStream(pngBytes); using var b = new System.Drawing.Bitmap(m); }
        double decPng = sw2.Elapsed.TotalMilliseconds / 5;
        sw2.Restart();
        for (int i = 0; i < 5; i++) { using var m = new MemoryStream(bmpBytes); using var b = new System.Drawing.Bitmap(m); }
        double decBmp = sw2.Elapsed.TotalMilliseconds / 5;
        Console.WriteLine($"codec strip {bmp0.Width}x{bmp0.Height}: PNG enc {encPng:0}ms dec {decPng:0}ms ({pngBytes.Length / 1024}KB) | BMP enc {encBmp:0}ms dec {decBmp:0}ms ({bmpBytes.Length / 1024}KB)");
    }
    if (fullTimings.Count > 0)
    {
        double Avg(Func<TooltipRecognitionTiming, double> select) => fullTimings.Average(select);
        int fast = fullTimings.Count(x => x.FastPathUsed);
        int fallback = fullTimings.Count(x => x.FallbackUsed);
        int located = fullTimings.Count(x => x.LocatorFound);
        Console.WriteLine($"path: fast {fast}/{n}, fallback {fallback}/{n}, locator {located}/{n}");
        Console.WriteLine($"stages avg: locator {Avg(x => x.LocatorMs):0}ms crop {Avg(x => x.CropMs):0}ms " +
            $"WinRT {Avg(x => x.WindowsOcrMs):0}ms ONNX {Avg(x => x.OnnxOcrMs):0}ms labels {Avg(x => x.LabelOcrMs):0}ms");
    }
    Console.WriteLine($"n={n}");
    Console.WriteLine($"recognition warm: p50 {Percentile(fullMs, 0.50):0}ms  p95 {Percentile(fullMs, 0.95):0}ms");
    for (int si = 0; si < scales.Length; si++)
        Console.WriteLine($"pass1 x{scales[si]:0.0}    : avg {t1[si] / n,6:0}ms  grade {grade1[si]}/{n}");
    Console.WriteLine($"pass2 crop    : avg {t2 / n,6:0}ms");
    Console.WriteLine($"pass3 labels  : avg {t3 / n,6:0}ms");
    Console.WriteLine($"onnx value    : avg {tv / n,6:0}ms");
    return;
}
// --anchor-crops: export crops normalized around the OCR grade anchor. This covers images where the
// pixel border locator is unavailable, while retaining the same canonical geometry.
if (anchorCropsPath is not null)
{
    Directory.CreateDirectory(anchorCropsPath);
    int anchored = 0, exported = 0;
    foreach (var path in Directory.GetFiles(dir, "*.png").OrderBy(p => p))
    {
        var file = Path.GetFileName(path);
        if (filter is not null && !file.Contains(filter)) continue;
        var imageBytes = File.ReadAllBytes(path);
        var reading = parser.Parse((await ocr.RecognizeAsync(imageBytes)).Lines);
        if (reading.GradeBox is not { } grade) continue;
        anchored++;
        if (!TooltipCropper.TryCrop(imageBytes, grade, out var crop)) continue;
        File.WriteAllBytes(Path.Combine(anchorCropsPath, file), crop);
        exported++;
    }
    Console.WriteLine($"anchor crops: anchored {anchored}, exported {exported}, dir {anchorCropsPath}");
    return;
}


// --locator-crops: export deterministic pixel-locator crops for normalized image duplicate analysis.
if (locatorCropsPath is not null)
{
    Directory.CreateDirectory(locatorCropsPath);
    int located = 0, exported = 0;
    foreach (var path in Directory.GetFiles(dir, "*.png").OrderBy(p => p))
    {
        var file = Path.GetFileName(path);
        if (filter is not null && !file.Contains(filter)) continue;
        var imageBytes = File.ReadAllBytes(path);
        var rect = TooltipLocator.Locate(imageBytes);
        if (rect is not { } locatedRect) continue;
        located++;
        var crop = TooltipCropper.CropRect(imageBytes, locatedRect);
        if (crop is null) continue;
        File.WriteAllBytes(Path.Combine(locatorCropsPath, file), crop);
        exported++;
    }
    Console.WriteLine($"locator crops: located {located}, exported {exported}, dir {locatorCropsPath}");
    return;
}

// --segdump: run the locator fast path's pieces on each image and print the pixel segments + the
// det-free rec text next to the det-path text, to debug rec-only misreads.
if (args.Contains("--segdump"))
{
    foreach (var path in Directory.GetFiles(dir, "*.png").OrderBy(p => p))
    {
        var file = Path.GetFileName(path);
        if (filter is not null && !file.Contains(filter)) continue;
        var png = File.ReadAllBytes(path);
        (int X, int Y)? cur = null;
        var cm2 = System.Text.RegularExpressions.Regex.Match(file, @"__c-(\d+)x(\d+)");
        if (cm2.Success) cur = (int.Parse(cm2.Groups[1].Value), int.Parse(cm2.Groups[2].Value));
        var swl = System.Diagnostics.Stopwatch.StartNew();
        var rect = TooltipLocator.Locate(png, cur);
        swl.Stop();
        Console.WriteLine($"{file}\n  rect={(rect is { } rr ? $"x{rr.Left}-{rr.Right} y{rr.Top}-{rr.Bottom}" : "null")} locate={swl.ElapsedMilliseconds}ms");
        foreach (var cd in TooltipLocator.LocateAll(png))
            Console.WriteLine($"  cand x{cd.Left}-{cd.Right} y{cd.Top}-{cd.Bottom} (w{cd.Width} h{cd.Height})");
        if (rect is not { } r2) continue;
        var swc = System.Diagnostics.Stopwatch.StartNew();
        var crop = TooltipCropper.CropRect(png, r2);
        swc.Stop();
        if (crop is null) continue;
        var swr = System.Diagnostics.Stopwatch.StartNew();
        var recres = await onnx.RecognizeLinesAsync(crop);
        swr.Stop();
        var sww = System.Diagnostics.Stopwatch.StartNew();
        await ocr.RecognizeAsync(crop);
        sww.Stop();
        Console.WriteLine($"  crop={swc.ElapsedMilliseconds}ms recLines={swr.ElapsedMilliseconds}ms({recres.Lines.Count}ln) winrtCrop={sww.ElapsedMilliseconds}ms");
        {
            // mirror the fast path's merge inputs to show which required field is missing
            var wres = await ocr.RecognizeAsync(crop);
            var rd = parser.Parse(wres.Lines);
            var vt = parser.ParseAll(recres.Lines);
            var vp = vt.Count > 0 ? vt[0] : null;
            string? lS = null, lR = null;
            var anch = rd.GradeBox ?? vp?.GradeBox;
            if (anch is not null && TooltipCropper.TryCropLabels(crop, anch, out var lab))
            {
                var third = await ocr.RecognizeAsync(lab, default, 2.0);
                var l3 = third.Lines.Select(l => l.Text).ToList();
                lS = TooltipParser.ResolveSlotLabels(l3);
                lR = TooltipParser.ResolveRarity(l3);
            }
            var col = reader.DetectRarity(crop, rd.RarityBox, rd.NameBox);
            Console.WriteLine($"  fast fields: anchor={(anch is null ? "NULL" : "ok")} slot={lS ?? rd.Slot ?? vp?.Slot ?? "NULL"} " +
                $"grade={rd.GradeTier ?? vp?.GradeTier ?? "NULL"} rarity={(lR ?? rd.RarityLabel) ?? "(label null)"}/color={col ?? "null"}/onnx={vp?.RarityLabel ?? "null"}");
            if (anch is not null && TooltipCropper.TryCropLabels(crop, anch, out var lab2))
            {
                var third2 = await ocr.RecognizeAsync(lab2, default, 2.0);
                Console.WriteLine($"  labels-pass lines (anchor left={anch.Left:0} top={anch.Top:0}): " +
                    string.Join(" | ", third2.Lines.Select(l => l.Text)));
            }
        }
        Console.WriteLine("  -- rec-only segments --");
        foreach (var l in recres.Lines)
            Console.WriteLine($"    ({l.Left,4:0},{l.Top,4:0} {l.Width,3:0}x{l.Height,2:0}) {l.Text}");
        var detres = await onnx.RecognizeAsync(crop);
        Console.WriteLine("  -- det path --");
        foreach (var l in detres.Lines)
            Console.WriteLine($"    ({l.Left,4:0},{l.Top,4:0} {l.Width,3:0}x{l.Height,2:0}) {l.Text}");
        Console.WriteLine();
    }
    return;
}

static (string rarity, string slot) Expected(string file)
{
    var name = Path.GetFileNameWithoutExtension(file);
    // Live debug capture: cap_HHmmss_fff__r-<rarity>__s-<slot>__m-…__o-… ("none" = the app failed
    // to resolve it live — treated as unknown truth, matched leniently below).
    if (name.StartsWith("cap_"))
    {
        string r = "?", s = "?";
        foreach (var part in name.Split("__"))
        {
            if (part.StartsWith("r-")) r = part[2..];
            if (part.StartsWith("s-")) s = part[2..];
        }
        return (r, s);
    }
    var first = name.Split('+')[0];
    var tokens = first.Split('_', StringSplitOptions.RemoveEmptyEntries);
    return (tokens[0], tokens[^1]);
}

static List<(string rarity, string slot)> TruthItems(string file)
{
    var nm = Path.GetFileNameWithoutExtension(file);
    var list = new List<(string, string)>();
    foreach (var seg in nm.Split('+', StringSplitOptions.RemoveEmptyEntries))
    {
        var tk = seg.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (tk.Length > 0) list.Add((tk[0], tk[^1]));
    }
    return list;
}

static string Clean(string? s) =>
    string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ').Trim();

if (catalogPath is not null)
{
    var sb = new StringBuilder();
    sb.Append("파일\t위치\t레어리티(정답)\t부위(정답)\t레어리티(OCR)\t부위(OCR)\t등급(OCR)\t강화\t이름(OCR)\t힘\t지능\t체력\t정신력\n");
    foreach (var path in Directory.GetFiles(dir, "*.png").OrderBy(p => p))
    {
        var file = Path.GetFileName(path);
        var truth = TruthItems(file);
        var png = File.ReadAllBytes(path);
        var res = await ocr.RecognizeAsync(png);
        var readings = parser.ParseAll(res.Lines);
        var inspected = await recognizer.RecognizeAsync(png); // 2-pass refines the inspected (left/single) item
        int n = Math.Max(truth.Count, readings.Count);
        for (int i = 0; i < n; i++)
        {
            var t = i < truth.Count ? truth[i] : ("", "");
            var r = i == 0 ? inspected.Reading : (i < readings.Count ? readings[i] : null);
            string pos = truth.Count >= 2 ? (i == 0 ? "좌(검사대상)" : "우(장착)") : "단일";
            string ocrR = i == 0 ? (inspected.Rarity ?? "")
                : (r is null ? "" : TooltipRecognizer.ReconcileRarity(reader.DetectRarity(png, r.RarityBox, r.NameBox), r.RarityLabel) ?? "");
            string grade = r is null || (r.GradeTier is null && r.GradePercent is null) ? ""
                : $"{r.GradeTier ?? "?"} {(r.GradePercent is int gp ? gp + "%" : "?%")}";
            string reinforce = r?.Reinforce is int rf ? "+" + rf : "";
            var sv = r?.MainStatValues;
            string Stat(string k) => sv != null && sv.TryGetValue(k, out int v) ? v.ToString() : "";
            sb.Append(file).Append('\t').Append(pos).Append('\t')
              .Append(t.Item1).Append('\t').Append(t.Item2).Append('\t')
              .Append(ocrR).Append('\t').Append(r?.Slot ?? "").Append('\t')
              .Append(grade).Append('\t').Append(reinforce).Append('\t').Append(Clean(r?.ItemName)).Append('\t')
              .Append(Stat("힘")).Append('\t').Append(Stat("지능")).Append('\t')
              .Append(Stat("체력")).Append('\t').Append(Stat("정신력")).Append('\n');
        }
    }
    File.WriteAllText(catalogPath, sb.ToString(), new UTF8Encoding(false));
    Console.WriteLine($"카탈로그 작성: {catalogPath}");
    return;
}
// slot_kor, and grade; quality_pct, item_name, main_stat, and stats are optional per sample.
if (labelsPath is not null)
{
    string evalDir = Path.GetDirectoryName(Path.GetFullPath(labelsPath))!;
    int n = 0, rOk = 0, sOk = 0, gOk = 0, qOk = 0, qItems = 0;
    int nameItems = 0, nameOk = 0, statTypeItems = 0, statTypeOk = 0;
    int statsNameItems = 0, statsNameOk = 0, statValueItems = 0, statValueOk = 0;
    int coreExactOk = 0, allOk = 0;
    var evalMs = new List<double>();
    var stageSamples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
    var statsValueFailures = new List<Dictionary<string, object?>>();
    if (failureCropsPath is not null) Directory.CreateDirectory(failureCropsPath);
    string? reportPath = Option(args, "--report");
    var evalLines = File.ReadAllLines(labelsPath).Where(l => l.Trim().Length > 0).ToList();
    foreach (var line in evalLines)
    {
        using var doc = JsonDocument.Parse(line);
        var e = doc.RootElement;
        string img = e.GetProperty("image").GetString()!;
        if (filter is not null && !img.Contains(filter)) continue;
        string path = Path.Combine(evalDir, img.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) { Console.WriteLine($"  missing: {img}"); continue; }
        n++;
        (int X, int Y)? lcur = null;
        var lcm = System.Text.RegularExpressions.Regex.Match(img, @"__c-(\d+)x(\d+)");
        if (lcm.Success) lcur = (int.Parse(lcm.Groups[1].Value), int.Parse(lcm.Groups[2].Value));
        var captureSw = System.Diagnostics.Stopwatch.StartNew();
        var imageBytes = File.ReadAllBytes(path);
        captureSw.Stop();
        AddStage(stageSamples, "capture", captureSw.Elapsed.TotalMilliseconds);
        if (n == 1)
            await recognizer.RecognizeAsync(
                imageBytes, lcur,
                mode: immediate ? TooltipRecognitionMode.Immediate : TooltipRecognitionMode.Balanced,
                includeItemName: includeItemName);
        var evalSw = System.Diagnostics.Stopwatch.StartNew();
        var rec = await recognizer.RecognizeAsync(
            imageBytes, lcur,
            mode: immediate ? TooltipRecognitionMode.Immediate : TooltipRecognitionMode.Balanced,
            includeItemName: includeItemName);
        evalSw.Stop();
        evalMs.Add(evalSw.Elapsed.TotalMilliseconds);
        AddStage(stageSamples, "total", evalSw.Elapsed.TotalMilliseconds);
        if (rec.Timing is { } timing)
        {
            AddStage(stageSamples, "tooltipLocator", timing.LocatorMs);
            AddStage(stageSamples, "crop", timing.CropMs);
            AddStage(stageSamples, "rarity", timing.RarityMs);
            AddStage(stageSamples, "qualityPercent", timing.QualityMs);
            AddStage(stageSamples, "slot", timing.SlotMs);
            AddStage(stageSamples, "statsName", timing.StatsNameMs);
            AddStage(stageSamples, "statsValue", timing.StatsValueMs);
            AddStage(stageSamples, "windowsOcr", timing.WindowsOcrMs);
            AddStage(stageSamples, "onnxInference", timing.OnnxOcrMs);
            AddStage(stageSamples, "itemNameOcr", timing.ItemNameOcrMs);
            AddStage(stageSamples, "postprocessing", timing.PostprocessingMs);
        }
        var reading = rec.Reading;

        string? lr = e.GetProperty("rarity").GetString();
        string? ls = e.GetProperty("slot_kor").GetString();
        string? lg = e.TryGetProperty("grade", out var gradeElement) ? gradeElement.GetString() : null;
        int? lq = e.TryGetProperty("quality_pct", out var qualityElement)
            && qualityElement.ValueKind == JsonValueKind.Number ? qualityElement.GetInt32() : null;
        string? expectedName = e.TryGetProperty("item_name", out var nameElement)
            ? nameElement.GetString() : null;
        string? expectedStatType = e.TryGetProperty("main_stat", out var statElement)
            ? statElement.GetString() : null;
        var expectedStats = new Dictionary<string, int>(StringComparer.Ordinal);
        if (e.TryGetProperty("stats", out var expectedStatsElement))
            foreach (var stat in expectedStatsElement.EnumerateObject())
                expectedStats[stat.Name] = stat.Value.GetInt32();

        string? gotR = rec.Rarity, gotS = reading.Slot, gotG = reading.GradeTier;
        int? gotQ = reading.GradePercent;
        bool r = gotR == lr, s = gotS == ls, g = lg is null || gotG == lg;
        bool q = true;
        if (lq is int expectedQuality)
        {
            qItems++;
            q = gotQ == expectedQuality;
            if (q) qOk++;
        }
        if (r) rOk++; if (s) sOk++; if (g) gOk++;

        bool name = true;
        if (includeItemName && expectedName is not null)
        {
            nameItems++;
            name = string.Equals(Clean(reading.ItemName), Clean(expectedName), StringComparison.Ordinal);
            if (name) nameOk++;
        }

        bool statType = true;
        if (expectedStatType is not null)
        {
            statTypeItems++;
            statType = reading.MainStatValues.ContainsKey(expectedStatType);
            if (statType) statTypeOk++;
        }

        bool statsName = true;
        if (expectedStats.Count > 0)
        {
            statsNameItems++;
            var expectedNames = expectedStats.Keys.ToHashSet(StringComparer.Ordinal);
            var gotNames = reading.MainStatValues.Keys.ToHashSet(StringComparer.Ordinal);
            statsName = expectedNames.SetEquals(gotNames);
            if (statsName) statsNameOk++;
        }

        bool value = true;
        var statBits = new List<string>();
        foreach (var pair in expectedStats)
        {
            statValueItems++;
            int? predicted = reading.MainStatValues.TryGetValue(pair.Key, out int parsed) && parsed != 0
                ? parsed
                : reading.BareMainStat;
            if (predicted != pair.Value)
            {
                value = false;
                statBits.Add($"{pair.Key} {pair.Value}->{(predicted?.ToString() ?? "null")}");
                var failure = new Dictionary<string, object?>
                {
                    ["image"] = img,
                    ["groundTruth"] = pair.Value,
                    ["prediction"] = predicted,
                    ["uiScale"] = ParseUiScale(img),
                    ["statName"] = pair.Key,
                    ["reason"] = predicted is null
                        ? "value_missing_after_ocr_or_parsing"
                        : "numeric_recognition_mismatch",
                    ["roiCrop"] = null,
                };
                byte[]? roiBytes = null;
                var rawFailure = await ocr.RecognizeAsync(imageBytes);
                var parsedFailure = parser.Parse(rawFailure.Lines);
                if (parsedFailure.MainStatValues.TryGetValue(pair.Key, out int windowsValue))
                    failure["windowsPrediction"] = windowsValue;
                if (failureCropsPath is not null)
                {
                    var located = TooltipLocator.Locate(imageBytes, lcur);
                    if (located is { } rect
                        && TooltipCropper.CropRect(imageBytes, rect) is { } normalized)
                    {
                        var canonicalGrade = new OcrLine("", 8, 110, 84, 19);
                        if (TooltipCropper.TryCropValue(normalized, canonicalGrade, out var normalizedRoi))
                            roiBytes = normalizedRoi;
                    }
                    if (roiBytes is null && parsedFailure.GradeBox is { } gradeBox
                        && TooltipCropper.TryCropValue(imageBytes, gradeBox, out var nativeRoi))
                        roiBytes = nativeRoi;
                    if (roiBytes is not null)
                    {
                        string cropName = $"{Path.GetFileNameWithoutExtension(img)}__{pair.Key}.bmp";
                        string cropPath = Path.Combine(failureCropsPath, cropName);
                        File.WriteAllBytes(cropPath, roiBytes);
                        failure["roiCrop"] = Path.GetRelativePath(
                            Path.GetDirectoryName(failureReportPath ?? reportPath ?? failureCropsPath)!,
                            cropPath);
                    }
                }
                statsValueFailures.Add(failure);
            }
            else
                statValueOk++;
        }

        bool coreExact = r && s && q && statsName && value;
        if (coreExact) coreExactOk++;
        bool all = r && s && g && q && value && (!includeItemName || name) && statType && statsName;
        if (all) allOk++;
        else Console.WriteLine($"[{img}] {(r ? "" : $"r:{lr}->{gotR} ")}{(s ? "" : $"s:{ls}->{gotS} ")}{(g ? "" : $"g:{lg}->{gotG} ")}{(q ? "" : $"q:{lq}->{gotQ} ")}{(includeItemName && !name ? $"name:{expectedName}->{reading.ItemName} " : "")}{(statType ? "" : $"mainStat:{expectedStatType} ")}{(statsName ? "" : $"statsName:{string.Join(",", expectedStats.Keys)}->{string.Join(",", reading.MainStatValues.Keys)} ")}{(statBits.Count > 0 ? "stat:" + string.Join(",", statBits) : "")}");
    }
    double mean = evalMs.Count == 0 ? 0 : evalMs.Average();
    double p50 = Percentile(evalMs, 0.50), p95 = Percentile(evalMs, 0.95);
    double p99 = Percentile(evalMs, 0.99), max = evalMs.Count == 0 ? 0 : evalMs.Max();
    var stageLatencyMs = stageSamples.ToDictionary(
        pair => pair.Key, pair => StageSummary(pair.Value), StringComparer.Ordinal);
    Console.WriteLine($"recognition warm: mean {mean:0}ms  p50 {p50:0}ms  p95 {p95:0}ms  p99 {p99:0}ms  max {max:0}ms");
    Console.WriteLine($"\n=== eval n={n}: rarity {rOk}/{n}, slot {sOk}/{n}, quality {gOk}/{n}, qualityPercent {qOk}/{qItems}, itemName {nameOk}/{nameItems}, mainStat {statTypeOk}/{statTypeItems}, statsName {statsNameOk}/{statsNameItems}, statsValue {statValueOk}/{statValueItems}, CORE {coreExactOk}/{n}, ALL {allOk}/{n} ===");

    if (reportPath is not null)
    {
        var report = new
        {
            dataset = Path.GetFullPath(labelsPath),
            samples = n,
            captureStage = "dataset file-read proxy; excludes real screen capture",
            includeItemName,
            itemNameMode = includeItemName
                ? "included in shared OCR output"
                : "deferred secondary identity path; excluded from core scoring",
            latencyMs = new { mean, p50, p95, p99, max },
            stageLatencyMs,
            fields = new
            {
                rarity = new { correct = rOk, total = n },
                slot = new { correct = sOk, total = n },
                grade = new { correct = gOk, labeled = n },
                quality = new { correct = gOk, labeled = n },
                qualityPercent = new { correct = qOk, labeled = qItems },
                itemName = new { correct = nameOk, labeled = nameItems },
                mainStat = new { correct = statTypeOk, labeled = statTypeItems },
                statsName = new { correct = statsNameOk, labeled = statsNameItems },
                statsValue = new { correct = statValueOk, total = statValueItems },
                coreExact = new { correct = coreExactOk, total = n },
                all = new { correct = allOk, total = n },
            },
            statsValueFailures,
        };
        string reportJson = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ResolvePath(root, reportPath), reportJson);
        Console.WriteLine($"label report: {ResolvePath(root, reportPath)}");
    }
    if (failureReportPath is not null)
    {
        var failureReport = new
        {
            dataset = Path.GetFullPath(labelsPath),
            samples = n,
            failureCount = statsValueFailures.Count,
            failures = statsValueFailures,
        };
        File.WriteAllText(failureReportPath,
            JsonSerializer.Serialize(failureReport, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"stats.value failure report: {failureReportPath}");
    }
    return;
}

int rarityOk = 0, slotOk = 0, total = 0, statValOk = 0, statValN = 0;
var perImageMs = new List<double>();
foreach (var path in Directory.GetFiles(dir, "*.png").OrderBy(p => p))
{
    var file = Path.GetFileName(path);
    if (filter is not null && !file.Contains(filter)) continue;
    total++;
    var (expRarity, expSlot) = Expected(file);
    // cap_ files also carry m-<주능력치>__o-<관측값>: score the stat VALUE the live app read.
    (string stat, int val)? expStat = null;
    {
        string? m = null, o = null;
        foreach (var part in Path.GetFileNameWithoutExtension(file).Split("__"))
        {
            if (part.StartsWith("m-")) m = part[2..];
            if (part.StartsWith("o-")) o = part[2..];
        }
        if (m is not null && m != "none" && o is not null && int.TryParse(o, out int ov)) expStat = (m, ov);
    }

    var png = File.ReadAllBytes(path);
    // Live captures carry the cursor position (__c-<x>x<y>) — feed it to the locator fast path the
    // way the app does; files without it fall back to the leftmost-candidate convention.
    (int X, int Y)? cursor = null;
    var cm = System.Text.RegularExpressions.Regex.Match(file, @"__c-(\d+)x(\d+)");
    if (cm.Success) cursor = (int.Parse(cm.Groups[1].Value), int.Parse(cm.Groups[2].Value));
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var recog = await recognizer.RecognizeAsync(
        png, cursor, mode: immediate ? TooltipRecognitionMode.Immediate : TooltipRecognitionMode.Balanced);
    sw.Stop();
    perImageMs.Add(sw.Elapsed.TotalMilliseconds);
    var reading = recog.Reading;
    var rarity = recog.Rarity ?? "(null)";
    var slot = reading.Slot ?? "(null)";

    bool rOk = rarity == expRarity, sOk = slot == expSlot;
    if (rOk) rarityOk++;
    if (sOk) slotOk++;
    string statMark = "";
    if (expStat is { } es)
    {
        statValN++;
        int? got = reading.MainStatValues.TryGetValue(es.stat, out int gv) ? gv : reading.BareMainStat;
        bool vOk = got == es.val;
        if (vOk) statValOk++;
        statMark = $"  stat {es.stat} {es.val}->{(got is int g ? g.ToString() : "null")} [{(vOk ? "OK" : "X")}]";
    }

    Console.WriteLine($"{file}  [{sw.Elapsed.TotalMilliseconds:0}ms]");
    Console.WriteLine($"  expect rarity={expRarity} slot={expSlot}");
    Console.WriteLine($"  got    rarity={rarity} [{(rOk ? "OK" : "X")}] (label={reading.RarityLabel ?? "-"})  slot={slot} [{(sOk ? "OK" : "X")}]{statMark}");
    string grade = reading.GradeTier is null && reading.GradePercent is null
        ? "(등급 미인식)"
        : $"{reading.GradeTier ?? "?"} {(reading.GradePercent is int p ? p + "%" : "?%")}";
    Console.WriteLine($"  이름: {reading.ItemName ?? "(미인식)"}{(reading.Reinforce is int rf ? $"  (+{rf})" : "")}");
    Console.WriteLine($"  등급: {grade}");
    var stats = reading.MainStatValues;
    Console.WriteLine(stats.Count == 0
        ? "  능력치: (미인식)"
        : $"  능력치: {string.Join(", ", stats.Select(kv => $"{kv.Key} {kv.Value}"))}");
    if (dump)
    {
        var raw = await ocr.RecognizeAsync(png);
        Console.WriteLine("   -- pass1 --");
        for (int i = 0; i < raw.Lines.Count; i++)
            Console.WriteLine($"     [{i}] ({raw.Lines[i].Left,4:0},{raw.Lines[i].Top,4:0}) {raw.Lines[i].Text}");
        var p1 = parser.Parse(raw.Lines);
        if (p1.GradeBox is { } gb && TooltipCropper.TryCrop(png, gb, out var cr))
        {
            var r2 = await ocr.RecognizeAsync(cr);
            Console.WriteLine("   -- pass2(crop) --");
            for (int i = 0; i < r2.Lines.Count; i++)
                Console.WriteLine($"     [{i}] ({r2.Lines[i].Left,4:0},{r2.Lines[i].Top,4:0}) {r2.Lines[i].Text}");
        }
        if (p1.GradeBox is { } gb2 && TooltipCropper.TryCropLabels(png, gb2, out var lc))
        {
            var r3 = await ocr.RecognizeAsync(lc, default, 2.0);
            Console.WriteLine("   -- pass3(labels) --");
            for (int i = 0; i < r3.Lines.Count; i++)
                Console.WriteLine($"     [{i}] ({r3.Lines[i].Left,4:0},{r3.Lines[i].Top,4:0}) {r3.Lines[i].Text}");
        }
        if (valueOcr is not null && p1.GradeBox is { } gb3 && TooltipCropper.TryCropValue(png, gb3, out var vc))
        {
            var rv = await valueOcr.RecognizeAsync(vc);
            Console.WriteLine("   -- value(onnx crop) --");
            for (int i = 0; i < rv.Lines.Count; i++)
                Console.WriteLine($"     [{i}] ({rv.Lines[i].Left,4:0},{rv.Lines[i].Top,4:0}) {rv.Lines[i].Text}");
        }
    }
    Console.WriteLine();
}
Console.WriteLine($"=== rarity {rarityOk}/{total}, slot {slotOk}/{total}{(statValN > 0 ? $", statVal {statValOk}/{statValN}" : "")} ===");
if (perImageMs.Count > 1)
{
    var warm = perImageMs.Skip(1).ToList(); // first image pays OCR engine warm-up
    Console.WriteLine($"=== time: avg {warm.Average():0}ms  min {warm.Min():0}ms  max {warm.Max():0}ms  (n={warm.Count}, cold first={perImageMs[0]:0}ms) ===");
}

static void AddStage(Dictionary<string, List<double>> stages, string name, double value)
{
    if (!double.IsFinite(value) || value < 0) return;
    if (!stages.TryGetValue(name, out var values))
        stages[name] = values = new List<double>();
    values.Add(value);
}

static object StageSummary(IReadOnlyList<double> values) => new
{
    samples = values.Count,
    mean = values.Count == 0 ? 0 : values.Average(),
    p50 = Percentile(values, 0.50),
    p95 = Percentile(values, 0.95),
    p99 = Percentile(values, 0.99),
};

static int? ParseUiScale(string image)
{
    var match = System.Text.RegularExpressions.Regex.Match(image, @"(?:^|_)ui(\d+)(?:_|$)");
    return match.Success && int.TryParse(match.Groups[1].Value, out int scale) ? scale : null;
}

static string? Option(string[] values, string name)
{
    int index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length
        && !values[index + 1].StartsWith("--", StringComparison.Ordinal)
            ? values[index + 1]
            : null;
}

static int IntOption(string[] values, string name, int fallback)
    => Option(values, name) is string value && int.TryParse(value, out int parsed)
        ? parsed
        : fallback;

static string ResolvePath(string root, string path)
    => Path.IsPathRooted(path) ? path : Path.Combine(root, path);

static double Percentile(IReadOnlyList<double> values, double p)
{
    if (values.Count == 0) return 0;
    var ordered = values.OrderBy(x => x).ToArray();
    double position = (ordered.Length - 1) * Math.Clamp(p, 0, 1);
    int lower = (int)Math.Floor(position), upper = (int)Math.Ceiling(position);
    return lower == upper ? ordered[lower] : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
}

static string FindProjectRoot()
{
    foreach (var start in new[]
             {
                 Directory.GetCurrentDirectory(),
                 AppContext.BaseDirectory,
             })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DnfItemChecker.slnx"))
                && Directory.Exists(Path.Combine(dir.FullName, "src"))
                && Directory.Exists(Path.Combine(dir.FullName, "tests")))
                return dir.FullName;
            dir = dir.Parent;
        }
    }

    throw new DirectoryNotFoundException(
        "Could not locate the project root. Run the probe from the project tree.");
}
