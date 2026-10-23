using Umbraco.Cms.Search.BackOffice.DependencyInjection;
using Umbraco.Cms.Search.Core.DependencyInjection;
using Umbraco.Cms.Search.Core.Extensions;
using Umbraco.Cms.Search.Core.Models.Searching;
using Umbraco.Cms.Search.Core.Models.Searching.Faceting;
using Umbraco.Cms.Search.Core.Services;
using WarpSearch.Umbraco.Search.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using SearchConstants = Umbraco.Cms.Search.Core.Constants;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

#if DEBUG
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
#endif

builder.CreateUmbracoBuilder()
    .AddBackOffice()
    .AddWebsite()
    .AddComposers()
    .AddSearchCore()
    .AddWarpSearchProvider()
    .AddBackOfficeSearch()
    .Build();

WebApplication app = builder.Build();

await app.BootUmbracoAsync();

app.UseUmbraco()
    .WithMiddleware(u =>
    {
        u.UseBackOffice();
        u.UseWebsite();
    })
    .WithEndpoints(u =>
    {
        u.UseBackOfficeEndpoints();
        u.UseWebsiteEndpoints();

        // a minimal search API over the published content index: /api/search?q=...&culture=en-US
        u.EndpointRouteBuilder.MapGet("/api/search", async (string? q, string? culture, int? skip, ISearcherResolver searcherResolver) =>
        {
            ISearcher searcher = searcherResolver.GetRequiredSearcher(SearchConstants.IndexAliases.PublishedContent);
            SearchResult result = await searcher.SearchAsync(
                SearchConstants.IndexAliases.PublishedContent,
                query: q,
                facets: [new KeywordFacet(SearchConstants.FieldNames.ContentTypeId)],
                culture: culture,
                skip: skip ?? 0,
                take: 10);
            return Results.Ok(new
            {
                query = q,
                culture,
                result.Total,
                documents = result.Documents.Select(document => document.Id),
                facets = result.Facets.ToDictionary(
                    facet => facet.FieldName,
                    facet => facet.Values.OfType<KeywordFacetValue>().ToDictionary(value => value.Key, value => value.Count))
            });
        });

        // unauthenticated demo endpoints: Development only, and only on localhost
        if (app.Environment.IsDevelopment())
        {
            // edits and publishes a product within a request, like an editor would; search follows the change
            u.EndpointRouteBuilder.MapPost("/api/demo/edit", (string name, string description, IContentService contentService) =>
            {
                // products sit right below the catalog
                IContent? product = contentService.GetByLevel(2).FirstOrDefault(content => content.GetCultureName("en-US") == name);
                if (product is null)
                {
                    return Results.NotFound();
                }

                product.SetValue("description", description, "en-US");
                contentService.Save(product);
                contentService.Publish(product, ["en-US"]);
                return Results.Ok(product.Key);
            }).RequireHost("localhost:*", "127.0.0.1:*");

            // rebuilds every search index, like the Rebuild button in Settings > Search
            u.EndpointRouteBuilder.MapPost("/api/demo/rebuild", (IContentIndexingService contentIndexingService, IOriginProvider originProvider, IOptions<IndexOptions> indexOptions) =>
            {
                string[] indexAliases = indexOptions.Value.GetContentIndexRegistrations().Select(registration => registration.IndexAlias).ToArray();
                foreach (var indexAlias in indexAliases)
                {
                    contentIndexingService.Rebuild(indexAlias, originProvider.GetCurrent());
                }

                return Results.Ok(indexAliases);
            }).RequireHost("localhost:*", "127.0.0.1:*");
        }
    });

await app.RunAsync();
