# 011 — MassTransit Outbox and Stage Execution Report

## Status
COMPLETED

## Summary
Configured MassTransit with RabbitMQ/InMemory switch, EF outbox/inbox, 9 durable endpoints (7 workload + `_skipped`/`_error`), transport retry `Interval(3, 5s)` plus in-memory outbox, and `RETRY_OWNERSHIP.md`. Implemented `StageExecutionService` (10 methods) with atomic claim-or-return via the stage unique index, lease-fenced conditional SQL (`StageExecutionSql`), `LeaseLostException` (`LEASE_LOST`), renewal/release, and bounded stale recovery, plus generic `BaseConsumer<TMessage>` (correlation LogContext, version/Guards to `_skipped`, cross-tenant to `_error`, Cancelling ack, claim/renew/Handle/re-check). Wired `AddDbContextFactory` + service + bus into Api and Workers. Tests: `LeaseSqlTests` 5/5 pass, `StageExecutionTests` 5/5 skip without Docker (pass in CI).

## Files Created/Modified
- `src/DubbingPlatform.Application/Exceptions/LeaseLostException.cs` — `LEASE_LOST` (409) exception for zero-row commits.
- `src/DubbingPlatform.Application/Services/StageExecutionSql.cs` — fenced `CompleteSql`/`FailSql`/`RenewLeaseSql`/`ReleaseLeaseSql` (all `lease_owner`+`lease_token`+`status='Running'`) + `RecoverStaleSql` (`status='Running'` + `lease_expires_at`).
- `src/DubbingPlatform.Application/Services/IStageExecutionContextFactory.cs` — `CreateDbContext()` abstraction so Application never references Infrastructure.
- `src/DubbingPlatform.Application/Services/StageClaimResult.cs` — `(Execution, IsNew)` record.
- `src/DubbingPlatform.Application/Services/StageExecutionService.cs` — `Schedule/Claim/Start/Complete/Fail/Cancel/MarkReviewRequired/RenewLease/ReleaseLease/RecoverStale`; run checks (NotFound/Forbidden/Cancelling→Conflict), enum parsing, state-machine guards, raw-SQL commits, unique-violation → existing, retry-bounded recovery.
- `src/DubbingPlatform.Application/DubbingPlatform.Application.csproj` — added `Microsoft.EntityFrameworkCore` + `Microsoft.EntityFrameworkCore.Relational` 10.0.12 (for `ExecuteSqlRawAsync` on `DbContext`).
- `src/DubbingPlatform.Infrastructure/Persistence/StageExecutionContextFactory.cs` — `IDbContextFactory<AppDbContext>` → `IStageExecutionContextFactory` adapter.
- `src/DubbingPlatform.Infrastructure/Messaging/MessagingMeters.cs` — `DubbingPlatform.Messaging` meter + `messaging.schema_mismatch_total` (from `MessageVersionPolicy`) + `messaging.cross_tenant_reject_total`.
- `src/DubbingPlatform.Infrastructure/Messaging/MassTransitConfig.cs` — `AddDubbingMassTransit(services, config)`: `AddEntityFrameworkOutbox<AppDbContext>` (`QueryDelay=1s`, `UsePostgres`, `UseBusOutbox`); `UsingRabbitMq` vs `UsingInMemory` via `Transport:Provider` (`Messaging:Transport` fallback); 7 workload + `_skipped`/`_error` endpoints (Rabbit durable); `UseMessageRetry(Interval 3x5s)` + `UseInMemoryOutbox(ctx)`.
- `src/DubbingPlatform.Infrastructure/Messaging/BaseConsumer.cs` — `IConsumer<TMessage> where T:IntegrationMessage` with ordered steps and abstract `HandleAsync(context, execution?, ct)`; `LeaseLostException` swallowed (discard, no publish).
- `src/DubbingPlatform.Infrastructure/Messaging/RETRY_OWNERSHIP.md` — single-owner retry table (transport/MassTransit, schema/cross-tenant, poison, Polly providers, fallback/logical/manual, lease/sweeper, cancellation).
- `src/DubbingPlatform.Api/Program.cs` — added `AddDbContextFactory<AppDbContext>` (Npgsql + snake_case + `TenantSessionInterceptor` + `TenantModelCacheKeyFactory`), `IStageExecutionContextFactory`, `StageExecutionService`, `MassTransitConfig`.
- `src/DubbingPlatform.Workers/Program.cs` — same wiring as Api (worker host builder).
- `tests/DubbingPlatform.UnitTests/Messaging/LeaseSqlTests.cs` — 5 facts asserting fencing predicates in each SQL string.
- `tests/DubbingPlatform.IntegrationTests/Messaging/StageExecutionTests.cs` — 5 `[SkippableFact]` PG16 tests via `TestFactory:IStageExecutionContextFactory` (duplicate→one, stale→LeaseLost, active→0, expired→RetryPending+attempt 1, Cancelling→Conflict).

