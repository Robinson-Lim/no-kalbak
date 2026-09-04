using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Core.Tests;

public class TooltipParserTests
{
    // Synthetic single-column layout: one line per row, increasing Top.
    private static OcrLine[] Lines(params string[] text)
    {
        var arr = new OcrLine[text.Length];
        for (int i = 0; i < text.Length; i++)
            arr[i] = new OcrLine(text[i], 10, i * 22, 200, 20);
        return arr;
    }

    // Real garbled OCR sample: name wraps across two lines, '%' OCRs as a digit, trailing "+113" enchant.
    private static readonly OcrLine[] Sample = Lines(
        "+11 잠식 : 찬란한 왕금힐의 플",
        "레미트 레긷스",
        "최상급(941)",
        "지능 144 +113",
        "최종 데미지 0.41 종가");

    [Fact]
    public void Parse_ExtractsReinforce() => Assert.Equal(11, new TooltipParser().Parse(Sample).Reinforce);

    [Fact]
    public void Parse_ExtractsGradeTierAndPercent()
    {
        var r = new TooltipParser().Parse(Sample);
        Assert.Equal("최상급", r.GradeTier);
        Assert.Equal(94, r.GradePercent); // "(941)" collapses the spurious % digit
    }

    [Fact]
    public void Parse_ExtractsBaseMainStat_IgnoringEnchant()
    {
        var r = new TooltipParser().Parse(Sample);
        Assert.True(r.MainStatValues.TryGetValue("지능", out int intel));
        Assert.Equal(144, intel);
    }

    [Fact]
    public void Parse_ConcatenatesWrappedName_StripsReinforceToken()
    {
        var r = new TooltipParser().Parse(Sample);
        Assert.NotNull(r.ItemName);
        Assert.DoesNotContain("+11", r.ItemName!);
        Assert.Contains("찬란한", r.ItemName!);
        Assert.Contains("레긷스", r.ItemName!); // proves wrap concatenation
    }

    [Fact]
    public void Parse_NonStatLines_DoNotProduceStats()
    {
        var r = new TooltipParser().Parse(Sample);
        Assert.False(r.MainStatValues.ContainsKey("힘"));
        Assert.Single(r.MainStatValues);
    }

    [Fact]
    public void Parse_ExtractsMainStat_WithPlusPrefix()
    {
        // Armor shows its main stat as "힘 +80"; the leading '+' must not stop the value being read.
        var r = new TooltipParser().Parse(Lines(
            "한계를 넘어선 에너지 세트",
            "최상급(88)",
            "물리 방어력 1435 +1400",
            "힘 +80"));
        Assert.True(r.MainStatValues.TryGetValue("힘", out int str));
        Assert.Equal(80, str);
    }

    [Fact]
    public void Parse_RecoversGarbledStatKeyword()
    {
        // OCR routinely reads 힘→험; the jamo-fuzzy fallback still resolves the stat (and its value).
        var r = new TooltipParser().Parse(Lines(
            "고대 전장의 발키리 세트",
            "상급(60)",
            "험 117 +36"));
        Assert.True(r.MainStatValues.TryGetValue("힘", out int str));
        Assert.Equal(117, str); // base value, ignoring the +36 enchant
    }
    [Fact]
    public void Parse_StopsAtEnchantSection_IgnoresEnchantStat()
    {
        // The <마법부여> section repeats "힘 +80"; it must NOT override the item's real main stat above.
        var r = new TooltipParser().Parse(Lines(
            "짙은 그림자를 녹여내는 하의",
            "최상급(94)",
            "물리 방어력 1420 +1400",
            "힘 144 +36",
            "<마법부여>",
            "물리 공격력 +110",
            "힘 +80"));
        Assert.True(r.MainStatValues.TryGetValue("힘", out int str));
        Assert.Equal(144, str); // the real main stat, not the +80 enchant below 마법부여
    }

    [Fact]
    public void Parse_DroppedStatKeyword_PrefersNoValueOverEnchant()
    {
        // When the real main-stat keyword is lost ("144 +36"), the enchant "힘 +90" must still not leak in.
        var r = new TooltipParser().Parse(Lines(
            "짙은 그림자를 입은 죽음 상의",
            "최상급(94)",
            "물리 방어력 1716 +1400",
            "144 +36",
            "<마법부여>",
            "힘 +90"));
        Assert.False(r.MainStatValues.ContainsKey("힘")); // no wrong value; correct-or-fail
    }

