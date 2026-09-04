using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Vision;

/// <summary>
/// Crops and normalizes a DNF tooltip for the second OCR pass. The input geometry is measured from
/// the tooltip width (or the grade-line height when only an OCR anchor is available), then all axes
/// use the same scale. This keeps the parser's canonical text geometry stable across UI scales while
/// preserving the tooltip's natural height and padding.
/// </summary>
public static class TooltipCropper
{
    // Canonical body geometry. These are OCR coordinates, not resolution presets: the source crop is
    // scaled isotropically from its measured tooltip width/grade height.
    private const int CanonicalBodyWidth = TooltipFieldLayout.CanonicalBodyWidth;
    private const int CanonicalCropWidth = TooltipFieldLayout.CanonicalCropWidth;
    private const int CanonicalGradeHeight = TooltipFieldLayout.CanonicalGradeHeight;
    private const int CanonicalLeftPad = 30;
    private const int CanonicalAbove = TooltipFieldLayout.GradeAnchorAbove;

    // Value-pass crop: grade row → stat block + label column. Its output height is intentionally tied to
    // the grade anchor's scale, not to the whole-tooltip height.
    private const int ValueAbove = 12;
    private const int ValueHeight = 342;

    // Right-aligned label column (등급/교환/부위/골드) relative to the grade line.
    private const int LabelLeft = 115;
    private const int LabelRight = 315;
    private const int LabelAbove = 12;
    private const int LabelBelow = 185;

    /// <summary>Crops the whole inspected tooltip from a pixel-located rectangle.</summary>
    public static byte[]? CropRect(byte[] imageBytes, Rectangle rect)
    {
        if (imageBytes is null || imageBytes.Length == 0 || rect.Width < 16 || rect.Height < 16)
            return null;
        using var ms = new MemoryStream(imageBytes);
        using var src = new Bitmap(ms);
        return CropRect(src, rect);
    }


    /// <summary>Crops and isotropically normalizes a pixel-located tooltip.</summary>
    private static byte[]? CropRect(Bitmap src, Rectangle rect)
    {
        int leftPad = Math.Max(4, (int)Math.Round(rect.Width * 0.025));
        int topPad = Math.Max(4, (int)Math.Round(rect.Width * 0.018));
        int bottomPad = Math.Max(8, (int)Math.Round(rect.Width * 0.045));
        int left = Math.Clamp(rect.Left - leftPad, 0, src.Width - 1);
        int top = Math.Clamp(rect.Top - topPad, 0, src.Height - 1);
        int right = Math.Clamp(rect.Right + leftPad, left + 1, src.Width);
        int bottom = Math.Clamp(rect.Bottom + bottomPad, top + 1, src.Height);
        double scale = CanonicalBodyWidth / (double)Math.Max(1, rect.Width);
        return EmitScaled(src, left, top, right, bottom, scale, out var cropped) ? cropped : null;
    }

    /// <summary>Crops the whole tooltip around an OCR grade anchor and normalizes it.</summary>
    public static bool TryCrop(byte[] imageBytes, OcrLine gradeBox, out byte[] cropped)
    {
        cropped = [];
        if (imageBytes is null || imageBytes.Length == 0 || gradeBox is null) return false;
        using var ms = new MemoryStream(imageBytes);
        using var src = new Bitmap(ms);
        return CropTooltip(src, gradeBox, out cropped);
    }

    /// <summary>
    /// Crops just the right-aligned label column. Set <paramref name="normalized"/> when the source
    /// already came from <see cref="CropRect(byte[], Rectangle)"/>; otherwise its native grade height
    /// determines the isotropic scale.
    /// </summary>
    public static bool TryCropLabels(byte[] imageBytes, OcrLine gradeBox, out byte[] cropped)
        => TryCropLabels(imageBytes, gradeBox, out cropped, normalized: false);

    public static bool TryCropLabels(byte[] imageBytes, OcrLine gradeBox, out byte[] cropped, bool normalized)
    {
        cropped = [];
        if (imageBytes is null || imageBytes.Length == 0 || gradeBox is null) return false;
        using var ms = new MemoryStream(imageBytes);
        using var src = new Bitmap(ms);
        return CropLabels(src, gradeBox, out cropped, normalized);
    }

