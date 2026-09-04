using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DnfItemChecker.Vision;

/// <summary>
/// Screen capture via GDI (<see cref="Graphics.CopyFromScreen(int, int, int, int, Size)"/>),
/// returning BMP bytes (see <see cref="IScreenCaptureService"/> for the codec choice).
/// Windows-only (System.Drawing.Common).
/// </summary>
public sealed class GdiScreenCaptureService : IScreenCaptureService
{
    private const int EffectiveDpi = 0; // MDT_EFFECTIVE_DPI

    public ScreenRect VirtualScreenBounds => new(
        NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
        NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

    public byte[] CaptureRegionBmp(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            return Array.Empty<byte>();

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(width, height));
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms, ImageFormat.Bmp);
        return ms.ToArray();
    }

    public ScreenCaptureSnapshot CaptureAroundCursorBmpWithMetadata(int width, int height)
    {
        var virtualScreen = VirtualScreenBounds;
        if (width <= 0 || height <= 0 || virtualScreen.Width <= 0 || virtualScreen.Height <= 0)
            return EmptySnapshot(virtualScreen);

        var cursor = ReadCursor(virtualScreen);
        var monitors = ReadMonitors();
        var foreground = ReadForegroundWindow();
        var captureBounds = ScreenCaptureGeometry.BoundsForCursor(
            virtualScreen, cursor, foreground?.ClientRectPx, monitors);
        var source = ScreenCaptureGeometry.AroundCursor(captureBounds, cursor, width, height);
        long captureStart = Stopwatch.GetTimestamp();

        var bytes = CaptureRegionBmp(source.Left, source.Top, source.Width, source.Height);
        long captureEnd = Stopwatch.GetTimestamp();
        var after = TryReadCursor();
        var metadata = new ScreenCaptureMetadata(
            SchemaVersion: 1,
            CaptureKind: "real-live",
            CaptureMethod: "gdi-cursor-region",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            CaptureStartTicks: captureStart,
            CaptureEndTicks: captureEnd,
            StopwatchFrequency: Stopwatch.Frequency,
            VirtualScreenPx: virtualScreen,
            SourceRectPx: source,
            CursorScreenPx: cursor,
            CursorImagePx: ScreenCaptureGeometry.ToImageCoordinates(source, cursor),
            CursorAfterCaptureScreenPx: after,
            CursorMovedDuringCapture: after is { } p && p != cursor,
            ProcessDpiAwareness: CurrentDpiAwareness(),
            Monitors: monitors,
            ForegroundWindow: foreground);
        var imagePoint = metadata.CursorImagePx;
        return new ScreenCaptureSnapshot(bytes, imagePoint.X, imagePoint.Y, metadata);
    }

    public (int X, int Y) GetCursorPosition()
    {
        var virtualScreen = VirtualScreenBounds;
        var cursor = TryReadCursor();
        if (cursor is { } point)
            return (point.X, point.Y);
        return (virtualScreen.Left + virtualScreen.Width / 2,
            virtualScreen.Top + virtualScreen.Height / 2);
    }

    private static ScreenPoint ReadCursor(ScreenRect virtualScreen)
        => TryReadCursor() ?? new ScreenPoint(
            virtualScreen.Left + virtualScreen.Width / 2,
            virtualScreen.Top + virtualScreen.Height / 2);

    private static ScreenPoint? TryReadCursor()
        => NativeMethods.GetCursorPos(out var point) ? new ScreenPoint(point.X, point.Y) : null;

    private static ScreenCaptureSnapshot EmptySnapshot(ScreenRect virtualScreen)
    {
        long now = Stopwatch.GetTimestamp();
        var metadata = new ScreenCaptureMetadata(
            1, "invalid", "gdi-cursor-region", DateTimeOffset.UtcNow, now, now, Stopwatch.Frequency,
            virtualScreen, new ScreenRect(0, 0, 0, 0), new ScreenPoint(0, 0),
            new ScreenPoint(0, 0), null, false, CurrentDpiAwareness(), [], null);
        return new ScreenCaptureSnapshot([], 0, 0, metadata);
    }

    private static IReadOnlyList<MonitorCaptureMetadata> ReadMonitors()
    {
        var monitors = new List<MonitorCaptureMetadata>();
        NativeMethods.MonitorEnumProc callback =
            (nint handle, nint hdc, ref NativeMethods.RECT clip, nint data) =>
        {
            var info = new NativeMethods.MONITORINFOEX
            {
                CbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>(),
                DeviceName = string.Empty,
            };
            if (!NativeMethods.GetMonitorInfo(handle, ref info))
                return true;

            uint? dpiX = null, dpiY = null;
            try
            {
                if (NativeMethods.GetDpiForMonitor(handle, EffectiveDpi, out uint x, out uint y) == 0)
                {
                    dpiX = x;
                    dpiY = y;
                }
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }

            monitors.Add(new MonitorCaptureMetadata(
                info.DeviceName ?? string.Empty,
                ToScreenRect(info.Monitor),
                ToScreenRect(info.Work),
                dpiX,
                dpiY));
            return true;
        };

        try
        {
            NativeMethods.EnumDisplayMonitors(0, 0, callback, 0);
        }
        catch (DllNotFoundException) { }
        return monitors;
    }

    private static WindowCaptureMetadata? ReadForegroundWindow()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == 0) return null;

        int length = Math.Max(1, NativeMethods.GetWindowTextLength(hwnd) + 1);
        var chars = new char[length];
        int copied = NativeMethods.GetWindowText(hwnd, chars, chars.Length);
        string title = new string(chars, 0, Math.Max(0, Math.Min(copied, chars.Length)));
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);

        ScreenRect? client = null;
        if (NativeMethods.GetClientRect(hwnd, out var clientRect))
        {
            var topLeft = new NativeMethods.POINT { X = clientRect.Left, Y = clientRect.Top };
            var bottomRight = new NativeMethods.POINT { X = clientRect.Right, Y = clientRect.Bottom };
            if (NativeMethods.ClientToScreen(hwnd, ref topLeft)
                && NativeMethods.ClientToScreen(hwnd, ref bottomRight))
            {
                client = new ScreenRect(topLeft.X, topLeft.Y,
                    bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y);
            }
        }

        string? processName = null;
        if (processId > 0)
        {
            try { processName = Process.GetProcessById((int)processId).ProcessName; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        uint dpi = 0;
        try { dpi = NativeMethods.GetDpiForWindow(hwnd); }
        catch (EntryPointNotFoundException) { }
        return new WindowCaptureMetadata(hwnd.ToInt64(), title, processId, processName, client,
            dpi > 0 ? dpi : null);
    }

    private static ScreenRect ToScreenRect(NativeMethods.RECT rect)
        => new(rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left),
            Math.Max(0, rect.Bottom - rect.Top));

    private static string CurrentDpiAwareness()
    {
        try
        {
            return NativeMethods.GetAwarenessFromDpiAwarenessContext(
                NativeMethods.GetThreadDpiAwarenessContext()) switch
            {
                0 => "Unaware",
                1 => "System",
                2 => "PerMonitor",
                _ => "Unknown",
            };
        }
        catch (EntryPointNotFoundException) { return "Unknown"; }
    }
}
