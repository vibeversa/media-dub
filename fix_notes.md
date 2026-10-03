# fix_notes.md — gap batches (GAP-001..007,009 P0; GAP-010..020 second batch)

Decisions: no overrides. Plan A wins backend, Plan B wins frontend (per user confirmation).

## GAP-001 — SSE `?access_token=` removed (DONE, 61d0b32)
- Verified: `Program.cs:388-404` honored query token on `/stream`; `ProcessingController:47,633`, `OpenApiConfiguration:33`, bundle `openapi.v1.json` documented it.
- Fix: header-only bearer. Removed `OnMessageReceived` query handling; updated controller docs, OpenAPI description, bundle (removed `AccessToken` param + component), regenerated frontend client (`make generate-api` equivalent via `node tools/generate-client.mjs`, stamp `88a0ffab…`).
- Tests inverted (Rule 3): `OpenApiCoverageTests.Sse_Contract` now asserts `DoesNotContain access_token`; `WorkspaceProgressTests.Stream_Auth_And_Tenant_Checks` and `AdminSseErrorContractTests.Stream_Accepts_LastEventId` now assert query-token → 401 and header → 200.
- Build: `dotnet build` 0 warn/0 err; `IntegrationTests --filter OpenApiCoverageTests` 8 passed.

## GAP-002 — AuthOptions.Authority absolute http(s) (DONE, 5b3b2c6)
- Verified: `OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority("/relative/path")` failed (1 failed, 2 passed). Root cause: `Uri.TryCreate(..., Absolute)` accepts `/relative/path` as file URI.
- Fix: `AuthOptionsValidator` now requires absolute URI with `http`/`https` scheme.
- Tests: existing theory now green (14 AuthOptions tests passed); full `dotnet build` 0/0.

## GAP-003 — LeaseTokenVersion increment (DONE, fb62b73)
- Verified: `grep LeaseTokenVersion src` → entity + migrations only; `TakeoverSql` had no version bump.
- Test-first: added `Takeover_Sql_Increments_Lease_Token_Version` (failed) + `Renew_Sql_Does_Not_Rotate` (passed).
- Fix: `TakeoverSql` now `SET lease_token_version = lease_token_version + 1`; `RenewLeaseSql` unchanged. No migration (column exists).
- Tests: `LeaseSqlTests` 8 passed; build 0/0.

## GAP-004 — Lease-recovery covering index (DONE with rule-compliant deviation, 419e1ba)
- Suggested fix in missing_plan.md (`HasFilter("status='Running' AND lease_expires_at < NOW())`) REJECTED per Rule 4: Postgres index predicates must be immutable; `NOW()` would fail migration (`functions in index predicate must be marked IMMUTABLE`) and violate expand-only compatibility.
- Implemented instead: `HasIndex(e => new { Status, LeaseExpiresAt }).HasFilter("status = 'Running'")` — immutable predicate, covering `lease_expires_at` for index-only sweeper scans (`RecoverStaleAsync` queries `status='Running' AND lease_expires_at < @now`, time comparison in query, not index).
- Migration `20261002143017_AddStageExecutionStaleLeaseCoveringIndex` is expand-only additive (`CreateIndex` only, no drops, no `NOW()`).
- Test-first: `PersistenceModelTests.Stage_Executions_Have_Stale_Lease_Covering_Partial_Index` failed before, 12 passed after.
- No BLOCKED: gap is DONE via compliant alternative; original `NOW()` predicate documented here as rejected.

## GAP-005 — Storage quota joined to commit txn (DONE, 70c40db)
- Verified: `QuotaService.CheckStorageAsync` used separate `DbContext` (TOCTOU); `ArtifactService.CommitNewAsync` called gate outside txn.
- Fix: `ArtifactService` now takes optional `IOptions<QuotaOptions>` (backward compatible), acquires `pg_advisory_xact_lock(hashtext(tenant))` in `CommitNewAsync` txn (mirrors `CostService.ReserveAsync` per-project lock), SUMs committed bytes on the SAME `DbContext`, throws `429 QUOTA_EXCEEDED` with `quota.rejections{storage}` on over-admit. Falls back to `IQuotaGate` when options absent (tests/mocks).
- Wiring: `StorageRegistration` passes `IOptions<QuotaOptions>`.
- Tests: new `ArtifactQuotaTransactionTests` (3 passed: ctor shape, advisory-lock wiring, pure over-admit); existing `QuotaTests` pure checks unaffected. Build 0/0.

