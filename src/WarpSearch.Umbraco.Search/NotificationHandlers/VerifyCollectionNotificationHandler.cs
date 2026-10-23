using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Sync;
using WarpSearch.Umbraco.Search.Configuration;
using WarpSearch.Umbraco.Search.Services;

namespace WarpSearch.Umbraco.Search.NotificationHandlers;

/// <summary>
/// Checks the WarpSearch collection on startup. A missing API key or collection is logged instead of stopping the
/// site from starting.
/// </summary>
internal sealed class VerifyCollectionNotificationHandler : INotificationAsyncHandler<UmbracoApplicationStartingNotification>
{
    private readonly ICollectionManager _collectionManager;
    private readonly IServerRoleAccessor _serverRoleAccessor;
    private readonly WarpSearchOptions _options;
    private readonly ILogger<VerifyCollectionNotificationHandler> _logger;

    public VerifyCollectionNotificationHandler(
        ICollectionManager collectionManager,
        IServerRoleAccessor serverRoleAccessor,
        IOptions<WarpSearchOptions> options,
        ILogger<VerifyCollectionNotificationHandler> logger)
    {
        _collectionManager = collectionManager;
        _serverRoleAccessor = serverRoleAccessor;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(UmbracoApplicationStartingNotification notification, CancellationToken cancellationToken)
    {
        if (_serverRoleAccessor.CurrentServerRole is ServerRole.Subscriber)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _logger.LogError("WarpSearch:ApiKey is not configured. Search indexes stored in WarpSearch will not work until it is set.");
            return;
        }

        try
        {
            await _collectionManager.VerifyAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || cancellationToken.IsCancellationRequested is false)
        {
            _logger.LogError(exception, "Could not check WarpSearch collection {Collection}", _options.CollectionName);
        }
    }
}
