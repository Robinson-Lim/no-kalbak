using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Tests;

public class RarityPaletteTests
{
    [Theory]
    [InlineData(42, 0.90, 0.80, "에픽")]      // gold/yellow (measured ~42)
    [InlineData(157, 0.60, 0.75, "태초")]     // teal/green (measured ~157)
    [InlineData(25, 0.85, 0.80, "레전더리")]  // orange
    [InlineData(320, 0.70, 0.80, "유니크")]   // pink/magenta
    public void Classify_MapsHueToRarity(double h, double s, double v, string expected)
        => Assert.Equal(expected, RarityPalette.Classify(h, s, v));

    [Theory]
    [InlineData(42, 0.10, 0.80)] // unsaturated => white/gray name text, not a rarity color
    [InlineData(42, 0.90, 0.20)] // too dark
    public void Classify_DullOrDark_IsNull(double h, double s, double v)
        => Assert.Null(RarityPalette.Classify(h, s, v));

    [Fact]
    public void IsColorAmbiguous_OnlyWarmPair()
    {
        Assert.True(RarityPalette.IsColorAmbiguous("레전더리"));
        Assert.True(RarityPalette.IsColorAmbiguous("에픽"));
        Assert.False(RarityPalette.IsColorAmbiguous("유니크"));
        Assert.False(RarityPalette.IsColorAmbiguous("태초"));
    }
}
