using DnfItemChecker.App.Mvvm;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.App.State;

/// <summary>
/// Cross-tab shared state. Tab1 sets the selected character (plus its resolved main stat
/// and fetched status); tabs 2 and 3 observe it. A single instance is injected into every tab.
/// </summary>
public sealed class AppState : ViewModelBase
{
    private RosterCharacter? _selectedCharacter;
    private MainStat? _mainStat;
    private DfStatusResponse? _status;
    private IReadOnlyList<DfEquippedItem> _equippedItems = Array.Empty<DfEquippedItem>();
    private string? _equippedItemsKey;

    public RosterCharacter? SelectedCharacter
    {
        get => _selectedCharacter;
        set
        {
            if (SetProperty(ref _selectedCharacter, value))
            {
                ClearEquippedItems();
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public MainStat? MainStat
    {
        get => _mainStat;
        set
        {
            if (SetProperty(ref _mainStat, value))
                OnPropertyChanged(nameof(MainStatKorean));
        }
    }

    public DfStatusResponse? Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    /// <summary>Latest equipment rows loaded by tab2 for the selected character.</summary>
    public IReadOnlyList<DfEquippedItem> EquippedItems => _equippedItems;

    /// <summary>Character key for <see cref="EquippedItems"/>, or null when not loaded.</summary>
    public string? EquippedItemsKey => _equippedItemsKey;

    public bool HasSelection => _selectedCharacter is not null;

    public string MainStatKorean => _mainStat?.ToKorean() ?? "-";

    /// <summary>
    /// Returns the tab2 snapshot only when it belongs to the requested character. An empty list is a
    /// valid loaded snapshot, so callers do not accidentally refetch it on every live recognition.
    /// </summary>
    public bool TryGetEquippedItems(
        string serverId, string characterId, out IReadOnlyList<DfEquippedItem> items)
    {
        if (string.Equals(_equippedItemsKey, Key(serverId, characterId), StringComparison.Ordinal))
        {
            items = _equippedItems;
            return true;
        }

        items = Array.Empty<DfEquippedItem>();
        return false;
    }

    public void SetEquippedItems(
        string serverId, string characterId, IReadOnlyList<DfEquippedItem> items)
    {
        _equippedItems = items.ToList();
        _equippedItemsKey = Key(serverId, characterId);
        OnPropertyChanged(nameof(EquippedItems));
        OnPropertyChanged(nameof(EquippedItemsKey));
    }

    public void ClearEquippedItems()
    {
        if (_equippedItemsKey is null && _equippedItems.Count == 0) return;
        _equippedItems = Array.Empty<DfEquippedItem>();
        _equippedItemsKey = null;
        OnPropertyChanged(nameof(EquippedItems));
        OnPropertyChanged(nameof(EquippedItemsKey));
    }

    private static string Key(string serverId, string characterId) => $"{serverId}/{characterId}";

    /// <summary>Raised when a tab requests the shell to switch to the given tab index.</summary>
    public event Action<int>? NavigationRequested;

    public void RequestNavigation(int tabIndex) => NavigationRequested?.Invoke(tabIndex);
}
