# 012A — Output and Export API

## Status
COMPLETED

## Summary
Completed the 012A split (output/export half of superseded 012). Changed export idempotency retention 24h→7d per spec, added explicit `generationState` alias to the output aggregate (top-level + per-asset + QC, R1), and wired export completed/failed projection to the Task 002 `NotificationProjector`/`ActivityProjector` best-effort in `ExportWorker` (creation stays audit + bus by design). Extended OpenAPI schemas/descriptions and added hermetic + Docker-gated coverage for retention, generationState, and export event mappers.

## Files Created/Modified
- `src/DubbingPlatform.Application/Services/IdempotencyRetention.cs` (modified) — `Export` 24h→7d (`TimeSpan.FromDays(7)`), class doc updated; `ExpiryFor(.../export...)` now returns 7d.
- `src/DubbingPlatform.Application/Output/OutputDtos.cs` (modified) — `OutputResponse{+GenerationState}`, `OutputAssetEntryDto{+GenerationState}`, `OutputQcDto{+GenerationState}` as explicit alias of `State` per R1.
- `src/DubbingPlatform.Application/Output/OutputService.cs` (modified) — populates `GenerationState` on all paths (Unavailable/Failed/Generating/Partial/Ready, Entry/Speakers/Qc, degraded fallback).
- `src/DubbingPlatform.Workers/Consumers/ExportWorker.cs` (modified) — ctor gains `NotificationProjector` + `ActivityProjector`; new `ProjectExportAsync` best-effort projects `FromExport` + `FromExportCompleted` on success and permanent failure (ids only, never payloads/secrets).
- `src/DubbingPlatform.Api/Controllers/ExportsController.cs` (modified) — doc 24h→7d.
- `src/DubbingPlatform.Api/Controllers/OutputController.cs` (modified) — doc mentions `generationState` alias.
- `src/DubbingPlatform.Api/OpenApi/OpenApiConfiguration.cs` (modified) — `OutputResponse`/`ExportResponse` schema descriptions mention `generationState` and 7d idempotency.
- `tests/DubbingPlatform.UnitTests/Output/OutputExportNotificationsContractTests.cs` (modified) — 4 prior + 3 new hermetic tests (7d retention, generationState alias, export completed/failed mappers).
- `tests/DubbingPlatform.IntegrationTests/Exports/OutputExportApiTests.cs` (modified) — +1 Docker-gated test `Output_GenerationState_Matches_State` (top + per-asset + QC alias, completeness 1/2).

## Decisions Made
- **7d wins over 24h:** combined-012 report kept 24h via `IdempotencyRetention`, but 012A Instruction 2 explicitly says 7d. Changed `Export` to 7d; no other retention touched. Documented in controller/OpenAPI.
- **generationState as alias, not replacement:** `State` stays canonical (existing 10 Docker tests + 012 combined assert `state`); `GenerationState` duplicates it everywhere to satisfy R1 wording without breaking wire compat. JSON adds `generationState` alongside `state`.
- **Created is audit+bus, completed/failed project:** `ActivityType` has no `ExportCreated`, so creation stays `export.create` audit (actor + idempotency key already) + best-effort `ExportJobRequested` bus publish; worker projects completed/failed to both notification and activity best-effort (projection failure never fails export, never logs URLs/text/secrets). Matches available mappers `FromExport`/`FromExportCompleted`.
- **Project 403 / sub-id 404 split preserved:** 012A security says cross-tenant→404; established B convention (007/009/010/011) is projectId→403, sub-id→404. `OutputService.GetAsync` already 404s projects; `ExportsController.RequireProjectAsync` keeps 403 for project, `GetOwnedAsync` maps export-id Forbidden→404. Documented to avoid breaking prior tests.
- **No new error codes or migration:** catalog stays 65 (`EXPORT_INCOMPLETE`/`OUTPUT_INCOMPLETE`/`URL_EXPIRED` reused); output derived from runs/segments/artifacts/QC, no schema change.

