# WarpSearch for Umbraco Search

[WarpSearch](https://warpsearch.io) is a serverless hybrid search engine. This package is a provider for
[Umbraco Search](https://docs.umbraco.com/umbraco-search): it stores your Umbraco search indexes in WarpSearch and
answers searches from there, so you get fast full-text search, filters and facets without running a search
cluster or keeping Lucene indexes on your web servers.

- Full-text search with Umbraco's relevance tiers (`TextsR1` to `TextsR3`), prefix matching as you type
- Keyword, integer, decimal and date filters, exact and range
- Exact and range facets with multi-select counts (each facet ignores its own selection)
- Sorting by score, text, keyword, number and date, and paging
- Culture and segment variants, and protected content by member and member group
- The backoffice index overview, health, rebuild and test search work as with any provider
- All indexes live in one WarpSearch collection, so it runs on the free plan
- Rebuilds are sent in batches, so a rebuild costs a few requests per hundred items
- Load-balanced sites: every server reads the same collection, and only the server where a change happens writes it

## Requirements

- Umbraco 18 with Umbraco Search (`Umbraco.Cms.Search.Core` 18.2 or later)
- A WarpSearch account. The free plan is enough to start.

## Set up WarpSearch

The package does not create collections, because that takes an admin key and the site only needs to write. Set up
WarpSearch once in the [console](https://warpsearch.io/console/):

1. Create a collection, for example `mysite_prod`. Leave the mapping empty: the package names its fields so
   WarpSearch types them automatically.
2. Create an API key with the **writer** role for the site.

Use one collection per site and environment. All of a site's search indexes share its collection.

## Installation

```bash
dotnet add package WarpSearch.Umbraco.Search
```

Register Umbraco Search and the provider in `Program.cs`:

```csharp
using Umbraco.Cms.Search.Core.DependencyInjection;
using WarpSearch.Umbraco.Search.DependencyInjection;

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddDeliveryApi()
    .AddComposers()
    .AddSearchCore()
    .AddWarpSearchProvider()
    .Build();
```

Add the collection and API key to `appsettings.json`. Keep it out of source control, for example in user secrets or an environment
variable (`WarpSearch__ApiKey`):

```json
{
  "WarpSearch": {
    "ApiKey": "ws_live_...",
    "Collection": "mysite_prod"
  }
}
```

Start the site and rebuild the indexes from **Settings > Search** in the backoffice. On startup the provider checks
the collection and logs what to do if it is missing or the key cannot write to it. From then on, content is indexed
as editors save and publish it, and is searchable within a second.

## Searching

Search with Umbraco Search's `ISearcherResolver`, as with any provider:

```csharp
ISearcher searcher = searcherResolver.GetRequiredSearcher(Constants.IndexAliases.PublishedContent);
SearchResult result = await searcher.SearchAsync(
    Constants.IndexAliases.PublishedContent,
    query: "running shoes",
    filters: [new KeywordFilter("brand", ["acme"], false)],
    facets: [new KeywordFacet("brand"), new DecimalRangeFacet("price", [new("cheap", null, 50m), new("premium", 50m, null)])],
    sorters: [new ScoreSorter(Direction.Descending)],
    culture: "en-US",
    skip: 0,
    take: 10);
```

See the [Umbraco Search documentation](https://docs.umbraco.com/umbraco-search) for filters, facets, sorting and
protected content.

## Configuration

All settings live under `WarpSearch`:

| Setting | Default | Description |
|---|---|---|
| `ApiKey` | | Your WarpSearch API key, with the writer role on the collection. Required. |
| `Collection` | `umbraco` | The WarpSearch collection for this site's indexes. Create it in the console first. |
| `TextAnalysis` | `Standard` | `Standard` folds case and works for every language. `English` adds accent folding and English stemming (`ponies` finds `pony`). Rebuild the indexes after changing it. |
| `RequestTimeout` | `00:00:30` | Timeout of a single request. Rate-limited, warming-up and failed requests are retried with backoff, honoring `Retry-After`. |
| `Indexer:BatchSize` | `100` | Documents per write request during a rebuild. Keep it within your plan's documents per request. |
| `Indexer:DeleteLookupPageSize` | `50` | Page size when finding documents to delete. Keep it within your plan's hits per search. |
| `Indexer:RebuildIdleFlush` | `00:00:10` | A rebuild that writes nothing for this long is treated as finished. |
| `Searcher:MaxFacetValues` | `100` | Values returned per exact facet. Keep it within your plan's facet size. |
| `Searcher:BoostFactorTextR1` to `R3` | `6`, `4`, `2` | Weight of each relevance tier in free-text search. |

The package always talks to `https://api.warpsearch.io`.

### Using WarpSearch for some indexes only

`AddWarpSearchProvider()` moves all four built-in indexes to WarpSearch. To keep some on Examine, register
WarpSearch yourself and choose the indexes:

```csharp
builder.Services.AddWarpSearch(builder.Config);
builder.AddWarpSearchNotificationHandlers();
builder.Services.Configure<IndexOptions>(options =>
    options.RegisterWarpSearchContentIndex<IPublishedContentChangeStrategy>(
        Constants.IndexAliases.PublishedContent,
        UmbracoObjectTypes.Document));
```

Custom indexes are registered the same way.

## Good to know

- **Plan limits.** Your plan sets the hits per search, paging depth, facets per search and requests per second.
  A search beyond them is logged and returns no results, so keep page sizes and facet counts within your plan.
  `GET https://api.warpsearch.io/v1/usage` shows your limits.
- **Documents.** Each culture of a content item is one WarpSearch document, so the document count in the backoffice
  counts cultures.
- **Rebuilds.** A rebuild deletes the index's documents and writes them again, so results are incomplete until it
  finishes. Documents written during a rebuild become searchable when it completes.
- **After startup** Umbraco runs its own Examine index job first, so changes made in the first minute after a
  restart are indexed once that job is done.
- **Field names.** WarpSearch field names allow letters, digits and single underscores, so other characters in
  Umbraco field names are replaced. `my-field` and `my_field` therefore share a WarpSearch field.
- **Suggestions and highlighting** are not supported yet. `maxSuggestions` is ignored.

## Development

You need the .NET 10 SDK. The solution has the package, its tests and a demo site:

| Project | |
|---|---|
| `src/WarpSearch.Umbraco.Search` | The package. |
| `tests/WarpSearch.Umbraco.Search.Tests` | Integration tests against a real WarpSearch, plus a few that need nothing. |
| `src/WarpSearch.Umbraco.Search.Site` | An Umbraco 18 site that seeds a small English and Danish product catalog on first start and serves `/api/search?q=...&culture=en-US`. |

```bash
dotnet build
WARPSEARCH_API_KEY=ws_live_... WARPSEARCH_COLLECTION=umbracotest dotnet test
```

The integration tests write to the collection in `WARPSEARCH_COLLECTION` (default `umbracotest`), which you create
in the console and give the key the writer role on. Each run uses its own `test_...` indexes in it and deletes their
documents afterwards. Without `WARPSEARCH_API_KEY` the integration tests are **skipped**, not failed, so check the
summary for skipped tests.

To run the demo site, copy `src/WarpSearch.Umbraco.Search.Site/appsettings.Local.json.example` to
`appsettings.Local.json`, add your API key and collection, and `dotnet run` in that folder. The backoffice login is
`admin@example.com` / `Admin1234567!`. The demo site is for local use only: in Development it serves unauthenticated
demo endpoints (`/api/demo/edit`, `/api/demo/rebuild`) that answer only on localhost.

Restore reports vulnerability advisories (NU1902, NU1903) for packages that Umbraco depends on. They are reported as
warnings and do not fail the build.

## License

MIT. The document layout and query translation follow Kenn Jacobsen's
[Elasticsearch provider for Umbraco Search](https://github.com/kjac/Kjac.SearchProvider.Elasticsearch); see `NOTICE`.
