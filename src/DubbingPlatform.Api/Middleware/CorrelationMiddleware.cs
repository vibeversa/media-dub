using DubbingPlatform.Api.Observability;

namespace DubbingPlatform.Api.Middleware;

/// <summary>
/// Task 038 naming facade for end-to-end correlation. The canonical transport
/// remains <see cref="CorrelationIdMiddleware"/> (header
/// <c>X-Correlation-Id</c>, echoed on responses and error envelopes per Task
/// 013, generated per user action by the frontend transport per Task 017); this
/// type adds the Task 038 propagation contract without forking that bootstrap:
/// <list type="bullet">
/// <item><see cref="ResolveWithPropagation"/> — pure: safe caller ids pass
/// through with <c>propagated=true</c>; missing/unsafe ids are minted
/// (random 32-hex, never sequential) with <c>propagated=false</c>.</item>
/// <item><see cref="InvokeAsync"/> — directly invokable validator middleware
/// that records <c>HttpContext.Items["CorrelationId"]</c>,
/// <c>Items["CorrelationPropagated"]</c>, the response header, and the
/// <c>correlation.minted_total</c> outcome counter. It is intentionally NOT in
/// the <c>Program.cs</c> pipeline (the canonical middleware owns the pipeline)
/// so existing echo/envelope behavior cannot regress; tests invoke it directly.</item>
/// </list>
/// </summary>
public sealed class CorrelationMiddleware
{
    public const string HeaderName = CorrelationIdMiddleware.HeaderName;

    public const string ItemKey = CorrelationIdMiddleware.ItemKey;

    /// <summary>Items key for the propagation flag (<c>true</c> when the caller supplied the id).</summary>
    public const string PropagatedItemKey = "CorrelationPropagated";

    private readonly RequestDelegate _next;

    public CorrelationMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var raw = context.Request.Headers.TryGetValue(HeaderName, out var values)
            ? values.ToString()
            : null;
        var (correlationId, propagated) = ResolveWithPropagation(raw);
        context.Items[ItemKey] = correlationId;
        context.Items[PropagatedItemKey] = propagated;
        context.Response.Headers[HeaderName] = correlationId;
        BackendMetrics.RecordCorrelationOutcome(propagated);
        await _next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves a correlation id with its propagation outcome. Pure.
    /// </summary>
    public static (string CorrelationId, bool Propagated) ResolveWithPropagation(string? headerValue)
    {
        var candidate = headerValue?.Trim() ?? string.Empty;
        if (IsPropagatedId(candidate))
        {
            return (candidate, true);
        }

        return (Guid.NewGuid().ToString("N"), false);
    }

    /// <summary>
    /// Reads the propagation flag recorded by <see cref="InvokeAsync"/>.
    /// Returns <c>null</c> when this middleware has not run.
    /// </summary>
    public static bool? WasPropagated(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Items.TryGetValue(PropagatedItemKey, out var value) && value is bool propagated)
        {
            return propagated;
        }

        return null;
    }

    /// <summary>
    /// Same acceptance policy as the canonical middleware: 1–128 chars of
    /// letters, digits, <c>-</c>, <c>_</c>. Anything else is a legacy path and
    /// gets a minted id. Pure.
    /// </summary>
    public static bool IsPropagatedId(string? candidate)
    {
        if (string.IsNullOrEmpty(candidate) || candidate.Length > 128)
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
