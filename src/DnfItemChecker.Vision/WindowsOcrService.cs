using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using CoreLine = DnfItemChecker.Core.Ocr.OcrLine;

namespace DnfItemChecker.Vision;

/// <summary>
/// Korean OCR backed by <see cref="OcrEngine"/> (Windows.Media.Ocr). Prefers the
/// "ko" recognizer, falling back to the user's profile languages.
/// </summary>
public sealed class WindowsOcrService : IOcrService
{
    private static readonly OcrResult Empty = new(Array.Empty<CoreLine>());

    private readonly OcrEngine? _engine;

    public WindowsOcrService()
    {
        _engine = OcrEngine.TryCreateFromLanguage(new Language("ko"))
                  ?? OcrEngine.TryCreateFromUserProfileLanguages();
    }

    public bool IsAvailable => _engine is not null;

    public async Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default, double maxScale = 4.0)
    {
        if (_engine is null || imageBytes.Length == 0)
            return Empty;

        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(imageBytes.AsBuffer()).AsTask(ct).ConfigureAwait(false);
        stream.Seek(0);

        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct).ConfigureAwait(false);

        // Upscale small captures before OCR: Korean tooltip text reads markedly better at higher
        // resolution. Scale by WIDTH — a tooltip is portrait, so width constrains the text size while a
        // tall capture (full tooltip + margin) is not over-zoomed; cap the longer side to a sane bitmap.
        // `maxScale` caps zoom — thin 2-char labels blur and drop above ~2x, so the label pass lowers it.
        uint w = decoder.PixelWidth, h = decoder.PixelHeight;
        double scale = 1.0;
        var transform = new BitmapTransform();
        uint maxDim = Math.Max(w, h);
        if (w is > 0 and < 1200)
        {
            scale = Math.Min(maxScale, 1200.0 / w);
            if (maxDim * scale > 2600) scale = 2600.0 / maxDim;
            if (scale > 1.0)
            {
                transform.ScaledWidth = (uint)(w * scale);
                transform.ScaledHeight = (uint)(h * scale);
                transform.InterpolationMode = BitmapInterpolationMode.Fant;
            }
            else scale = 1.0;
        }
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)
            .AsTask(ct).ConfigureAwait(false);

        var result = await _engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);

        var lines = new List<CoreLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (line.Words.Count == 0)
            {
                lines.Add(new CoreLine(line.Text, 0, 0, 0, 0));
                continue;
            }

            double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                if (r.Left < left) left = r.Left;
                if (r.Top < top) top = r.Top;
                if (r.Left + r.Width > right) right = r.Left + r.Width;
                if (r.Top + r.Height > bottom) bottom = r.Top + r.Height;
            }

            // Report boxes in the *input* image's coordinates (undo the OCR upscale) so callers
            // (e.g. the rarity color reader) can sample the original PNG at these positions.
            lines.Add(new CoreLine(line.Text, left / scale, top / scale, (right - left) / scale, (bottom - top) / scale));
        }

        return new OcrResult(lines);
    }
}
