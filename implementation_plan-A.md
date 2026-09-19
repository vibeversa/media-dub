implementation_plan_final.md

# Implementation Plan

## Purpose and Scope

This is the final, production-grade implementation plan for the complete AI dubbing platform.

This plan is **not** an MVP plan. It preserves the full intended product capability: durable pipeline execution, multi-provider AI support, media safety, tenant isolation, manual review, reproducibility, observability, security, retention, deletion, export generation, deployment automation, and future-ready extension points for enrichment and local inference.

Where reviews recommended simplification, scope reduction, or removal of production safeguards, those recommendations are rejected unless they improve correctness without reducing required capability. Where reviews identified real architectural defects, the plan has been revised to remove contradictions and make the system internally consistent.

The plan is organized as an executable engineering program. It is phased for delivery, but no required production capability is removed.

---

## Global Architectural Decisions

These decisions are binding for all implementation steps.

1. **Architecture style**
   - Modular monolith API.
   - Separate worker hosts by workload class.
   - PostgreSQL is the authoritative system of record.
   - RabbitMQ is the durable transport for asynchronous work.
   - Redis is used only for cache, rate limiting, throttling, and ephemeral coordination.
   - Correctness-critical state is never owned by Redis.

2. **Workflow model**
   - The pipeline is an explicit scoped DAG, not a linear chain.
   - Stage scopes include: run, project, speaker, window, segment.
   - Fan-out and fan-in are modeled explicitly.
   - Barriers use atomic completion ledgers and summary counters.
   - `ProcessingRun` is the authoritative execution lifecycle.
   - `DubbingProject` status is a product-level projection of the active run.

3. **Orchestration**
   - Orchestration is implemented with MassTransit saga state machines plus durable PostgreSQL state.
   - Workers execute work; they do not independently decide global pipeline progression.
   - A sweeper exists only as a safety reconciler; primary timeout handling uses delayed messages.

4. **Artifact model**
   - Immutable bytes are stored as tenant-scoped `ContentObject` records keyed by SHA-256.
   - Logical provenance is stored as `Artifact` records referencing `ContentObject`.
   - Artifact lineage is relational, not JSON-only.
   - Artifact publication is atomic with stage completion through the outbox.
   - Orphan blob reconciliation is mandatory.

5. **Provider model**
   - Providers are selected by capability descriptors, tenant policy, route configuration, provider health, and workload compatibility.
   - No provider name is hardcoded in core domain logic.
   - Azure, OpenAI, Google/Gemini, Mock, and Local Inference are provider families.
   - Provider failure and quality failure are distinct outcome classes.
   - External provider work is treated as at-least-once and reconciled.

6. **Identity and tenancy**
   - Internal identifiers are ULID-compatible UUIDs stored as PostgreSQL `uuid`.
   - Public APIs expose prefixed string IDs.
   - Every tenant-scoped table carries `TenantId`.
   - Storage keys, Redis keys, signed URLs, and message validation are tenant-scoped.
   - Row-Level Security is enabled as a production hardening layer in addition to application filters.

7. **Media processing**
   - FFmpeg and FFprobe are invoked only through argument-safe process APIs.
   - Media workers are hardened, resource-limited, and isolated by workload class.
   - Canonical archival audio is lossless 48 kHz / 24-bit PCM or FLAC by default.
   - 32-bit float is used for temporary DSP working files when needed.
   - Source media is preserved byte-for-byte.

8. **Delivery model**
   - The implementation is delivered in production phases, not as an MVP reduction.
   - Phase 1 proves the full core dubbing path with mocks and one real provider path.
   - Phase 2 hardens production operations, security, DR, and scale.
   - Phase 3 enables enrichment and optional advanced capabilities.

---

## Assumptions

1. The repository is greenfield.
2. Runtime is .NET 10.
3. API is ASP.NET Core.
4. Workers are .NET Worker Services.
5. PostgreSQL 16 is the system of record.
6. RabbitMQ 3.13 is the message broker.
7. Redis 7 is used for cache, rate limits, throttles, and ephemeral coordination.
8. MassTransit is the messaging library.
9. EF Core is the persistence layer.
10. EF Core transactional outbox and inbox are enabled.
11. Database naming uses snake_case.
12. Internal IDs are ULID-compatible UUIDs stored as `uuid`.
13. Public IDs are prefixed strings:
   - `tenant_`
   - `prj_`
   - `run_`
   - `asset_`
   - `upl_`
   - `seg_`
   - `spk_`
   - `ctx_`
   - `voice_`
   - `art_`
   - `cnt_`
   - `exe_`
   - `prov_`
   - `qc_`
   - `rev_`
   - `exp_`
   - `job_`
14. Timeline values are integer milliseconds.
15. Source media is immutable and preserved byte-for-byte.
16. One target language is supported per project.
17. Multiple target languages are separate derived projects.
18. Canonical archival audio defaults to:
   - 48 kHz
   - 24-bit PCM or FLAC
   - preserve source channel layout
19. Temporary DSP working audio may use 32-bit float.
20. Provider working audio may be downmixed when required by a provider.
21. Artifacts are immutable.
22. Artifact bytes are content-addressed by SHA-256 within a tenant.
23. Cross-tenant blob deduplication is disabled by default.
24. Object storage is S3-compatible.
25. MinIO is used locally.
26. Managed object storage is used in production.
27. FFmpeg and FFprobe are present only in media worker images.
28. Shell interpolation is prohibited.
29. `ProcessStartInfo.ArgumentList` is mandatory.
30. At-least-once delivery is assumed.
31. Exactly-once execution is not assumed.
32. Idempotent consumers and durable execution records are mandatory.
33. External provider side effects are at-least-once.
34. Provider execution reconciliation is mandatory.
35. Provider routing is configuration-driven.
36. Provider routing includes tenant privacy policy.
37. Provider capability compatibility is checked before routing.
38. Provider failure and output quality are separate classifications.
39. Mock providers are deterministic.
40. Mock providers are the default for local development and CI.
41. Azure, OpenAI, Google/Gemini, and Local Inference are supported provider families.
42. Local inference is exposed through a Python/gRPC inference sidecar or service.
43. GPU workloads run on dedicated worker pools.
44. Stage execution is the durable unit of work.
45. Stage execution identity is enforced by database constraints.
46. Stage leases use fencing tokens.
47. Stage commits are conditional on lease token and run state.
48. Retry layers have explicit ownership:
   - transport retry/redelivery
   - provider request retry
   - provider fallback
   - logical stage retry
   - manual retry
49. Retry budgets are explicit per stage/capability.
50. Manual retry creates new attempts and invalidates downstream dependents.
51. Reprocessing creates a new `ProcessingRun`.
52. Cancellation is durable.
53. Cancellation blocks new scheduling.
54. Running work must re-check cancellation and lease validity before commit.
55. `ProcessingRunStatus` is authoritative.
56. `ProjectStatus` is projected from run state and review state.
57. Manual review is a first-class workflow.
58. Review items are durable, auditable, and actionable.
59. Export generation is an on-demand job.
60. Export is not a core pipeline prerequisite.
61. Partial exports include completeness metadata.
62. API idempotency records are replayable and race-safe.
63. Idempotency retention is endpoint-class specific.
64. Cost tracking includes estimate, usage dimensions, and reconciled actuals where available.
65. Cost and quota enforcement uses atomic reservations.
66. Rate limiting models provider-specific dimensions.
67. Tenant fairness is enforced by active-work caps.
68. Loudness targets are explicit.
69. Default loudness target is -16 LUFS integrated for web output.
70. Default true-peak limit is -1 dBTP.
71. Broadcast profile may use -23 LUFS.
72. Timing policy is multidimensional.
73. Timing optimization remains bounded.
74. Deterministic duration estimation is used before paid TTS attempts.
75. Observability includes logs, traces, metrics, SLOs, alerts, dashboards, and runbooks.
76. Security includes authentication, authorization, tenant isolation, secret rotation, mTLS where appropriate, network policy, and container hardening.
77. Audit events are append-only and retained.
78. Retention and deletion separate logical deletion from physical garbage collection.
79. Production dependencies prefer managed services.
80. Kubernetes is used for stateless API and workers.
81. KEDA scaling uses queue depth plus workload/resource metrics.
82. Local development has a fast profile and a full distributed profile.
83. The implementation is delivered in production phases, not reduced to MVP scope.

---

## 1. Solution Scaffolding and Repository Conventions

### Objective

Create a buildable .NET solution with project boundaries, container definitions, local compose profiles, build tooling, and repository conventions.

### Actions

1. Create `DubbingPlatform.sln`.
2. Create projects:
   - `src/DubbingPlatform.Domain`
   - `src/DubbingPlatform.Contracts`
   - `src/DubbingPlatform.Application`
   - `src/DubbingPlatform.Infrastructure`
   - `src/DubbingPlatform.Api`
   - `src/DubbingPlatform.Workers`
   - `tests/DubbingPlatform.UnitTests`
   - `tests/DubbingPlatform.IntegrationTests`
   - `tests/DubbingPlatform.ContractTests`
   - `tests/DubbingPlatform.E2ETests`
3. Enforce dependency rules:
   - Domain references nothing.
   - Contracts references nothing.
   - Application references Domain and Contracts.
   - Infrastructure references Application, Domain, Contracts.
   - Api references Application, Infrastructure, Contracts.
   - Workers references Application, Infrastructure, Contracts.
4. Add `Directory.Build.props`:
   - `net10.0`
   - nullable enabled
   - implicit usings enabled
   - warnings as errors
   - latest analysis level
   - code style enforced in build
5. Add `global.json` pinning .NET 10 SDK.
6. Add `.editorconfig`.
7. Add `.gitignore`.
8. Add local tool manifest with `dotnet-ef`.
9. Add required NuGet packages:
   - EF Core
   - Npgsql
   - EFCore.NamingConventions
   - MassTransit
   - MassTransit.RabbitMQ
   - MassTransit.EntityFrameworkCore
   - StackExchange.Redis
   - AWSSDK.S3
   - Serilog
   - OpenTelemetry
   - Prometheus exporters
   - Polly
   - FluentValidation
   - xUnit
   - Testcontainers
   - WireMock.Net
   - ASP.NET Core.Mvc.Testing
10. Create Dockerfiles:
   - `Dockerfile.api`
   - `Dockerfile.worker.control`
   - `Dockerfile.worker.media.preparation`
   - `Dockerfile.worker.media.render`
   - `Dockerfile.worker.ai`
   - `Dockerfile.worker.gpu`
   - `Dockerfile.worker.export`
   - `Dockerfile.maintenance`
11. Install FFmpeg and FFprobe only in media images.
12. Create `docker-compose.yml` with profiles:
   - `full`
   - `fast`
13. `full` profile includes:
   - PostgreSQL
   - RabbitMQ
   - Redis
   - MinIO
   - API
   - workers
14. `fast` profile includes:
   - PostgreSQL
   - MinIO
   - API
   - control worker using in-memory transport where safe
   - mock providers
15. Add healthchecks for dependencies.
16. Add `Makefile` targets:
   - `build`
   - `test`
   - `test-unit`
   - `test-integration`
   - `test-e2e`
   - `compose-full`
   - `compose-fast`
   - `migrate`
   - `run-api`
   - `run-workers`
   - `lint`
   - `security-scan`
17. Add configuration placeholders for:
   - ConnectionStrings
   - RabbitMq
   - Redis
   - Storage
   - Providers
   - Media
   - Timing
   - Retry
   - Quota
   - RateLimit
   - Observability
   - Auth
   - Retention
   - Privacy
   - Features
   - Deployment
18. Ensure no placeholder project fails compilation.

### Files / Components

- `DubbingPlatform.sln`
- `src/*`
- `tests/*`
- `Directory.Build.props`
- `global.json`
- `.editorconfig`
- `.gitignore`
- `.config/dotnet-tools.json`
- `docker-compose.yml`
- `Dockerfile.*`
- `Makefile`
- `src/DubbingPlatform.Api/appsettings*.json`
- `src/DubbingPlatform.Workers/appsettings*.json`

### Dependencies

None.

### Expected Result

The repository builds, containers are defined, local profiles exist, and the solution boundaries are stable.

