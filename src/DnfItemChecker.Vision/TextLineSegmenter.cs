using SkiaSharp;

namespace DnfItemChecker.Vision;

/// <summary>
/// Pixel projection-profile text segmentation for tooltip crops — replaces the ONNX DbNet detection
/// pass (~200-350ms) with a ~2ms scan. Works because tooltip text is bright glyphs on a near-black
/// opaque box: rows with enough bright pixels form line bands; within a band, wide dark gaps split
/// side-by-side texts (stat text left vs the right-aligned 부위/등급 label column) the way the
/// detector's separate boxes did.
/// </summary>
public static class TextLineSegmenter
{
    // Glyph threshold 80: grayed-out stat lines render as dim as ~(134,120,79) with sparse strokes,
    // while the tooltip body stays <40 and section-band backgrounds ~70. Row gaps of ≤2 must be
    // bridged for those sparse lines; the over-merging this used to cause is handled by the valley
    // split below.
    private const int BrightThresh = 80;
    private const int MinRowPixels = 3;
    private const int MaxRowGap = 2;
    private const int MinLineHeight = 7;   // real text is ≥ ~12px; below = separators
    private const int SingleLineMax = 26;  // one tooltip text line incl. ascent/descent
    private const int MaxBlockHeight = 44; // a band this tall with no interior valley = icon strip
    private const int MinSegGap = 20;      // horizontal dark gap that separates two texts on one row
    private const int MinSegWidth = 8;
    // CRNN digit reads are sensitive to the crop margin in BOTH directions: margin-less crops misread
    // ("93"→"98" at 19px tall, correct at 25px), but a margin that overlaps the neighbouring line's
    // glyphs also misreads ("힘 171"→"힘 17" when the row above bleeds in). So: pad up to PadMax, but
    // never past the midpoint of the gap to the adjacent text band.
    private const int PadMax = 6;
    private const int PadX = 6;

    public static List<SKRectI> Segment(SKBitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var result = new List<SKRectI>();
        if (w < 16 || h < 16) return result;

        var pixels = bmp.Pixels; // one managed copy
        bool Bright(int x, int y)
        {
            var c = pixels[y * w + x];
            return Math.Max(c.Red, Math.Max(c.Green, c.Blue)) >= BrightThresh;
        }

        var rowCnt = new int[h];
        for (int y = 0; y < h; y++)
        {
            int n = 0;
            for (int x = 0; x < w; x++)
                if (Bright(x, y)) n++;
            rowCnt[y] = n;
        }

        // Tooltip line pitch (~17px) is barely taller than the glyphs, so adjacent lines often have
        // no fully-blank row between them — bands must be split at their weakest interior row, not
        // only at blank rows.
        var bands = new List<(int Y0, int Y1)>();
        void CollectBand(int y0, int y1)
        {
            int bh = y1 - y0 + 1;
            if (bh < MinLineHeight) return;
            if (bh > SingleLineMax)
            {
                // find the weakest row in the middle 70% — a real inter-line boundary is a deep valley
                int lo = y0 + Math.Max(3, bh * 15 / 100), hi = y1 - Math.Max(3, bh * 15 / 100);
                int minRow = -1, minVal = int.MaxValue, maxVal = 0;
                for (int y = y0; y <= y1; y++) maxVal = Math.Max(maxVal, rowCnt[y]);
                for (int y = lo; y <= hi; y++)
                    if (rowCnt[y] < minVal) { minVal = rowCnt[y]; minRow = y; }
                if (minRow >= 0 && minVal <= maxVal / 2)
                {
                    CollectBand(y0, minRow - 1);
                    CollectBand(minRow + 1, y1);
                    return;
                }
                if (bh > MaxBlockHeight) return; // solid bright block (icon strip), not text
            }
            bands.Add((y0, y1));
        }

        int bandStart = -1, lastText = -1;
        for (int y = 0; y <= h; y++)
        {
            bool text = y < h && rowCnt[y] >= MinRowPixels;
            if (text) { if (bandStart < 0) bandStart = y; lastText = y; }
            else if (bandStart >= 0 && (y == h || y - lastText > MaxRowGap))
            {
                CollectBand(bandStart, lastText);
                bandStart = -1;
            }
        }
        if (bandStart >= 0) CollectBand(bandStart, lastText);
        bands.Sort((a, b) => a.Y0.CompareTo(b.Y0));

        for (int i = 0; i < bands.Count; i++)
        {
            var (y0, y1) = bands[i];
            // vertical pad clamped to half the gap to the adjacent band, so context margin never
            // bleeds a neighbouring line's glyphs into the crop
            int gapUp = i > 0 ? y0 - bands[i - 1].Y1 - 1 : int.MaxValue;
            int gapDown = i + 1 < bands.Count ? bands[i + 1].Y0 - y1 - 1 : int.MaxValue;
            int padTop = Math.Min(PadMax, Math.Max(0, gapUp / 2));
            int padBottom = Math.Min(PadMax, Math.Max(0, gapDown / 2));

            // horizontal segments measured on the raw band rows (pre-pad)
            int segStart = -1, lastCol = -1;
            for (int x = 0; x <= w; x++)
            {
                bool col = false;
                if (x < w)
                    for (int yy = y0; yy <= y1 && !col; yy++)
                        col = Bright(x, yy);
                if (col) { if (segStart < 0) segStart = x; lastCol = x; }
                else if (segStart >= 0 && (x - lastCol > MinSegGap || x == w))
                {
                    if (lastCol - segStart + 1 >= MinSegWidth)
                        result.Add(new SKRectI(
                            Math.Max(0, segStart - PadX), Math.Max(0, y0 - padTop),
                            Math.Min(w, lastCol + 1 + PadX), Math.Min(h, y1 + 1 + padBottom)));
                    segStart = -1;
                }
            }
        }
        return result;
    }
}
