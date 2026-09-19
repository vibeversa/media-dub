# Task 7 — Initial Migration and Constraints

## Goal

Create and verify the initial EF Core migration covering the entire schema so a clean PostgreSQL 16 database can be created with all tables, indexes, and constraints.

## Context

Binding: PostgreSQL 16; EF Core transactional outbox/inbox enabled; snake_case; uuid IDs; TenantId everywhere; partial unique one active run per project; unique (TenantId,Endpoint,IdempotencyKey), (TenantId,ContentHash), (Run,Stage,Scope,ScopeId,Attempt), (Run,Stage,Scope,ScopeId,ExecutionId), (UploadSession,PartNumber); composite/workload indexes; lease partial index; JSON artifacts carry schema version. Prior task defined AppDbContext + configurations + RLS SQL + state machines + hash calculators.

## Starting State

AppDbContext with all entity configurations exists and builds. No `Migrations/` folder yet. Infrastructure has dotnet-ef tool manifest. PostgreSQL 16 reachable via Testcontainers or `docker compose --profile fast up postgres`.

## Scope

Must implement: initial migration, RLS application, verification tests. Must not implement: messaging runtime, storage, providers, API logic.

## Instructions

1. Startup project: `src/DubbingPlatform.Api`. Target context: `DubbingPlatform.Infrastructure.Persistence.AppDbContext`.
2. Run:
   ```bash
   dotnet ef migrations add InitialCreate --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext --output-dir Persistence/Migrations
   dotnet ef database update --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext
   ```
   Connection string env: `ConnectionStrings__Default="Host=localhost;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing"`.
3. Verify migration contains: tables `tenants,dubbing_projects,processing_runs,media_assets,upload_sessions,upload_parts,speakers,speaker_voice_assignments,voice_profiles,consent_records,speech_segments,overlap_groups,segment_overlaps,context_windows,segment_context_assignments,transcript_versions,translation_versions,generated_audio_artifacts,sync_results,stage_executions,run_stage_summaries,stage_unit_completions,content_objects,artifacts,artifact_parents,stage_input_artifacts,stage_output_artifacts,provider_executions,provider_capability_descriptors,provider_route_snapshots,prompt_templates,prompt_template_versions,quality_results,review_items,review_decisions,output_assets,export_jobs,export_artifacts,audit_events,idempotency_records,cost_reservations,quota_usages,processing_policies,retention_holds,deletion_jobs` plus `outbox_state,outbox_message,inbox_state`. All columns snake_case, Ids uuid, JSON columns jsonb.
4. Apply RLS: execute `Sql/rls_policies.sql` in migration `Up()` via `migrationBuilder.Sql(File.ReadAllText(...))` or separate migration `AddRlsPolicies`. Decision documented: RLS enabled in same initial migration after table creation.
5. Rollback expectation: `dotnet ef database update 0` drops all; document in `MIGRATION_NOTES.md` (create at `src/DubbingPlatform.Infrastructure/Persistence/MIGRATION_NOTES.md` with commands + rollback note + expand/contract rule: additive only, no destructive renames without 2-step).
6. Add schema-version metadata: `Artifact.SchemaVersion` and message `SchemaVersion` default `1`; document in notes.

## Requirements

- R1: `InitialCreate` migration applies cleanly on empty PG16.
- R2: All tables/indexes/unique constraints from Task 6 present.
- R3: Snake_case verified (no camelCase table/column).
- R4: RLS policies created.
- R5: Rollback to 0 succeeds.

## Edge Cases and Error Handling

- Existing DB with tables: migration must fail with clear error, not partial apply; require empty DB or explicit update.
- Concurrent migrate: EF history table serializes; second runner exits 0 (already applied).
- Missing connection string: fail fast with message naming `ConnectionStrings__Default`.
- RLS blocking maintenance: maintenance connection uses `SET ROLE maintenance` bypass; document.

## Security and Safety Requirements

- No secrets in migration files. Connection strings via env only. RLS deny-by-default.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Persistence/MigrationTests.cs` (Testcontainers.PostgreSql):
- `Migration_Applies_Cleanly` — apply migration, assert all expected tables exist via information_schema.
- `Snake_Case_Names_Verified` — no uppercase in table/column names.
- `Unique_Constraints_Verified` — insert duplicate (TenantId,ContentHash) fails; duplicate active runs fails; duplicate (UploadSession,PartNumber) fails.
- `One_Active_Run_Per_Project` — second Pending run for same project throws DbUpdateException; Completed run allows new Pending.

## Validation

```bash
dotnet ef database update --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api --context AppDbContext
dotnet test --filter FullyQualifiedName~MigrationTests
dotnet ef migrations list --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api
```

## Completion Criteria

- Migration applies, all tables/constraints verified, tests pass, MIGRATION_NOTES.md exists.

## Traceability

- Plan Section 2 actions 21–23, 19–20; Assumptions 5,9–11; Integration checklist PostgreSQL migrated; Tests checklist migrations.
