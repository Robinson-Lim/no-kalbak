using DnfItemChecker.Core.Ocr;
using RapidOcrNet;
using SkiaSharp;
using RapidResult = RapidOcrNet.OcrResult;

namespace DnfItemChecker.Vision;

/// <summary>
/// Korean OCR via PaddleOCR PP-OCRv5 ONNX models (detection + angle cls + recognition) run through
/// RapidOcrNet/OnnxRuntime. Reads the small pixel-font digits and quality% that Windows.Media.Ocr
/// drops, so the actual stat VALUE — not just the grade tier — can be verified. Fully offline/CPU.
/// </summary>
public sealed class OnnxOcrService : IOcrService, IDisposable
{
    private RapidOcr? _ocr;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private volatile bool _disposed;
    private readonly string _modelsDir;
    private readonly int _limitSideLen;
    private readonly Lazy<Task> _initialization;
    private bool _available;

    // Recognition-only engines for the pixel-segmented fast path (see RecognizeLinesAsync). One
    // engine per lane — each instance holds mutable per-call state. Lane count × intra-op threads
    // measured on the 20-line bench: 4×2 = 274ms vs 1×default 522ms (default threads let a single
    // tiny CRNN inference occupy every core, so extra lanes only contended).
    private const int RecLanes = 4;
    private const int RecThreadsPerLane = 2;
    private TextRecognizer[]? _recs;

    /// <param name="modelsDir">Folder holding the four model files (det/cls/rec onnx + dict txt).</param>
    /// <param name="limitSideLen">Detector short-side upscale target. The tooltip crop is small, so this
    /// upscales the pixel-font digits for the recognizer. Higher = more accurate but slower (detection
    /// cost grows with the resized area); 560 is lossless on the 340px value crop (statVal 32/32 on the
    /// live-capture bench, ~45ms faster than 600); 520 starts dropping digits.</param>
    public OnnxOcrService(string modelsDir, int limitSideLen = 560)
    {
        _modelsDir = modelsDir;
        _limitSideLen = limitSideLen;
        _initialization = new Lazy<Task>(
            () => Task.Run(InitializeCore),
            LazyThreadSafetyMode.ExecutionAndPublication);
        // Start model loading asynchronously. The constructor must remain UI-safe because DI creates
        // this singleton while resolving MainWindow, before window.Show().
        _ = _initialization.Value;
    }

    private void InitializeCore()
    {
        RapidOcr? ocr = null;
        TextRecognizer[]? recs = null;
        try
        {
            ocr = new RapidOcr();
            ocr.InitModels(
                detPath: Path.Combine(_modelsDir, "ch_PP-OCRv5_mobile_det.onnx"),
                clsPath: Path.Combine(_modelsDir, "ch_ppocr_mobile_v2.0_cls_infer.onnx"),
                recPath: Path.Combine(_modelsDir, "korean_PP-OCRv5_rec_mobile.onnx"),
                keysPath: Path.Combine(_modelsDir, "ppocrv5_korean_dict.txt"));

            string recPath = Path.Combine(_modelsDir, "korean_PP-OCRv5_rec_mobile.onnx");
            string keysPath = Path.Combine(_modelsDir, "ppocrv5_korean_dict.txt");
            recs = new TextRecognizer[RecLanes];
            for (int i = 0; i < RecLanes; i++)
            {
                recs[i] = new TextRecognizer();
                recs[i].InitModel(recPath, keysPath, RecThreadsPerLane);
            }

            _ocr = ocr;
            _recs = recs;
            _available = true;
            Log(_modelsDir, "OK");
        }
        catch (Exception ex)
        {
            ocr?.Dispose();
            if (recs is not null)
                foreach (var rec in recs) rec?.Dispose();
            _available = false;
            Log(_modelsDir, ex.ToString());
        }
    }

