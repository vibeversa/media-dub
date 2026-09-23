using DubbingPlatform.Application.Activity;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.Exports;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Notifications;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Infrastructure.Messaging;
using DubbingPlatform.Infrastructure.Observability;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// On-demand export generation on the <c>export</c> queue (independent of the
/// core DAG per the task Context: export was removed from the DAG).
/// Intentionally a plain <see cref="IConsumer{T}"/> rather than
/// <see cref="BaseConsumer{TMessage}"/>: the base consumer
/// acknowledge-without-work gate drops messages for
/// <c>Cancelling/Cancelled</c> runs, which would make partial exports for
/// cancelled runs impossible (R5). This worker instead validates only schema
/// version and tenant identity itself (poison to <c>_skipped</c>,
/// cross-tenant/missing-run to <c>_error</c>, mirroring
/// <see cref="MessageDisposition"/>), early-returns on terminal export jobs
/// (Completed/Failed/Cancelled — a <c>Cancelled</c> job produces no artifact,
/// owned by Task 035 cancellation), and delegates to
/// <see cref="ExportService.GenerateAsync"/> which owns the
/// <c>Pending→Running→Completed|Failed</c> transitions, the immutable
/// <c>Export</c> artifact, and the completeness block. On terminal success or
/// permanent failure the worker best-effort projects to the Task 002
/// <see cref="NotificationProjector"/> (via
/// <see cref="NotificationEventMapper.FromExport"/>) and
/// <see cref="ActivityProjector"/> (via
/// <see cref="ActivityEventMapper.FromExportCompleted"/>); creation itself is
/// audit-only (<c>export.create</c> with actor + idempotency key in
/// <see cref="ExportService"/>) plus the best-effort <c>ExportJobRequested</c>
/// bus publish — there is no <c>ExportCreated</c> activity type by design.
/// Transient I/O rethrows
/// into transport retry; permanent failures are already marked
/// <c>Failed</c> by the service and acknowledged without retry. Never logs
/// export text or secrets: only ids, formats, and counts.
/// </summary>
public sealed class ExportWorker : IConsumer<ExportJobRequested>
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly ExportService _exports;
    private readonly NotificationProjector _notifications;
    private readonly ActivityProjector _activity;
    private readonly ILogger<ExportWorker> _logger;

    public ExportWorker(
        IStageExecutionContextFactory contextFactory,
        ExportService exports,
        NotificationProjector notifications,
        ActivityProjector activity,
        ILogger<ExportWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(exports);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _exports = exports;
        _notifications = notifications;
        _activity = activity;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<ExportJobRequested> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(message.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : message.CorrelationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        using (Serilog.Context.LogContext.PushProperty("TenantId", message.TenantId))
        using (Serilog.Context.LogContext.PushProperty("ProjectId", message.ProjectId))
        using (Serilog.Context.LogContext.PushProperty("ProcessingRunId", message.ProcessingRunId))
        using (Serilog.Context.LogContext.PushProperty("ExportJobId", message.ExportJobId))
        {
            if (!MessageVersionPolicy.IsSupported(message.SchemaVersion)
                || message.TenantId == Guid.Empty
                || message.ProjectId == Guid.Empty
                || message.ProcessingRunId == Guid.Empty
                || string.IsNullOrWhiteSpace(message.ExportJobId))
            {
                MessagingMeters.SchemaMismatches.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            if (!TryParseExportId(message.ExportJobId, out var exportJobId))
            {
                _logger.LogWarning("Skipping export message with unparsable export id.");
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            if (!ExportFormatParser.TryParse(message.Format, out var format))
            {
                _logger.LogWarning("Skipping export {ExportId}: unknown format.", exportJobId);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            var runTenant = await LoadRunTenantAsync(message.ProcessingRunId, context.CancellationToken).ConfigureAwait(false);
            if (runTenant is null)
            {
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            if (runTenant.Value != message.TenantId)
            {
                MessagingMeters.CrossTenantRejects.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            ExportJob? job = await LoadJobAsync(exportJobId, context.CancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            if (job.TenantId != message.TenantId || job.ProjectId != message.ProjectId)
            {
                MessagingMeters.CrossTenantRejects.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                return;
            }

            if (job.Status is ExportJobStatus.Completed or ExportJobStatus.Failed or ExportJobStatus.Cancelled)
            {
                return;
            }

            try
            {
                var result = await _exports.GenerateAsync(
                    message.TenantId, message.ProjectId, exportJobId,
                    context.CancellationToken).ConfigureAwait(false);
                PlatformMetrics.ExportGenerated(message.TenantId);
                _logger.LogInformation(
                    "Export {ExportId} ({Format}) completed: artifact {ArtifactId}, partial={Partial}.",
                    exportJobId, ExportFormatParser.ToWireName(format),
                    result.ArtifactId, result.IsPartial);
                await ProjectExportAsync(
                    message, format, success: true, correlationId,
                    context.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                throw;
            }
#pragma warning disable CA1031 // Worker poison contract: permanent export failures are already marked Failed; acknowledge without retry.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                PlatformMetrics.ExportFailed(message.TenantId);
                _logger.LogWarning(
                    "Export {ExportId} failed: {Error}.",
                    exportJobId, ex.Message);
                await ProjectExportAsync(
                    message, format, success: false, correlationId,
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Guid?> LoadRunTenantAsync(Guid runId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            var run = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
            return run?.TenantId;
        }
    }

    private async Task<ExportJob?> LoadJobAsync(Guid exportJobId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            return await db.Set<ExportJob>()
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == exportJobId, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProjectExportAsync(
        ExportJobRequested message,
        ExportFormat format,
        bool success,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (!TryParseExportId(message.ExportJobId, out var exportJobId))
        {
            return;
        }

        var wireFormat = ExportFormatParser.ToWireName(format);
        try
        {
            await _notifications.ProjectAsync(
                NotificationEventMapper.FromExport(
                    message.TenantId, message.ProjectId, exportJobId, wireFormat, success, message.MessageId),
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Projection is best effort; export outcome already committed.
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        try
        {
            await _activity.AppendAsync(
                ActivityEventMapper.FromExportCompleted(
                    message.TenantId, message.ProjectId,
                    message.ProcessingRunId == Guid.Empty ? null : message.ProcessingRunId,
                    exportJobId, wireFormat, success, correlationId, DateTimeOffset.UtcNow),
                cancellationToken).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Projection is best effort; export outcome already committed.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private static bool TryParseExportId(string raw, out Guid exportJobId)
    {
        exportJobId = Guid.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = raw.Trim();
        if (Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty)
        {
            exportJobId = guid;
            return true;
        }

        var compact = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (Guid.TryParseExact(compact, "N", out var compactGuid) && compactGuid != Guid.Empty)
        {
            exportJobId = compactGuid;
            return true;
        }

        try
        {
            var (_, id) = DubbingPlatform.Domain.Identity.PublicIdMapper.FromPublic(trimmed);
            if (id == Guid.Empty)
            {
                return false;
            }

            exportJobId = id;
            return true;
        }
        catch (DomainException)
        {
            return false;
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