### Validation

- `dotnet build` succeeds.
- `docker compose config` validates.
- `make compose-fast` starts API and minimum dependencies.
- `make compose-full` starts all dependency containers.
- `dotnet tool restore` succeeds.

---

## 2. Domain Model, Database Schema, and State Machines

### Objective

Define the complete domain model, relational schema, state machines, identity mapping, tenant columns, indexes, and constraints.

### Actions

1. Define core entities:
   - `Tenant`
   - `DubbingProject`
   - `ProcessingRun`
   - `MediaAsset`
   - `UploadSession`
   - `UploadPart`
   - `Speaker`
   - `SpeakerVoiceAssignment`
   - `VoiceProfile`
   - `ConsentRecord`
   - `SpeechSegment`
   - `OverlapGroup`
   - `SegmentOverlap`
   - `ContextWindow`
   - `SegmentContextAssignment`
   - `TranscriptVersion`
   - `TranslationVersion`
   - `GeneratedAudioArtifact`
   - `SyncResult`
   - `StageExecution`
   - `RunStageSummary`
   - `StageUnitCompletion`
   - `ContentObject`
   - `Artifact`
   - `ArtifactParent`
   - `StageInputArtifact`
   - `StageOutputArtifact`
   - `ProviderExecution`
   - `ProviderCapabilityDescriptor`
   - `ProviderRouteSnapshot`
   - `PromptTemplate`
   - `PromptTemplateVersion`
   - `QualityResult`
   - `ReviewItem`
   - `ReviewDecision`
   - `OutputAsset`
   - `ExportJob`
   - `ExportArtifact`
   - `AuditEvent`
   - `IdempotencyRecord`
   - `CostReservation`
   - `QuotaUsage`
   - `ProcessingPolicy`
   - `RetentionHold`
   - `DeletionJob`
2. Define enums:
   - `ProjectStatus`
   - `ProcessingRunStatus`
   - `StageStatus`
   - `StageType`
   - `ScopeType`
   - `OutcomeClass`
   - `FailureCategory`
   - `AssetType`
   - `ArtifactType`
   - `ArtifactStatus`
   - `ContentObjectStatus`
   - `UploadStatus`
   - `MediaAssetStatus`
   - `QualityStatus`
   - `SyncStatus`
   - `ProviderType`
   - `ProviderCapability`
   - `VoiceType`
   - `ExportFormat`
   - `ExportJobStatus`
   - `ReviewStatus`
   - `ReviewDecisionType`
   - `AudioMixPolicy`
   - `SourceSeparationPolicy`
   - `ConsentStatus`
3. Define value objects:
   - `TimeRange`
   - `ContentHash`
   - `ConfigurationHash`
   - `ExecutionSnapshotHash`
   - `ProviderRouteHash`
   - `PromptHash`
   - `Money`
   - `ProviderModelReference`
   - `LoudnessTarget`
   - `TimingWindow`
4. Store internal IDs as PostgreSQL `uuid`.
5. Expose prefixed string IDs through EF value converters and API serialization.
6. Add `TenantId` to all tenant-scoped tables.
7. Make `ProcessingRun` authoritative for execution lifecycle.
8. Define project status projection rules from run state.
9. Define state machine transitions in domain services.
10. Enforce illegal transitions with domain exceptions.
11. Define project transitions:
   - `Created -> Uploading`
   - `Uploading -> MediaReady`
   - `Uploading -> MediaRejected`
   - `MediaReady -> Processing`
   - `Processing -> Cancelling`
   - `Processing -> Completed`
   - `Processing -> Failed`
   - `Processing -> ManualReviewRequired`
   - `Cancelling -> Cancelled`
   - `ManualReviewRequired -> Processing`
   - `ManualReviewRequired -> Cancelled`
   - `Failed -> Processing` only by explicit manual retry
12. Define run transitions:
   - `Pending -> Running`
   - `Running -> Completed`
   - `Running -> Failed`
   - `Running -> Cancelling`
   - `Running -> ManualReviewRequired`
   - `Cancelling -> Cancelled`
   - `ManualReviewRequired -> Running` when review resolved
   - `Failed -> Running` only by explicit manual retry creating new attempt or run according to retry type
13. Define stage transitions:
   - `Pending -> Scheduled`
   - `Scheduled -> Running`
   - `Running -> Completed`
   - `Running -> Failed`
   - `Running -> RetryPending`
   - `Running -> Cancelled`
   - `Running -> ManualReviewRequired`
   - `RetryPending -> Scheduled`
   - `Failed -> Scheduled` only by manual retry
14. Define upload transitions:
   - `Created -> InProgress`
   - `InProgress -> Completed`
   - `InProgress -> Aborted`
   - `InProgress -> Expired`
   - `Completed -> Duplicate`
15. Define review transitions:
   - `Open -> Approved`
   - `Open -> Rejected`
   - `Open -> Requeued`
   - `Open -> ResolvedWithEdit`
16. Define export job transitions:
   - `Pending -> Running`
   - `Running -> Completed`
   - `Running -> Failed`
   - `Running -> Cancelled`
17. Define artifact statuses:
   - `Pending`
   - `Committed`
   - `Deleted`
18. Define content object statuses:
   - `Pending`
   - `Committed`
   - `Orphaned`
   - `Deleted`
19. Define schema constraints:
   - partial unique index: one non-terminal active run per project
   - unique `(TenantId, Endpoint, IdempotencyKey)` on idempotency records
   - unique `(TenantId, ContentHash)` on `ContentObject`
   - unique `(ProcessingRunId, StageType, ScopeType, ScopeId, Attempt)` on `StageExecution`
   - unique `(ProcessingRunId, StageType, ScopeType, ScopeId, StageExecutionId)` on `StageUnitCompletion`
   - unique `(UploadSessionId, PartNumber)` on upload parts
   - composite indexes for tenant/project/run/stage/status queries
   - partial index for lease recovery: `Status = Running AND LeaseExpiresAt < now()`
20. Define workload-oriented indexes:
   - `(TenantId, ProjectId)`
   - `(ProcessingRunId, StageType, Status)`
   - `(ProcessingRunId, Status, LeaseExpiresAt)`
   - `(ProjectId, Sequence)` on segments
   - `(TenantId, Status, CreatedAt)` on review items
   - `(TenantId, Status, ExpiresAt)` on retention candidates
   - `(ProjectId, CreatedAt)` on provider executions
   - `(ContentObjectId)` on artifacts
   - `(TenantId, ContentHash)` on content objects
21. Create `AppDbContext`.
22. Add EF configurations for all entities.
23. Add initial migration.
24. Add schema version metadata for JSON artifacts.
25. Add `ConfigurationHashCalculator`.
26. Add `ExecutionSnapshotCalculator`.
27. Ensure secrets are excluded from hashes.
28. Ensure hashes are deterministic and culture-invariant.

### Files / Components

- `src/DubbingPlatform.Domain/*`
- `src/DubbingPlatform.Infrastructure/Persistence/*`
- `src/DubbingPlatform.Application/Configuration/*`
- `src/DubbingPlatform.Application/StateMachines/*`
- migrations

### Dependencies

Step 1.

### Expected Result

The database schema is complete, state transitions are explicit, identities are efficient, and tenant isolation is represented at the data model level.

### Validation

- Migration applies cleanly.
- Snake_case names verified.
- Unique constraints verified.
- Unit tests prove allowed transitions succeed.
- Unit tests prove forbidden transitions throw.
- Unit tests prove configuration hash determinism.
- Unit tests prove execution snapshot determinism.
- Integration test verifies one active non-terminal run per project.

---

## 3. Cross-Cutting Infrastructure

### Objective

Implement configuration binding, structured logging, tracing, metrics, health checks, correlation IDs, exception handling, validation, process execution safety, and local development profiles.

### Actions

1. Implement `CorrelationIdMiddleware`.
2. Use header `X-Correlation-Id`.
3. Generate correlation ID if missing.
4. Propagate correlation ID through:
   - HTTP context
   - logs
   - traces
   - message headers
   - provider executions
5. Implement `ExceptionHandlingMiddleware`.
6. Define public error codes:
   - `VALIDATION_FAILED`
   - `UNAUTHORIZED`
   - `FORBIDDEN`
   - `NOT_FOUND`
   - `CONFLICT`
   - `MEDIA_UNSUPPORTED`
   - `MEDIA_CORRUPT`
   - `UPLOAD_INCOMPLETE`
   - `DUPLICATE_MEDIA`
   - `PROVIDER_CONFIGURATION_ERROR`
   - `PROVIDER_RATE_LIMITED`
   - `PROVIDER_TIMEOUT`
   - `PROVIDER_INVALID_RESPONSE`
   - `PROVIDER_QUOTA_EXHAUSTED`
   - `PROVIDER_FAILED`
   - `QC_BLOCKED`
   - `QUOTA_EXCEEDED`
   - `RATE_LIMITED`
   - `RESOURCE_EXHAUSTED`
   - `ARTIFACT_UNAVAILABLE`
   - `ARTIFACT_CHECKSUM_MISMATCH`
   - `LEASE_LOST`
   - `PIPELINE_INVARIANT_VIOLATION`
   - `MANUAL_REVIEW_REQUIRED`
   - `EXPORT_NOT_READY`
   - `STORAGE_UNAVAILABLE`
   - `CONSENT_REQUIRED`
   - `POLICY_DENIED`
   - `INTERNAL_ERROR`
7. Define richer internal failure categories separately from public codes.
8. Configure Serilog for API and workers.
9. Add structured properties:
   - `CorrelationId`
   - `TenantId`
   - `ProjectId`
   - `ProcessingRunId`
   - `StageType`
   - `ScopeType`
   - `ScopeId`
   - `SegmentId`
   - `Attempt`
   - `Provider`
   - `Model`
   - `LeaseToken`
10. Configure OpenTelemetry tracing for:
   - ASP.NET Core
   - HTTP clients
   - EF Core
   - MassTransit
   - provider calls
   - FFmpeg processes
11. Configure Prometheus metrics.
12. Expose `/metrics`.
13. Implement health checks:
   - liveness: process-local only
   - readiness: dependencies required to accept work
   - worker-specific readiness: storage, queue, provider, FFmpeg where relevant
14. Do not make liveness depend on PostgreSQL, RabbitMQ, Redis, or storage.
15. Implement FFmpeg/FFprobe version health checks only in media workers.
16. Add FluentValidation registration.
17. Add Polly policies for outbound HTTP:
   - retry with exponential backoff and jitter
   - circuit breaker
   - timeout
18. Implement options classes:
   - `ObservabilityOptions`
   - `MediaOptions`
   - `RetryOptions`
   - `TimingOptions`
   - `StorageOptions`
   - `ProviderOptions`
   - `QuotaOptions`
   - `RateLimitOptions`
   - `PrivacyOptions`
   - `FeatureOptions`
   - `DeploymentOptions`
19. Validate options at startup.
20. Mark secrets as sensitive.
21. Exclude secrets from logs, traces, hashes, and error responses.
22. Implement shared `ProcessRunner`:
   - `ArgumentList` only
   - no shell
   - cancellation support
   - stdout/stderr capture
   - timeout support
   - temp directory binding
   - resource limit metadata
23. Add local fast profile:
   - PostgreSQL
   - MinIO
   - mock providers
   - in-memory transport where safe
24. Add full local profile:
   - PostgreSQL
   - RabbitMQ
   - Redis
   - MinIO
   - workers

### Files / Components

- `src/DubbingPlatform.Api/Middleware/*`
- `src/DubbingPlatform.Api/Program.cs`
- `src/DubbingPlatform.Workers/Program.cs`
- `src/DubbingPlatform.Infrastructure/Observability/*`
- `src/DubbingPlatform.Infrastructure/Processes/*`
- `src/DubbingPlatform.Application/Options/*`
- `src/DubbingPlatform.Application/Validators/*`

### Dependencies

Step 2.

### Expected Result

The platform has consistent observability, safe process execution, structured errors, health separation, and usable local profiles.

### Validation

- API starts.
- `/health/live` returns process-local status.
- `/health/ready` reflects dependency readiness.
- Invalid request returns structured error envelope.
- Logs include correlation ID.
- `/metrics` returns Prometheus metrics.
- Unit test proves `ProcessRunner` rejects shell concatenation.
- Fast local profile runs without RabbitMQ.

