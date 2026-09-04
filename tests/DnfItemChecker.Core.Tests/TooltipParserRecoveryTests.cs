using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Core.Tests;

/// <summary>
/// Regression tests for OCR recovery behaviors: garbled tier syllables, label-column token fuzzing,
/// clipped rarity prefixes, and grade anchors merged with neighbouring glyphs.
/// </summary>
public class TooltipParserRecoveryTests
{
    // ---- 1. Grade-tier recovery (NormalizeTier via Parse) -------------------------------------

    [Theory]
    [InlineData("펼상급(911)", "최상급")]  // OCR broke 최 into 펼: Hangul fused onto 상급( can only be 최
    [InlineData("상급(60%)", "상급")]      // clean 상급 stays 상급 — no over-recovery
    [InlineData("최하급(5%)", "최하급")]   // the lowest tier resolves as itself, not 하급
    [InlineData("최상급(90%)", "최상급")]  // clean 최상급 untouched
    public void Parse_GradeLine_RecoversTier(string gradeLine, string expectedTier)
    {
        var r = new TooltipParser().Parse(new[] { new OcrLine(gradeLine, 10, 0, 120, 20) });
        Assert.Equal(expectedTier, r.GradeTier);
    }

    // ---- 2. Label-column per-token fuzzy (ResolveSlotLabels) ----------------------------------

    [Fact]
    public void ResolveSlotLabels_GarbledTokenInLongLine_RecoversSlot()
    {
        // Long label line: token "머리머매" is a jamo garble of 머리어깨 (0.75 ≥ 0.65 token gate).
        Assert.Equal("머리어깨", TooltipParser.ResolveSlotLabels(new[] { "머리머매 우;拿(흐기" }));
    }

    [Fact]
    public void ResolveSlotLabels_BodyBleedSetPointLine_DoesNotMatchBelt()
    {
        // 세트포인트 body text bleeding into the label crop: "세트"→벨트 scores 0.60, under the
        // 0.65 token gate — must not resolve to a slot at all.
        Assert.Null(TooltipParser.ResolveSlotLabels(new[] { "鵡공용 세트 포인트 65k -150)" }));
    }

    [Fact]
    public void ResolveSlotLabels_ShortPureLabel_RecoversAtLooseGate()
    {
        // Pure short label line (≤6 chars) fuzzes at the loose 0.5 gate: "머리메H" → 머리어깨.
        Assert.Equal("머리어깨", TooltipParser.ResolveSlotLabels(new[] { "머리메H" }));
    }

    // ---- 3. Rarity-prefix recovery on the grade row -------------------------------------------

    [Fact]
    public void Parse_ClippedRarityLabel_UniquePrefixRecovers()
    {
        // Crop clipped 태초's second glyph: a lone "태" on the grade row, right of the grade text,
        // is a unique prefix of exactly one rarity word.
        var r = new TooltipParser().Parse(new[]
        {
            new OcrLine("최하급(5%)", 20, 100, 120, 20),
            new OcrLine("태", 260, 105, 20, 20),   // grade row (|Δtop| ≤ 25), label position (Left > 140)
        });
        Assert.Equal("태초", r.RarityLabel);
    }

    [Fact]
    public void Parse_ClippedRarityLabel_AmbiguousPrefixStaysNull()
    {
        // "레" prefixes both 레어 and 레전더리 — ambiguous, must not guess.
        var r = new TooltipParser().Parse(new[]
        {
            new OcrLine("최하급(5%)", 20, 100, 120, 20),
            new OcrLine("레", 260, 105, 20, 20),
        });
        Assert.Null(r.RarityLabel);
    }

    // ---- 4. Grade-anchor refinement (RefineGradeAnchor via ParseAll) --------------------------

    [Fact]
    public void ParseAll_GradeLineMergedWithIconGlyphs_ShiftsAnchorRight()
    {
        // OCR merged an icon glyph run into the grade line, dragging Left ~100px left of the real
        // tooltip border. The anchor must shift right, proportional to the tier match's char index
        // (index 6 of 13 chars over 280px ≈ +129px), while keeping the line's right edge.
        var merged = new OcrLine("十흐돈견 1최상급(911)", 245, 276, 280, 20);
        var readings = new TooltipParser().ParseAll(new[] { merged });

        var r = Assert.Single(readings);
        Assert.Equal("최상급", r.GradeTier);
        Assert.NotNull(r.GradeBox);
        Assert.True(r.GradeBox!.Left > 300,
            $"anchor should shift right of the glyph junk, got Left={r.GradeBox.Left}");
        Assert.Equal(245 + 280, r.GradeBox.Left + r.GradeBox.Width); // right edge preserved
    }
}
