using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Security;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Durable logical deletion on the <c>maintenance</c> queue, independent of
/// the core DAG. Intentionally a plain <see cref="IConsumer{T}"/> rather than
/// <see cref="BaseConsumer{TMessage}"/>: deletion jobs are project-scoped
/// (the envelope carries an empty <c>ProcessingRunId</c> by contract), so the
/// run-scoped claim/lease gates do not apply. Poison (unsupported version or
/// empty identity) goes to <c>_skipped</c>; missing jobs and cross-tenant
/// mismatches go to <c>_error</c> with both the messaging and the
/// <c>security.cross_tenant_rejected</c> counters. Execution delegates to
/// <see cref="RetentionService.ExecutePendingAsync"/>, whose conditional claim
/// serializes duplicate deliveries to exactly one winner. A
/// <c>POLICY_DENIED</c> hold is acknowledged (the daily
/// <c>RetentionSweeper</c> retries after hold release via stale-takeover);
/// transient I/O rethrows into transport retry. Never logs subjects, evidence,
/// or secrets: only ids.
/// </summary>
public sealed class DeletionJobWorker : IConsumer<DeletionJobRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly RetentionService _retention;
    private readonly ILogger<DeletionJobWorker> _logger;

    public DeletionJobWorker(
        IStageExecutionContextFactory contextFactory,
        RetentionService retention,
        ILogger<DeletionJobWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(retention);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _retention = retention;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<DeletionJobRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(message.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : message.CorrelationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        using (Serilog.Context.LogContext.PushProperty("TenantId", message.TenantId))
        using (Serilog.Context.LogContext.PushProperty("ProjectId", message.ProjectId))
        using (Serilog.Context.LogContext.PushProperty("DeletionJobId", message.DeletionJobId))
        {
            if (!MessageVersionPolicy.IsSupported(message.SchemaVersion)
                || message.TenantId == Guid.Empty
                || message.ProjectId == Guid.Empty
                || message.DeletionJobId == Guid.Empty)
            {
                MessagingMeters.SchemaMismatches.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            var job = await LoadJobAsync(message.DeletionJobId, context.CancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            if (job.TenantId != message.TenantId || job.ProjectId != message.ProjectId)
            {
                MessagingMeters.CrossTenantRejects.Add(1);
                SecurityMeters.CrossTenantRejects.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            if (job.Status is "Completed" or "Failed" or "Cancelled")
            {
                return;
            }

            try
            {
                var executed = await _retention.ExecutePendingAsync(
                    message.TenantId, message.DeletionJobId,
                    context.CancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Deletion job {JobId} finished with status {Status}.",
                    message.DeletionJobId, executed.Status);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw;
            }
            catch (ErrorCodeException ex) when (string.Equals(ex.ErrorCode, ErrorCodes.PolicyDenied, StringComparison.Ordinal))
            {
                _logger.LogWarning(
                    "Deletion job {JobId} blocked by hold; acknowledged for sweeper retry: {Error}.",
                    message.DeletionJobId, ex.Message);
            }
        }
    }

    private async Task<DeletionJob?> LoadJobAsync(Guid deletionJobId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<DeletionJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(j => j.Id == deletionJobId, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsTransient(Exception exception)
    {
        return exception is HttpRequestException
            or TimeoutException
            or System.Net.Sockets.SocketException
            or IOException;
    }

    private static async Task SendToQueueAsync<T>(ConsumeContext<T> context, string queue, T message)
        where T : class
    {
        var endpoint = await context.GetSendEndpoint(new Uri(string.Concat("queue:", queue))).ConfigureAwait(false);
        await endpoint.Send(message, context.CancellationToken).ConfigureAwait(false);
    }
}
