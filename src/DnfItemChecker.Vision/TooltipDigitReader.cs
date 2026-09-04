using System.Text.RegularExpressions;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Vision;

/// <summary>
/// Extracts numeric and name fields only from their canonical ROIs. The OCR engine remains an input
/// sensor, while domain bounds and finite stat names reject values from enchantment, gold, and set rows.
/// </summary>
public static partial class TooltipDigitReader
{
    private const int MainStatMin = 40;
    private const int MainStatMax = 320;

    public static int? ReadQuality(IReadOnlyList<OcrLine> normalizedLines, string? tier = null)
    {
        var roi = TooltipFieldLayout.Get(TooltipFieldKind.Grade);
        foreach (var line in normalizedLines.Where(line => Intersects(line, roi)))
        {
            var match = PercentRegex().Match(NormalizeDigits(line.Text));
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int value) || value > 100)
                continue;
            if (value < 10 && tier is not null && TierContains(tier, value * 10))
                return value * 10;
            return value;
        }
        return null;
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

    public static IReadOnlyDictionary<string, int> ReadMainStats(IReadOnlyList<OcrLine> normalizedLines)
    {
        var result = new Dictionary<string, int>(4);
        var roi = TooltipFieldLayout.Get(TooltipFieldKind.MainStat);
        foreach (var line in normalizedLines.Where(line => Intersects(line, roi)))
        {
            string text = NormalizeDigits(line.Text);
            foreach (var stat in MainStatNames.All)
            {
                int statIndex = text.IndexOf(stat, StringComparison.Ordinal);
                if (statIndex < 0) continue;
                var match = NumberRegex().Match(text, statIndex + stat.Length);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out int value)
                    || value is < MainStatMin or > MainStatMax)
                    continue;
                result[stat] = value;
            }
        }
        return result;
    }

    public static string? ReadItemName(IReadOnlyList<OcrLine> normalizedLines)
    {
        var roi = TooltipFieldLayout.Get(TooltipFieldKind.ItemName);
        var parts = normalizedLines
            .Where(line => Intersects(line, roi))
            .OrderBy(line => line.Top)
            .Select(line => ReinforceRegex().Replace(line.Text?.Trim() ?? string.Empty, string.Empty).Trim())
            .Where(static text => text.Length > 0)
            .ToArray();
        string name = string.Concat(parts).Trim();
        return name.Length == 0 ? null : name;
    }

    private static string NormalizeDigits(string? text)
        => (text ?? string.Empty)
            .Replace('Ｏ', '0').Replace('O', '0').Replace('o', '0')
            .Replace('Ｓ', '5').Replace('S', '5').Replace('s', '5')
            .Replace('Ｂ', '8').Replace('B', '8');

    private static bool Intersects(OcrLine line, System.Drawing.Rectangle roi)
        => line.Left < roi.Right && line.Left + line.Width > roi.Left
            && line.Top < roi.Bottom && line.Top + line.Height > roi.Top;

    [GeneratedRegex(@"([0-9]{1,3})\s*%")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"[+＋\s:=：-]*([0-9]{1,4})")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^[+＋]\s*[0-9]{1,3}\s*")]
    private static partial Regex ReinforceRegex();
}
