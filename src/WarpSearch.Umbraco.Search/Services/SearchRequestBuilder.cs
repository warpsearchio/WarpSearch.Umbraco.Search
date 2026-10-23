using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;
using WarpSearch.Umbraco.Search.Configuration;
using static WarpSearch.Umbraco.Search.WarpSearchConstants;
using UmbracoDirection = Umbraco.Cms.Core.Direction;

namespace WarpSearch.Umbraco.Search.Services;

/// <summary>
/// Translates an Umbraco Search request to an Elasticsearch Query DSL body for WarpSearch's
/// Elasticsearch-compatible search API.
/// </summary>
internal sealed class SearchRequestBuilder
{
    private readonly FieldNames _fieldNames;
    private readonly SearcherOptions _options;

    public SearchRequestBuilder(FieldNames fieldNames, IOptions<SearcherOptions> options)
    {
        _fieldNames = fieldNames;
        _options = options.Value;
    }

    public static string FacetName(Facet facet)
        => $"{facet.FieldName}_{facet.GetType().Name}";

    public JsonObject Build(
        string indexAlias,
        string? query,
        Filter[] filters,
        Facet[] facets,
        Sorter[]? sorters,
        string? culture,
        string? segment,
        AccessContext? accessContext,
        int skip,
        int take)
    {
        var filterClauses = new JsonArray
        {
            Terms(SystemFields.Index, [indexAlias]),
            Terms(SystemFields.Culture, culture is null
                ? [Cultures.InvariantCulture]
                : [DocumentBuilder.IndexCulture(culture), Cultures.InvariantCulture])
        };

        // protected content is visible to the members and member groups in its access list; unprotected content
        // carries the empty key
        if (accessContext?.Bypass is not true)
        {
            Guid[] accessKeys = accessContext is null
                ? [Guid.Empty]
                : new[] { Guid.Empty, accessContext.PrincipalId }.Union(accessContext.GroupIds ?? []).ToArray();
            filterClauses.Add(Terms(SystemFields.AccessKeys, accessKeys.Select(key => key.ToString("D"))));
        }

        // filters on faceted fields are applied after the facets are counted, so a facet keeps showing its other
        // values while one is selected; each facet is counted with the filters of the other facets
        var facetFieldNames = facets.Select(facet => facet.FieldName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Filter[] facetFilters = filters.Where(filter => facetFieldNames.Contains(filter.FieldName)).ToArray();
        Filter[] regularFilters = filters.Except(facetFilters).ToArray();

        foreach (Filter filter in regularFilters.Where(filter => filter.Negate is false))
        {
            filterClauses.Add(FilterClause(filter, segment));
        }

        var queryBool = new JsonObject
        {
            ["filter"] = filterClauses,
            ["must_not"] = new JsonArray(regularFilters.Where(filter => filter.Negate).Select(filter => (JsonNode?)FilterClause(filter, segment)).ToArray())
        };

        if (string.IsNullOrWhiteSpace(query) is false)
        {
            queryBool["must"] = new JsonArray(FullTextQuery(query, segment));
        }

        var body = new JsonObject
        {
            ["query"] = new JsonObject { ["bool"] = queryBool },
            ["from"] = skip,
            ["size"] = take,
            ["track_total_hits"] = true,
            ["_source"] = new JsonArray(SystemFields.Key, SystemFields.ObjectType),
            ["sort"] = new JsonArray((sorters ?? [new ScoreSorter(UmbracoDirection.Descending)]).Select(sorter => (JsonNode?)SortClause(sorter)).ToArray())
        };

        if (facetFilters.Length > 0)
        {
            body["post_filter"] = BoolFilter(facetFilters, segment);
        }

        if (facets.Length > 0)
        {
            var aggregations = new JsonObject();
            foreach (Facet facet in facets)
            {
                Filter[] otherFacetFilters = facetFilters
                    .Where(filter => string.Equals(filter.FieldName, facet.FieldName, StringComparison.OrdinalIgnoreCase) is false)
                    .ToArray();

                aggregations[FacetName(facet)] = otherFacetFilters.Length is 0
                    ? Aggregation(facet)
                    : new JsonObject
                    {
                        ["filter"] = BoolFilter(otherFacetFilters, segment),
                        ["aggs"] = new JsonObject { [FacetName(facet)] = Aggregation(facet) }
                    };
            }

            body["aggs"] = aggregations;
        }

        return body;
    }

    // free text matches all words, the last one as a prefix, in the "all texts" fields weighted by relevance
    private JsonObject FullTextQuery(string query, string? segment)
    {
        var should = new JsonArray();
        foreach (var fieldSegment in segment is null ? [null] : new[] { null, segment })
        {
            should.Add(MatchBoolPrefix(_fieldNames.AllTexts(FieldKinds.Texts, fieldSegment), query, 1.0f));
            should.Add(MatchBoolPrefix(_fieldNames.AllTexts(FieldKinds.TextsR1, fieldSegment), query, _options.BoostFactorTextR1));
            should.Add(MatchBoolPrefix(_fieldNames.AllTexts(FieldKinds.TextsR2, fieldSegment), query, _options.BoostFactorTextR2));
            should.Add(MatchBoolPrefix(_fieldNames.AllTexts(FieldKinds.TextsR3, fieldSegment), query, _options.BoostFactorTextR3));
        }

        return ShouldAny(should);
    }

    private JsonObject BoolFilter(Filter[] filters, string? segment)
        => new()
        {
            ["bool"] = new JsonObject
            {
                ["filter"] = new JsonArray(filters.Where(filter => filter.Negate is false).Select(filter => (JsonNode?)FilterClause(filter, segment)).ToArray()),
                ["must_not"] = new JsonArray(filters.Where(filter => filter.Negate).Select(filter => (JsonNode?)FilterClause(filter, segment)).ToArray())
            }
        };

    // values within a filter are OR'ed; a filter also matches the segment variant of the field when searching a segment
    private JsonObject FilterClause(Filter filter, string? segment)
    {
        string?[] segments = segment is null ? [null] : [null, segment];
        var should = new JsonArray();

        foreach (var fieldSegment in segments)
        {
            switch (filter)
            {
                case TextFilter textFilter:
                    foreach (var relevance in new[] { FieldKinds.Texts, FieldKinds.TextsR1, FieldKinds.TextsR2, FieldKinds.TextsR3 })
                    {
                        foreach (var text in textFilter.Values.Where(text => string.IsNullOrWhiteSpace(text) is false))
                        {
                            should.Add(MatchBoolPrefix(_fieldNames.Texts(filter.FieldName, relevance, fieldSegment), text, 1.0f));
                        }
                    }

                    break;
                case KeywordFilter keywordFilter:
                    should.Add(Terms(FieldNames.Field(filter.FieldName, FieldKinds.Keywords, fieldSegment), keywordFilter.Values));
                    break;
                case IntegerExactFilter integerExactFilter:
                    should.Add(TermValues(FieldNames.Field(filter.FieldName, FieldKinds.Integers, fieldSegment), integerExactFilter.Values.Select(v => (JsonNode?)v)));
                    break;
                case DecimalExactFilter decimalExactFilter:
                    should.Add(TermValues(FieldNames.Field(filter.FieldName, FieldKinds.Decimals, fieldSegment), decimalExactFilter.Values.Select(v => (JsonNode?)(double)v)));
                    break;
                case DateTimeOffsetExactFilter dateTimeOffsetExactFilter:
                    should.Add(Terms(FieldNames.Field(filter.FieldName, FieldKinds.DateTimeOffsets, fieldSegment), dateTimeOffsetExactFilter.Values.Select(DocumentBuilder.FormatDate)));
                    break;
                case IntegerRangeFilter integerRangeFilter:
                    foreach (IntegerRangeFilterRange range in integerRangeFilter.Ranges)
                    {
                        should.Add(Range(FieldNames.Field(filter.FieldName, FieldKinds.Integers, fieldSegment), range.MinValue, range.MaxValue));
                    }

                    break;
                case DecimalRangeFilter decimalRangeFilter:
                    foreach (DecimalRangeFilterRange range in decimalRangeFilter.Ranges)
                    {
                        should.Add(Range(FieldNames.Field(filter.FieldName, FieldKinds.Decimals, fieldSegment), (double?)range.MinValue, (double?)range.MaxValue));
                    }

                    break;
                case DateTimeOffsetRangeFilter dateTimeOffsetRangeFilter:
                    foreach (DateTimeOffsetRangeFilterRange range in dateTimeOffsetRangeFilter.Ranges)
                    {
                        should.Add(Range(
                            FieldNames.Field(filter.FieldName, FieldKinds.DateTimeOffsets, fieldSegment),
                            range.MinValue.HasValue ? DocumentBuilder.FormatDate(range.MinValue.Value) : null,
                            range.MaxValue.HasValue ? DocumentBuilder.FormatDate(range.MaxValue.Value) : null));
                    }

                    break;
                default:
                    throw new NotSupportedException($"The filter type {filter.GetType().Name} is not supported by WarpSearch.");
            }
        }

        return ShouldAny(should);
    }

    private JsonObject Aggregation(Facet facet)
        => facet switch
        {
            KeywordFacet => TermsAggregation(FieldNames.Field(facet.FieldName, FieldKinds.Keywords)),
            IntegerExactFacet => TermsAggregation(FieldNames.Field(facet.FieldName, FieldKinds.Integers)),
            DecimalExactFacet => TermsAggregation(FieldNames.Field(facet.FieldName, FieldKinds.DecimalKeywords)),
            DateTimeOffsetExactFacet => TermsAggregation(FieldNames.Field(facet.FieldName, FieldKinds.DateTimeOffsets)),
            IntegerRangeFacet integerRangeFacet => RangeAggregation(
                "range",
                FieldNames.Field(facet.FieldName, FieldKinds.Integers),
                integerRangeFacet.Ranges.Select(r => (r.Key, (JsonNode?)r.MinValue, (JsonNode?)r.MaxValue))),
            DecimalRangeFacet decimalRangeFacet => RangeAggregation(
                "range",
                FieldNames.Field(facet.FieldName, FieldKinds.Decimals),
                decimalRangeFacet.Ranges.Select(r => (r.Key, (JsonNode?)(double?)r.MinValue, (JsonNode?)(double?)r.MaxValue))),
            DateTimeOffsetRangeFacet dateTimeOffsetRangeFacet => RangeAggregation(
                "date_range",
                FieldNames.Field(facet.FieldName, FieldKinds.DateTimeOffsets),
                dateTimeOffsetRangeFacet.Ranges.Select(r => (
                    r.Key,
                    (JsonNode?)(r.MinValue.HasValue ? DocumentBuilder.FormatDate(r.MinValue.Value) : null),
                    (JsonNode?)(r.MaxValue.HasValue ? DocumentBuilder.FormatDate(r.MaxValue.Value) : null)))),
            _ => throw new NotSupportedException($"The facet type {facet.GetType().Name} is not supported by WarpSearch.")
        };

    private JsonObject TermsAggregation(string field)
        => new() { ["terms"] = new JsonObject { ["field"] = field, ["size"] = _options.MaxFacetValues } };

    private static JsonObject RangeAggregation(string type, string field, IEnumerable<(string Key, JsonNode? From, JsonNode? To)> ranges)
        => new()
        {
            [type] = new JsonObject
            {
                ["field"] = field,
                ["ranges"] = new JsonArray(ranges.Select(range =>
                {
                    var rangeObject = new JsonObject { ["key"] = range.Key };
                    if (range.From is not null)
                    {
                        rangeObject["from"] = range.From;
                    }

                    if (range.To is not null)
                    {
                        rangeObject["to"] = range.To;
                    }

                    return (JsonNode?)rangeObject;
                }).ToArray())
            }
        };

    private JsonObject SortClause(Sorter sorter)
    {
        var order = sorter.Direction is UmbracoDirection.Ascending ? "asc" : "desc";
        var field = sorter switch
        {
            ScoreSorter => "_score",
            TextSorter => FieldNames.Field(sorter.FieldName, FieldKinds.TextSort),
            KeywordSorter => FieldNames.Field(sorter.FieldName, FieldKinds.Keywords),
            IntegerSorter => FieldNames.Field(sorter.FieldName, FieldKinds.Integers),
            DecimalSorter => FieldNames.Field(sorter.FieldName, FieldKinds.Decimals),
            DateTimeOffsetSorter => FieldNames.Field(sorter.FieldName, FieldKinds.DateTimeOffsets),
            _ => throw new NotSupportedException($"The sorter type {sorter.GetType().Name} is not supported by WarpSearch.")
        };

        return new JsonObject { [field] = new JsonObject { ["order"] = order } };
    }

    private static JsonObject MatchBoolPrefix(string field, string query, float boost)
        => new()
        {
            ["match_bool_prefix"] = new JsonObject
            {
                [field] = new JsonObject { ["query"] = query, ["operator"] = "and", ["boost"] = boost }
            }
        };

    private static JsonObject Terms(string field, IEnumerable<string> values)
        => TermValues(field, values.Select(value => (JsonNode?)value));

    private static JsonObject TermValues(string field, IEnumerable<JsonNode?> values)
        => new() { ["terms"] = new JsonObject { [field] = new JsonArray(values.ToArray()) } };

    // range filters include the minimum and exclude the maximum
    private static JsonObject Range<T>(string field, T? min, T? max)
    {
        var range = new JsonObject();
        if (min is not null)
        {
            range["gte"] = JsonValue.Create(min);
        }

        if (max is not null)
        {
            range["lt"] = JsonValue.Create(max);
        }

        return new JsonObject { ["range"] = new JsonObject { [field] = range } };
    }

    private static JsonObject ShouldAny(JsonArray should)
        => should.Count is 1
            ? should[0]!.DeepClone().AsObject()
            : new JsonObject { ["bool"] = new JsonObject { ["should"] = should, ["minimum_should_match"] = 1 } };
}
