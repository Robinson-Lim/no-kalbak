using System.IO;
using System.Windows;
using DnfItemChecker.App.Settings;
using DnfItemChecker.App.State;
using DnfItemChecker.App.ViewModels;
using DnfItemChecker.Core.Api;
using DnfItemChecker.Core.Comparison;
using DnfItemChecker.Core.Data;
using DnfItemChecker.Core.Ocr;
using DnfItemChecker.Core.Stats;
using DnfItemChecker.Vision;
using Microsoft.Extensions.DependencyInjection;

namespace DnfItemChecker.App;

/// <summary>
/// Composition root. Wires the Core/Vision services, the shared <see cref="AppState"/> and the
/// four tab view models into a DI container, then shows the shell window.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Surface any unhandled UI/background exception instead of silently freezing/closing, and record
        // the full stack to error.log next to the exe for post-mortem diagnosis.
        DispatcherUnhandledException += (_, args) =>
        {
            LogError("Dispatcher", args.Exception);
            MessageBox.Show($"예기치 못한 오류:\n\n{args.Exception.Message}", "오류",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                LogError("AppDomain", ex);
                MessageBox.Show($"치명적 오류:\n\n{ex.Message}", "오류",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
        var settings = AppSettings.Load();
        if (!settings.HasApiKey)
        {
            MessageBox.Show(
                "Neople API 키가 없습니다.\n\n실행 파일 옆 config.json에 {\"apiKey\":\"...\"} 형태로 키를 넣거나 " +
                "NEOPLE_API_KEY 환경 변수를 설정하세요. (config.json.sample 참고)",
                "DNF 아이템 등급 판별기", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown(1);
            return;
        }

        var collection = new ServiceCollection();
        ConfigureServices(collection, settings);
        _services = collection.BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        window.Show();

        // Preload the ONNX value-reader off the UI thread (loads 3 models, ~1-2s) AND run one dummy
        // inference through both engines: the first inference pays a one-time warm-up (session allocs,
        // JIT — bench: cold first ≈ +500ms) that would otherwise land on the first real hover. Its init
        // result is recorded in onnx_init.log next to the exe.
        _ = Task.Run(async () =>
        {
            var onnx = _services!.GetRequiredService<OnnxOcrService>();
            var winrt = _services.GetRequiredService<IOcrService>();
            var dummy = WarmupImage();
            await winrt.RecognizeAsync(dummy);
            await onnx.RecognizeAsync(dummy);
        });

        // Load the (부위 × 레어리티) stat table (seeds defaults on first run).
        var statTable = _services.GetRequiredService<IStatTable>();
        try
        {
            await statTable.LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"능력치 표 로딩 실패: {ex.Message}", "경고",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        await _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    // Small dark tile with tooltip-like text so the warm-up exercises the full pipeline
    // (detection AND recognition; a blank image would skip the recognizer).
    private static byte[] WarmupImage()
    {
        using var bmp = new System.Drawing.Bitmap(200, 80);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.Black);
            using var font = new System.Drawing.Font("맑은 고딕", 12);
            g.DrawString("최상급(100%) 123", font, System.Drawing.Brushes.White, 8, 28);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        return ms.ToArray();
    }

    private static void LogError(string source, Exception ex)
    {
        try
        {
            var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:HH:mm:ss}] {source}: {ex}\n\n");
        }
        catch { /* best-effort */ }
    }

    private static void ConfigureServices(IServiceCollection services, AppSettings settings)
    {
        services.AddSingleton(settings);

        // Core / API
        services.AddSingleton(_ => new NeopleApiClient(settings.ApiKey));
        services.AddSingleton<IStatTable>(_ => new JsonStatTable(settings.StatTablePath));
        services.AddSingleton<IRosterStore>(_ => new SqliteRosterStore(settings.DbPath));
        services.AddSingleton<IEquippedStatStore>(_ => new SqliteEquippedStatStore(settings.DbPath));
        services.AddSingleton<IMainStatResolver, JobMainStatResolver>();
        services.AddSingleton<ITooltipParser, TooltipParser>();
        services.AddSingleton<IComparisonEngine, ComparisonEngine>();
        // Vision (Windows-only). WinRT (Windows.Media.Ocr) reads the small gray 부위/등급 labels + locates
        // the tooltip; the ONNX PP-OCRv5 engine then reads the tiny pixel-font stat digits + quality% on
        // the *tooltip crop* (not the full capture) — fast and accurate. See TooltipRecognizer.
        services.AddSingleton<IOcrService, WindowsOcrService>();
        // Models live in a `models/` folder next to the exe. Use the exe directory (not
        // AppContext.BaseDirectory, which is the single-file extraction temp dir).
        var modelsDir = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "models");
        services.AddSingleton(_ => new OnnxOcrService(modelsDir));
        services.AddSingleton<IScreenCaptureService, GdiScreenCaptureService>();
        services.AddSingleton<IRarityColorReader, RarityColorReader>();
        services.AddSingleton<ITooltipRecognizer>(sp => new TooltipRecognizer(
            sp.GetRequiredService<IOcrService>(),
            sp.GetRequiredService<ITooltipParser>(),
            sp.GetRequiredService<IRarityColorReader>(),
            sp.GetRequiredService<OnnxOcrService>()));

        // App state + view models
        services.AddSingleton<AppState>();
        services.AddSingleton<CharacterSelectionService>();
        services.AddSingleton<CharacterTabViewModel>();
        services.AddSingleton<EquipmentTabViewModel>();
        services.AddSingleton<InGameTabViewModel>();
        services.AddSingleton<StatTableTabViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}
