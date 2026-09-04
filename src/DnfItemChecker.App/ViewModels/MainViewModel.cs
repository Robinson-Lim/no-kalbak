using DnfItemChecker.App.Mvvm;
using DnfItemChecker.App.State;

namespace DnfItemChecker.App.ViewModels;

/// <summary>Shell view model: hosts the four tab view models and owns the selected-tab index.</summary>
public sealed class MainViewModel : ViewModelBase
{
    private readonly AppState _state;
    private int _selectedTabIndex;

    // Tab order in MainWindow.xaml: 캐릭터(0) · 장비(1) · 인게임 인식(2) · 능력치 표(3).
    private const int InGameTabIndex = 2;

    public MainViewModel(
        AppState state,
        CharacterTabViewModel characterTab,
        EquipmentTabViewModel equipmentTab,
        InGameTabViewModel inGameTab,
        StatTableTabViewModel statTableTab)
    {
        _state = state;
        CharacterTab = characterTab;
        EquipmentTab = equipmentTab;
        InGameTab = inGameTab;
        StatTableTab = statTableTab;

        // Tab1 selecting a character requests a jump to the 장비 tab.
        _state.NavigationRequested += index => SelectedTabIndex = index;
    }

    public CharacterTabViewModel CharacterTab { get; }
    public EquipmentTabViewModel EquipmentTab { get; }
    public InGameTabViewModel InGameTab { get; }
    public StatTableTabViewModel StatTableTab { get; }

    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (SetProperty(ref _selectedTabIndex, value))
                InGameTab.SetTabActive(value == InGameTabIndex);
        }
    }

    /// <summary>Forwards shell-window activation so the live watcher re-arms the same game hover.</summary>
    public void SetWindowActive(bool active) => InGameTab.SetWindowActive(active);

    /// <summary>One-time startup load: roster (tab1) and the stat-table rows (tab4).</summary>
    public async Task InitializeAsync()
    {
        StatTableTab.Load();
        await CharacterTab.LoadRosterAsync();
    }
}
