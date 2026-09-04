using System.Drawing;
using System.Drawing.Imaging;

namespace DnfItemChecker.Vision.Tests;

public sealed class TooltipGeometryTests
{
    public static IEnumerable<object[]> ValidationResolutions()
    {
        yield return [800, 600];
        yield return [1024, 768];
        yield return [1600, 1200];
        yield return [1280, 720];
        yield return [1366, 768];
        yield return [1280, 800];
    }

    [Theory]
    [MemberData(nameof(ValidationResolutions))]
    public void LocatorFindsRelativeTooltipAtValidationResolutions(int width, int height)
    {
        var image = CreateTooltipCapture(width, height, 0.55);
        var tooltip = TooltipLocator.Locate(image, (width / 3, height / 3));

        Assert.True(tooltip.HasValue, $"No tooltip candidate at {width}x{height}");
        Assert.InRange(tooltip.Value.Left, width / 10 - 6, width / 10 + 6);
        Assert.InRange(tooltip.Value.Top, height / 8 - 6, height / 8 + 6);
        Assert.InRange(tooltip.Value.Width, (int)(Math.Min(width, height) * 0.48) - 6,
            (int)(Math.Min(width, height) * 0.48) + 6);
        Assert.InRange(tooltip.Value.Height, (int)(Math.Min(width, height) * 0.55) - 6,
            (int)(Math.Min(width, height) * 0.55) + 6);
    }

    [Theory]
    [MemberData(nameof(ValidationResolutions))]
    public void BlankCaptureHasNoTooltipFalsePositive(int width, int height)
    {
        var image = CreateBlankCapture(width, height);

        Assert.Null(TooltipLocator.Locate(image, (width / 2, height / 2)));
        Assert.Empty(TooltipLocator.LocateAll(image));
    }

    [Theory]
    [MemberData(nameof(ValidationResolutions))]
    public void CropUsesIsotropicWidthScaleAndPreservesVariableHeight(int width, int height)
    {
        var shortImage = CreateTooltipCapture(width, height, 0.38);
        var tallImage = CreateTooltipCapture(width, height, 0.68);
        var shortRect = Assert.IsType<Rectangle>(TooltipLocator.Locate(shortImage, (width / 3, height / 3)));
        var tallRect = Assert.IsType<Rectangle>(TooltipLocator.Locate(tallImage, (width / 3, height / 3)));

        var shortCrop = Assert.IsType<byte[]>(TooltipCropper.CropRect(shortImage, shortRect));
        var tallCrop = Assert.IsType<byte[]>(TooltipCropper.CropRect(tallImage, tallRect));
        using var shortBitmap = Decode(shortCrop);
        using var tallBitmap = Decode(tallCrop);

        Assert.InRange(shortBitmap.Width, 320, 350);
        Assert.InRange(tallBitmap.Width, 320, 350);
        Assert.True(tallBitmap.Height > shortBitmap.Height + 40,
            $"Variable source heights collapsed: {shortBitmap.Height} vs {tallBitmap.Height}");
        Assert.NotEqual(450, shortBitmap.Height);
        Assert.NotEqual(450, tallBitmap.Height);
    }
    [Fact]
    public void CropAllCanForceGradeAnchorForClippedComparisonPanel()
    {
        const int width = 800, height = 700;
        var image = CreateComparisonLikeCapture(width, height);
        var rightGrade = new DnfItemChecker.Core.Ocr.OcrLine("최상급(60)", 400, 150, 80, 19);

        var locatedCrop = TooltipCropper.CropAll(image, rightGrade).Tooltip;
        var anchoredCrop = TooltipCropper.CropAll(image, rightGrade, forceGradeAnchor: true).Tooltip;
        Assert.NotNull(locatedCrop);
        Assert.NotNull(anchoredCrop);

        using var leftBitmap = Decode(locatedCrop!);
        using var rightBitmap = Decode(anchoredCrop!);
        Assert.True(CountPixels(leftBitmap, static c => c.B > c.R + 40) > 0,
            "Locator crop should contain the left-panel marker.");
        Assert.True(CountPixels(rightBitmap, static c => c.R > c.B + 40) > 0,
            "Forced grade crop should contain the right-panel marker.");
    }

    private static byte[] CreateComparisonLikeCapture(int width, int height)
    {
        var left = new Rectangle(100, 80, 288, 460);
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(128, 128, 128));
            graphics.FillRectangle(Brushes.Black, left);
            graphics.FillRectangle(Brushes.Black, new Rectangle(400, 80, 288, 460));
            graphics.FillRectangle(Brushes.Blue, new Rectangle(190, 200, 20, 20));
            graphics.FillRectangle(Brushes.Red, new Rectangle(450, 200, 20, 20));
        }
        for (int y = left.Top; y < left.Bottom; y++)
        {
            bitmap.SetPixel(left.Left, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(left.Left + 1, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(left.Right - 2, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(left.Right - 1, y, Color.FromArgb(48, 48, 48));
        }
        return Encode(bitmap);
    }

    private static int CountPixels(Bitmap bitmap, Func<Color, bool> predicate)
    {
        int count = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
                if (predicate(bitmap.GetPixel(x, y))) count++;
        return count;
    }

    private static byte[] CreateBlankCapture(int width, int height)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.Clear(Color.FromArgb(128, 128, 128));
        return Encode(bitmap);
    }

    private static byte[] CreateTooltipCapture(int width, int height, double heightFraction)
    {
        int minDimension = Math.Min(width, height);
        int tooltipWidth = (int)Math.Round(minDimension * 0.48);
        int tooltipHeight = (int)Math.Round(minDimension * heightFraction);
        int left = width / 10;
        int top = height / 8;
        var rect = new Rectangle(left, top, tooltipWidth, tooltipHeight);

        using var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(128, 128, 128));
            graphics.FillRectangle(Brushes.Black, rect);
        }

        // The real detector relies on two neutral-gray vertical edges and a dark interior. Keep the
        // shape independent of any fixed pixel width so each target canvas exercises the same path.
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            bitmap.SetPixel(rect.Left, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(rect.Left + 1, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(rect.Right - 2, y, Color.FromArgb(48, 48, 48));
            bitmap.SetPixel(rect.Right - 1, y, Color.FromArgb(48, 48, 48));
        }

        return Encode(bitmap);
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        return stream.ToArray();
    }

    private static Bitmap Decode(byte[] bytes)
    {
        var stream = new MemoryStream(bytes);
        var bitmap = new Bitmap(stream);
        bitmap.Tag = stream;
        return bitmap;
    }
}
