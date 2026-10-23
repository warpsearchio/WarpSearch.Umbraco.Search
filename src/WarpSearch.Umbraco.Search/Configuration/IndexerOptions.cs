namespace WarpSearch.Umbraco.Search.Configuration;

public sealed class IndexerOptions
{
    /// <summary>
    /// The maximum number of documents sent to WarpSearch in one write request while rebuilding an index.
    /// Keep it within your plan's documents per write request.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// The page size used when looking up documents to delete. Keep it within your plan's hits per search.
    /// </summary>
    public int DeleteLookupPageSize { get; set; } = 50;

    /// <summary>
    /// A rebuild that writes nothing for this long is considered finished and its pending documents are written,
    /// in case Umbraco never reports the rebuild as completed.
    /// </summary>
    public TimeSpan RebuildIdleFlush { get; set; } = TimeSpan.FromSeconds(10);
}
