# Task 5 — Domain Execution Artifact Entities

## Goal

Implement pipeline execution, artifact/content, provider trace, QC/review/export, audit/cost/policy/retention entities with exact fields so the full schema can be configured.

## Context

Binding: Artifacts immutable; bytes content-addressed SHA-256 within tenant; cross-tenant dedup disabled; storage keys `{tenantId}/{projectId}/{processingRunId}/{stageType}/{artifactType}/{contentHash}{extension}`; lineage relational not JSON-only (JSONB only for non-relational metadata); publication atomic with stage completion via outbox; orphan reconciliation mandatory. Stage execution identity by (Run,StageType,ScopeType,ScopeId,Attempt); leases use fencing tokens; commits conditional on lease token + run state. Retry layers: transport/provider-request/fallback/logical/manual with per-stage budgets; manual retry new attempts + invalidate dependents; reprocessing = new run; cancellation durable. Run status authoritative, project status projected. Review first-class durable/auditable. Export on-demand, not pipeline prerequisite, partial exports carry completeness metadata. Idempotency replayable race-safe, endpoint-class retention. Cost estimate+usage+reconciled actuals, atomic reservations. Loudness/timing policies as in Task 3.

## Starting State

Solution builds; Domain enums/value objects and core entities (Tenant, DubbingProject, ProcessingRun, MediaAsset, UploadSession/Part, Speaker, Voice, Consent, SpeechSegment, Overlap, Context) exist. No execution/artifact/provider/QC/review/export/audit/cost entities yet.

## Scope

Must implement in `src/DubbingPlatform.Domain/Entities/`: TranscriptVersion, TranslationVersion, GeneratedAudioArtifact, SyncResult, StageExecution, RunStageSummary, StageUnitCompletion, ContentObject, Artifact, ArtifactParent, StageInputArtifact, StageOutputArtifact, ProviderExecution, ProviderCapabilityDescriptor, ProviderRouteSnapshot, PromptTemplate, PromptTemplateVersion, QualityResult, ReviewItem, ReviewDecision, OutputAsset, ExportJob, ExportArtifact, AuditEvent, IdempotencyRecord, CostReservation, QuotaUsage, ProcessingPolicy, RetentionHold, DeletionJob. Must not implement: DbContext, migrations, services.

## Instructions