## GAP-006 — Sensitive/Voice/Retention enforcement (DONE, b49797b)
- Verified: `PolicyChecker.CanUseProvider` enforced external/allowed/residency/local only; sensitive/voice/retention hashed-not-blocked.
- Fix: restrictive values (`restricted, local-only, no-external, deny-external, private, confidential, no-clone, deny, block-external`, case-insensitive + substring `local-only/no-external/deny-external/block-external/restricted`) now block external providers. Permissive (`allow, default`) unchanged. `IsRestrictive` public for tests.
- Tests: `ResolverTests.Restrictive_Sensitive_Policy_Blocks_External_Route` (4 theories) + `Permissive_Policies_Preserve_External_Routing`; 11 ResolverTests passed. `PolicyDenied` recorded via existing `ProviderMeters.PolicyDenied` path in `ProviderResolver`.
- Backward compat: only restrictive strings newly block; existing `allow/default` tenants unaffected.

## GAP-007 — Server-side project filters (DONE, e509670)
- Verified: `ProjectsController.List` accepted `status/ownerId/search/archived` only; `targetLanguage/date` filtered in `applyClientFilters` in memory; `reviewRequired` absent.
- Fix backend: `ProjectListQuery` gains `TargetLanguage/CreatedFrom/CreatedTo/ReviewRequired` (optional, validated: 2-3 letters, YYYY-MM-DD, from<=to); `ProjectService.ListFilteredAsync` filters `TargetLanguage` (case-insensitive), `CreatedAt >= fromStart` / `< toExclusive`, `ReviewRequired==true → Status==ManualReviewRequired`; `ProjectsController.List` accepts `targetLanguage/from/to/reviewRequired`.
- Fix frontend: `ServerProjectQuery` + `toServerQuery` send `targetLanguage/from/to`; `applyClientFilters` is now identity (server-owned); `useProjectsQuery` key covers full server query; `ProjectsPage` comment updated.
- Tests inverted (Rule 3): backend `ProjectGuardHashTests.ListQuery_Validates_Gap007` (5 passed); frontend `urlSync` + `projectsMatrix2` updated to assert server query carries filters and client is identity (36 passed). `dotnet build` 0/0; `npm run typecheck/lint` clean.

## GAP-009 — MinDiskFreeBytes + memory fail-fast (DONE, 6a539dd)
- Verified: `AudioPreparationService` used `EnsureFree(2*Size)` ignoring `MinDiskFreeBytes`; zero memory refs.
- Fix: `required = max(2*Size, MinDiskFreeBytes)`; new `EnsureMemoryAvailable(required)` via `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` → `RESOURCE_EXHAUSTED` when available < required (probe failures skip, disk gate still applies); temp-dir creation/cleanup unchanged (temp volume covered by `EnsureFree(workDir, ...)`, `DeleteWorkDirQuietly` in `finally`, no partial commit per existing `Disk_Full_Fails_Fast` test).
- Tests: new `MediaResourceEnforcementTests` (3 passed); build 0/0.

## Blocked
None. GAP-004 original `NOW()` predicate rejected but gap closed via compliant alternative (see above); not counted as BLOCKED.

## Commits
- GAP-002 5b3b2c65b58134161cc09472c4db5c63a7ff14db
- GAP-001 61d0b323f326ea7bc169878461a10e4e5a5e4f28
- GAP-003 fb62b7385e2a77417b0e14f201ce702f760adac9
- GAP-004 419e1ba5a192cb8197c4fc8eb7e105892a653419
- GAP-005 70c40dbb43794281b6b0c5c2008235d0a32c7805
- GAP-006 b49797bd28cdb2a29ef4863a7594f89cfe72f0b4
- GAP-007 e5096708b5a28c85d48fb86acdb490df61bef25d
- GAP-009 6a539dd64c7ebc00b5f7acab9f5cf153f0046d05

# Second batch — GAP-010..GAP-020

Scope: the eleven P1/P2 gaps below. No decision-table overrides; Plan A wins
backend, Plan B wins frontend.

