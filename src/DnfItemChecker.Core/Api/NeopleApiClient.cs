using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DnfItemChecker.Core.Models;

namespace DnfItemChecker.Core.Api;

/// <summary>
/// Thin async client over the Neople Dungeon &amp; Fighter Open API.
/// One <see cref="HttpClient"/> is reused for the client lifetime; the API key is
/// appended to every request as the <c>apikey</c> query parameter.
/// </summary>
public sealed class NeopleApiClient : IDisposable
{
    private const string BaseUrl = "https://api.neople.co.kr/df/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly bool _ownsHttp;

    public NeopleApiClient(string apiKey, HttpClient? http = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException("API key is required.", nameof(apiKey));
        _apiKey = apiKey;
        _ownsHttp = http is null;
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri(BaseUrl);
    }

    // ---- Servers ----------------------------------------------------------

    public async Task<IReadOnlyList<DfServer>> GetServersAsync(CancellationToken ct = default)
        => (await GetAsync<RowList<DfServer>>("servers", ct)).Rows ?? [];

    // ---- Characters -------------------------------------------------------

    /// <param name="serverId">A server id, or "all" for a cross-server search.</param>
    public async Task<IReadOnlyList<DfCharacterSearchRow>> SearchCharactersAsync(
        string serverId, string characterName, string wordType = "match", int limit = 10, CancellationToken ct = default)
    {
        var url = $"servers/{Enc(serverId)}/characters?characterName={Enc(characterName)}&wordType={wordType}&limit={limit}";
        return (await GetAsync<RowList<DfCharacterSearchRow>>(url, ct)).Rows ?? [];
    }

    public Task<DfCharacterInfo> GetCharacterAsync(string serverId, string characterId, CancellationToken ct = default)
        => GetAsync<DfCharacterInfo>($"servers/{Enc(serverId)}/characters/{Enc(characterId)}", ct);

    public Task<DfEquipmentResponse> GetEquipmentAsync(string serverId, string characterId, CancellationToken ct = default)
        => GetAsync<DfEquipmentResponse>($"servers/{Enc(serverId)}/characters/{Enc(characterId)}/equip/equipment", ct);

    public Task<DfStatusResponse> GetStatusAsync(string serverId, string characterId, CancellationToken ct = default)
        => GetAsync<DfStatusResponse>($"servers/{Enc(serverId)}/characters/{Enc(characterId)}/status", ct);

    // ---- Items ------------------------------------------------------------

    public async Task<IReadOnlyList<DfItemSearchRow>> SearchItemsAsync(
        string itemName, string wordType = "match", int limit = 10,
        int? minLevel = null, int? maxLevel = null, string? rarity = null, CancellationToken ct = default)
    {
        var url = new StringBuilder($"items?itemName={Enc(itemName)}&wordType={wordType}&limit={limit}");
        var q = BuildItemQuery(minLevel, maxLevel, rarity);
        if (q.Length > 0) url.Append("&q=").Append(q);
        return (await GetAsync<RowList<DfItemSearchRow>>(url.ToString(), ct)).Rows ?? [];
    }

    public Task<DfItemDetail> GetItemAsync(string itemId, CancellationToken ct = default)
        => GetAsync<DfItemDetail>($"items/{Enc(itemId)}", ct);

    /// <summary>Batch detail lookup (max 15 ids per call, enforced by the API).</summary>
    public async Task<IReadOnlyList<DfItemDetail>> GetMultiItemsAsync(IEnumerable<string> itemIds, CancellationToken ct = default)
    {
        var ids = string.Join(",", itemIds);
        if (ids.Length == 0) return [];
        return (await GetAsync<RowList<DfItemDetail>>($"multi/items?itemIds={Enc(ids)}", ct)).Rows ?? [];
    }

    // ---- Set items --------------------------------------------------------

    public async Task<IReadOnlyList<DfSetItemRow>> SearchSetItemsAsync(
        string setItemName, string wordType = "match", int limit = 10, CancellationToken ct = default)
        => (await GetAsync<RowList<DfSetItemRow>>(
            $"setitems?setItemName={Enc(setItemName)}&wordType={wordType}&limit={limit}", ct)).Rows ?? [];

    // ---- Jobs -------------------------------------------------------------

    public async Task<IReadOnlyList<DfJob>> GetJobsAsync(CancellationToken ct = default)
        => (await GetAsync<RowList<DfJob>>("jobs", ct)).Rows ?? [];

    // ---- Plumbing ---------------------------------------------------------

    private static string BuildItemQuery(int? minLevel, int? maxLevel, string? rarity)
    {
        var parts = new List<string>(3);
        if (minLevel is int lo) parts.Add($"minLevel:{lo}");
        if (maxLevel is int hi) parts.Add($"maxLevel:{hi}");
        if (!string.IsNullOrEmpty(rarity)) parts.Add($"rarity:{rarity}");
        return parts.Count == 0 ? "" : Enc(string.Join(",", parts));
    }

    private static string Enc(string value) => Uri.EscapeDataString(value);

    private string WithKey(string relativeUrl)
        => relativeUrl + (relativeUrl.Contains('?') ? '&' : '?') + "apikey=" + _apiKey;

    private async Task<T> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var response = await _http.GetAsync(WithKey(relativeUrl), ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw await BuildExceptionAsync(response, ct).ConfigureAwait(false);

        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false);
        return value ?? throw new NeopleApiException((int)response.StatusCode, null, "Empty response body.");
    }

    private static async Task<NeopleApiException> BuildExceptionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var code = err.TryGetProperty("code", out var c) ? c.GetString() : null;
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : response.ReasonPhrase;
                return new NeopleApiException(status, code, msg ?? "Neople API error.");
            }
        }
        catch (JsonException) { /* fall through to generic */ }
        return new NeopleApiException(status, null, response.ReasonPhrase ?? "Neople API error.");
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
