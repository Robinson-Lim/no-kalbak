using System.Collections.ObjectModel;
using DnfItemChecker.App.Mvvm;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.App.ViewModels;

/// <summary>
/// Tab4 (능력치 표): browse + edit the (부위 × 레어리티) → 최상급 100% main-stat table the comparisons
/// reference. Replaces the old item-catalog browser/crawler.
/// </summary>
public sealed class StatTableTabViewModel : ViewModelBase
{
    private readonly IStatTable _table;
    private string? _statusMessage;

    public StatTableTabViewModel(IStatTable table)
    {
        _table = table;
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        ReloadCommand = new RelayCommand(Load);
    }

    public ObservableCollection<StatTableRowViewModel> Rows { get; } = new();

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    /// <summary>Rebuild the editable rows from the loaded table (startup + 되돌리기).</summary>
    public void Load()
    {
        Rows.Clear();
        foreach (var slot in _table.Slots)
            foreach (var rarity in _table.RaritiesFor(slot))
                if (_table.Get(slot, rarity) is { } line)
                    Rows.Add(new StatTableRowViewModel(slot, rarity, line));
        StatusMessage = $"{Rows.Count}개 항목 — 최상급 100% 기준 (직접 수정 후 저장).";
    }

    private async Task SaveAsync()
    {
        try
        {
            foreach (var row in Rows)
                await _table.SetAsync(row.Slot, row.Rarity, row.ToLine());
            StatusMessage = "저장되었습니다.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"저장 실패: {ex.Message}";
        }
    }
}