---

## 4. Messaging, Orchestration, Leases, and Recovery

### Objective

Implement durable messaging, saga-based orchestration, scoped DAG execution, atomic stage claiming, fan-in barriers, lease fencing, delayed timeouts, sweeper reconciliation, and retry budget ownership.

### Actions

1. Define message contracts in `Contracts`:
   - `RunStarted`
   - `RunCancelledRequested`
   - `StageWorkRequested`
   - `StageCompleted`
   - `StageFailed`
   - `StageCancelled`
   - `StageReviewRequired`
   - `ReviewResolved`
   - `RunCompleted`
   - `RunFailed`
   - `RunCancelled`
   - `MediaUploaded`
   - `MediaValidated`
   - `ExportJobRequested`
   - `StageLeaseTimeout`
2. Include common fields:
   - `MessageId`
   - `CorrelationId`
   - `TenantId`
   - `ProjectId`
   - `ProcessingRunId`
   - `StageExecutionId`
   - `StageType`
   - `ScopeType`
   - `ScopeId`
   - `SegmentId`
   - `SchemaVersion`
   - `CreatedAt`
   - `Attempt`
   - `InputHash`
   - `ConfigurationHash`
   - `ExecutionSnapshotHash`
3. Remove `JobId`.
4. Define schema version policy:
   - additive changes only
   - tolerant readers
   - unsupported versions move to `_skipped`
   - metric emitted for skipped schema mismatch
5. Configure MassTransit:
   - RabbitMQ transport
   - durable queues
   - error queue
   - dead-letter/skipped queue
   - EF Core outbox
   - EF Core inbox
6. Define workload-class queues:
   - `control.orchestration`
   - `media.preparation`
   - `media.render`
   - `ai.provider`
   - `ai.gpu`
   - `export`
   - `maintenance`
7. Do not create one queue per micro-stage by default.
8. Implement `BaseConsumer<TMessage>`:
   - extract correlation ID
   - validate schema version
   - validate tenant/project/run
   - check cancellation
   - claim or return existing `StageExecution`
   - enforce idempotency
   - renew lease
   - re-check lease before commit
9. Implement `StageExecutionService`:
   - `ScheduleAsync`
   - `ClaimAsync`
   - `StartAsync`
   - `CompleteAsync`
   - `FailAsync`
   - `CancelAsync`
   - `MarkReviewRequiredAsync`
   - `RenewLeaseAsync`
   - `ReleaseLeaseAsync`
   - `RecoverStaleAsync`
10. Make stage claiming atomic:
   - insert or select existing by unique `(RunId, StageType, ScopeType, ScopeId, Attempt)`
   - use transaction
   - return existing execution on duplicate
11. Add `LeaseToken` to `StageExecution`.
12. Increment `LeaseToken` on every new lease grant.
13. Require conditional commit:
   ```sql
   UPDATE stage_executions
   SET status = ..., completed_at = ..., output_artifact_ids = ...
   WHERE id = @id
     AND lease_owner = @owner
     AND lease_token = @token
     AND status = 'Running'
   ```
14. If zero rows affected:
   - discard result
   - do not publish downstream
   - mark local operation aborted
15. Implement `ProcessingRunSaga`:
   - run lifecycle
   - stage scheduling
   - cancellation
   - review blocking
   - retry coordination
   - dependency-aware invalidation
16. Implement `StageGraph` as metadata:
   - stage type
   - scope
   - prerequisites
   - fan-out rule
   - completion criterion
   - failure aggregation
   - skip policy
   - review policy
   - retry policy
   - on-demand flag
17. Define stage DAG:
   - `MediaValidation`
   - `MediaAnalysis` after `MediaValidation`
   - `AudioPreparation` after `MediaAnalysis`
   - `SourceSeparation` after `AudioPreparation`
   - `Vad` after `SourceSeparation` completed or skipped
   - `SegmentBuild` after `Vad`
   - `Diarization` after `SegmentBuild`
   - `Transcription` after `Diarization`
   - `ContextBuild` after `Transcription`
   - `Translation` after `ContextBuild`
   - `VoiceAssignment` after `Diarization`
   - `VoiceGeneration` after `Translation` and `VoiceAssignment`
   - `TimingOptimization` after `VoiceGeneration`
   - `TimelineAssembly` after `TimingOptimization`
   - `AudioMixing` after `TimelineAssembly`
   - `QualityControl` after `AudioMixing`
   - `Render` after `QualityControl`
18. Define scopes:
   - run/project: `MediaValidation`, `MediaAnalysis`, `AudioPreparation`, `SourceSeparation`, `Vad`, `SegmentBuild`, `Diarization`, `TimelineAssembly`, `AudioMixing`, `QualityControl`, `Render`
   - speaker: `VoiceAssignment`
   - window: `ContextBuild`
   - segment: `Transcription`, `Translation`, `VoiceGeneration`, `TimingOptimization`, segment QC
19. Define eligible unit states:
   - `Completed`
   - `Skipped`
20. Define terminal unit states:
   - `Completed`
   - `Skipped`
   - `Failed`
   - `ManualReviewRequired`
   - `Cancelled`
21. Implement `RunStageSummary` rows per stage/scope.
22. Implement `StageUnitCompletion` ledger.
23. On stage unit completion:
   - insert ledger row
   - update summary counter atomically
   - publish `StageCompleted`
24. Prevent duplicate fan-in increments using ledger unique constraint.
25. Do not use `SELECT COUNT(*)` for every completion event.
26. Implement work dispatcher:
   - publishes segment work in bounded batches
   - respects stage/provider/tenant concurrency limits
   - respects rate limits and cost reservations
27. Implement delayed lease timeout:
   - schedule `StageLeaseTimeout` when stage starts
   - timeout checks lease token and status
   - recovers only if still stale
28. Implement `WorkerRecoverySweeper` as safety reconciler:
   - runs every 60 seconds
   - uses partial index
   - recovers expired leases
   - does not replace delayed timeout mechanism
29. Define retry ownership:
   - MassTransit redelivery: transport failure only
   - Polly: provider HTTP transient errors only
   - provider fallback: capability route policy only
   - logical stage retry: stage policy only
   - manual retry: API-initiated only
30. Define retry budgets:
   - transport attempts
   - provider request attempts
   - fallback attempts
   - logical stage attempts
   - manual retry attempts
31. Ensure retry budgets are configurable per stage/capability.
32. Ensure provider rate-limit errors use delayed retry.
33. Ensure invalid input fails fast.
34. Ensure configuration errors fail fast.
35. Ensure cancellation prevents new work scheduling.
36. Ensure workers re-check run state before commit.
37. Ensure DLQ behavior is explicit:
   - poison messages to `_skipped`
   - exhausted failures to `_error`
   - alerts on DLQ depth

### Files / Components

- `src/DubbingPlatform.Contracts/Messages/*`
- `src/DubbingPlatform.Infrastructure/Messaging/*`
- `src/DubbingPlatform.Infrastructure/Orchestration/*`
- `src/DubbingPlatform.Application/Services/StageExecutionService.cs`
- `src/DubbingPlatform.Application/Orchestration/StageGraph.cs`
- `src/DubbingPlatform.Workers/Services/WorkerRecoverySweeper.cs`

### Dependencies

Steps 2 and 3.

### Expected Result

The pipeline executes as a durable scoped DAG with atomic claims, safe fan-in, lease fencing, controlled retries, and no duplicate state changes.

### Validation

- Duplicate message produces one stage execution.
- Duplicate completion does not double-count barrier.
- Stage barrier advances exactly once.
- Expired lease is recovered.
- Active lease is not recovered.
- Stale worker cannot commit after lease loss.
- Cancellation blocks new work.
- DLQ tests pass.
- Schema mismatch moves to `_skipped`.

---

## 5. Artifact Storage, Content Objects, Lineage, and Reconciliation

### Objective

Implement immutable tenant-scoped content storage, logical artifact provenance, atomic publication, relational lineage, orphan reconciliation, and deletion-safe reference handling.

### Actions

1. Define `ContentObject`:
   - immutable bytes
   - tenant-scoped
   - SHA-256 content hash
   - size
   - media format
   - status
   - storage key
   - created at
   - last referenced at
2. Define `Artifact`:
   - logical artifact
   - project/run/stage ownership
   - artifact type
   - content object reference
   - provider/model metadata
   - configuration hash
   - execution snapshot hash
   - status
   - schema version
3. Define `ArtifactParent` and `ArtifactChild` relational tables.
4. Define `StageInputArtifact` and `StageOutputArtifact`.
5. Remove JSON-only lineage as source of truth.
6. Keep JSONB only for non-relational metadata.
7. Define storage key convention:
   `{tenantId}/{projectId}/{processingRunId}/{stageType}/{artifactType}/{contentHash}{extension}`
8. Implement `IArtifactStorage`:
   - upload
   - download
   - exists
   - delete
   - presigned download
   - presigned upload where needed
9. Implement S3 storage adapter.
10. Stream uploads and downloads.
11. Compute SHA-256 streaming.
12. Use storage-provided checksum when available and supported.
13. Do not download an object solely to hash it when storage checksum is authoritative.
14. Implement artifact publication workflow:
   - reserve `ContentObject` row `Pending`
   - reserve `Artifact` row `Pending`
   - upload blob
   - verify checksum
   - mark `ContentObject` committed
   - mark `Artifact` committed
   - complete stage in same transaction
   - publish outbox message in same transaction
15. Implement orphan object reconciler:
   - finds storage objects without committed metadata
   - verifies age threshold
   - deletes or quarantines
16. Implement dangling content object reconciler:
   - finds committed content objects with no artifact references
   - marks orphaned after grace period
17. Implement tenant-scoped deduplication:
   - unique `(TenantId, ContentHash)`
   - reuse content object within tenant
   - create new logical artifact row when reused
18. Disable cross-tenant deduplication by default.
19. Require authorization validation before presigned URL issuance.
20. Require project ownership validation before download URL generation.
21. Implement artifact integrity check option:
   - revalidate SHA-256 on download for QC/render when configured
22. Implement retention hooks:
   - logical deletion first
   - physical deletion only after reference count zero and retention satisfied
23. Add retention hold support.
24. Add deletion job support.
25. Record artifact schema version in metadata.
26. Add integration tests with MinIO.

### Files / Components

- `src/DubbingPlatform.Application/Abstractions/IArtifactStorage.cs`
- `src/DubbingPlatform.Infrastructure/Storage/S3ArtifactStorage.cs`
- `src/DubbingPlatform.Application/Services/ArtifactService.cs`
- `src/DubbingPlatform.Application/Services/ContentObjectService.cs`
- `src/DubbingPlatform.Infrastructure/Persistence/Repositories/*`
- `src/DubbingPlatform.Workers/Services/OrphanObjectReconciler.cs`

### Dependencies

Steps 2 and 3.

### Expected Result

Artifact bytes are immutable, logical provenance is preserved, publication is atomic, lineage is queryable, and orphan objects are reconciled.

### Validation

- Upload produces committed content object and artifact.
- Duplicate bytes within tenant reuse content object.
- Duplicate bytes across tenants do not reuse content object.
- Presigned URL requires ownership.
- Failed DB commit leaves no committed artifact.
- Orphan reconciler detects uncommitted blobs.
- Lineage queries return parent/child relationships.
- Retention hold prevents deletion.

---

## 6. Provider Capability Model, Routing, Privacy, and Adapters

### Objective

Implement a production provider architecture with capability descriptors, privacy-aware routing, provider health, failure classification, execution traceability, and adapters for Mock, Azure, OpenAI, Google/Gemini, and Local Inference.

### Actions

1. Define provider capability interfaces:
   - `IVadProvider`
   - `IDiarizationProvider`
   - `ITranscriptionProvider`
   - `ITranslationProvider`
   - `ITtsProvider`
   - `ISourceSeparationProvider`
   - `IVideoIntelligenceProvider`
   - `ILocalInferenceProvider`
2. Define provider request/response DTOs:
   - language
   - duration
   - timestamps
   - speaker labels
   - confidence
   - model
   - model version
   - deployment
   - usage
   - raw metadata
   - capability-specific optional fields
