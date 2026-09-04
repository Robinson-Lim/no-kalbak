using System.Drawing;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;

namespace DnfItemChecker.Vision.Tests;

public sealed class LiveCaptureValidatorTests
{
    [Fact]
    public void AroundCursorPreservesNegativeVirtualScreenOrigin()
    {
        var virtualScreen = new ScreenRect(-1920, -1440, 3840, 2160);
        var source = ScreenCaptureGeometry.AroundCursor(
            virtualScreen, new ScreenPoint(-100, -100), 620, 900);

        Assert.True(virtualScreen.Contains(source));
        Assert.True(source.Contains(new ScreenPoint(-100, -100)));
        Assert.Equal(new ScreenPoint(160, 160),
            ScreenCaptureGeometry.ToImageCoordinates(source, new ScreenPoint(source.Left + 160, source.Top + 160)));
    }
    [Fact]
    public void BoundsForCursorPrefersForegroundClientContainingCursor()
    {
        var virtualScreen = new ScreenRect(0, 0, 3840, 2160);
        var cursor = new ScreenPoint(1157, 1316);
        var foregroundClient = new ScreenRect(80, 80, 1400, 1400);
        var monitors = new[]
        {
            new MonitorCaptureMetadata("DISPLAY1", virtualScreen, virtualScreen, 96, 96),
        };

        var bounds = ScreenCaptureGeometry.BoundsForCursor(
            virtualScreen, cursor, foregroundClient, monitors);
        var source = ScreenCaptureGeometry.AroundCursor(bounds, cursor, 620, 1600);

        Assert.Equal(foregroundClient, bounds);
        Assert.True(foregroundClient.Contains(source));
        Assert.True(source.Contains(cursor));
    }

    [Fact]
    public void BoundsForCursorUsesCursorMonitorWhenForegroundDoesNotContainCursor()
    {
        var virtualScreen = new ScreenRect(-1920, 0, 3840, 1440);
        var cursor = new ScreenPoint(200, 500);
        var foregroundClient = new ScreenRect(1920, 0, 800, 600);
        var monitors = new[]
        {
            new MonitorCaptureMetadata("DISPLAY1",
                new ScreenRect(-1920, 0, 1920, 1440),
                new ScreenRect(-1920, 0, 1920, 1400), 96, 96),
            new MonitorCaptureMetadata("DISPLAY2",
                new ScreenRect(0, 0, 1920, 1440),
                new ScreenRect(0, 0, 1920, 1400), 144, 144),
        };

        var bounds = ScreenCaptureGeometry.BoundsForCursor(
            virtualScreen, cursor, foregroundClient, monitors);
        var source = ScreenCaptureGeometry.AroundCursor(bounds, cursor, 620, 900);

        Assert.Equal(monitors[1].BoundsPx, bounds);
        Assert.True(bounds.Contains(source));
        Assert.True(virtualScreen.Contains(source));
        Assert.True(source.Contains(cursor));
    }


    [Fact]
    public void SelfForegroundPolicySkipsOnlyCurrentProcess()
    {
        var ownWindow = new WindowCaptureMetadata(
            Handle: 1,
            Title: "DNF 아이템 등급 판별기",
            ProcessId: 42,
            ProcessName: "DnfItemChecker.App",
            ClientRectPx: null,
            Dpi: 96);
        var gameWindow = ownWindow with
        {
            Handle = 2,
            ProcessId = 43,
            ProcessName = "DNF",
        };
        var otherWindow = ownWindow with
        {
            Handle = 3,
            ProcessId = 44,
            ProcessName = "Code",
        };

        Assert.True(LiveCapturePolicy.IsSelfForeground(ownWindow, 42));
        Assert.False(LiveCapturePolicy.IsSelfForeground(ownWindow, 43));
        Assert.False(LiveCapturePolicy.IsSelfForeground(null, 42));
        Assert.True(LiveCapturePolicy.IsDnfForeground(gameWindow));
        Assert.True(LiveCapturePolicy.IsDnfForeground(gameWindow with { ProcessName = "dnf" }));
        Assert.False(LiveCapturePolicy.IsDnfForeground(ownWindow));
        Assert.False(LiveCapturePolicy.IsDnfForeground(otherWindow));
        Assert.False(LiveCapturePolicy.IsDnfForeground(null));
    }

