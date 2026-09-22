# Task 6 — Persistence Context and State Machines

## Goal

Implement AppDbContext, all EF configurations, state-machine transition enforcement, hash calculators, and tenant-session handling so the full domain persists with correct constraints and transitions.

## Context

Binding: PostgreSQL 16 system of record; EF Core + Npgsql + NamingConventions snake_case; internal UUIDs as uuid; prefixed public IDs via value converters + API serialization; TenantId on all scoped tables; ProcessingRun authoritative, ProjectStatus projected; RLS as hardening plus app filters; secrets excluded from hashes; hashes deterministic culture-invariant. Transitions: project (Created→Uploading→MediaReady|MediaRejected; MediaReady→Processing→Cancelling|Completed|Failed|ManualReviewRequired; Cancelling→Cancelled; ManualReviewRequired→Processing|Cancelled; Failed→Processing only via manual retry), run (Pending→Running→Completed|Failed|Cancelling|ManualReviewRequired; Cancelling→Cancelled; ManualReviewRequired→Running; Failed→Running only via manual retry), stage (Pending→Scheduled→Running→Completed|Failed|RetryPending|Cancelled|ManualReviewRequired; RetryPending→Scheduled; Failed→Scheduled only manual retry), upload (Created→InProgress→Completed|Aborted|Expired; Completed→Duplicate), review (Open→Approved|Rejected|Requeued|ResolvedWithEdit), export (Pending→Running→Completed|Failed|Cancelled), artifact Pending→Committed→Deleted, content Pending→Committed→Orphaned→Deleted.

## Starting State

Domain enums, value objects, core + execution entities exist per prior tasks. Infrastructure classlib exists with EF Core packages. No DbContext, no configurations, no migrations yet. PostgreSQL available via Testcontainers or local compose for verification.

## Scope

Must implement: AppDbContext, entity configurations for all ~44 entities, value converters for public IDs, query filters, RLS session interceptor, state-machine domain services, ConfigurationHashCalculator, ExecutionSnapshotCalculator. Must not implement: migrations (next task), messaging, storage, providers, API.

## Instructions

1. Create `src/DubbingPlatform.Infrastructure/Persistence/AppDbContext.cs` namespace `DubbingPlatform.Infrastructure.Persistence`: DbSets for every entity in Tasks 4–5 plus MassTransit outbox/inbox tables (use `AddTransactionalOutboxEntities`/`AddTransactionalInboxEntities` pattern — actual MassTransit wiring in Task 11, but DbSets/config base here). Constructor `AppDbContext(DbContextOptions<AppDbContext>)`. Override `OnModelCreating`: `UseSnakeCaseNamingConvention()`, apply all `IEntityTypeConfiguration<>`, define MassTransit outbox tables `outbox_state,outbox_message,inbox_state` snake_case. Set `HasDefaultValueSql("gen_random_uuid()")` for Ids where applicable.
2. Create `src/DubbingPlatform.Infrastructure/Persistence/Configurations/` — one file per entity (e.g., `DubbingProjectConfiguration.cs`): table names snake_case plural (e.g., `dubbing_projects`, `processing_runs`, `stage_executions`, `content_objects`, `artifacts`, `provider_executions`, etc.), keys, required/maxlength (languages 8, hashes 64, storage keys 1024, JSON jsonb), indexes:
   - partial unique: one non-terminal active run per project: `HasIndex(r=>(r.ProjectId)).IsUnique().HasFilter("status IN ('Pending','Running','Cancelling','ManualReviewRequired')")` on processing_runs.
   - unique (TenantId,Endpoint,IdempotencyKey) on idempotency_records.
   - unique (TenantId,ContentHash) on content_objects.
   - unique (ProcessingRunId,StageType,ScopeType,ScopeId,Attempt) on stage_executions.
   - unique (ProcessingRunId,StageType,ScopeType,ScopeId,StageExecutionId) on stage_unit_completions.
   - unique (UploadSessionId,PartNumber) on upload_parts.
   - partial lease index: `WHERE status='Running' AND lease_expires_at < now()` (use `HasFilter`).
   - workload indexes: (TenantId,ProjectId), (ProcessingRunId,StageType,Status), (ProcessingRunId,Status,LeaseExpiresAt), (ProjectId,Sequence) on speech_segments, (TenantId,Status,CreatedAt) on review_items, (TenantId,Status,ExpiresAt) on idempotency + retention candidates, (ProjectId,CreatedAt) on provider_executions, (ContentObjectId) on artifacts, (TenantId,ContentHash) on content_objects.
