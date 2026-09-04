namespace DnfItemChecker.Core.Data;

/// <summary>A character the user has added to their local roster (grouped by adventure).</summary>
public sealed record RosterCharacter(
    string ServerId,
    string CharacterId,
    string CharacterName,
    int Level,
    string JobId,
    string JobGrowId,
    string JobName,
    string JobGrowName,
    long? Fame,
    string? AdventureName);

/// <summary>
/// Persists the user's characters locally. Tab1 builds an adventure roster here because
/// the Neople API cannot enumerate an adventure's characters directly.
/// </summary>
public interface IRosterStore
{
    Task<IReadOnlyList<RosterCharacter>> GetAllAsync(CancellationToken ct = default);
    Task UpsertAsync(RosterCharacter character, CancellationToken ct = default);
    Task RemoveAsync(string serverId, string characterId, CancellationToken ct = default);
}
