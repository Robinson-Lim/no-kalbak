namespace DnfItemChecker.Core.Stats;

/// <summary>
/// The four primary-stat values an item provides at 등급 최상급 100% (the reference values the
/// app checks against). Off-stats are still present (DNF lists all four), so every cell is complete.
/// </summary>
public sealed record StatLine(int Strength, int Intelligence, int Vitality, int Spirit)
{
    public int Get(MainStat stat) => stat switch
    {
        MainStat.Strength => Strength,
        MainStat.Intelligence => Intelligence,
        MainStat.Vitality => Vitality,
        MainStat.Spirit => Spirit,
        _ => 0,
    };

    public StatLine With(MainStat stat, int value) => stat switch
    {
        MainStat.Strength => this with { Strength = value },
        MainStat.Intelligence => this with { Intelligence = value },
        MainStat.Vitality => this with { Vitality = value },
        MainStat.Spirit => this with { Spirit = value },
        _ => this,
    };
}
