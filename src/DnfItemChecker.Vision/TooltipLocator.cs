using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace DnfItemChecker.Vision;

/// <summary>
/// Locates the DNF tooltip rectangle by relative border geometry instead of OCR. The game tooltip is
/// an opaque near-black box with two long, approximately parallel edge runs. Edge spacing and overlap
/// are derived from the captured image; no resolution-specific tooltip width is assumed.
/// </summary>
public static class TooltipLocator
{
    private const int EdgeClusterGap = 4;
    private const double MinDarkFraction = 0.52;
    private const double MinRunFraction = 0.10;
    private const double MaxRunGapFraction = 0.012;
    private const double MinPairGapFraction = 0.15;
    private const double MaxPairGapFraction = 0.92;
    private const double MinOverlapFraction = 0.10;

    private readonly record struct Edge(int X, int Y0, int Y1, int LastX);
    private readonly record struct Candidate(Rectangle Rect, double DarkFraction);

    /// <summary>
    /// Finds the inspected tooltip. With a cursor position (relative to the capture), the candidate
    /// adjacent to it wins. Without a cursor, the leftmost candidate wins, matching
    /// <see cref="TooltipParser"/>'s "leftmost grade = inspected" convention.
    /// </summary>
    public static Rectangle? Locate(byte[] imageBytes, (int X, int Y)? cursor = null)
    {
        if (imageBytes is null || imageBytes.Length == 0) return null;
        using var ms = new MemoryStream(imageBytes);
        using var src = new Bitmap(ms);
        return Locate(src, cursor);
    }

    /// <summary>All surviving candidates (post filters, pre selection) — diagnostics for RecogProbe.</summary>
    public static List<Rectangle> LocateAll(byte[] imageBytes)
    {
        if (imageBytes is null || imageBytes.Length == 0) return new List<Rectangle>();
        using var ms = new MemoryStream(imageBytes);
        using var src = new Bitmap(ms);
        return Candidates(src, out _, out _, out _).Select(c => c.Rect).ToList();
    }

    public static Rectangle? Locate(Bitmap src, (int X, int Y)? cursor = null)
    {
        var cands = Candidates(src, out int h, out var isDark, out var isBorder);
        if (cands.Count == 0) return null;
        return Select(cands, cursor, h, isDark, isBorder);
    }

