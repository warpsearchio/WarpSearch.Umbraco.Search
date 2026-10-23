using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WarpSearch.Umbraco.Search.Client;

public sealed record CollectionInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// What the API key may do on the collection: monitor, read, write and admin.
    /// </summary>
    [JsonPropertyName("permissions")]
    public string[] Permissions { get; init; } = [];

    [JsonPropertyName("doc_count")]
    public long? DocCount { get; init; }

    [JsonPropertyName("stats_status")]
    public string? StatsStatus { get; init; }
}

/// <summary>
/// Indexes <see cref="Document"/> under <see cref="Id"/>, or deletes <see cref="Id"/> when there is no document.
/// </summary>
public sealed record BulkAction(string Id, JsonObject? Document);

public sealed record BulkFailure(string? Id, string? Code, string? Message);
