using System.Globalization;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Data;

/// <summary>SQLite-backed store for verified equipped-slot tooltip observations.</summary>
public sealed class SqliteEquippedStatStore : IEquippedStatStore
{
    private readonly string _dbPath;

    /// <param name="dbPath">DB file path; defaults to %LOCALAPPDATA%/DnfItemChecker/data.db.</param>
    public SqliteEquippedStatStore(string? dbPath = null)
        => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? SqliteSupport.DefaultDbPath() : dbPath;

    public async Task<IReadOnlyList<EquippedStatObservation>> GetForCharacterAsync(
        string serverId, string characterId, CancellationToken ct = default)
    {
        await using var conn = await SqliteSupport.OpenConnectionAsync(_dbPath, ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT serverId, characterId, slot, itemId, itemName, rarity, mainStat,
                   observedValue, gradeTier, qualityPercent, capturedAtUtc, source
            FROM equipped_stat_observations
            WHERE serverId = $serverId AND characterId = $characterId;
            """;
        cmd.Parameters.AddWithValue("$serverId", serverId);
        cmd.Parameters.AddWithValue("$characterId", characterId);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var result = new List<EquippedStatObservation>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!Enum.TryParse<MainStat>(reader.GetString(6), ignoreCase: false, out var stat))
                continue;
            if (!DateTimeOffset.TryParse(reader.GetString(10), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var capturedAt))
                continue;

            var observation = new EquippedStatObservation(
                ServerId: reader.GetString(0),
                CharacterId: reader.GetString(1),
                Slot: reader.GetString(2),
                ItemId: reader.GetString(3),
                ItemName: reader.GetString(4),
                Rarity: reader.GetString(5),
                Stat: stat,
                ObservedValue: reader.GetInt32(7),
                GradeTier: reader.IsDBNull(8) ? null : reader.GetString(8),
                QualityPercent: reader.IsDBNull(9) ? null : reader.GetInt32(9),
                CapturedAtUtc: capturedAt,
                Source: reader.GetString(11));
            result.Add(observation);
        }
        return result;
    }

    public async Task UpsertAsync(EquippedStatObservation observation, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(observation.ServerId)
            || string.IsNullOrWhiteSpace(observation.CharacterId)
            || string.IsNullOrWhiteSpace(observation.Slot)
            || string.IsNullOrWhiteSpace(observation.ItemId)
            || string.IsNullOrWhiteSpace(observation.ItemName)
            || string.IsNullOrWhiteSpace(observation.Rarity)
            || observation.ObservedValue <= 0
            || string.IsNullOrWhiteSpace(observation.Source))
            throw new ArgumentException("A verified equipped observation is incomplete.", nameof(observation));

        await using var conn = await SqliteSupport.OpenConnectionAsync(_dbPath, ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO equipped_stat_observations
                (serverId, characterId, slot, itemId, itemName, rarity, mainStat,
                 observedValue, gradeTier, qualityPercent, capturedAtUtc, source)
            VALUES
                ($serverId, $characterId, $slot, $itemId, $itemName, $rarity, $mainStat,
                 $observedValue, $gradeTier, $qualityPercent, $capturedAtUtc, $source);
            """;
        cmd.Parameters.AddWithValue("$serverId", observation.ServerId);
        cmd.Parameters.AddWithValue("$characterId", observation.CharacterId);
        cmd.Parameters.AddWithValue("$slot", observation.Slot);
        cmd.Parameters.AddWithValue("$itemId", observation.ItemId);
        cmd.Parameters.AddWithValue("$itemName", observation.ItemName);
        cmd.Parameters.AddWithValue("$rarity", observation.Rarity);
        cmd.Parameters.AddWithValue("$mainStat", observation.Stat.ToString());
        cmd.Parameters.AddWithValue("$observedValue", observation.ObservedValue);
        cmd.Parameters.AddWithValue("$gradeTier", (object?)observation.GradeTier ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$qualityPercent", (object?)observation.QualityPercent ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$capturedAtUtc", observation.CapturedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$source", observation.Source);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
