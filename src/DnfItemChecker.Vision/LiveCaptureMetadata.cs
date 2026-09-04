namespace DnfItemChecker.Vision;

/// <summary>Physical screen-pixel point. Never convert this type to WPF device-independent units.</summary>
public readonly record struct ScreenPoint(int X, int Y);

/// <summary>Physical screen-pixel rectangle; coordinates may be negative on a secondary monitor.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Width, int Height)
{
    public int Right => checked(Left + Width);
    public int Bottom => checked(Top + Height);

    public bool Contains(ScreenPoint point)
        => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

    public bool Contains(ScreenRect other)
        => other.Left >= Left && other.Top >= Top
            && other.Right <= Right && other.Bottom <= Bottom;
}

/// <summary>Monitor topology and effective DPI observed at capture time.</summary>
public sealed record MonitorCaptureMetadata(
    string DeviceName,
    ScreenRect BoundsPx,
    ScreenRect WorkAreaPx,
    uint? EffectiveDpiX,
    uint? EffectiveDpiY);

/// <summary>Foreground window diagnostics captured alongside a live frame.</summary>
public sealed record WindowCaptureMetadata(
    long Handle,
    string Title,
    uint ProcessId,
    string? ProcessName,
    ScreenRect? ClientRectPx,
    uint? Dpi);

/// <summary>Coordinates and display topology for one screen capture.</summary>
public sealed record ScreenCaptureMetadata(
    int SchemaVersion,
    string CaptureKind,
    string CaptureMethod,
    DateTimeOffset CapturedAtUtc,
    long CaptureStartTicks,
    long CaptureEndTicks,
    long StopwatchFrequency,
    ScreenRect VirtualScreenPx,
    ScreenRect SourceRectPx,
    ScreenPoint CursorScreenPx,
    ScreenPoint CursorImagePx,
    ScreenPoint? CursorAfterCaptureScreenPx,
    bool CursorMovedDuringCapture,
    string ProcessDpiAwareness,
    IReadOnlyList<MonitorCaptureMetadata> Monitors,
    WindowCaptureMetadata? ForegroundWindow);
/// <summary>Runtime filters that prevent the live watcher from reading its own WPF surface.</summary>
public static class LiveCapturePolicy
{
    public static bool IsSelfForeground(WindowCaptureMetadata? foreground, uint currentProcessId)
        => foreground is not null && foreground.ProcessId == currentProcessId;
    public static bool IsDnfForeground(WindowCaptureMetadata? foreground)
        => foreground is { ProcessName: string name }
            && string.Equals(name, "DNF", StringComparison.OrdinalIgnoreCase);

}


/// <summary>Capture bytes plus the physical coordinate metadata needed to interpret them.</summary>
public sealed record ScreenCaptureSnapshot(
    byte[] Bytes,
    int CursorX,
    int CursorY,
    ScreenCaptureMetadata Metadata);

/// <summary>
/// Monotonic timing and recognition outcome for one live capture trial. `UiCommitTicks` is sampled
/// from a one-shot WPF CompositionTarget.Rendering callback after result/status notifications.
/// </summary>
public sealed record LiveCaptureTiming(
    long StableSinceTicks,
    long StableAtTicks,
    long CaptureStartTicks,
    long CaptureEndTicks,
    long RecognitionEndTicks,
    long? UiCommitTicks,
    bool UiRendered,
    double? CursorStoppedToUiMs,
    double? StableWindowToUiMs,
    double? CaptureToUiMs,
    int CursorPollIntervalMs,
    int RequiredStableMs,
    string Outcome,
    bool RecognitionSucceeded,
    bool CursorMovedDuringCapture,
    TooltipRecognitionTiming? Recognition);

/// <summary>Atomic PNG + JSON sidecar written by the live debug capture ring.</summary>
public sealed record LiveCaptureArtifact(
    int SchemaVersion,
    string? CaptureKind,
    string? ImageFile,
    string? ImageSha256,
    int ImageWidth,
    int ImageHeight,
    ScreenCaptureMetadata? Capture,
    LiveCaptureTiming? Timing,
    string? Rarity,
    string? Slot,
    string? MainStat,
    int? ObservedValue);

/// <summary>Resolution-independent placement and cursor coordinate helpers.</summary>
public static class ScreenCaptureGeometry
{
    public static ScreenRect AroundCursor(ScreenRect bounds, ScreenPoint cursor, int width, int height,
        int margin = 160)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || width <= 0 || height <= 0)
            return new ScreenRect(bounds.Left, bounds.Top, 0, 0);

        int captureWidth = Math.Min(width, bounds.Width);
        int captureHeight = Math.Min(height, bounds.Height);
        int localCursorX = Math.Clamp(cursor.X - bounds.Left, 0, bounds.Width - 1);
        int localCursorY = Math.Clamp(cursor.Y - bounds.Top, 0, bounds.Height - 1);
        int left = localCursorX < bounds.Width / 2
            ? localCursorX - margin
            : localCursorX - (captureWidth - margin);
        int top = localCursorY < bounds.Height / 2
            ? localCursorY - margin
            : localCursorY - (captureHeight - margin);
        left = Math.Clamp(left, 0, bounds.Width - captureWidth);
        top = Math.Clamp(top, 0, bounds.Height - captureHeight);
        return new ScreenRect(bounds.Left + left, bounds.Top + top, captureWidth, captureHeight);
    }
    /// <summary>
    /// Chooses the smallest reliable screen bounds for a cursor capture. A foreground window client
    /// that contains the cursor wins (the normal game-window case); otherwise use the monitor under
    /// the cursor, then the full virtual desktop as the last fallback.
    /// </summary>
    public static ScreenRect BoundsForCursor(
        ScreenRect virtualScreen,
        ScreenPoint cursor,
        ScreenRect? foregroundClient,
        IReadOnlyList<MonitorCaptureMetadata> monitors)
    {
        if (foregroundClient is { } client
            && client.Width > 0
            && client.Height > 0
            && client.Contains(cursor))
            return client;

        foreach (var monitor in monitors)
        {
            if (monitor.BoundsPx.Width > 0
                && monitor.BoundsPx.Height > 0
                && monitor.BoundsPx.Contains(cursor))
                return monitor.BoundsPx;
        }

        return virtualScreen;
    }


    public static ScreenPoint ToImageCoordinates(ScreenRect source, ScreenPoint screen)
        => new(screen.X - source.Left, screen.Y - source.Top);
}