    /// <summary>
    /// All crops from one source decode. Tooltip geometry is located once using the grade anchor; the
    /// labels/value crops use the same measured scale so every crop shares canonical coordinates.
    /// </summary>
    /// <param name="forceGradeAnchor">
    /// When true, bypasses the pixel locator for the tooltip crop. This is required for a right-side
    /// comparison grade when the locator only sees the left panel; labels/value crops always use the
    /// supplied grade anchor.
    /// </param>
    public static (byte[]? Tooltip, byte[]? Labels, byte[]? Value) CropAll(
        byte[] imageBytes, OcrLine gradeBox, bool forceGradeAnchor = false)
    {
        if (imageBytes is null || imageBytes.Length == 0 || gradeBox is null)
            return (null, null, null);
        using var ms = new MemoryStream(imageBytes);
        using var src = new Bitmap(ms);

        byte[]? tooltip;
        if (forceGradeAnchor)
        {
            tooltip = CropTooltip(src, gradeBox, out var anchored) ? anchored : null;
        }
        else
        {
            var located = TooltipLocator.Locate(
                src, ((int)Math.Round(gradeBox.Left), (int)Math.Round(gradeBox.Top)));
            if (located is { } rect
                && gradeBox.Left >= rect.Left - 48 && gradeBox.Left <= rect.Right + 48
                && gradeBox.Top >= rect.Top - 48 && gradeBox.Top <= rect.Bottom + 48)
                tooltip = CropRect(src, rect);
            else
                tooltip = CropTooltip(src, gradeBox, out var anchored) ? anchored : null;
        }

        byte[]? labels = CropLabels(src, gradeBox, out var labelBytes) ? labelBytes : null;
        byte[]? value = CropValue(src, gradeBox, out var valueBytes) ? valueBytes : null;
        return (tooltip, labels, value);
    }

    /// <summary>Grade row → stat block only, for the ONNX value pass.</summary>
    public static bool TryCropValue(byte[] imageBytes, OcrLine gradeBox, out byte[] cropped)
    {
        cropped = [];
        if (imageBytes is null || imageBytes.Length == 0 || gradeBox is null) return false;
        using var ms = new MemoryStream(imageBytes);
        using var src = new Bitmap(ms);
        return CropValue(src, gradeBox, out cropped);
    }

    private static bool CropTooltip(Bitmap src, OcrLine gradeBox, out byte[] cropped)
    {
        double scale = AnchorScale(gradeBox);
        double desiredLeft = gradeBox.Left - CanonicalLeftPad * scale;
        double desiredTop = gradeBox.Top - CanonicalAbove * scale;
        double desiredRight = gradeBox.Left + (CanonicalCropWidth - CanonicalLeftPad) * scale;
        double desiredBottom = Math.Min(src.Height, desiredTop + Math.Max(260, (int)Math.Round((CanonicalAbove + 440) * scale)));
        return EmitPadded(src, desiredLeft, desiredTop, desiredRight, desiredBottom, 1.0 / scale, out cropped);
    }

    private static bool CropValue(Bitmap src, OcrLine gradeBox, out byte[] cropped)
    {
        double scale = AnchorScale(gradeBox);
        int left = Math.Clamp((int)Math.Round(gradeBox.Left - CanonicalLeftPad * scale), 0, src.Width - 1);
        int top = Math.Clamp((int)Math.Round(gradeBox.Top - ValueAbove * scale), 0, src.Height - 1);
        int right = Math.Clamp((int)Math.Round(left + CanonicalBodyWidth * scale), left + 1, src.Width);
        int bottom = Math.Clamp((int)Math.Round(top + ValueHeight * scale), top + 1, src.Height);
        return EmitScaled(src, left, top, right, bottom, 1.0 / scale, out cropped);
    }