1. Namespace `DubbingPlatform.Domain.Entities`. All include `Guid Id`, `Guid TenantId`, `DateTimeOffset CreatedAt` (+UpdatedAt where mutable). Exact fields:
   - `TranscriptVersion { Id,TenantId,ProjectId,RunId,SegmentId,Provider,Model,Language,Text,Confidence,double,WordTimestampsArtifactId?,bool IsSelected,bool NeedsReview,DateTimeOffset CreatedAt }`
   - `TranslationVersion { Id,TenantId,ProjectId,RunId,SegmentId,PrimaryText,string[] AlternativeTexts,double SemanticScore,double NaturalnessScore,double TimingScore,Provider,Model,PromptTemplateId?,PromptHash?,bool IsSelected,CreatedAt }`
   - `GeneratedAudioArtifact { Id,TenantId,ProjectId,RunId,SegmentId,Provider,Model,Guid VoiceProfileId,Guid ContentObjectId,int DurationMs,bool IsPreview,int Attempt,CreatedAt }`
   - `SyncResult { Id,TenantId,ProjectId,RunId,SegmentId,double SyncScore,SyncStatus Status,int TargetWindowMs,int ActualDurationMs,double RateDelta,double StretchFactor,CreatedAt }`
   - `StageExecution { Id,TenantId,ProjectId,ProcessingRunId,StageType StageType,ScopeType ScopeType,string ScopeId,Guid? SegmentId,int Attempt,StageStatus Status,string LeaseOwner,string LeaseToken,long LeaseTokenVersion,DateTimeOffset LeaseExpiresAt,DateTimeOffset? StartedAt,CompletedAt,string? InputHash,string ConfigurationHash,string ExecutionSnapshotHash,string? OutputArtifactIdsJson,string? ErrorCode,string? ErrorMessage,CreatedAt,UpdatedAt }`
   - `RunStageSummary { Id,TenantId,ProcessingRunId,StageType StageType,int ExpectedUnits,int CompletedUnits,int FailedUnits,int SkippedUnits,int ReviewUnits,int CancelledUnits,CreatedAt,UpdatedAt }`
   - `StageUnitCompletion { Id,TenantId,ProcessingRunId,StageType StageType,ScopeType ScopeType,string ScopeId,Guid StageExecutionId,string UnitState,CreatedAt }` — UnitState in Completed,Skipped,Failed,ManualReviewRequired,Cancelled.
   - `ContentObject { Id,TenantId,ContentHash,Sha256Hex,long SizeBytes,string MediaFormat,string StorageKey,ContentObjectStatus Status,DateTimeOffset CreatedAt,LastReferencedAt }`
   - `Artifact { Id,TenantId,ProjectId,ProcessingRunId,StageType? ProducedByStage,ArtifactType Type,string? SchemaVersion,Guid ContentObjectId,string? Provider,string? Model,string? ConfigurationHash,string? ExecutionSnapshotHash,ArtifactStatus Status,string? MetadataJson,CreatedAt }`
   - `ArtifactParent { Id,TenantId,Guid ChildArtifactId,Guid ParentArtifactId,CreatedAt }`
   - `StageInputArtifact { Id,TenantId,Guid StageExecutionId,Guid ArtifactId,CreatedAt }`
   - `StageOutputArtifact { Id,TenantId,Guid StageExecutionId,Guid ArtifactId,CreatedAt }`
   - `ProviderExecution { Id,TenantId,ProjectId,ProcessingRunId,Guid? StageExecutionId,ProviderType Provider,ProviderCapability Capability,string Model,string? ModelVersion,string? Deployment,string? Region,string? ApiVersion,int Attempt,string RequestHash,string? ResponseHash,long LatencyMs,int? TokensIn,int? TokensOut,double? AudioSeconds,double? EstimatedCost,double? ActualCost,string? PriceTableVersion,OutcomeClass Outcome,string? FallbackReason,string? PromptTemplateId,string? PromptHash,string? VoiceProfileVersion,string? ExternalJobId,string? ProviderIdempotencyKey,CreatedAt }`
   - `ProviderCapabilityDescriptor { Id,TenantId,ProviderType Provider,ProviderCapability Capability,string[] SupportedLanguages,string[] SupportedFormats,long MaxInputBytes,int MaxDurationMs,bool Batching,bool AsyncJob,bool WordTimestamps,bool Diarization,string[] VoiceInventory,bool VoiceCloning,double[] TimingControls,string ConfidenceSemantics,string RateLimitDimsJson,string CostDimsJson,string PrivacyClass,string Region,int Version,CreatedAt }`
   - `ProviderRouteSnapshot { Id,TenantId,ProjectId,ProcessingRunId,string RouteConfigHash,string CapabilityHash,string PrivacyHash,DateTimeOffset CreatedAt }`
   - `PromptTemplate { Id,TenantId,string Name,string Description,CreatedAt }`
   - `PromptTemplateVersion { Id,TenantId,Guid PromptTemplateId,int Version,string SystemInstruction,string TemplateBody,string SafetySettingsJson,string PromptHash,CreatedAt }`
   - `QualityResult { Id,TenantId,ProjectId,ProcessingRunId,ScopeType ScopeType,string ScopeId,Guid? SegmentId,QualityStatus Status,string Code,string Severity,string Message,string? DetailsJson,Guid? ArtifactId,CreatedAt }`
   - `ReviewItem { Id,TenantId,ProjectId,ProcessingRunId,ScopeType ScopeType,string ScopeId,Guid? SegmentId,ReviewStatus Status,string Reason,string? PayloadJson,CreatedAt,UpdatedAt,ResolvedAt? }`
   - `ReviewDecision { Id,TenantId,Guid ReviewItemId,ReviewDecisionType Type,string Reviewer,string Reason,string? MetadataJson,CreatedAt }`
   - `OutputAsset { Id,TenantId,ProjectId,ProcessingRunId,Guid ArtifactId,string MediaKind,int DurationMs,string Container,CreatedAt }` — MediaKind in Video,Audio.
   - `ExportJob { Id,TenantId,ProjectId,ProcessingRunId,ExportFormat Format,ExportJobStatus Status,string? ArtifactIdRef,string? CompletenessJson,bool IsPartial,CreatedAt,UpdatedAt }`
   - `ExportArtifact { Id,TenantId,Guid ExportJobId,Guid ArtifactId,CreatedAt }`
   - `AuditEvent { Id,TenantId,Guid? ProjectId,string Actor,string Action,string ResourceType,string ResourceId,string? DetailsJson,DateTimeOffset CreatedAt }` — append-only (no update).
   - `IdempotencyRecord { Id,TenantId,string Endpoint,string IdempotencyKey,string RequestHash,string State,string? ResponseStatus,string? ResponseBody,DateTimeOffset CreatedAt,ExpiresAt }` — State in Started,Succeeded,Failed.
   - `CostReservation { Id,TenantId,ProjectId,ProcessingRunId,Guid? SegmentId,ProviderCapability Capability,double ReservedAmount,double ActualAmount,string Currency,string PriceTableVersion,string State,CreatedAt }` — State in Reserved,Reconciled,Released.
   - `QuotaUsage { Id,TenantId,string Dimension,long Used,long Limit,DateTimeOffset WindowStart,WindowEnd }`
   - `ProcessingPolicy { Id,TenantId,bool ExternalProvidersAllowed,string[] AllowedProviders,string? ResidencyConstraint,string SensitivePolicy,string VoicePolicy,bool LocalInferenceAllowed,string? RetentionOverride,CreatedAt,UpdatedAt }`
   - `RetentionHold { Id,TenantId,Guid? ProjectId,Guid? ArtifactId,string Reason,string PlacedBy,DateTimeOffset PlacedAt,ReleasedAt?,bool IsActive }`
   - `DeletionJob { Id,TenantId,Guid? ProjectId,string Scope,string Status,DateTimeOffset CreatedAt,CompletedAt? }` — Status in Pending,Running,Completed,Failed,Cancelled.
