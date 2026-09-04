using System.Collections.ObjectModel;
using DnfItemChecker.App.Mvvm;
using DnfItemChecker.App.State;
using DnfItemChecker.Core.Api;
using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Stats;
using DnfItemChecker.Core.Data;

namespace DnfItemChecker.App.ViewModels;

/// <summary>
/// Tab2 (장비): lists the selected character's 11 equipped items and compares each stored
/// equipped-slot tooltip observation against the 최상급 100% reference. A missing or stale item id remains
/// explicitly unmeasured.
/// </summary>
public sealed class EquipmentTabViewModel : ViewModelBase
{
    private readonly NeopleApiClient _api;
    private readonly IComparisonEngine _engine;
    private readonly IStatTable _table;
    private readonly IEquippedStatStore _equippedStore;
    private readonly AppState _state;
    private string? _statusMessage;
    private bool _hasMissingReferences;

    public EquipmentTabViewModel(NeopleApiClient api, IComparisonEngine engine, IStatTable table,
        IEquippedStatStore equippedStore, AppState state)
    {
        _api = api;
        _engine = engine;
        _table = table;
        _equippedStore = equippedStore;
        _state = state;

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => _state.HasSelection);
        _state.PropertyChanged += OnStateChanged;
    }

    public ObservableCollection<EquipmentRowViewModel> Items { get; } = new();

    public AsyncRelayCommand RefreshCommand { get; }

    public string Header => _state.SelectedCharacter is { } c
        ? $"{c.CharacterName} · 주능력치 {_state.MainStatKorean}"
        : "캐릭터를 먼저 선택하세요.";

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasMissingReferences
    {
        get => _hasMissingReferences;
        private set => SetProperty(ref _hasMissingReferences, value);
    }

    private async void OnStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.SelectedCharacter))
        {
            OnPropertyChanged(nameof(Header));
            RefreshCommand.RaiseCanExecuteChanged();
            await LoadAsync();
        }
        else if (e.PropertyName is nameof(AppState.MainStat))
        {
            OnPropertyChanged(nameof(Header));
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        Items.Clear();
        HasMissingReferences = false;
        OnPropertyChanged(nameof(Header));

        var character = _state.SelectedCharacter;
        if (character is null || _state.MainStat is not { } stat)
        {
            _state.ClearEquippedItems();
            StatusMessage = "캐릭터를 먼저 선택하세요.";
            return;
        }

        // A refresh invalidates the shared snapshot immediately; tab3 must never match against rows
        // that are known to belong to an older API response.
        _state.ClearEquippedItems();
        StatusMessage = "장비 불러오는 중...";
        try
        {
            var equipmentTask = _api.GetEquipmentAsync(character.ServerId, character.CharacterId);
            var observationsTask = _equippedStore.GetForCharacterAsync(character.ServerId, character.CharacterId);
            await Task.WhenAll(equipmentTask, observationsTask);
            var response = await equipmentTask;
            var observations = await observationsTask;

            // Keep only the 11 tracked armor/accessory/special-equipment rows. The API also returns
            // weapon and title rows, neither of which has a (slot × rarity) stat-table cell.
            var equipment = (response.Equipment ?? [])
                .Where(e => !EquipmentSlots.IsWeaponOrTitle(
                    e.SlotId, e.SlotName, e.ItemType, e.ItemTypeDetail))
                .ToList();
            _state.SetEquippedItems(character.ServerId, character.CharacterId, equipment);
            int missing = 0, unmeasured = 0;
            foreach (var item in equipment)
            {
                string? slot = _table.TryResolveSlot(item.SlotName, out var bySlotName)
                    ? bySlotName
                    : _table.TryResolveSlot(item.ItemTypeDetail, out var byTypeDetail)
                        ? byTypeDetail
                        : null;
                // The store has one observation per character/slot. Keep the API item-id check so an
                // observation from a previously equipped item is not reused after a gear swap.
                var observation = slot is not null
                    ? observations.FirstOrDefault(stored =>
                        string.Equals(stored.Slot, slot, StringComparison.Ordinal)
                        && string.Equals(stored.ItemId, item.ItemId, StringComparison.Ordinal))
                    : null;
                var result = _engine.CompareEquipped(item, stat, observation);
                if (result.Outcome == ComparisonOutcome.NotFound) missing++;
                if (result.Outcome == ComparisonOutcome.Unmeasured) unmeasured++;
                Items.Add(new EquipmentRowViewModel(item, result, stat));
            }

            HasMissingReferences = missing > 0;
            StatusMessage = equipment.Count == 0
                ? "장착된 장비가 없습니다."
                : missing > 0
                    ? $"{equipment.Count}개 중 {missing}개는 능력치 표에 기준값이 없습니다. ④ 능력치 표에서 확인하세요."
                    : unmeasured > 0
                        ? $"{equipment.Count}개 장비 중 {unmeasured}개는 미측정입니다. ③ 인게임 탭에서 착용 장비 등록을 시작하고 장비창의 현재 착용 아이템에 커서를 올려 등록하세요."
                        : $"{equipment.Count}개 장비 비교 완료.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"장비 불러오기 실패: {ex.Message}";
        }
    }
}
