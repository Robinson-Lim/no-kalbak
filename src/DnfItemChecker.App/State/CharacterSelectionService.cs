using DnfItemChecker.Core.Api;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.App.State;

/// <summary>
/// Resolves the per-character context that tabs 2 and 3 depend on: the final /status block and
/// the primary stat. Status-based resolution is preferred (argmax over the four stats); the
/// job-grow mapping is the fallback when /status is unavailable.
/// </summary>
public sealed class CharacterSelectionService
{
    private readonly NeopleApiClient _api;
    private readonly IMainStatResolver _resolver;

    public CharacterSelectionService(NeopleApiClient api, IMainStatResolver resolver)
    {
        _api = api;
        _resolver = resolver;
    }

    public async Task SelectAsync(AppState state, RosterCharacter character, CancellationToken ct = default)
    {
        // Resolve status + main stat first, then publish SelectedCharacter last so the tabs that
        // reload on selection (tab2/tab3) already see a populated MainStat.
        MainStat? mainStat = null;
        DfStatusResponse? status = null;
        try
        {
            status = await _api.GetStatusAsync(character.ServerId, character.CharacterId, ct);
            if (status.Status is { Count: > 0 })
                mainStat = _resolver.ResolveFromStatus(status.Status);
        }
        catch
        {
            status = null;
        }

        mainStat ??= _resolver.Resolve(character.JobGrowId, character.JobGrowName);

        state.Status = status;
        state.MainStat = mainStat;
        state.SelectedCharacter = character;
    }
}
