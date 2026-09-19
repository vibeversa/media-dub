# 006 — Persistence Context and State Machines Report

## Status
COMPLETED

## Summary
Implemented `AppDbContext` with DbSets for all 45 domain entities plus MassTransit outbox/inbox tables, 45 per-entity EF configurations (snake_case tables, required/maxlength, jsonb, all required unique/partial/composite indexes), `PublicIdConverter`, `TenantSessionInterceptor` (parameterized `set_config`), `TenantContext` AsyncLocal holder, 8 state machines + `ProjectStatusProjector`, canonical `ConfigurationHashCalculator`/`ExecutionSnapshotCalculator`, and `Sql/rls_policies.sql` covering all 44 tenant tables. `dotnet build` and the lint build succeed 0/0; StateMachineTests 86/86, HashCalculatorTests 14/14, offline PersistenceModelTests 11/11 pass; SchemaSmokeTests skips cleanly without Docker (exit 0) and runs EnsureCreated + Tenant/Project insert where Docker exists.

## Files Created/Modified
- `src/DubbingPlatform.Infrastructure/Persistence/AppDbContext.cs` — 48 DbSets (45 domain + `OutboxMessage/OutboxState/InboxState`), `ApplyConfigurationsFromAssembly`, `AddTransactionalOutboxEntities()` + `AddInboxStateEntity()` with explicit `ToTable("outbox_message/outbox_state/inbox_state")`, per-entity tenant query filters (defense in depth), `SaveChanges[Async]` overrides (empty-TenantId → DomainException; Postgres 23505 → CONFLICT DomainException).
- `src/DubbingPlatform.Infrastructure/Persistence/TenantModelCacheKeyFactory.cs` — `IModelCacheKeyFactory` keyed by context type + tenant id so pooled contexts never reuse a cached model across tenants.
- `src/DubbingPlatform.Infrastructure/Persistence/Configurations/*.cs` (45 files, one per entity) — snake_case plural tables, `gen_random_uuid()` id defaults, languages ≤8, hashes ≤64, storage keys ≤1024, JSON as jsonb, enums as string ≤32, all spec indexes (partial unique active-run, unique idempotency/content/stage-identity/unit-completion/upload-part, lease partial, workload composites) plus documented same-shape extras (e.g. unique speaker key, route snapshot per run).
- `src/DubbingPlatform.Infrastructure/Persistence/Converters/PublicIdConverter.cs` — `ValueConverter<Guid,string>` via `PublicIdMapper` + `For<TEntity>()`; XML doc states DB stores uuid, converter is for compat/serialization tests only.
- `src/DubbingPlatform.Infrastructure/Persistence/Interceptors/TenantSessionInterceptor.cs` — PostgreSQL-only; parameterized `SELECT set_config('app.tenant_id', $1, false)`; RESET on maintenance/no-tenant (fail closed, pool-safe); throws on non-Npgsql.
- `src/DubbingPlatform.Infrastructure/Persistence/Sql/rls_policies.sql` — `ENABLE ROW LEVEL SECURITY` + `tenant_isolation` policy (`tenant_id = current_setting('app.tenant_id', true)::uuid`) on all 44 tenant tables; excludes `tenants` (no tenant_id) and outbox/inbox tables; applied by Task 7.
- `src/DubbingPlatform.Application/MultiTenancy/TenantContext.cs` — AsyncLocal tenant + maintenance flag; `SetTenant/BeginScope/BeginMaintenanceScope/Clear` (empty Guid → DomainException).
- `src/DubbingPlatform.Application/StateMachines/` — `ProjectStateMachine`, `RunStateMachine`, `StageStateMachine`, `UploadStateMachine`, `ReviewStateMachine`, `ExportStateMachine` (spec transitions; `CanTransition` + `EnsureCanTransition` throwing DomainException), plus `ArtifactStateMachine` (Pending→Committed→Deleted) and `ContentObjectStateMachine` (Pending→Committed→Orphaned→Deleted) from the Context section; `ProjectStatusProjector.ProjectFrom` delegating to domain `ProjectStatusProjection`.
- `src/DubbingPlatform.Application/Configuration/ConfigurationHashCalculator.cs` — canonical JSON (ordinal-sorted keys, invariant, UTF-8) → SHA-256 lowercase hex; strips keys containing secret/password/token/key/credential (case-insensitive); null → `{}`.
- `src/DubbingPlatform.Application/Configuration/ExecutionSnapshotCalculator.cs` — `Compute(runConfig, routeHash, inputHashes, promptHashes, policyHash)` with ordinal-sorted hash lists, null-tolerant, delegates to ConfigurationHashCalculator.
- `tests/DubbingPlatform.UnitTests/StateMachines/StateMachineTests.cs` — allowed/forbidden theories for all 6 machines + manual-retry gates + idempotent self-transitions + projector mapping (86 cases).
- `tests/DubbingPlatform.UnitTests/Configuration/HashCalculatorTests.cs` — key-order determinism, nested order, secret exclusion (5 key shapes), non-secret sensitivity, null handling, de-DE/tr-TR culture invariance, hex shape, snapshot order-independence/nulls/sensitivity (14 tests).
- `tests/DubbingPlatform.UnitTests/Persistence/PersistenceModelTests.cs` — offline (no DB) model checks: snake_case tables/columns, partial-unique active-run index, 5 required unique indexes, lease/workload indexes, MT tables, tenant filters present (44) / absent on Tenant (11 tests).
- `tests/DubbingPlatform.IntegrationTests/Persistence/SchemaSmokeTests.cs` — `[SkippableFact]` Testcontainers `postgres:16-alpine`: EnsureCreated + insert Tenant/Project + read-back inside `TenantContext.BeginScope`; skips with reason when Docker unavailable.
- `tests/DubbingPlatform.IntegrationTests/DubbingPlatform.IntegrationTests.csproj` — added `Xunit.SkippableFact 1.3.12` (test-only; restored offline from local NuGet cache).

