using System.Net;
using Polly;

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

    // a request that failed, or that the resilience pipeline rejected: timed out, or not sent because the circuit
    // breaker is open. Umbraco also references Polly 7, whose exceptions share the names, so match Polly 8 by assembly.
    internal static bool IsRequestFailure(Exception exception)
        => exception is WarpSearchException or HttpRequestException or TaskCanceledException
           || exception.GetType().Assembly == typeof(ResiliencePipeline).Assembly;
}
