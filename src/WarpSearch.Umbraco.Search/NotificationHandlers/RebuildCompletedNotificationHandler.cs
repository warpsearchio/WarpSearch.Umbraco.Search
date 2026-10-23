using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Search.Core.Notifications;
using WarpSearch.Umbraco.Search.Services;

namespace WarpSearch.Umbraco.Search.NotificationHandlers;

/// <summary>
/// Writes and commits the last documents of a rebuild.
/// </summary>
internal sealed class RebuildCompletedNotificationHandler : INotificationAsyncHandler<IndexRebuildCompletedNotification>
{
    private readonly IndexWriter _writer;

    public RebuildCompletedNotificationHandler(IndexWriter writer)
        => _writer = writer;

    public async Task HandleAsync(IndexRebuildCompletedNotification notification, CancellationToken cancellationToken)
        => await _writer.CompleteRebuildAsync(notification.IndexAlias);
}
