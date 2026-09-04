using System.Text;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;
using DnfItemChecker.Core.Text;

namespace DnfItemChecker.Core.Comparison;

/// <summary>How confidently a single equipped-tooltip reading was linked to an API equipment row.</summary>
public enum EquippedItemMatchKind
{
    ExactName,
    FuzzyName,
    SlotAndRarity,
    Slot,
}

/// <summary>
/// A current-equipment row matched to a single tooltip read by hovering the equipped gear slot. The
/// reference value is derived from the (slot × rarity) stat table, never from OCR.
/// </summary>
public sealed record EquippedItemMatch(
    DfEquippedItem Item,
    string Slot,
    string Rarity,
    int? ReferenceValue,
    double Score,
    EquippedItemMatchKind Kind);

    /// <summary>
    /// Matches a single equipped-slot tooltip against the selected character's API equipment. Names
    /// are useful evidence but are not trusted alone when OCR is ambiguous; a unique slot can identify
    /// the row because a character has one tracked item per slot.
    /// </summary>
public static class EquippedItemMatcher
{
    private const double MinimumFuzzyNameScore = 0.60;
    private const double MinimumFuzzyMargin = 0.08;

    private sealed record Candidate(
        DfEquippedItem Item,
        string Slot,
        string Rarity,
        string NormalizedName,
        int? ReferenceValue);

    /// <summary>
    /// Finds one unambiguous API row for <paramref name="reading"/>. The <paramref name="rarity"/>
    /// argument is the reconciled rarity when available; the reading label is used as fallback.
    /// </summary>
    public static EquippedItemMatch? Find(
        TooltipReading reading,
        string? rarity,
        IEnumerable<DfEquippedItem> equipped,
        IStatTable table,
        MainStat? stat = null)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(equipped);
        ArgumentNullException.ThrowIfNull(table);

        var candidates = new List<Candidate>();
        foreach (var item in equipped)
        {
            if (EquipmentSlots.IsWeaponOrTitle(item.SlotId, item.SlotName, item.ItemType, item.ItemTypeDetail))
                continue;

            string slot;
            if (!table.TryResolveSlot(item.SlotName, out slot)
                && !table.TryResolveSlot(item.ItemTypeDetail, out slot))
                continue;
            if (!table.TryResolveRarity(item.ItemRarity, out var itemRarity))
                continue;

            candidates.Add(new Candidate(
                item,
                slot,
                itemRarity,
                NormalizeItemName(item.ItemName),
                stat is { } selectedStat ? table.GetValue(slot, itemRarity, selectedStat) : null));
        }

        if (candidates.Count == 0)
            return null;

        string? readSlot = table.TryResolveSlot(reading.Slot, out var resolvedSlot)
            ? resolvedSlot : null;
        string? readRarity = table.TryResolveRarity(rarity ?? reading.RarityLabel, out var resolvedRarity)
            ? resolvedRarity : null;
        string normalizedReadName = NormalizeItemName(reading.ItemName);

        // Exact names after removing reinforcement and upgrade prefixes are authoritative unless the
        // API response contains genuinely indistinguishable duplicate rows.
        if (normalizedReadName.Length > 0)
        {
            var exact = candidates
                .Where(c => c.NormalizedName.Length > 0
                    && string.Equals(c.NormalizedName, normalizedReadName, StringComparison.Ordinal))
                .ToList();
            if (TryChooseUnique(exact, readSlot, readRarity, out var exactMatch))
                return ToMatch(exactMatch!, 1.0, EquippedItemMatchKind.ExactName);

            // Restrict fuzzy comparison to metadata-supported rows first. If OCR metadata is wrong,
            // the global fallback below still gets a chance, but only with a strong score and margin.
            var narrowed = Narrow(candidates, readSlot, readRarity);
            if (TryChooseFuzzy(narrowed, normalizedReadName, readSlot, readRarity, out var fuzzyMatch))
                return fuzzyMatch;
            if (TryChooseFuzzy(candidates, normalizedReadName, readSlot, readRarity, out fuzzyMatch))
                return fuzzyMatch;
        }

        // The single equipped-slot tooltip has the same slot as the equipped API row. With one row in
        // that slot, the table supplies canonical name, rarity, and the 100% reference even if OCR
        // dropped the colored name and rarity label.
        var byMetadata = Narrow(candidates, readSlot, readRarity);
        if (byMetadata.Count == 1 && readSlot is not null)
        {
            var kind = readRarity is null
                ? EquippedItemMatchKind.Slot
                : EquippedItemMatchKind.SlotAndRarity;
            return ToMatch(byMetadata[0], readRarity is null ? 0.75 : 0.85, kind);
        }