3. Define `ProviderCapabilityDescriptor`:
   - provider
   - capability
   - supported languages
   - supported formats
   - max input bytes
   - max duration
   - batching support
   - async job support
   - word timestamps support
   - diarization support
   - voice inventory
   - voice cloning support
   - timing controls
   - confidence semantics
   - rate limit dimensions
   - cost dimensions
   - privacy classification
   - region/residency metadata
4. Store descriptors in configuration and/or database.
5. Version descriptors.
6. Require compatibility check before routing.
7. Define outcome classes:
   - `Success`
   - `ProviderUnavailable`
   - `ProviderRateLimited`
   - `ProviderTransientFailure`
   - `ProviderPermanentFailure`
   - `ProviderInvalidResponse`
   - `ProviderTimeout`
   - `QualityBelowThreshold`
   - `PolicyRejected`
   - `UnsupportedCapability`
   - `Cancelled`
8. Define which outcomes allow:
   - retry
   - fallback
   - manual review
   - fail fast
9. Define `ProviderRouteSnapshot`:
   - route configuration hash
   - capability descriptors hash
   - privacy policy hash
   - created at
10. Define `ProcessingPolicy` per tenant:
   - external providers allowed
   - allowed providers
   - data residency constraints
   - sensitive content policy
   - voice data policy
   - local inference allowed
   - retention overrides
11. Implement `ProviderResolver`:
   - capability first
   - tenant policy second
   - route priority third
   - provider health fourth
   - cost guardrails fifth
12. Do not hardcode Azure/OpenAI precedence in domain logic.
13. Implement provider health tracking:
   - configuration valid
   - runtime available
   - circuit state
   - recent error rate
   - rate-limit pressure
14. Separate configuration validation from runtime health.
15. Implement `ProviderExecutionRecorder`.
16. Record for every provider call:
   - provider
   - capability
   - model
   - model version
   - deployment
   - region
   - API version
   - attempt
   - request hash
   - response hash
   - latency
   - tokens in/out
   - audio duration
   - usage dimensions
   - estimated cost
   - actual provider-reported usage where available
   - price table version
   - outcome class
   - fallback reason
   - prompt template ID/version
   - prompt hash
   - system instruction hash
   - safety settings hash
   - voice profile version
   - external job ID
   - provider idempotency key
17. Use provider idempotency keys where supported.
18. Treat external provider work as at-least-once.
19. Reconcile duplicate provider executions by:
   - stage execution ID
   - provider idempotency key
   - request hash
   - output content hash
20. Implement Mock adapters:
   - VAD
   - diarization
   - transcription
   - translation
   - TTS
   - source separation
   - video intelligence
   - local inference
21. Make mocks deterministic:
   - fixture-script driven
   - stable text
   - stable timestamps
   - stable speaker labels
   - stable duration behavior
   - configurable failures
22. Implement Azure adapters:
   - STT
   - diarization
   - translation
   - TTS
23. Implement OpenAI adapters:
   - STT
   - translation
   - TTS
24. Implement Google/Gemini adapters:
   - Speech-to-Text
   - Translation
   - Gemini LLM for context/translation where configured
   - Text-to-Speech where configured
25. Implement Local Inference adapter:
   - gRPC/HTTP bridge to Python inference service
   - model registry reference
   - model artifact hash
   - device profile
   - concurrency policy
   - warmup state
26. Implement long-running provider job support:
   - start job
   - store external job ID
   - poll status
   - reconcile after lease loss
27. Implement provider contract test fixtures:
   - 429
   - timeout
   - malformed payload
   - async job lifecycle
   - duplicate request
   - partial result
   - job expiry
   - quota exhausted
28. Register providers in DI.
29. Fail fast on missing credentials for configured non-mock providers.
30. Record provider execution even when fallback occurs.

### Files / Components

- `src/DubbingPlatform.Application/Abstractions/Providers/*`
- `src/DubbingPlatform.Application/Providers/*`
- `src/DubbingPlatform.Infrastructure/Providers/Mock/*`
- `src/DubbingPlatform.Infrastructure/Providers/Azure/*`
- `src/DubbingPlatform.Infrastructure/Providers/OpenAI/*`
- `src/DubbingPlatform.Infrastructure/Providers/Google/*`
- `src/DubbingPlatform.Infrastructure/Providers/LocalInference/*`

### Dependencies

Steps 2 and 3.

### Expected Result

Providers are routable by capability and policy, failures are classified correctly, executions are traceable, and adapters are testable without live paid calls.

### Validation

- Mock providers pass determinism tests.
- Capability mismatch blocks routing.
- Privacy policy blocks disallowed provider.
- Provider health avoids unhealthy route.
- Retryable failure triggers fallback according to budget.
- Quality failure does not masquerade as transport failure.
- Provider executions are recorded for every call.
- Contract tests cover async and failure cases.

---

## 7. API Foundation, Authorization, and Idempotency

### Objective

Implement secure API endpoints for projects, uploads, processing, segments, reviews, exports, and downloads with race-safe idempotency and role-based authorization.

### Actions

1. Implement JWT bearer authentication.
2. Define roles:
   - `TenantAdmin`
   - `ProjectOwner`
   - `ProjectEditor`
   - `Reviewer`
   - `ProjectViewer`
   - `Service`
3. Define authorization matrix per endpoint.
4. Enforce tenant and project ownership.
5. Implement idempotency middleware/filter for mutation endpoints.
6. Define `IdempotencyRecord`:
   - tenant
   - endpoint
   - idempotency key
   - request hash
   - state: `Started`, `Succeeded`, `Failed`
   - response status
   - response body or resource reference
   - created at
   - expires at
7. Make idempotency insertion atomic.
8. Store canonical response body or reconstructible resource ID.
9. Support concurrent duplicate requests safely.
10. Return same response for matching replay.
11. Return `409 CONFLICT` for same key with different request hash.
12. Define endpoint-class retention:
   - project creation: 7 days
   - upload creation: 7 days
   - upload complete: 7 days
   - processing start: 7 days
   - cancel: 24 hours
   - retry: 24 hours
   - export: 24 hours
13. Implement controllers:
   - `ProjectsController`
   - `UploadsController`
   - `ProcessingController`
   - `SegmentsController`
   - `ReviewsController`
   - `ExportsController`
   - `OutputController`
   - `AdminController`
14. Implement endpoints:
   - `POST /api/v1/projects`
   - `GET /api/v1/projects`
   - `GET /api/v1/projects/{projectId}`
   - `DELETE /api/v1/projects/{projectId}`
   - `POST /api/v1/projects/{projectId}/uploads`
   - `GET /api/v1/projects/{projectId}/uploads/{uploadId}`
   - `POST /api/v1/projects/{projectId}/uploads/{uploadId}/parts`
   - `POST /api/v1/projects/{projectId}/uploads/{uploadId}/complete`
   - `POST /api/v1/projects/{projectId}/uploads/{uploadId}/abort`
   - `POST /api/v1/projects/{projectId}/processing`
   - `GET /api/v1/projects/{projectId}/processing`
   - `POST /api/v1/projects/{projectId}/cancel`
   - `POST /api/v1/projects/{projectId}/retry`
   - `GET /api/v1/projects/{projectId}/progress`
   - `GET /api/v1/projects/{projectId}/segments`
   - `GET /api/v1/projects/{projectId}/segments/{segmentId}`
   - `POST /api/v1/projects/{projectId}/segments/{segmentId}/retry`
   - `GET /api/v1/projects/{projectId}/reviews`
   - `GET /api/v1/reviews/{reviewId}`
   - `POST /api/v1/reviews/{reviewId}/approve`
   - `POST /api/v1/reviews/{reviewId}/reject`
   - `POST /api/v1/reviews/{reviewId}/requeue`
   - `POST /api/v1/reviews/{reviewId}/resolve`
   - `POST /api/v1/projects/{projectId}/exports`
   - `GET /api/v1/projects/{projectId}/exports`
   - `GET /api/v1/projects/{projectId}/exports/{exportId}`
   - `GET /api/v1/projects/{projectId}/exports/{exportId}/download`
   - `GET /api/v1/projects/{projectId}/output/download`
15. Add pagination envelope:
   - items
   - page
   - pageSize
   - total
   - hasMore
16. Add OpenAPI:
   - schemas
   - error envelope
   - JWT scheme
   - Idempotency-Key header
   - pagination
17. Enforce signed URLs for downloads.
18. Enforce 15-minute signed URL expiry by default.
19. Record audit events for privileged operations.
20. Ensure all endpoints emit correlation IDs.

### Files / Components

- `src/DubbingPlatform.Api/Controllers/*`
- `src/DubbingPlatform.Api/Middleware/*`
- `src/DubbingPlatform.Application/Services/ProjectService.cs`
- `src/DubbingPlatform.Application/Services/UploadService.cs`
- `src/DubbingPlatform.Application/Services/IdempotencyService.cs`
- `src/DubbingPlatform.Application/Authorization/*`

### Dependencies

Steps 2 and 3.

### Expected Result

The API is secure, replay-safe, paginated, auditable, and ready for workflow endpoints.

### Validation

- Unauthorized returns 401.
- Forbidden tenant access returns 403.
- Idempotent duplicate returns same response.
- Idempotent mismatch returns 409.
- Concurrent duplicate requests do not duplicate state.
- Signed URL expires.
- OpenAPI document is complete.

---

## 8. Resumable Uploads and Media Ingestion

### Objective

Implement resumable multipart uploads, upload completion, content hashing, media validation, duplicate detection, and media readiness.

### Actions

1. Implement project creation:
   - validate source/target language
   - validate settings
   - calculate initial configuration hash
   - persist project
   - publish project created event
2. Implement upload creation:
   - validate file name, content type, declared size
   - enforce max upload size
   - create upload session
   - create S3 multipart upload
   - return part size and upload ID
3. Treat object storage multipart state as authoritative for uploaded parts.
4. Store DB upload session metadata and reconcile at completion.
5. Implement part URL endpoint:
   - generate presigned upload URLs
   - expiry 15 minutes
6. Implement upload status endpoint:
   - completed parts
   - missing parts
   - storage authoritative state
7. Implement upload completion:
   - validate required parts
   - complete multipart upload
   - mark session completed
   - publish `MediaUploaded`
8. Implement upload abort:
   - abort multipart upload
   - mark session aborted
9. Implement media ingestion worker:
   - validate upload completion
   - validate expected client SHA-256 if supplied
   - compute or verify SHA-256
   - create tenant-scoped content object
   - create source artifact
   - detect duplicate content hash within tenant/project
10. If duplicate within same project:
   - link to existing media asset
   - mark upload duplicate
   - do not create new pipeline
11. If duplicate across projects in same tenant:
   - reuse content object
   - create new logical media asset/artifact reference
12. Perform content sniffing after upload.
13. Do not trust client MIME type or extension.
14. Run FFprobe on staged or streamed media.
15. Parse FFprobe JSON:
   - container
   - duration
   - streams
   - codecs
   - resolution
   - frame rate
   - audio sample rate
   - channels
16. Validate:
   - size
   - duration
   - container allowlist
   - decodable audio
   - supported video if present
   - corruption
17. Reject:
   - unsupported media: `MEDIA_UNSUPPORTED`
   - corrupt media: `MEDIA_CORRUPT`
18. Persist media metadata.
19. Persist FFprobe analysis artifact.
20. Record `MediaValidation` stage execution.
21. If valid:
   - media asset valid
   - project media ready
   - publish media validated
22. If invalid:
   - media asset invalid
   - project media rejected
   - record failure reason
23. Enforce storage quota on upload completion.
24. Enforce media duration quota.
25. Add duplicate upload tests.

### Files / Components

- `src/DubbingPlatform.Api/Controllers/ProjectsController.cs`
- `src/DubbingPlatform.Api/Controllers/UploadsController.cs`
- `src/DubbingPlatform.Application/Services/UploadService.cs`
- `src/DubbingPlatform.Workers/Consumers/MediaIngestionWorker.cs`
- `src/DubbingPlatform.Infrastructure/Media/FFprobeService.cs`

### Dependencies

