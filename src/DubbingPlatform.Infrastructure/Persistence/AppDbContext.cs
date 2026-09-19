using System.Reflection;
using DubbingPlatform.Application.MultiTenancy;
using DubbingPlatform.Domain.Entities;
using DubbingPlatform.Domain.Exceptions;
using MassTransit;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DubbingPlatform.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL system-of-record context. Table and column names are snake_case
/// (via <c>UseSnakeCaseNamingConvention</c> on the options builder plus explicit
/// snake_case table names). Tenant isolation is enforced primarily by PostgreSQL
/// row-level security (see <c>Sql/rls_policies.sql</c>, applied by the migration task)
/// driven by <c>TenantSessionInterceptor</c>; per-entity query filters below are
/// defense in depth. Register <see cref="TenantModelCacheKeyFactory"/> in composition
/// roots that pool contexts across tenants so cached models never leak tenant filters.
/// </summary>
public class AppDbContext : DbContext
{
    private static readonly MethodInfo ApplyTenantQueryFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(ApplyTenantQueryFilterCore), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly Guid _tenantId;

    private readonly bool _bypassTenantFilter;

    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
        _tenantId = TenantContext.CurrentTenantId ?? Guid.Empty;
        _bypassTenantFilter = TenantContext.IsMaintenance;
    }

    internal Guid TenantIdForCache => _tenantId;

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<DubbingProject> DubbingProjects => Set<DubbingProject>();

    public DbSet<ProcessingRun> ProcessingRuns => Set<ProcessingRun>();

    public DbSet<MediaAsset> MediaAssets => Set<MediaAsset>();

    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    public DbSet<UploadPart> UploadParts => Set<UploadPart>();

    public DbSet<VoiceProfile> VoiceProfiles => Set<VoiceProfile>();

    public DbSet<Speaker> Speakers => Set<Speaker>();

    public DbSet<SpeechSegment> SpeechSegments => Set<SpeechSegment>();

    public DbSet<ContextWindow> ContextWindows => Set<ContextWindow>();

    public DbSet<SegmentContextAssignment> SegmentContextAssignments => Set<SegmentContextAssignment>();

    public DbSet<OverlapGroup> OverlapGroups => Set<OverlapGroup>();

    public DbSet<SegmentOverlap> SegmentOverlaps => Set<SegmentOverlap>();

    public DbSet<SpeakerVoiceAssignment> SpeakerVoiceAssignments => Set<SpeakerVoiceAssignment>();

    public DbSet<ConsentRecord> ConsentRecords => Set<ConsentRecord>();

    public DbSet<TranscriptVersion> TranscriptVersions => Set<TranscriptVersion>();

    public DbSet<TranslationVersion> TranslationVersions => Set<TranslationVersion>();

    public DbSet<GeneratedAudioArtifact> GeneratedAudioArtifacts => Set<GeneratedAudioArtifact>();

    public DbSet<SyncResult> SyncResults => Set<SyncResult>();

    public DbSet<StageExecution> StageExecutions => Set<StageExecution>();

    public DbSet<RunStageSummary> RunStageSummaries => Set<RunStageSummary>();

    public DbSet<StageUnitCompletion> StageUnitCompletions => Set<StageUnitCompletion>();

    public DbSet<ContentObject> ContentObjects => Set<ContentObject>();

    public DbSet<Artifact> Artifacts => Set<Artifact>();

    public DbSet<ArtifactParent> ArtifactParents => Set<ArtifactParent>();

    public DbSet<StageInputArtifact> StageInputArtifacts => Set<StageInputArtifact>();

    public DbSet<StageOutputArtifact> StageOutputArtifacts => Set<StageOutputArtifact>();

    public DbSet<ProviderExecution> ProviderExecutions => Set<ProviderExecution>();

    public DbSet<ProviderCapabilityDescriptor> ProviderCapabilityDescriptors => Set<ProviderCapabilityDescriptor>();

    public DbSet<ProviderRouteSnapshot> ProviderRouteSnapshots => Set<ProviderRouteSnapshot>();

    public DbSet<PromptTemplate> PromptTemplates => Set<PromptTemplate>();

    public DbSet<PromptTemplateVersion> PromptTemplateVersions => Set<PromptTemplateVersion>();

    public DbSet<QualityResult> QualityResults => Set<QualityResult>();

    public DbSet<ReviewItem> ReviewItems => Set<ReviewItem>();

    public DbSet<ReviewDecision> ReviewDecisions => Set<ReviewDecision>();

    public DbSet<OutputAsset> OutputAssets => Set<OutputAsset>();

    public DbSet<ExportJob> ExportJobs => Set<ExportJob>();

    public DbSet<ExportArtifact> ExportArtifacts => Set<ExportArtifact>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<CostReservation> CostReservations => Set<CostReservation>();

    public DbSet<QuotaUsage> QuotaUsages => Set<QuotaUsage>();

    public DbSet<ProcessingPolicy> ProcessingPolicies => Set<ProcessingPolicy>();

    public DbSet<RetentionHold> RetentionHolds => Set<RetentionHold>();

    public DbSet<DeletionJob> DeletionJobs => Set<DeletionJob>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<OutboxState> OutboxStates => Set<OutboxState>();

    public DbSet<InboxState> InboxStates => Set<InboxState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        modelBuilder.AddTransactionalOutboxEntities();
        modelBuilder.AddInboxStateEntity();
        modelBuilder.Entity<OutboxMessage>().ToTable("outbox_message");
        modelBuilder.Entity<OutboxState>().ToTable("outbox_state");
        modelBuilder.Entity<InboxState>().ToTable("inbox_state");
        ApplyTenantQueryFilters(modelBuilder);
    }

    public override int SaveChanges()
    {
        ValidateTenantIds();
        try
        {
            return base.SaveChanges();
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new DomainException($"CONFLICT: unique constraint violated. {ex.InnerException?.Message}", ex);
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ValidateTenantIds();
        try
        {
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            throw new DomainException($"CONFLICT: unique constraint violated. {ex.InnerException?.Message}", ex);
        }
    }

    private void ValidateTenantIds()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is not EntityState.Added and not EntityState.Modified)
            {
                continue;
            }

            var property = entry.Properties.FirstOrDefault(p => p.Metadata.Name == "TenantId");
            if (property?.CurrentValue is Guid tenantId && tenantId == Guid.Empty)
            {
                throw new DomainException($"{entry.Metadata.Name} TenantId must not be empty.");
            }
        }
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (var current = exception.InnerException; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgres && postgres.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        var domainAssembly = typeof(Tenant).Assembly;
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var clrType = entityType.ClrType;
            if (clrType is null || clrType.Assembly != domainAssembly)
            {
                continue;
            }

            if (entityType.FindProperty("TenantId")?.ClrType != typeof(Guid))
            {
                continue;
            }

            ApplyTenantQueryFilterMethod.MakeGenericMethod(clrType).Invoke(null, [this, modelBuilder]);
        }
    }

    private static void ApplyTenantQueryFilterCore<TEntity>(AppDbContext context, ModelBuilder modelBuilder)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>().HasQueryFilter(e =>
            context._bypassTenantFilter || EF.Property<Guid>(e, "TenantId") == context._tenantId);
    }
}
