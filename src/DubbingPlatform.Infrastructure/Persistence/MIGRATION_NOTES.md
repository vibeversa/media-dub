# Migration Notes — InitialCreate (Task 7)

## Commands

All commands run from the repository root. The EF Core CLI version is pinned in
`.config/dotnet-tools.json` (`dotnet-ef 10.0.12`).

```powershell
$env:ConnectionStrings__Default = "Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing"

# Create the initial migration (already created; command kept for reproducibility).
dotnet ef migrations add InitialCreate --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext --output-dir Persistence/Migrations

# Apply to an empty PostgreSQL 16 database.
dotnet ef database update --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext

# List applied/pending migrations.
dotnet ef migrations list --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext

# Offline SQL review (no database required).
dotnet ef migrations script --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext --idempotent --output migrations.sql
```

Local PostgreSQL via compose (fast profile) or Testcontainers `postgres:16-alpine`
is sufficient; production is PostgreSQL 16 managed.

## Connection string

- Only `ConnectionStrings__Default` is read, via environment variable only
  (`AppDbContextDesignTimeFactory.ConnectionStringEnvironmentVariable`).
- Missing or empty value fails fast:
  `Missing required connection string 'ConnectionStrings__Default'. ...`
- No secrets are stored in migration files, appsettings, or the snapshot.
  Use `ConnectionStrings__Postgres`-style env placeholders in deploy manifests,
  never real credentials.

## What InitialCreate contains

- 45 domain tables (snake_case, `uuid` primary keys with
  `gen_random_uuid()` defaults, JSON payload columns as `jsonb`):
  `tenants, dubbing_projects, processing_runs, media_assets, upload_sessions,
  upload_parts, speakers, speaker_voice_assignments, voice_profiles,
  consent_records, speech_segments, overlap_groups, segment_overlaps,
  context_windows, segment_context_assignments, transcript_versions,
  translation_versions, generated_audio_artifacts, sync_results,
  stage_executions, run_stage_summaries, stage_unit_completions,
  content_objects, artifacts, artifact_parents, stage_input_artifacts,
  stage_output_artifacts, provider_executions, provider_capability_descriptors,
  provider_route_snapshots, prompt_templates, prompt_template_versions,
  quality_results, review_items, review_decisions, output_assets, export_jobs,
  export_artifacts, audit_events, idempotency_records, cost_reservations,
  quota_usages, processing_policies, retention_holds, deletion_jobs`
  plus MassTransit transactional outbox/inbox tables
  `outbox_state, outbox_message, inbox_state`.
- Required constraints: partial unique one-active-run-per-project on
  `processing_runs (project_id) WHERE status IN
  ('Pending','Running','Cancelling','ManualReviewRequired')`; unique
  `(tenant_id, endpoint, idempotency_key)`, `(tenant_id, content_hash)`,
  `(processing_run_id, stage_type, scope_type, scope_id, attempt)`,
  `(processing_run_id, stage_type, scope_type, scope_id, stage_execution_id)`,
  `(upload_session_id, part_number)`; composite/workload indexes including
  `(processing_run_id, stage_type, status)` and
  `(processing_run_id, status, lease_expires_at)`; lease partial index
  `stage_executions (status) WHERE status = 'Running'` (the expiry comparison
  stays in the sweeper query because PostgreSQL rejects non-immutable
  `now()` in index predicates).
- RLS decision: row-level security is enabled in this same initial migration,
  after table/index creation, via embedded `migrationBuilder.Sql` (source of
  truth: `Persistence/Sql/rls_policies.sql`). Embedded rather than
  `File.ReadAllText` so the migration is self-contained at deploy time.
  44 tenant tables get `ENABLE ROW LEVEL SECURITY` plus policy
  `tenant_isolation USING (tenant_id =
  current_setting('app.tenant_id', true)::uuid)` (deny-by-default when the
  setting is missing). Excluded: `tenants` (no `tenant_id` column) and the
  outbox/inbox tables (internal transport state, no `tenant_id`).

## Rollback

```powershell
$env:ConnectionStrings__Default = "Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing"
dotnet ef database update 0 --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext
```

- `Down()` first drops all 44 `tenant_isolation` policies (`IF EXISTS`,
  idempotent), then drops all tables. `DROP TABLE` would remove policies
  anyway; the explicit drops keep rollback robust to ordering changes.
- Rollback to `0` removes the schema including `__EFMigrationsHistory`;
  re-running `database update` re-applies cleanly on the emptied database.
- Existing non-empty database: migration runs in a transaction and fails with
  a clear `already exists` error instead of partially applying; start from an
  empty database or resolve explicitly — never hand-edit applied migrations.
- Concurrent `migrate`: the `__EFMigrationsHistory` table serializes runners;
  the second runner observes the already-applied migration and exits 0.

## Expand/contract rule

- Migrations are additive only. No destructive renames, drops, or type
  changes without a two-step expand (add new, dual-write, backfill) followed
  by a separate contract (remove old) migration.
- Adding a table/column/index is a compatible expand. Changing semantics
  requires a new migration; never edit `InitialCreate` after it has applied
  anywhere shared.

## Schema-version metadata

- `artifacts.schema_version` defaults to `'1'`
  (`ArtifactConfiguration.HasDefaultValue("1")`; nullable for legacy rows,
  application sets `"1"` on create). JSON artifact payloads carry the same
  `"1"` schema version inside `metadata_json` where applicable.
- Messaging contracts (Task 10+) carry a `SchemaVersion` defaulting to `1`
  with the same additive-only evolution rule. Documented here so producers,
  consumers, and the outbox share one versioning policy from day one.

## RLS operations

- Application connections set `app.tenant_id` per connection open via
  `TenantSessionInterceptor` (parameterized `set_config`, reset when no tenant
  scope is active so pooled connections fail closed).
- Maintenance (migrations, reconciliation, retention) uses a dedicated
  `maintenance` role with `BYPASSRLS` (`SET ROLE maintenance`); never grant
  bypass to the application role. App-level query filters remain as defense in
  depth; RLS is the primary enforcement.
