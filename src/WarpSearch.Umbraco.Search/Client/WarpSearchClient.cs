using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WarpSearch.Umbraco.Search.Client;

internal sealed class WarpSearchClient : IWarpSearchClient
{
    private readonly HttpClient _httpClient;

    public WarpSearchClient(HttpClient httpClient)
        => _httpClient = httpClient;

    public async Task<CollectionInfo?> GetCollectionAsync(string collection, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(CollectionPath(collection), cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<CollectionInfo>(cancellationToken);
    }

    public async Task DeleteDocumentsAsync(string collection, IReadOnlyCollection<string> ids, bool commit, CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["ids"] = new JsonArray(ids.Select(JsonNode? (id) => id).ToArray()),
            ["commit"] = commit
        };

        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"{CollectionPath(collection)}/documents/_delete", body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<BulkFailure>> BulkAsync(string collection, IReadOnlyCollection<BulkAction> actions, bool refresh, CancellationToken cancellationToken = default)
    {
        if (actions.Count is 0)
        {
            return [];
        }

        var body = new StringBuilder();
        foreach (BulkAction action in actions)
        {
            var header = new JsonObject { [action.Document is null ? "delete" : "index"] = new JsonObject { ["_id"] = action.Id } };
            body.Append(header.ToJsonString()).Append('\n');
            if (action.Document is not null)
            {
                body.Append(action.Document.ToJsonString()).Append('\n');
            }
        }

        using var content = new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson");
        using HttpResponseMessage response = await _httpClient.PostAsync($"{EsPath(collection)}/_bulk{(refresh ? "?refresh=true" : string.Empty)}", content, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        JsonObject? result = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        if (result?["errors"]?.GetValue<bool>() is not true)
        {
            return [];
        }

        // each item is {"index": {...}} or {"delete": {...}} with a status and, when it failed, an error
        return (result["items"] as JsonArray ?? [])
            .Select(item => (item as JsonObject)?.FirstOrDefault().Value)
            .Where(item => item?["error"] is not null)
            .Select(item => new BulkFailure(
                item!["_id"]?.GetValue<string>(),
                item["error"]?["type"]?.GetValue<string>(),
                item["error"]?["reason"]?.GetValue<string>()))
            .ToArray();
    }

    public async Task RefreshAsync(string collection, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.PostAsync($"{CollectionPath(collection)}/refresh", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
    }

    public async Task<long> CountAsync(string collection, JsonObject query, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"{EsPath(collection)}/_count", new JsonObject { ["query"] = query }, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        JsonObject? result = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken);
        return result?["count"]?.GetValue<long>() ?? 0;
    }

    public async Task<JsonObject> SearchAsync(string collection, JsonObject body, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.PostAsJsonAsync($"{EsPath(collection)}/_search", body, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? new JsonObject();
    }

    public async Task<JsonObject> WhoAmIAsync(CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync("v1/auth/me", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? new JsonObject();
    }

    private static string CollectionPath(string collection)
        => $"v1/collections/{Uri.EscapeDataString(collection)}";

    private static string EsPath(string collection)
        => $"compat/es/{Uri.EscapeDataString(collection)}";

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await CreateExceptionAsync(response, cancellationToken);
    }

    // the native API answers RFC 9457 problem details, the Elasticsearch-compatible API answers {"error": {...}}
    private static async Task<WarpSearchException> CreateExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        string? code = null;
        string? requestId = null;
        string? detail = null;

        try
        {
            if (JsonNode.Parse(content) is JsonObject json)
            {
                if (json["error"] is JsonObject esError)
                {
                    code = esError["type"]?.GetValue<string>();
                    detail = esError["reason"]?.GetValue<string>();
                }
                else
                {
                    code = json["code"]?.GetValue<string>();
                    requestId = json["request_id"]?.GetValue<string>();
                    detail = json["detail"]?.GetValue<string>() ?? json["title"]?.GetValue<string>();
                }
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            // not JSON; fall back to the raw content
        }

        detail ??= content.Length > 500 ? content[..500] : content;
        var message = $"WarpSearch request {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} failed with {(int)response.StatusCode}"
                      + (code is null ? string.Empty : $" ({code})")
                      + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $": {detail}")
                      + (requestId is null ? string.Empty : $" [request {requestId}]");

        return new WarpSearchException(response.StatusCode, code, requestId, message);
    }
}
