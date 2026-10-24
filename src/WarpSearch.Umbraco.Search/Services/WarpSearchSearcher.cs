using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using Umbraco.Cms.Search.Core.Services;
using Microsoft.Extensions.Options;
using WarpSearch.Umbraco.Search.Client;
using WarpSearch.Umbraco.Search.Configuration;
using static WarpSearch.Umbraco.Search.WarpSearchConstants;

namespace WarpSearch.Umbraco.Search.Services;

/// <summary>
/// The WarpSearch searcher. Registering indexes with this interface (rather than <see cref="ISearcher"/>) binds them
/// to WarpSearch regardless of the default searcher.
/// </summary>
public interface IWarpSearchSearcher : ISearcher
{
}

internal sealed class WarpSearchSearcher : IWarpSearchSearcher
{
    private readonly IWarpSearchClient _client;
    private readonly string _collection;
    private readonly SearchRequestBuilder _requestBuilder;
    private readonly ILogger<WarpSearchSearcher> _logger;

    public WarpSearchSearcher(
        IWarpSearchClient client,
        IOptions<WarpSearchOptions> options,
        SearchRequestBuilder requestBuilder,
        ILogger<WarpSearchSearcher> logger)
    {
        _client = client;
        _collection = options.Value.CollectionName;
        _requestBuilder = requestBuilder;
        _logger = logger;
    }

    public async Task<SearchResult> SearchAsync(
        string indexAlias,
        string? query = null,
        IEnumerable<Filter>? filters = null,
        IEnumerable<Facet>? facets = null,
        IEnumerable<Sorter>? sorters = null,
        string? culture = null,
        string? segment = null,
        AccessContext? accessContext = null,
        int skip = 0,
        int take = 10,
        int maxSuggestions = 0)
    {
        // nothing to search for
        if (query is null && filters is null && facets is null && sorters is null)
        {
            return new SearchResult(0, [], []);
        }

        Filter[] filtersAsArray = filters?.ToArray() ?? [];

        // duplicate facets would produce duplicate aggregation names
        Facet[] facetsAsArray = facets?
            .GroupBy(SearchRequestBuilder.FacetName)
            .Select(group => group.First())
            .ToArray() ?? [];

        JsonObject body = _requestBuilder.Build(
            indexAlias,
            query,
            filtersAsArray,
            facetsAsArray,
            sorters?.ToArray(),
            culture,
            string.IsNullOrWhiteSpace(segment) ? null : segment,
            accessContext,
            skip,
            take);

        JsonObject response;
        try
        {
            response = await _client.SearchAsync(_collection, body);
        }
        catch (Exception exception) when (WarpSearchException.IsRequestFailure(exception))
        {
            _logger.LogError(exception, "WarpSearch search of index {IndexAlias} in {Collection} failed", indexAlias, _collection);
            return new SearchResult(0, [], []);
        }

        var total = response["hits"]?["total"]?["value"]?.GetValue<long>() ?? 0;
        Document[] documents = (response["hits"]?["hits"] as JsonArray ?? [])
            .Select(ExtractDocument)
            .OfType<Document>()
            .ToArray();
        FacetResult[] facetResults = facetsAsArray
            .Select(facet => ExtractFacetResult(facet, response["aggregations"] as JsonObject))
            .OfType<FacetResult>()
            .ToArray();

        // WarpSearch has no suggesters yet, so maxSuggestions is not supported
        return new SearchResult(total, documents, facetResults);
    }

    private Document? ExtractDocument(JsonNode? hit)
    {
        // the document id is "{index}.{key}.{culture}"; the key is also stored as a field
        var keyValue = hit?["_source"]?[SystemFields.Key]?.GetValue<string>()
                       ?? hit?["_id"]?.GetValue<string>()?.Split('.')[^2];
        if (Guid.TryParse(keyValue, out Guid key) is false)
        {
            _logger.LogWarning("WarpSearch returned a hit without a document key: {Hit}", hit?.ToJsonString());
            return null;
        }

        UmbracoObjectTypes objectType = Enum.TryParse(hit?["_source"]?[SystemFields.ObjectType]?.GetValue<string>(), out UmbracoObjectTypes parsed)
            ? parsed
            : UmbracoObjectTypes.Unknown;

        return new Document(key, objectType);
    }

