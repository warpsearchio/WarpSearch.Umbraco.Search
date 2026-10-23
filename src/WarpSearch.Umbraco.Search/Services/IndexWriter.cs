using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WarpSearch.Umbraco.Search.Client;
using WarpSearch.Umbraco.Search.Configuration;

namespace WarpSearch.Umbraco.Search.Services;

/// <summary>
/// Sends document writes to WarpSearch. Outside a rebuild every write is committed at once, so editors see their
/// changes in search right away. During a rebuild writes are collected and sent in batches, which keeps a rebuild
/// to a few requests per hundred documents, and are committed when the rebuild completes.
/// </summary>
internal sealed class IndexWriter : IDisposable
{
    private readonly IWarpSearchClient _client;
    private readonly IndexerOptions _options;
    private readonly string _collection;
    private readonly ILogger<IndexWriter> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Rebuild> _rebuilds = new(StringComparer.OrdinalIgnoreCase);

    public IndexWriter(
        IWarpSearchClient client,
        IOptions<IndexerOptions> options,
        IOptions<WarpSearchOptions> warpSearchOptions,
        ILogger<IndexWriter> logger)
    {
        _client = client;
        _options = options.Value;
        _collection = warpSearchOptions.Value.CollectionName;
        _logger = logger;
    }

    public bool IsRebuilding(string indexAlias)
    {
        lock (_lock)
        {
            return _rebuilds.ContainsKey(indexAlias);
        }
    }

    public void BeginRebuild(string indexAlias)
    {
        lock (_lock)
        {
            if (_rebuilds.ContainsKey(indexAlias))
            {
                return;
            }

            // a rebuild that goes quiet is completed, in case Umbraco never reports its completion
            var timer = new Timer(
                _ => _ = CompleteRebuildSafelyAsync(indexAlias),
                null,
                _options.RebuildIdleFlush,
                Timeout.InfiniteTimeSpan);
            _rebuilds[indexAlias] = new Rebuild(timer);
        }
    }

    public async Task CompleteRebuildAsync(string indexAlias)
    {
        BulkAction[] pending;
        bool uncommitted;
        lock (_lock)
        {
            if (_rebuilds.Remove(indexAlias, out Rebuild? rebuild) is false)
            {
                return;
            }

            rebuild.IdleTimer.Dispose();
            pending = rebuild.Pending.ToArray();
            uncommitted = rebuild.Uncommitted;
        }

        // the last batch commits everything written during the rebuild; commits are billed, so only commit when
        // something is waiting for one
        if (pending.Length > 0)
        {
            await SendAsync(pending, refresh: true);
        }
        else if (uncommitted)
        {
            await _client.RefreshAsync(_collection);
        }
    }

    public async Task WriteAsync(string indexAlias, IReadOnlyCollection<BulkAction> actions)
    {
        BulkAction[]? batch = null;
        lock (_lock)
        {
            if (_rebuilds.TryGetValue(indexAlias, out Rebuild? rebuild))
            {
                rebuild.Pending.AddRange(actions);
                rebuild.IdleTimer.Change(_options.RebuildIdleFlush, Timeout.InfiniteTimeSpan);
                if (rebuild.Pending.Count < _options.BatchSize)
                {
                    return;
                }

                batch = rebuild.Pending.ToArray();
                rebuild.Pending.Clear();
                rebuild.Uncommitted = true;
            }
        }

        if (batch is not null)
        {
            await SendAsync(batch, refresh: false);
            return;
        }

        await SendAsync(actions.ToArray(), refresh: true);
    }

    /// <summary>
    /// Sends and commits the writes collected so far, so a following lookup sees them.
    /// </summary>
    public async Task FlushAsync(string indexAlias)
    {
        BulkAction[] pending;
        lock (_lock)
        {
            if (_rebuilds.TryGetValue(indexAlias, out Rebuild? rebuild) is false || rebuild.Pending.Count is 0)
            {
                return;
            }

            pending = rebuild.Pending.ToArray();
            rebuild.Pending.Clear();
            rebuild.Uncommitted = false;
        }

        await SendAsync(pending, refresh: true);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (Rebuild rebuild in _rebuilds.Values)
            {
                rebuild.IdleTimer.Dispose();
            }

            _rebuilds.Clear();
        }
    }

    private async Task CompleteRebuildSafelyAsync(string indexAlias)
    {
        try
        {
            await CompleteRebuildAsync(indexAlias);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Could not write the last documents of the rebuild of index {IndexAlias} to WarpSearch", indexAlias);
        }
    }

    // a batch over the plan's request size is split in halves until it fits
    private async Task SendAsync(BulkAction[] actions, bool refresh)
    {
        IReadOnlyList<BulkFailure> failures;
        try
        {
            failures = await _client.BulkAsync(_collection, actions, refresh);
        }
        catch (WarpSearchException exception) when (exception.StatusCode is HttpStatusCode.RequestEntityTooLarge && actions.Length > 1)
        {
            var half = actions.Length / 2;
            await SendAsync(actions[..half], false);
            await SendAsync(actions[half..], refresh);
            return;
        }

        foreach (BulkFailure failure in failures)
        {
            _logger.LogError(
                "WarpSearch could not write document {DocumentId} in {Collection}: {ErrorCode} {ErrorMessage}",
                failure.Id,
                _collection,
                failure.Code,
                failure.Message);
        }
    }

    private sealed record Rebuild(Timer IdleTimer)
    {
        public List<BulkAction> Pending { get; } = [];

        // a batch was written without a commit
        public bool Uncommitted { get; set; }
    }
}