3. Create `src/DubbingPlatform.Infrastructure/Persistence/Converters/PublicIdConverter.cs`: EF ValueConverter Guid↔string with prefix per entity (use PublicIdMapper). Apply to API-facing DTOs later; for DB keep uuid columns (converter only where string column needed for compat — document: DB stores uuid, converter used for serialization/query translation tests).
4. Create `src/DubbingPlatform.Infrastructure/Persistence/Interceptors/TenantSessionInterceptor.cs`: on connection open execute `SET app.tenant_id = '<tenant>'` when TenantId in async-local `TenantContext`; maintenance role bypasses. Create `TenantContext` AsyncLocal holder in Application.
5. Create `src/DubbingPlatform.Application/StateMachines/`:
   - `ProjectStateMachine.cs` static `CanTransition(ProjectStatus from,to,bool isManualRetry)` enforcing list above.
   - `RunStateMachine.cs`, `StageStateMachine.cs`, `UploadStateMachine.cs`, `ReviewStateMachine.cs`, `ExportStateMachine.cs` similarly. Throw `DomainException` on illegal.
   - `ProjectStatusProjector.cs`: `ProjectStatus ProjectFrom(RunStatus, bool hasOpenRequiredReviews)` mapping.
6. Create `src/DubbingPlatform.Application/Configuration/ConfigurationHashCalculator.cs`: `static string Compute(object settings)` — canonical JSON (sorted keys, invariant, UTF8, no secrets: strip keys containing secret/password/token/key/credential) → SHA256 hex lowercase. Same for `ExecutionSnapshotCalculator.Compute(runConfig, routeHash, inputHashes, promptHashes, policyHash)`.
7. Add `snake_case` verification: all table/column names lowercase with underscores (NamingConventions enforces; add test).
8. RLS SQL files `src/DubbingPlatform.Infrastructure/Persistence/Sql/rls_policies.sql`: `ALTER TABLE <each tenant table> ENABLE ROW LEVEL SECURITY; CREATE POLICY tenant_isolation ON <t> USING (tenant_id = current_setting('app.tenant_id',true)::uuid);` — applied in migration task, defined here.

## Requirements

- R1: AppDbContext with all DbSets builds.
- R2: All unique/partial/composite indexes as listed exist.
- R3: Illegal transitions throw DomainException; legal succeed.
- R4: Hash calculators deterministic, culture-invariant, secrets excluded.
- R5: Tenant interceptor sets app.tenant_id; RLS SQL covers all tenant tables.

## Edge Cases and Error Handling

- Duplicate active run insert: DB unique violation → translate to DomainException CONFLICT.
- Empty TenantId on save: throw before DB call.
- Null settings JSON: treat as `{}` for hashing, do not throw.
- RLS session without tenant: default deny (policy fails closed).

## Security and Safety Requirements

- Secrets never in hashes; interceptor uses parameterized SET; no string-concat SQL except whitelisted table names in RLS file. App filters remain even with RLS.

## Testing

Create `tests/DubbingPlatform.UnitTests/StateMachines/StateMachineTests.cs` (allowed/forbidden transitions for all 6 machines + projector), `HashCalculatorTests.cs` (determinism: same input different key order same hash; secret exclusion: changing secret doesn't change hash; culture invariance).
Create `tests/DubbingPlatform.IntegrationTests/Persistence/SchemaSmokeTests.cs` using Testcontainers.PostgreSql: can create DbContext, `EnsureCreated`, insert Tenant+Project.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~StateMachineTests
dotnet test --filter FullyQualifiedName~HashCalculatorTests
dotnet test --filter FullyQualifiedName~SchemaSmokeTests
```

## Completion Criteria

- DbContext + configs + state machines + calculators + RLS SQL exist; unit+smoke tests pass.

## Traceability

- Plan Section 2 actions 7–28; Assumptions 5,9–12,55–56; Tests checklist state machines, hash, migration integration (partial).
