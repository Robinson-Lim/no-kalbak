using System.Drawing;
using System.Drawing.Imaging;
using DnfItemChecker.Core.Ocr;

namespace DnfItemChecker.Vision.Tests;

[CollectionDefinition("Native OCR", DisableParallelization = true)]
public sealed class NativeOcrCollection;

// Exercise real native engines: mocks cannot detect access to disposed SKBitmap memory.
// Requires Windows OCR and the bundled models; input is generated, not private captures.
[Collection("Native OCR")]
[Trait("Category", "NativeOcr")]
public sealed class OcrConcurrencyTests
{
    [Fact]
    public async Task WindowsOcrOverlappingCallsAndCancellationLeaveEngineUsable()
    {
        var service = new WindowsOcrService();
        Assert.True(service.IsAvailable, "Install the Windows OCR language component.");
        var image = CreateTextImage();
        Assert.NotEmpty((await service.RecognizeAsync(image)).Lines);

        using var cancelled = new CancellationTokenSource();
        var pending = Enumerable.Range(0, 16)
            .Select(i => service.RecognizeAsync(image, i % 2 == 0 ? cancelled.Token : default))
            .ToArray();
        cancelled.Cancel();
        var outcomes = await Task.WhenAll(pending.Select(ObserveCancellationAsync));
        Assert.Contains(false, outcomes);
        Assert.Equal(8, outcomes.Count(success => success));
        Assert.NotEmpty((await service.RecognizeAsync(image)).Lines);
    }

    [Fact]
    public async Task OnnxRepeatedMidFlightCancellationAndRetryDoNotFreeActiveCrops()
    {
        using var service = CreateOnnx();
        var image = CreateTextImage();
        Assert.NotEmpty((await service.RecognizeLinesAsync(image)).Lines);

        var cancellations = 0;
        for (var attempt = 0; attempt < 32; attempt++)
        {
            using var cts = new CancellationTokenSource();
            var cancelled = service.RecognizeLinesAsync(image, ct: cts.Token);
            // Vary the cancellation point across segmentation and native recognition.
            cts.CancelAfter(10 + attempt % 8 * 10);
            var retry = service.RecognizeLinesAsync(image);
            if (!await ObserveCancellationAsync(cancelled)) cancellations++;
            Assert.NotEmpty((await retry).Lines);
        }
        Assert.True(cancellations > 0, "The scenario must actually cancel active work.");
        Assert.NotEmpty((await service.RecognizeAsync(image)).Lines);
    }

    [Fact]
    public async Task OnnxDisposeWaitsForActiveInferenceAndRejectsLaterCalls()
    {
        var service = CreateOnnx();
        try
        {
            var image = CreateTextImage();
            Assert.NotEmpty((await service.RecognizeLinesAsync(image)).Lines);
            var active = service.RecognizeLinesAsync(image);
            var dispose = Task.Run(service.Dispose);
            Assert.NotEmpty((await active).Lines);
            await dispose;
            Assert.False(service.IsAvailable);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RecognizeLinesAsync(image));
            await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RecognizeAsync(image));
        }
        finally { service.Dispose(); }
    }

    [Fact]
    public async Task OnnxDisposeDuringInitializationDoesNotPublishDisposedEngines()
    {
        var service = CreateOnnx();
        await Task.Run(service.Dispose);
        Assert.False(service.IsAvailable);
        Assert.False(service.SupportsLineRecognition);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => service.RecognizeAsync(CreateTextImage()));
        service.Dispose();
    }

    private static OnnxOcrService CreateOnnx()
        => new(Path.Combine(AppContext.BaseDirectory, "models"));

    private static async Task<bool> ObserveCancellationAsync(Task<OcrResult> operation)
    {
        try
        {
            Assert.NotEmpty((await operation).Lines);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }

    private static byte[] CreateTextImage()
    {
        using var bitmap = new Bitmap(340, 650);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var font = new Font("Malgun Gothic", 13, GraphicsUnit.Pixel))
        {
            graphics.Clear(Color.Black);
            for (var row = 0; row < 24; row++)
                graphics.DrawString($"지능 {149 + row} 1234567890", font, Brushes.White, 12, 8 + row * 26);
        }
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
