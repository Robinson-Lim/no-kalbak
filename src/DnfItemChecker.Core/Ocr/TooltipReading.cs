namespace DnfItemChecker.Core.Ocr;

/// <summary>
/// Structured result of parsing an OCR'd item tooltip. Numbers OCR reliably; the colored item name
/// does not, so rarity is taken from the name color (sample <see cref="NameBox"/>) and the name text
/// itself is best-effort.
/// </summary>
public sealed record TooltipReading(
    IReadOnlyList<string> RawLines,
    string? ItemName,
    int? Reinforce,
    string? GradeTier,                                  // 최상급/상급/중급/하급
    int? GradePercent,                                  // e.g. 94
    IReadOnlyDictionary<string, int> MainStatValues,    // 힘/지능/체력/정신력 -> base value (pre-enchant)
    string? Slot,                                       // 부위 (canonical) or null
    string? RarityLabel,                                // OCR'd rarity word (에픽/태초/…) or null
    OcrLine? NameBox,                                   // item-name line bbox for color sampling, or null
    OcrLine? GradeBox = null,                           // grade-line bbox; the two-pass crop anchor, or null
    OcrLine? RarityBox = null,                          // rarity-label bbox (clean grade-tier color), or null
    int? BareMainStat = null);                          // main-stat value whose keyword OCR dropped ("271 +113")

public interface ITooltipParser
{
    TooltipReading Parse(IReadOnlyList<OcrLine> lines);
    IReadOnlyList<TooltipReading> ParseAll(IReadOnlyList<OcrLine> lines);
}
