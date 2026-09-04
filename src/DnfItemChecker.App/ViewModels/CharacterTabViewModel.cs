using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using DnfItemChecker.App.Mvvm;
using DnfItemChecker.App.State;
using DnfItemChecker.Core.Api;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Models;

namespace DnfItemChecker.App.ViewModels;

/// <summary>
/// Tab1 (캐릭터): search characters via the Neople API, add them to a local roster grouped by
/// adventure, and select a roster character to drive tabs 2 and 3.
/// </summary>
public sealed class CharacterTabViewModel : ViewModelBase
{
    private readonly NeopleApiClient _api;
    private readonly IRosterStore _roster;
    private readonly AppState _state;
    private readonly CharacterSelectionService _selection;

    private string _searchName = string.Empty;
    private DfCharacterSearchRow? _selectedSearchResult;
    private RosterCharacter? _selectedRosterCharacter;
    private string? _statusMessage;

    public CharacterTabViewModel(NeopleApiClient api, IRosterStore roster, AppState state, CharacterSelectionService selection)
    {
        _api = api;
        _roster = roster;
        _state = state;
        _selection = selection;

        RosterView = CollectionViewSource.GetDefaultView(Roster);
        RosterView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RosterCharacter.AdventureName)));
        RosterView.SortDescriptions.Add(new SortDescription(nameof(RosterCharacter.AdventureName), ListSortDirection.Ascending));
        RosterView.SortDescriptions.Add(new SortDescription(nameof(RosterCharacter.Fame), ListSortDirection.Descending));

        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !string.IsNullOrWhiteSpace(SearchName));
        AddToRosterCommand = new AsyncRelayCommand(p => AddToRosterAsync(p as DfCharacterSearchRow), p => p is DfCharacterSearchRow);
        SelectCharacterCommand = new AsyncRelayCommand(p => SelectCharacterAsync(p as RosterCharacter), p => p is RosterCharacter);
        RemoveCommand = new AsyncRelayCommand(p => RemoveAsync(p as RosterCharacter), p => p is RosterCharacter);
    }

    public ObservableCollection<DfCharacterSearchRow> SearchResults { get; } = new();
    public ObservableCollection<RosterCharacter> Roster { get; } = new();
    public ICollectionView RosterView { get; }

    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand AddToRosterCommand { get; }
    public AsyncRelayCommand SelectCharacterCommand { get; }
    public AsyncRelayCommand RemoveCommand { get; }

    public string SearchName
    {
        get => _searchName;
        set
        {
            if (SetProperty(ref _searchName, value)) SearchCommand.RaiseCanExecuteChanged();
        }
    }

    public DfCharacterSearchRow? SelectedSearchResult
    {
        get => _selectedSearchResult;
        set => SetProperty(ref _selectedSearchResult, value);
    }

    public RosterCharacter? SelectedRosterCharacter
    {
        get => _selectedRosterCharacter;
        set => SetProperty(ref _selectedRosterCharacter, value);
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Loads the persisted roster (called once at startup).</summary>
    public async Task LoadRosterAsync()
    {
        try
        {
            var all = await _roster.GetAllAsync();
            Roster.Clear();
            foreach (var c in all) Roster.Add(c);
            StatusMessage = Roster.Count == 0 ? "로스터가 비어 있습니다. 캐릭터를 검색해 추가하세요." : null;
        }
        catch (Exception ex)
        {
            StatusMessage = $"로스터 불러오기 실패: {ex.Message}";
        }
    }

    private async Task SearchAsync()
    {
        StatusMessage = "검색 중...";
        SearchResults.Clear();
        try
        {
            var rows = await _api.SearchCharactersAsync("all", SearchName.Trim(), "full", 30);
            foreach (var r in rows) SearchResults.Add(r);
            StatusMessage = rows.Count == 0 ? "검색 결과가 없습니다." : $"{rows.Count}명 검색됨.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"검색 실패: {ex.Message}";
        }
    }

    private async Task AddToRosterAsync(DfCharacterSearchRow? row)
    {
        if (row is null) return;
        StatusMessage = $"'{row.CharacterName}' 추가 중...";
        try
        {
            // adventureName is only exposed by the basic-info endpoint.
            var info = await _api.GetCharacterAsync(row.ServerId, row.CharacterId);
            var character = new RosterCharacter(
                row.ServerId, row.CharacterId, row.CharacterName, row.Level,
                row.JobId, row.JobGrowId, row.JobName, row.JobGrowName, row.Fame, info.AdventureName);

            await _roster.UpsertAsync(character);
            await LoadRosterAsync();
            StatusMessage = $"'{row.CharacterName}' 추가됨 (모험단: {info.AdventureName ?? "미상"}).";
        }
        catch (Exception ex)
        {
            StatusMessage = $"추가 실패: {ex.Message}";
        }
    }

    private async Task SelectCharacterAsync(RosterCharacter? character)
    {
        if (character is null) return;
        SelectedRosterCharacter = character;
        await _selection.SelectAsync(_state, character);
        StatusMessage = $"'{character.CharacterName}' 선택됨 — 주능력치 {_state.MainStatKorean}.";
        _state.RequestNavigation(1); // jump to 장비 tab
    }

    private async Task RemoveAsync(RosterCharacter? character)
    {
        if (character is null) return;
        try
        {
            await _roster.RemoveAsync(character.ServerId, character.CharacterId);
            await LoadRosterAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"삭제 실패: {ex.Message}";
        }
    }
}
