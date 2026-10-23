using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using Umbraco.Cms.Search.Core.Models.Searching.Sorting;

namespace WarpSearch.Umbraco.Search.Tests;

/// <summary>
/// 20 invariant documents: count = i, price = i * 1.5, date = 2025-01-01 + i days, tags = even/odd (+ "three" for
/// multiples of three). "zebra" appears at a different relevance level in documents 3, 4, 5 and 6.
/// </summary>
public class SearcherTests : WarpSearchTestBase
{
    private string IndexAlias = null!;
    private static readonly DateTimeOffset BaseDate = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Guid Key(int i) => new($"00000000-0000-0000-0000-{i:D12}");

    protected override async Task SetUpDataAsync()
    {
        IndexAlias = await CreateIndexAsync("searcher");
        await RebuildAsync(IndexAlias, AddDocumentsAsync);
    }

    private async Task AddDocumentsAsync()
    {
        for (var i = 1; i <= 20; i++)
        {
            await Indexer.AddOrUpdateAsync(
                IndexAlias,
                Key(i),
                UmbracoObjectTypes.Document,
                [new Variation(null, null)],
                [
                    new IndexField("title", new IndexValue { TextsR1 = [$"{(i % 2 == 0 ? "Even" : "Odd")} title {i}", i == 5 ? "zebra" : "horse"] }, null, null),
                    new IndexField("subtitle", new IndexValue { TextsR2 = [i == 6 ? "zebra" : "cow"] }, null, null),
                    new IndexField("summary", new IndexValue { TextsR3 = [i == 3 ? "zebra" : "goat"] }, null, null),
                    new IndexField("body", new IndexValue { Texts = [$"Body number {i}", i == 4 ? "zebra" : "lorem ipsum"] }, null, null),
                    new IndexField("tags", new IndexValue { Keywords = i % 3 == 0 ? [i % 2 == 0 ? "even" : "odd", "three"] : [i % 2 == 0 ? "even" : "odd"] }, null, null),
                    new IndexField("count", new IndexValue { Integers = [i] }, null, null),
                    new IndexField("price", new IndexValue { Decimals = [i * 1.5m] }, null, null),
                    new IndexField("date", new IndexValue { DateTimeOffsets = [BaseDate.AddDays(i)] }, null, null)
                ],
                null);
        }
    }

    [Test]
    public async Task Query_RanksByRelevanceLevel()
    {
        SearchResult result = await SearchAsync(query: "zebra");

        Assert.That(result.Total, Is.EqualTo(4));
        Assert.That(result.Documents.Select(d => d.Id), Is.EqualTo(new[] { Key(5), Key(6), Key(3), Key(4) }));
        Assert.That(result.Documents.All(d => d.ObjectType is UmbracoObjectTypes.Document), Is.True);
    }

    [Test]
    public async Task Query_MatchesLastWordAsPrefix()
    {
        SearchResult result = await SearchAsync(query: "body numb");

        Assert.That(result.Total, Is.EqualTo(20));
    }

    [Test]
    public async Task Query_RequiresAllWords()
    {
        SearchResult result = await SearchAsync(query: "even zebra");

        Assert.That(result.Total, Is.EqualTo(0));
    }

    [TestCase(false, 10)]
    [TestCase(true, 10)]
    public async Task KeywordFilter(bool negate, int expected)
    {
        SearchResult result = await SearchAsync(filters: [new KeywordFilter("tags", ["even"], negate)]);

        Assert.That(result.Total, Is.EqualTo(expected));
        Assert.That(result.Documents.All(d => (Index(d) % 2 == 0) != negate), Is.True);
    }

    [Test]
    public async Task TextFilter_MatchesFieldTextByPrefix()
    {
        SearchResult result = await SearchAsync(filters: [new TextFilter("title", ["even tit"], false)]);

        Assert.That(result.Total, Is.EqualTo(10));
    }

    [Test]
    public async Task IntegerRangeFilter_IncludesMinExcludesMax()
    {
        SearchResult result = await SearchAsync(filters: [IntegerRangeFilter.Single("count", 5, 10, false)]);

        Assert.That(result.Documents.Select(Index).Order(), Is.EqualTo(new[] { 5, 6, 7, 8, 9 }));
    }