## GAP-010 — Public IDs stay API-only (DONE, 53c1883)
- Verified: `PublicIdMapper`/`PublicIdParser` exist; all 50 EF configurations persist raw `uuid` with `gen_random_uuid()`; `PublicIdConverter` exists but is referenced nowhere.
- Fix: the gap's acceptance criterion allows "or ADR records rejection". Added `docs/adr/ADR-010-public-ids-api-only.md`: applying prefixed-text converters to PK/FK would break `gen_random_uuid()`, uuid indexes/FKs/RLS over 50+ tables, and require a non-expand-only `uuid → text` rewrite (Rule 4/5 violation).
- Tests: `Persistence/PublicIdStorageTests` pins that no Id/FK property uses `PublicIdConverter` and round-trips `prj_…` (2 passed).

## GAP-011 — `ProviderExecution` audit columns (DONE, 7a69ae2)
- Verified: 30 properties, missing `UsageDimensionsJson|SafetySettingsHash|SystemInstructionHash|PromptTemplateVersion|OutputContentHash` (`grep` zero); recorder at `Application/Providers/ProviderExecutionRecorder.cs` (not the path the plan cited).
- Fix: added the 5 nullable columns (+ validation), EF config, expand-only migration `20261002171245_AddProviderExecutionAuditColumns` (AddColumn only), and population helpers `HashContent` (raw SHA-256 hex, same encoding as `PromptHash`/`RequestHash`) and `BuildUsageDimensionsJson` (canonical, secret-stripped, 128-char value cap, `usage.*` keys only). Wired into all 10 recorder call sites (Translation, Transcription, TTS, ContextBuild, Separation ×2, Vad, Diarization, VideoIntelligence, VoicePreview, Timing).
- Reconcile now compares `OutputContentHash` too, so a distinct output body no longer proxies through `ResponseHash`.
- Tests: `ProviderExecutionAuditColumnsTests` (11) + `PersistenceModelTests.Provider_Executions_Have_Audit_Columns_With_Expected_Types_And_Lengths`; old 28-arg constructor calls still compile (optional parameters, backward compatible).

## GAP-012 — Batch support is a compiler-checked contract (DONE, 7071bdd)
- Verified: `StartBatchAsync`/`GetBatchStatusAsync` existed only on Azure/OpenAI STT as duck-typed concrete methods; no interface, no caller; descriptor `AsyncJob` was hardcoded `false`.
- Fix (option "or plan scoping note", done both ways): `Infrastructure/Providers/IBatchTranscriptionProvider` (extends `ITranscriptionProvider`, returns `ProviderJobPoller.JobStatus`) implemented by Azure/OpenAI STT; `BatchProviderContract.ValidateAsyncJobFlag` + `ProviderStartupValidator.RequireHonestAsyncJobFlags` fail fast when `Providers:Descriptors[].AsyncJob=true` for a provider that does not implement it; `DescriptorStore` now surfaces `option.AsyncJob`; scoping note added to `Providers/README.md` (Google/Local are synchronous by contract).
- Tests: `BatchProviderContractTests` (12) incl. startup fail-fast.

