# Zero-Downtime Migrations and Compat Window

## Expand/contract rule (frozen)

From `src/DubbingPlatform.Infrastructure/Persistence/MIGRATION_NOTES.md`
and `deploy/README.md` rollback notes — migrations are **additive only**:

1. **Expand release N**: the migration adds only nullable columns, new
   tables, or new indexes. Application code tolerates both shapes for one
   full release (reads use the old columns; writes populate both where
   dual-written; new columns are nullable so old writers keep working).
2. **Contract release N+1 (or later)**: only after release N is running
   everywhere shared does a separate migration drop the old
   columns/tables. Never edit an applied migration; never roll back a
   migration that already applied anywhere shared — forward-fix instead.

Changing semantics always means a new migration, never an edit to
`InitialCreate` (or any applied migration). Concurrent migrators
serialize on `__EFMigrationsHistory`; the second runner exits 0.

## Compat window mechanics

- The API `wait-for-migrations` initContainer (`/app/efbundle`, same
  bundle as the migration Job) blocks pod start on a broken schema, so a
  bad expand never serves traffic.
- Deployments roll with `maxUnavailable: 0`, and the API + control PDBs
  (`minAvailable: 1`) keep serving during the rollout — old and new code
  briefly coexist, which is exactly the window the additive rule makes
  safe.
- Rollback is code-only: `kubectl rollout undo deploy/<name>`; the schema
  stays at the expanded shape because old code tolerates the new nullable
  columns.

## Verification

- Command: `dotnet test --filter FullyQualifiedName~MigrationCompatTests`.
- Result on 2026-09-17: **PASS** — Failed: 0, Passed: 1, Skipped: 1,
  Total: 2 (`DubbingPlatform.IntegrationTests.dll`, ~10 s).
  - `Migration_Files_Present` passed (migration files + model snapshot
    found; at least one migration plus snapshot required).
  - `Additive_Schema_Compat` skipped (needs Docker PostgreSQL; live in
    CI): applies the full migration chain, asserts the six core tables
    every old reader depends on (`tenants`, `dubbing_projects`,
    `processing_runs`, `stage_executions`, `artifacts`,
    `content_objects`) exist, and asserts the 16 pinned
    `stage_executions` columns (ids, stage/scope identity, attempt,
    status, `lease_owner`/`lease_token`/`lease_expires_at`, hashes,
    timestamps) keep their names so old lease/state-machine SQL keeps
    working after additive changes.
- Overall: **PASS** (R5 compat tier green; additive-only rule documented
  and enforced by the compat test pins).

## 2026-09-19 — Task 045 local re-verification (full stack up)

- Command: `dotnet test --filter FullyQualifiedName~MigrationCompatTests`.
- Result: **PASS** — Failed: 0, Passed: 2, Skipped: 0, Total: 2
  (`DubbingPlatform.IntegrationTests.dll`, ~43 s).
  - `Migration_Files_Present` passed.
  - `Additive_Schema_Compat` passed live (Docker available): full
    migration chain applied, six core tables present, 16 pinned
    `stage_executions` columns intact.
- Overall: **PASS**.