    [Test]
    public async Task IntegerExactFilter_OrsValues()
    {
        SearchResult result = await SearchAsync(filters: [new IntegerExactFilter("count", [2, 4, 99], false)]);

        Assert.That(result.Documents.Select(Index).Order(), Is.EqualTo(new[] { 2, 4 }));
    }

    [Test]
    public async Task DecimalFilters()
    {
        SearchResult exact = await SearchAsync(filters: [new DecimalExactFilter("price", [3.0m], false)]);
        SearchResult range = await SearchAsync(filters: [DecimalRangeFilter.Single("price", 3.0m, 6.0m, false)]);

        Assert.That(exact.Documents.Select(Index), Is.EqualTo(new[] { 2 }));
        Assert.That(range.Documents.Select(Index).Order(), Is.EqualTo(new[] { 2, 3 }));
    }

    [Test]
    public async Task DateTimeOffsetFilters()
    {
        SearchResult exact = await SearchAsync(filters: [new DateTimeOffsetExactFilter("date", [BaseDate.AddDays(7)], false)]);
        SearchResult range = await SearchAsync(filters: [DateTimeOffsetRangeFilter.Single("date", BaseDate.AddDays(2), BaseDate.AddDays(5), false)]);

        Assert.That(exact.Documents.Select(Index), Is.EqualTo(new[] { 7 }));
        Assert.That(range.Documents.Select(Index).Order(), Is.EqualTo(new[] { 2, 3, 4 }));
    }

    [Test]
    public async Task Filters_AreAnded()
    {
        SearchResult result = await SearchAsync(filters:
        [
            new KeywordFilter("tags", ["three"], false),
            IntegerRangeFilter.Single("count", null, 10, false)
        ]);

        Assert.That(result.Documents.Select(Index).Order(), Is.EqualTo(new[] { 3, 6, 9 }));
    }

    [Test]
    public async Task KeywordFacet_IgnoresItsOwnFilter()
    {
        SearchResult result = await SearchAsync(
            filters: [new KeywordFilter("tags", ["even"], false)],
            facets: [new KeywordFacet("tags")]);

        Assert.That(result.Total, Is.EqualTo(10));
        Assert.That(FacetCounts(result, "tags"), Is.EquivalentTo(new Dictionary<string, long> { ["even"] = 10, ["odd"] = 10, ["three"] = 6 }));
    }

    [Test]
    public async Task Facets_AreCountedWithTheOtherFacetsFilters()
    {
        SearchResult result = await SearchAsync(
            filters:
            [
                new KeywordFilter("tags", ["even"], false),
                IntegerRangeFilter.Single("count", null, 5, false)
            ],
            facets:
            [
                new KeywordFacet("tags"),
                new IntegerRangeFacet("count", [new IntegerRangeFacetRange("low", null, 5), new IntegerRangeFacetRange("high", 5, null)])
            ]);

        Assert.That(result.Documents.Select(Index).Order(), Is.EqualTo(new[] { 2, 4 }));
        Assert.That(FacetCounts(result, "tags"), Is.EquivalentTo(new Dictionary<string, long> { ["even"] = 2, ["odd"] = 2, ["three"] = 1 }));
        Assert.That(FacetCounts(result, "count"), Is.EquivalentTo(new Dictionary<string, long> { ["low"] = 2, ["high"] = 8 }));
    }

    [Test]
    public async Task ExactFacets()
    {
        SearchResult result = await SearchAsync(
            filters: [IntegerRangeFilter.Single("count", null, 4, false)],
            facets: [new IntegerExactFacet("count"), new DecimalExactFacet("price"), new DateTimeOffsetExactFacet("date")]);

        FacetValue[] counts = result.Facets.Single(f => f.FieldName == "count").Values.ToArray();
        FacetValue[] prices = result.Facets.Single(f => f.FieldName == "price").Values.ToArray();
        FacetValue[] dates = result.Facets.Single(f => f.FieldName == "date").Values.ToArray();

        // the count filter does not narrow the count facet itself, only the other facets
        Assert.That(counts.Cast<IntegerExactFacetValue>().Select(v => v.Key).Order(), Is.EqualTo(Enumerable.Range(1, 20)));
        Assert.That(prices.Cast<DecimalExactFacetValue>().Select(v => v.Key).Order(), Is.EqualTo(new[] { 1.5m, 3.0m, 4.5m }));
        Assert.That(dates.Cast<DateTimeOffsetExactFacetValue>().Select(v => v.Key).Order(), Is.EqualTo(new[] { BaseDate.AddDays(1), BaseDate.AddDays(2), BaseDate.AddDays(3) }));
        Assert.That(counts.Concat(prices).Concat(dates).All(v => v.Count == 1), Is.True);
    }

