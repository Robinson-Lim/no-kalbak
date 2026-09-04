using DnfItemChecker.Core.Data;

namespace DnfItemChecker.Core.Tests;

public sealed class SqliteRosterStoreTests : IDisposable
{
    private readonly string _dbPath;

    public SqliteRosterStoreTests()
        => _dbPath = Path.GetTempFileName() + ".db";

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static RosterCharacter Sample(
        string serverId = "cain",
        string characterId = "char-001",
        string name = "검신",
        long? fame = 50000,
        string? adventure = "모험단A")
        => new(
            ServerId: serverId,
            CharacterId: characterId,
            CharacterName: name,
            Level: 110,
            JobId: "job-1",
            JobGrowId: "grow-1",
            JobName: "귀검사",
            JobGrowName: "검귀",
            Fame: fame,
            AdventureName: adventure);

    [Fact]
    public async Task Upsert_GetAll_Remove_RoundTrips()
    {
        var store = new SqliteRosterStore(_dbPath);
        var a = Sample(characterId: "c1", name: "검신");
        var b = Sample(characterId: "c2", name: "마도", fame: null, adventure: null);

        await store.UpsertAsync(a);
        await store.UpsertAsync(b);

        var all = await store.GetAllAsync();
        Assert.Equal(2, all.Count);

        var loadedA = all.Single(c => c.CharacterId == "c1");
        Assert.Equal("검신", loadedA.CharacterName);
        Assert.Equal(50000, loadedA.Fame);
        Assert.Equal("모험단A", loadedA.AdventureName);

        var loadedB = all.Single(c => c.CharacterId == "c2");
        Assert.Null(loadedB.Fame);
        Assert.Null(loadedB.AdventureName);

        await store.RemoveAsync(a.ServerId, a.CharacterId);
        var remaining = await store.GetAllAsync();
        Assert.Single(remaining);
        Assert.Equal("c2", remaining[0].CharacterId);
    }

    [Fact]
    public async Task Upsert_With_Same_Key_Replaces()
    {
        var store = new SqliteRosterStore(_dbPath);
        await store.UpsertAsync(Sample(characterId: "c1", name: "구이름"));
        await store.UpsertAsync(Sample(characterId: "c1", name: "새이름"));

        var all = await store.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("새이름", all[0].CharacterName);
    }
}