## Build/Test Results
- `dotnet build` — `Build succeeded. 0 Warning(s) 0 Error(s) Time Elapsed 00:00:11.81` (TreatWarningsAsErrors on; second build after test edits).
- `dotnet test tests/DubbingPlatform.UnitTests` — `Passed! - Failed: 0, Passed: 407, Skipped: 0, Total: 407` (404 prior + 3 new).
- `dotnet test --filter FullyQualifiedName~OutputExportApiTests` — `Skipped! - Failed: 0, Passed: 0, Skipped: 11, Total: 11, Duration: 10 s` (10 prior + 1 new; no Docker locally, CI runs live).
- `dotnet test tests/DubbingPlatform.ContractTests --filter FullyQualifiedName~MessageContractTests` — `Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4`.
- First `dotnet build` after impl — `Build succeeded. 0 Warning(s) 0 Error(s) Time Elapsed 00:01:40.00`.

## Recommendations for Next Agent (012B)
- **State:** 001–011 plus combined-012 in tree (uncommitted); 012A delta in tree (uncommitted). Counts pinned: `ErrorCodes.All`=65, `PublicIdMapper.AllPrefixes`=22, permissions=12, `WorkspaceService.QueryCeiling`=12, Contracts=20. No Docker locally; CI must run `OutputExportApiTests` (11) live.
- **Key APIs:** `IdempotencyRetention.Export`=7d, `ExpiryFor("POST .../exports")`=7d; `OutputResponse(State,GenerationState,Reason?,Completeness,ProgressApproximate?,ErrorCode?,Items,Warnings,UpdatedAt)`, `OutputAssetEntryDto(State,GenerationState,DownloadUrl?,Missing,Completeness?)`, `OutputQcDto(State,GenerationState,Summary,IssuesUrl?,Missing)`; `OutputService.GetAsync(tenant,project,ct)`; `ExportWorker(contextFactory,exports,notifications,activity,logger)` + `ProjectExportAsync(message,format,success,correlationId,ct)` (private, best-effort); `NotificationEventMapper.FromExport(tenant,project,export,format,success,sourceId)`, `ActivityEventMapper.FromExportCompleted(tenant,project,run?,export,format,success,corr,at)`.
- **Gotchas:** (1) Export `Create` still REQUIRES `Idempotency-Key` + `[ServiceFilter(IdempotencyFilter)]` (7d replay single row); do not remove. (2) Export download is 302 — tests must use `AllowAutoRedirect=false` and read `Headers.Location`. (3) `OutputService` swallows storage errors (null URL + fallback state) by design — do not throw. (4) Unit-test namespace trap: files under `DubbingPlatform.UnitTests.Output` need `global::` for `Domain.Enums`/`Domain.Exceptions`. (5) xUnit analyzers are errors: `Assert.Single(col,pred)`, never `.Where+Single`. (6) Never log signed URLs, `?access_token`, `RefreshToken`/`Salt`/`TokenHash`, transcript/translation bodies.
- **Incomplete integration points:** 012B owns notifications HTTP (do not touch output/export shape); 013 owns SSE `notification.created` (this worker projection is source of truth, SSE is hint); 014 consumes OpenAPI `generationState`/7d text; 025 workspace output panel (008 projection canonical); 033 exports UI (partial 96/100, `EXPORT_INCOMPLETE` offer, click-time signed-URL fetch, single refetch on 410).
- **Test helpers:** `OutputExportApiTests.CreateFactory(conn,fake)` (`Auth:*`, `Transport:InMemory`, `AddSingleton<IArtifactStorage>(fake)`), `FakeStorage` (`https://fake-storage.test/{key}?exp=`, records `LastExpiry` ≤15m), `SeedRunAsync(tenant,project,status,ready,total,withVersions)`, `SeedOutputArtifactsAsync`, `SeedCompletedExportAsync→exp_`, `SendExportAsync(project,format,profile?,allowPartial,key)`; hermetic `OutputExportNotificationsContractTests` for retention/alias/mappers without Docker.
- **Warnings:** `TreatWarningsAsErrors` on. No migration added. Contract suite flaky `Handles_Timeout(local)` — rerun before blaming new code. Worker DI: `NotificationProjector`+`ActivityProjector` already registered in both Api and Workers hosts; any new worker ctor deps must exist in both.
