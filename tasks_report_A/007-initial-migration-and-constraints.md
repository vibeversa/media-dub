# 007 — Initial Migration and Constraints Report

## Status
COMPLETED

## Summary
Created the `InitialCreate` migration (`20260911061428_InitialCreate`) covering all 48 tables (45 domain + `outbox_state/outbox_message/inbox_state`) with snake_case columns, `uuid` keys, `jsonb` payloads, all required unique/partial/composite indexes, and RLS (`tenant_isolation` on 44 tenant tables) embedded in the same migration's `Up()` after table creation. Added the design-time factory (fail-fast on missing `ConnectionStrings__Default`), `MIGRATION_NOTES.md` (commands, rollback to 0, expand/contract rule, schema-version defaults, maintenance bypass), and `MigrationTests.cs` (5 skippable Testcontainers tests). Offline SQL generation verified (49 `CREATE TABLE`, 44 policies); live apply is exercised by the tests wherever Docker/PG16 exists.

## Files Created/Modified
- `src/DubbingPlatform.Infrastructure/Persistence/Migrations/20260911061428_InitialCreate.cs` — generated `InitialCreate` plus hand-added RLS block: single `migrationBuilder.Sql` with 44× `ENABLE ROW LEVEL SECURITY` + 44× `CREATE POLICY tenant_isolation ...` in `Up()` (after indexes), and 44× `DROP POLICY IF EXISTS` in `Down()` before drops.
- `src/DubbingPlatform.Infrastructure/Persistence/Migrations/20260911061428_InitialCreate.Designer.cs` — EF-generated model snapshot (unmodified).
- `src/DubbingPlatform.Infrastructure/Persistence/Migrations/AppDbContextModelSnapshot.cs` — EF-generated current model snapshot (unmodified).
- `src/DubbingPlatform.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs` — `IDesignTimeDbContextFactory<AppDbContext>` reading only `ConnectionStrings__Default` env var; throws `InvalidOperationException` naming it when missing; snake_case, no interceptor (schema work is maintenance context).
- `src/DubbingPlatform.Infrastructure/Persistence/MIGRATION_NOTES.md` — commands (add/update/list/idempotent script), rollback-to-0 note, expand-only + 2-step contract rule, `artifacts.schema_version` default `'1'` + message `SchemaVersion=1` policy, RLS decision/operations (`SET ROLE maintenance` bypass), existing-DB/concurrent-migrate/missing-connection-string behaviors, no-secrets rule.
- `src/DubbingPlatform.Infrastructure/Persistence/Configurations/ArtifactConfiguration.cs` — `SchemaVersion` now `.HasDefaultValue("1")` (migration shows `defaultValue: "1"`); only config change, domain untouched.
- `src/DubbingPlatform.Infrastructure/DubbingPlatform.Infrastructure.csproj` — added `Microsoft.EntityFrameworkCore.Design 10.0.12` (matches EF 10.0.12 pin; required for `dotnet ef`).
- `tests/DubbingPlatform.IntegrationTests/Persistence/MigrationTests.cs` — 5 `[SkippableFact]` tests on `postgres:16-alpine` via `Database.MigrateAsync()`: `Migration_Applies_Cleanly` (48 tables + RLS + policy count), `Snake_Case_Names_Verified`, `Unique_Constraints_Verified` (content-hash / active-run / part-number duplicates), `One_Active_Run_Per_Project` (second Pending fails, Completed allows new Pending via parameterized raw-SQL status flip), `Rls_Policies_Created` (44 RLS tables, 44 policies, `tenants`/outbox/inbox excluded).

## Decisions Made
- RLS in the same `InitialCreate` (per task decision), but embedded as literal `migrationBuilder.Sql("""...""")` rather than `File.ReadAllText(...)`: file I/O at migrate time is fragile (missing-file failure at deploy); the `.sql` file remains the reviewed source of truth and the migration header documents the embedding. Separate `AddRlsPolicies` migration was rejected for the same reason.
- `CREATE POLICY` without `IF NOT EXISTS` (PG16 has no such clause; migration runs once), `DROP POLICY IF EXISTS` in `Down()` for idempotent rollback; `DROP TABLE` would remove policies anyway, explicit drops guard reordering.
- `Artifact.SchemaVersion` DB default `'1'` only; domain stays nullable/optional and messaging contracts do not exist yet (Task 10), so the message-`SchemaVersion=1` half is documented as policy in `MIGRATION_NOTES.md`, not code — implementing contracts now would be over-implementation.
- Uniqueness tests accept `DomainException` (`CONFLICT` prefix from `AppDbContext.SaveChanges`) or inner `DbUpdateException`: the task text predates the 006 CONFLICT mapping, so `One_Active_Run_Per_Project` additionally asserts `IsType<DomainException>` to lock the actual behavior while still proving the DB constraint.
- Status flip to `Completed` uses parameterized `ExecuteSqlRawAsync("UPDATE ... WHERE id = {0}", id)` (fixed enum literal, bound Guid) because entities expose no status-transition method and the task forbids under-implementation; no string concatenation.
- No Docker on this host, so live-DB proof is via `migrations script` counts + skippable tests (SKIP, exit 0) that run fully in CI; `database update` without a server fails only at connection open (recorded below), not on migration content.

