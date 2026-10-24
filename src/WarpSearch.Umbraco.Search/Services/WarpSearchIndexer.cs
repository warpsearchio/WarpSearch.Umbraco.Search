using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Services;
using WarpSearch.Umbraco.Search.Client;
using WarpSearch.Umbraco.Search.Configuration;
using static WarpSearch.Umbraco.Search.WarpSearchConstants;
using CoreConstants = Umbraco.Cms.Search.Core.Constants;

namespace WarpSearch.Umbraco.Search.Services;

/// <summary>
/// The WarpSearch indexer. Registering indexes with this interface (rather than <see cref="IIndexer"/>) binds them
/// to WarpSearch regardless of the default indexer.
/// </summary>
public interface IWarpSearchIndexer : IIndexer
{
}

/// <summary>
/// Keeps every index in one WarpSearch collection; each document records its index.
/// </summary>
internal sealed class WarpSearchIndexer : IWarpSearchIndexer
{
    private readonly IWarpSearchClient _client;
    private readonly IndexWriter _writer;
    private readonly DocumentBuilder _documentBuilder;
    private readonly IIndexCultureProvider _cultureProvider;
    private readonly IServerRoleAccessor _serverRoleAccessor;
    private readonly IndexerOptions _options;
    private readonly string _collection;
    private readonly ILogger<WarpSearchIndexer> _logger;

    public WarpSearchIndexer(
        IWarpSearchClient client,
        IndexWriter writer,
        DocumentBuilder documentBuilder,
        IIndexCultureProvider cultureProvider,
        IServerRoleAccessor serverRoleAccessor,
        IOptions<IndexerOptions> options,
        IOptions<WarpSearchOptions> warpSearchOptions,
        ILogger<WarpSearchIndexer> logger)
    {
        _client = client;
        _writer = writer;
        _documentBuilder = documentBuilder;
        _cultureProvider = cultureProvider;
        _serverRoleAccessor = serverRoleAccessor;
        _options = options.Value;
        _collection = warpSearchOptions.Value.CollectionName;
        _logger = logger;
    }

    public async Task AddOrUpdateAsync(
        string indexAlias,
        Guid id,
        UmbracoObjectTypes objectType,
        IEnumerable<Variation> variations,
        IEnumerable<IndexField> fields,
        ContentProtection? protection)
    {
        if (ShouldNotManipulateIndexes())
        {
            return;
        }

        (string Id, JsonObject Document)[] documents = _documentBuilder.Build(indexAlias, id, objectType, variations, fields, protection);
        var actions = documents.Select(document => new BulkAction(document.Id, document.Document)).ToList();

        // remove the cultures that are no longer there, e.g. a culture that was unpublished; deleting an id that
        // does not exist is a no-op, and the deletes go in the same request as the writes. A rebuild starts from an
        // empty index, so there is nothing to remove then.
        if (_writer.IsRebuilding(indexAlias) is false)
        {
            var documentIds = documents.Select(document => document.Id).ToHashSet();
            IEnumerable<string?> cultures = (await _cultureProvider.GetCulturesAsync()).Append(null);
            actions.AddRange(cultures
                .Select(culture => DocumentBuilder.DocumentId(indexAlias, id, culture))
                .Distinct()
                .Where(documentId => documentIds.Contains(documentId) is false)
                .Select(documentId => new BulkAction(documentId, null)));
        }

        await _writer.WriteAsync(indexAlias, actions);
    }

    public async Task DeleteAsync(string indexAlias, IEnumerable<Guid> ids)
    {
        if (ShouldNotManipulateIndexes())
        {
            return;
        }

        var keys = ids.Select(id => id.ToString("D")).ToArray();
        if (keys.Length is 0)
        {
            return;
        }

        // the documents and their descendants
        await _writer.FlushAsync(indexAlias);
        await DeleteMatchingAsync(indexAlias, new JsonObject
        {
            ["bool"] = new JsonObject
            {
                ["should"] = new JsonArray(
                    TermsQuery(SystemFields.Key, keys),
                    TermsQuery(FieldNames.Field(CoreConstants.FieldNames.PathIds, FieldKinds.Keywords), keys)),
                ["minimum_should_match"] = 1
            }
        });
    }

    public async Task ResetAsync(string indexAlias)
    {
        if (ShouldNotManipulateIndexes())
        {
            return;
        }

        // other indexes share the collection, so the index's documents are deleted rather than the collection
        await DeleteMatchingAsync(indexAlias, new JsonObject { ["match_all"] = new JsonObject() });

        // Umbraco rebuilds the index right after resetting it
        _writer.BeginRebuild(indexAlias);
    }

    public async Task<IndexMetadata> GetMetadataAsync(string indexAlias)
    {
        try
        {
            var count = await _client.CountAsync(_collection, IndexQuery(indexAlias, new JsonObject { ["match_all"] = new JsonObject() }));
            HealthStatus status = _writer.IsRebuilding(indexAlias)
                ? HealthStatus.Rebuilding
                : count > 0 ? HealthStatus.Healthy : HealthStatus.Empty;
            return new IndexMetadata(count, status, ProviderName);
        }
        catch (Exception exception) when (WarpSearchException.IsRequestFailure(exception))
        {
            _logger.LogWarning(exception, "Could not count the documents of index {IndexAlias} in WarpSearch collection {Collection}", indexAlias, _collection);
            return new IndexMetadata(0, HealthStatus.Unknown, ProviderName);
        }
    }

    // WarpSearch has no delete by query: find a page of matching documents, delete them, and repeat until none are
    // left (always from the top, since each round removes what it found)
    private async Task DeleteMatchingAsync(string indexAlias, JsonObject query)
    {
        var deleted = new HashSet<string>();
        while (true)
        {
            var body = new JsonObject
            {
                ["query"] = IndexQuery(indexAlias, query.DeepClone()),
                ["size"] = _options.DeleteLookupPageSize,
                ["_source"] = false,
                ["track_total_hits"] = false
            };

            JsonObject response = await _client.SearchAsync(_collection, body);
            var documentIds = (response["hits"]?["hits"] as JsonArray ?? [])
                .Select(hit => hit?["_id"]?.GetValue<string>())
                .OfType<string>()
                .ToArray();
            if (documentIds.Length is 0)
            {
                return;
            }

            // guards against looping forever if deletes do not become visible
            if (documentIds.All(deleted.Contains))
            {
                _logger.LogWarning("WarpSearch still returns {Count} deleted documents of index {IndexAlias}; giving up", documentIds.Length, indexAlias);
                return;
            }

            await _client.DeleteDocumentsAsync(_collection, documentIds, commit: true);
            deleted.UnionWith(documentIds);
        }
    }

    private static JsonObject IndexQuery(string indexAlias, JsonNode query)
        => new()
        {
            ["bool"] = new JsonObject
            {
                ["filter"] = new JsonArray(TermsQuery(SystemFields.Index, [indexAlias])),
                ["must"] = new JsonArray(query)
            }
        };

    private static JsonObject TermsQuery(string field, IEnumerable<string> values)
        => new() { ["terms"] = new JsonObject { [field] = new JsonArray(values.Select(value => (JsonNode?)value).ToArray()) } };

    private bool ShouldNotManipulateIndexes()
        => _serverRoleAccessor.CurrentServerRole is ServerRole.Subscriber;
}
