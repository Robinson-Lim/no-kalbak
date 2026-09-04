using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;

namespace DnfItemChecker.Vision;

/// <summary>One game-client resolution required by the live acceptance run.</summary>
public sealed record LiveValidationProfile(string Id, int Width, int Height);

/// <summary>Per-resolution result emitted by <see cref="LiveCaptureValidator"/>.</summary>
public sealed record LiveValidationProfileResult(
    string Id,
    int Width,
    int Height,
    int CaptureCount,
    int ValidTrialCount,
    int SuccessfulTrialCount,
    double RecognitionSuccessRate,
    double? P50StableWindowToUiMs,
    double? P95StableWindowToUiMs,
    string Status);

/// <summary>Machine-readable outcome of a live artifact validation run.</summary>
public sealed record LiveValidationReport(
    string Status,
    int ArtifactCount,
    int ValidArtifactCount,
    IReadOnlyList<string> MissingProfiles,
    IReadOnlyList<string> InvalidArtifacts,
    IReadOnlyList<LiveValidationProfileResult> Profiles);

/// <summary>
/// Validates PNG/JSON artifacts produced by the passive live capture path. It never opens a game window,
/// moves the cursor, or runs OCR; the existing RecogProbe remains responsible for field-level labels.
/// Render-timeout artifacts remain inspectable but have no UI timing and do not count as completed trials.
/// </summary>
public static class LiveCaptureValidator
{
    public static readonly IReadOnlyList<LiveValidationProfile> DefaultProfiles =
    [
        new("800x600", 800, 600),
        new("1024x768", 1024, 768),
        new("1600x1200", 1600, 1200),
        new("1280x720", 1280, 720),
        new("1366x768", 1366, 768),
        new("1280x800", 1280, 800),
    ];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static IReadOnlyList<LiveValidationProfile> LoadProfiles(string path)
    {
        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ProfileConfig>(json, JsonOptions);
        if (config?.Profiles is not { Count: > 0 }
            || config.Profiles.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id)
                || p.Width <= 0 || p.Height <= 0))
            throw new InvalidDataException("The acceptance profile file has no valid profiles.");
        return config.Profiles;
    }

    public static LiveValidationReport Validate(string directory,
        IReadOnlyList<LiveValidationProfile>? profiles = null, int minimumTrials = 30,
        bool requireDpi = false, string? requiredWindowTitle = null)
    {
        profiles ??= DefaultProfiles;
        minimumTrials = Math.Max(0, minimumTrials);
        string root = Path.GetFullPath(directory);
        var invalid = new List<string>();
        var artifacts = new List<(LiveCaptureArtifact Artifact, string ImagePath)>();
        if (!Directory.Exists(root))
        {
            return new LiveValidationReport("INCONCLUSIVE", 0, 0,
                profiles.Select(p => p.Id).ToArray(), [$"Input directory does not exist: {directory}"], []);
        }

        var seenImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sidecar in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            LiveCaptureArtifact? artifact;
            try
            {
                artifact = JsonSerializer.Deserialize<LiveCaptureArtifact>(File.ReadAllText(sidecar), JsonOptions);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                invalid.Add($"{Path.GetFileName(sidecar)}: invalid JSON ({ex.Message})");
                continue;
            }

            if (artifact is null)
            {
                invalid.Add($"{Path.GetFileName(sidecar)}: empty artifact");
                continue;
            }

            var error = ValidateArtifact(root, artifact, seenImages, requireDpi, requiredWindowTitle,
                out string? imagePath);
            if (error is not null)
            {
                invalid.Add($"{Path.GetFileName(sidecar)}: {error}");
                continue;
            }
            artifacts.Add((artifact, imagePath!));
        }

        var results = new List<LiveValidationProfileResult>(profiles.Count);
        var missing = new List<string>();
        foreach (var profile in profiles)
        {
            var matching = artifacts.Where(x => MatchesProfile(x.Artifact, profile)).ToList();
            var validTrials = matching.Where(x => IsValidTrial(x.Artifact)).Select(x => x.Artifact).ToList();
            var successful = validTrials.Count(x => x.Timing!.RecognitionSucceeded
                && string.Equals(x.Timing.Outcome, "recognized", StringComparison.Ordinal));
            double successRate = validTrials.Count == 0 ? 0 : (double)successful / validTrials.Count;
            var durations = validTrials.Select(x => x.Timing!.StableWindowToUiMs!.Value)
                .Where(x => double.IsFinite(x) && x >= 0).OrderBy(x => x).ToArray();
            bool enough = validTrials.Count >= minimumTrials;
            bool hasNonRendered = matching.Count != validTrials.Count;
            bool allSuccessful = validTrials.Count > 0 && successful == validTrials.Count;
            string profileStatus = hasNonRendered || !enough
                ? "INCONCLUSIVE"
                : allSuccessful ? "PASS" : "FAIL";
            if (matching.Count == 0 || !enough || hasNonRendered) missing.Add(profile.Id);
            results.Add(new LiveValidationProfileResult(
                profile.Id, profile.Width, profile.Height, matching.Count, validTrials.Count,
                successful, successRate,
                durations.Length == 0 ? null : Percentile(durations, 0.50),
                durations.Length == 0 ? null : Percentile(durations, 0.95), profileStatus));
        }

        string overallStatus = invalid.Count > 0 || results.Any(x => x.Status == "FAIL")
            ? "FAIL"
            : missing.Count > 0 || results.Any(x => x.Status == "INCONCLUSIVE")
                ? "INCONCLUSIVE"
                : "PASS";
        return new LiveValidationReport(overallStatus, artifacts.Count + invalid.Count, artifacts.Count,
            missing, invalid, results);
    }

    private static string? ValidateArtifact(string root, LiveCaptureArtifact artifact,
        HashSet<string> seenImages, bool requireDpi, string? requiredWindowTitle, out string? imagePath)
    {
        imagePath = null;
        if (artifact.SchemaVersion != 1) return $"unsupported schema version {artifact.SchemaVersion}";
        if (!string.Equals(artifact.CaptureKind, "real-live", StringComparison.Ordinal))
            return "captureKind is not real-live";
        if (artifact.Capture is null) return "capture metadata is missing";
        if (artifact.Timing is null) return "timing is missing";
        if (!string.Equals(artifact.Capture.CaptureKind, "real-live", StringComparison.Ordinal))
            return "capture metadata is not real-live";
        if (!string.Equals(artifact.Capture.CaptureMethod, "gdi-cursor-region", StringComparison.Ordinal))
            return "unsupported capture method";
        if (string.IsNullOrWhiteSpace(artifact.ImageFile)) return "imageFile is empty";
        imagePath = Path.GetFullPath(Path.Combine(root, artifact.ImageFile!));
        if (!IsWithin(root, imagePath)) return "imageFile escapes the input directory";
        if (!File.Exists(imagePath)) return "image file is missing";
        if (!seenImages.Add(imagePath)) return "duplicate imageFile";

        byte[] imageBytes;
        try { imageBytes = File.ReadAllBytes(imagePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return $"image cannot be read ({ex.Message})"; }
        string hash = Convert.ToHexString(SHA256.HashData(imageBytes));
        if (!string.Equals(hash, artifact.ImageSha256, StringComparison.OrdinalIgnoreCase))
            return "imageSha256 mismatch";

        try
        {
            using var stream = new MemoryStream(imageBytes, writable: false);
            using var bitmap = new Bitmap(stream);
            if (bitmap.Width != artifact.ImageWidth || bitmap.Height != artifact.ImageHeight)
                return "image dimensions do not match artifact";
        }
        catch (ArgumentException ex) { return $"invalid image ({ex.Message})"; }

        var capture = artifact.Capture;
        if (capture.VirtualScreenPx.Width <= 0 || capture.VirtualScreenPx.Height <= 0
            || capture.SourceRectPx.Width <= 0 || capture.SourceRectPx.Height <= 0)
            return "invalid screen rectangle";
        if (!capture.VirtualScreenPx.Contains(capture.SourceRectPx))
            return "source rectangle is outside virtual screen";
        if (capture.SourceRectPx.Width != artifact.ImageWidth
            || capture.SourceRectPx.Height != artifact.ImageHeight)
            return "source rectangle does not match image dimensions";
        if (!capture.SourceRectPx.Contains(capture.CursorScreenPx)
            || !capture.VirtualScreenPx.Contains(capture.CursorScreenPx))
            return "cursor is outside source or virtual screen";
        var expectedImagePoint = ScreenCaptureGeometry.ToImageCoordinates(
            capture.SourceRectPx, capture.CursorScreenPx);
        if (expectedImagePoint != capture.CursorImagePx
            || expectedImagePoint.X != artifact.Capture.CursorImagePx.X
            || expectedImagePoint.Y != artifact.Capture.CursorImagePx.Y)
            return "cursor image coordinates are inconsistent";
        if (capture.Monitors is not { Count: > 0 }) return "monitor topology is missing";
        if (capture.Monitors.Any(m => m.BoundsPx.Width <= 0 || m.BoundsPx.Height <= 0
            || !m.BoundsPx.Contains(m.WorkAreaPx)))
            return "monitor bounds/work area are invalid";
        if (requireDpi && (!capture.ProcessDpiAwareness.StartsWith("PerMonitor", StringComparison.Ordinal)
            || capture.Monitors.Any(m => m.EffectiveDpiX is null || m.EffectiveDpiY is null)
            || capture.ForegroundWindow?.Dpi is null))
            return "required DPI metadata is missing";
        if (requiredWindowTitle is not null
            && (capture.ForegroundWindow is null
                || !capture.ForegroundWindow.Title.Contains(requiredWindowTitle, StringComparison.OrdinalIgnoreCase)))
            return "foreground window title does not match required title";

        var timing = artifact.Timing;
        if (timing is null) return "timing is missing";
        if (timing.CaptureStartTicks != capture.CaptureStartTicks
            || timing.CaptureEndTicks != capture.CaptureEndTicks)
            return "timing/capture timestamp mismatch";
        if (timing.CaptureStartTicks < timing.StableAtTicks
            || timing.CaptureEndTicks < timing.CaptureStartTicks
            || timing.RecognitionEndTicks < timing.CaptureEndTicks)
            return "timestamps are not monotonic";
        if (timing.CursorMovedDuringCapture || capture.CursorMovedDuringCapture)
            return "cursor moved during capture";
        if (timing.UiRendered)
        {
            if (timing.UiCommitTicks is not long uiCommitTick
                || uiCommitTick < timing.RecognitionEndTicks)
                return "rendered UI commit is missing or not monotonic";
            if (timing.CursorStoppedToUiMs is not double stoppedToUi
                || timing.StableWindowToUiMs is not double stableToUi
                || timing.CaptureToUiMs is not double captureToUi
                || !double.IsFinite(stoppedToUi) || stoppedToUi < 0
                || !double.IsFinite(stableToUi) || stableToUi < 0
                || !double.IsFinite(captureToUi) || captureToUi < 0)
                return "timing duration is invalid";
        }
        else if (timing.UiCommitTicks is not null
            || timing.CursorStoppedToUiMs is not null
            || timing.StableWindowToUiMs is not null
            || timing.CaptureToUiMs is not null)
        {
            return "non-rendered trial contains a fabricated UI timestamp";
        }
        return null;
    }

    private static bool MatchesProfile(LiveCaptureArtifact artifact, LiveValidationProfile profile)
    {
        var client = artifact.Capture?.ForegroundWindow?.ClientRectPx;
        return client is { } rect && rect.Width == profile.Width && rect.Height == profile.Height;
    }

    private static bool IsValidTrial(LiveCaptureArtifact artifact)
        => artifact.Timing is { } timing
            && timing.UiRendered
            && !timing.CursorMovedDuringCapture
            && timing.UiCommitTicks is not null
            && timing.CursorStoppedToUiMs is not null
            && timing.StableWindowToUiMs is not null
            && timing.CaptureToUiMs is not null
            && timing.UiCommitTicks.Value >= timing.RecognitionEndTicks;

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        int index = Math.Clamp((int)Math.Ceiling(percentile * values.Count) - 1, 0, values.Count - 1);
        return values[index];
    }

    private static bool IsWithin(string root, string path)
    {
        string rootWithSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ProfileConfig(IReadOnlyList<LiveValidationProfile> Profiles);
}
