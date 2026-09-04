using DnfItemChecker.Core.Models;

namespace DnfItemChecker.Core.Stats;

/// <summary>
/// Resolves a character's primary stat. The authoritative path is
/// <see cref="ResolveFromStatus"/> (argmax over the four primary stats in the
/// /status block). <see cref="Resolve"/> is only a curated fallback for when the
/// status block is unavailable.
/// </summary>
public sealed class JobMainStatResolver : IMainStatResolver
{
    // Intentionally small, documented advancement -> stat fallback table.
    //
    // Only advancement (전직) names whose primary stat is unambiguous *regardless
    // of gender* are listed. Names shared across genders with a different primary
    // stat (e.g. 격투가's 스트라이커/넨마스터, the 크루세이더 buffers where 남→정신력
    // but 여→지능) are deliberately omitted so we return null and let the
    // status-argmax path decide. The 眞/그림자/기간틱 awakening prefixes are handled
    // implicitly via substring matching, so no explicit stripping is needed.
    private static readonly (string Keyword, MainStat Stat)[] Table =
    {
        // 지능 — 마법사(남/여) 계열 딜러 + 프리스트(여) 버퍼 (모두 지능 스케일)
        ("엘레멘탈마스터", MainStat.Intelligence),
        ("엘레멘탈바머", MainStat.Intelligence),
        ("디멘션워커", MainStat.Intelligence),
        ("소환사", MainStat.Intelligence),
        ("배틀메이지", MainStat.Intelligence),
        ("마도학자", MainStat.Intelligence),
        ("빙결사", MainStat.Intelligence),
        ("블러드 메이지", MainStat.Intelligence),
        ("스위프트 마스터", MainStat.Intelligence),
        ("소드마스터", MainStat.Intelligence),
        ("인챈트리스", MainStat.Intelligence),
        ("뮤즈", MainStat.Intelligence),
        // 힘 — 귀검사(남) 계열 + 귀검사(여) 물리 딜러
        ("웨펀마스터", MainStat.Strength),
        ("소울브링어", MainStat.Strength),
        ("버서커", MainStat.Strength),
        ("아수라", MainStat.Strength),
        ("검귀", MainStat.Strength),
        ("데몬슬레이어", MainStat.Strength),
        ("베가본드", MainStat.Strength),
        ("다크템플러", MainStat.Strength),
    };

    /// <inheritdoc/>
    public MainStat? Resolve(string? jobGrowId, string? jobGrowName)
    {
        if (string.IsNullOrWhiteSpace(jobGrowName)) return null;
        foreach (var (keyword, stat) in Table)
            if (jobGrowName.Contains(keyword, StringComparison.Ordinal))
                return stat;
        return null;
    }

    /// <inheritdoc/>
    public MainStat? ResolveFromStatus(IReadOnlyList<DfStat> status)
    {
        if (status is null || status.Count == 0) return null;

        MainStat? best = null;
        int bestValue = int.MinValue;
        foreach (var s in status)
        {
            var stat = MainStatNames.FromKorean(s.Name);
            if (stat is null) continue;
            if (!TryParseInt(s.Value, out int v)) continue;
            if (v > bestValue) { bestValue = v; best = stat; }
        }
        return best;
    }

    /// <summary>Parses the first contiguous digit run in <paramref name="text"/>.</summary>
    private static bool TryParseInt(string? text, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text)) return false;
        int i = 0;
        while (i < text.Length && !char.IsDigit(text[i])) i++;
        if (i >= text.Length) return false;
        int start = i;
        while (i < text.Length && char.IsDigit(text[i])) i++;
        return int.TryParse(text.AsSpan(start, i - start), out value);
    }
}