## Build/Test Results
- `dotnet build --nologo -v q` (last 4 lines):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.57
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true --nologo -v q` (last 4 lines):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:24.32
```
- `dotnet ef migrations list --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext` (last 3 lines):
```
20260911061428_InitialCreate
Pending status not shown. Unable to determine which migrations have been applied. This can happen when your project uses a version of Entity Framework Core lower than 5.0.0 or when an error occurs while accessing the database.
```
(list resolves offline; pending status needs a live DB — expected without PG.)
- `dotnet ef migrations script ... --idempotent`: generated OK; `CREATE TABLE` count `49` (48 tables + `__EFMigrationsHistory`), `CREATE POLICY|ENABLE ROW LEVEL SECURITY|CREATE TABLE` count `137` (49 + 44 + 44), `tenant_isolation` lines `44`.
- `dotnet ef database update ...` without a server (last 3 lines, connection-only failure, exit 1):
```
Failed to connect to 127.0.0.1:5432
```
(full stack: `NpgsqlException ... Failed to connect to 127.0.0.1:5432` → `SocketException (10061)` in `HistoryRepository.GetAppliedMigrations`; migration content itself builds and scripts cleanly.)
- `dotnet test --filter FullyQualifiedName~MigrationTests` (last 6 lines):
```
[xUnit.net 00:00:14.33]     DubbingPlatform.IntegrationTests.Persistence.MigrationTests.Rls_Policies_Created [SKIP]
[xUnit.net 00:00:14.36]     DubbingPlatform.IntegrationTests.Persistence.MigrationTests.Unique_Constraints_Verified [SKIP]
[xUnit.net 00:00:14.36]     DubbingPlatform.IntegrationTests.Persistence.MigrationTests.One_Active_Run_Per_Project [SKIP]
[xUnit.net 00:00:14.36]     DubbingPlatform.IntegrationTests.Persistence.MigrationTests.Migration_Applies_Cleanly [SKIP]
[xUnit.net 00:00:14.36]     DubbingPlatform.IntegrationTests.Persistence.MigrationTests.Snake_Case_Names_Verified [SKIP]
Skipped! - Failed:     0, Passed:     0, Skipped:     5, Total:     5, Duration: 11 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- Full suites: UnitTests `Passed: 127, Skipped: 0` (exit 0); IntegrationTests `Passed: 1, Skipped: 6` (5 new + `SchemaSmokeTests`, exit 0).

## Recommendations for Next Agent (008)
- Repo state: solution + lint build 0/0 (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest`). Migration `20260911061428_InitialCreate` exists with snapshot; do NOT re-scaffold or hand-edit it (expand/contract rule in `src/DubbingPlatform.Infrastructure/Persistence/MIGRATION_NOTES.md`). Next schema change = new `dotnet ef migrations add` with `$env:ConnectionStrings__Default` set.
- EF wiring: design-time factory is `AppDbContextDesignTimeFactory` (`src/DubbingPlatform.Infrastructure/Persistence/AppDbContextDesignTimeFactory.cs`, const `ConnectionStringEnvironmentVariable = "ConnectionStrings__Default"`). Runtime composition still has no `AddDbContext` (Api `Program.cs` is minimal) — 008/009 should register `AppDbContext` with `UseNpgsql(connStr).UseSnakeCaseNamingConvention().AddInterceptors(new TenantSessionInterceptor())` and optionally `.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()` when pooling across tenants. Design-time factory deliberately omits the interceptor.
- Key files: migration + RLS block `src/DubbingPlatform.Infrastructure/Persistence/Migrations/20260911061428_InitialCreate.cs` (`Up` ends with RLS `Sql`, `Down` starts with policy drops); RLS source `src/DubbingPlatform.Infrastructure/Persistence/Sql/rls_policies.sql` (44 tables; `tenants`/outbox/inbox excluded); notes `.../Persistence/MIGRATION_NOTES.md`; tests `tests/DubbingPlatform.IntegrationTests/Persistence/MigrationTests.cs` (helpers `CreateOptions`, `StartContainerAsync`, `AssertUniqueViolation`, `NewRun`, `QuerySingleColumnAsync`).
- Gotchas: `default.shell` runs PowerShell here (no `tail`; use `Select-Object`/`Select-String`). No `docker` on this host — keep `[SkippableFact]` + `Skip.If` (`Xunit.SkippableFact 1.3.12` already referenced; v2 ignores `Xunit.Sdk.SkipException.ForSkip`). Testcontainers needs `new PostgreSqlBuilder("postgres:16-alpine")` (parameterless ctor is obsolete-as-error). Tests use `MigrateAsync()` (not `EnsureCreated`) so RLS/constraints are exercised; `TenantContext.BeginMaintenanceScope()` for migrate/schema queries, `BeginScope(tenantId)` for inserts. `AppDbContext` converts PG 23505 to `DomainException` ("CONFLICT") — expect that, not bare `DbUpdateException`. Status has no domain mutator; tests flip via parameterized `ExecuteSqlRawAsync` with `{0}` placeholder.
- Warnings: keep `#pragma warning disable CA1031` only around the Docker-availability probe; `StringComparison.Ordinal` everywhere; `ConfigureAwait(true)` matches existing integration-test style; never concatenate SQL/shell values.
- Config: only `ConnectionStrings__Default` is honored (env var, placeholder `Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing`); appsettings still has 16 empty `{}` sections — 008 owns real options wiring. Never commit real credentials.
