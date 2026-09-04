using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Data;

/// <summary>
/// A user-verified main-stat observation from the single tooltip shown while hovering an equipped gear
/// slot. The API item id is part of the identity so a later equipment replacement cannot reuse an old value.
/// </summary>
public sealed record EquippedStatObservation(
    string ServerId,
    string CharacterId,
    string Slot,
    string ItemId,
    string ItemName,
    string Rarity,
    MainStat Stat,
    int ObservedValue,
    string? GradeTier,
    int? QualityPercent,
    DateTimeOffset CapturedAtUtc,
    string Source);

/// <summary>Persists verified equipped-slot tooltip observations for the selected character.</summary>
public interface IEquippedStatStore
{
    Task<IReadOnlyList<EquippedStatObservation>> GetForCharacterAsync(
        string serverId, string characterId, CancellationToken ct = default);

    Task UpsertAsync(EquippedStatObservation observation, CancellationToken ct = default);
}
