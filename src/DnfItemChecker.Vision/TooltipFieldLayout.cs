using System.Drawing;

namespace DnfItemChecker.Vision;

/// <summary>
/// Canonical field geometry for a tooltip normalized around its grade row.
/// Coordinates are independent of screen resolution and UI scale. The main-stat ROI is deliberately
/// a search region: optional rows move the stat line vertically between item types.
/// </summary>
public enum TooltipFieldKind
{
    ItemName,
    Grade,
    Rarity,
    Slot,
    MainStat,
}

public static class TooltipFieldLayout
{
    public const int CanonicalBodyWidth = 320;
    public const int CanonicalCropWidth = 340;
    public const int CanonicalGradeHeight = 19;
    public const int GradeAnchorAbove = 110;

    public static Rectangle Get(TooltipFieldKind field)
        => field switch
        {
            TooltipFieldKind.ItemName => new Rectangle(35, 0, 285, 100),
            TooltipFieldKind.Grade => new Rectangle(0, 95, 125, 35),
            TooltipFieldKind.Rarity => new Rectangle(245, 95, 95, 35),
            TooltipFieldKind.Slot => new Rectangle(225, 150, 115, 65),
            TooltipFieldKind.MainStat => new Rectangle(12, 250, 308, 210),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null),
        };
}
