using System.Diagnostics;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace CloudEmuera.Api.Security;

public static class RequestCorrelation
{
    private static readonly AsyncLocal<string?> CurrentValue = new();

    public static string? Current => CurrentValue.Value;

    public static IDisposable Push(string requestId)
    {
        string? previous = CurrentValue.Value;
        CurrentValue.Value = requestId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => CurrentValue.Value = previous;
    }
}

public static class RequestCorrelationMiddleware
{
    public static async Task InvokeAsync(HttpContext context, RequestDelegate next, ILoggerFactory loggerFactory)
    {
        string requestId = $"req_{Guid.CreateVersion7():N}";
        context.TraceIdentifier = requestId;
        context.Response.Headers["X-Request-ID"] = requestId;
        using IDisposable correlation = RequestCorrelation.Push(requestId);
        ILogger logger = loggerFactory.CreateLogger("CloudEmuera.Request");
        using IDisposable logScope = logger
            .BeginScope(new Dictionary<string, object?> { ["requestId"] = requestId })!;
        long started = Stopwatch.GetTimestamp();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        finally
        {
            string route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched";
            HttpRequestCompletedLog(logger, requestId, context.Request.Method, route,
                context.Response.StatusCode, checked((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds), null);
        }
    }

    private static readonly Action<ILogger, string, string, string, int, long, Exception?> HttpRequestCompletedLog =
        LoggerMessage.Define<string, string, string, int, long>(
            LogLevel.Information,
            new EventId(2001, "HttpRequestCompleted"),
            "http.request.completed requestId={RequestId} method={Method} route={Route} statusCode={StatusCode} durationMs={DurationMs}");
}