2. Use enums: StageType, ScopeType, StageStatus, ArtifactType, ArtifactStatus, ContentObjectStatus, ProviderType, ProviderCapability, OutcomeClass, QualityStatus, SyncStatus, ReviewStatus, ReviewDecisionType, ExportFormat, ExportJobStatus.
3. Add `ProjectStatusProjection` static documenting projection: run Running→Processing, Completed→Completed (unless open required reviews→ManualReviewRequired), Failed→Failed, Cancelling/Cancelled→Cancelling/Cancelled, ManualReviewRequired→ManualReviewRequired. (Enforcement in Task 6.)

## Requirements

- R1: All 29 entities exist with exact fields.
- R2: All carry TenantId.
- R3: Artifact lineage relational (ArtifactParent + input/output tables, no JSON-only lineage).
- R4: Stage identity fields present for unique constraint.
- R5: Lease fields present.

## Edge Cases and Error Handling

- Empty hashes where required: throw DomainException.
- Negative durations/costs: throw.
- Unknown UnitState/MediaKind/State strings: consumers must reject (document allowed sets above).

## Security and Safety Requirements

- No secrets in entities. TenantId mandatory. AuditEvent immutable by convention (no setters for update).

## Testing

Create `tests/DubbingPlatform.UnitTests/Domain/ExecutionEntitiesTests.cs`:
- `StageExecution_Has_Lease_Fields`
- `ArtifactParent_Links_Child_Parent`
- `ProviderExecution_Requires_RequestHash`
- `All_Execution_Entities_Have_TenantId` (reflection over the 29 types)

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ExecutionEntitiesTests
```

## Completion Criteria

- 29 entities compile; tests pass.

## Traceability

- Plan Section 2 action 1 (execution subset); Assumptions 21–23, 44–47, 57–66; Functional checklist artifacts/traceability/cost.
