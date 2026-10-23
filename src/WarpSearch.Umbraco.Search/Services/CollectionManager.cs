using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpSearch.Umbraco.Search.Client;
using WarpSearch.Umbraco.Search.Configuration;

namespace WarpSearch.Umbraco.Search.Services;

public interface ICollectionManager
{
    /// <summary>
    /// Checks that the collection exists and the API key can use it. Returns false, and logs what to do, when it
    /// cannot.
    /// </summary>
    Task<bool> VerifyAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The provider never creates collections: that takes an admin key, while the provider only needs a writer key.
/// The collection is created once in the WarpSearch console.
/// </summary>
internal sealed class CollectionManager : ICollectionManager
{
    private readonly IWarpSearchClient _client;
    private readonly string _collection;
    private readonly ILogger<CollectionManager> _logger;

    public CollectionManager(IWarpSearchClient client, IOptions<WarpSearchOptions> options, ILogger<CollectionManager> logger)
    {
        _client = client;
        _collection = options.Value.CollectionName;
        _logger = logger;
    }

    public async Task<bool> VerifyAsync(CancellationToken cancellationToken = default)
    {
        // a key without a role on the collection sees 404, exactly as for a collection that does not exist
        CollectionInfo? collection = await _client.GetCollectionAsync(_collection, cancellationToken);
        if (collection is null)
        {
            _logger.LogError(
                "WarpSearch collection {Collection} was not found. Create it in the WarpSearch console (Collections > New collection, no mapping needed) and give the API key the writer role on it, or set WarpSearch:Collection to an existing collection.",
                _collection);
            return false;
        }

        if (collection.Permissions.Contains("write") is false)
        {
            _logger.LogError(
                "The WarpSearch API key cannot write to collection {Collection}. Use a key with the writer role on it.",
                _collection);
            return false;
        }

        _logger.LogInformation("Using WarpSearch collection {Collection}", _collection);
        return true;
    }
}
