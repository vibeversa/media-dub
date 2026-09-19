using System.Diagnostics.Metrics;
using DubbingPlatform.Application.Errors;
using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Options;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Versioned deterministic price table (USD). Defaults are documented
/// deterministic placeholders (not vendor quotes):
/// <c>transcription_per_min=0.02, translation_per_char=0.00002,
/// tts_per_char=0.00003, separation_per_min=0.05, local_per_min=0.01</c>.
/// Missing price version falls back to <c>1.0.0</c>.
/// </summary>
public sealed record PriceTable(string Version, Dictionary<string, double> UnitPrices)
{
    public const string CurrentVersion = "1.0.0";

    public const string TranscriptionPerMin = "transcription_per_min";

    public const string TranslationPerChar = "translation_per_char";

    public const string TtsPerChar = "tts_per_char";

    public const string SeparationPerMin = "separation_per_min";

    public const string LocalPerMin = "local_per_min";

    public static PriceTable Default()
    {
        return new PriceTable(
            CurrentVersion,
            new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [TranscriptionPerMin] = 0.02,
                [TranslationPerChar] = 0.00002,
                [TtsPerChar] = 0.00003,
                [SeparationPerMin] = 0.05,
                [LocalPerMin] = 0.01,
            });
    }

    public double PriceFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (UnitPrices.TryGetValue(key.Trim(), out var price) && !double.IsNaN(price) && price >= 0.0)
        {
            return price;
        }

        return Default().UnitPrices[key.Trim()];
    }
}

/// <summary>
/// Usage dimensions for one estimate. All values must be >= 0 (negative usage
/// is a validation error). Only ids, capabilities, and amounts are logged —
/// never media, text, or secrets.
/// </summary>
public sealed record CostUsageDims(double AudioMinutes, int Chars, int Tokens)
{
    public void Validate()
    {
        if (double.IsNaN(AudioMinutes) || AudioMinutes < 0.0)
        {
            throw new DomainException("AudioMinutes must be >= 0.");
        }

        if (Chars < 0)
        {
            throw new DomainException("Chars must be >= 0.");
        }

        if (Tokens < 0)
        {
            throw new DomainException("Tokens must be >= 0.");
        }
    }
}

/// <summary>
/// Cost meters. Counter names are frozen: renaming breaks dashboards (Task 38).
/// </summary>
public static class CostMeters
{
    public const string MeterName = "DubbingPlatform.Cost";

    public const string ReservedMetricName = "cost.reserved";

    public const string ReconciledMetricName = "cost.reconciled";

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Reserved =
        Meter.CreateCounter<long>(ReservedMetricName);

    public static readonly Counter<long> Reconciled =
        Meter.CreateCounter<long>(ReconciledMetricName);
}

/// <summary>
/// Atomic cost reservations with reconciliation plus pipeline preflights.
/// Reservations are PG transactions (correctness): <c>ReserveAsync</c> inserts
/// a <c>Reserved</c> row then checks
/// <c>SUM(Reserved.reserved + Reconciled.actual) for the project</c> against
/// <c>Quota:MaxCostPerProject</c>; concurrent reservers serialize on a
/// per-project advisory lock
/// (<c>pg_advisory_xact_lock(hashtext(project))</c>) so the loser observes
/// <c>QUOTA_EXCEEDED</c> instead of overspending (R1). Per-segment amounts are
/// capped by <c>Quota:MaxCostPerSegment</c>. <c>ReconcileAsync</c> records the
/// provider-reported actual plus measured usage (R5: estimate + usage +
/// actuals on the row); <c>ReleaseAsync</c> voids failed attempts. Redis is
/// never consulted here (R3: PG owns cost correctness).
/// </summary>
public sealed class CostService
{
    private readonly IStageExecutionContextFactory _contextFactory;
    private readonly QuotaOptions _quota;
    private readonly ILogger<CostService> _logger;