    private static List<Candidate> Candidates(Bitmap src, out int height,
        out Func<int, int, bool> isDark, out Func<int, int, bool> isBorder)
    {
        int w = src.Width, h = src.Height;
        height = h;
        isDark = static (_, _) => false;
        isBorder = static (_, _) => false;
        if (w < 64 || h < 64) return new List<Candidate>();

        // One managed copy of the pixels. The scan then stays safe code and does not hold a GDI lock
        // while candidates are scored.
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        int stride = data.Stride;
        var px = new byte[stride * h];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        src.UnlockBits(data);

        bool IsBorder(int x, int y)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return false;
            int i = y * stride + x * 3;
            int b = px[i], g = px[i + 1], r = px[i + 2];
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            // Keep the deliberately conservative neutral-gray edge test. Colored item borders are
            // handled by the shape fallback below rather than by broadening this predicate until UI
            // panels become candidates.
            return mx is >= 18 and <= 82 && mx - mn <= 18;
        }
        bool IsDark(int x, int y)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return false;
            int i = y * stride + x * 3;
            return Math.Max(px[i + 2], Math.Max(px[i + 1], px[i])) < 34;
        }

        int minRun = Math.Clamp((int)Math.Round(Math.Min(w, h) * MinRunFraction), 60, 220);
        int maxGap = Math.Clamp((int)Math.Round(Math.Min(w, h) * MaxRunGapFraction), 4, 10);

        // A column may contain several independent panels. Retaining every long run prevents a
        // bottom inventory panel from being unioned with the tooltip edge at the same x coordinate.
        var runs = new List<(int Y0, int Y1)>[w];
        for (int x = 0; x < w; x++)
        {
            List<(int Y0, int Y1)>? column = null;
            int runStart = -1, lastGood = -1;
            for (int y = 0; y < h; y += 2)
            {
                if (IsBorder(x, y))
                {
                    if (runStart < 0) runStart = y;
                    lastGood = y;
                }
                else if (runStart >= 0 && y - lastGood > maxGap)
                {
                    if (lastGood - runStart >= minRun)
                        (column ??= new()).Add((runStart, lastGood));
                    runStart = -1;
                }
            }
            if (runStart >= 0 && lastGood - runStart >= minRun)
                (column ??= new()).Add((runStart, lastGood));
            runs[x] = column ?? [];
        }

        // Cluster only vertically-overlapping runs from adjacent columns. A union of non-overlapping
        // intervals is precisely the old false-positive: it made a tooltip edge and a bottom panel
        // look like one 1200px edge.
        var edges = new List<Edge>();
        for (int x = 0; x < w; x++)
            foreach (var (y0, y1) in runs[x])
            {
                int hit = -1;
                for (int i = edges.Count - 1; i >= 0; i--)
                {
                    var edge = edges[i];
                    if (x - edge.LastX > EdgeClusterGap) break;
                    int overlap = Math.Min(y1, edge.Y1) - Math.Max(y0, edge.Y0);
                    int required = Math.Max(24, (int)Math.Round(Math.Min(y1 - y0, edge.Y1 - edge.Y0) * 0.25));
                    if (overlap >= required) { hit = i; break; }
                }

                if (hit < 0)
                    edges.Add(new Edge(x, y0, y1, x));
                else
                {
                    var edge = edges[hit];
                    edges[hit] = edge with
                    {
                        X = (edge.X + x) / 2,
                        Y0 = Math.Min(edge.Y0, y0),
                        Y1 = Math.Max(edge.Y1, y1),
                        LastX = x,
                    };
                }
            }

        var cands = new List<Candidate>();
        int minGap = Math.Max(64, (int)Math.Round(Math.Min(w, h) * MinPairGapFraction));
        int maxPairGap = Math.Max(minGap + 1, (int)Math.Round(w * MaxPairGapFraction));
        int minOverlap = Math.Max(60, Math.Min(220, (int)Math.Round(Math.Min(w, h) * MinOverlapFraction)));
        for (int a = 0; a < edges.Count; a++)
            for (int b = a + 1; b < edges.Count; b++)
            {
                int gap = edges[b].X - edges[a].X;
                if (gap < minGap || gap > maxPairGap) continue;

                int overlapTop = Math.Max(edges[a].Y0, edges[b].Y0);
                int overlapBottom = Math.Min(edges[a].Y1, edges[b].Y1);
                int overlap = overlapBottom - overlapTop;
                if (overlap < minOverlap) continue;

                int dark = 0, total = 0;
                int inset = Math.Max(5, (int)Math.Round(gap * 0.025));
                int stepY = Math.Max(2, overlap / 28);
                int stepX = Math.Max(2, gap / 20);
                for (int yy = overlapTop + 8; yy < overlapBottom - 8; yy += stepY)
                    for (int xx = edges[a].X + inset; xx < edges[b].X - inset; xx += stepX)
                    {
                        total++;
                        if (IsDark(xx, yy)) dark++;
                    }
                double darkFraction = total == 0 ? 0 : (double)dark / total;
                if (darkFraction < MinDarkFraction) continue;
                cands.Add(new Candidate(
                    new Rectangle(edges[a].X, overlapTop, gap, overlap),
                    darkFraction));
            }

        if (cands.Count == 0) return cands;


        // Collapse duplicate pairs from several adjacent edge columns. Do not normalize the candidate
        // height here: short tooltips are valid and their natural height is needed by the cropper.
        var unique = new List<Candidate>();
        foreach (var candidate in cands.OrderBy(c => c.Rect.Left).ThenBy(c => c.Rect.Top))
        {
            if (unique.Any(existing =>
                Math.Abs(existing.Rect.Left - candidate.Rect.Left) <= EdgeClusterGap * 2
                && Math.Abs(existing.Rect.Top - candidate.Rect.Top) <= 12
                && Math.Abs(existing.Rect.Width - candidate.Rect.Width) <= 16
                && Math.Abs(existing.Rect.Height - candidate.Rect.Height) <= 20))
                continue;
            unique.Add(candidate);
        }
        // A UI panel can share the tooltip's right border, creating a wider union candidate. Prefer the
        // contained candidate when it has comparable height; this is a shape decision, not a pixel-width
        // preset, so it continues to work as the tooltip scales.
        var contained = unique.Where(candidate => !unique.Any(other =>
            other.Rect != candidate.Rect
            && other.Rect.Left >= candidate.Rect.Left - EdgeClusterGap
            && other.Rect.Right <= candidate.Rect.Right + EdgeClusterGap
            && other.Rect.Width < candidate.Rect.Width - 40
            && other.Rect.Height >= candidate.Rect.Height * 0.75)).ToList();
        if (contained.Count > 0)
            unique = contained;

        isDark = IsDark;
        isBorder = IsBorder;
        return unique;
    }

    private static Rectangle Select(List<Candidate> cands, (int X, int Y)? cursor, int h,
        Func<int, int, bool> isDark, Func<int, int, bool> isBorder)
    {
        Candidate picked;
        if (cursor is { } c)
        {
            static int DistanceToRange(int value, int start, int end, int tolerance)
                => value < start - tolerance ? start - tolerance - value
                 : value > end + tolerance ? value - end - tolerance
                 : 0;

            int Score(Candidate candidate)
            {
                var r = candidate.Rect;
                int xTol = Math.Max(12, r.Width / 4);
                int yTol = Math.Max(20, r.Height / 8);
                int distance = DistanceToRange(c.X, r.Left, r.Right, xTol)
                             + DistanceToRange(c.Y, r.Top, r.Bottom, yTol);
                // Position is primary; darkness only breaks near ties. This rejects a large empty
                // panel that happens to be closer in x while retaining a dim tooltip.
                return distance * 100000
                     - candidate.Rect.Height * 100
                     - candidate.Rect.Width
                     - (int)Math.Round(candidate.DarkFraction * 100);
            }
            picked = cands.MinBy(Score);

        }
        else
        {
            // Offline captures usually have no cursor metadata. UI panels can be wider than a tooltip,
            // but the actual tooltip is the tallest coherent dark rectangle; keep leftmost only as the
            // deterministic tie-break for a comparison pair.
            int maxHeight = cands.Max(c => c.Rect.Height);
            picked = cands.Where(c => c.Rect.Height >= maxHeight * 0.8)
                         .OrderBy(c => c.Rect.Left)
                         .ThenByDescending(c => c.Rect.Height)
                         .First();
        }

        // while the interior remains dark, stopping on a border-gray row. The budget scales with the
        // candidate height and never imposes a fixed tooltip height.
        int missBudget = Math.Clamp((int)Math.Round(picked.Rect.Height * 0.12), 30, 110);
        (double Dark, double Border) RowProfile(int y, Rectangle r)
        {
            int dark = 0, border = 0, total = 0;
            for (int x = r.Left + Math.Max(4, r.Width / 40); x < r.Right - Math.Max(4, r.Width / 40); x += 4)
            {
                total++;
                if (isDark(x, y)) dark++;
                else if (isBorder(x, y)) border++;
            }
            return total == 0 ? (0, 0) : ((double)dark / total, (double)border / total);
        }

        int top = picked.Rect.Top, bottom = picked.Rect.Bottom;
        for (int y = picked.Rect.Top - 1, miss = 0; y >= 0 && miss <= missBudget; y--)
        {
            var (dark, border) = RowProfile(y, picked.Rect);
            if (border >= 0.55) { top = y; break; }
            if (dark >= MinDarkFraction) { top = y; miss = 0; } else miss++;
        }
        for (int y = picked.Rect.Bottom + 1, miss = 0; y < h && miss <= missBudget; y++)
        {
            var (dark, border) = RowProfile(y, picked.Rect);
            if (border >= 0.55) { bottom = y; break; }
            if (dark >= MinDarkFraction) { bottom = y; miss = 0; } else miss++;
        }
        return Rectangle.FromLTRB(picked.Rect.Left, top, picked.Rect.Right, bottom);
    }
}