        if (readSlot is not null)
        {
            var bySlot = candidates.Where(c => string.Equals(c.Slot, readSlot, StringComparison.Ordinal)).ToList();
            if (bySlot.Count == 1)
                return ToMatch(bySlot[0], 0.70, EquippedItemMatchKind.Slot);
        }

        return null;

        EquippedItemMatch ToMatch(Candidate candidate, double score, EquippedItemMatchKind kind)
            => new(candidate.Item, candidate.Slot, candidate.Rarity, candidate.ReferenceValue, score, kind);
    }

    /// <summary>
    /// Canonicalizes names for matching. DNF's <c>성축 :</c> and <c>잠식 :</c> upgrades preserve the
    /// base item's slot/rarity/stat values, so either prefix is deliberately ignored. The OCR-reversed
    /// forms <c>축성 :</c> and <c>장식 :</c> are accepted for the same reason.
    /// </summary>
    public static string NormalizeItemName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var value = raw.Trim();
        while (TryStripReinforce(ref value) || TryStripUpgradePrefix(ref value))
            value = value.TrimStart();

        var normalized = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c)) continue;
            normalized.Append(c == '：' ? ':' : c);
        }
        return normalized.ToString();
    }

    private static bool TryStripReinforce(ref string value)
    {
        if (value.Length < 2 || (value[0] != '+' && value[0] != '＋')) return false;
        int i = 1;
        while (i < value.Length && char.IsDigit(value[i])) i++;
        if (i == 1) return false;
        value = value[i..];
        return true;
    }

    private static readonly string[] UpgradePrefixes = { "성축", "잠식", "축성", "장식" };

    private static bool TryStripUpgradePrefix(ref string value)
    {
        foreach (var prefix in UpgradePrefixes)
        {
            if (!value.StartsWith(prefix, StringComparison.Ordinal)) continue;
            int i = prefix.Length;
            while (i < value.Length && char.IsWhiteSpace(value[i])) i++;
            if (i >= value.Length || (value[i] != ':' && value[i] != '：')) continue;
            value = value[(i + 1)..];
            return true;
        }
        return false;
    }
    private static List<Candidate> Narrow(
        IReadOnlyList<Candidate> candidates, string? slot, string? rarity)
    {
        var result = candidates.ToList();
        if (slot is not null)
            result = result.Where(c => string.Equals(c.Slot, slot, StringComparison.Ordinal)).ToList();
        if (rarity is not null)
            result = result.Where(c => string.Equals(c.Rarity, rarity, StringComparison.Ordinal)).ToList();
        return result;
    }

    private static bool TryChooseUnique(
        IReadOnlyList<Candidate> candidates,
        string? slot,
        string? rarity,
        out Candidate? chosen)
    {
        chosen = null;
        if (candidates.Count == 0) return false;
        if (candidates.Count == 1) { chosen = candidates[0]; return true; }

        var ranked = candidates
            .Select(c => (Candidate: c,
                Score: (slot is not null && c.Slot == slot ? 1 : 0)
                     + (rarity is not null && c.Rarity == rarity ? 1 : 0)))
            .OrderByDescending(x => x.Score)
            .ToList();
        if (ranked.Count > 1 && ranked[0].Score == ranked[1].Score) return false;
        chosen = ranked[0].Candidate;
        return true;
    }

    private static bool TryChooseFuzzy(
        IReadOnlyList<Candidate> candidates,
        string query,
        string? slot,
        string? rarity,
        out EquippedItemMatch? match)
    {
        match = null;
        var scored = candidates
            .Where(c => c.NormalizedName.Length > 0)
            .Select(c => (Candidate: c, NameScore: FuzzyMatcher.Similarity(query, c.NormalizedName)))
            .OrderByDescending(x => x.NameScore)
            .ToList();
        if (scored.Count == 0 || scored[0].NameScore < MinimumFuzzyNameScore) return false;
        if (scored.Count > 1 && scored[0].NameScore - scored[1].NameScore < MinimumFuzzyMargin)
            return false;

        var best = scored[0].Candidate;
        double score = scored[0].NameScore
            + (slot is not null && best.Slot == slot ? 0.05 : 0)
            + (rarity is not null && best.Rarity == rarity ? 0.05 : 0);
        match = new EquippedItemMatch(
            best.Item, best.Slot, best.Rarity, best.ReferenceValue, score, EquippedItemMatchKind.FuzzyName);
        return true;
    }
}
