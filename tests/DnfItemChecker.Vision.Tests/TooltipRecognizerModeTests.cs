using System.Drawing;
using System.Drawing.Imaging;
using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Vision.Tests;

public sealed class TooltipRecognizerModeTests
{
    [Fact]
    public async Task ImmediateModeUsesOneTooltipAndSkipsValueOcr()
    {
        var primary = new StubOcr(new OcrResult([
            new OcrLine("테스트 상의", 62, 8, 200, 20),
            new OcrLine("최상급(60)", 8, 60, 120, 20),
            new OcrLine("상의", 263, 60, 60, 20),
            new OcrLine("에픽", 548, 60, 60, 20),
            new OcrLine("힘 130", 8, 290, 120, 20),
        ]));
        var value = new StubOcr(new OcrResult([
            new OcrLine("힘 999", 8, 290, 120, 20),
        ]));
        var recognizer = new TooltipRecognizer(
            primary,
            new TooltipParser(),
            new FixedRarityReader("에픽"),
            value);

        var result = await recognizer.RecognizeAsync(
            CreateTooltipCapture(), (200, 200), mode: TooltipRecognitionMode.Immediate);

        Assert.True(result.Timing!.FastPathUsed);
        Assert.True(result.Timing.LocatorFound);
        Assert.Equal(0, result.Timing.OnnxOcrMs);
        Assert.Equal(0, value.Calls);
        Assert.Equal("상의", result.Reading.Slot);
        Assert.Equal("에픽", result.Rarity);
        Assert.Equal("최상급", result.Reading.GradeTier);
        Assert.Single(result.Readings);

    }
    private sealed class StubOcr(OcrResult result) : IOcrService
    {
        public int Calls { get; private set; }
        public bool IsAvailable => true;

        public Task<OcrResult> RecognizeAsync(
            byte[] imageBytes, CancellationToken ct = default, double maxScale = 4.0)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class FixedRarityReader(string rarity) : IRarityColorReader
    {
        public string? DetectRarity(byte[] imageBytes, params OcrLine?[] candidateBoxes) => rarity;
    }

    private static byte[] CreateTooltipCapture()
    {
        const int width = 800, height = 600;
        var rect = new Rectangle(80, 75, 288, 330);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(128, 128, 128));
            graphics.FillRectangle(Brushes.Black, rect);
        }
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            bitmap.SetPixel(rect.Left, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(rect.Left + 1, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(rect.Right - 2, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(rect.Right - 1, y, Color.FromArgb(48, 48, 48));
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        return stream.ToArray();
    }
}
