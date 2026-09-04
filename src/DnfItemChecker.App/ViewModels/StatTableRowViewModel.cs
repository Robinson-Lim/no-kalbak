using DnfItemChecker.App.Mvvm;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.App.ViewModels;

/// <summary>One editable (부위 × 레어리티) row of the 최상급 100% stat table (tab4).</summary>
public sealed class StatTableRowViewModel : ViewModelBase
{
    private int _strength, _intelligence, _vitality, _spirit;

    public StatTableRowViewModel(string slot, string rarity, StatLine line)
    {
        Slot = slot;
        Rarity = rarity;
        _strength = line.Strength;
        _intelligence = line.Intelligence;
        _vitality = line.Vitality;
        _spirit = line.Spirit;
    }

    public string Slot { get; }
    public string Rarity { get; }

    public int Strength { get => _strength; set => SetProperty(ref _strength, value); }
    public int Intelligence { get => _intelligence; set => SetProperty(ref _intelligence, value); }
    public int Vitality { get => _vitality; set => SetProperty(ref _vitality, value); }
    public int Spirit { get => _spirit; set => SetProperty(ref _spirit, value); }

    public StatLine ToLine() => new(Strength, Intelligence, Vitality, Spirit);
}