    // Startup diagnostic: record ONNX init success/failure next to the exe (single-file native/model
    // extraction issues surface here). Best-effort; never throws.
    private static void Log(string modelsDir, string msg)
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            var displayPath = Path.IsPathRooted(modelsDir)
                ? Path.GetRelativePath(dir, Path.GetFullPath(modelsDir))
                : modelsDir;
            File.WriteAllText(Path.Combine(dir, "onnx_init.log"), $"modelsDir={displayPath}\n{msg}");
        }
        catch { /* ignore */ }
    }

    public bool IsAvailable => !_disposed && _available;

    private async Task<OcrResult> RunExclusiveAsync(Func<Task<OcrResult>> operation, CancellationToken ct)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ct.ThrowIfCancellationRequested();
            var result = await operation().ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return result;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<bool> EnsureInitializedAsync(CancellationToken ct)
    {
        try
        {
            await _initialization.Value.WaitAsync(ct).ConfigureAwait(false);
            return _available;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public Task<OcrResult> RecognizeAsync(
        byte[] imageBytes, CancellationToken ct = default, double maxScale = 4.0)
        => RunExclusiveAsync(() => RecognizeCoreAsync(imageBytes, ct), ct);

    private async Task<OcrResult> RecognizeCoreAsync(byte[] imageBytes, CancellationToken ct)
    {
        if (imageBytes.Length == 0 || !await EnsureInitializedAsync(ct).ConfigureAwait(false))
            return new OcrResult(Array.Empty<OcrLine>());

        return await Task.Run(() =>
        {
            using var bmp = SKBitmap.Decode(imageBytes);
            if (bmp is null) return new OcrResult(Array.Empty<OcrLine>());

            RapidResult res;
            // Tooltip glyphs are small; upscale the short side (ImgResize=0 → LimitSideLen adaptive)
            // instead of the default longer-side cap, which would downscale tall captures and blur text.
            // DoAngle=false: tooltip text is always upright, so skip the per-line 180° classifier pass.
            var opts = RapidOcrOptions.Default with
            {
                ImgResize = 0, LimitSideLen = _limitSideLen, DoAngle = false,
            };
            // The operation gate keeps inference and disposal mutually exclusive.
            ct.ThrowIfCancellationRequested();
            res = _ocr!.Detect(bmp, opts);

            var lines = new List<OcrLine>();
            foreach (var block in res.TextBlocks)
            {
                if (string.IsNullOrWhiteSpace(block.Text) || block.BoxPoints is not { Length: > 0 } pts) continue;
                float minX = pts[0].X, maxX = pts[0].X, minY = pts[0].Y, maxY = pts[0].Y;
                foreach (var p in pts)
                {
                    if (p.X < minX) minX = p.X; else if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y; else if (p.Y > maxY) maxY = p.Y;
                }
                lines.Add(new OcrLine(block.Text.Trim(), minX, minY, maxX - minX, maxY - minY));
            }
            // Parser expects reading order: top-to-bottom, then left-to-right within a row.
            lines.Sort((a, b) => Math.Abs(a.Top - b.Top) > 6 ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));
            return new OcrResult(lines);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// True once initialization has succeeded or while the one-time initialization is in progress.
    /// The latter lets callers await the same task instead of falling back to the slow full-strip path.
    /// </summary>
    public bool SupportsLineRecognition
    {
        get
        {
            var init = _initialization.Value;
            return !_disposed && (_recs is not null || !init.IsCompleted);
        }
    }

    /// <summary>
    /// Recognition-only OCR: <see cref="TextLineSegmenter"/> finds the text boxes by pixel projection
    /// (~2ms) and only the CRNN recognizer runs per segment, skipping the DbNet detection inference
    /// that dominates <see cref="RecognizeAsync"/>. Segments are recognized on four engine lanes
    /// concurrently. Boxes in the returned lines are in the input image's coordinates.
    /// </summary>
    /// <param name="maxTop">Segments starting below this y are skipped — the caller knows where its
    /// fields of interest end (each skipped segment saves a ~10ms CRNN call).</param>
    public Task<OcrResult> RecognizeLinesAsync(
        byte[] imageBytes, int maxTop = int.MaxValue, CancellationToken ct = default)
        => RunExclusiveAsync(() => RecognizeLinesCoreAsync(imageBytes, maxTop, ct), ct);

    private async Task<OcrResult> RecognizeLinesCoreAsync(byte[] imageBytes, int maxTop, CancellationToken ct)
    {
        if (imageBytes.Length == 0 || !await EnsureInitializedAsync(ct).ConfigureAwait(false))
            return new OcrResult(Array.Empty<OcrLine>());

        var recs = _recs;
        if (recs is null) return new OcrResult(Array.Empty<OcrLine>());

        return await Task.Run(async () =>
        {
            using var bmp = SKBitmap.Decode(imageBytes);
            if (bmp is null) return new OcrResult(Array.Empty<OcrLine>());

            var boxes = TextLineSegmenter.Segment(bmp);
            boxes.RemoveAll(b => b.Top > maxTop);
            if (boxes.Count == 0) return new OcrResult(Array.Empty<OcrLine>());

            // 2x nearest upscale per segment: CRNN resizes lines to height 48 internally, and native
            // ~18px tooltip glyphs survive one 2x + one small lib resize far better than a single
            // ~2.7x jump. Nearest, not cubic: the game's pixel font has hard edges, and smoothing
            // resamplers were the most digit-confusion-prone on tight crops in the A/B ("93"→"98").
            var crops = new SKBitmap[boxes.Count];
            var texts = new string[boxes.Count];
            try
            {
                using var srcImage = SKImage.FromBitmap(bmp);
                for (int i = 0; i < boxes.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var r = boxes[i];
                    var dst = new SKBitmap(new SKImageInfo(r.Width * 2, r.Height * 2, bmp.ColorType, bmp.AlphaType));
                    using (var canvas = new SKCanvas(dst))
                        canvas.DrawImage(srcImage, SKRect.Create(r.Left, r.Top, r.Width, r.Height),
                            SKRect.Create(0, 0, dst.Width, dst.Height),
                            new SKSamplingOptions(SKFilterMode.Nearest));
                    crops[i] = dst;
                }

                void RecLane(int lane)
                {
                    for (int i = lane; i < crops.Length; i += RecLanes)
                    {
                        ct.ThrowIfCancellationRequested();
                        texts[i] = string.Concat(recs[lane].GetTextLine(crops[i]).Chars ?? Array.Empty<string>());
                    }
                }
                var lanes = new Task[RecLanes];
                for (int l = 0; l < RecLanes; l++)
                {
                    int lane = l;
                    lanes[l] = Task.Run(() => RecLane(lane));
                }
                // Cancellation/failure in ANY lane must still join ALL lanes before the
                // finally block frees their shared crops. A cancellable WaitAll caused
                // sk_bitmap_get_info access violations while other lanes were still running.
                await Task.WhenAll(lanes).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            finally
            {
                foreach (var c in crops) c?.Dispose();
            }

            var lines = new List<OcrLine>(boxes.Count);
            for (int i = 0; i < boxes.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(texts[i])) continue;
                var r = boxes[i];
                lines.Add(new OcrLine(texts[i].Trim(), r.Left, r.Top, r.Width, r.Height));
            }
            lines.Sort((a, b) => Math.Abs(a.Top - b.Top) > 6 ? a.Top.CompareTo(b.Top) : a.Left.CompareTo(b.Left));
            return new OcrResult(lines);
        }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        // Do not dispose native sessions underneath an active inference or initialization.
        _operationGate.Wait();
        try
        {
            if (_disposed) return;
            _disposed = true;
            _initialization.Value.GetAwaiter().GetResult();
            _available = false;
            _ocr?.Dispose();
            if (_recs is not null)
                foreach (var r in _recs) r.Dispose();
        }
        finally
        {
            _operationGate.Release();
        }
    }
}
