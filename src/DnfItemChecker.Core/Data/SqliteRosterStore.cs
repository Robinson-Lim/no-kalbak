using Microsoft.Data.Sqlite;

namespace DnfItemChecker.Core.Data;

/// <summary>
/// SQLite-backed store for the user's local roster. The Neople API cannot
/// enumerate an adventure's characters, so tab1 persists them here.
/// </summary>
public sealed class SqliteRosterStore : IRosterStore
{
    private readonly string _dbPath;

    /// <param name="dbPath">DB file path; defaults to %LOCALAPPDATA%/DnfItemChecker/data.db.</param>
    public SqliteRosterStore(string? dbPath = null)
        => _dbPath = string.IsNullOrWhiteSpace(dbPath) ? SqliteSupport.DefaultDbPath() : dbPath;

    public async Task<IReadOnlyList<RosterCharacter>> GetAllAsync(CancellationToken ct = default)
    {
        await using var conn = await SqliteSupport.OpenConnectionAsync(_dbPath, ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT serverId, characterId, characterName, level, jobId, jobGrowId,
                   jobName, jobGrowName, fame, adventureName
            FROM roster
            ORDER BY adventureName, characterName;
            """;

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var list = new List<RosterCharacter>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new RosterCharacter(
                ServerId: reader.GetString(0),
                CharacterId: reader.GetString(1),
                CharacterName: reader.GetString(2),
                Level: reader.GetInt32(3),
                JobId: reader.GetString(4),
                JobGrowId: reader.GetString(5),
                JobName: reader.GetString(6),
                JobGrowName: reader.GetString(7),
                Fame: reader.IsDBNull(8) ? null : reader.GetInt64(8),
                AdventureName: reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return list;
    }

    public async Task UpsertAsync(RosterCharacter character, CancellationToken ct = default)
    {
        await using var conn = await SqliteSupport.OpenConnectionAsync(_dbPath, ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO roster
                (serverId, characterId, characterName, level, jobId, jobGrowId,
                 jobName, jobGrowName, fame, adventureName)
            VALUES
                ($serverId, $characterId, $characterName, $level, $jobId, $jobGrowId,
                 $jobName, $jobGrowName, $fame, $adventureName);
            """;
        cmd.Parameters.AddWithValue("$serverId", character.ServerId);
        cmd.Parameters.AddWithValue("$characterId", character.CharacterId);
        cmd.Parameters.AddWithValue("$characterName", character.CharacterName);
        cmd.Parameters.AddWithValue("$level", character.Level);
        cmd.Parameters.AddWithValue("$jobId", character.JobId);
        cmd.Parameters.AddWithValue("$jobGrowId", character.JobGrowId);
        cmd.Parameters.AddWithValue("$jobName", character.JobName);
        cmd.Parameters.AddWithValue("$jobGrowName", character.JobGrowName);
        cmd.Parameters.AddWithValue("$fame", (object?)character.Fame ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$adventureName", (object?)character.AdventureName ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task RemoveAsync(string serverId, string characterId, CancellationToken ct = default)
    {
        await using var conn = await SqliteSupport.OpenConnectionAsync(_dbPath, ct).ConfigureAwait(false);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM roster WHERE serverId = $serverId AND characterId = $characterId;";
        cmd.Parameters.AddWithValue("$serverId", serverId);
        cmd.Parameters.AddWithValue("$characterId", characterId);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