Steps 4, 5, 6, 7.

### Expected Result

Uploads are resumable, media is validated safely, duplicates are detected, and valid projects become ready for processing.

### Validation

- Valid MP4 fixture becomes media ready.
- Invalid text file renamed MP4 is rejected.
- Incomplete upload completion fails.
- Duplicate same-project upload links existing asset.
- Cross-project same-tenant duplicate reuses content object but creates logical asset.
- Storage quota rejection works.
- FFprobe output artifact exists.

---

## 9. Processing Start, Media Analysis, and Canonical Audio Preparation

### Objective

Start processing explicitly, create authoritative processing runs, analyze media, and produce canonical archival audio.

### Actions

1. Implement `POST /projects/{projectId}/processing`.
2. Validate:
   - project media ready
   - no active non-terminal run
   - quota checks
   - cost preflight estimate
   - privacy policy valid
3. Create `ProcessingRun`:
   - status pending
   - attempt
   - pipeline version
   - configuration hash
   - provider route hash
   - execution snapshot basis
4. Start saga.
5. Publish `RunStarted`.
6. Implement `MediaAnalyzerWorker`:
   - claim stage execution
   - validate source artifact
   - verify content hash
   - produce analysis artifact
   - record processing plan
7. Implement `AudioPreparationWorker`:
   - extract audio
   - produce canonical archival audio
   - default 48 kHz / 24-bit PCM or FLAC
   - preserve channel layout
   - produce optional 32-bit float working file only when needed
   - verify output with FFprobe
   - upload artifact
   - record exact FFmpeg arguments
8. Enforce media worker resource limits:
   - CPU threads
   - memory
   - temp disk
   - process timeout
   - max concurrent jobs
9. Check disk space before FFmpeg.
10. Clean temp directories always.
11. Record stage executions.
12. Publish next stage.

### Files / Components

- `src/DubbingPlatform.Api/Controllers/ProcessingController.cs`
- `src/DubbingPlatform.Workers/Consumers/MediaAnalyzerWorker.cs`
- `src/DubbingPlatform.Workers/Consumers/AudioPreparationWorker.cs`
- `src/DubbingPlatform.Infrastructure/Media/FFmpegService.cs`

### Dependencies

Step 8.

### Expected Result

A project can start explicitly and produces a valid canonical audio artifact with durable run state.

### Validation

- Start processing creates one active run.
- Second start returns conflict.
- Canonical audio FFprobe validates sample rate/layout.
- Duration matches source within tolerance.
- FFmpeg arguments are recorded.
- Resource exhaustion checks fail fast.

---

## 10. Source Separation and Background Policy

### Objective

Implement optional source separation with normalized confidence, safe fallback, explicit audited decisions, and background preservation.

### Actions

1. Read project source separation policy.
2. If disabled:
   - skip stage
   - select canonical audio
   - record skip reason
3. If enabled:
   - invoke source separation provider
   - store dialogue stem
   - store background stem if available
   - record provider confidence
4. Normalize provider confidence through policy layer.
5. Do not compare raw provider confidence values across providers directly.
6. Define acceptance threshold as normalized score.
7. Default normalized threshold equivalent to original 0.70 policy.
8. If below threshold:
   - fallback to canonical audio
   - record warning
   - store fallback reason
9. If above threshold:
   - select separated dialogue for speech processing
   - store background for mixing
10. Record provider execution.
11. Record stage execution output artifact references.
12. Publish next stage.
13. Ensure fallback path is explicit and queryable.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/SourceSeparationWorker.cs`
- `src/DubbingPlatform.Application/Services/SourceSeparationService.cs`

### Dependencies

Step 9.

### Expected Result

Source separation is optional, safe, and auditable, with deterministic fallback.

### Validation

- Disabled policy skips stage.
- Low normalized confidence falls back.
- High normalized confidence selects separated audio.
- Warning recorded on fallback.
- Provider execution recorded.

---

## 11. VAD, Segment Builder, and Overlap Model

### Objective

Detect speech regions and build canonical segments with stable IDs, sequence ordering, overlap relations, and timeline validation.

### Actions

1. Implement `VadWorker`:
   - invoke VAD provider
   - persist VAD regions artifact
2. Implement `SegmentBuilderWorker`:
   - merge short pauses
   - split long speech
   - respect speaker boundaries when available
   - preserve intentional overlaps
   - align to word timestamps when available
3. Assign stable segment IDs.
4. Assign deterministic sequence numbers by start time.
5. Persist segments:
   - start
   - end
   - duration
   - sequence
   - status
   - speaker placeholder if not diarized
6. Create `OverlapGroup` records where overlap detected.
7. Create `SegmentOverlap` relationships:
   - segment
   - overlap group
   - relation type
   - order
   - overlap start/end
8. Do not rely solely on a single `OverlapGroupId` field.
9. Validate timeline:
   - no negative start
   - no end before start
   - no duration beyond media
   - no invalid overlaps
   - no overflow
10. Persist VAD and segment artifacts.
11. Publish next stage.
12. Enforce max segment count quota.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/VadWorker.cs`
- `src/DubbingPlatform.Workers/Consumers/SegmentBuilderWorker.cs`
- `src/DubbingPlatform.Application/Services/SegmentBuilderService.cs`

### Dependencies

Step 10.

### Expected Result

Canonical speech segments exist with deterministic timeline and explicit overlap relationships.

### Validation

- Fixture with known speech produces expected segments.
- Overlap fixture creates overlap relationships.
- Invalid timeline rejected.
- Segment count quota enforced.
- Sequence ordering verified.

---

## 12. Speaker Diarization and Speaker Identity

### Objective

Map speech segments to stable project-scoped speakers with provenance and retry-safe mapping.

### Actions

1. Implement `DiarizationWorker`.
2. Invoke diarization provider.
3. Store diarization artifact.
4. Map provider speaker labels to internal speaker IDs.
5. Persist `Speaker`:
   - stable speaker key
   - display name
   - first appearance
   - last appearance
   - mapping method
   - mapping version
   - confidence
   - provider label provenance
6. Update segment speaker references.
7. Treat speaker mapping as derived data.
8. Preserve mapping history for reruns.
9. If diarization fails permanently:
   - fallback to single-speaker assignment if policy allows
   - record warning
   - record fallback reason
10. If fallback not allowed:
   - fail stage
11. Publish next stage.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/DiarizationWorker.cs`
- `src/DubbingPlatform.Application/Services/DiarizationService.cs`

### Dependencies

Step 11.

### Expected Result

Every segment has a speaker assignment, and speaker identity has provenance.

### Validation

- Multi-speaker fixture maps same speaker consistently.
- Distinct speakers get different IDs.
- Fallback warning recorded.
- Mapping provenance stored.

---

## 13. Transcription

### Objective

Produce versioned transcripts with word timestamps, confidence, provider fallback, and manual review states.

### Actions

1. Implement `TranscriptionWorker`.
2. Schedule segment-scoped stage executions.
3. Dispatch segments in bounded batches.
4. Invoke transcription provider per segment or batch where supported.
5. Persist `TranscriptVersion`:
   - provider
   - model
   - language
   - text
   - confidence
   - word timestamps artifact
   - selected flag
   - review flag
6. Record provider execution.
7. Handle low confidence:
   - retry according to budget
   - fallback provider if compatible
   - mark review required if still low
8. Do not fail entire project for one low-confidence segment unless failure policy requires.
9. Select best transcript version using deterministic policy:
   - confidence
   - provider priority
   - completeness
   - timestamp quality
10. Store unselected alternatives.
11. Publish stage completion per segment.
12. Update barrier summary.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/TranscriptionWorker.cs`
- `src/DubbingPlatform.Application/Services/TranscriptionService.cs`

### Dependencies

Step 12.

### Expected Result

Eligible segments have selected transcripts or review states.

### Validation

- Clear speech fixture produces selected transcripts.
- Low confidence fallback works.
- Persistent low confidence creates review item.
- Provider executions recorded.
- Barrier counts correct.

---

## 14. Context Build

### Objective

Build reusable conversation context windows at the correct granularity.

### Actions

1. Implement `ContextBuilderWorker`.
2. Define context windows from:
   - adjacent segments
   - speaker identity
   - transcript text
   - glossary
   - style
   - target language
   - max token budget
3. Create `ContextWindow` entities.
4. Assign segments to context windows through `SegmentContextAssignment`.
5. Make context build deterministic for a given transcript version and configuration.
6. Persist context artifact per window.
7. Do not create one unique context artifact per segment unless deliberately required.
8. Record stage executions with window scope.
9. Publish translation requests only after required context windows complete.
10. Record prompt template and prompt hash for LLM-based context summarization if used.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/ContextBuilderWorker.cs`
- `src/DubbingPlatform.Application/Services/ContextBuilderService.cs`

### Dependencies

Step 13.

### Expected Result

Translation consumes stable reusable context windows.

### Validation

- Window boundaries are bounded.
- Same input produces same context hash.
- Context artifacts are reusable by multiple segments.
- Prompt metadata recorded.

---

## 15. Translation

### Objective

Produce versioned translations with glossary, style, timing awareness, bounded candidate generation, and deterministic selection.

### Actions

1. Implement `TranslationWorker`.
2. Schedule segment-scoped executions.
3. Invoke translation provider with:
   - source text
   - context window reference
   - glossary
   - speaker info
   - target duration
   - style constraints
   - target language
4. Persist `TranslationVersion`:
   - primary text
   - alternative texts
   - semantic score
   - naturalness score
   - timing score
   - provider/model
   - prompt metadata
   - selected flag
5. Enforce segment budget:
   - max candidates
   - max tokens
   - max cost
   - max wall-clock
6. Use deterministic scoring heuristics by default.
7. Do not use AI judge by default.
8. Store alternatives for timing optimization.
9. Record provider executions.
10. Mark review required when quality below policy and fallback exhausted.
11. Publish stage completion.
12. Update barrier.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/TranslationWorker.cs`
- `src/DubbingPlatform.Application/Services/TranslationService.cs`

### Dependencies

Step 14.

### Expected Result

Translations are context-aware, bounded in cost, and traceable.

### Validation

- Glossary term used.
- Context window bounded.
- Candidate budget enforced.
- Selected translation marked.
- Quality failure routes to review when configured.

---

## 16. Voice Assignment and Voice Profiles

### Objective

Assign stable voices to speakers with consent-aware policies and durable speaker-scoped assignments.

### Actions

1. Implement `VoiceAssignmentWorker`.
2. Use speaker-scoped stage executions.
3. Resolve each speaker to a voice profile:
   - target language
   - project style
   - voice overrides
   - provider voice inventory
   - tenant policy
4. Persist `SpeakerVoiceAssignment`.
5. Ensure same speaker receives same voice across all segments.
6. Ensure assignment stability across retries unless explicitly overridden.
7. Record assignment metadata:
   - provider
   - voice ID
   - voice version
   - assignment reason
   - policy hash
8. Enforce voice cloning disabled by default.
9. If voice cloning enabled:
   - require consent record
   - validate consent status
   - validate scope and subject
   - record audit event
10. Define `ConsentRecord`:
   - subject identity
   - evidence/reference
   - scope
   - jurisdiction/policy context
   - timestamp
   - revocation
   - voice profile relation
11. Publish completion per speaker.
12. Update project-level barrier.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/VoiceAssignmentWorker.cs`
- `src/DubbingPlatform.Application/Services/VoiceAssignmentService.cs`
- `src/DubbingPlatform.Domain/Entities/VoiceProfile.cs`
- `src/DubbingPlatform.Domain/Entities/ConsentRecord.cs`

### Dependencies

Step 12.

### Expected Result

Voice assignment is stable, policy-aware, and auditable.

### Validation

- Speaker-to-voice mapping stable.
- Voice cloning without consent rejected.
- Consent revocation blocks new cloning use.
- Provider voice incompatibility fails route.

---

## 17. Voice Generation (TTS)

### Objective

Generate target-language speech artifacts with provider traceability, duration estimation, and bounded preview behavior.

### Actions

1. Implement `VoiceGenerationWorker`.
2. Schedule segment-scoped executions.
3. Use selected translation and assigned voice profile.
4. Run deterministic duration estimator before paid TTS:
   - language phoneme/speaking-rate model
   - target window
   - prosody parameters
5. Apply initial SSML/prosody adjustment before first TTS call when possible.
6. Generate preview artifacts separately from final artifacts.
7. Persist `GeneratedAudioArtifact`:
   - provider
   - model
   - voice profile
   - duration
   - content object
   - attempt
   - preview/final flag
8. Record provider executions.
9. Enforce TTS attempts budget.
10. Enforce cost reservation before provider call.
11. Publish completion per segment.
12. Update barrier.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/VoiceGenerationWorker.cs`
- `src/DubbingPlatform.Application/Services/TtsService.cs`

