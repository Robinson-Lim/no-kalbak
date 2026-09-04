namespace DnfItemChecker.Core.Stats;

/// <summary>
/// Maps a DNF item-name color to its rarity. Measured in-game name hues (HSV degrees):
/// 레어 ≈ 보라/자(H≈269), 유니크 ≈ 분홍, 레전더리 ≈ 주황, 에픽 ≈ 금색(H≈42), 태초 ≈ 청록(H≈157).
/// 레전더리/에픽 sit close on the warm side, so callers cross-check that pair with the OCR'd label.
/// </summary>
public static class RarityPalette
{
    public const string Rare = "레어";
    public const string Unique = "유니크";
    public const string Legendary = "레전더리";
    public const string Epic = "에픽";
    public const string Primeval = "태초";

    /// <summary>Rarities in ascending order — also the label cross-check vocabulary.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Rare, Unique, Legendary, Epic, Primeval };

    /// <summary>True for the warm pair that color alone cannot reliably separate.</summary>
    public static bool IsColorAmbiguous(string? rarity) => rarity is Legendary or Epic;

    /// <summary>
    /// Classify an averaged/median name color (hue 0-360, sat/val 0-1) into a rarity. Returns null
    /// when the sample is too dull/dark to be a colored rarity name (white/gray text).
    /// </summary>
    public static string? Classify(double hue, double sat, double val)
    {
        if (val < 0.30 || sat < 0.22) return null;          // white/gray/dark → not a rarity color
        if (hue >= 110 && hue <= 200) return Primeval;       // teal/green
        if (hue >= 245 && hue < 288) return Rare;            // purple/violet (measured ~269)
        if (hue >= 288 || hue < 12) return Unique;           // pink/magenta (with red wrap)
        if (hue >= 12 && hue < 37) return Legendary;         // orange
        if (hue >= 37 && hue < 75) return Epic;              // gold/yellow
        return null;
    }
}
