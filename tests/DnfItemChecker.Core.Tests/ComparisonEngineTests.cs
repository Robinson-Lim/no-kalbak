using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Tests;

public class ComparisonEngineTests
{
    // Real seeded table (file is read/seeded then removed; the table stays in memory).
    private static ComparisonEngine Engine()
    {
        var path = Path.Combine(Path.GetTempPath(), $"st_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        table.LoadAsync().GetAwaiter().GetResult();
        try { return new ComparisonEngine(table); }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    // slot -> ItemTypeDetail, rarity -> ItemRarity, grade -> ItemGradeName.
    private static DfEquippedItem Equipped(string slot, string rarity, string? grade) =>
        new("SLOT", slot, "id", "name", null, slot, 115, rarity, null, null, 0, grade, null, 0, null);
    private static EquippedStatObservation Observation(DfEquippedItem item, MainStat stat, int value,
        string? itemId = null) =>
        new("s", "c", item.ItemTypeDetail!, itemId ?? item.ItemId, item.ItemName,
            item.ItemRarity, stat, value, item.ItemGradeName, 100, DateTimeOffset.UtcNow, "test");

    [Fact]
    public void CompareEquipped_TopTier_WithStoredObservation_IsMatch()
    {
        // 팔찌 에픽 힘 = 153 (100% reference).
        var item = Equipped("팔찌", "에픽", "최상급");
        var r = Engine().CompareEquipped(item, MainStat.Strength, Observation(item, MainStat.Strength, 153));
        Assert.Equal(ComparisonOutcome.Match, r.Outcome);
        Assert.Equal(153, r.ReferenceValue);
        Assert.Equal(153, r.ObservedValue);
    }

    [Fact]
    public void CompareEquipped_BelowTopTier_WithStoredObservation_IsBelow()
    {
        var item = Equipped("팔찌", "에픽", "상급");
        var r = Engine().CompareEquipped(item, MainStat.Strength, Observation(item, MainStat.Strength, 150));
        Assert.Equal(ComparisonOutcome.Below, r.Outcome);
        Assert.Equal(153, r.ReferenceValue);
        Assert.Equal(150, r.ObservedValue);
    }

    [Fact]
    public void CompareEquipped_NonGradedRarity_WithStoredObservation_IsMatch()
    {
        // 유니크 has no upgrade grade tier, but tab2 still requires the measured value.
        var item = Equipped("상의", "유니크", null);
        var r = Engine().CompareEquipped(item, MainStat.Vitality, Observation(item, MainStat.Vitality, 142));
        Assert.Equal(ComparisonOutcome.Match, r.Outcome);
        Assert.Equal(142, r.ReferenceValue);
        Assert.Equal(142, r.ObservedValue);
    }

    [Fact]
    public void CompareEquipped_WithoutStoredObservation_IsUnmeasured()
    {
        var r = Engine().CompareEquipped(Equipped("팔찌", "에픽", "최상급"), MainStat.Strength);
        Assert.Equal(ComparisonOutcome.Unmeasured, r.Outcome);
        Assert.Equal("미측정/재인식 필요", r.Note);
        Assert.Null(r.ObservedValue);
    }

    [Fact]
    public void CompareEquipped_StaleItemId_IsUnmeasured()
    {
        var item = Equipped("팔찌", "에픽", "최상급");
        var r = Engine().CompareEquipped(item, MainStat.Strength,
            Observation(item, MainStat.Strength, 153, itemId: "replaced"));
        Assert.Equal(ComparisonOutcome.Unmeasured, r.Outcome);
        Assert.Null(r.ObservedValue);
    }

    [Fact]
    public void CompareEquipped_AboveReference_IsBelow_NotAtLeastMatch()
    {
        var item = Equipped("팔찌", "에픽", "최상급");
        var r = Engine().CompareEquipped(item, MainStat.Strength, Observation(item, MainStat.Strength, 154));
        Assert.Equal(ComparisonOutcome.Below, r.Outcome);
    }

    [Fact]
    public void CompareEquipped_UnfilledReference_IsIndeterminate()
    {
        var item = Equipped("상의", "레어", "최상급");
        var result = Engine().CompareEquipped(item, MainStat.Strength,
            Observation(item, MainStat.Strength, 144));

        Assert.Equal(ComparisonOutcome.Indeterminate, result.Outcome);
        Assert.Null(result.ReferenceValue);
        Assert.Null(result.ObservedValue);
    }
    [Fact]
    public void CompareEquipped_UnknownSlot_IsNotFound()
    {
        var r = Engine().CompareEquipped(Equipped("무기", "에픽", "최상급"), MainStat.Strength);
        Assert.Equal(ComparisonOutcome.NotFound, r.Outcome);
        Assert.Null(r.ReferenceValue);
    }

    // 상의 에픽 = 144 all-stat in the seed. Tab3 verdict is grade-based.
    [Fact]
    public void Compare_TopTier100_IsMatch()
    {
        var r = Engine().Compare("상의", "에픽", "최상급", 100, 144, MainStat.Strength);
        Assert.Equal(ComparisonOutcome.Match, r.Outcome);
        Assert.Equal(144, r.ReferenceValue);
    }

    [Fact]
    public void Compare_TopTierAnyQuality_IsMatch()
    {
        // 최상급 tier = 극옵, regardless of the (OCR-unreliable) quality% — verdict is tier-only.
        var r = Engine().Compare("상의", "에픽", "최상급", 69, 140, MainStat.Strength);
        Assert.Equal(ComparisonOutcome.Match, r.Outcome);
    }

    [Fact]
    public void Compare_BelowTopTier_IsBelow()
    {
        var r = Engine().Compare("상의", "에픽", "상급", 100, 130, MainStat.Strength);
        Assert.Equal(ComparisonOutcome.Below, r.Outcome);
    }

    [Fact]
    public void Compare_StatUnresolved_TopTier100_IsMatch()
    {
        // Class-independent: with no main stat and no observed value, 최상급 100% still verdicts 극옵.
        var r = Engine().Compare("상의", "에픽", "최상급", 100, null, null);
        Assert.Equal(ComparisonOutcome.Match, r.Outcome);
    }

    [Fact]
    public void Compare_RareTopTier100_IsMatch_WithoutTableEntry()
    {
        // 레어 cell is an unfilled 0 placeholder, yet the grade-based verdict still resolves 극옵.
        var r = Engine().Compare("상의", "레어", "최상급", 100, null, MainStat.Strength);
        Assert.Equal(ComparisonOutcome.Match, r.Outcome);
        Assert.Null(r.ReferenceValue); // unfilled → not shown as a reference
    }

    [Fact]
    public void Compare_NoGrade_FallsBackToValue()
    {
        // Grade line unreadable → observed ≥ reference still matches; below it is Below.
        Assert.Equal(ComparisonOutcome.Match, Engine().Compare("상의", "에픽", null, null, 144, MainStat.Strength).Outcome);
        Assert.Equal(ComparisonOutcome.Below, Engine().Compare("상의", "에픽", null, null, 140, MainStat.Strength).Outcome);
    }

    [Fact]
    public void Compare_NoGrade_NoValue_IsIndeterminate()
    {
        var r = Engine().Compare("상의", "에픽", null, null, null, MainStat.Strength);
        Assert.Equal(ComparisonOutcome.Indeterminate, r.Outcome);
    }

    [Fact]
    public void Compare_TopTier_NoSlot_StillMatches()
    {
        // Grade tier alone decides the verdict — a 최상급 item is 극옵 even if the slot label failed OCR.
        var r = Engine().Compare(null, "에픽", "최상급", 80, null, MainStat.Strength);
        Assert.Equal(ComparisonOutcome.Match, r.Outcome);
    }

    [Fact]
    public void Compare_NoGrade_NoSlot_IsNotFound()
    {
        var r = Engine().Compare(null, "에픽", null, null, 144, MainStat.Strength);
        Assert.Equal(ComparisonOutcome.NotFound, r.Outcome);
    }
}
