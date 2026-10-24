using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Telemetry;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Notifications;
using Umbraco.Cms.Search.Core.Services;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using WarpSearch.Umbraco.Search.Client;
using WarpSearch.Umbraco.Search.Configuration;
using WarpSearch.Umbraco.Search.Extensions;
using WarpSearch.Umbraco.Search.NotificationHandlers;
using WarpSearch.Umbraco.Search.Services;
using CoreConstants = Umbraco.Cms.Search.Core.Constants;

namespace WarpSearch.Umbraco.Search.DependencyInjection;

public static class UmbracoBuilderExtensions
{
    /// <summary>
    /// Adds the WarpSearch provider and moves the published content, draft content, media and member indexes to
    /// WarpSearch. Call it after AddSearchCore().
    /// </summary>
    public static IUmbracoBuilder AddWarpSearchProvider(this IUmbracoBuilder builder)
    {
        builder.Services.AddWarpSearch(builder.Config);

        builder.Services.Configure<IndexOptions>(options =>
        {
            options.RegisterWarpSearchContentIndex<IPublishedContentChangeStrategy>(CoreConstants.IndexAliases.PublishedContent, UmbracoObjectTypes.Document);
            options.RegisterWarpSearchContentIndex<IDraftContentChangeStrategy>(CoreConstants.IndexAliases.DraftContent, UmbracoObjectTypes.Document);
            options.RegisterWarpSearchContentIndex<IDraftContentChangeStrategy>(CoreConstants.IndexAliases.DraftMedia, UmbracoObjectTypes.Media);
            options.RegisterWarpSearchContentIndex<IDraftContentChangeStrategy>(CoreConstants.IndexAliases.DraftMembers, UmbracoObjectTypes.Member);
        });

        builder.AddWarpSearchNotificationHandlers();

        return builder;
    }
}

public static class WarpSearchUmbracoBuilderExtensions
{
    /// <summary>
    /// Adds the notification handlers WarpSearch indexes need. <see cref="UmbracoBuilderExtensions.AddWarpSearchProvider"/>
    /// calls it; call it yourself when you register WarpSearch indexes with
    /// <see cref="ServiceCollectionExtensions.AddWarpSearch"/>.
    /// </summary>
    public static IUmbracoBuilder AddWarpSearchNotificationHandlers(this IUmbracoBuilder builder)
    {
        // check the collection on startup
        builder.AddNotificationAsyncHandler<UmbracoApplicationStartingNotification, VerifyCollectionNotificationHandler>();

        // commit the documents of a rebuild when it completes
        builder.AddNotificationAsyncHandler<IndexRebuildCompletedNotification, RebuildCompletedNotificationHandler>();

        return builder;
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the WarpSearch indexer, searcher and client without registering any indexes. Use
    /// <see cref="IndexOptionsExtensions.RegisterWarpSearchContentIndex{TContentChangeStrategy}"/> to register them.
    /// </summary>
    public static IServiceCollection AddWarpSearch(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        services.Configure<WarpSearchOptions>(configuration.GetSection(WarpSearchConstants.ConfigurationSection));
        services.Configure<IndexerOptions>(configuration.GetSection($"{WarpSearchConstants.ConfigurationSection}:Indexer"));
        services.Configure<SearcherOptions>(configuration.GetSection($"{WarpSearchConstants.ConfigurationSection}:Searcher"));

        services
            .AddHttpClient<IWarpSearchClient, WarpSearchClient>(WarpSearchConstants.HttpClientName, (serviceProvider, httpClient) =>
            {
                WarpSearchOptions options = serviceProvider.GetRequiredService<IOptions<WarpSearchOptions>>().Value;
                httpClient.BaseAddress = new Uri(WarpSearchConstants.ApiUrl);
                // the resilience pipeline below times requests out; HttpClient's own 100 second default would cut
                // its retries short
                httpClient.Timeout = Timeout.InfiniteTimeSpan;
                httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WarpSearch.Umbraco.Search", typeof(WarpSearchClient).Assembly.GetName().Version?.ToString(3)));
                if (string.IsNullOrWhiteSpace(options.ApiKey) is false)
                {
                    httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
                }
            })
            // singletons hold on to the client, so connections are recycled by the handler rather than by the
            // factory, which keeps DNS changes of the API picked up
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) })
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            // retries 429, 5xx (including "warming up" 503s) and timeouts with backoff, honouring Retry-After;
            // every write is an idempotent upsert or delete by id, so retrying them is safe
            .AddResilienceHandler("resilience", (pipeline, context) =>
            {
                TimeSpan requestTimeout = context.ServiceProvider.GetRequiredService<IOptions<WarpSearchOptions>>().Value.RequestTimeout;
                pipeline
                    .AddTimeout(requestTimeout * 4)
                    // the free plan allows 5 requests a second, so allow for a few rate limited answers in a row
                    .AddRetry(new HttpRetryStrategyOptions { MaxRetryAttempts = 6 })
                    .AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions { SamplingDuration = requestTimeout * 2 })
                    .AddTimeout(requestTimeout)
                    // every request is an "execution attempt"; log those as debug, retries and failures stay warnings
                    .ConfigureTelemetry(new TelemetryOptions
                    {
                        LoggerFactory = context.ServiceProvider.GetRequiredService<ILoggerFactory>(),
                        SeverityProvider = args => args.Event.EventName is "ExecutionAttempt" && args.Event.Severity is ResilienceEventSeverity.Information
                            ? ResilienceEventSeverity.Debug
                            : args.Event.Severity
                    });
            });

        // the indexer and searcher, explicitly for index registrations and as the defaults
        services.AddTransient<IWarpSearchIndexer, WarpSearchIndexer>();
        services.AddTransient<IWarpSearchSearcher, WarpSearchSearcher>();
        services.AddTransient<IIndexer, WarpSearchIndexer>();
        services.AddTransient<ISearcher, WarpSearchSearcher>();

        services.AddSingleton<IndexWriter>();
        services.AddSingleton<IIndexCultureProvider, LanguageIndexCultureProvider>();
        services.AddSingleton<ICollectionManager, CollectionManager>();
        services.AddSingleton<FieldNames>();
        services.AddSingleton<DocumentBuilder>();
        services.AddSingleton<SearchRequestBuilder>();

        return services;
    }
}
