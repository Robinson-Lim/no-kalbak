using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Comparison;

public enum ComparisonOutcome
{
    /// <summary>Observed main stat equals the 최상급 100% reference.</summary>
    Match,
    /// <summary>Observed value is below the 100% reference.</summary>
    Below,
    /// <summary>Slot/rarity could not be resolved, or the table has no value for them.</summary>
    NotFound,
    /// <summary>Reference known but the table cell is unfilled or the value cannot be compared.</summary>
    Indeterminate,
    /// <summary>No verified observation exists for the current API item id.</summary>
    Unmeasured,
}

public sealed record ComparisonResult(
    string? Slot,
    string? Rarity,
    MainStat? Stat,
    int? ObservedValue,
    int? ReferenceValue,
    ComparisonOutcome Outcome,
    string? Note);

/// <summary>
/// Compares tab3's inspected tooltip grade and tab2's stored equipped-slot tooltip observation.
/// Tab3 remains grade-based; tab2 requires a current API item id and compares the stored main-stat
/// value against the 100% table reference.
/// </summary>
public interface IComparisonEngine
{
    /// <summary>
    /// Tab2: equipped item. A verified observation is required; its API item id must match the current
    /// item before an equality verdict is emitted.
    /// </summary>
    ComparisonResult CompareEquipped(DfEquippedItem item, MainStat stat,
        EquippedStatObservation? observation = null);

    /// <summary>Tab3: in-game tooltip. The 100% verdict comes from <paramref name="gradeTier"/> +
    /// <paramref name="gradePercent"/> (최상급 100% ⇒ 극옵), independent of class. Slot/rarity + the
    /// optional <paramref name="stat"/> only drive the supplementary observed/reference display.</summary>
    ComparisonResult Compare(string? slot, string? rarity, string? gradeTier, int? gradePercent,
        int? observedValue, MainStat? stat);
}
