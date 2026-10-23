using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Models.Indexing;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Filtering;
using CoreConstants = Umbraco.Cms.Search.Core.Constants;

namespace WarpSearch.Umbraco.Search.Tests;

public class IndexerTests : WarpSearchTestBase
{
    private string IndexAlias = null!;

    [SetUp]
    public async Task SetUpAsync()
        => IndexAlias = await CreateIndexAsync($"indexer{TestContext.CurrentContext.Test.MethodName!.ToLowerInvariant()}");

    [Test]
    public async Task Update_RemovesCulturesThatAreGone()
    {
        Guid key = Guid.NewGuid();
        await AddAsync(key, [], ("en-US", "first"), ("da-DK", "first"));
        await AddAsync(key, [], ("en-US", "second"));

        SearchResult danish = await Searcher.SearchAsync(IndexAlias, "first", culture: "da-DK");
        SearchResult english = await Searcher.SearchAsync(IndexAlias, "second", culture: "en-US");

        Assert.That(danish.Total, Is.EqualTo(0));
        Assert.That(english.Documents.Select(d => d.Id), Is.EqualTo(new[] { key }));
        Assert.That((await Indexer.GetMetadataAsync(IndexAlias)).DocumentCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Delete_RemovesDescendants()
    {
        Guid root = Guid.NewGuid(), child = Guid.NewGuid(), grandchild = Guid.NewGuid(), other = Guid.NewGuid();
        await AddAsync(root, [root], (null, "page"));
        await AddAsync(child, [root, child], (null, "page"));
        await AddAsync(grandchild, [root, child, grandchild], (null, "page"));
        await AddAsync(other, [other], (null, "page"));

        await Indexer.DeleteAsync(IndexAlias, [child]);

        SearchResult result = await Searcher.SearchAsync(IndexAlias, "page");
        Assert.That(result.Documents.Select(d => d.Id), Is.EquivalentTo(new[] { root, other }));
    }

    [Test]
    public async Task Delete_FindsMoreThanOnePageOfDocuments()
    {
        Guid root = Guid.NewGuid();
        await RebuildAsync(IndexAlias, async () =>
        {
            for (var i = 0; i < 120; i++)
            {
                Guid key = Guid.NewGuid();
                await AddAsync(key, [root, key], (null, "many"));
            }
        });
        Assert.That((await Indexer.GetMetadataAsync(IndexAlias)).DocumentCount, Is.EqualTo(120));

        await Indexer.DeleteAsync(IndexAlias, [root]);

        Assert.That((await Indexer.GetMetadataAsync(IndexAlias)).DocumentCount, Is.EqualTo(0));
    }

    [Test]
    public async Task Reset_EmptiesTheIndex()
    {
        await AddAsync(Guid.NewGuid(), [], (null, "page"));
        Assert.That((await Indexer.GetMetadataAsync(IndexAlias)).HealthStatus, Is.EqualTo(HealthStatus.Healthy));

        await Indexer.ResetAsync(IndexAlias);
        Assert.That((await Indexer.GetMetadataAsync(IndexAlias)).HealthStatus, Is.EqualTo(HealthStatus.Rebuilding));
        await Writer.CompleteRebuildAsync(IndexAlias);

        IndexMetadata metadata = await Indexer.GetMetadataAsync(IndexAlias);
        Assert.That(metadata.DocumentCount, Is.EqualTo(0));
        Assert.That(metadata.HealthStatus, Is.EqualTo(HealthStatus.Empty));
        Assert.That(metadata.ProviderName, Is.EqualTo(WarpSearchConstants.ProviderName));
    }

    [Test]
    public async Task Rebuild_WritesInBatchesAndCommitsAtTheEnd()
    {
        await Indexer.ResetAsync(IndexAlias);
        for (var i = 0; i < 5; i++)
        {
            await AddAsync(Guid.NewGuid(), [], (null, "batched"));
        }

        // fewer documents than a batch are held back until the rebuild completes
        Assert.That((await Searcher.SearchAsync(IndexAlias, "batched")).Total, Is.EqualTo(0));

        await Writer.CompleteRebuildAsync(IndexAlias);

        Assert.That((await Searcher.SearchAsync(IndexAlias, "batched")).Total, Is.EqualTo(5));
    }

    [Test]
    public async Task Indexes_AreKeptApartInTheCollection()
    {
        var otherIndex = await CreateIndexAsync("other");
        Guid key = Guid.NewGuid();
        await AddAsync(key, [], (null, "shared"));

        Assert.That((await Searcher.SearchAsync(otherIndex, "shared")).Total, Is.EqualTo(0));
        Assert.That((await Indexer.GetMetadataAsync(otherIndex)).DocumentCount, Is.EqualTo(0));

        await Indexer.ResetAsync(otherIndex);
        await Writer.CompleteRebuildAsync(otherIndex);
        Assert.That((await Searcher.SearchAsync(IndexAlias, "shared")).Total, Is.EqualTo(1));
    }

    [Test]
    public async Task OddFieldNames_AreIndexedAndSearchable()
    {
        Guid key = Guid.NewGuid();
        await Indexer.AddOrUpdateAsync(IndexAlias, key, UmbracoObjectTypes.Media, [new Variation(null, null)],
        [
            new IndexField("__weird.name-with spaces", new IndexValue { Keywords = ["yes"] }, null, null),
            new IndexField(new string('a', 200), new IndexValue { Keywords = ["long"] }, null, null)
        ], null);

        SearchResult weird = await Searcher.SearchAsync(IndexAlias, filters: [new KeywordFilter("__weird.name-with spaces", ["yes"], false)]);
        SearchResult longName = await Searcher.SearchAsync(IndexAlias, filters: [new KeywordFilter(new string('a', 200), ["long"], false)]);

        Assert.That(weird.Documents.Single(), Is.EqualTo(new Document(key, UmbracoObjectTypes.Media)));
        Assert.That(longName.Documents.Single().Id, Is.EqualTo(key));
    }

    private Task AddAsync(Guid key, Guid[] pathIds, params (string? Culture, string Text)[] variants)
        => Indexer.AddOrUpdateAsync(
            IndexAlias,
            key,
            UmbracoObjectTypes.Document,
            variants.Select(v => new Variation(v.Culture, null)),
            variants
                .Select(v => new IndexField("title", new IndexValue { TextsR1 = [v.Text] }, v.Culture, null))
                .Append(new IndexField(CoreConstants.FieldNames.PathIds, new IndexValue { Keywords = pathIds.Select(id => id.ToString("D")).ToArray() }, null, null)),
            null);
}
