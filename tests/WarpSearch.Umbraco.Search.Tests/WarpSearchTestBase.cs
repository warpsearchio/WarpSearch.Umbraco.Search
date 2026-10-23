using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Sync;
using WarpSearch.Umbraco.Search.Client;
using WarpSearch.Umbraco.Search.DependencyInjection;
using WarpSearch.Umbraco.Search.Services;

namespace WarpSearch.Umbraco.Search.Tests;

/// <summary>
/// Integration tests against WarpSearch. Set WARPSEARCH_API_KEY, and WARPSEARCH_COLLECTION for a collection other
/// than "umbracotest"; the tests are skipped without a key. Every fixture writes to its own index in the collection
/// and deletes its documents afterwards.
/// </summary>
[Category(IntegrationCategory)]
public abstract class WarpSearchTestBase
{
    public const string IntegrationCategory = "Integration";

    private ServiceProvider _serviceProvider = null!;
    private readonly List<string> _indexAliases = [];
    private readonly string _prefix = $"test_{Guid.NewGuid():N}"[..13];

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        var apiKey = Environment.GetEnvironmentVariable("WARPSEARCH_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            Assert.Ignore("WARPSEARCH_API_KEY is not set");
        }

        var collection = Environment.GetEnvironmentVariable("WARPSEARCH_COLLECTION");
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WarpSearch:ApiKey"] = apiKey,
                ["WarpSearch:Collection"] = string.IsNullOrWhiteSpace(collection) ? "umbracotest" : collection
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging().AddWarpSearch(configuration);
        services.AddSingleton<IServerRoleAccessor, SingleServerRoleAccessor>();
        services.AddSingleton<IIndexCultureProvider>(new FixedCultures(["en-US", "da-DK"]));
        _serviceProvider = services.BuildServiceProvider();

        Assert.That(await GetRequiredService<ICollectionManager>().VerifyAsync(), Is.True, "The test collection is missing");

        await SetUpDataAsync();
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_serviceProvider is null)
        {
            return;
        }

        try
        {
            foreach (var indexAlias in _indexAliases)
            {
                try
                {
                    await Indexer.ResetAsync(indexAlias);
                    await Writer.CompleteRebuildAsync(indexAlias);
                }
                catch (Exception exception)
                {
                    TestContext.Progress.WriteLine($"Could not delete the documents of test index {indexAlias}: {exception.Message}");
                }
            }
        }
        finally
        {
            await _serviceProvider.DisposeAsync();
        }
    }

    protected virtual Task SetUpDataAsync()
        => Task.CompletedTask;

    protected IWarpSearchIndexer Indexer => GetRequiredService<IWarpSearchIndexer>();

    protected IWarpSearchSearcher Searcher => GetRequiredService<IWarpSearchSearcher>();

    protected IWarpSearchClient Client => GetRequiredService<IWarpSearchClient>();

    internal IndexWriter Writer => GetRequiredService<IndexWriter>();

    protected T GetRequiredService<T>() where T : notnull
        => _serviceProvider.GetRequiredService<T>();

    /// <summary>
    /// Returns a new, empty index of this fixture, removed again after the fixture.
    /// </summary>
    protected async Task<string> CreateIndexAsync(string name)
    {
        var indexAlias = $"{_prefix}_{name}";
        _indexAliases.Add(indexAlias);
        await Indexer.ResetAsync(indexAlias);
        await Writer.CompleteRebuildAsync(indexAlias);
        return indexAlias;
    }

    /// <summary>
    /// Fills an index the way Umbraco rebuilds one: reset, write everything, complete.
    /// </summary>
    protected async Task RebuildAsync(string indexAlias, Func<Task> writeDocuments)
    {
        await Indexer.ResetAsync(indexAlias);
        await writeDocuments();
        await Writer.CompleteRebuildAsync(indexAlias);
    }

    private sealed class FixedCultures(string[] cultures) : IIndexCultureProvider
    {
        public Task<IReadOnlyCollection<string>> GetCulturesAsync()
            => Task.FromResult<IReadOnlyCollection<string>>(cultures);
    }
}
