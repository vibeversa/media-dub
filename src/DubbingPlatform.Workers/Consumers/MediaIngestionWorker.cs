using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Services;
using DubbingPlatform.Contracts.Messages;
using DubbingPlatform.Domain.Exceptions;
using DubbingPlatform.Domain.Identity;
using DubbingPlatform.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DubbingPlatform.Workers.Consumers;

/// <summary>
/// Ingests completed multipart uploads on <c>media.preparation</c>.
/// Intentionally an <c>IConsumer&lt;MediaUploaded&gt;</c> rather than
/// <see cref="BaseConsumer{TMessage}"/>: ingestion runs before any
/// <c>ProcessingRun</c> exists (the message carries
/// <c>ProcessingRunId=Guid.Empty</c>), while <c>BaseConsumer</c> requires a
/// run row and would park every ingestion in <c>_skipped</c>. At-least-once
/// semantics are preserved here: redeliveries hit the same idempotent
/// <see cref="MediaIngestionService"/> (same-project asset → <c>Duplicate</c>,
/// no new rows; otherwise existing rows are reused), and <c>MediaValidated</c>
/// is republished. Unsupported schema/identity → <c>_skipped</c> (no retry);
/// cross-tenant → <c>_error</c>; corrupt/unsupported media → <c>_skipped</c>
/// (fail fast, no transport retry); transient storage/I/O → rethrow into the
/// transport retry/fault pipeline (<c>_error</c> after exhaustion).
/// </summary>
public sealed class MediaIngestionWorker : IConsumer<MediaUploaded>
{
    private readonly MediaIngestionService _ingestion;
    private readonly ILogger<MediaIngestionWorker> _logger;

    public MediaIngestionWorker(MediaIngestionService ingestion, ILogger<MediaIngestionWorker> logger)
    {
        ArgumentNullException.ThrowIfNull(ingestion);
        ArgumentNullException.ThrowIfNull(logger);
        _ingestion = ingestion;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<MediaUploaded> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var message = context.Message;
        var correlationId = string.IsNullOrWhiteSpace(message.CorrelationId)
            ? context.CorrelationId?.ToString() ?? Guid.NewGuid().ToString("N")
            : message.CorrelationId;

        using (Serilog.Context.LogContext.PushProperty("CorrelationId", correlationId))
        using (Serilog.Context.LogContext.PushProperty("TenantId", message.TenantId))
        using (Serilog.Context.LogContext.PushProperty("ProjectId", message.ProjectId))
        using (Serilog.Context.LogContext.PushProperty("UploadSessionId", message.UploadSessionId ?? string.Empty))
        {
            if (!MessageVersionPolicy.IsSupported(message.SchemaVersion)
                || message.TenantId == Guid.Empty
                || message.ProjectId == Guid.Empty)
            {
                MessagingMeters.SchemaMismatches.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            Guid uploadId;
            try
            {
                uploadId = ParseUploadId(message.UploadSessionId);
            }
            catch (DomainException)
            {
                MessagingMeters.SchemaMismatches.Add(1);
                MessagingMeters.DlqDepth.Add(1);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            MediaIngestionResult result;
            try
            {
                using (TenantContext.BeginScope(message.TenantId))
                {
                    result = await _ingestion.IngestAsync(
                        message.TenantId, message.ProjectId, uploadId, context.CancellationToken).ConfigureAwait(false);
                }
            }
#pragma warning disable CA1031 // Poison routing contract: permanent ingestion failures park in _skipped; transient failures skip this filter and retry.
            catch (Exception ex) when (MessageDisposition.DecideFailure(ex) == MessageFate.Skipped)
#pragma warning restore CA1031
            {
                // Fail fast: corrupt/unsupported/incomplete/validation poison never retries.
                // Cross-tenant (Forbidden) is operational: route to _error for visibility.
                if (IsCrossTenant(ex))
                {
                    MessagingMeters.CrossTenantRejects.Add(1);
                    MessagingMeters.DlqDepth.Add(1);
                    await SendToQueueAsync(context, QueueNames.Error, message).ConfigureAwait(false);
                    return;
                }

                MessagingMeters.DlqDepth.Add(1);
                _logger.LogWarning(
                    "Media ingestion poison for upload {UploadId}: {Error}. Parked in _skipped.",
                    uploadId, ex.Message);
                await SendToQueueAsync(context, QueueNames.Skipped, message).ConfigureAwait(false);
                return;
            }

            if (result.IsDuplicate)
            {
                _logger.LogInformation(
                    "Upload {UploadId} duplicates asset {AssetId}; no MediaValidated published.",
                    uploadId, result.ExistingAssetId ?? result.MediaAssetId);
                return;
            }

            var validated = new MediaValidated(
                Guid.NewGuid(), correlationId,
                message.TenantId, message.ProjectId, Guid.Empty,
                null, "MediaValidation", "Project", message.ProjectId.ToString("N"), null,
                MessageVersionPolicy.CurrentVersion, DateTimeOffset.UtcNow, 0,
                result.ContentHash, null, null,
                PublicIdMapper.ToPublic(result.MediaAssetId, PublicIdMapper.MediaAssetPrefix),
                result.IsValid);
            await context.Publish(validated, context.CancellationToken).ConfigureAwait(false);
        }
    }

    internal static Guid ParseUploadId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new DomainException("UploadSessionId must not be empty.");
        }

        var trimmed = raw.Trim();
        if (Guid.TryParse(trimmed, out var guid) && guid != Guid.Empty)
        {
            return guid;
        }

        var compact = trimmed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (Guid.TryParseExact(compact, "N", out var compactGuid) && compactGuid != Guid.Empty)
        {
            return compactGuid;
        }

        var (prefix, id) = PublicIdMapper.FromPublic(trimmed);
        if (!string.Equals(prefix, PublicIdMapper.UploadSessionPrefix, StringComparison.Ordinal))
        {
            throw new DomainException($"UploadSessionId has an unexpected prefix '{prefix}'.");
        }

        return id;
    }

    private static bool IsCrossTenant(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.GetType().Name.Contains("Forbidden", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task SendToQueueAsync<T>(ConsumeContext<T> context, string queue, T message)
        where T : class
    {
        var endpoint = await context.GetSendEndpoint(new Uri($"queue:{queue}", UriKind.Absolute)).ConfigureAwait(false);
        await endpoint.Send(message, context.CancellationToken).ConfigureAwait(false);
    }
}
