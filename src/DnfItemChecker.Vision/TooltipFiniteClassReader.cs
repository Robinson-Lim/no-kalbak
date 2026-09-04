using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Vision;

/// <summary>
/// Reads fields whose output domain is closed by the game schema. Recognition may still use OCR for
/// glyph evidence, but the result is never an unconstrained string: slot is selected only from the
/// known equipment-slot vocabulary and rarity is selected only from the color palette.
/// </summary>
public static class TooltipFiniteClassReader
{
    public static string? ReadRarity(byte[] normalizedTooltip, RarityColorReader colorReader)
    {
        ArgumentNullException.ThrowIfNull(colorReader);
        return colorReader.DetectRarity(
            normalizedTooltip, TooltipFieldLayout.Get(TooltipFieldKind.Rarity));
    }

    public static string? ReadSlot(IReadOnlyList<OcrLine> normalizedLines)
    {
        if (normalizedLines is null || normalizedLines.Count == 0)
            return null;

        var roi = TooltipFieldLayout.Get(TooltipFieldKind.Slot);
        var candidates = normalizedLines
            .Where(line => Intersects(line, roi))
            .Select(line => line.Text ?? string.Empty)
            .Where(static text => !string.IsNullOrWhiteSpace(text));
        return TooltipParser.ResolveSlotLabels(candidates);
    }

    private static bool Intersects(OcrLine line, System.Drawing.Rectangle roi)
        => line.Left < roi.Right && line.Left + line.Width > roi.Left
            && line.Top < roi.Bottom && line.Top + line.Height > roi.Top;
}
