using System.Text.Json.Nodes;

namespace WarpSearch.Umbraco.Search.Client;

/// <summary>
/// A thin client for the parts of the WarpSearch API the provider uses.
/// </summary>
public interface IWarpSearchClient
{
    Task<CollectionInfo?> GetCollectionAsync(string collection, CancellationToken cancellationToken = default);

    Task DeleteDocumentsAsync(string collection, IReadOnlyCollection<string> ids, bool commit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes and deletes documents in one request through the Elasticsearch-compatible _bulk API. Returns the
    /// documents that failed.
    /// </summary>
    Task<IReadOnlyList<BulkFailure>> BulkAsync(string collection, IReadOnlyCollection<BulkAction> actions, bool refresh, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes pending writes searchable.
    /// </summary>
    Task RefreshAsync(string collection, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the documents matching an Elasticsearch Query DSL query.
    /// </summary>
    Task<long> CountAsync(string collection, JsonObject query, CancellationToken cancellationToken = default);

    /// <summary>
    /// Runs an Elasticsearch Query DSL search through WarpSearch's Elasticsearch-compatible API.
    /// </summary>
    Task<JsonObject> SearchAsync(string collection, JsonObject body, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns who the API key belongs to, or throws when it is not accepted.
    /// </summary>
    Task<JsonObject> WhoAmIAsync(CancellationToken cancellationToken = default);
}