### Dependencies

Steps 15 and 16.

### Expected Result

Every accepted translated segment has generated audio or a controlled failure/review state.

### Validation

- Generated audio readable by FFprobe.
- Preview and final artifacts distinct.
- Duration estimator reduces unnecessary attempts.
- Cost reservation enforced.
- Provider execution recorded.

---

## 18. Timing Optimization

### Objective

Fit generated dialogue into source timing windows using an explicit bounded optimization sub-workflow with deterministic scoring.

### Actions

1. Implement `TimingOptimizationWorker`.
2. Define timing dimensions:
   - target onset
   - target voiced duration
   - target window
   - allowable lead/lag
   - allowable internal silence
   - speaking-rate limit
   - time-stretch limit
3. Default limits:
   - preferred fit ±50 ms
   - maximum acceptable fit ±100 ms
   - max rate change ±15%
   - max stretch 1.15x
4. Measure generated audio duration.
5. Compare to target window.
6. Run bounded optimization loop:
   - try alternate translation candidate
   - adjust prosody/rate
   - request translation rewrite
   - apply time stretch
7. Enforce limits:
   - max translation candidates = 3
   - max TTS preview attempts = 3
   - max rewrites = 2
8. Compute deterministic `syncScore`:
   - duration error
   - onset error
   - rate delta
   - stretch penalty
   - silence penalty
9. Classify result:
   - `SyncAcceptable`
   - `SyncAcceptableWithWarning`
   - `SyncRetryable`
   - `ManualReviewRequired`
10. Persist `SyncResult`.
11. Update selected translation/generated audio when changed.
12. Record all attempts and provider executions.
13. Mark review required when loop exhausted and tolerance not met.
14. Publish completion per segment.
15. Update barrier.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/TimingOptimizationWorker.cs`
- `src/DubbingPlatform.Application/Services/TimingOptimizationService.cs`

### Dependencies

Step 17.

### Expected Result

Segments meet timing constraints or are explicitly classified for retry/review.

### Validation

- Tight-window fixture respects limits.
- Loop stops after configured attempts.
- No stretch/rate limit violation.
- SyncResult stored.
- Review created when exhausted.

---

## 19. Timeline Assembly

### Objective

Place generated audio on the source timeline while preserving silence, intentional overlap, and timeline integrity.

### Actions

1. Implement `TimelineAssemblerWorker`.
2. Load:
   - segments
   - selected generated audio
   - source timeline
   - selected background audio
   - overlap relations
3. Place each generated segment at assigned start time.
4. Preserve valid source silence.
5. Preserve intentional overlaps using overlap relationships.
6. Detect invalid overlaps.
7. Detect timeline overflow.
8. Persist JSON timeline artifact with schema version.
9. Publish next stage.
10. Record stage execution.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/TimelineAssemblerWorker.cs`
- `src/DubbingPlatform.Application/Services/TimelineAssemblyService.cs`

### Dependencies

Step 18.

### Expected Result

A deterministic timeline artifact exists and is ready for mixing.

### Validation

- Segment placement matches timeline artifact.
- Intentional overlaps preserved.
- Invalid overlaps blocked.
- Overflow detected.

---

## 20. Audio Mixing and Loudness

### Objective

Mix dubbed dialogue with background audio using FFmpeg filter graphs, loudness targets, clipping prevention, and ducking policy.

### Actions

1. Implement `AudioMixerWorker`.
2. Use FFmpeg `complex_filter` for mixing.
3. Do not implement a custom C# sample mixer for final production mixing.
4. Apply:
   - loudness normalization
   - true-peak limiting
   - clipping prevention
   - ducking
   - crossfades where appropriate
   - sample-rate consistency
   - channel layout handling
5. Default loudness:
   - integrated -16 LUFS
   - true peak -1 dBTP
6. Broadcast profile:
   - integrated -23 LUFS
   - true peak -1 dBTP
7. Default ducking:
   - -12 dB
   - 150 ms fade-in/out
8. Use two-pass loudness normalization where determinism is required.
9. Ensure final mix sample rate is 48 kHz.
10. Persist final audio artifact.
11. Record exact FFmpeg filter graph.
12. Publish QC request.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/AudioMixerWorker.cs`
- `src/DubbingPlatform.Infrastructure/Media/FFmpegMixer.cs`

### Dependencies

Step 19.

### Expected Result

Final mixed audio meets loudness and integrity requirements.

### Validation

- FFprobe validates duration/sample rate/channels.
- Loudness measurement within target.
- True peak within limit.
- No clipping above threshold.
- Ducking applied in expected regions.

---

## 21. Quality Control

### Objective

Run segment-level and project-level QC checks, produce structured reports, and block invalid renders.

### Actions

1. Implement `QualityControlWorker`.
2. Segment checks:
   - missing segment
   - empty translation
   - missing voice assignment
   - missing generated audio
   - sync failure
   - checksum mismatch
   - stale provider metadata
   - unresolved review
3. Project checks:
   - invalid overlaps
   - overflow
   - unexpected gaps
   - duration drift
   - clipping
   - excessive silence
   - corrupt artifacts
   - sample rate mismatch
   - channel configuration mismatch
   - speaker-to-voice instability
   - terminology violations where configured
4. Add signal-based assertions:
   - channel routing
   - dialogue placement
   - background attenuation
   - peak/RMS/loudness
   - silence boundaries
   - crossfade behavior
5. Persist `QualityResult`:
   - scope
   - status
   - code
   - severity
   - message
   - details
   - artifact reference
6. Classify:
   - `PASS`
   - `PASS_WITH_WARNINGS`
   - `RETRY_REQUIRED`
   - `MANUAL_REVIEW_REQUIRED`
   - `BLOCKED`
7. Block render on any blocking result.
8. Store QC report artifact.
9. Publish render request only when allowed.
10. Create review items for review-required results.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/QualityControlWorker.cs`
- `src/DubbingPlatform.Application/Services/QualityControlService.cs`

### Dependencies

Step 20.

### Expected Result

Invalid output cannot pass to render, and QC results are queryable.

### Validation

- Corrupt artifact blocks render.
- Clean fixture passes or passes with warnings.
- QC report artifact persisted.
- Review items created when required.

---

## 22. Final Rendering

### Objective

Mux final audio with original video or produce audio-only output, then validate final media.

### Actions

1. Implement `RenderWorker`.
2. For video:
   - copy original video stream when safe
   - replace/add audio stream
   - if unsafe, re-encode H.264 high-quality preset
   - record re-encode decision
3. For audio-only:
   - encode WAV/MP3/AAC as requested
4. Pad or trim final audio within tolerance.
5. Tolerances:
   - video: within one frame of source duration
   - audio-only: within 100 ms
6. Validate output with FFprobe.
7. Register `OutputAsset`.
8. Persist render artifact and metadata.
9. Mark run completed if validation succeeds and no blocking review exists.
10. Publish `RunCompleted`.
11. If failure:
   - mark run failed
   - publish `RunFailed`