## GAP-013 — `MediaValidationWorker` registered (DONE, ca3578a)
- Verified (and worse than reported): `ProcessingRunSaga.OnRunStarted` dispatches `StageWorkRequested(MediaValidation)` on `RunStarted`, but no consumer claimed it (the five `media.preparation` `BaseConsumer`s return `false` from `ShouldProcess`), so the message was acked as a no-op and the barrier never advanced — runs stalled before `MediaAnalysis`. The audit rated this "functionally wired"; on re-check it is a broken pump.
- Fix: `MediaValidationService` (adoption semantics: re-affirms the ingestion verdict via `SourceMediaLoader`, completes the run's own execution with the source + ingestion probe artifacts, fails permanently `ARTIFACT_UNAVAILABLE` when the media is gone) + `MediaValidationWorker : BaseConsumer<StageWorkRequested>` + DI (`MediaRegistration`) + registration in `Workers/Program.cs`.
- Collateral fix on the same path (needed for the stage to complete at all): `StageExecutionSql.CompleteSql` and the inline skip SQL bound `output_artifact_ids_json` (jsonb) as text → PG `42804`. Added `::jsonb` casts. Other raw-SQL jsonb writes (e.g. `UPDATE artifacts SET metadata_json = {0}` in `MediaAnalysisService`) have the same defect but are pre-existing and out of scope — recorded here for a follow-up.
- Tests: `StageConsumerRegistrationTests` (every `StageGraph` stage has a registered consumer; 17 == 17), `MediaValidationServiceTests` (2, PG-gated).

## GAP-014 — Artifact publish as-built (DONE, 30073ee)
- Verified: publish is upload-first + single-txn commit (no `Pending` rows), stage completion is a separate lease-fenced UPDATE, `ArtifactChild` does not exist.
- Fix: `docs/adr/ADR-014-artifact-publish-as-built.md` records (a) `ArtifactChild` = reverse query over `artifact_parents` (both directions already relational: `ArtifactService.GetParentsAsync`, `RetentionService.CountLiveReferencesAsync`), (b) upload-first instead of `Pending` reserve (a reserve would need a stale-Pending sweeper and would expose uncommitted bytes), (c) why the stage completion cannot join the artifact transaction (lease fencing), and (d) crash windows W1–W4 with redrive proof.
- Tests: `ArtifactPublishAsBuiltTests` (5) pins the as-built (no Pending in the publish path, single txn, lineage unique index, no `artifact_children`).

## GAP-015 — `Committed → Deleted` transition (DONE, 84daa7a)
- Verified: `RetentionService.SweepAsync` deletes dereferenced `Committed` content rows with raw SQL (bypassing the state machine), while the machine only allowed `Pending→Committed→Orphaned→Deleted`.
- Fix: added the direct transition to `ContentObjectStateMachine` with docs that `Orphaned` stays the zero-reference path and deletion still requires zero live refs + expired window + no hold.
- Tests: new `ContentObjectStateMachineTests` (12) + existing `StateMachineMatrixTests.ContentObject_Full_Matrix` extended (strengthened, not weakened).

## GAP-016 — Tenant-wide provider-call cap (DONE, 5ba9315)
- Verified: only `RateLimit:Concurrency` per (tenant, provider); no tenant-wide cap, no `MaxConcurrentProvider*` anywhere.
- Fix: `RateLimitOptions.TenantConcurrency` (default 50, validated `>= Concurrency`), new `tenantConcurrency` Redis dimension (`RateLimiter.NormalizeDimension`/`LimitForDimension`), `TenantFairnessGate.TenantConcurrencySegment = "_tenant"`, and `TryAcquireProviderCallAsync` now charges the tenant window first, then the per-provider window (fail-open as before; rejections reuse `ratelimit.rejections{provider,dimension}` so the existing dashboard groups them).
- Tests: `TenantProviderConcurrencyCapTests` (5) incl. "tenant A at cap blocks only A".

## GAP-017 — Measured crossfade for overlapping dialogue (DONE, 3c332b7)
- Verified: `BuildPremixFilter` emitted `adelay+apad+atrim` + `amix`; `acrossfade` nowhere; QC `QC_CROSSFADE` warned when the overlap window was silent.
- Fix: `PlanCrossfades` (pure, clamps overlap to the shorter entry) + a crossfade chain in `BuildPremixFilter` used only when overlaps exist: entries are chained in start order, each pair crossfaded over its measured window (`c1=tri:c2=tri`), silent gaps preserved by padding the accumulated stream before `concat`, then `apad/atrim` to the timeline length. Non-overlapping timelines keep the previous `apad`/`amix` graph byte-for-byte, so no existing mixing behaviour changed. The graph is recorded in `MixResult.PremixFilter`/`FilterComplex`.
- Tests: `FFmpegMixerCrossfadeTests` (7, hermetic: overlap planning, graph content, label balance, non-overlap unchanged) + `MixingTests.Overlapping_Dialogue_Crossfades_And_Stays_Audible` (ffmpeg-gated: mixes a 200ms overlap and asserts the window stays above −60 dBFS). The ffmpeg-gated test is skipped in this env (no ffmpeg); it runs in CI.

## GAP-018 — Media-bomb / disk-pressure proof (DONE, b08ba8d)
- Verified: `MediaBombTests` covered the validator + `DiskSpaceChecker` only; `scripts/generate-fixtures.sh` claimed "media-bomb negative controls" but generated none; no `PrepareAsync` exhaustion test.
- Fix: generated the two negative controls deterministically (`fixtures/zip-bomb.zip`: 64KiB of zeros declaring ~4GiB uncompressed; `fixtures/media-bomb-truncated.mp4`: first 8KiB of `valid-2s.mp4`), extended the script to recreate them (with the size gate), documented them in `fixtures/README.md`, and extracted `AudioPreparationService.RequiredScratchBytes` (pure) from the inline `max(2×Size, MinDiskFreeBytes)` so the scratch floor is directly testable.
- Tests: `MediaBombTests` extended to 11 (zip-bomb fixture rejected, undecodable audio codec, disallowed video codec, scratch floor semantics incl. `OverflowException`, memory exhaustion, disk pressure).

## GAP-019 — Retention hold proof (DONE, d484fb7)
- Verified: `RetentionHold`/`RetentionService`/`ContentObjectService` gates existed; zero tests referenced them (`grep RetentionHold|CanDelete|IsDeleteBlockedByHold tests` → one typeof assertion).
- Fix: tests only (no behaviour change) — `IntegrationTests/Storage/RetentionHoldTests` (5, PG-gated): active hold → `POLICY_DENIED` naming the hold id + project not soft-deleted + no deletion job; released hold → deletion completes and audits; sweeper skips a held `Deleted` artifact and deletes it after release; `IsDeleteBlockedByHoldAsync` tracks hold state and fails closed cross-tenant/unknown; `CanDeleteContentObjectAsync` requires dereference + expired window.
- Noted, not changed: `CanDeleteContentObjectAsync` cannot map a project-scoped hold to a fully dereferenced content object (documented in its comment). Closing that would need a content→project edge; out of scope for this gap.

## GAP-020 — Worker health endpoint (DONE, 74f45a5)
- Verified: `WorkerHealthService` + DI + DTO existed; `AdminController` injected only 4 of the 5 query services and no route, OpenAPI entry, or RoleMatrix row existed.
- Fix: `GET /api/v1/admin/diagnostics/workers` returning `IReadOnlyList<WorkerHealthDto>` behind the same elevated check + `admin.access` audit as the other diagnostics reads, `WorkerHealthService` injected into `AdminController`, `RoleMatrix` entry, `WorkerHealth` schema + path in `openapi.v1.json`, regenerated frontend client (`node tools/generate-client.mjs`, `node tools/check-api-drift.mjs` clean).
- Tests: `WorkerHealthEndpointTests` (5: route, response type, ctor wiring, secret-free DTO shape, frozen status values) + `AdminAuthzTests`/`AdminSseErrorContractTests`/`OpenApiCoverageTests` route lists extended (OpenAPI coverage 8 passed; the authz tests that spin the host still fail in this env for the pre-existing 401-instead-of-403 reason, verified on baseline).

## Second-batch commits
- GAP-010 53c1883
- GAP-011 7a69ae2
- GAP-012 7071bdd
- GAP-013 ca3578a
- GAP-014 30073ee
- GAP-015 84daa7a
- GAP-016 5ba9315
- GAP-017 3c332b7
- GAP-018 b08ba8d
- GAP-019 d484fb7
- GAP-020 74f45a5

## Second-batch verification
- `dotnet build DubbingPlatform.sln`: 0 warnings / 0 errors after every gap.
- `dotnet test UnitTests`: 3145 passed, 1 failed (`MediaValidationTests.Probe_Real_Files_Via_Ffprobe` — missing `ffprobe` in this env, pre-existing).
- `dotnet test ContractTests`: 39 passed.
- `dotnet test IntegrationTests`: new PG-gated tests pass (`MediaValidationServiceTests` 2, `RetentionHoldTests` 5, `MediaBombTests` 11); ffmpeg-gated mixing tests skip here (no ffmpeg, CI runs them).
- Pre-existing integration failures unrelated to these gaps (verified identical on baseline via `git stash`): WebApplicationFactory-based admin/export suites answer 401 instead of 403, and `metadata_json`/`jsonb` raw-SQL writes raise PG 42804 (see GAP-013 note).
- Frontend: `npm run typecheck` and `npm run lint` clean; `node tools/check-api-drift.mjs` clean.

# Third batch — GAP-021..GAP-028

Scope: the eight API/contracts/product gaps below. The DECISIONS block in the
task was left as a placeholder, so the repo's own decision rules applied (Plan A
wins backend, Plan B wins frontend/product; `missing_plan.md` §2 ambiguities).

