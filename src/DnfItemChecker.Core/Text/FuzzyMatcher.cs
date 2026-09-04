using System.Text;

namespace DnfItemChecker.Core.Text;

/// <summary>
/// Lightweight string similarity used to recover canonical item names from noisy
/// OCR output (colored in-game item names OCR poorly). Pure, allocation-conscious.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>Levenshtein edit distance between two strings.</summary>
    public static int Levenshtein(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        // Two-row rolling buffer.
        Span<int> prev = b.Length < 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];
        Span<int> curr = b.Length < 256 ? stackalloc int[b.Length + 1] : new int[b.Length + 1];

        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            char ca = a[i - 1];
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = ca == b[j - 1] ? 0 : 1;
                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                curr[j] = Math.Min(Math.Min(del, ins), sub);
            }
            prev.Clear();
            curr.CopyTo(prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// Normalized similarity in [0,1]; 1 = identical. Compares on decomposed Hangul jamo so the common
    /// Korean OCR confusions that swap a single consonant/vowel (ㅋ↔ㄱ, ㅁ↔ㅇ) cost one jamo edit
    /// instead of a whole syllable — "귀컬미"≈"귀걸이". Non-Hangul text is unchanged (char-level).
    /// </summary>
    public static double Similarity(string a, string b)
    {
        string ja = ToJamo(a), jb = ToJamo(b);
        if (ja.Length == 0 && jb.Length == 0) return 1.0;
        int max = Math.Max(ja.Length, jb.Length);
        if (max == 0) return 1.0;
        return 1.0 - (double)Levenshtein(ja, jb) / max;
    }

    // Expands 가–힣 syllables into initial+medial+final Hangul jamo; passes other chars through.
    private static string ToJamo(string s)
    {
        var sb = new StringBuilder(s.Length * 3);
        foreach (char c in s)
        {
            if (c is >= '\uAC00' and <= '\uD7A3')
            {
                int code = c - 0xAC00;
                sb.Append((char)('\u1100' + code / (21 * 28)));      // 초성
                sb.Append((char)('\u1161' + code % (21 * 28) / 28)); // 중성
                int jong = code % 28;
                if (jong != 0) sb.Append((char)('\u11A7' + jong));   // 종성
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Best candidate by similarity to <paramref name="query"/>, or null when none
    /// reaches <paramref name="minScore"/>.
    /// </summary>
    public static (T Item, double Score)? BestMatch<T>(
        string query, IEnumerable<T> candidates, Func<T, string> keySelector, double minScore = 0.0)
    {
        T? best = default;
        double bestScore = -1.0;
        foreach (var c in candidates)
        {
            double s = Similarity(query, keySelector(c));
            if (s > bestScore) { bestScore = s; best = c; }
        }
        return best is not null && bestScore >= minScore ? (best, bestScore) : null;
    }
}