## Decisions Made
- Task text names `AddTransactionalInboxEntities`, which does not exist in MassTransit 9.2.1 (verified via package XML docs: only `AddTransactionalOutboxEntities`, `AddOutboxMessageEntity`, `AddOutboxStateEntity`, `AddInboxStateEntity` in `MassTransit.EntityFrameworkOutboxConfigurationExtensions`). Used `AddTransactionalOutboxEntities()` + `AddInboxStateEntity()`; entities live in `MassTransit.EntityFrameworkCoreIntegration` (`OutboxMessage/OutboxState/InboxState`), extensions in `MassTransit`.
- Lease partial index spec (`WHERE status='Running' AND lease_expires_at < now()`) is not implementable as written: PostgreSQL rejects non-immutable (`now()`) index predicates, which would break EnsureCreated/migrations. Implemented `HasFilter("status = 'Running'")` on `stage_executions.status` plus the specified composite `(ProcessingRunId,Status,LeaseExpiresAt)` covering the sweeper query; expiry comparison stays in the query.
- `EFCore.NamingConventions` is options-level (`optionsBuilder.UseSnakeCaseNamingConvention()`), not `OnModelCreating`; all table names are additionally explicit snake_case so the model is correct with or without the plugin, and MT tables are pinned via `ToTable`.
- Same-state transitions return true (idempotent retries); `StageStateMachine` additionally allows Running→Skipped (Skipped exists in the enum but not in the Context chain); `Failed→…` manual-retry gates take `isManualRetry` (default false). `UnitState`/`State`-vs-`Status` workload indexes map to the actual property names (`UnitState` on completions, `State` on idempotency) and are documented in test names.
- Tenant query filters use the documented instance-field pattern plus a public `TenantModelCacheKeyFactory` for pooling correctness; RLS (session SET per connection open) is primary enforcement and has no model-cache caveat.
- xUnit v2 runner does not honor `Xunit.Sdk.SkipException.ForSkip` (verified: reported FAIL; docs confirm v3-only), and v2 has no `Assert.Skip`. Used `Xunit.SkippableFact 1.3.12` (`[SkippableFact]` + `Skip.If`) so Docker absence reports SKIP, not FAIL or vacuous PASS.
- Extra indexes beyond the spec list (e.g. unique speaker key per project, unique route snapshot per run, unique policy per tenant) are tightening constraints consistent with domain invariants; each is a single line in its config file and noted here.

## Build/Test Results
- `dotnet build` (last 6 lines):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:07.57
```
- `dotnet test --filter FullyQualifiedName~StateMachineTests` (last line):
```
Passed!  - Failed:     0, Passed:    86, Skipped:     0, Total:    86, Duration: 362 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~HashCalculatorTests` (last line):
```
Passed!  - Failed:     0, Passed:    14, Skipped:     0, Total:    14, Duration: 470 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~PersistenceModelTests` (last line):
```
Passed!  - Failed:     0, Passed:    11, Skipped:     0, Total:    11, Duration: 1 s - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~SchemaSmokeTests` (last lines):
```
[xUnit.net 00:00:10.95]     DubbingPlatform.IntegrationTests.Persistence.SchemaSmokeTests.Can_Create_Schema_And_Insert_Tenant_Project [SKIP]
  Skipped DubbingPlatform.IntegrationTests.Persistence.SchemaSmokeTests.Can_Create_Schema_And_Insert_Tenant_Project [1 ms]

