using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using DnfItemChecker.Core.Text;

namespace DnfItemChecker.Core.Stats;

/// <summary>
/// JSON-file-backed <see cref="IStatTable"/>. Shape: <c>{ "상의": { "에픽": {Strength,…} } }</c>.
/// Seeds + writes defaults when the file is missing, and merges any seed cells that a pre-existing
/// file lacks (so new tiers like 레어 appear for existing users without losing their edits).
/// </summary>
public sealed class JsonStatTable : IStatTable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, StatLine>> _table = new(StringComparer.Ordinal);
    private bool _loaded;

    public JsonStatTable(string path) => _path = path;

    public async Task LoadAsync(CancellationToken ct = default)
    {
        if (_loaded) return;

        Dictionary<string, Dictionary<string, StatLine>>? loaded = null;
        if (File.Exists(_path))
        {
            try
            {
                await using var stream = File.OpenRead(_path);
                loaded = await JsonSerializer
                    .DeserializeAsync<Dictionary<string, Dictionary<string, StatLine>>>(stream, JsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                loaded = null;
            }
        }

        var seed = StatTableSeed.Build();
        bool dirty;
        if (loaded is null || loaded.Count == 0)
        {
            loaded = seed;
            dirty = true;
        }
        else
        {
            dirty = MergeMissing(loaded, seed); // add new seed cells (e.g. 레어) to existing files
        }

        if (dirty)
            await SaveInternalAsync(loaded, ct).ConfigureAwait(false);

        lock (_gate)
        {
            _table = loaded;
            _loaded = true;
        }
    }

    public IReadOnlyList<string> Slots => EquipmentSlots.All;

    public IReadOnlyList<string> RaritiesFor(string slot)
    {
        lock (_gate)
        {
            if (_table.TryGetValue(slot, out var byRarity))
                return RarityPalette.All.Where(byRarity.ContainsKey).ToList();
        }
        return Array.Empty<string>();
    }

    public StatLine? Get(string slot, string rarity)
    {
        lock (_gate)
            return _table.TryGetValue(slot, out var byRarity) && byRarity.TryGetValue(rarity, out var line)
                ? line : null;
    }

    public int? GetValue(string slot, string rarity, MainStat stat) => Get(slot, rarity)?.Get(stat);

    public async Task SetAsync(string slot, string rarity, StatLine line, CancellationToken ct = default)
    {
        Dictionary<string, Dictionary<string, StatLine>> snapshot;
        lock (_gate)
        {
            if (!_table.TryGetValue(slot, out var byRarity))
                _table[slot] = byRarity = new Dictionary<string, StatLine>(StringComparer.Ordinal);
            byRarity[rarity] = line;
            snapshot = Clone(_table);
        }
        await SaveInternalAsync(snapshot, ct).ConfigureAwait(false);
    }

    public bool TryResolveSlot(string? raw, out string slot) => TryResolve(raw, EquipmentSlots.All, out slot);

    public bool TryResolveRarity(string? raw, out string rarity) => TryResolve(raw, RarityPalette.All, out rarity);

    // Exact/substring first (handles API values + names that embed the slot word), then a fuzzy
    // fallback for noisy OCR. The vocabularies are tiny, so fuzzy stays reliable.
    private static bool TryResolve(string? raw, IReadOnlyList<string> vocab, out string match)
    {
        match = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        foreach (var v in vocab)
            if (raw.Contains(v, StringComparison.Ordinal)) { match = v; return true; }

        var best = FuzzyMatcher.BestMatch(raw, vocab, static x => x, 0.5);
        if (best is { } b) { match = b.Item; return true; }
        return false;
    }

    // Adds any (slot, rarity) cell present in the seed but missing from the loaded table. Never
    // overwrites a user value. Returns true when something was added.
    private static bool MergeMissing(
        Dictionary<string, Dictionary<string, StatLine>> table,
        Dictionary<string, Dictionary<string, StatLine>> seed)
    {
        bool changed = false;
        foreach (var (slot, byRarity) in seed)
        {
            if (!table.TryGetValue(slot, out var existing))
            {
                table[slot] = new Dictionary<string, StatLine>(byRarity, StringComparer.Ordinal);
                changed = true;
                continue;
            }
            foreach (var (rarity, line) in byRarity)
                if (!existing.ContainsKey(rarity)) { existing[rarity] = line; changed = true; }
        }
        return changed;
    }

    private async Task SaveInternalAsync(Dictionary<string, Dictionary<string, StatLine>> table, CancellationToken ct)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var stream = File.Create(_path);
            await JsonSerializer.SerializeAsync(stream, table, JsonOptions, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Best-effort persistence; an unwritable path must not crash the app.
        }
    }

    private static Dictionary<string, Dictionary<string, StatLine>> Clone(
        Dictionary<string, Dictionary<string, StatLine>> src)
    {
        var copy = new Dictionary<string, Dictionary<string, StatLine>>(StringComparer.Ordinal);
        foreach (var (k, v) in src) copy[k] = new Dictionary<string, StatLine>(v, StringComparer.Ordinal);
        return copy;
    }
}
