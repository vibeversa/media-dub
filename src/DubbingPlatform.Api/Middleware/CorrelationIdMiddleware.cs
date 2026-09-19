using System.Diagnostics;
using DubbingPlatform.Infrastructure.Observability;
using Serilog.Context;

namespace DubbingPlatform.Api.Middleware;

/// <summary>
/// Reads the <c>X-Correlation-Id</c> request header or generates a random one
/// when missing, then exposes it via <c>HttpContext.Items["CorrelationId"]</c>,
/// the response header, <c>HttpContext.TraceIdentifier</c>, Serilog log context,
/// and the current <see cref="Activity"/> trace tag. Generated IDs are random
/// 32-character hex strings, never sequential.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";

    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = Resolve(context);
        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);
        var startedAt = Stopwatch.GetTimestamp();

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context).ConfigureAwait(false);
        }

        var elapsedMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        PlatformMetrics.ObserveApiLatency(elapsedMs, context.Request.Path.Value);
    }

    /// <summary>
    /// Gets the correlation ID for the current request, falling back to the
    /// trace identifier when the middleware has not run.
    /// </summary>
    public static string GetCorrelationId(HttpContext context)
    {
        if (context.Items.TryGetValue(ItemKey, out var value) &&
            value is string candidate &&
            !string.IsNullOrWhiteSpace(candidate))
        {
            return candidate;
        }

        return context.TraceIdentifier;
    }

    internal static string Resolve(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            var candidate = values.ToString().Trim();
            if (IsSafe(candidate))
            {
                return candidate;
            }
        }

        return Guid.NewGuid().ToString("N");
    }

    private static bool IsSafe(string candidate)
    {
        if (candidate.Length == 0 || candidate.Length > 128)
        {
            return false;
        }

        foreach (var c in candidate)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
