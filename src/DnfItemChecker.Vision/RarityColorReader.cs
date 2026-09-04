using System.Drawing;
using System.IO;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;

namespace DnfItemChecker.Vision;

/// <summary>Classifies item rarity from the in-game name color (more reliable than OCR'ing the
/// colored name text).</summary>
public interface IRarityColorReader
{
    /// <summary>
    /// Sample colored glyphs inside each candidate box (input-image pixel space, supplied by the parser)
    /// in order, classifying by the first box that carries a saturated name/label color — or null when
    /// none do. The rarity-label box is the cleanest source; the item-name box is the fallback.
    /// </summary>
    string? DetectRarity(byte[] imageBytes, params OcrLine?[] candidateBoxes);
}

/// <summary>
/// GDI/System.Drawing implementation. Uses the <em>median</em> hue of the saturated glyph pixels so a
/// mixed-color prefix ("+11", upgrade tag) doesn't drag the estimate off the dominant name color.
/// </summary>
public sealed class RarityColorReader : IRarityColorReader
{
    public string? DetectRarity(byte[] imageBytes, params OcrLine?[] candidateBoxes)
    {
        if (imageBytes is null || imageBytes.Length == 0 || candidateBoxes is null)
            return null;

        using var ms = new MemoryStream(imageBytes);
        using var bmp = new Bitmap(ms);
        foreach (var box in candidateBoxes)
        {
            var rarity = Classify(bmp, box);
            if (rarity is not null) return rarity;
        }
        return null;
    }

    /// <summary>
    /// Reads rarity from a canonical fixed ROI. This path does not depend on OCR boxes and is used
    /// after <see cref="TooltipCropper"/> has normalized the tooltip around its grade anchor.
    /// </summary>
    public string? DetectRarity(byte[] imageBytes, Rectangle candidateBox)
    {
        if (imageBytes is null || imageBytes.Length == 0 || candidateBox.Width <= 0 || candidateBox.Height <= 0)
            return null;

        using var ms = new MemoryStream(imageBytes);
        using var bmp = new Bitmap(ms);
        return Classify(bmp, candidateBox);
    }

    private static string? Classify(Bitmap bmp, OcrLine? box)
    {
        if (box is null)
            return null;
        return Classify(bmp, new Rectangle(
            (int)Math.Round(box.Left), (int)Math.Round(box.Top),
            (int)Math.Round(box.Width), (int)Math.Round(box.Height)));
    }

    private static string? Classify(Bitmap bmp, Rectangle box)
    {
        if (box.Width <= 0 || box.Height <= 0) return null;

        int x0 = Math.Clamp(box.Left, 0, bmp.Width - 1);
        int y0 = Math.Clamp(box.Top, 0, bmp.Height - 1);
        int x1 = Math.Clamp(box.Right, x0 + 1, bmp.Width);
        int y1 = Math.Clamp(box.Bottom, y0 + 1, bmp.Height);

        var hues = new List<double>();
        var sats = new List<double>();
        var vals = new List<double>();
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                int mx = Math.Max(p.R, Math.Max(p.G, p.B));
                int mn = Math.Min(p.R, Math.Min(p.G, p.B));
                double sat = mx == 0 ? 0 : (double)(mx - mn) / mx;
                if (mx > 120 && sat > 0.35) // bright + saturated => a colored glyph, not bg/white
                {
                    RgbToHsv(p.R, p.G, p.B, out double h, out double s, out double v);
                    hues.Add(h); sats.Add(s); vals.Add(v);
                }
            }
        }
        if (hues.Count == 0) return null;

        return RarityPalette.Classify(Median(hues), Median(sats), Median(vals));
    }

    private static double Median(List<double> xs)
    {
        xs.Sort();
        int n = xs.Count;
        return n % 2 == 1 ? xs[n / 2] : (xs[n / 2 - 1] + xs[n / 2]) / 2.0;
    }

    private static void RgbToHsv(double r, double g, double b, out double hue, out double sat, out double val)
    {
        r /= 255; g /= 255; b /= 255;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double d = max - min;
        val = max;
        sat = max == 0 ? 0 : d / max;
        if (d == 0) { hue = 0; return; }

        double h;
        if (max == r) h = ((g - b) / d) % 6;
        else if (max == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        hue = h;
    }
}
