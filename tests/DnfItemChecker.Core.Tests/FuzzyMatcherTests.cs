using DnfItemChecker.Core.Text;

namespace DnfItemChecker.Core.Tests;

public class FuzzyMatcherTests
{
    [Fact]
    public void Similarity_IdenticalStrings_IsOne()
    {
        Assert.Equal(1.0, FuzzyMatcher.Similarity("황금향의 영광", "황금향의 영광"));
    }

    [Fact]
    public void Similarity_CloserString_ScoresHigher()
    {
        double near = FuzzyMatcher.Similarity("abcde", "abcdx");   // 1 edit
        double far = FuzzyMatcher.Similarity("abcde", "vwxyz");    // 5 edits

        Assert.True(near > far);
        Assert.True(near < 1.0);
    }

    [Fact]
    public void BestMatch_ReturnsClosestCandidate()
    {
        var candidates = new[] { "전혀다른이름", "찬란한 황금향의 플레미트", "다른 아이템" };

        var result = FuzzyMatcher.BestMatch("찬란한 황금향의 플레미트", candidates, x => x);

        Assert.NotNull(result);
        Assert.Equal("찬란한 황금향의 플레미트", result!.Value.Item);
        Assert.Equal(1.0, result.Value.Score);
    }

    [Fact]
    public void BestMatch_BelowMinScore_ReturnsNull()
    {
        var candidates = new[] { "abx", "zzz" };

        // Best similarity to "abc" is "abx" = 1 - 1/3 ≈ 0.667, under the 0.99 floor.
        var result = FuzzyMatcher.BestMatch("abc", candidates, x => x, minScore: 0.99);

        Assert.Null(result);
    }
}
