using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Tests;

public class StatTableTests
{
    private static JsonStatTable Loaded()
    {
        var path = Path.Combine(Path.GetTempPath(), $"st_{Guid.NewGuid():N}.json");
        var table = new JsonStatTable(path);
        table.LoadAsync().GetAwaiter().GetResult();
        try { return table; }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [Fact]
    public void Seed_AccessoriesHavePrimeval_ArmorDoesNot()
    {
        var t = Loaded();
        Assert.Contains("태초", t.RaritiesFor("목걸이"));
        Assert.Contains("태초", t.RaritiesFor("반지"));
        Assert.DoesNotContain("태초", t.RaritiesFor("상의"));
    }

    [Fact]
    public void Get_NecklaceEpic_MainAndOffStats()
    {
        var t = Loaded();
        Assert.Equal(153, t.GetValue("목걸이", "에픽", MainStat.Intelligence)); // main
        Assert.Equal(235, t.GetValue("목걸이", "태초", MainStat.Spirit));        // main, top tier
        Assert.Equal(100, t.GetValue("목걸이", "에픽", MainStat.Strength));      // off-stat floor
    }

    [Fact]
    public void GetValue_UnknownCell_IsNull()
    {
        var t = Loaded();
        Assert.Null(t.GetValue("상의", "태초", MainStat.Strength)); // armor has no 태초
        Assert.Null(t.GetValue("무기", "에픽", MainStat.Strength)); // not a tracked slot
    }

    [Theory]
    [InlineData("팔찌", "팔찌")]                  // exact (API itemTypeDetail)
    [InlineData("영롱한 황금향의 영광 - 팔찌", "팔찌")] // slot word embedded in the item name
    public void TryResolveSlot_Maps(string raw, string expected)
    {
        Assert.True(Loaded().TryResolveSlot(raw, out var slot));
        Assert.Equal(expected, slot);
    }

    [Fact]
    public void TryResolveRarity_FromApiValue()
    {
        Assert.True(Loaded().TryResolveRarity("에픽", out var rarity));
        Assert.Equal("에픽", rarity);
    }

    [Fact]
    public async Task SetAsync_PersistsAndReloads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"st_{Guid.NewGuid():N}.json");
        try
        {
            var t1 = new JsonStatTable(path);
            await t1.LoadAsync();
            await t1.SetAsync("상의", "에픽", new StatLine(999, 999, 999, 999));

            var t2 = new JsonStatTable(path);
            await t2.LoadAsync();
            Assert.Equal(999, t2.GetValue("상의", "에픽", MainStat.Strength));
        }
        finally { try { File.Delete(path); } catch { /* best effort */ } }
    }

    [Fact]
    public void Seed_RareRowsExistForAllSlots_AsZeroPlaceholders()
    {
        var t = Loaded();
        foreach (var slot in t.Slots)
        {
            Assert.Contains("레어", t.RaritiesFor(slot));
            Assert.Equal(0, t.GetValue(slot, "레어", MainStat.Strength));
        }
    }
    [Theory]
    [InlineData("WEAPON", "무기", null, null)]
    [InlineData("TITLE", "칭호", null, null)]
    [InlineData(null, "무기", "무기", "검")]
    public void EquipmentRows_WeaponAndTitleAreExcluded(
        string? slotId, string? slotName, string? itemType, string? itemTypeDetail)
        => Assert.True(EquipmentSlots.IsWeaponOrTitle(slotId, slotName, itemType, itemTypeDetail));

    [Fact]
    public void EquipmentRows_TrackedArmorIsRetained()
        => Assert.False(EquipmentSlots.IsWeaponOrTitle("TOP", "상의", "방어구", "상의"));
}
