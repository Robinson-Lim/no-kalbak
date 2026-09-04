using System.Text;
using System.Text.RegularExpressions;
using DnfItemChecker.Core.Stats;
using DnfItemChecker.Core.Text;

namespace DnfItemChecker.Core.Ocr;

/// <summary>
/// Parses OCR'd tooltip lines into a <see cref="TooltipReading"/>. Geometry-aware: it anchors on the
/// grade line ("최상급/상급/…(NN%)") and, when several tooltips were captured (e.g. the in-game
/// comparison tooltip), keeps only the leftmost one — the hovered/inspected item — discarding the
/// equipped item shown to its right. Stat numbers OCR reliably; the colored name does not, so rarity
/// is taken from the name color (see RarityColorReader) and the name text is best-effort.
/// </summary>
public sealed partial class TooltipParser : ITooltipParser
{
    // Inspected tooltip column right of its grade line (px). DNF item tooltips are ~300 wide; this
    // excludes the comparison partner that starts ~14px past the inspected tooltip's right edge.
    private const double ColumnRight = 300;
    private const double ColumnLeftPad = 30;
    private const double NameSpanAbove = 90;   // how far above the grade a wrapped name may reach
    private const double RarityRowSlack = 25;  // rarity label sits on the grade row

    // Slot labels are short and colored (OCR garbles them); fuzzy-match only short lines so a long
    // stat line like "마법 방어력 6151" can't be mistaken for "마법석".
    private const double SlotFuzzy = 0.5;
    // Per-token fuzzy is looser on WHERE it looks (tokens inside longer label lines), so it must be
    // stricter on WHAT it accepts: 0.65 rejects "세트"→벨트 (0.60, the 세트포인트 line bleeding into
    // the label crop) while keeping real garbles — "머리머매"→머리어깨 0.75, "팔기"→팔찌 0.80.
    private const double SlotTokenFuzzy = 0.65;
    private const int SlotFuzzyMaxLen = 6;
    private const double SlotLabelIndent = 120; // slot label sits in the right column, well right of the grade
    private const double RarityFuzzy = 0.6;

    // Garbled main-stat keyword (험→힘) tolerance. The keyword set is tiny and the value must follow,
    // so a loose jamo threshold recovers OCR slips without matching unrelated short labels.
    private const double StatKeywordFuzzy = 0.5;

    // Bounds for a keyword-less main-stat value ("271 +113"). Main stats sit in ~40–320; bounds reject
    // 명성(1000+)·세트포인트·골드 and stray small numbers.
    private const int BareStatMin = 40;
    private const int BareStatMax = 320;

    // The item name is indented just right of the grade line; reject far-right bleed lines (e.g. the
    // comparison partner's set name) when choosing the name box.
    private const double NameMaxIndent = 180;

    public TooltipReading Parse(IReadOnlyList<OcrLine> lines)
    {
        lines ??= Array.Empty<OcrLine>();
        var textLines = lines.Select(l => l.Text).ToList();
        var grade = LeftmostGrade(lines);
        return grade is null ? ParseLinear(lines, textLines) : ParseColumn(lines, grade, textLines);
    }

    /// <summary>
    /// Parses every tooltip captured in the image (one per grade line), left→right. The comparison
    /// tooltip yields two: index 0 = inspected (hovered) item, index 1 = equipped item. Falls back to
    /// a single linear reading when no grade line was recognized.
    /// </summary>
    public IReadOnlyList<TooltipReading> ParseAll(IReadOnlyList<OcrLine> lines)
    {
        lines ??= Array.Empty<OcrLine>();
        var textLines = lines.Select(l => l.Text).ToList();
        var grades = lines.Where(l => GradeRegex().IsMatch(l.Text ?? string.Empty))
                          .Select(RefineGradeAnchor)
                          .OrderBy(l => l.Left).ToList();
        if (grades.Count == 0)
            return new[] { ParseLinear(lines, textLines) };
        var result = new List<TooltipReading>(grades.Count);
        foreach (var g in grades)
            result.Add(ParseColumn(lines, g, textLines));
        return result;
    }