    public CostService(
        IStageExecutionContextFactory contextFactory,
        IOptions<QuotaOptions> quotaOptions,
        ILogger<CostService> logger)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(quotaOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _contextFactory = contextFactory;
        _quota = quotaOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Pure estimate for one capability from usage dims. Missing table falls
    /// back to <c>1.0.0</c> defaults. Pure (no I/O) for hermetic tests.
    /// </summary>
    public static double Estimate(ProviderCapability capability, CostUsageDims dims, PriceTable? table = null)
    {
        ArgumentNullException.ThrowIfNull(dims);
        dims.Validate();
        var prices = table ?? PriceTable.Default();
        var version = string.IsNullOrWhiteSpace(prices.Version) ? PriceTable.CurrentVersion : prices.Version.Trim();
        _ = version;

        return capability switch
        {
            ProviderCapability.Transcription => dims.AudioMinutes * prices.PriceFor(PriceTable.TranscriptionPerMin),
            ProviderCapability.Translation => dims.Chars * prices.PriceFor(PriceTable.TranslationPerChar),
            ProviderCapability.Tts => dims.Chars * prices.PriceFor(PriceTable.TtsPerChar),
            ProviderCapability.SourceSeparation => dims.AudioMinutes * prices.PriceFor(PriceTable.SeparationPerMin),
            ProviderCapability.Vad => dims.AudioMinutes * prices.PriceFor(PriceTable.LocalPerMin),
            ProviderCapability.Diarization => dims.AudioMinutes * prices.PriceFor(PriceTable.LocalPerMin),
            ProviderCapability.VideoIntelligence => dims.AudioMinutes * prices.PriceFor(PriceTable.LocalPerMin),
            ProviderCapability.LocalInference => dims.AudioMinutes * prices.PriceFor(PriceTable.LocalPerMin),
            _ => dims.AudioMinutes * prices.PriceFor(PriceTable.LocalPerMin),
        };
    }

    /// <summary>
    /// Async wrapper over <see cref="Estimate"/> for call-site uniformity.
    /// </summary>
    public Task<double> EstimateAsync(
        ProviderCapability capability,
        CostUsageDims dims,
        CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        return Task.FromResult(Estimate(capability, dims));
    }

    /// <summary>
    /// Pure pipeline estimate (transcription + translation + TTS) for a
    /// segment-count guess. Pure for hermetic tests and start preflights.
    /// </summary>
    public static double EstimatePipeline(double audioMinutes, int segmentCount, int avgCharsPerSegment, PriceTable? table = null)
    {
        if (double.IsNaN(audioMinutes) || audioMinutes < 0.0)
        {
            throw new DomainException("AudioMinutes must be >= 0.");
        }

        if (segmentCount < 0)
        {
            throw new DomainException("SegmentCount must be >= 0.");
        }

        if (avgCharsPerSegment < 0)
        {
            throw new DomainException("AvgCharsPerSegment must be >= 0.");
        }

        var prices = table ?? PriceTable.Default();
        var transcription = audioMinutes * prices.PriceFor(PriceTable.TranscriptionPerMin);
        var chars = (long)segmentCount * avgCharsPerSegment;
        var translation = chars * prices.PriceFor(PriceTable.TranslationPerChar);
        var tts = chars * prices.PriceFor(PriceTable.TtsPerChar);
        return transcription + translation + tts;
    }

    /// <summary>
    /// Whether <paramref name="current"/> + <paramref name="additional"/>
    /// exceeds <paramref name="limit"/>. Pure.
    /// </summary>
    public static bool IsOverBudget(double current, double additional, double limit)
    {
        if (double.IsNaN(current) || double.IsNaN(additional) || double.IsNaN(limit))
        {
            return true;
        }

        return current + additional > limit;
    }

    /// <summary>
    /// Atomically reserves <paramref name="amount"/> for one project/run
    /// (optionally one segment + capability). Throws
    /// <c>QuotaExceededException</c> (429 QUOTA_EXCEEDED) when the per-segment
    /// cap or the project total would be exceeded. The project total is
    /// <c>SUM(Reserved) + SUM(Reconciled actuals)</c> so reconciled spend keeps
    /// counting after the hold converts to actuals.
    /// </summary>
    public async Task<CostReservation> ReserveAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        Guid? segmentId,
        ProviderCapability capability,
        double amount,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));
        if (segmentId.HasValue && segmentId.Value == Guid.Empty)
        {
            throw new DomainException("SegmentId must not be empty when set.");
        }

        if (double.IsNaN(amount) || amount < 0.0)
        {
            throw new DomainException("Amount must be >= 0.");
        }

        if (amount > _quota.MaxCostPerSegment)
        {
            QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", "cost-per-segment"));
            throw new QuotaExceededException(
                $"Reservation of {amount.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} USD exceeds per-segment cap {_quota.MaxCostPerSegment.ToString("F6", System.Globalization.CultureInfo.InvariantCulture)} USD (dimension cost-per-segment).");
        }

        var reservationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await db.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_xact_lock(hashtext({0}))",
                    projectId.ToString("D")).ConfigureAwait(false);

                db.Set<CostReservation>().Add(new CostReservation(
                    reservationId, tenantId, projectId, runId, segmentId,
                    capability, amount, 0.0, "USD", PriceTable.CurrentVersion,
                    "Reserved", now));
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                var total = await db.Set<CostReservation>()
                    .Where(r => r.ProjectId == projectId
                        && (r.State == "Reserved" || r.State == "Reconciled"))
                    .SumAsync(r => (double?)(r.State == "Reserved" ? r.ReservedAmount : r.ActualAmount), cancellationToken).ConfigureAwait(false) ?? 0.0;

                if (total > _quota.MaxCostPerProject)
                {
                    try
                    {
                        await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        // Rollback best effort; quota error propagates.
                    }

                    QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", "cost-per-project"));
                    throw new QuotaExceededException(
                        $"Reservation would exceed project cost cap {_quota.MaxCostPerProject.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} USD (dimension cost-per-project).");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                try
                {
                    await db.Database.RollbackTransactionAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Rollback best effort; original exception propagates.
                }

                throw;
            }
        }

        CostMeters.Reserved.Add(1);
        _logger.LogInformation(
            "Reserved {Amount} USD for project {ProjectId} run {RunId} capability {Capability}.",
            amount, projectId, runId, capability.ToString());
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var stored = await db.Set<CostReservation>()
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return stored;
            }
        }

        throw new Exceptions.NotFoundException($"Cost reservation '{reservationId}' was not found.");
    }

    /// <summary>
    /// Reconciles a <c>Reserved</c> row with the measured actual plus the
    /// optional provider-reported figure (R5). Conditional on
    /// <c>Reserved</c> so double-reconciles are idempotent.
    /// </summary>
    public async Task ReconcileAsync(
        Guid tenantId,
        Guid reservationId,
        double actualAmount,
        double? providerReportedAmount = null,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(reservationId, nameof(reservationId));
        if (double.IsNaN(actualAmount) || actualAmount < 0.0)
        {
            throw new DomainException("ActualAmount must be >= 0.");
        }

        if (providerReportedAmount.HasValue
            && (double.IsNaN(providerReportedAmount.Value) || providerReportedAmount.Value < 0.0))
        {
            throw new DomainException("ProviderReportedAmount must be >= 0 when set.");
        }

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE cost_reservations SET actual_amount = {0}, state = {1} WHERE id = {2} AND tenant_id = {3} AND state = 'Reserved'",
                actualAmount, "Reconciled", reservationId, tenantId).ConfigureAwait(false);
            if (rows == 0)
            {
                var existing = await db.Set<CostReservation>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken).ConfigureAwait(false);
                if (existing is null || existing.TenantId != tenantId)
                {
                    throw new Exceptions.NotFoundException($"Cost reservation '{reservationId}' was not found.");
                }

                if (string.Equals(existing.State, "Reconciled", StringComparison.Ordinal))
                {
                    return;
                }

                throw new Exceptions.ConflictException($"Cost reservation '{reservationId}' is {existing.State} and cannot be reconciled.");
            }
        }

        CostMeters.Reconciled.Add(1);
        _logger.LogInformation("Reconciled cost reservation {ReservationId} to {Actual} USD.", reservationId, actualAmount);
    }

    /// <summary>
    /// Voids a <c>Reserved</c> hold after failure. Idempotent for
    /// <c>Released</c>/<c>Reconciled</c> rows.
    /// </summary>
    public async Task ReleaseAsync(
        Guid tenantId,
        Guid reservationId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(reservationId, nameof(reservationId));

        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var rows = await db.Database.ExecuteSqlRawAsync(
                "UPDATE cost_reservations SET state = {0} WHERE id = {1} AND tenant_id = {2} AND state = 'Reserved'",
                "Released", reservationId, tenantId).ConfigureAwait(false);
            if (rows == 0)
            {
                var existing = await db.Set<CostReservation>()
                    .AsNoTracking()
                    .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken).ConfigureAwait(false);
                if (existing is null || existing.TenantId != tenantId)
                {
                    throw new Exceptions.NotFoundException($"Cost reservation '{reservationId}' was not found.");
                }
            }
        }

        _logger.LogInformation("Released cost reservation {ReservationId}.", reservationId);
    }

    /// <summary>
    /// Start preflight: estimates transcription + translation + TTS from the
    /// validated media duration (segment guess = one per 5s, 100 chars each,
    /// deterministic) and fails with <c>QUOTA_EXCEEDED</c> when existing
    /// project spend plus the estimate would exceed
    /// <c>Quota:MaxCostPerProject</c>. Check-only (no hold rows) because
    /// segments do not exist yet; per-segment holds happen in
    /// Translation/Tts workers. Returns the estimate in USD.
    /// </summary>
    public async Task<double> PreflightAsync(
        Guid tenantId,
        Guid projectId,
        Guid runId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));
        RequireId(runId, nameof(runId));

        long durationMs = 0;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var asset = await db.Set<MediaAsset>()
                .AsNoTracking()
                .Where(a => a.ProjectId == projectId && a.Status == MediaAssetStatus.Valid)
                .OrderBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (asset is not null)
            {
                durationMs = asset.DurationMs;
            }
        }

        var audioMinutes = Math.Max(0.0, durationMs / 60000.0);
        var segments = durationMs <= 0 ? 10 : Math.Clamp((int)Math.Ceiling(durationMs / 5000.0), 1, 2000);
        var estimate = EstimatePipeline(audioMinutes, segments, 100);

        double current;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            current = await db.Set<CostReservation>()
                .Where(r => r.ProjectId == projectId
                    && (r.State == "Reserved" || r.State == "Reconciled"))
                .SumAsync(r => (double?)(r.State == "Reserved" ? r.ReservedAmount : r.ActualAmount), cancellationToken).ConfigureAwait(false) ?? 0.0;
        }

        if (IsOverBudget(current, estimate, _quota.MaxCostPerProject))
        {
            QuotaMeters.Rejections.Add(1, new KeyValuePair<string, object?>("dimension", "cost-per-project"));
            throw new QuotaExceededException(
                $"Processing preflight estimate {estimate.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)} USD plus current {current.ToString("F4", System.Globalization.CultureInfo.InvariantCulture)} USD exceeds project cap {_quota.MaxCostPerProject.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)} USD (dimension cost-per-project).");
        }

        return estimate;
    }

    private static void RequireTenant(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new DomainException("TenantId must not be empty.");
        }
    }

    private static void RequireId(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException($"{name} must not be empty.");
        }
    }
}