    private FacetResult? ExtractFacetResult(Facet facet, JsonObject? aggregations)
    {
        var name = SearchRequestBuilder.FacetName(facet);
        JsonNode? aggregation = aggregations?[name];

        // facets counted with other facets' filters are wrapped in a filter aggregation
        if (aggregation?[name] is JsonObject inner)
        {
            aggregation = inner;
        }

        if (aggregation?["buckets"] is not JsonArray buckets)
        {
            _logger.LogWarning("WarpSearch returned no buckets for facet {FacetName}", facet.FieldName);
            return null;
        }

        IEnumerable<FacetValue> values = facet switch
        {
            KeywordFacet => buckets.Select(bucket => (FacetValue)new KeywordFacetValue(KeyString(bucket), DocCount(bucket))),
            IntegerExactFacet => buckets.Select(bucket => (FacetValue)new IntegerExactFacetValue((int)KeyNumber(bucket), DocCount(bucket))),
            DecimalExactFacet => buckets.Select(bucket => (FacetValue)new DecimalExactFacetValue(
                decimal.TryParse(KeyString(bucket), NumberStyles.Float, CultureInfo.InvariantCulture, out var key) ? key : 0,
                DocCount(bucket))),
            DateTimeOffsetExactFacet => buckets.Select(bucket => (FacetValue)new DateTimeOffsetExactFacetValue(KeyDate(bucket), DocCount(bucket))),
            IntegerRangeFacet => buckets.Select(bucket => (FacetValue)new IntegerRangeFacetValue(
                KeyString(bucket),
                Number(bucket?["from"]) is { } from ? (int)from : null,
                Number(bucket?["to"]) is { } to ? (int)to : null,
                DocCount(bucket))),
            DecimalRangeFacet => buckets.Select(bucket => (FacetValue)new DecimalRangeFacetValue(
                KeyString(bucket),
                Number(bucket?["from"]) is { } from ? (decimal)from : null,
                Number(bucket?["to"]) is { } to ? (decimal)to : null,
                DocCount(bucket))),
            DateTimeOffsetRangeFacet => buckets.Select(bucket => (FacetValue)new DateTimeOffsetRangeFacetValue(
                KeyString(bucket),
                Date(bucket?["from"]),
                Date(bucket?["to"]),
                DocCount(bucket))),
            _ => throw new NotSupportedException($"The facet type {facet.GetType().Name} is not supported by WarpSearch.")
        };

        return new FacetResult(facet.FieldName, values.ToArray());
    }

    private static long DocCount(JsonNode? bucket)
        => bucket?["doc_count"]?.GetValue<long>() ?? 0;

    private static string KeyString(JsonNode? bucket)
        => bucket?["key"] switch
        {
            JsonValue value when value.GetValueKind() is JsonValueKind.String => value.GetValue<string>(),
            JsonValue value => value.ToJsonString(),
            _ => string.Empty
        };

    private static double KeyNumber(JsonNode? bucket)
        => Number(bucket?["key"]) ?? 0;

    // dates are bucketed by epoch milliseconds
    private static DateTimeOffset KeyDate(JsonNode? bucket)
        => Date(bucket?["key"]) ?? DateTimeOffset.MinValue;

    private static double? Number(JsonNode? node)
        => node switch
        {
            JsonValue value when value.GetValueKind() is JsonValueKind.Number => value.GetValue<double>(),
            JsonValue value when value.GetValueKind() is JsonValueKind.String
                                 && double.TryParse(value.GetValue<string>(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };

    private static DateTimeOffset? Date(JsonNode? node)
        => node switch
        {
            JsonValue value when value.GetValueKind() is JsonValueKind.Number => DateTimeOffset.FromUnixTimeMilliseconds((long)value.GetValue<double>()),
            JsonValue value when value.GetValueKind() is JsonValueKind.String
                                 && DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsed) => parsed,
            _ => null
        };
}
