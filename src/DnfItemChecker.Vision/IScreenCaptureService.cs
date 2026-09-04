namespace DnfItemChecker.Vision;

/// <summary>Captures screen regions as BMP bytes for OCR. BMP over PNG: the strip is produced and
/// consumed in-process several times per recognition, and PNG's ~56ms encode + ~10ms/decode on the
/// 620×1440 strip was pure hot-path cost (BMP: ~10ms/~1ms). Debug captures re-encode to PNG on save.</summary>
public interface IScreenCaptureService
{
    /// <summary>Current virtual desktop bounds in physical screen pixels.</summary>
    ScreenRect VirtualScreenBounds { get; }

    byte[] CaptureRegionBmp(int x, int y, int width, int height);

    /// <summary>
    /// Captures around the physical cursor and returns the source rectangle, virtual-desktop origin,
    /// monitor/DPI topology, and foreground-window diagnostics captured with the frame.
    /// </summary>
    ScreenCaptureSnapshot CaptureAroundCursorBmpWithMetadata(int width, int height);

    /// <summary>Current mouse cursor position in physical screen pixels.</summary>
    (int X, int Y) GetCursorPosition();
}