12. Record stage execution.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/RenderWorker.cs`
- `src/DubbingPlatform.Infrastructure/Media/RenderService.cs`

### Dependencies

Step 21.

### Expected Result

Final output exists, is validated, and run state reaches terminal state correctly.

### Validation

- Full pipeline produces final MP4.
- Duration within tolerance.
- Video stream copied when safe.
- Re-encode decision recorded when not safe.
- Output download URL works.

---

## 23. Export Jobs and Downloads

### Objective

Implement on-demand export generation as independent durable jobs with completeness metadata and secure downloads.

### Actions

1. Remove export from core processing DAG.
2. Define `ExportJob` entity.
3. Support export formats:
   - SRT
   - WebVTT
   - JSON timeline
   - speaker metadata JSON
   - transcript JSON
   - translation JSON
   - quality report JSON
4. Implement `ExportService`.
5. Generate exports from selected immutable run data.
6. Support completed projects.
7. Support partial exports for failed/cancelled projects where data exists.
8. Do not export final rendered media unless render completed.
9. Include completeness metadata:
   - project ID
   - run ID
   - source hash
   - completed segment count
   - failed segment count
   - skipped segment count
   - review count
   - completeness flag
   - generated-at snapshot
10. Persist export artifacts immutably.
11. Record audit events.
12. Return signed download URLs.
13. Enforce tenant/project ownership.
14. Implement export download endpoint.
15. Implement export list endpoint.

### Files / Components

- `src/DubbingPlatform.Api/Controllers/ExportsController.cs`
- `src/DubbingPlatform.Application/Services/ExportService.cs`
- `src/DubbingPlatform.Application/Exports/*`
- `src/DubbingPlatform.Workers/Consumers/ExportWorker.cs`

### Dependencies

Steps 7 and 22.

### Expected Result

Exports are generated safely on demand and are explicitly complete or partial.

### Validation

- SRT timestamps valid.
- WebVTT format valid.
- JSON timeline matches segments.
- Speaker metadata stable.
- Quality report includes QC entries.
- Partial export includes completeness metadata.
- Signed URL expires.

---

## 24. Progress, Cancellation, Retry, and Manual Review APIs

### Objective

Expose durable progress, cancellation, selective retry, and manual review resolution.

### Actions

1. Implement full progress service.
2. Return:
   - pipeline phase
   - current stage
   - completed units
   - failed units
   - retrying units
   - review units
   - skipped units
   - warnings
   - estimated work remaining
   - percentage indicator
3. Do not present percentage as guaranteed ETA.
4. Implement optional real-time progress:
   - SSE or WebSocket endpoint
   - Redis pub/sub or outbox-driven notifications
5. Implement cancellation service:
   - mark run cancelling
   - block new scheduling
   - notify saga
   - allow in-flight calls to finish or cancel where possible
   - cancel FFmpeg via token where possible
   - mark cancelled only after durable state clean
6. Implement retry service:
   - manual retry stage/segment
   - dependency-aware invalidation
   - new attempts
   - preserve immutable artifacts
   - update selected references only after success
   - enforce quotas/rate limits
   - respect cancellation
7. Implement review APIs:
   - list review items
   - inspect review item
   - approve
   - reject
   - requeue
   - resolve with edit
8. For resolve with edit:
   - create new transcript/translation version as manual version
   - record reviewer
   - record reason
   - record decision metadata
   - resume blocked stage
9. Ensure review resolution updates run state.
10. Ensure project can continue eligible units while other units await review.
11. Ensure final completion blocked by unresolved required reviews.
12. Audit all manual decisions.

### Files / Components

- `src/DubbingPlatform.Api/Controllers/ProcessingController.cs`
- `src/DubbingPlatform.Api/Controllers/SegmentsController.cs`
- `src/DubbingPlatform.Api/Controllers/ReviewsController.cs`
- `src/DubbingPlatform.Application/Services/ProgressService.cs`
- `src/DubbingPlatform.Application/Services/CancellationService.cs`
- `src/DubbingPlatform.Application/Services/RetryService.cs`
- `src/DubbingPlatform.Application/Services/ReviewService.cs`

### Dependencies

Steps 7, 21, 22.

### Expected Result

Operators can observe, cancel, retry, and resolve review states safely.

### Validation

- Progress reflects true unit states.
- Cancel prevents new work.
- Retry invalidates only dependents.
- Review approval resumes pipeline.
- Review rejection blocks or fails according to policy.
- Audit events recorded.

---

## 25. Cost, Quotas, Rate Limits, and Fairness

### Objective

Implement atomic cost enforcement, quotas, provider-specific rate limits, and tenant fairness caps.

### Actions

1. Implement `CostService`:
   - configurable price tables
   - price table versioning
   - estimated cost
   - usage dimensions
   - provider-reported usage
   - reconciled actual cost where available
2. Implement atomic budget reservation:
   - project budget
   - segment budget
   - capability budget
3. Reserve before provider call.
4. Reconcile after call.
5. Release or adjust reservation on failure.
6. Fail with `QUOTA_EXCEEDED` when reservation fails.
7. Implement quotas:
   - max active projects per tenant
   - max projects per day
   - max estimated cost per project
   - max estimated cost per segment
   - max segment count
   - max storage usage
   - max concurrent stage executions per tenant
8. Enforce active projects with partial unique index/transactional count.
9. Enforce storage quota transactionally with artifact commit.
10. Implement provider-specific rate limiters:
   - requests
   - tokens
   - characters
   - audio seconds
   - concurrency
11. Store limiter dimensions in configuration.
12. Use Redis for rate limiting only.
13. Use PostgreSQL for quota correctness.
14. Implement preflight cost estimate at processing start.
15. Implement preflight cost estimate before expensive stages.
16. Implement tenant fairness caps:
   - max active segment-stage executions per tenant
   - max concurrent provider calls per tenant
17. Emit cost and quota metrics.
18. Record quota rejections.

### Files / Components

- `src/DubbingPlatform.Application/Services/CostService.cs`
- `src/DubbingPlatform.Application/Services/QuotaService.cs`
- `src/DubbingPlatform.Infrastructure/Redis/RateLimiter.cs`
- `src/DubbingPlatform.Infrastructure/Persistence/Repositories/CostReservationRepository.cs`

### Dependencies

Steps 4, 6, 7.

### Expected Result

Cost and quota enforcement is race-safe and observable.

### Validation

- Concurrent provider calls cannot overspend reserved budget.
- Quota exceeded returns structured error.
- Rate limit dimensions enforced.
- Tenant concurrency cap enforced.
- Cost records include estimate and usage.

---

## 26. Security, Privacy, Audit, Retention, and Deletion

### Objective

Implement defense-in-depth security, privacy policies, auditable consent, retention, logical deletion, and physical garbage collection.

### Actions

1. Enforce JWT authentication.
2. Enforce role-based authorization.
3. Enforce tenant scoping at repository level.
4. Enable PostgreSQL Row-Level Security for tenant-scoped tables in production.
5. Set tenant context per database session through EF interceptor.
6. Provide restricted maintenance role for migrations and reconcilers.
7. Keep application filters even when RLS enabled.
8. Enforce tenant-prefixed storage keys.
9. Enforce tenant-prefixed Redis keys.
10. Validate tenant ownership before signed URL issuance.
11. Validate message tenant before consumer side effects.
12. Reject cross-tenant messages and move to error/skipped with metric.
13. Implement secret management:
   - environment or secret manager
   - no hardcoded secrets
   - no secrets in logs/hashes/errors
14. Implement secret rotation policy.
15. Implement mTLS for service-to-service where production topology requires.
16. Implement network policies in Kubernetes.
17. Harden containers:
   - non-root
   - read-only root filesystem where possible
   - restricted writable volumes
   - no unnecessary egress
   - resource limits
18. Implement image scanning, SBOM, and image signing.
19. Implement immutable/append-only audit storage.
20. Record audit events for:
   - project creation/deletion
   - processing start
   - cancel
   - retry
   - review decisions
   - export generation
   - consent changes
   - admin access
21. Implement retention configuration:
   - intermediate artifacts default 30 days
   - final outputs default 90 days
   - audit events default 365 days
22. Implement logical deletion:
   - project metadata deleted flag
   - artifact references removed
   - content object reference counts updated
23. Implement physical deletion:
   - only when no references remain
   - only after retention satisfied
   - respect legal holds
24. Implement deletion jobs.
25. Implement retention sweeper.
26. Implement consent lifecycle:
   - grant
   - revoke
   - audit
   - enforce at voice assignment/cloning
27. Ensure voice cloning disabled by default.
28. Ensure privacy policy participates in provider routing.

### Files / Components

- `src/DubbingPlatform.Infrastructure/Security/*`
- `src/DubbingPlatform.Application/Services/AuditService.cs`
- `src/DubbingPlatform.Application/Services/RetentionService.cs`
- `src/DubbingPlatform.Workers/Services/RetentionSweeper.cs`
- `src/DubbingPlatform.Workers/Services/DeletionJobWorker.cs`

### Dependencies

Steps 5, 6, 7, 23.

### Expected Result

Security and privacy controls are enforced, deletion is safe, and auditability is complete.

### Validation

- Cross-tenant API access denied.
- Cross-tenant storage access denied.
- Cross-tenant message rejected.
- RLS negative tests pass.
- Secret rotation runbook tested.
- Retention hold blocks deletion.
- Physical deletion occurs only after reference count zero.
- Consent revocation enforced.

---

## 27. Observability, SLOs, Diagnostics, and Runbooks

### Objective

Provide production observability with SLOs, alerts, dashboards, diagnostics, and operational runbooks.

### Actions

1. Define SLOs:
   - API availability
   - API p95 latency
   - pipeline success rate
   - stage failure rate
   - provider error rate
   - queue depth
   - DLQ depth
   - lease recovery rate
   - export success rate
   - storage orphan rate
2. Define alert thresholds.
3. Define dashboards:
   - pipeline health
   - provider health
   - cost
   - queue depth
   - worker concurrency
   - DLQ
   - tenant usage
   - storage/orphans
   - review backlog
4. Emit metrics:
   - project started/completed/failed/cancelled
   - stage started/completed/failed/retry
   - segment duration
   - provider latency/error/cost
   - quota rejections
   - rate-limit rejections
   - review open/resolved
   - export generated/failed
   - orphan objects detected
   - lease recoveries
5. Add trace enrichment:
   - tenant
   - project
   - run
   - stage
   - provider
   - model
   - attempt
6. Add diagnostics endpoints for authorized operators:
   - stage execution detail
   - provider execution detail
   - DLQ summary
   - lease status
   - review backlog
7. Create runbooks:
   - DLQ inspection
   - lease recovery
   - provider outage
   - storage outage
   - database failover
   - orphan reconciliation
   - quota/cost incident
   - review backlog incident
8. Define on-call escalation.
9. Ensure correlation ID end-to-end.

### Files / Components

- `src/DubbingPlatform.Infrastructure/Observability/*`
- `docs/runbooks/*`
- dashboards configuration

### Dependencies

Steps 3 and 4.

### Expected Result

Operations can detect, diagnose, and respond to failures.

### Validation

- Metrics scraped.
- Alerts fire on simulated failures.
- Dashboards display core metrics.
- Runbooks reviewed and tested.

---

## 28. Testing Strategy and Fixtures

### Objective

Implement comprehensive test tiers covering unit, integration, contract, workflow, E2E, recovery, media signal quality, and operational failure modes.

### Actions

1. Define test tiers:
   - unit
   - integration
   - provider contract
   - workflow integration
   - E2E smoke
   - recovery/chaos
   - load/soak
2. Generate deterministic fixtures using FFmpeg:
   - short single speaker audio
   - multi-speaker audio
   - overlapping speech
   - long silence
   - music plus dialogue
   - noisy speech
   - single speaker video
   - multi-speaker video
   - low-quality audio
   - non-English marker
3. Keep fixtures small.
4. Use Testcontainers for:
   - PostgreSQL
   - RabbitMQ
   - Redis
   - MinIO
5. Use WireMock for provider adapters.
6. Use mock providers for deterministic CI.
7. Add state machine tests.
8. Add idempotency tests.
9. Add duplicate message tests.
10. Add fan-in barrier tests.
11. Add lease fencing tests.
12. Add stale worker commit tests.
13. Add cancellation race tests.
14. Add retry invalidation tests.
15. Add provider failover tests.
16. Add provider async contract tests.
17. Add cross-tenant denial tests:
   - API
   - storage
   - consumers
   - Redis
   - RLS
18. Add cost reservation race tests.
19. Add quota and rate limit tests.
20. Add media resource exhaustion tests.
21. Add disk pressure tests.
22. Add orphan reconciliation tests.
23. Add manual review workflow tests.
24. Add partial export tests.
25. Add loudness/signal tests.
26. Add golden reference checks where practical.
27. Add backup/restore tests.
28. Add migration compatibility tests:
   - old code/new schema
   - new code/old schema where applicable
29. Add load tests for fan-out and fan-in.
30. Add soak tests for long-running jobs.

### Files / Components

- `tests/*`
- `scripts/generate-fixtures.sh`
- `fixtures/*`

### Dependencies

All prior steps as applicable.

### Expected Result

The system is validated for correctness, recovery, isolation, and media quality.

### Validation

- All test tiers pass.
- E2E full pipeline passes with mocks.
- Recovery tests pass.
- Tenant isolation negative tests pass.

---

## 29. CI/CD, Containers, Kubernetes, KEDA, and GPU

### Objective

Automate build, test, scan, publish, and deployment with production-grade pipeline and workload separation.

### Actions

1. Create CI pipeline:
   - restore
   - build
   - unit tests
   - integration tests
   - contract tests
   - E2E smoke
   - container build
   - image scan
   - SBOM generation
   - image signing
   - publish
2. Fail CI on warnings.
3. Cache NuGet and Docker layers.
4. Create Kubernetes manifests or Helm charts:
   - namespace
   - API deployment
   - control worker
   - media preparation worker
   - media render worker
   - AI provider worker
   - GPU worker
   - export worker
   - maintenance worker
   - secrets
   - config maps
   - ingress
   - network policies
   - pod disruption budgets
5. Use managed service references for:
   - PostgreSQL
   - object storage
   - RabbitMQ or managed broker
   - Redis
6. Do not self-host stateful production dependencies by default.
7. Configure probes:
   - liveness process-local
   - readiness dependency-specific
8. Configure resource requests/limits.
9. Configure PVC or high-IOPS scratch storage for media workers.
10. Configure KEDA scalers:
   - queue depth
   - CPU/memory for media
   - active lease count where useful
   - GPU utilization for GPU workers
11. Set bounded concurrency for media workers.
12. Add GPU node selector/taints and `nvidia.com/gpu` resources.
13. Configure secret consumption from external secret manager.
14. Add environment-specific configuration.
15. Add migration job before API rollout.
16. Require expand/contract schema compatibility.
17. Add deployment instructions.
18. Add rollback procedure.
19. Add staging and production environments.
20. Add optional blue/green or canary automation later without blocking core delivery.

### Files / Components

- `.github/workflows/ci.yml`
- `deploy/k8s/*`
- `deploy/helm/*`
- `README.md`

### Dependencies

Steps 24 and 28.

### Expected Result

The platform can be built, tested, scanned, published, and deployed repeatably.

### Validation

- CI passes.
- Images scan and sign.
- Migration job succeeds before API rollout.
- API becomes ready.
- Workers scale under queue load.
- Media workers respect concurrency limits.
- GPU worker schedules only on GPU nodes when enabled.

---

## 30. Production Hardening, HA, DR, and Operational Readiness

### Objective

Complete production readiness with high availability, disaster recovery, backup/restore, chaos testing, and operational procedures.

### Actions

1. Define HA topology for managed dependencies.
2. Define backup schedules:
   - database PITR
   - object storage versioning/lifecycle
   - audit retention
3. Define RPO/RTO defaults:
   - RPO: 5 minutes for database
   - RTO: 1 hour for core platform
4. Test restore procedures.
5. Test failover procedures.
6. Define failback procedures.
7. Add chaos tests:
   - database outage
   - broker outage
   - storage outage
   - provider outage
   - worker crash
   - lease loss
   - network partition
8. Add load tests:
   - large segment count
   - concurrent projects
   - large media
   - provider throttling
9. Add media bomb protection tests.
10. Add secret rotation drills.
11. Add incident response runbooks.
12. Add operational dashboards for on-call.
13. Verify zero-downtime migration approach.
14. Verify old/new version compatibility window.
15. Define support diagnostics access controls.

### Files / Components

- `docs/operations/*`
- `docs/dr/*`
- test plans

### Dependencies

Step 29.

### Expected Result

The platform is recoverable, testable under failure, and operationally supportable.

### Validation

- Restore test succeeds.
- Failover test succeeds.
- Chaos scenarios recover.
- Load targets met.
- Secret rotation drill succeeds.

---

## 31. Optional Enrichment: Video Intelligence and Lip Synchronization

### Objective

Provide optional enrichment capabilities without blocking or corrupting core dubbing.

### Actions

1. Implement `VideoIntelligenceWorker` as optional enrichment job.
2. Support:
   - face detection
   - face tracking
   - active speaker detection
   - face-to-speaker association
   - confidence scores
3. Persist enrichment artifacts.
4. Implement `LipSyncWorker` as optional enrichment job.
5. Support:
   - lip movement analysis
   - lip-sync score
   - optional mouth-region transformation
   - post-transformation validation
6. Feature flags:
   - `Features:VideoIntelligenceEnabled`
   - `Features:LipSyncEnabled`
7. Default flags false.
8. Enrichment jobs run after core render when requested.
9. Enrichment failures do not fail core dubbing.
10. Enrichment outputs are separate output assets.
11. Core completion remains independent.
12. Record provider/model metadata for enrichment.
13. Add tests for isolated failure behavior.

### Files / Components

- `src/DubbingPlatform.Workers/Consumers/VideoIntelligenceWorker.cs`
- `src/DubbingPlatform.Workers/Consumers/LipSyncWorker.cs`
- `src/DubbingPlatform.Application/Abstractions/Providers/IVideoIntelligenceProvider.cs`

### Dependencies

Step 22.

### Expected Result

Optional enrichment is available without risking core pipeline correctness.

### Validation

- Core pipeline completes with enrichment disabled.
- Enrichment enabled with mock produces artifacts.
- Enrichment failure does not fail core project.

---

## 32. Local AI / GPU Inference Integration

### Objective

Provide a production-ready boundary for local AI and GPU inference without forcing it into remote HTTP provider assumptions.

### Actions

1. Define local inference provider adapter.
2. Implement Python inference service contract:
   - gRPC or HTTP
   - model name
   - model version
   - model artifact hash
   - capability
   - request payload
   - response payload
   - health
   - warmup
3. Define model registry metadata:
   - model ID
   - version
   - artifact hash
   - device profile
   - capability descriptors
   - runtime requirements
4. Deploy GPU workers separately.
5. Configure GPU queue.
6. Configure KEDA or custom scaling using GPU metrics.
7. Enforce concurrency per GPU worker.
8. Support warmup before accepting work.
9. Record local executions as provider executions.
10. Record model hash and device profile.
11. Treat local inference as provider route option subject to privacy policy.
12. Add mock local inference adapter for tests.
13. Add failure tests:
   - model load failure
   - GPU exhaustion
   - timeout
   - invalid model version

### Files / Components

- `src/DubbingPlatform.Infrastructure/Providers/LocalInference/*`
- `deploy/k8s/gpu-worker.yaml`
- Python inference service contract

### Dependencies

Steps 6 and 29.

### Expected Result

Local inference can be added as a first-class provider family without architectural rework.

### Validation

- Mock local provider passes tests.
- GPU worker schedules correctly.
- Privacy policy can restrict routing to local only.
- Model hash recorded.
- GPU exhaustion handled safely.

---

# Final Verification

## Functional Requirements Checklist

- [ ] Resumable multipart uploads work.
- [ ] Upload sessions can be queried and resumed.
- [ ] Incomplete uploads are rejected.
- [ ] Duplicate uploads are detected.
- [ ] Media validation rejects unsupported/corrupt media.
- [ ] Source media remains byte-for-byte unchanged.
- [ ] Canonical audio is produced losslessly.
- [ ] Source separation is optional and fallback-safe.
- [ ] VAD produces speech regions.
- [ ] Segments have stable IDs and sequence.
- [ ] Overlap is represented relationally.
- [ ] Diarization maps stable speaker IDs.
- [ ] Transcripts are versioned.
- [ ] Context windows are reusable.
- [ ] Translations are versioned and bounded.
- [ ] Voice assignment is stable per speaker.
- [ ] TTS artifacts are versioned and traceable.
- [ ] Timing optimization respects bounds.
- [ ] Timeline preserves source timing.
- [ ] Audio mixing meets loudness targets.
- [ ] QC blocks invalid render.
- [ ] Final render validates duration.
- [ ] Exports are on-demand and durable.
- [ ] Partial exports include completeness metadata.
- [ ] Manual review is actionable.
- [ ] Cancellation is durable.
- [ ] Retry is dependency-aware.
- [ ] Progress is transparent.
- [ ] Provider/model metadata is recorded.
- [ ] Cost and usage are recorded.
- [ ] Tenant isolation is enforced.

## Error Handling and Edge Cases Checklist

- [ ] Interrupted uploads resume.
- [ ] Incomplete uploads detected.
- [ ] Unsupported media rejected.
- [ ] Corrupt media rejected.
- [ ] FFmpeg invoked safely.
- [ ] Source separation fallback explicit.
- [ ] Low-confidence transcription routes to fallback/review.
- [ ] Translation failure retries correct dependents.
- [ ] TTS failure retries correct dependents.
- [ ] Sync failure classified correctly.
- [ ] Invalid overlaps blocked.
- [ ] Intentional overlaps preserved.
- [ ] Timeline overflow detected.
- [ ] Missing segments detected.
- [ ] Clipping detected.
- [ ] Artifact checksum mismatch detected.
- [ ] Provider rate limits delayed/retried.
- [ ] Provider configuration errors fail fast.
- [ ] Invalid input does not retry.
- [ ] Cancellation prevents new work.
- [ ] Worker death recovers through lease timeout/sweeper.
- [ ] Stale worker cannot commit.
- [ ] Duplicate messages do not duplicate state.
- [ ] Duplicate external provider work reconciled.
- [ ] Outbox failure does not lose messages.
- [ ] Inbox prevents duplicate processing.
- [ ] AI loops bounded.
- [ ] Quota exceeded returns structured error.
- [ ] Rate limit exceeded returns structured error.
- [ ] Partial exports remain machine-readable.
- [ ] Orphan blobs reconciled.
- [ ] Retention holds respected.

## Tests Checklist

- [ ] Unit tests for domain and state machines.
- [ ] Unit tests for segment builder.
- [ ] Unit tests for timing bounds.
- [ ] Unit tests for idempotency keys.
- [ ] Unit tests for configuration hash.
- [ ] Unit tests for execution snapshot.
- [ ] Unit tests for mock providers.
- [ ] Integration tests for migrations.
- [ ] Integration tests for outbox/inbox.
- [ ] Integration tests for artifact storage.
- [ ] Integration tests for uploads.
- [ ] Integration tests for API endpoints.
- [ ] Contract tests for Azure/OpenAI/Google adapters.
- [ ] Provider tests for async jobs, 429, malformed responses, expiry.
- [ ] Integration tests for lease recovery.
- [ ] Integration tests for cancellation races.
- [ ] Integration tests for retry invalidation.
- [ ] Integration tests for quota/rate limiting.
- [ ] Cross-tenant denial tests.
- [ ] RLS negative tests.
- [ ] E2E full pipeline test.
- [ ] E2E low-confidence fallback.
- [ ] E2E source separation fallback.
- [ ] E2E overlap fixture.
- [ ] E2E video duration tolerance.
- [ ] E2E audio-only output.
- [ ] E2E exports.
- [ ] E2E manual review resolution.
- [ ] Media signal quality tests.
- [ ] Loudness tests.
- [ ] Load/soak tests.
- [ ] Chaos/recovery tests.
- [ ] Backup/restore tests.
- [ ] Migration compatibility tests.

## Integration and Dependencies Checklist

- [ ] PostgreSQL migrated.
- [ ] RabbitMQ queues durable.
- [ ] Redis available for ephemeral use only.
- [ ] Object storage accessible.
- [ ] FFmpeg/FFprobe installed in media images.
- [ ] MassTransit outbox/inbox configured.
- [ ] Serilog configured.
- [ ] OpenTelemetry configured.
- [ ] Prometheus metrics exposed.
- [ ] Health checks separated correctly.
- [ ] Docker Compose full profile works.
- [ ] Docker Compose fast profile works.
- [ ] Kubernetes manifests deploy.
- [ ] KEDA configured.
- [ ] GPU scheduling configured when enabled.
- [ ] CI builds/tests all projects.
- [ ] Provider adapters configured by environment.
- [ ] Secrets externalized.
- [ ] Signed URLs private and short-lived.
- [ ] Audit events persisted.
- [ ] Retention sweeper configured.
- [ ] Orphan reconciler configured.

## Security and Privacy Checklist

- [ ] JWT authentication enforced.
- [ ] Role authorization enforced.
- [ ] Tenant isolation enforced in API.
- [ ] Tenant isolation enforced in consumers.
- [ ] Tenant isolation enforced in storage.
- [ ] Tenant isolation enforced in Redis keys.
- [ ] Tenant isolation enforced in signed URLs.
- [ ] RLS enabled in production hardening.
- [ ] Cross-tenant negative tests pass.
- [ ] Secrets not logged.
- [ ] Secrets not hashed.
- [ ] Secret rotation supported.
- [ ] mTLS configured where required.
- [ ] Network policies configured.
- [ ] Containers non-root and hardened.
- [ ] Image scanning enabled.
- [ ] SBOM generated.
- [ ] Images signed.
- [ ] Audit events append-only.
- [ ] Consent records complete.
- [ ] Voice cloning disabled by default.
- [ ] Privacy policy participates in routing.
- [ ] Deletion respects holds and retention.

## Observability and Operations Checklist

- [ ] Correlation IDs propagate end-to-end.
- [ ] Structured logs include stage/provider context.
- [ ] Metrics cover pipeline/provider/cost/queue/review.
- [ ] Traces cover API/workers/providers/FFmpeg.
- [ ] SLOs defined.
- [ ] Alerts configured.
- [ ] Dashboards configured.
- [ ] DLQ monitored.
- [ ] Lease recovery monitored.
- [ ] Orphan reconciliation monitored.
- [ ] Cost anomalies observable.
- [ ] Review backlog observable.
- [ ] Runbooks written.
- [ ] On-call escalation defined.
- [ ] Diagnostics endpoints protected.
- [ ] Incident response tested.

## Deployment Checklist

- [ ] Managed PostgreSQL used in production.
- [ ] Managed object storage used in production.
- [ ] Managed/operator RabbitMQ used.
- [ ] Managed Redis used.
- [ ] Migration job runs before API rollout.
- [ ] Expand/contract migration compatibility verified.
- [ ] Liveness/readiness probes correct.
- [ ] Resource limits configured.
- [ ] Media scratch storage configured.
- [ ] KEDA uses workload-aware scaling.
- [ ] GPU workers isolated.
- [ ] Secrets sourced from secret manager.
- [ ] Network policies applied.
- [ ] Ingress configured.
- [ ] Backup/restore verified.
- [ ] DR runbook tested.
- [ ] Rollback procedure tested.
- [ ] Staging environment validated.
- [ ] Production launch gates defined.

## Completion Criteria Checklist

- [ ] A user can upload supported media.
- [ ] A user can resume interrupted uploads.
- [ ] A user can start processing explicitly.
- [ ] A user can observe durable progress.
- [ ] Processing completes for supported fixtures.
- [ ] Final dubbed media can be downloaded.
- [ ] Exports can be downloaded.
- [ ] Source media remains unchanged.
- [ ] Final timeline stays within tolerance.
- [ ] Dialogue is placed in correct windows.
- [ ] Intentional overlap is preserved.
- [ ] Speaker-to-voice mapping is stable.
- [ ] Translation is context-aware.
- [ ] Generated speech meets timing constraints.
- [ ] Background audio is preserved when feasible.
- [ ] Failed segments can be ret ried independently.
- [ ] Interrupted projects can resume.
- [ ] Manual review can be resolved.
- [ ] Artifacts are traceable.
- [ ] Provider/model versions are recorded.
- [ ] Cost and quotas are enforced.
- [ ] Security controls are active.
- [ ] Observability covers all critical paths.
- [ ] Deployment is repeatable.
- [ ] Disaster recovery is tested.
- [ ] The platform remains extensible for enrichment and local inference.