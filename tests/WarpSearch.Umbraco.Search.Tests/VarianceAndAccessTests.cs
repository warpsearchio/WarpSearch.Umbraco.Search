using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;

namespace WarpSearch.Umbraco.Search.Tests;

public class VarianceAndAccessTests : WarpSearchTestBase
{
    private string IndexAlias = null!;

    private static readonly Guid Invariant = Guid.NewGuid();
    private static readonly Guid CultureVariant = Guid.NewGuid();
    private static readonly Guid SegmentVariant = Guid.NewGuid();
    private static readonly Guid Protected = Guid.NewGuid();
    private static readonly Guid MemberGroup = Guid.NewGuid();
    private static readonly Guid Member = Guid.NewGuid();

    protected override async Task SetUpDataAsync()
    {
        IndexAlias = await CreateIndexAsync("variance");

        await Indexer.AddOrUpdateAsync(IndexAlias, Invariant, UmbracoObjectTypes.Document, [new Variation(null, null)],
        [
            new IndexField("title", new IndexValue { TextsR1 = ["invariant apple"] }, null, null),
            new IndexField("tag", new IndexValue { Keywords = ["shared"] }, null, null)
        ], null);

        await Indexer.AddOrUpdateAsync(IndexAlias, CultureVariant, UmbracoObjectTypes.Document, [new Variation("en-US", null), new Variation("da-DK", null)],
        [
            new IndexField("title", new IndexValue { TextsR1 = ["english apple"] }, "en-US", null),
            new IndexField("title", new IndexValue { TextsR1 = ["danish æble trailløbesko"] }, "da-DK", null),
            new IndexField("tag", new IndexValue { Keywords = ["shared"] }, null, null)
        ], null);

        await Indexer.AddOrUpdateAsync(IndexAlias, SegmentVariant, UmbracoObjectTypes.Document, [new Variation(null, null), new Variation(null, "vip")],
        [
            new IndexField("title", new IndexValue { TextsR1 = ["segment default"] }, null, null),
            new IndexField("title", new IndexValue { TextsR1 = ["segment special"] }, null, "vip"),
            new IndexField("level", new IndexValue { Keywords = ["basic"] }, null, null),
            new IndexField("level", new IndexValue { Keywords = ["gold"] }, null, "vip")
        ], null);

        await Indexer.AddOrUpdateAsync(IndexAlias, Protected, UmbracoObjectTypes.Document, [new Variation(null, null)],
        [
            new IndexField("title", new IndexValue { TextsR1 = ["secret apple"] }, null, null)
        ], new ContentProtection([MemberGroup, Member]));
    }

    [TestCase(null, new[] { "invariant" })]
    [TestCase("en-US", new[] { "invariant", "culture" })]
    [TestCase("da-DK", new[] { "invariant" })]
    public async Task Culture_IncludesInvariantContent(string? culture, string[] expected)
    {
        SearchResult result = await Searcher.SearchAsync(IndexAlias, "apple", culture: culture);

        Assert.That(result.Documents.Select(Name), Is.EquivalentTo(expected));
    }

    [Test]
    public async Task Culture_SearchesOnlyThatCulturesTexts()
    {
        SearchResult danish = await Searcher.SearchAsync(IndexAlias, "danish", culture: "da-DK");
        SearchResult english = await Searcher.SearchAsync(IndexAlias, "danish", culture: "en-US");

        Assert.That(danish.Documents.Select(Name), Is.EqualTo(new[] { "culture" }));
        Assert.That(english.Total, Is.EqualTo(0));
    }

    [TestCase("æble")]
    [TestCase("trailløb")]
    [TestCase("TRAILLØBESKO")]
    public async Task Query_MatchesNonAsciiText(string query)
    {
        SearchResult result = await Searcher.SearchAsync(IndexAlias, query, culture: "da-DK");

        Assert.That(result.Documents.Select(Name), Is.EqualTo(new[] { "culture" }));
    }

    [Test]
    public async Task Culture_KeywordFilterCountsEachDocumentOnce()
    {
        SearchResult result = await Searcher.SearchAsync(IndexAlias, filters: [new KeywordFilter("tag", ["shared"], false)], culture: "en-US");

        Assert.That(result.Total, Is.EqualTo(2));
    }

    [Test]
    public async Task Segment_SearchesSegmentAndDefaultTexts()
    {
        SearchResult withoutSegment = await Searcher.SearchAsync(IndexAlias, "special");
        SearchResult withSegment = await Searcher.SearchAsync(IndexAlias, "special", segment: "vip");
        SearchResult defaultInSegment = await Searcher.SearchAsync(IndexAlias, "default", segment: "vip");

        Assert.That(withoutSegment.Total, Is.EqualTo(0));
        Assert.That(withSegment.Documents.Select(Name), Is.EqualTo(new[] { "segment" }));
        Assert.That(defaultInSegment.Documents.Select(Name), Is.EqualTo(new[] { "segment" }));
    }

    [Test]
    public async Task Segment_FiltersMatchSegmentValues()
    {
        SearchResult withoutSegment = await Searcher.SearchAsync(IndexAlias, filters: [new KeywordFilter("level", ["gold"], false)]);
        SearchResult withSegment = await Searcher.SearchAsync(IndexAlias, filters: [new KeywordFilter("level", ["gold"], false)], segment: "vip");

        Assert.That(withoutSegment.Total, Is.EqualTo(0));
        Assert.That(withSegment.Documents.Select(Name), Is.EqualTo(new[] { "segment" }));
    }

    [Test]
    public async Task Access_ProtectedContentNeedsAccess()
    {
        SearchResult anonymous = await Searcher.SearchAsync(IndexAlias, "apple");
        SearchResult otherMember = await Searcher.SearchAsync(IndexAlias, "apple", accessContext: new AccessContext(Guid.NewGuid(), [Guid.NewGuid()]));
        SearchResult byGroup = await Searcher.SearchAsync(IndexAlias, "apple", accessContext: new AccessContext(Guid.NewGuid(), [MemberGroup]));
        SearchResult byMember = await Searcher.SearchAsync(IndexAlias, "apple", accessContext: new AccessContext(Member, null));
        SearchResult bypass = await Searcher.SearchAsync(IndexAlias, "apple", accessContext: AccessContext.BypassProtection());

        Assert.That(anonymous.Documents.Select(Name), Does.Not.Contain("protected"));
        Assert.That(otherMember.Documents.Select(Name), Does.Not.Contain("protected"));
        Assert.That(byGroup.Documents.Select(Name), Does.Contain("protected"));
        Assert.That(byMember.Documents.Select(Name), Does.Contain("protected"));
        Assert.That(bypass.Documents.Select(Name), Does.Contain("protected"));
    }

    private static string Name(Document document)
        => document.Id == Invariant ? "invariant"
            : document.Id == CultureVariant ? "culture"
            : document.Id == SegmentVariant ? "segment"
            : document.Id == Protected ? "protected"
            : "unknown";
}