Skipped! - Failed:     0, Passed:     0, Skipped:     1, Total:     1, Duration: 10 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- Extra `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true`: `Build succeeded. 0 Warning(s) 0 Error(s)` (18.97s).
- Extra full suites: UnitTests `Passed: 127, Skipped: 0` (16 prior + 86 + 14 + 11); IntegrationTests `Passed: 1, Skipped: 1` (exit 0).
- Two root-caused failures fixed (no tests deleted): reflection `Invoke(this, [modelBuilder])` passed 1 arg to 2-param method → `Invoke(null, [this, modelBuilder])`; v2-incompatible skip → SkippableFact.

## Recommendations for Next Agent (007)
- Repo state: solution builds 0/0 including lint build (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest`). 007 scope is the initial migration + RLS apply + constraint verification. RLS SQL is ready at `src/DubbingPlatform.Infrastructure/Persistence/Sql/rls_policies.sql` (44 tenant tables; excludes `tenants` and MT tables). No migrations exist yet; `dotnet-ef 10.0.12` (per 001 report) is the tool pin.
- DbContext wiring (no composition exists yet — do it in 007 if needed for migration): `new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connStr).UseSnakeCaseNamingConvention().AddInterceptors(new TenantSessionInterceptor())` plus optionally `.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()` when pooling across tenants. `AppDbContext` ctor is `(DbContextOptions<AppDbContext>)`; tenant is captured from `TenantContext` at construction; design-time factory (if added) must run under `TenantContext.BeginMaintenanceScope()` or with no tenant (filters deny-all by default, fine for schema work).
- Key APIs: `TenantContext.SetTenant/BeginScope/BeginMaintenanceScope/Clear` (`DubbingPlatform.Application.MultiTenancy`); machines `XxxStateMachine.CanTransition(from, to[, isManualRetry])` + `EnsureCanTransition` (throw `DomainException`); `ProjectStatusProjector.ProjectFrom(ProcessingRunStatus, bool)`; `ConfigurationHashCalculator.Compute(object?)`; `ExecutionSnapshotCalculator.Compute(runConfig, routeHash, inputHashes, promptHashes, policyHash)`.
- Table names (all snake_case, 45 domain + `outbox_message/outbox_state/inbox_state`): tenants, dubbing_projects, processing_runs, media_assets, upload_sessions, upload_parts, voice_profiles, speakers, speech_segments, context_windows, segment_context_assignments, overlap_groups, segment_overlaps, speaker_voice_assignments, consent_records, transcript_versions, translation_versions, generated_audio_artifacts, sync_results, stage_executions, run_stage_summaries, stage_unit_completions, content_objects, artifacts, artifact_parents, stage_input_artifacts, stage_output_artifacts, provider_executions, provider_capability_descriptors, provider_route_snapshots, prompt_templates, prompt_template_versions, quality_results, review_items, review_decisions, output_assets, export_jobs, export_artifacts, audit_events, idempotency_records, cost_reservations, quota_usages, processing_policies, retention_holds, deletion_jobs.
- Gotchas: `default.shell` is broken — run everything via `default.execute` → `tools["claude-code"].PowerShell`. No `docker`/`make` on this host: Docker-dependent tests must keep the `[SkippableFact]` + `Skip.If` pattern (`Xunit.SkippableFact 1.3.12` is already referenced by IntegrationTests; v2 has no `Assert.Skip` and ignores `Xunit.Sdk.SkipException.ForSkip`). Testcontainers 4.15 uses `new PostgreSqlBuilder("postgres:16-alpine")` (parameterless ctor is obsolete-as-error). EF10 renamed `GetQueryFilter()` → `GetDeclaredQueryFilters()` (old name is obsolete-as-error). MassTransit namespaces: entities `MassTransit.EntityFrameworkCoreIntegration`, model-builder extensions `MassTransit`.
- Test helpers: offline model tests (`tests/DubbingPlatform.UnitTests/Persistence/PersistenceModelTests.cs`) build `AppDbContext` on a dummy Npgsql connection string (no server contact) — extend these for 007 constraint verification (e.g. assert migration snapshot contains RLS). Live test (`tests/DubbingPlatform.IntegrationTests/Persistence/SchemaSmokeTests.cs`) uses `TenantContext.BeginScope` + interceptor; it will exercise RLS once 007 applies policies.
- Warnings: `AnalysisLevel latest`; the Docker-probe `catch (Exception) when (...)` needs its `#pragma warning disable CA1031` justification kept; `StringComparison.Ordinal` everywhere; no secrets in hashes (calculators strip secret/password/token/key/credential substrings, including `*Key` fields — intentional).
- Config: appsettings still 16 empty `{}` sections; connection-string keys are 007's choice — prefer `ConnectionStrings__Postgres` env-style placeholder, never real credentials.