    [Fact]
    public void Parse_RepairsSingleDigitQualityWhenTierRejectsTrailingOne()
    {
        var r = new TooltipParser().Parse(Lines(
            "엘더 페어리 신발",
            "최하급(51)",
            "지능 104"));

        Assert.Equal("최하급", r.GradeTier);
        Assert.Equal(5, r.GradePercent);
    }

    [Fact]
    public void Parse_RepairsDroppedTrailingZeroWhenUpperTierStartsAtSixty()
    {
        var r = new TooltipParser().Parse(Lines(
            "선봉의 발키리 수호 팔찌",
            "상급(6)",
            "지능 140"));

        Assert.Equal("상급", r.GradeTier);
        Assert.Equal(60, r.GradePercent);
    }

    [Fact]
    public void Parse_RepairsNinePercentWhenTierRejectsTrailingOne()
    {
        var r = new TooltipParser().Parse(Lines(
            "견습 여우의 은빛 신발",
            "최하급(91)",
            "지능 106"));

        Assert.Equal(9, r.GradePercent);
    }

    [Fact]
    public void Parse_StopsAtAttackPowerBoundaryWhenEnchantHeaderIsClipped()
    {
        var r = new TooltipParser().Parse(Lines(
            "고유 - 청해의 강건한 각반",
            "상급(69)",
            "지능 137",
            "체력 133",
            "정신력 136",
            "공격력 증가 +3189.0%",
            "체력 +90"));

        Assert.Equal(133, r.MainStatValues["체력"]);
    }

    [Fact]
    public void Parse_ExtractsSlotAndRarityLabel()
    {
        var r = new TooltipParser().Parse(Lines(
            "찬란한 황금향의 집착 - 보조장비", // slot word embedded in the name
            "에픽",                              // rarity label on the grade row
            "최상급(96)",
            "지능 151 +113"));
        Assert.Equal("보조장비", r.Slot);
        Assert.Equal("에픽", r.RarityLabel);
    }

    [Fact]
    public void Parse_PicksLeftmostTooltip_InComparison()
    {
        // Comparison capture: inspected (left, small X) vs equipped (right, large X). Keep the left.
        var lines = new[]
        {
            new OcrLine("개시의 극기 훈련 상의", 62, 8, 200, 20),   // left name
            new OcrLine("최상급(60)", 8, 60, 120, 20),              // left grade
            new OcrLine("상의", 263, 60, 60, 20),                   // left slot label
            new OcrLine("지능 130", 8, 290, 120, 20),               // left base stat
            new OcrLine("현재 장착 중", 418, 8, 120, 20),           // right marker
            new OcrLine("수습 여우의 상의", 323, 36, 200, 20),      // right name
            new OcrLine("최상급(60)", 322, 89, 120, 20),            // right grade
            new OcrLine("레전더리", 548, 89, 90, 20),               // right rarity label
            new OcrLine("지능 133", 322, 250, 120, 20),             // right stat
        };
        var r = new TooltipParser().Parse(lines);
        Assert.Equal("상의", r.Slot);
        Assert.True(r.MainStatValues.TryGetValue("지능", out int v));
        Assert.Equal(130, v);                       // left stat, not the right's 133
        Assert.NotEqual("레전더리", r.RarityLabel); // right's label must not bleed in
    }

    [Fact]
    public void ParseAll_RetainsRightComparisonTooltipForSaveMode()
    {
        var lines = new[]
        {
            new OcrLine("검사 대상 상의", 62, 8, 200, 20),
            new OcrLine("최상급(60)", 8, 60, 120, 20),
            new OcrLine("상의", 263, 60, 60, 20),
            new OcrLine("지능 130", 8, 290, 120, 20),
            new OcrLine("현재 장착 중", 418, 8, 120, 20),
            new OcrLine("장착 상의", 323, 36, 200, 20),
            new OcrLine("최상급(60)", 322, 89, 120, 20),
            new OcrLine("에픽", 548, 89, 90, 20),
            new OcrLine("지능 133", 322, 250, 120, 20),
        };

        var readings = new TooltipParser().ParseAll(lines);

        Assert.Equal(2, readings.Count);
        Assert.Equal(130, readings[0].MainStatValues["지능"]);
        Assert.Equal(133, readings[1].MainStatValues["지능"]);
        Assert.True(readings[1].GradeBox!.Left > readings[0].GradeBox!.Left);
    }
}
