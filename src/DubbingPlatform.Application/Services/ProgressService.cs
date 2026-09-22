using DubbingPlatform.Application.Exceptions;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Application.Orchestration;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Enums;
using DubbingPlatform.Domain.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace DubbingPlatform.Application.Services;

/// <summary>
/// Durable progress snapshot for one project. <see cref="PercentageIndicator"/>
/// is <c>completed/expected*100</c> rounded (indicator only) and
/// <see cref="NotEta"/> is always true: the platform never promises an ETA.
/// </summary>
public sealed record ProgressSnapshot(
    Guid ProjectId,
    Guid? RunId,
    string Status,
    string Phase,
    string? CurrentStage,
    int CompletedUnits,
    int FailedUnits,
    int RetryingUnits,
    int ReviewUnits,
    int SkippedUnits,
    int CancelledUnits,
    int ExpectedUnits,
    int EstimatedRemaining,
    int PercentageIndicator,
    bool NotEta,
    IReadOnlyList<string> Warnings,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Durable progress over <c>RunStageSummary</c> barrier counters plus
/// <c>StageExecution</c> retry states and open <c>ReviewItem</c> rows for the
/// active run (else the latest run). Progress reflects true unit states, never
/// an ETA (<c>notEta:true</c> always). Phase is derived from the first
/// incomplete DAG stage: <c>upload|validation|speech|translation|voice|timing|
/// mix|qc|render|completed|failed</c>. Eligible units continue while others
/// await review: review-gated units surface as <c>ReviewUnits</c> plus warning
/// entries, they never inflate <c>CompletedUnits</c>. Only ids, stages, counts,
/// and phases are logged by callers — never transcript/translation text,
/// audio, or secrets.
/// </summary>
public sealed class ProgressService
{
    private static readonly ProcessingRunStatus[] ActiveStatuses =
    [
        ProcessingRunStatus.Pending,
        ProcessingRunStatus.Running,
        ProcessingRunStatus.Cancelling,
        ProcessingRunStatus.ManualReviewRequired,
    ];

    private readonly IStageExecutionContextFactory _contextFactory;

    public ProgressService(IStageExecutionContextFactory contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        _contextFactory = contextFactory;
    }

    /// <summary>
    /// Gets the progress snapshot for a project (active run preferred, else the
    /// latest run; 404 when the project never started processing).
    /// </summary>
    public async Task<ProgressSnapshot> GetAsync(
        Guid tenantId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        RequireTenant(tenantId);
        RequireId(projectId, nameof(projectId));

        await RequireProjectAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        var run = await FindRunAsync(tenantId, projectId, cancellationToken).ConfigureAwait(false);
        if (run is null)
        {
            throw new NotFoundException($"Project '{projectId}' has no processing run.");
        }

        List<RunStageSummary> summaries;
        int retrying;
        List<ReviewItem> openReviews;
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            summaries = await db.Set<RunStageSummary>()
                .AsNoTracking()
                .Where(s => s.ProcessingRunId == run.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            retrying = await db.Set<StageExecution>()
                .AsNoTracking()
                .CountAsync(
                    e => e.ProcessingRunId == run.Id && e.Status == StageStatus.RetryPending,
                    cancellationToken).ConfigureAwait(false);
            openReviews = await db.Set<ReviewItem>()
                .AsNoTracking()
                .Where(r => r.ProcessingRunId == run.Id && r.Status == ReviewStatus.Open)
                .OrderBy(r => r.CreatedAt)
                .Take(20)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        return BuildSnapshot(projectId, run, summaries, retrying, openReviews);
    }

    /// <summary>
    /// Builds a snapshot from loaded rows. Pure (no I/O) so it is unit-testable
    /// and shared by the SSE poll loop.
    /// </summary>
    public static ProgressSnapshot BuildSnapshot(
        Guid projectId,
        ProcessingRun run,
        IReadOnlyList<RunStageSummary> summaries,
        int retryingUnits,
        IReadOnlyList<ReviewItem> openReviews)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(openReviews);

        var completed = summaries.Sum(s => s.CompletedUnits);
        var failed = summaries.Sum(s => s.FailedUnits);
        var review = summaries.Sum(s => s.ReviewUnits);
        var skipped = summaries.Sum(s => s.SkippedUnits);
        var cancelled = summaries.Sum(s => s.CancelledUnits);
        var expected = summaries.Sum(s => s.ExpectedUnits);
        var remaining = Math.Max(0, expected - completed - skipped);
        var percentage = ComputePercentageIndicator(completed, expected);
        var currentStage = DeriveCurrentStage(summaries);
        var phase = DerivePhase(run.Status, currentStage, completed);
        var warnings = BuildWarnings(run.Status, openReviews);

        return new ProgressSnapshot(
            projectId, run.Id, run.Status.ToString(), phase, currentStage,
            completed, failed, Math.Max(0, retryingUnits), review, skipped, cancelled,
            expected, remaining, percentage, true, warnings, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Computes <c>completed/expected*100</c> rounded away from zero, clamped to
    /// 0..100. Pure. Zero expected yields 0 (indicator only, never an ETA).
    /// </summary>
    public static int ComputePercentageIndicator(int completedUnits, int expectedUnits)
    {
        if (expectedUnits <= 0 || completedUnits <= 0)
        {
            return 0;
        }

        var raw = (double)completedUnits * 100.0 / expectedUnits;
        return Math.Clamp((int)Math.Round(raw, MidpointRounding.AwayFromZero), 0, 100);
    }

    /// <summary>
    /// Derives the user-facing phase from the run status and the first
    /// incomplete stage. Pure. Terminal <c>Completed</c> → <c>completed</c>;
    /// terminal <c>Failed</c>/<c>Cancelled</c> → <c>failed</c>;
    /// <c>Cancelling</c> keeps the stage phase (work is draining, not terminal);
    /// <c>Pending</c> with zero completions → <c>upload</c> (media staging).
    /// </summary>
    public static string DerivePhase(ProcessingRunStatus status, string? currentStage, int completedUnits)
    {
        if (status == ProcessingRunStatus.Completed)
        {
            return "completed";
        }

        if (status is ProcessingRunStatus.Failed or ProcessingRunStatus.Cancelled)
        {
            return "failed";
        }

        if (status == ProcessingRunStatus.Pending && completedUnits == 0)
        {
            return "upload";
        }

        if (string.IsNullOrWhiteSpace(currentStage))
        {
            return status == ProcessingRunStatus.ManualReviewRequired ? "qc" : "render";
        }

        return MapStageToPhase(currentStage);
    }

    /// <summary>
    /// Maps a stage name to its progress phase. Pure. Unknown names map to
    /// <c>speech</c> (fail-open for display only; completion math is unaffected).
    /// </summary>
    public static string MapStageToPhase(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return "speech";
        }

        var name = stage.Trim();
        if (string.Equals(name, nameof(StageType.MediaValidation), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.MediaAnalysis), StringComparison.Ordinal))
        {
            return "validation";
        }

        if (string.Equals(name, nameof(StageType.AudioPreparation), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.SourceSeparation), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.Vad), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.SegmentBuild), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.Diarization), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.Transcription), StringComparison.Ordinal))
        {
            return "speech";
        }

        if (string.Equals(name, nameof(StageType.ContextBuild), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.Translation), StringComparison.Ordinal))
        {
            return "translation";
        }

        if (string.Equals(name, nameof(StageType.VoiceAssignment), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.VoiceGeneration), StringComparison.Ordinal))
        {
            return "voice";
        }

        if (string.Equals(name, nameof(StageType.TimingOptimization), StringComparison.Ordinal)
            || string.Equals(name, nameof(StageType.TimelineAssembly), StringComparison.Ordinal))
        {
            return "timing";
        }

        if (string.Equals(name, nameof(StageType.AudioMixing), StringComparison.Ordinal))
        {
            return "mix";
        }

        if (string.Equals(name, nameof(StageType.QualityControl), StringComparison.Ordinal))
        {
            return "qc";
        }

        if (string.Equals(name, nameof(StageType.Render), StringComparison.Ordinal))
        {
            return "render";
        }

        return "speech";
    }

    /// <summary>
    /// Finds the first incomplete DAG stage (barrier not crossed), or null when
    /// no summaries exist yet (run with no stages) or every initialized stage
    /// is complete. Pure. Stages with
    /// <c>ExpectedUnits == 0</c> (fan-out not yet sized by the dispatcher) are
    /// treated as pending only when every sized stage before them is complete,
    /// so early progress points at the true frontier.
    /// </summary>
    public static string? DeriveCurrentStage(IReadOnlyList<RunStageSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        if (summaries.Count == 0)
        {
            return null;
        }

        var byStage = summaries.ToDictionary(s => s.StageType);
        foreach (var node in StageGraph.Nodes)
        {
            if (!byStage.TryGetValue(node.StageType, out var summary))
            {
                return node.StageType.ToString();
            }

            if (summary.ExpectedUnits <= 0)
            {
                continue;
            }

            if (summary.CompletedUnits + summary.SkippedUnits < summary.ExpectedUnits)
            {
                return node.StageType.ToString();
            }
        }

        var last = StageGraph.Nodes.Count == 0 ? null : StageGraph.Nodes[^1].StageType.ToString();
        return last;
    }

    /// <summary>
    /// Builds display warnings: one per open review (capped by the caller at 20)
    /// plus pipeline-state advisories. Pure. Carries reasons and scopes only —
    /// never review payload text.
    /// </summary>
    public static IReadOnlyList<string> BuildWarnings(
        ProcessingRunStatus status,
        IReadOnlyList<ReviewItem> openReviews)
    {
        ArgumentNullException.ThrowIfNull(openReviews);
        var warnings = new List<string>();
        foreach (var review in openReviews)
        {
            warnings.Add(string.Concat(
                "review:",
                (review.Reason ?? "review").Trim().ToLowerInvariant(),
                ":",
                review.ScopeType.ToString().ToLowerInvariant()));
        }

        if (openReviews.Count > 0)
        {
            warnings.Add("unresolved-required-reviews-block-completion");
        }

        if (status == ProcessingRunStatus.Cancelling)
        {
            warnings.Add("cancel-pending: draining in-flight work");
        }

        return warnings;
    }

    private async Task<ProcessingRun?> FindRunAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        using (TenantContext.BeginScope(tenantId))
        {
            using var db = _contextFactory.CreateDbContext();
            var active = await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId && ActiveStatuses.Contains(r.Status))
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (active is not null)
            {
                return active;
            }

            return await db.Set<ProcessingRun>()
                .AsNoTracking()
                .Where(r => r.ProjectId == projectId)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequireProjectAsync(Guid tenantId, Guid projectId, CancellationToken cancellationToken)
    {
        DubbingProject? project;
        bool isDeleted;
        using (TenantContext.BeginMaintenanceScope())
        {
            using var db = _contextFactory.CreateDbContext();
            project = await db.Set<DubbingProject>()
                .AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken).ConfigureAwait(false);
            if (project is null)
            {
                throw new NotFoundException($"Project '{projectId}' was not found.");
            }

            isDeleted = await db.Set<DubbingProject>()
                .Where(p => p.Id == projectId)
                .Select(p => EF.Property<bool>(p, "IsDeleted"))
                .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        if (project.TenantId != tenantId)
        {
            throw new ForbiddenException($"Project '{projectId}' does not belong to the current tenant.");
        }

        if (isDeleted)
        {
            throw new NotFoundException($"Project '{projectId}' was not found.");
        }
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