    private static OcrLine? LeftmostGrade(IReadOnlyList<OcrLine> lines)
    {
        OcrLine? best = null;
        foreach (var l in lines)
            if (GradeRegex().IsMatch(l.Text ?? string.Empty))
            {
                var refined = RefineGradeAnchor(l);
                if (best is null || refined.Left < best.Left) best = refined;
            }
        return best;
    }

    /// <summary>
    /// OCR sometimes merges neighbouring glyphs (an inventory icon, a stray glow) into the grade line
    /// ("十흐돈견 1최상급(911)"), dragging the line's Left ~100px left of the real tooltip border. Every
    /// downstream geometry — column bounds, crop, label column — is anchored on that Left, so shift it
    /// to the tier match's estimated position (proportional to its char index; OCR'd Hangul is
    /// near-monospace at tooltip sizes).
    /// </summary>
    private static OcrLine RefineGradeAnchor(OcrLine line)
    {
        var text = line.Text ?? string.Empty;
        var m = GradeRegex().Match(text);
        if (!m.Success || m.Index == 0 || text.Length == 0 || line.Width <= 0) return line;
        double charW = line.Width / text.Length;
        double shift = charW * m.Index;
        return line with { Left = line.Left + shift, Width = Math.Max(1, line.Width - shift) };
    }