## GAP-021 — top-level `timezone` on `/me` (DONE, 86f9698)
- Verified: `MeResponse` carried `locale` but no `timezone`; the value existed only under `UserPreference:timezone`.
- Fix: `MeResponse.Timezone` (after `Locale`), read from the `timezone` preference in one query alongside `locale`, default `UTC` via the pure `MeController.ParseTimezonePreference`; bundle + regenerated client.
- Tests: `UnitTests/Auth/MeTimezoneTests` (13) plus `MePreferencesTests` asserting the default and the `Europe/Paris` echo. The `MePreferencesTests` assertions are added but the host-based auth suites still fail in this environment (login returns 500 here, verified on baseline).

## GAP-022 — i18n copy debt 939 → 0 (DONE, 1df94fc)
- Verified: the gate was ratchet-only with `scripts/hardcoded-copy-baseline.json` = 939 (`jsx-text:466, user-facing-prop:416, copy-fallback:57`) across 61 production files.
- Fix: `scripts/migrate-hardcoded-copy.mjs` — a TypeScript-AST codemod that rewrites `jsx-text` to `{t('ns:key')}`, user-facing prop literals to `t(...)` (template attributes become i18next `{{…}}` interpolations), and prose `??`/`||` fallbacks to `t(...)`, writing the literal verbatim into `frontend/src/i18n/locales/en/<ns>.json` and inserting `useTranslation()` only into functions that can host it. It refuses (and reports) anything it cannot prove, and is idempotent.
- Because the English value is the original literal, rendered strings — and every existing `getByText` assertion — are unchanged. Three namespaces were added (`admin`, `cost`, `enrichment`) and registered in `resources.ts`.
- Four findings needed hand fixes: `ChunkErrorBoundary` is a class (React requires `componentDidCatch`), so the copy moved to a new `ChunkErrorFallback` component; `features/dashboard/api.ts`, `features/exports/useOutputs.ts`, and `hooks/useProgressStream.ts` are data-layer modules and resolve through the shared i18next instance (the documented non-React path; `APPROXIMATE_PERCENT_NOTE` became `approximatePercentNote()` so a locale switch after import still reads correctly).
- Gate: `scripts/hardcoded-copy-baseline.json` rewritten to 0; `node scripts/check-no-hardcoded-copy.mjs` passes **and** `--strict` passes at zero findings.

