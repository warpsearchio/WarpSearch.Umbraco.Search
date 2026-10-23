using System.Net;

namespace WarpSearch.Umbraco.Search.Client;

/// <summary>
/// A failed WarpSearch request. <see cref="Code"/> is WarpSearch's machine-readable error code when one was returned.
/// </summary>
public sealed class WarpSearchException : Exception
{
    public WarpSearchException(HttpStatusCode statusCode, string? code, string? requestId, string message)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
        RequestId = requestId;
    }

    public HttpStatusCode StatusCode { get; }

    public string? Code { get; }

    public string? RequestId { get; }
}
