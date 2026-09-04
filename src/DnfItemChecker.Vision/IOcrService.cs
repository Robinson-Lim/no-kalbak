using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Vision;

public sealed record OcrResult(IReadOnlyList<OcrLine> Lines)
{
    public IReadOnlyList<string> TextLines => Lines.Select(l => l.Text).ToList();
}

/// <summary>Korean OCR over a captured image (Windows.Media.Ocr backed).</summary>
public interface IOcrService
{
    /// <summary>False when the Korean recognizer language pack is unavailable.</summary>
    bool IsAvailable { get; }

    /// <summary><paramref name="maxScale"/> caps the pre-OCR upscale of small images; lower it (≈2) for
    /// thin 2-char labels that blur and drop at higher zoom.</summary>
    Task<OcrResult> RecognizeAsync(byte[] imageBytes, CancellationToken ct = default, double maxScale = 4.0);
}