## GAP-023 — responsive layout (DONE, bb83956)
- Verified: Tailwind was configured, but production code used exactly one breakpoint class (`AppShell`'s `hidden md:block`). The audit's negative specs (`e2e/visual/screens.spec.ts`, `responsive-layout.spec.ts`) do not exist in this tree, so nothing was inverted; the spec was added as a positive one.
- Fix: `timelineResponsive.ts` pins the boundary to Tailwind `md` (767 list-mode / 768+ canvas); `useMediaQuery` (matchMedia + `useSyncExternalStore`, no resize handler) drives `TimelineWorkspace`, which renders the new `TimelineListMode` (stacked rows: speaker, time range, review state, review jump) below the breakpoint and the canvas waveform + five-lane timeline above it; `AppShell` gets a `md:hidden` mobile nav and a stacking content column.
- Tests: `timelineResponsive.test.ts` (4, pure) + `timelineLayout.test.tsx` (2, jsdom with a stubbed `matchMedia`), and `e2e/responsive-layout.spec.ts` (3 viewports). The Playwright suite cannot run in this environment — `e2e/auth.spec.ts` fails identically here on baseline — so the e2e assertions run in CI.

## GAP-024 — admin/enrichment/GPU reads (DONE, ba59c5e)
- Verified: the panels called `/admin/tenants`, `/admin/users`, `/admin/retention`, `/admin/feature-flags`, `/admin/local-gpu`; none existed, so five panels rendered the "not provisioned" placeholder. Plan B §12.19 lists tenants, users, roles, retention, audit, and feature flags as admin areas.
- Fix: `AdminScopeReadsService` + DTOs behind `GET /admin/tenants|users|retention|feature-flags|audit-events`, same elevated gate and `admin.access` audit as the other admin reads; `RoleMatrix` entries; OpenAPI schemas/paths; regenerated client. Audit rows deliberately omit `details_json` payloads.
- Out of scope **by decision**, recorded in `docs/api-contract.md`: local-GPU device health (node-level infrastructure telemetry with no product consumer) and enrichment runtime reads (Plan A §19 future-ready; the stages are not in the 32-endpoint v1 contract). The frontend keeps its 404-tolerant `EmptyState` path for both.
- Tests: `AdminReadsProvisioningTests` (9) + PG-gated `AdminScopeReadsTests` (2); `OpenApiCoverageTests` route list and count extended; `AdminAuthzTests`/`AdminSseErrorContractTests` route lists extended.

## GAP-025 — cost preflight is an estimate surface (DONE, bd77339)
- Verified: `CostService.PreflightAsync` returned a `double` the controller discarded, and the budget refusal carried figures only in prose.
- Fix: `CostPreflightEstimate` (estimate / current spend / cap / decision, all from the same pure `IsOverBudget`) returned by `PreflightEstimateAsync`; `ProcessingController.Start` returns it as `costEstimateUsd` on the 202 and converts an over-budget result into the new `CostBudgetExceededException`, whose `IErrorDetailsProvider` puts `estimateUsd|currentSpendUsd|limitUsd|projectedTotalUsd|dimension` in the 429 body. The old `PreflightAsync` still returns the estimate, so no caller broke.
- Tests: `CostPreflightSurfaceTests` (9). "Before expensive stages" was not extended: the per-stage gate is `ICostGate` (boolean, frozen contract) and per-segment holds already exist in the translation/TTS workers; adding an estimate to the dispatcher would change a frozen contract for no acceptance criterion.

## GAP-026 — canonical vs compat cancel/retry (DONE, 58beee2)
- Verified: 30/32 plan endpoints with `processing/{runId}/cancel|retry` (run-scoped) and `processing/cancel|retry` (project-scoped) both present.
- Fix: the run-scoped pair is documented as canonical in the bundle and the controller; the project-scoped pair is marked `deprecated: true` with a cross-reference to the canonical path, and keeps its frozen operationIds (`cancelActiveProcessingRun`, `retryActiveProcessingRun`) because renaming them would break clients. Plan A §7 carries a route-shape errata table.
- Tests: `ProcessingRouteCanonicalityTests` (8).

## GAP-027 — contract doc drift (DONE, 0b53389)
- Fix: Plan B §8.2.2's `reviewThreshold: "Default"` example corrected to `0.4` with an errata (the validator rejects strings, and now the plan's own example is executed by the test suite); §8.6.1's `VoicePreviewJob` list aligned field-for-field with the entity; `docs/api-contract.md` gained the authority table (including `IsTerminal` as computed, and why there is no per-job expiry).
- Tests: `ContractDocDriftTests` (5) compares the documented field sets with the entity by reflection and runs `HaveValidShape` on the plan's example.