    // Extracts one tooltip anchored on its grade line: isolates the column right of the grade and
    // reads name/stats/slot/rarity from it, excluding any neighbouring tooltip.
    private static TooltipReading ParseColumn(IReadOnlyList<OcrLine> lines, OcrLine grade, IReadOnlyList<string> textLines)
    {
        double colMin = grade.Left - ColumnLeftPad, colMax = grade.Left + ColumnRight, gradeTop = grade.Top;
        bool InCol(OcrLine l) => l.Left >= colMin && l.Left <= colMax;

        string gradeTier = NormalizeTier(grade.Text);
        int? gradePercent = ParsePercent(grade.Text, gradeTier);

        // Name = in-column line(s) just above the grade.
        OcrLine? nameBox = null;
        foreach (var l in lines)
            if (InCol(l) && l.Left <= grade.Left + NameMaxIndent && l.Top < gradeTop
                && (nameBox is null || l.Top > nameBox.Top))
                nameBox = l;

        string? name = null;
        int? reinforce = null;
        if (nameBox is not null)
        {
            var nameLines = lines
                .Where(l => InCol(l) && l.Left <= grade.Left + NameMaxIndent
                         && l.Top < gradeTop && l.Top >= nameBox.Top - NameSpanAbove)
                .OrderBy(l => l.Top).ToList();
            var sb = new StringBuilder();
            for (int i = 0; i < nameLines.Count; i++)
            {
                var part = nameLines[i].Text ?? string.Empty;
                if (i == 0)
                {
                    var rm = ReinforceRegex().Match(part);
                    if (rm.Success && int.TryParse(rm.Groups[1].ValueSpan, out int rf)) reinforce = rf;
                }
                sb.Append(ReinforceStripRegex().Replace(part, string.Empty).Trim());
            }
            var n = sb.ToString().Trim();
            name = n.Length == 0 ? null : n;
        }

        // Main stats: keyword + value, in-column, between the grade row and the first stat-boundary line.
        // The enchant section and attack-power effects repeat stat words whose values are not the item's
        // main stat, so stop before either boundary.
        var stats = new Dictionary<string, int>(4);
        double statEnd = double.MaxValue;
        foreach (var l in lines.Where(l => InCol(l) && l.Top >= gradeTop - 5))
            if (l.Top > gradeTop && IsStatBoundary(l.Text) && l.Top < statEnd) statEnd = l.Top;
        foreach (var l in lines.Where(l => InCol(l) && l.Top >= gradeTop - 5 && l.Top < statEnd).OrderBy(l => l.Top))
            AccumulateStats(l.Text, stats);

        // Keyword fully dropped by OCR ("힘 271 +113" → "271 +113"): recover the in-column
        // "<value> +<enchant…>" line above the first boundary as a stat-less candidate.
        int? bareMainStat = null;
        foreach (var l in lines.Where(x => InCol(x) && x.Top >= gradeTop - 5 && x.Top < statEnd).OrderBy(x => x.Top))
        {
            var bm = BareStatRegex().Match(l.Text ?? string.Empty);
            if (bm.Success && int.TryParse(bm.Groups[1].ValueSpan, out int bv) && bv is >= BareStatMin and <= BareStatMax)
            { bareMainStat = bv; break; }
        }
        var colText = lines.Where(InCol).Select(l => l.Text).ToList();
        // Slot: exact label substring on the grade row and below; the item name above the grade counts
        // only when it ENDS with a slot word (the game's "…집착 - 보조장비" naming) — a mid-name hit is
        // an accident ("발할라의 여신 발키리 수호…" contains "신발" but the item is a 팔찌). Fuzzy runs
        // ONLY on right-side label lines, excluding left-aligned stat fragments which jamo-fuzzy can
        // spuriously hit ("레미트"→"벨트").
        var belowNameText = lines.Where(l => InCol(l) && l.Top >= gradeTop - 5).Select(l => l.Text).ToList();
        var nameText = lines.Where(l => InCol(l) && l.Top < gradeTop - 5).Select(l => l.Text).ToList();
        var labelText = lines.Where(l => InCol(l) && l.Left > grade.Left + SlotLabelIndent).Select(l => l.Text).ToList();
        string? slot = ResolveVocab(belowNameText, EquipmentSlots.All, 0)
                    ?? ResolveSlotNameSuffix(nameText)
                    ?? ResolveVocab(labelText, EquipmentSlots.All, SlotFuzzy, SlotFuzzyMaxLen, SlotTokenFuzzy);

        // Rarity label sits on the grade row, right of the grade text; fall back to anywhere in the
        // column. Even when its text is garbled ("메픽"), its box still carries the clean rarity color,
        // so as a last resort take the rightmost grade-row line (the label's position).
        var rarityRowLines = lines.Where(l => InCol(l) && Math.Abs(l.Top - gradeTop) <= RarityRowSlack).ToList();
        var rarityRowText = rarityRowLines.Select(l => l.Text).ToList();
        string? rarity = ResolveVocab(rarityRowText, RarityPalette.All, RarityFuzzy)
                      ?? ResolveVocab(colText, RarityPalette.All, RarityFuzzy)
                      ?? ResolveRarityPrefix(rarityRowLines, grade);
        OcrLine? rarityBox = (rarity is null ? null
                : FindVocabBox(rarityRowLines, rarity, RarityFuzzy) ?? FindVocabBox(lines.Where(InCol), rarity, RarityFuzzy))
            ?? rarityRowLines.Where(l => l.Left > grade.Left + 20 && (l.Text?.Trim().Length ?? 99) <= 5)
                             .OrderByDescending(l => l.Left).FirstOrDefault();

        return new TooltipReading(textLines, name, reinforce, gradeTier, gradePercent, stats, slot, rarity, nameBox, grade, rarityBox, bareMainStat);
    }

