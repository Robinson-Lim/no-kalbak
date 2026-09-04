using DnfItemChecker.Core.Models;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Core.Tests;

public class JobMainStatResolverTests
{
    private static DfStat Stat(string name, string value) => new(name, value);

    [Fact]
    public void ResolveFromStatus_ReturnsArgmaxStat()
    {
        var resolver = new JobMainStatResolver();
        var status = new[]
        {
            Stat("힘", "100"),
            Stat("지능", "500"),
            Stat("체력", "60"),
            Stat("정신력", "40"),
        };

        Assert.Equal(MainStat.Intelligence, resolver.ResolveFromStatus(status));
    }

    [Fact]
    public void ResolveFromStatus_PicksStrengthWhenHighest()
    {
        var resolver = new JobMainStatResolver();
        var status = new[]
        {
            Stat("힘", "700"),
            Stat("지능", "120"),
            Stat("정신력", "650"),
        };

        Assert.Equal(MainStat.Strength, resolver.ResolveFromStatus(status));
    }

    [Fact]
    public void ResolveFromStatus_IgnoresNonPrimaryStats()
    {
        var resolver = new JobMainStatResolver();
        var status = new[]
        {
            Stat("물리 방어력", "9999"),
            Stat("정신력", "30"),
        };

        Assert.Equal(MainStat.Spirit, resolver.ResolveFromStatus(status));
    }

    [Fact]
    public void ResolveFromStatus_NoPrimaryStats_ReturnsNull()
    {
        var resolver = new JobMainStatResolver();
        var status = new[] { Stat("물리 방어력", "9999"), Stat("HP", "30000") };

        Assert.Null(resolver.ResolveFromStatus(status));
    }

    [Fact]
    public void ResolveFromStatus_Empty_ReturnsNull()
    {
        Assert.Null(new JobMainStatResolver().ResolveFromStatus(Array.Empty<DfStat>()));
    }

    [Theory]
    [InlineData("엘레멘탈마스터", MainStat.Intelligence)]
    [InlineData("眞 엘레멘탈마스터", MainStat.Intelligence)]
    [InlineData("웨펀마스터", MainStat.Strength)]
    [InlineData("眞 버서커", MainStat.Strength)]
    public void Resolve_KnownAdvancement_ReturnsMappedStat(string jobGrowName, MainStat expected)
    {
        Assert.Equal(expected, new JobMainStatResolver().Resolve(null, jobGrowName));
    }

    [Fact]
    public void Resolve_UnknownOrAmbiguous_ReturnsNull()
    {
        var resolver = new JobMainStatResolver();

        Assert.Null(resolver.Resolve(null, "알수없는직업"));
        Assert.Null(resolver.Resolve(null, null));
    }
}
