using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Tests;

public class EquippedItemMatcherTests
{
    private static JsonStatTable Loaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"st_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        table.LoadAsync().GetAwaiter().GetResult();
        try { return table; }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    private static DfEquippedItem Equipped(
        string slot, string name, string rarity = "에픽", string? slotId = null) =>
        new(slotId ?? slot, slot, $"id-{slot}-{name}", name, null, slot, 115, rarity,
            null, null, 0, "최상급", null, 0, null);

    private static TooltipReading Reading(
        string? name, string? slot, string? rarity) =>
        new(Array.Empty<string>(), name, null, "최상급", 100,
            new Dictionary<string, int>(), slot, rarity, null);

    [Fact]
    public void Find_UpgradePrefixesShareTheBaseItem_AndUsesTableReference()
    {
        var table = Loaded();
        var apiItem = Equipped("팔찌", "잠식 : 찬란한 황금향의 영광");
        var reading = Reading("+11 성축 : 찬란한 황금향의 영광", "팔찌", "에픽");

        var match = EquippedItemMatcher.Find(
            reading, "에픽", new[] { apiItem }, table, MainStat.Strength);

        Assert.NotNull(match);
        Assert.Equal(apiItem.ItemId, match!.Item.ItemId);
        Assert.Equal("팔찌", match.Slot);
        Assert.Equal("에픽", match.Rarity);
        Assert.Equal(153, match.ReferenceValue);
        Assert.Equal(EquippedItemMatchKind.ExactName, match.Kind);
    }

    [Fact]
    public void Find_UniqueSlotCanRecoverMissingNameAndRarity()
    {
        var table = Loaded();
        var bracelet = Equipped("팔찌", "현재 장착 팔찌");
        var top = Equipped("상의", "현재 장착 상의");
        var reading = Reading(null, "팔찌", null);

        var match = EquippedItemMatcher.Find(
            reading, null, new[] { bracelet, top }, table, MainStat.Strength);

        Assert.NotNull(match);
        Assert.Equal(bracelet.ItemId, match!.Item.ItemId);
        Assert.Equal(EquippedItemMatchKind.Slot, match.Kind);
        Assert.Equal(153, match.ReferenceValue);
    }

    [Fact]
    public void Find_MismatchedRarityFallsBackToUniqueSlotWithoutClaimingIt()
    {
        var table = Loaded();
        var bracelet = Equipped("팔찌", "현재 장착 팔찌", rarity: "에픽");
        var reading = Reading(null, "팔찌", "태초");

        var match = EquippedItemMatcher.Find(
            reading, "태초", new[] { bracelet }, table, MainStat.Strength);

        Assert.NotNull(match);
        Assert.Equal(bracelet.ItemId, match!.Item.ItemId);
        Assert.Equal("에픽", match.Rarity);
        Assert.Equal(EquippedItemMatchKind.Slot, match.Kind);
    }

    [Fact]
    public void Find_DifferentSlotDoesNotUseUnrelatedUniqueRow()
    {
        var table = Loaded();
        var top = Equipped("상의", "현재 장착 상의");

        var match = EquippedItemMatcher.Find(
            Reading(null, "팔찌", "에픽"), "에픽", new[] { top }, table);

        Assert.Null(match);
    }

    [Fact]
    public void Find_DuplicateExactNamesRemainUnmatched()
    {
        var table = Loaded();
        var first = Equipped("팔찌", "같은 장비");
        var second = Equipped("팔찌", "같은 장비", slotId: "BRACELET2");

        var match = EquippedItemMatcher.Find(
            Reading("같은 장비", "팔찌", "에픽"), "에픽", new[] { first, second }, table);

        Assert.Null(match);
    }

    [Fact]
    public void Find_WeaponAndTitleRowsAreNeverCandidates()
    {
        var table = Loaded();
        var weapon = Equipped("무기", "무기 이름", slotId: "WEAPON");
        var title = Equipped("칭호", "칭호 이름", slotId: "TITLE");

        var match = EquippedItemMatcher.Find(
            Reading("무기 이름", "무기", "에픽"), "에픽", new[] { weapon, title }, table);

        Assert.Null(match);
    }

    [Theory]
    [InlineData("+11 성축 : 찬란한 장비", "잠식 : 찬란한 장비")]
    [InlineData("잠식 : 찬란한 장비", "찬란한 장비")]
    public void NormalizeItemName_RemovesReinforceAndUpgradePrefix(string left, string right)
        => Assert.Equal(
            EquippedItemMatcher.NormalizeItemName(left),
            EquippedItemMatcher.NormalizeItemName(right));
    [Fact]
    public void Find_FuzzyNameUsesSlotAndRarityToDisambiguate()
    {
        var table = Loaded();
        var target = Equipped("팔찌", "찬란한 황금향의 영광");
        var other = Equipped("상의", "찬란한 황금향의 갑옷");

        var match = EquippedItemMatcher.Find(
            Reading("찬란한 황금향의 영", "팔찌", "에픽"),
            "에픽", new[] { target, other }, table);

        Assert.NotNull(match);
        Assert.Equal(target.ItemId, match!.Item.ItemId);
        Assert.Equal(EquippedItemMatchKind.FuzzyName, match.Kind);
    }

}
