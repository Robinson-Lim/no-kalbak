using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Comparison;

/// <summary>
/// Compares a main-stat value against the (부위 × 레어리티) 최상급 100% reference in
/// <see cref="IStatTable"/>. Tab2 requires a stored equipped-slot tooltip observation whose API item
/// id matches the current equipment item; tab3 remains grade-based for the inspected tooltip.
/// A reference of 0 means the cell is unfilled (e.g. a 레어 placeholder) → Indeterminate.
/// </summary>
public sealed class ComparisonEngine : IComparisonEngine
{
    private const string TopTier = "최상급";
    private const string Unfilled = "기준값이 입력되지 않았습니다 — ④ 능력치 표에서 입력하세요.";
    private readonly IStatTable _table;

    public ComparisonEngine(IStatTable table) => _table = table;

    public ComparisonResult CompareEquipped(DfEquippedItem item, MainStat stat,
        EquippedStatObservation? observation = null)
    {
        string? slot = _table.TryResolveSlot(item.SlotName, out var bySlotName)
            ? bySlotName
            : _table.TryResolveSlot(item.ItemTypeDetail, out var byTypeDetail)
                ? byTypeDetail
                : null;
        string? rarity = _table.TryResolveRarity(item.ItemRarity, out var r) ? r : null;
        int? reference = slot is null || rarity is null ? null : _table.GetValue(slot, rarity, stat);

        if (reference is null)
            return new ComparisonResult(slot, rarity, stat, null, null, ComparisonOutcome.NotFound,
                $"표에 {item.ItemTypeDetail}/{item.ItemRarity} 기준값이 없습니다.");

        if (reference == 0)
            return new ComparisonResult(slot, rarity, stat, null, null, ComparisonOutcome.Indeterminate,
                Unfilled);

        bool observationMatches = observation is not null
            && string.Equals(observation.ItemId, item.ItemId, StringComparison.Ordinal)
            && string.Equals(observation.Slot, slot, StringComparison.Ordinal)
            && string.Equals(observation.Rarity, rarity, StringComparison.Ordinal)
            && observation.Stat == stat
            && observation.ObservedValue > 0;
        if (!observationMatches)
            return new ComparisonResult(slot, rarity, stat, null, reference, ComparisonOutcome.Unmeasured,
                "미측정/재인식 필요");

        int observed = observation!.ObservedValue;
        if (observed == reference)
            return new ComparisonResult(slot, rarity, stat, observed, reference, ComparisonOutcome.Match,
                "관측값 == 100% 기준값");

        return new ComparisonResult(slot, rarity, stat, observed, reference, ComparisonOutcome.Below,
            $"관측값 {observed} != 100% 기준값 {reference}");
    }

    public ComparisonResult Compare(string? slot, string? rarity, string? gradeTier, int? gradePercent,
        int? observedValue, MainStat? stat)
    {
        // Supplementary only: the observed value's 100% reference (needs slot + rarity + main stat).
        int? reference = slot is not null && rarity is not null && stat is { } s
            ? _table.GetValue(slot, rarity, s) : null;
        int? refDisplay = reference is > 0 ? reference : null;

        // Verdict from the grade TIER alone (등급). Class-independent: needs no main stat, no per-stat
        // cell, no reading of the tiny stat digits — and not even the slot/rarity, which only enrich the
        // display. 최상급 = top tier = 극옵. Quality % is NOT gated on: its 2nd digit is routinely lost
        // by OCR ("60%"→"6%") and within 최상급 the stat is already ~97-98% of max, so the tier is the
        // reliable signal. So a 최상급 item still verdicts 극옵 even when the slot label couldn't be read.
        if (gradeTier is not null)
        {
            if (string.Equals(gradeTier, TopTier, StringComparison.Ordinal))
                return new ComparisonResult(slot, rarity, stat, observedValue, refDisplay,
                    ComparisonOutcome.Match, "최상급 — 극옵 충족");
            return new ComparisonResult(slot, rarity, stat, observedValue, refDisplay,
                ComparisonOutcome.Below, $"등급 '{gradeTier}' — 최상급 미만");
        }

        // No grade tier → fall back to observed value vs reference when both are present.
        if (reference is > 0 && observedValue is int ov)
            return ov >= reference
                ? new ComparisonResult(slot, rarity, stat, observedValue, refDisplay,
                    ComparisonOutcome.Match, "기준값 이상 — 100% 충족")
                : new ComparisonResult(slot, rarity, stat, observedValue, refDisplay,
                    ComparisonOutcome.Below, null);

        // Neither grade nor a usable value: distinguish "nothing identified" from "grade unreadable".
        if (slot is null || rarity is null)
            return new ComparisonResult(slot, rarity, stat, observedValue, null,
                ComparisonOutcome.NotFound, "부위/등급을 판별하지 못했습니다.");
        return new ComparisonResult(slot, rarity, stat, observedValue, refDisplay,
            ComparisonOutcome.Indeterminate, "등급을 읽지 못해 판정할 수 없습니다.");
    }
}