    [Test]
    public async Task RangeFacets()
    {
        SearchResult result = await SearchAsync(
            filters: [new KeywordFilter("tags", ["odd", "even"], false)],
            facets:
            [
                new DecimalRangeFacet("price", [new DecimalRangeFacetRange("cheap", null, 15m), new DecimalRangeFacetRange("pricey", 15m, null)]),
                new DateTimeOffsetRangeFacet("date", [new DateTimeOffsetRangeFacetRange("early", null, BaseDate.AddDays(6)), new DateTimeOffsetRangeFacetRange("late", BaseDate.AddDays(6), null)])
            ]);

        Assert.That(FacetCounts(result, "price"), Is.EquivalentTo(new Dictionary<string, long> { ["cheap"] = 9, ["pricey"] = 11 }));
        Assert.That(FacetCounts(result, "date"), Is.EquivalentTo(new Dictionary<string, long> { ["early"] = 5, ["late"] = 15 }));
    }

    [Test]
    public async Task Sorters()
    {
        SearchResult byInteger = await SearchAsync(sorters: [new IntegerSorter("count", Direction.Descending)], take: 3);
        SearchResult byDecimal = await SearchAsync(sorters: [new DecimalSorter("price", Direction.Ascending)], take: 3);
        SearchResult byDate = await SearchAsync(sorters: [new DateTimeOffsetSorter("date", Direction.Descending)], take: 3);
        SearchResult byText = await SearchAsync(sorters: [new TextSorter("title", Direction.Ascending)], take: 3);
        SearchResult byKeywordThenInteger = await SearchAsync(
            sorters: [new KeywordSorter("tags", Direction.Ascending), new IntegerSorter("count", Direction.Ascending)],
            take: 3);

        Assert.That(byInteger.Documents.Select(Index), Is.EqualTo(new[] { 20, 19, 18 }));
        Assert.That(byDecimal.Documents.Select(Index), Is.EqualTo(new[] { 1, 2, 3 }));
        Assert.That(byDate.Documents.Select(Index), Is.EqualTo(new[] { 20, 19, 18 }));
        Assert.That(byText.Documents.Select(Index), Is.EqualTo(new[] { 10, 12, 14 }));
        Assert.That(byKeywordThenInteger.Documents.Select(Index), Is.EqualTo(new[] { 2, 4, 6 }));
    }

    [Test]
    public async Task Paging()
    {
        SearchResult result = await SearchAsync(sorters: [new IntegerSorter("count", Direction.Ascending)], skip: 5, take: 5);

        Assert.That(result.Total, Is.EqualTo(20));
        Assert.That(result.Documents.Select(Index), Is.EqualTo(new[] { 6, 7, 8, 9, 10 }));
    }

    [Test]
    public async Task NothingToSearchFor_ReturnsNothing()
    {
        SearchResult result = await Searcher.SearchAsync(IndexAlias);

        Assert.That(result.Total, Is.EqualTo(0));
    }

    private Task<SearchResult> SearchAsync(
        string? query = null,
        Filter[]? filters = null,
        Facet[]? facets = null,
        Sorter[]? sorters = null,
        int skip = 0,
        int take = 50)
        => Searcher.SearchAsync(
            IndexAlias,
            query,
            filters ?? (query is null ? [] : null),
            facets,
            sorters,
            skip: skip,
            take: take);

    private static int Index(Document document)
        => int.Parse(document.Id.ToString("N")[^12..]);

    private static Dictionary<string, long> FacetCounts(SearchResult result, string fieldName)
        => result.Facets
            .Single(facet => facet.FieldName == fieldName)
            .Values
            .ToDictionary(
                value => value switch
                {
                    KeywordFacetValue keyword => keyword.Key,
                    IntegerRangeFacetValue integerRange => integerRange.Key,
                    DecimalRangeFacetValue decimalRange => decimalRange.Key,
                    DateTimeOffsetRangeFacetValue dateRange => dateRange.Key,
                    _ => throw new ArgumentOutOfRangeException(nameof(value))
                },
                value => value.Count);
}