    private static bool CropLabels(Bitmap src, OcrLine gradeBox, out byte[] cropped, bool normalized = false)
    {
        double scale = normalized ? 1.0 : AnchorScale(gradeBox);
        int left = Math.Clamp((int)Math.Round(gradeBox.Left + LabelLeft * scale), 0, src.Width - 1);
        int top = Math.Clamp((int)Math.Round(gradeBox.Top - LabelAbove * scale), 0, src.Height - 1);
        int right = Math.Clamp((int)Math.Round(gradeBox.Left + LabelRight * scale), left + 1, src.Width);
        int bottom = Math.Clamp((int)Math.Round(gradeBox.Top + LabelBelow * scale), top + 1, src.Height);
        return EmitScaled(src, left, top, right, bottom, 1.0 / scale, out cropped);
    }

    private static double AnchorScale(OcrLine gradeBox)
    {
        double byHeight = gradeBox.Height > 0 ? gradeBox.Height / (double)CanonicalGradeHeight : 0;
        double byWidth = gradeBox.Width > 0 ? gradeBox.Width / 84.0 : 0;
        double scale = byHeight > 0 ? byHeight : byWidth;
        return Math.Clamp(scale > 0 ? scale : 1.0, 0.55, 4.0);
    }

    private static bool EmitPadded(Bitmap src, double desiredLeft, double desiredTop,
        double desiredRight, double desiredBottom, double scale, out byte[] cropped)
    {
        cropped = [];
        if (desiredRight <= desiredLeft || desiredBottom <= desiredTop || scale <= 0) return false;

        int sourceLeft = Math.Max(0, (int)Math.Floor(desiredLeft));
        int sourceTop = Math.Max(0, (int)Math.Floor(desiredTop));
        int sourceRight = Math.Min(src.Width, (int)Math.Ceiling(desiredRight));
        int sourceBottom = Math.Min(src.Height, (int)Math.Ceiling(desiredBottom));
        if (sourceRight <= sourceLeft || sourceBottom <= sourceTop) return false;

        int outWidth = Math.Max(1, (int)Math.Round((desiredRight - desiredLeft) * scale));
        int outHeight = Math.Max(1, (int)Math.Round((desiredBottom - desiredTop) * scale));
        using var normalized = new Bitmap(outWidth, outHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            int destLeft = Math.Clamp((int)Math.Round((sourceLeft - desiredLeft) * scale), 0, outWidth - 1);
            int destTop = Math.Clamp((int)Math.Round((sourceTop - desiredTop) * scale), 0, outHeight - 1);
            int destRight = Math.Clamp((int)Math.Round((sourceRight - desiredLeft) * scale), destLeft + 1, outWidth);
            int destBottom = Math.Clamp((int)Math.Round((sourceBottom - desiredTop) * scale), destTop + 1, outHeight);
            graphics.DrawImage(src,
                new Rectangle(destLeft, destTop, destRight - destLeft, destBottom - destTop),
                new Rectangle(sourceLeft, sourceTop, sourceRight - sourceLeft, sourceBottom - sourceTop),
                GraphicsUnit.Pixel);
        }
        using var outMs = new MemoryStream();
        normalized.Save(outMs, ImageFormat.Bmp);
        cropped = outMs.ToArray();
        return true;
    }
    private static bool EmitScaled(Bitmap src, int left, int top, int right, int bottom, double scale,
        out byte[] cropped)
    {
        cropped = [];
        right = Math.Clamp(right, left + 1, src.Width);
        bottom = Math.Clamp(bottom, top + 1, src.Height);
        int width = right - left, height = bottom - top;
        if (width < 16 || height < 16) return false;

        int outWidth = Math.Max(1, (int)Math.Round(width * scale));
        int outHeight = Math.Max(1, (int)Math.Round(height * scale));
        using var normalized = new Bitmap(outWidth, outHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.Clear(Color.Black);
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(src,
                new Rectangle(0, 0, outWidth, outHeight),
                new Rectangle(left, top, width, height),
                GraphicsUnit.Pixel);
        }
        using var outMs = new MemoryStream();
        normalized.Save(outMs, ImageFormat.Bmp);
        cropped = outMs.ToArray();
        return true;
    }

}
