using System.Net.Sockets;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Enums;

namespace DubbingPlatform.Infrastructure.Messaging;

/// <summary>
/// Routing fate for a consumed integration message.
/// </summary>
public enum MessageFate
{
    /// <summary>Normal processing path.</summary>
    Process,

    /// <summary>Acknowledge without work (run is cancelling/cancelled).</summary>
    AckWithoutWork,

    /// <summary>Park in <c>_skipped</c> (version/identity/poison problems, no retry).</summary>
    Skipped,

    /// <summary>Park in <c>_error</c> or let the transport fault pipeline handle it.</summary>
    Error,
}

/// <summary>
/// Pure routing decisions shared by consumers and the saga so queue behavior is
/// unit-testable without a broker. Ordering is fixed: schema version first,
/// then identity, then run existence, then tenant, then cancellation.
/// </summary>
public static class MessageDisposition
{
    /// <summary>
    /// Decides the fate of a message given the loaded run state.
    /// <paramref name="runStatus"/> is null when the run row does not exist;
    /// <paramref name="runTenantId"/> is the run row's tenant for cross-tenant validation.
    /// </summary>
    public static MessageFate Decide(
        IntegrationMessage message,
        ProcessingRunStatus? runStatus,
        Guid? runTenantId)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!MessageVersionPolicy.IsSupported(message.SchemaVersion))
        {
            return MessageFate.Skipped;
        }

        if (message.TenantId == Guid.Empty
            || message.ProjectId == Guid.Empty
            || message.ProcessingRunId == Guid.Empty)
        {
            return MessageFate.Skipped;
        }

        if (runStatus is null)
        {
            return MessageFate.Error;
        }

        if (runTenantId != message.TenantId)
        {
            return MessageFate.Error;
        }

        if (runStatus is ProcessingRunStatus.Cancelling or ProcessingRunStatus.Cancelled)
        {
            return MessageFate.AckWithoutWork;
        }

        return MessageFate.Process;
    }

    /// <summary>
    /// Decides the fate of a handler failure. Transient transport-level failures
    /// rethrow into the MassTransit retry/fault pipeline (ending in
    /// <c>_error</c>); permanent poison is parked in <c>_skipped</c> with the
    /// <c>dlq.depth</c> counter, never retried.
    /// </summary>
    public static MessageFate DecideFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return IsTransient(exception) ? MessageFate.Error : MessageFate.Skipped;
    }

    /// <summary>
    /// Transient means worth a transport retry: HTTP I/O, timeouts, sockets, and
    /// general I/O. Everything else (validation, state, bugs, provider
    /// business errors) is poison for this delivery.
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is HttpRequestException
            or TimeoutException
            or SocketException
            or IOException;
    }
}
