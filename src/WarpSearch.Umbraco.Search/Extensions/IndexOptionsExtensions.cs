using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Search.Core.Configuration;
using Umbraco.Cms.Search.Core.Services.ContentIndexing;
using WarpSearch.Umbraco.Search.Services;

namespace WarpSearch.Umbraco.Search.Extensions;

public static class IndexOptionsExtensions
{
    /// <summary>
    /// Registers a content index stored in WarpSearch. Only the server where a change happens writes it to
    /// WarpSearch, since all servers share the same collection.
    /// </summary>
    public static IndexOptions RegisterWarpSearchContentIndex<TContentChangeStrategy>(
        this IndexOptions indexOptions,
        string indexAlias,
        params UmbracoObjectTypes[] containedObjectTypes)
        where TContentChangeStrategy : class, IContentChangeStrategy
    {
        indexOptions.RegisterContentIndex<IWarpSearchIndexer, IWarpSearchSearcher, TContentChangeStrategy>(
            indexAlias,
            sameOriginOnly: true,
            containedObjectTypes);
        return indexOptions;
    }
}
