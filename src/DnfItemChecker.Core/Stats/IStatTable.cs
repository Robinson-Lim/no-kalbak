namespace DnfItemChecker.Core.Stats;

/// <summary>
/// The (부위 × 레어리티) → 최상급 100% main-stat table that replaces the item catalog. Within a
/// (slot, rarity) the 100% main-stat is fixed (verified against the API catalog), so this small
/// editable table is sufficient. Persisted as JSON next to the exe; corrected via tab4.
/// </summary>
public interface IStatTable
{
    /// <summary>Load from disk, seeding + writing defaults when the file is absent. Idempotent.</summary>
    Task LoadAsync(CancellationToken ct = default);

    /// <summary>Canonical equipment slots in display order.</summary>
    IReadOnlyList<string> Slots { get; }

    /// <summary>Rarities present for a slot (accessories include 태초), ascending order.</summary>
    IReadOnlyList<string> RaritiesFor(string slot);

    /// <summary>The 100% stat line for a slot+rarity, or null when unknown.</summary>
    StatLine? Get(string slot, string rarity);

    /// <summary>The 100% value of one main stat for a slot+rarity, or null when unknown.</summary>
    int? GetValue(string slot, string rarity, MainStat stat);

    /// <summary>Set and persist one cell (tab4 correction).</summary>
    Task SetAsync(string slot, string rarity, StatLine line, CancellationToken ct = default);

    /// <summary>Map noisy/exact text (OCR or API itemTypeDetail) to a canonical slot.</summary>
    bool TryResolveSlot(string? raw, out string slot);

    /// <summary>Map noisy/exact text (OCR label or API itemRarity) to a canonical rarity.</summary>
    bool TryResolveRarity(string? raw, out string rarity);
}