    // Fallback when no grade line was recognized: scan everything, name = top-most line.
    private static TooltipReading ParseLinear(IReadOnlyList<OcrLine> lines, List<string> textLines)
    {
        int? reinforce = null;
        var stats = new Dictionary<string, int>(4);
        double statEnd = double.MaxValue;
        foreach (var l in lines)
            if (l.Top > 0 && IsStatBoundary(l.Text) && l.Top < statEnd) statEnd = l.Top;
        for (int i = 0; i < lines.Count; i++)
        {
            var text = lines[i].Text ?? string.Empty;
            if (i == 0)
            {
                var rm = ReinforceRegex().Match(text);
                if (rm.Success && int.TryParse(rm.Groups[1].ValueSpan, out int rf)) reinforce = rf;
            }
            if (lines[i].Top < statEnd) AccumulateStats(text, stats);
        }

        int? bareMainStat = null;
        foreach (var l in lines.Where(x => x.Top < statEnd).OrderBy(x => x.Top))
        {
            var bm = BareStatRegex().Match(l.Text ?? string.Empty);
            if (bm.Success && int.TryParse(bm.Groups[1].ValueSpan, out int bv) && bv is >= BareStatMin and <= BareStatMax)
            { bareMainStat = bv; break; }
        }

        OcrLine? nameBox = lines.Count > 0 ? lines[0] : null;
        string? name = null;
        if (nameBox is not null)
        {
            var s = ReinforceStripRegex().Replace(nameBox.Text ?? string.Empty, string.Empty).Trim();
            name = s.Length == 0 ? null : s;
        }
        string? slot = ResolveVocab(textLines, EquipmentSlots.All, SlotFuzzy, SlotFuzzyMaxLen);
        string? rarity = ResolveVocab(textLines, RarityPalette.All, RarityFuzzy);
        OcrLine? rarityBox = rarity is null ? null : FindVocabBox(lines, rarity, RarityFuzzy);
        return new TooltipReading(textLines, name, reinforce, null, null, stats, slot, rarity, nameBox, null, rarityBox, bareMainStat);
    }
    private static int? ParsePercent(string text, string? tier = null)
    {
        var pm = PercentRegex().Match(text);
        if (!pm.Success || !int.TryParse(pm.Groups[1].ValueSpan, out int raw)) return null;
        int normalized = raw;
        while (normalized > 100) normalized /= 10;
        if (tier is null) return normalized;
        if (TierContains(tier, normalized)) return normalized;
        if (raw is < 10 && TierContains(tier, raw * 10)) return raw * 10;
        if (raw % 10 == 1)
        {
            int trailingOneRemoved = raw / 10;
            if (TierContains(tier, trailingOneRemoved)) return trailingOneRemoved;
        }
        return normalized;
    }
    private static bool TierContains(string tier, int value) => tier switch
    {
        "최하급" => value is >= 1 and <= 20,
        "하급" => value is >= 21 and <= 40,
        "중급" => value is >= 41 and <= 59,
        "상급" => value is >= 60 and <= 80,
        "최상급" => value is >= 81 and <= 100,
        _ => false,
    };

    /// <summary>
    /// Extracts the tier from a grade line, recovering 최상급 when OCR garbled its first syllable: a
    /// Hangul char glued directly onto "상급(" ("펼상급(911)") can only be a misread 최 — the genuine
    /// tier starts the tooltip's grade text, so anything fused in front of 상급 is that first syllable.
    /// </summary>
    internal static string NormalizeTier(string gradeText)
    {
        var m = GradeRegex().Match(gradeText);
        string tier = m.Groups[1].Value;
        if (tier == "상급" && m.Index > 0)
        {
            char prev = gradeText[m.Index - 1];
            if (prev is >= '\uAC00' and <= '\uD7A3' and not '최') return "최상급";
        }
        return tier;
    }

