using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Vision.Tests;

public sealed class TooltipStructuredFieldTests
{
    [Fact]
    public void QualityReaderUsesOnlyCanonicalGradeRoi()
    {
        var lines = new[]
        {
            new OcrLine("최상급(94%)", 30, 108, 100, 18),
            new OcrLine("최상급(12%)", 30, 20, 100, 18),
        };

        Assert.Equal(94, TooltipDigitReader.ReadQuality(lines));
    }

    [Fact]
    public void QualityReaderRecoversDroppedTrailingZeroForTier()
    {
        var lines = new[]
        {
            new OcrLine("상급(6%)", 30, 108, 100, 18),
        };

        Assert.Equal(60, TooltipDigitReader.ReadQuality(lines, "상급"));
    }

    [Fact]
    public void StructuredReaderRepairsQualityUsingTheReadingTier()
    {
        var reading = new TooltipReading(
            new[] { "상급(60)" }, null, null, "상급", null,
            new Dictionary<string, int>(), null, null, null);
        var lines = new[]
        {
            new OcrLine("상급(6%)", 30, 108, 100, 18),
        };

        var applied = TooltipStructuredReader.Apply(reading, lines);

        Assert.Equal(60, applied.GradePercent);
    }

    [Theory]
    [InlineData(3, 60, 80, 19, 200, 220)]
    [InlineData(80, 130, 160, 38, 400, 500)]
    public void AnchoredCropPadsOutsideImageAndKeepsCanonicalGradePosition(
        int gradeLeft, int gradeTop, int gradeWidth, int gradeHeight, int sourceWidth, int sourceHeight)
    {
        using var bitmap = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Black);
            graphics.FillRectangle(Brushes.White, gradeLeft, gradeTop, gradeWidth, gradeHeight);
        }

        var grade = new OcrLine("상급(60)", gradeLeft, gradeTop, gradeWidth, gradeHeight);
        Assert.True(TooltipCropper.TryCrop(Encode(bitmap), grade, out var cropped));

        using var normalized = new Bitmap(new MemoryStream(cropped));
        Assert.Equal(TooltipFieldLayout.CanonicalCropWidth, normalized.Width);
        Assert.True(normalized.GetPixel(70, 119).R > 200);
    }

    [Fact]
    public void MainStatReaderUsesFiniteDomainAndRejectsOtherRows()
    {
        var lines = new[]
        {
            new OcrLine("힘 143", 25, 330, 90, 20),
            new OcrLine("힘 1000", 25, 370, 100, 20),
            new OcrLine("힘 80", 25, 480, 90, 20),
        };

        var stats = TooltipDigitReader.ReadMainStats(lines);

        Assert.Equal(143, stats["힘"]);
        Assert.Single(stats);
    }

    [Fact]
    public void ItemNameReaderStripsReinforcementFromNameRoi()
    {
        var lines = new[]
        {
            new OcrLine("+12 ", 35, 20, 80, 18),
            new OcrLine("시공의 폭풍", 35, 48, 120, 18),
            new OcrLine("힘 143", 20, 330, 80, 18),
        };

        Assert.Equal("시공의 폭풍", TooltipDigitReader.ReadItemName(lines));
    }

    [Fact]
    public void SlotReaderRestrictsResultToCanonicalSlotRoi()
    {
        var lines = new[]
        {
            new OcrLine("반지", 250, 40, 40, 18),
            new OcrLine("마법석", 260, 175, 60, 18),
        };

        Assert.Equal("마법석", TooltipFiniteClassReader.ReadSlot(lines));
    }

    [Fact]
    public void RarityReaderClassifiesCanonicalColorRoi()
    {
        using var bitmap = new Bitmap(340, 500, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.FromArgb(25, 25, 25));
            graphics.FillRectangle(new SolidBrush(Color.Gold), TooltipFieldLayout.Get(TooltipFieldKind.Rarity));
        }

        Assert.Equal("에픽", TooltipFiniteClassReader.ReadRarity(Encode(bitmap), new RarityColorReader()));
    }

    private static byte[] Encode(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Bmp);
        return stream.ToArray();
    }
}
