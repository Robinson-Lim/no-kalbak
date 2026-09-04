using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Tests;

public sealed class SqliteEquippedStatStoreTests : IDisposable
{
    private readonly string _dbPath = Path.GetTempFileName() + ".db";

    public void Dispose()
    {
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private static EquippedStatObservation Sample(
        string characterId = "char-001", string slot = "상의", string itemId = "item-001",
        int value = 144) =>
        new("cain", characterId, slot, itemId, "테스트 상의", "에픽", MainStat.Intelligence,
            value, "최상급", 95, DateTimeOffset.UtcNow, "equipped-tooltip");

    [Fact]
    public async Task Upsert_ThenReload_RoundTripsByCharacter()
    {
        var store = new SqliteEquippedStatStore(_dbPath);
        var observation = Sample();

        await store.UpsertAsync(observation);
        var loaded = await store.GetForCharacterAsync("cain", "char-001");

        var actual = Assert.Single(loaded);
        Assert.Equal(observation, actual);
        Assert.Equal("item-001", actual.ItemId);
        Assert.Equal(144, actual.ObservedValue);
    }

    [Fact]
    public async Task Upsert_SameSlot_ReplacesPreviousObservation()
    {
        var store = new SqliteEquippedStatStore(_dbPath);
        await store.UpsertAsync(Sample(itemId: "old", value: 130));
        await store.UpsertAsync(Sample(itemId: "new", value: 144));

        var loaded = await store.GetForCharacterAsync("cain", "char-001");

        var actual = Assert.Single(loaded);
        Assert.Equal("new", actual.ItemId);
        Assert.Equal(144, actual.ObservedValue);
    }

    [Fact]
    public async Task LegacyItemKeyMigratesToCharacterSlotKeyKeepingLatest()
    {
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE equipped_stat_observations (
                    serverId TEXT NOT NULL, characterId TEXT NOT NULL, slot TEXT NOT NULL,
                    itemId TEXT NOT NULL, itemName TEXT NOT NULL, rarity TEXT NOT NULL,
                    mainStat TEXT NOT NULL, observedValue INTEGER NOT NULL, gradeTier TEXT,
                    qualityPercent INTEGER, capturedAtUtc TEXT NOT NULL, source TEXT NOT NULL,
                    PRIMARY KEY (serverId, characterId, slot, itemId)
                );
                INSERT INTO equipped_stat_observations
                    VALUES ('cain','char-001','상의','old','old','에픽','Strength',130,'최상급',90,
                            '2026-01-01T00:00:00.0000000Z','test');
                INSERT INTO equipped_stat_observations
                    VALUES ('cain','char-001','상의','new','new','에픽','Strength',144,'최상급',95,
                            '2026-01-02T00:00:00.0000000Z','test');
                """;
            command.ExecuteNonQuery();
        }

        var loaded = await new SqliteEquippedStatStore(_dbPath)
            .GetForCharacterAsync("cain", "char-001");

        var actual = Assert.Single(loaded);
        Assert.Equal("new", actual.ItemId);
        Assert.Equal(144, actual.ObservedValue);
    }

    [Fact]
    public async Task GetForCharacter_DoesNotReturnAnotherCharacter()
    {
        var store = new SqliteEquippedStatStore(_dbPath);
        await store.UpsertAsync(Sample(characterId: "char-001"));
        await store.UpsertAsync(Sample(characterId: "char-002", itemId: "other"));

        var loaded = await store.GetForCharacterAsync("cain", "char-001");

        var actual = Assert.Single(loaded);
        Assert.Equal("char-001", actual.CharacterId);
    }
}