    // Exact substring first; then a jamo-fuzzy fallback when <paramref name="minFuzzy"/> &gt; 0.
    // Two gates keep the fuzzy pass from hitting stat-line fragments:
    //  - short lines (≤ maxLenForFuzzy) match tokens at the loose <paramref name="minFuzzy"/> — a pure
    //    label line ("머리메H", 0.5 vs 머리어깨) carries no risk;
    //  - longer lines are searched per token only when <paramref name="tokenFuzzy"/> &gt; 0, at that
    //    stricter score — recovers a label glued to junk ("머리머매 우;拿(흐기" → 0.75) while rejecting
    //    body bleed ("세트 포인트…" token "세트" → 벨트 0.60 &lt; 0.65).
    private static string? ResolveVocab(
        IReadOnlyList<string> lines, IReadOnlyList<string> vocab, double minFuzzy,
        int maxLenForFuzzy = int.MaxValue, double tokenFuzzy = 0)
    {
        foreach (var line in lines)
        {
            if (line is null) continue;
            foreach (var v in vocab)
                if (line.Contains(v, StringComparison.Ordinal)) return v;
        }
        if (minFuzzy <= 0) return null;

        string? best = null;
        double bestScore = double.MaxValue;
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            bool shortLine = line.Trim().Length <= maxLenForFuzzy;
            double gate = shortLine ? minFuzzy : tokenFuzzy;
            if (gate <= 0) continue;
            foreach (var token in line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!shortLine && token.Length > maxLenForFuzzy) continue;
                var m = FuzzyMatcher.BestMatch(token, vocab, static x => x, gate);
                if (m is { } mm && (best is null || mm.Score > bestScore)) { bestScore = mm.Score; best = mm.Item; }
            }
        }
        return best;
    }

    /// <summary>
    /// Last-resort rarity: a truncated label on the grade row ("태" from a crop that clipped 태초's
    /// second glyph) that is a unique prefix of exactly one rarity word. Restricted to the label
    /// position (right of the grade text) so the tier itself ("최하급(5%)") can never match.
    /// </summary>
    private static string? ResolveRarityPrefix(IReadOnlyList<OcrLine> rarityRowLines, OcrLine grade)
    {
        foreach (var l in rarityRowLines)
        {
            if (l.Left <= grade.Left + SlotLabelIndent) continue;
            foreach (var token in (l.Text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length is 0 or > 3) continue;
                string? hit = null;
                foreach (var r in RarityPalette.All)
                    if (r.StartsWith(token, StringComparison.Ordinal))
                    {
                        if (hit is not null) { hit = null; break; }   // ambiguous prefix
                        hit = r;
                    }
                if (hit is not null && hit.Length > token.Length) return hit;
            }
        }
        return null;
    }

    // Locates the OCR line that <see cref="ResolveVocab"/> matched to <paramref name="value"/>, so the
    // colour reader can sample that exact box (e.g. the rarity label's glyphs).
    private static OcrLine? FindVocabBox(IEnumerable<OcrLine> candidates, string value, double minFuzzy)
    {
        foreach (var l in candidates)
            if (ResolveVocab(new[] { l.Text ?? string.Empty }, new[] { value }, minFuzzy) == value)
                return l;
        return null;
    }

    /// <summary>Slot from the name region: only a line-final slot word counts (see Parse).</summary>
    private static string? ResolveSlotNameSuffix(IReadOnlyList<string?> nameLines)
    {
        foreach (var raw in nameLines)
        {
            var t = raw?.TrimEnd();
            if (string.IsNullOrEmpty(t)) continue;
            foreach (var s in EquipmentSlots.All)
                if (t.EndsWith(s, StringComparison.Ordinal))
                    return s;
        }
        return null;
    }

    /// <summary>
    /// Resolves an equipment slot from raw OCR lines (exact label, then short-line fuzzy) across every
    /// tooltip present. Valid for comparison captures, where the inspected and equipped items share a
    /// slot, so a garbled inspected label can be recovered from the equipped tooltip's clearer one.
    /// </summary>
    public static string? ResolveSlot(IEnumerable<string> lines) =>
        ResolveVocab(lines as IReadOnlyList<string> ?? lines.ToList(), EquipmentSlots.All, SlotFuzzy, SlotFuzzyMaxLen);

    /// <summary>
    /// Slot resolution for the focused label-column re-OCR, whose lines are only the right-aligned
    /// 등급/교환/부위/골드 labels: short lines fuzzy at the loose gate, longer lines per token at the
    /// strict gate — a 부위 label OCR glued to stray glyphs still resolves. Never feed full-tooltip
    /// lines here — stat-line fragments would hit.
    /// </summary>
    public static string? ResolveSlotLabels(IEnumerable<string> lines) =>
        ResolveVocab(lines as IReadOnlyList<string> ?? lines.ToList(), EquipmentSlots.All, SlotFuzzy, SlotFuzzyMaxLen, SlotTokenFuzzy);

    /// <summary>Resolves a rarity word from raw OCR lines (exact, then jamo-fuzzy) — used for the
    /// focused label-column re-OCR, where lines are just the right-aligned 등급/부위/골드 labels.</summary>
    public static string? ResolveRarity(IEnumerable<string> lines) =>
        ResolveVocab(lines as IReadOnlyList<string> ?? lines.ToList(), RarityPalette.All, RarityFuzzy);

    // Records main-stat values from one OCR line. Exact keyword match first (handles multi-stat lines);
    // then a jamo-fuzzy fallback so a garbled keyword ("험 +80") still resolves to its canonical stat.
    private static void AccumulateStats(string? text, IDictionary<string, int> stats)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (Match sm in StatRegex().Matches(text))
        {
            var key = sm.Groups[1].Value;
            if (!stats.ContainsKey(key) && int.TryParse(sm.Groups[2].ValueSpan, out int sv))
                stats[key] = sv;
        }
        var fm = StatLineRegex().Match(text);
        if (fm.Success && int.TryParse(fm.Groups[2].ValueSpan, out int fv)
            && FuzzyMatcher.BestMatch(fm.Groups[1].Value, MainStatNames.All, static x => x, StatKeywordFuzzy) is { } m
            && !stats.ContainsKey(m.Item))
            stats[m.Item] = fv;
    }

    [GeneratedRegex(@"^\+(\d+)")]
    private static partial Regex ReinforceRegex();

    [GeneratedRegex(@"^\+\d+\s*")]
    private static partial Regex ReinforceStripRegex();

    // Tier + opening paren; the % digit is parsed separately because OCR often garbles its first char.
    // 최상급/최하급 before their suffixes so alternation prefers the full tier ("최하급(" ≠ 하급).
    [GeneratedRegex(@"(최상급|최하급|상급|중급|하급)\s*\(")]
    private static partial Regex GradeRegex();
    [GeneratedRegex(@"\((\d+)")]
    private static partial Regex PercentRegex();

    // The "<마법부여>" (enchant) section header. OCR garbles the brackets and 여→며, but "부여"/"부며"
    // is distinctive and appears nowhere above the main stat — it marks where main-stat reading stops.
    [GeneratedRegex(@"부[여며]")]
    private static partial Regex EnchantHeaderRegex();

    // Attack-power effects are another reliable end marker. Cropped value OCR can include later
    // stat/enchant text while clipping the explicit <마법부여> header.
    private static bool IsStatBoundary(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (EnchantHeaderRegex().IsMatch(text)) return true;
        return text.Contains("공격력", StringComparison.Ordinal)
            && (text.Contains("증가", StringComparison.Ordinal)
                || text.Contains("주가", StringComparison.Ordinal)
                || text.Contains("좋가", StringComparison.Ordinal));
    }

    // A whole line that is just "<value> +<enchant>" — the main-stat line after OCR dropped its keyword
    // (the +증폭 amount makes it specific, so plain numbers like 세트포인트 don't match).
    [GeneratedRegex(@"^\s*(\d+)(?:\s*\+\s*\d+)+\s*$")]
    private static partial Regex BareStatRegex();

    // Main stat: keyword then value. The value may carry a leading "+" (armor shows main stat as
    // "힘 +80"); the OCR'd keyword is exact here, garbled keywords are recovered by AccumulateStats.
    [GeneratedRegex(@"(?<![가-힣])(힘|지능|체력|정신력)\s*\+?\s*(\d+)")]
    private static partial Regex StatRegex();

    // Fuzzy fallback shape: a short leading Hangul token immediately followed by an optional "+" and the
    // value. The token is jamo-matched to a main-stat keyword so a garbled "험 +80" still yields 힘 80.
    [GeneratedRegex(@"^\s*([가-힣]{1,3})\s*\+?\s*(\d+)")]
    private static partial Regex StatLineRegex();
}
