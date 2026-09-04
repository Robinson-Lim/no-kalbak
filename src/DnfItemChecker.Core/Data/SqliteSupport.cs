using Microsoft.Data.Sqlite;

namespace DnfItemChecker.Core.Data;

/// <summary>
/// Shared SQLite plumbing for the local caches: default DB location, connection
/// creation and one-time schema bootstrap. The same file backs the item repository,
/// roster store and verified equipped-stat observations.
/// </summary>
internal static class SqliteSupport
{
    /// <summary>%LOCALAPPDATA%/DnfItemChecker/data.db (directory created on demand).</summary>
    public static string DefaultDbPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "DnfItemChecker", "data.db");
    }

    /// <summary>Opens (creating if absent) a connection to <paramref name="dbPath"/> with the schema applied.</summary>
    public static async Task<SqliteConnection> OpenConnectionAsync(string dbPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            EnsureSchema(connection);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static void EnsureSchema(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS items (
                itemId             TEXT PRIMARY KEY,
                itemName           TEXT,
                itemRarity         TEXT,
                itemTypeDetail     TEXT,
                itemAvailableLevel INTEGER,
                setItemId          TEXT,
                setItemName        TEXT,
                json               TEXT
            );

            CREATE TABLE IF NOT EXISTS roster (
                serverId      TEXT,
                characterId   TEXT,
                characterName TEXT,
                level         INTEGER,
                jobId         TEXT,
                jobGrowId     TEXT,
                jobName       TEXT,
                jobGrowName   TEXT,
                fame          INTEGER,
                adventureName TEXT,
                PRIMARY KEY (serverId, characterId)
            );

            CREATE TABLE IF NOT EXISTS equipped_stat_observations (
                serverId       TEXT NOT NULL,
                characterId    TEXT NOT NULL,
                slot           TEXT NOT NULL,
                itemId         TEXT NOT NULL,
                itemName       TEXT NOT NULL,
                rarity         TEXT NOT NULL,
                mainStat       TEXT NOT NULL,
                observedValue  INTEGER NOT NULL,
                gradeTier      TEXT,
                qualityPercent INTEGER,
                capturedAtUtc  TEXT NOT NULL,
                source         TEXT NOT NULL,
                PRIMARY KEY (serverId, characterId, slot)
            );
            """;
        cmd.ExecuteNonQuery();

        MigrateEquippedObservationKey(connection);
    }
    private static void MigrateEquippedObservationKey(SqliteConnection connection)
    {
        var primaryKeyColumns = new List<string>();
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(equipped_stat_observations);";
            using var reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetInt32(5) > 0)
                    primaryKeyColumns.Add(reader.GetString(1));
            }
        }

        if (!primaryKeyColumns.Contains("itemId", StringComparer.Ordinal))
            return;

        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TABLE IF EXISTS equipped_stat_observations_v2;
            CREATE TABLE equipped_stat_observations_v2 (
                serverId       TEXT NOT NULL,
                characterId    TEXT NOT NULL,
                slot           TEXT NOT NULL,
                itemId         TEXT NOT NULL,
                itemName       TEXT NOT NULL,
                rarity         TEXT NOT NULL,
                mainStat       TEXT NOT NULL,
                observedValue  INTEGER NOT NULL,
                gradeTier      TEXT,
                qualityPercent INTEGER,
                capturedAtUtc  TEXT NOT NULL,
                source         TEXT NOT NULL,
                PRIMARY KEY (serverId, characterId, slot)
            );
            INSERT OR REPLACE INTO equipped_stat_observations_v2
                (serverId, characterId, slot, itemId, itemName, rarity, mainStat,
                 observedValue, gradeTier, qualityPercent, capturedAtUtc, source)
            SELECT serverId, characterId, slot, itemId, itemName, rarity, mainStat,
                   observedValue, gradeTier, qualityPercent, capturedAtUtc, source
            FROM equipped_stat_observations
            ORDER BY capturedAtUtc ASC;
            DROP TABLE equipped_stat_observations;
            ALTER TABLE equipped_stat_observations_v2 RENAME TO equipped_stat_observations;
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
    }
}