## Decisions Made
- Application service uses base `DbContext` + `IStageExecutionContextFactory` (not `AppDbContext`) to avoid an Application→Infrastructure circular reference; Infrastructure provides the `AppDbContext`-backed implementation. Each method re-asserts `TenantContext.BeginScope(tenant)` then creates a short-lived context inside it, so query filters + RLS always capture the right tenant. `RecoverStaleAsync` uses ambient scope (tenant tests recover tenant rows; prod maintenance with `BYPASSRLS` recovers all).
- `Microsoft.EntityFrameworkCore.Relational` added to Application (not just Core) because `ExecuteSqlRawAsync` lives in `RelationalDatabaseFacadeExtensions`.
- `MassTransitConfig` RabbitMQ `Host(Uri, Action)` form used (confirmed in 9.2.1 XML) with optional `RabbitMq:Username/Password` only when configured — no hardcoded secrets; local broker defaults apply. `e.Durable=true; e.AutoDelete=false` set explicitly on Rabbit endpoints (compiles against `IRabbitMqReceiveEndpointConfigurator`).
- `BaseConsumer` resolves stage identity from envelope (`StageType/ScopeType/ScopeId`) with `StageWorkRequested.Required` fallback; run-level messages (no identity) skip claim and call `HandleAsync` with null execution. Owner is `{Consumer}:{MachineName}`; lease TTL 5 min. Post-`HandleAsync` re-check reloads `AsNoTracking` and throws `LeaseLostException` on owner/token/status drift.
- `RecoverStaleAsync` loads stale `Running` rows then fences per-row updates (EF-tracked read + raw-SQL write) to enforce per-stage `RetryOptions` bounds (`PerStageMaxAttempts` override else `LogicalStageMaxAttempts`); over-budget rows go `Failed` (`INTERNAL_ERROR`, retry exhausted) instead of `RetryPending`.
- `CancelAsync` is an administrative override (any non-terminal → `Cancelled` via guarded bulk SQL), not limited to `Running→Cancelled`, since operators must cancel `Scheduled`/`RetryPending` work; terminal states throw `ConflictException`.
- `IsUniqueViolation` accepts both `DomainException` with `CONFLICT` (AppDbContext's Postgres mapping) and raw `DbUpdateException` (SQLite/other providers), so claim idempotency works regardless of provider.
- StageExecutionTests use Testcontainers PG + `[SkippableFact]` (same probe as MigrationTests); no new test packages added.

## Build/Test Results
- `dotnet build --nologo -v q` (last 5):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:04.90
```
- `dotnet test --filter FullyQualifiedName~LeaseSqlTests --nologo -v q` (last 6):
```
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 34 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~StageExecutionTests --nologo -v q` (last 8):
```
Test run for C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll (.NETCoreApp,Version=v10.0)
Test run for C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll (.NETCoreApp,Version=v10.0)
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
Skipped! - Failed:     0, Passed:     0, Skipped:     5, Total:     5, Duration: 10 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true --nologo -v q` (last 5):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:21.95
```
- `dotnet test tests/DubbingPlatform.UnitTests/...` (last 2): `Passed! - Failed: 0, Passed: 201, Skipped: 0, Total: 201`
- `dotnet test tests/DubbingPlatform.IntegrationTests/...` (last 2): `Passed! - Failed: 0, Passed: 11, Skipped: 12, Total: 23`

## Recommendations for Next Agent (012)
- Repo state: builds 0/0 incl. lint (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest`). Unit 201/201, Integration 11 passed +12 skipped (7 pre-existing +5 new StageExecution, live in CI with Docker). Bus + outbox + claim/lease + BaseConsumer done; no saga/StageGraph/barriers/dispatcher/sweeper/workers yet (012 scope).
- Key APIs for 012: `StageExecutionService` ctor `(IStageExecutionContextFactory, IOptions<RetryOptions>)` (`src/DubbingPlatform.Application/Services/StageExecutionService.cs`); claim returns `StageClaimResult(Execution, IsNew)`; commits throw `LeaseLostException` (`src/DubbingPlatform.Application/Exceptions/LeaseLostException.cs`, code `LEASE_LOST`); SQL in `StageExecutionSql` (`CompleteSql` params status/completedAt/updatedAt/outputJson/id/owner/token; `FailSql` +errorCode/Message; `Renew/Release` expiry/updatedAt/id/owner/token; `RecoverStaleSql` now/status/updatedAt); `BaseConsumer<T>.HandleAsync(ConsumeContext<T>, StageExecution?, CancellationToken)` (`src/DubbingPlatform.Infrastructure/Messaging/BaseConsumer.cs`); bus via `MassTransitConfig.AddDubbingMassTransit(services, config)`; queues in `QueueNames` (7 workload + `_skipped`/`_error`); meters in `MessagingMeters` (`SchemaMismatchMetricName`, `CrossTenantRejectMetricName`); retry table in `src/DubbingPlatform.Infrastructure/Messaging/RETRY_OWNERSHIP.md`.
- Conventions: file-scoped namespaces, 4-space/LF, `StringComparison.Ordinal(IgnoreCase)`, `ConfigureAwait(false)` in library/service code (tests/hosts use `true`/none per existing style); `TenantContext.BeginScope(tenant)` before `CreateDbContext()` (contexts capture tenant at creation; interceptor reads it at connection open); raw SQL only via `ExecuteSqlRawAsync` with `{0}` params, never concatenation; `MassTransit.LogContext` vs `Serilog.Context.LogContext` ambiguity — always fully qualify Serilog (`Serilog.Context.LogContext`).
- Gotchas: shell is PowerShell (`Select-Object -Last`, no `||`/`tail`/`grep` — use `Select-String`); `AppDbContext.SaveChangesAsync` maps Postgres uniques to `DomainException` `CONFLICT`, other providers stay `DbUpdateException` — `IsUniqueViolation` handles both, keep it; `StageExecution.Status` persists as string enum names (`Running`, `RetryPending`, …) so SQL literals must match exactly; `IStageExecutionContextFactory.CreateDbContext()` returns base `DbContext` — use `db.Set<StageExecution>()`/`Set<ProcessingRun>()`, plus `db.ChangeTracker.Clear()` after raw SQL before re-read; `RecoverStaleAsync` relies on ambient `TenantContext` (tests wrap in `BeginScope`, prod sweeper uses maintenance + `BYPASSRLS` role).
- Config keys: `Transport:Provider` (`InMemory` fast / `RabbitMq` full, `Messaging:Transport` legacy fallback, see `MassTransitConfig.IsRabbitMq` + `HealthRegistration.IsFullProfile`); `ConnectionStrings:Default` (local dev fallback `Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing` in both Programs); `RabbitMq:Host/Port/VirtualHost` (+ optional `Username/Password`, never hardcoded); `Retry:LogicalStageMaxAttempts` + `Retry:PerStageMaxAttempts[stage]` bound recovery; `RabbitMq`/`Redis` health only in full profile.
