using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Vision;

/// <summary>
/// Applies the normalized fixed-ROI readers to one cropped tooltip OCR result. This is the boundary
/// between pixel/finite-field extraction and the existing <see cref="TooltipReading"/> contract.
/// </summary>
public static class TooltipStructuredReader
{
    public static TooltipReading Apply(TooltipReading reading, IReadOnlyList<OcrLine> normalizedLines)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(normalizedLines);

        var stats = new Dictionary<string, int>(reading.MainStatValues);
        foreach (var pair in TooltipDigitReader.ReadMainStats(normalizedLines))
            stats[pair.Key] = pair.Value;

        var quality = TooltipDigitReader.ReadQuality(normalizedLines, reading.GradeTier);
        var name = TooltipDigitReader.ReadItemName(normalizedLines);
        var slot = TooltipFiniteClassReader.ReadSlot(normalizedLines);
        return reading with
        {
            ItemName = name ?? reading.ItemName,
            GradePercent = quality ?? reading.GradePercent,
            Slot = slot ?? reading.Slot,
            MainStatValues = stats,
        };
    }
}
