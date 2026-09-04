namespace DnfItemChecker.Core.Stats;

/// <summary>The four primary stats a DNF character can scale with.</summary>
public enum MainStat
{
    Strength,     // 힘
    Intelligence, // 지능
    Vitality,     // 체력
    Spirit,       // 정신력
}

public static class MainStatNames
{
    public const string Strength = "힘";
    public const string Intelligence = "지능";
    public const string Vitality = "체력";
    public const string Spirit = "정신력";

    /// <summary>All four primary-stat Korean names.</summary>
    public static readonly IReadOnlyList<string> All = new[] { Strength, Intelligence, Vitality, Spirit };

    public static string ToKorean(this MainStat stat) => stat switch
    {
        MainStat.Strength => Strength,
        MainStat.Intelligence => Intelligence,
        MainStat.Vitality => Vitality,
        MainStat.Spirit => Spirit,
        _ => throw new ArgumentOutOfRangeException(nameof(stat)),
    };

    public static MainStat? FromKorean(string? name) => name switch
    {
        Strength => MainStat.Strength,
        Intelligence => MainStat.Intelligence,
        Vitality => MainStat.Vitality,
        Spirit => MainStat.Spirit,
        _ => null,
    };
}
