# Task 001 — Identity, Preferences, Project Metadata, Membership

## Goal
Add TenantUser, UserPreference, DubbingProject extensions, and ProjectMembership with migrations and RLS.

## Context
Plan B needs user resolution for `/me`, persisted UI preferences, named/archivable projects with versioned processing settings, and project-level roles for authorization, notification recipients, and activity attribution. Plan A remains authoritative; this task only adds the smallest required product entities.

## Starting State
Plan A backend implemented: modular monolith API, EF Core + PostgreSQL 16, `DubbingProject` without product metadata, no `TenantUser`/`UserPreference`/`ProjectMembership`. Assumes `src/DubbingPlatform.Domain`, `Infrastructure`, `Api` exist.

## Scope
Included: entities, EF configurations, expand/contract migration, RLS policies, indexes, domain validation for settings JSON schemaVersion.
Excluded: endpoints (Tasks 006–007), notifications/activity (Task 002), selection concurrency (Task 003), frontend.

## Instructions
1. Create `src/DubbingPlatform.Domain/Entities/TenantUser.cs`: `Id` (Guid, public `usr_` prefix mapped in API), `TenantId`, `ExternalSubject`, `Email`, `DisplayName`, `Status` (Active/Disabled), `CreatedAt`, `UpdatedAt`. Unique `(TenantId, ExternalSubject)`.
2. Create `src/DubbingPlatform.Domain/Entities/UserPreference.cs`: `TenantId`, `UserId`, `Key`, `ValueJson`, `UpdatedAt`; PK `(TenantId, UserId, Key)`. Whitelist keys: `locale, timezone, theme, defaultProjectFilters, timelineZoom, notificationPreferences`; reject others with validation error; never store secrets.
3. Extend `src/DubbingPlatform.Domain/Entities/DubbingProject.cs` with `Name` (required, max 200), `Description` (max 2000, nullable), `OwnerUserId`, `CreatedByUserId`, `UpdatedByUserId`, `IsArchived`, `ArchivedAt`, `SettingsVersion`, `ProcessingSettingsJson` (versioned: `schemaVersion, sourceSeparationPolicy, outputProfile, timingStrictness, voicePolicy, reviewThreshold, glossary[{sourceTerm,targetTerm,notes}], styleInstructions`). Target language stays immutable (enforce in Task 007).
4. Create `src/DubbingPlatform.Domain/Entities/ProjectMembership.cs`: `Id` (`mbr_`), `TenantId`, `ProjectId`, `UserId`, `Role` (ProjectOwner/ProjectEditor/Reviewer/ProjectViewer), `GrantedByUserId`, `CreatedAt`. Unique `(ProjectId, UserId)`.
5. Add EF configs in `src/DubbingPlatform.Infrastructure/Persistence/Configurations/`: `TenantUserConfiguration.cs`, `UserPreferenceConfiguration.cs`, `ProjectMembershipConfiguration.cs`, extend `DubbingProjectConfiguration.cs`. snake_case naming, tenant indexes, RLS enabled (`ALTER TABLE ... ENABLE ROW LEVEL SECURITY`, tenant policy on `tenant_id`).
6. Add migration `dotnet ef migrations add AddProductIdentityExtensions --project src/DubbingPlatform.Infrastructure`; verify expand/contract: only additive nullable columns / new tables.
7. Add `ProjectProcessingSettingsValidator.cs` (FluentValidation) for settings JSON schemaVersion=1.

## Requirements
- R1: Unique `(TenantId, ExternalSubject)` enforced at DB.
- R2: Preference PK `(TenantId, UserId, Key)`; unknown keys rejected.
- R3: `IsArchived` does not alter processing semantics.
- R4: Settings changes allowed only with no conflicting active run (guard lives in Task 007; entity carries `SettingsVersion` + hash input).
- R5: RLS enabled on all three new tables; tenant indexes exist.
- R6: Migration applies cleanly on Plan A database (expand/contract compatible).

## Edge Cases and Error Handling
- Duplicate membership → 409 unique violation mapped to structured error.
- Unknown preference key → 400 validation error.
- Overlong name/description → 400, not truncation.
- Settings JSON with unknown schemaVersion → 400 with code `SETTINGS_VERSION_UNSUPPORTED`.

## Security and Safety Requirements
- All queries tenant-scoped; no cross-tenant user enumeration.
- No secrets in preferences; `ValueJson` size-capped (4KB) and logged only as key names.
- Archival/ownership changes audited (hook into existing AuditEvent).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Identity/IdentityPreferencesTests.cs`: unique subject, preference round-trip + unknown-key rejection, membership uniqueness, RLS blocks cross-tenant read, archival flag persistence.
- Type: integration (Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet ef migrations script --project src/DubbingPlatform.Infrastructure --no-build | Select-Object -First 50
dotnet test --filter FullyQualifiedName~IdentityPreferencesTests
```

## Completion Criteria
- Entities, configs, migration, RLS, and tests exist; `IdentityPreferencesTests` pass; migration script shows additive-only changes.

## Traceability
- Plan B §8.1, §8.2, §8.9, §20; API consumers §9.1–§9.2.