    [Fact]
    public void ValidateAcceptsHashedArtifactAndComputesResolutionTiming()
    {
        string directory = CreateTempDirectory();
        try
        {
            var (artifact, imageBytes) = CreateArtifact();
            File.WriteAllBytes(Path.Combine(directory, artifact.ImageFile!), imageBytes);
            File.WriteAllText(Path.Combine(directory, "frame.json"),
                JsonSerializer.Serialize(artifact, LiveCaptureValidator.JsonOptions));

            var report = LiveCaptureValidator.Validate(directory,
                [new LiveValidationProfile("800x600", 800, 600)], minimumTrials: 1,
                requireDpi: true, requiredWindowTitle: "Dungeon");

            Assert.Equal("PASS", report.Status);
            Assert.Equal(1, report.ArtifactCount);
            Assert.Equal(1, report.ValidArtifactCount);
            var profile = Assert.Single(report.Profiles);
            Assert.Equal(1, profile.CaptureCount);
            Assert.Equal(1, profile.ValidTrialCount);
            Assert.Equal(1, profile.SuccessfulTrialCount);
            Assert.Equal(400, profile.P50StableWindowToUiMs);
            Assert.Equal(400, profile.P95StableWindowToUiMs);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ValidateRejectsHashMismatchAndReportsIncompleteProfiles()
    {
        string directory = CreateTempDirectory();
        try
        {
            var (artifact, imageBytes) = CreateArtifact();
            File.WriteAllBytes(Path.Combine(directory, artifact.ImageFile!), imageBytes);
            var invalid = artifact with { ImageSha256 = "00" };
            File.WriteAllText(Path.Combine(directory, "frame.json"),
                JsonSerializer.Serialize(invalid, LiveCaptureValidator.JsonOptions));

            var report = LiveCaptureValidator.Validate(directory,
                [new LiveValidationProfile("1600x1200", 1600, 1200)], minimumTrials: 1);

            Assert.Equal("FAIL", report.Status);
            Assert.Contains(report.InvalidArtifacts, error => error.Contains("mismatch", StringComparison.Ordinal));
            Assert.Contains("1600x1200", report.MissingProfiles);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ValidateCountsStructurallyValidFailureInSuccessDenominator()
    {
        string directory = CreateTempDirectory();
        try
        {
            var (success, imageBytes) = CreateArtifact();
            var failure = success with
            {
                ImageFile = "frame-failure.png",
                Timing = success.Timing! with
                {
                    Outcome = "no-tooltip",
                    RecognitionSucceeded = false,
                },
            };
            File.WriteAllBytes(Path.Combine(directory, success.ImageFile!), imageBytes);
            File.WriteAllBytes(Path.Combine(directory, failure.ImageFile!), imageBytes);
            File.WriteAllText(Path.Combine(directory, "frame.json"),
                JsonSerializer.Serialize(success, LiveCaptureValidator.JsonOptions));
            File.WriteAllText(Path.Combine(directory, "frame-failure.json"),
                JsonSerializer.Serialize(failure, LiveCaptureValidator.JsonOptions));

            var report = LiveCaptureValidator.Validate(directory,
                [new LiveValidationProfile("800x600", 800, 600)], minimumTrials: 2);

            Assert.Equal("FAIL", report.Status);
            var profile = Assert.Single(report.Profiles);
            Assert.Equal(2, profile.CaptureCount);
            Assert.Equal(2, profile.ValidTrialCount);
            Assert.Equal(1, profile.SuccessfulTrialCount);
            Assert.Equal(0.5, profile.RecognitionSuccessRate);
            Assert.Equal("FAIL", profile.Status);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    [Fact]
    public void ValidateTreatsRenderTimeoutAsInconclusiveWithoutFakeUiTimestamp()
    {
        string directory = CreateTempDirectory();
        try
        {
            var (artifact, imageBytes) = CreateArtifact();
            for (int i = 0; i < 30; i++)
            {
                var success = artifact with { ImageFile = $"frame-{i}.png" };
                File.WriteAllBytes(Path.Combine(directory, success.ImageFile!), imageBytes);
                File.WriteAllText(Path.Combine(directory, $"frame-{i}.json"),
                    JsonSerializer.Serialize(success, LiveCaptureValidator.JsonOptions));
            }

            var nonRendered = artifact with
            {
                ImageFile = "frame-timeout.png",
                Timing = artifact.Timing! with
                {
                    UiCommitTicks = null,
                    UiRendered = false,
                    CursorStoppedToUiMs = null,
                    StableWindowToUiMs = null,
                    CaptureToUiMs = null,
                    Outcome = "ui-timeout",
                    RecognitionSucceeded = false,
                },
            };
            File.WriteAllBytes(Path.Combine(directory, nonRendered.ImageFile!), imageBytes);
            File.WriteAllText(Path.Combine(directory, "frame-timeout.json"),
                JsonSerializer.Serialize(nonRendered, LiveCaptureValidator.JsonOptions));

            var report = LiveCaptureValidator.Validate(directory,
                [new LiveValidationProfile("800x600", 800, 600)], minimumTrials: 30);

            Assert.Equal("INCONCLUSIVE", report.Status);
            Assert.Equal(31, report.ArtifactCount);
            Assert.Equal(31, report.ValidArtifactCount);
            Assert.Empty(report.InvalidArtifacts);
            Assert.Contains("800x600", report.MissingProfiles);
            var profile = Assert.Single(report.Profiles);
            Assert.Equal(31, profile.CaptureCount);
            Assert.Equal(30, profile.ValidTrialCount);
            Assert.Equal(30, profile.SuccessfulTrialCount);
            Assert.Equal("INCONCLUSIVE", profile.Status);
        }
        finally
        {
            DeleteTempDirectory(directory);
        }
    }

    private static (LiveCaptureArtifact Artifact, byte[] ImageBytes) CreateArtifact()
    {
        byte[] imageBytes;
        using (var bitmap = new Bitmap(80, 60, PixelFormat.Format24bppRgb))
        using (var graphics = Graphics.FromImage(bitmap))
        using (var stream = new MemoryStream())
        {
            graphics.Clear(Color.FromArgb(32, 32, 32));
            graphics.FillRectangle(Brushes.White, 20, 20, 10, 10);
            bitmap.Save(stream, ImageFormat.Png);
            imageBytes = stream.ToArray();
        }

        var capture = new ScreenCaptureMetadata(
            SchemaVersion: 1,
            CaptureKind: "real-live",
            CaptureMethod: "gdi-cursor-region",
            CapturedAtUtc: DateTimeOffset.UtcNow,
            CaptureStartTicks: 300,
            CaptureEndTicks: 400,
            StopwatchFrequency: 1000,
            VirtualScreenPx: new ScreenRect(0, 0, 1920, 1080),
            SourceRectPx: new ScreenRect(100, 100, 80, 60),
            CursorScreenPx: new ScreenPoint(120, 120),
            CursorImagePx: new ScreenPoint(20, 20),
            CursorAfterCaptureScreenPx: new ScreenPoint(120, 120),
            CursorMovedDuringCapture: false,
            ProcessDpiAwareness: "PerMonitorV2",
            Monitors: [new MonitorCaptureMetadata("DISPLAY1",
                new ScreenRect(0, 0, 1920, 1080),
                new ScreenRect(0, 0, 1920, 1040), 96, 96)],
            ForegroundWindow: new WindowCaptureMetadata(
                Handle: 1,
                Title: "Dungeon & Fighter",
                ProcessId: 42,
                ProcessName: "game",
                ClientRectPx: new ScreenRect(0, 0, 800, 600),
                Dpi: 96));
        var timing = new LiveCaptureTiming(
            StableSinceTicks: 100,
            StableAtTicks: 200,
            CaptureStartTicks: 300,
            CaptureEndTicks: 400,
            RecognitionEndTicks: 500,
            UiCommitTicks: 600,
            UiRendered: true,
            CursorStoppedToUiMs: 500,
            StableWindowToUiMs: 400,
            CaptureToUiMs: 300,
            CursorPollIntervalMs: 150,
            RequiredStableMs: 350,
            Outcome: "recognized",
            RecognitionSucceeded: true,
            CursorMovedDuringCapture: false,
            Recognition: null);
        var artifact = new LiveCaptureArtifact(
            SchemaVersion: 1,
            CaptureKind: "real-live",
            ImageFile: "frame.png",
            ImageSha256: Convert.ToHexString(SHA256.HashData(imageBytes)),
            ImageWidth: 80,
            ImageHeight: 60,
            Capture: capture,
            Timing: timing,
            Rarity: "레어",
            Slot: "상의",
            MainStat: "힘",
            ObservedValue: 100);
        return (artifact, imageBytes);
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "DnfItemCheckerVision-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTempDirectory(string directory)
    {
        try { Directory.Delete(directory, recursive: true); }
        catch { /* test cleanup best-effort */ }
    }
}
