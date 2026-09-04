using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DnfItemChecker.App.Settings;

/// <summary>
/// Runtime configuration resolved at startup. The Neople API key comes from a
/// <c>config.json</c> next to the executable (<c>{ "apiKey": "..." }</c>) or, failing that,
/// the <c>NEOPLE_API_KEY</c> environment variable. The local SQLite cache lives next to the exe.
/// </summary>
public sealed class AppSettings
{
    public required string ApiKey { get; init; }

    public required string DbPath { get; init; }

    public required string StatTablePath { get; init; }

    public bool HasApiKey => !string.IsNullOrWhiteSpace(ApiKey);

    public static AppSettings Load()
    {
        // The exe folder — NOT AppContext.BaseDirectory, which for a single-file publish is the temp
        // extraction dir (so config.json/dnfitems.db/stattable.json sit beside the temp DLLs, never
        // found). Environment.ProcessPath is the launched exe, so config/db/table resolve next to it.
        var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        var apiKey = ReadConfigApiKey(Path.Combine(baseDir, "config.json"))
            ?? Environment.GetEnvironmentVariable("NEOPLE_API_KEY")
            ?? string.Empty;

        return new AppSettings
        {
            ApiKey = apiKey.Trim(),
            DbPath = Path.Combine(baseDir, "dnfitems.db"),
            StatTablePath = Path.Combine(baseDir, "stattable.json"),
        };
    }

    private static string? ReadConfigApiKey(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            var config = JsonSerializer.Deserialize<ConfigFile>(stream);
            return string.IsNullOrWhiteSpace(config?.ApiKey) ? null : config!.ApiKey;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private sealed record ConfigFile(
        [property: JsonPropertyName("apiKey")] string? ApiKey);
}