## GAP-028 — error envelope (DONE, 7d8ce47)
- Verified: the wire is nested everywhere and the frontend maps all 65 codes; Plan B §9.12 still printed the flat shape and nine categories.
- Fix: §9.12 rewritten with the nested example, the 9→7 kind collapse (auth+authorization share `Auth`; provider-transient failures share `Unknown`), and a code→kind table covering all 65 codes.
- Tests: `ErrorEnvelopeDocTests` (6) asserts the plan example parses into `ErrorBody` field-for-field (camelCase), that both frontend maps cover every catalog code, and that the old category names survive only inside the errata.

## Third-batch commits
- GAP-021 86f9698
- GAP-022 1df94fc
- GAP-023 bb83956
- GAP-024 ba59c5e
- GAP-025 bd77339
- GAP-026 58beee2
- GAP-027 0b53389
- GAP-028 7d8ce47

## Third-batch verification
- `dotnet build DubbingPlatform.sln`: 0 warnings / 0 errors after every gap.
- `dotnet test UnitTests`: 3197 passed, 1 failed (`MediaValidationTests.Probe_Real_Files_Via_Ffprobe` — missing `ffprobe`, pre-existing).
- `dotnet test ContractTests`: 39 passed. `dotnet test IntegrationTests`: new PG-gated tests pass (`AdminScopeReadsTests` 2); the WebApplicationFactory auth suites still fail in this environment (verified identical on baseline via `git stash`).
- Frontend: `npm run typecheck` and `npm run lint` clean; `vitest` 1840 passed / 2 failed, both identical on baseline (version-stamp env assertions); `node scripts/check-no-hardcoded-copy.mjs` passes in ratchet **and** `--strict` mode at 0 findings; `check-no-hex` and `check-api-drift` clean.
