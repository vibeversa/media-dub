# missing_plan.md — Audit of AI-generated codebase vs Plan A (`implementation_plan-A.md`) and Plan B (`implementation_plan-B.md`)

> Rule applied throughout: a requirement is DONE only with code **plus wiring** (DI registration, route/controller, consumer/saga registration, hosted-service registration, EF configuration + migration, router entry). Every verdict cites path + symbol. MISSING states what was searched. Where plans conflict: Plan A wins for backend, Plan B wins for frontend/product.
> Plans live at repo root (`implementation_plan-A.md`, `implementation_plan-B.md`), not `docs/plans/`. Full traceability table is in `audit/traceability.csv`; Appendix here holds status counts + the complete ID table in compact form.

## 1. Summary

### Build / test baseline (Phase 0, verified 2026-10-02 env)
- `dotnet build DubbingPlatform.sln` (full clean): **not verifiable in audit env** — two runs exceeded 120 s (cold restore). Incremental build via `dotnet test` compiled with **0 warnings / 0 errors** (`TreatWarningsAsErrors=true`, `Directory.Build.props:6`). Recorded as GAP-036a, not a code defect.
- `dotnet test tests/DubbingPlatform.UnitTests`: **3065 passed, 2 failed, 0 skipped (total 3067, ~38 s)**. Failures: (1) `MediaValidationTests.Probe_Real_Files_Via_Ffprobe` — missing `ffprobe` binary in container (environmental; media images install ffmpeg — `Dockerfile.worker.media.preparation`, `Dockerfile.worker.media.render`); (2) `OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority("/relative/path")` — real validation gap (GAP-002).
- Frontend `npx tsc --noEmit`: **PASS exit 0**. Scripts present: `generate-api, check-drift, prebuild, typecheck, lint, build, test, test:presence, coverage:gap, build-storybook` (`frontend/package.json:7-22`).
- `docker compose config`: **PASS exit 0**. Layout matches Plan A step 1 (6 src projects + 4 required test projects + 8 Dockerfiles + fast/full profiles + Makefile targets) and Plan B 10.2 (domains all present; grouping names differ cosmetically — flat `components/`, single `stores/index.ts`, `app/session` vs `features/auth` split).

### Counts
- Atomic requirements extracted: **480 rows** (Plan A steps 1–32 + Final Verification; Plan B sections 8–25). Full list: `audit/traceability.csv`.
- Status roll-up (from CSV): **DONE 425 | PARTIAL 50 | DEVIATED 3 | UNVERIFIABLE 2 | MISSING 0 as standalone rows, STUB 0**. (No STUBs found: `grep NotImplementedException|TODO|FIXME` across audited paths returns only `NotSupportedException` in read-only stream wrappers. Wholly-absent items surface as PARTIAL parents — e.g. `acrossfade`, worker-health endpoint, media-bomb tests — and are tracked as work packages below.) One subagent-reported gap, "cloning disabled by default missing", was **refuted on re-check**: `VoiceOptions.CloningEnabled` exists, default `false`, `src/DubbingPlatform.Application/Options/VoiceOptions.cs:8-19` — logged under ambiguities, not counted as a gap.
- By area (work packages only): orchestration/leases **4** · artifacts/storage **3** · providers **3** · API/contracts **7** · pipeline stages **5** · cost/quota **3** · frontend arch/UX **7** · product workflows **5** · ops/testing **4** · UNVERIFIABLE records **4 (1 package)**.
- Test-coverage gaps (Validation bullets with no exercising test): retention-hold-blocks-delete, disk-pressure/media-bomb, storage-quota-atomicity, lease-version-increment, responsive-layout (self-pinned failing spec), global-offline, full-journey green run record.

### Top 10 blockers
1. GAP-001 SSE `?access_token=` in URL (security, contradicts Plan B 9.11).
2. GAP-002 `AuthOptions.Authority` accepts relative URI (auth, failing test proves it).
3. GAP-003 `LeaseTokenVersion` never incremented (lease-fencing counter dead).
4. GAP-004 Lease-recovery index lacks `LeaseExpiresAt` predicate (sweeper scans unindexed).
5. GAP-005 Storage quota is pre-check only, not transactional with artifact commit (over-admit race).
6. GAP-006 `SensitivePolicy/VoicePolicy/RetentionOverride` hashed but not enforced (privacy routing hole).
7. GAP-007 Project-list language/date filtered client-side; review-required server support unverified (full-dataset + scale risk).
8. GAP-008 Preflight estimate is a client-side mirror, no server endpoint (cost authority in browser).
9. GAP-009 Memory / temp-disk / `MinDiskFreeBytes` unenforced (OOM/temp-full not failed fast).
10. GAP-010 Prefixed public IDs are API-only; no EF value converters (DB/API mapping split).

## 2. Plan ambiguities / contradictions
- **A-01 (resolved, not a gap):** ops subagent claimed "voice cloning disabled by default" switch missing (`grep VoiceCloning|CloningEnabled` no hits). Re-check found `VoiceOptions.CloningEnabled` (bool default `false`, kill-switch doc) at `src/DubbingPlatform.Application/Options/VoiceOptions.cs:8-19` plus `VoiceProfile.CloningEnabled` (`Domain/Entities/VoiceProfile.cs:22`). Subagent grep scope error. No action.
- **SSE token transport:** Plan B 9.11 forbids tokens in URLs. Backend explicitly supports `?access_token=` on `/stream` only (`Api/Program.cs:391-396`, `ProcessingController.cs:47,633`, `OpenApiConfiguration.cs:33`). Plan A is silent (`grep access_token|progress/stream implementation_plan-A.md` → zero). Frontend is compliant (header-only, `useProgressStream.ts:356-357,632-641`, negative tests). Decision required, tracked as GAP-001.
- **Error envelope shape:** Plan B 9.12 prints flat `{code,…}`; wire is `{error:{code,message,correlationId,details}}` (`Api/Errors/ApiError.cs`, `Middleware/ErrorResponse.cs`, frontend `httpClient.ts` parses nested). Backend+frontend consistent → implementation authoritative; plan snippet is illustrative (GAP-028, docs-only).
- **Review endpoints:** Plan A lists approve/reject/requeue/resolve; impl adds dismiss/reopen/resolve-with-edit + context + idempotency/version rules (Plan B 9.7). Additive superset, no contradiction — keep.
- **`reviewThreshold` type:** Plan B example shows string `"Default"`; validator requires number 0–1 (`ProjectProcessingSettingsValidator.cs:123,243`). Plan errata or enum support (GAP-027a).
- **`VoicePreviewJob` fields:** plan `RequestedText/VoiceProfileId/FailureCategory/ExpiresAt` vs impl `Text/VoiceId/ErrorCode+ErrorMessage/StartedAt+CompletedAt` (+ extras). Backend authoritative; contract doc must be aligned (GAP-027b).
- **`/me.timezone`:** Plan B 9.1 requires top-level; impl exposes only `locale` + `UserPreference:timezone`. Frontend/product wins → backend adds field (GAP-021).
- **"tus" protocol:** audit brief mentions tus; Plan A steps 2–8 specify S3-multipart resumable (presigned PUTs + reconcile), which is implemented. `grep -rni tus|Upload-Offset src tests docs frontend/src` → zero (outside `NotFound` noise). S3-multipart is authoritative; no tus work unless plan is amended.
- **Cancel/retry routes:** plan `POST /projects/{id}/cancel|retry` vs impl `processing/cancel`, `processing/retry` (legacy) + `processing/{runId}/cancel|retry`. Functional superset; document legacy (GAP-026, docs-only).
- **Nav consolidation:** Plan B 11.1 lists Diagnostics/ProviderHealth/Usage as nav; impl consolidates under `/admin` panels (`AdminPage.tsx`, `getTopNavItems`). Accept as-is (confirm at sign-off).
- **Cookies/CSRF:** plan mandates HttpOnly+SameSite+CSRF "where session-based"; impl is bearer-only (memory tokens, `authStore.ts:12-32`), so CSRF is N/A. Confirm posture.
- **Backup RPO/RTO contacts:** `deploy/backup/policy.json` carries `CHANGE_ME`; Plan A §30 values apply. Replace before gating release.
- **Preflight "where available":** read as permitting client mirror (current); strict reading requires server endpoint. GAP-008 adopts strict reading per §4 backend-owns-cost.

## 3. Work packages (ordered by dependency; foundations first; one agent session each)

### [GAP-001] SSE `?access_token=` in URL violates "never in query string"
- Plan ref: B-9.11 Transport Rules (Plan B wins)
- Status: DONE (61d0b32)
- Priority: P0 (security: token in URL → logs/history leak)
- Current state: supported on `/stream` only — `src/DubbingPlatform.Api/Program.cs:391-396`, `src/DubbingPlatform.Api/Controllers/ProcessingController.cs:47,633`, `src/DubbingPlatform.Api/OpenApi/OpenApiConfiguration.cs:33`. Frontend compliant (header-only + negative tests `frontend/src/hooks/__tests__/envelope.test.ts:181`).
- Missing: either remove query-token support (breaking `EventSource`) or amend Plan B to allow short-TTL single-use scoped token on `/stream` only with never-logged guarantee + tests.
- Acceptance criteria: no `access_token` query handling in backend OR amended plan + single-use TTL + redaction tests + OpenAPI updated; frontend unchanged (header-only).
- Where to implement: `src/DubbingPlatform.Api/Program.cs`, `ProcessingController.cs`, `OpenApiConfiguration.cs`, `implementation_plan-B.md` §9.11
- Depends on: none

### [GAP-002] `AuthOptions.Authority` accepts relative URI `/relative/path`
- Plan ref: A-§3 Action 19 (options validation at startup)
- Status: DONE (5b3b2c6)
- Priority: P0 (auth bypass surface)
- Current state: validator exists; `tests/DubbingPlatform.UnitTests/Options/OptionsValidationMatrixTests.cs:146` FAILS (`Assert.True() Failure`).
- Missing: absolute-URI + scheme enforcement for Authority.
- Acceptance criteria: theory (`/relative/path`, `example.com`, `not-a-uri`) all reject; full UnitTests green except env-only ffprobe test.
- Where to implement: `src/DubbingPlatform.Application/Options/AuthOptions.cs` (`AuthOptionsValidator` — `Uri.TryCreate(..., UriKind.Absolute)` + scheme check)
- Depends on: none

### [GAP-003] `LeaseTokenVersion` column exists but is never incremented
- Plan ref: A-§4 Action 12 ("Increment LeaseToken on every new lease grant")
- Status: DONE (fb62b73)
- Priority: P0 (fencing correctness; stale holder can commit if token string comparison ever skipped)
- Current state: `StageExecution.cs:32,103` sets `0`, zero writers (`grep LeaseTokenVersion src` → entity + migrations only). String rotation works (`StageExecutionService.cs:ClaimCoreAsync/TryTakeoverExpiredAsync`, `StageExecutionSql.cs:TakeoverSql`); `RenewLeaseSql` correctly does not rotate.
- Missing: `SET lease_token=@new, lease_token_version=lease_token_version+1` on grant/takeover; unit test (bumps on takeover, unchanged on renew).
- Acceptance criteria: takeover increments version; renew does not; `StaleCommit_Fenced` tests cover version path.
- Where to implement: `src/DubbingPlatform.Application/Services/StageExecutionService.cs`, `StageExecutionSql.cs`, `Domain/Entities/StageExecution.cs`
- Depends on: none

### [GAP-004] Lease-recovery partial index missing `LeaseExpiresAt` predicate
- Plan ref: A-§2 Action 19
- Status: DONE (419e1ba; NOW() rejected per Rule 4, immutable covering index; see fix_notes.md)
- Priority: P0 (recovery scan performance/correctness)
- Current state: `StageExecutionConfiguration.cs:28` filters `status='Running'` only; composite `(ProcessingRunId,Status,LeaseExpiresAt)` at `:27` is non-partial.
- Missing: `.HasFilter("status='Running' AND lease_expires_at < NOW()")` + migration; sweeper query plan uses it.
- Acceptance criteria: migration creates true stale-lease partial index; `WorkerRecoverySweeper` (`Workers/Services/WorkerRecoverySweeper.cs:60s`) EXPLAIN uses it.
- Where to implement: `src/DubbingPlatform.Infrastructure/Persistence/Configurations/StageExecutionConfiguration.cs` + new migration
- Depends on: none

### [GAP-005] Storage quota enforced as pre-check, not transactionally with artifact commit
- Plan ref: A-§25 Action 9
- Status: DONE (70c40db)
- Priority: P0 (quota over-admit race → storage exhaustion)
- Current state: `QuotaService.cs:188 CheckStorageAsync` pre-check; `ArtifactService.cs:CommitNewAsync` separate txn; `CostService.cs:264-298 ReserveAsync` is transactional but storage gate is not joined.
- Missing: serializable reservation joined to artifact-commit txn (or documented counter + test proving no over-admit under concurrency).
- Acceptance criteria: concurrent-commit test cannot exceed quota; rejection is `429 QUOTA_EXCEEDED`.
- Where to implement: `src/DubbingPlatform.Application/Services/QuotaService.cs`, `ArtifactService.cs`, `tests/DubbingPlatform.IntegrationTests/Cost/QuotaTests.cs`
- Depends on: none

### [GAP-006] `SensitivePolicy` / `VoicePolicy` / `RetentionOverride` hashed but not enforced in routing
- Plan ref: A-§6 (privacy policy in routing), B-§4.8
- Status: DONE (b49797b)
- Priority: P0 (privacy: sensitive data may route to external providers)
- Current state: `ProcessingPolicy.cs:5-20` has all 7 fields; `PolicyChecker.cs:CanUseProvider` enforces external/allowed/residency/local only; sensitive/voice/retention explicitly hashed-not-blocked (docstring).
- Missing: block/route semantics for the three fields, or explicit risk-accepted decision with tests.
- Acceptance criteria: `ResolverTests`-style test proves sensitive payload + restrictive policy blocks external route and records `ProviderMeters.PolicyDenied`.
- Where to implement: `src/DubbingPlatform.Application/Providers/PolicyChecker.cs`, `ProviderResolver.cs`, `tests/DubbingPlatform.UnitTests/Providers/ResolverTests.cs`
- Depends on: none

### [GAP-007] Project-list language/date filters applied client-side; review-required server support unverified
- Plan ref: B-9.2 + B-12.3 (server pagination, "do not return full datasets")
- Status: DONE (e509670)
- Priority: P0 (data-exposure/scale: page fetched then filtered in memory)
- Current state: server sends `status/ownerId/archived/sort` only; `target-language/date-range` refined in memory (`features/projects/useProjectsQuery.ts:16`, `ProjectsPage.tsx:40`, `api.ts:228`); `review-required` server filter not found by grep.
- Missing: server-side `targetLanguage/dateFrom/dateTo/reviewRequired` filters + sort, or documented small-tenant exception with dataset cap.
- Acceptance criteria: filter combinations narrow server response (MSW + integration test); no full-dataset fetch for filtered views.
- Where to implement: `frontend/src/features/projects/`, `src/DubbingPlatform.Api/Controllers/ProjectsController.cs`, `Application/Services/ProjectService.cs`
- Depends on: none

### [GAP-008] Preflight estimate is a client-side mirror; no server estimate endpoint
- Plan ref: B-12.6/12.18 + B-§4 backend-owns-cost
- Status: PARTIAL (DEVIATED authority)
- Priority: P1 (cost correctness; drift risk) — P0-adjacent for paid pipelines
- Current state: `features/processing/usePreflight.ts:4` mirrors `CostService/PriceTable` with `PREFLIGHT_*` constants; cost gate is boolean `ICostGate.CanProceedAsync` (`ProcessingController.cs:195`).
- Missing: `GET .../processing/preflight|estimate` returning server estimate + quota/consent verdict; UI consumes it (client mirror removed or labeled fallback).
- Acceptance criteria: estimate endpoint covered by `PreflightAsync` (`CostService.cs:422`); UI test asserts server figure rendered; drift test fails on PriceTable change without client update (until mirror removed).
- Where to implement: `src/DubbingPlatform.Api/Controllers/ProcessingController.cs`, `Application/Services/CostService.cs`, `frontend/src/features/processing/`
- Depends on: GAP-005 (quota truth)

### [GAP-009] Memory / temp-disk / `MinDiskFreeBytes` unenforced in media path
- Plan ref: A-§9 Action 8 + Validation "resource exhaustion fails fast"
- Status: DONE (6a539dd)
- Priority: P0 (worker OOM / disk-full → partial commits)
- Current state: `MediaOptions{CpuThreads,FfmpegTimeoutSec,MaxConcurrentMediaJobs,MinDiskFreeBytes=1GiB}` exists (`MediaOptions.cs:24`); `AudioPreparationService` checks `EnsureFree(2*SizeBytes)` but ignores `MinDiskFreeBytes`; zero memory refs; no temp-dir quota (`grep Memory|MinDiskFreeBytes` in service/media paths → validator only).
- Missing: cgroup/memory check, temp-disk quota, honor `MinDiskFreeBytes`; OOM/temp-full fails `RESOURCE_EXHAUSTED` with no partial commit.
- Acceptance criteria: `MediaOptionsValidator` + `DiskSpaceChecker` + service enforce all five knobs; exhaustion test asserts fast fail + cleanup.
- Where to implement: `Application/Options/MediaOptions.cs`, `Infrastructure/Media/DiskSpaceChecker.cs`, `Application/Services/AudioPreparationService.cs`
- Depends on: none

### [GAP-010] Prefixed public IDs are API-only; no EF value converters
- Plan ref: A-§2 Action 5
- Status: DONE (53c1883; plan amendment recorded in `docs/adr/ADR-010-public-ids-api-only.md` per acceptance criterion "or ADR records rejection")
- Priority: P1 (ID-mapping split; round-trip risk)
- Current state: `Domain/Identity/PublicIdMapper.cs:12-33` (16 prefixes + extras) + `Api/Models/PublicIdParser.cs`; `grep HasConversion|ValueConverter|PublicIdMapper Infrastructure/Persistence` → enum→string only; IDs persist as raw Guid.
- Missing: `ValueConverter<Guid,string>` + `HasConversion` per ID property (or ADR explicitly rejecting prefixed storage).
- Acceptance criteria: every public ID round-trips `prj_/run_/…` ↔ Guid through EF, or ADR records rejection.
- Where to implement: `Infrastructure/Persistence/Converters/` (new) + `Configurations/*Configuration.cs` or shared `ConfigurePublicIds(ModelBuilder)`
- Depends on: none

### [GAP-011] `ProviderExecution` missing 4–5 recorder columns
- Plan ref: A-§6 (`ProviderExecutionRecorder` fields)
- Status: DONE (7a69ae2)
- Priority: P1 (audit/reconciliation completeness)
- Current state: `Domain/Entities/ProviderExecution.cs:8-66` has 20/24; `grep UsageDimensions|SystemInstructionHash|SafetySettingsHash|OutputContentHash|PromptTemplateVersion` → zero (`ResponseHash` proxies output; `ActualCost` proxies usage; `PromptTemplateId:string?` not Id+Version).
- Missing: `UsageDimensionsJson`, `SystemInstructionHash`, `SafetySettingsHash`, `PromptTemplateVersion`, distinct `OutputContentHash` + migration + population from `ProviderUsage/RawMetadata`.
- Acceptance criteria: `grep "public.*get" ProviderExecution.cs` lists all 24+; recorder populates them on success + fallback paths.
- Where to implement: `Domain/Entities/ProviderExecution.cs`, `Configurations/`, migration, `Application/Services/*Service.cs` (Transcription/Translation/Tts/Separation/ContextBuilder), `Infrastructure/Providers/ProviderExecutionRecorder.cs`
- Depends on: none

### [GAP-012] Google / LocalInference have no long-running batch job API
- Plan ref: A-§6 (async job support)
- Status: DONE (7071bdd; plan scoped to STT-batch-only with `IBatchTranscriptionProvider` + provider README scoping note, per acceptance criterion)
- Priority: P1 (lease-loss reconcile gap for those providers)
- Current state: `StartBatch/GetBatchStatus` + `ProviderJobPoller` + `ExternalJobId` exist for Azure/OpenAI STT only; `grep StartBatch|GetBatchStatus Providers/Google/ Providers/LocalInference/` → zero (mock-only `MockAsyncJobStore`).
- Missing: batch start/poll for Google/Local, or scope plan to STT-batch-only with doc.
- Acceptance criteria: poll/reconcile-after-lease-loss tested per provider, or plan scoping note.
- Where to implement: `Infrastructure/Providers/Google/`, `LocalInference/`, `ProviderJobPoller.cs`
- Depends on: GAP-011 (execution columns for job linkage)

### [GAP-013] `MediaValidation` has no `StageWorkRequested` / `BaseConsumer` worker
- Plan ref: A-§4 ("each StageGraph stage must have registered consumer")
- Status: DONE (ca3578a; `MediaValidationWorker` registered — the saga dispatched this stage on `RunStarted` with no consumer, so runs stalled before `MediaAnalysis`)
- Priority: P1 (uniform retry/lease/instrumentation)
- Current state: 16/17 stages via `BaseConsumer` + `Workers/Program.cs:218-233` + `ShouldProcess`; MediaValidation only via `MediaIngestionWorker:IConsumer<MediaUploaded>` (`Workers/Consumers/MediaIngestionWorker.cs:27,118`) + `MediaIngestionService.cs:412,451`. `grep ShouldProcess.*MediaValidation` → zero.
- Missing: `MediaValidationWorker:BaseConsumer<StageWorkRequested>` or explicit plan exception for ingestion-owned MediaValidation.
- Acceptance criteria: every `StageGraph.Nodes` stage resolves to a registered consumer in a test (or doc exception); `RunStarted→MediaValidation` pump covered.
- Where to implement: `src/DubbingPlatform.Workers/Consumers/`, `Workers/Program.cs`, `Application/Orchestration/StageGraph.cs` docs
- Depends on: none

### [GAP-014] Artifact publish deviates: no `Pending` reserve; stage-complete/outbox not in artifact txn; `ArtifactChild` table absent
- Plan ref: A-§5 Actions 3, 14
- Status: DONE (30073ee; as-built recorded in `docs/adr/ADR-014-artifact-publish-as-built.md` incl. W1-W4 crash windows + redrive proof)
- Priority: P1
- Current state: `ArtifactParent.cs` + config exist; `grep ArtifactChild src` → plan mention only; `ArtifactService.cs` zero `Pending` refs, commits `Committed` directly post-upload in one txn (`CommitNewAsync`); stage completion + `StageCompleted` publish happen in workers via bus outbox (separate `DbContext`).
- Missing: (a) decide `ArtifactChild` = second table vs edge-direction alias — implement or amend plan; (b) Pending-reserve or amend to upload-first+reconciler (current is orphan-safe and tested); (c) document two-txn crash windows (artifact-commit vs stage-commit/outbox) with redrive proof.
- Acceptance criteria: lineage both directions without JSON; crash-between-upload-and-commit → quarantinable orphan (tested); crash-between-artifact-commit-and-stage-complete → redrivable without duplicates.
- Where to implement: `Domain/Entities/`, `Configurations/`, `Application/Services/ArtifactService.cs`, `Workers/Consumers/*Worker.cs`
- Depends on: GAP-005 (atomicity posture)

### [GAP-015] `ContentObjectStateMachine` omits direct `Committed → Deleted`
- Plan ref: A-§2 Action 18 + §3 retention
- Status: DONE (84daa7a; `Committed → Deleted` fast path added to the state machine)
- Priority: P1 (sweeper can throw `DomainException` on legal path)
- Current state: `ContentObjectStateMachine.cs:21-23` only Pending→Committed→Orphaned→Deleted.
- Missing: `Committed → Deleted` fast path (empty-ref + retention satisfied) or documented decision that all deletes pass through `Orphaned`.
- Acceptance criteria: retention sweeper's transition never throws for its legal path; table matches sweeper.
- Where to implement: `Application/StateMachines/ContentObjectStateMachine.cs`, `Application/Services/RetentionService.cs`
- Depends on: none

### [GAP-016] No max-concurrent-provider-calls-per-tenant fairness cap
- Plan ref: A-§25 Action 16 (second fairness cap)
- Status: DONE (5ba9315; `RateLimit:TenantConcurrency` + `tenantConcurrency` dimension enforced in `TenantFairnessGate`)
- Priority: P1 (noisy-neighbor on shared provider quota)
- Current state: only `MaxConcurrentStagesPerTenant=20` (`QuotaOptions.cs:32`); RateLimiter `concurrency` dimension is per-provider fail-open, not a tenant fairness cap. `grep MaxConcurrentProvider src` (excl obj/bin) → zero.
- Missing: distinct per-tenant concurrent-provider-call cap + enforcement in dispatcher/gates + metric.
- Acceptance criteria: second tenant's calls admitted while first at cap; rejection recorded.
- Where to implement: `Application/Options/QuotaOptions.cs`, `Infrastructure/Orchestration/*Gates*.cs`, `Infrastructure/Redis/RateLimiter.cs`
- Depends on: GAP-005

### [GAP-017] Mixing `acrossfade` absent (overlapping dialogue has no measured crossfade)
- Plan ref: A-§20 Action 4
- Status: DONE (3c332b7; conditional `acrossfade` chain + `PlanCrossfades`, recorded in `MixResult.PremixFilter`/`FilterGraph`)
- Priority: P1 (audible overlap seams)
- Current state: `grep -n crossfade FFmpegMixer.cs` → zero; premix uses `adelay+apad+amix`. QC `CodeCrossfade` check exists (`QualityControlService.cs:119,944-1187`) and will flag silent overlap windows.
- Missing: conditional `acrossfade` + filter-graph recording + QC wiring.
- Acceptance criteria: overlapping dialogue has measured crossfade; `MixResult.FilterGraph` records it.
- Where to implement: `Infrastructure/Media/FFmpegMixer.cs:BuildPremixFilter`, `Application/Services/QualityControlService.cs:CodeCrossfade`
- Depends on: none

### [GAP-018] Disk-pressure and media-bomb protection tests absent
- Plan ref: A-§28 Actions 20–21, A-§30 Action 9, Final Verification
- Status: DONE (b08ba8d; media-bomb fixtures + validator/scratch/disk/memory exhaustion tests)
- Priority: P1 (resource-exhaustion safety unverified)
- Priority: P1 (resource-exhaustion safety unverified)
- Current state: `grep DiskPressure|MediaBomb tests` (147 files) → zero; only `scripts/generate-fixtures.sh` mentions "media-bomb negative controls".
- Missing: dedicated tests (quota-exceeded mid-upload, temp-full FFmpeg fail, oversized/corrupt media reject) with fixtures.
- Acceptance criteria: Validation bullets "resource exhaustion fails fast" and "media-bomb rejected" have executing tests.
- Where to implement: `tests/DubbingPlatform.IntegrationTests/Media/`, `fixtures/`, `scripts/generate-fixtures.sh`
- Depends on: GAP-009

### [GAP-019] Retention-hold-blocks-deletion untested
- Plan ref: A-§5 Validation "Retention hold prevents deletion"
- Status: DONE (d484fb7; PG-gated `RetentionHoldTests` prove hold blocks delete and release re-enables it)
- Priority: P1 (legal-hold data loss risk)
- Current state: `RetentionService.cs`, `ContentObjectService.cs:101,160`, `RetentionHold.cs`, `DeletionJob.cs`, `RetentionSweeper`, `OrphanObjectReconciler` all exist + hosted (`Workers/Program.cs:208-210,237`); `grep RetentionHold|CanDelete|RetentionService tests/` → zero.
- Missing: integration test — hold placed → `CanDelete==false`/sweep skips; released + expired + refcount 0 → deletable.
- Acceptance criteria: hold-blocks-delete bullet has a live (Testcontainers/PG-gated) test.
- Where to implement: `tests/DubbingPlatform.IntegrationTests/Storage/` (`RetentionTests.cs` new or extend `ArtifactStorageTests.cs`)
- Depends on: none

### [GAP-020] `WorkerHealthService` has no admin endpoint
- Plan ref: B-8.8 + B-9.10 (Plan B wins)
- Status: DONE (74f45a5; `GET /api/v1/admin/diagnostics/workers` + RoleMatrix + OpenAPI + regen client)
- Priority: P1 (operator blindness)
- Current state: `Application/Diagnostics/WorkerHealthService.cs:GetWorkerHealthAsync` + DI `DiagnosticsRegistration.cs` exist; `grep WorkerHealth|diagnostics/worker src/DubbingPlatform.Api` → zero endpoints. Other 7/8 diagnostics reads present (`AdminController.cs:95-366`, `RequireTenantAdmin`).
- Missing: `GET /api/v1/admin/diagnostics/workers` (TenantAdmin, secret-free, audited if destructive-adjacent).
- Acceptance criteria: endpoint returns worker health; `AdminAuthzTests`-style auth test; OpenAPI lists it.
- Where to implement: `src/DubbingPlatform.Api/Controllers/AdminController.cs`, `Application/Diagnostics/`
- Depends on: none

### [GAP-021] `/me` missing top-level `timezone`
- Plan ref: B-9.1 `/me` Response Must Include (Plan B wins)
- Status: PARTIAL
- Priority: P2 (extra round-trip; contract breach)
- Current state: `MeResponse` (`Api/Models/AuthMeDtos.cs:63`) has user/tenant/roles/permissions/locale/flags/session but no `timezone`; timezone only via `UserPreference:timezone` key (`MeController.cs:GetPreferences/PutPreferences`).
- Missing: top-level `timezone` sourced from preferences with default, or plan amendment.
- Acceptance criteria: `GET /me` includes `timezone`; `MePreferencesTests` asserts it.
- Where to implement: `src/DubbingPlatform.Api/Models/AuthMeDtos.cs`, `MeController.cs:Get`
- Depends on: none

### [GAP-022] i18n copy debt: 939 hardcoded literals remain
- Plan ref: B-10.9 (no hardcoded strings) + B-11
- Status: PARTIAL (ratchet stops growth; baseline not cleared)
- Priority: P1 (localization/RTL completeness)
- Current state: infra DONE (`i18n/{resources,i18n,direction,format,localePreference,useLocale}`, 19 `en` namespaces, `ar`/`ru`, `rtl.css`, `LocaleProvider`); gate is ratchet-only: `scripts/hardcoded-copy-baseline.json` = 939 (`jsx-text:466,user-facing-prop:416,copy-fallback:57`); `frontend.yml` hard-coded-copy + physical-side checks.
- Missing: per-file migration to keys + `--strict` gate at zero.
- Acceptance criteria: baseline counts reach 0 for user-facing categories; new literals fail CI (already true).
- Where to implement: `frontend/src/features/**`, `frontend/src/components/**`, `scripts/check-no-hardcoded-copy.mjs`
- Depends on: none

### [GAP-023] Responsive mobile/tablet layout absent (zero breakpoint utilities)
- Plan ref: B-11.5 + B-15.8 R4 + B-12.12 timeline degradation
- Status: MISSING (self-declared by repo tests)
- Priority: P1 (required UX; timeline/review/playback/export mobile paths)
- Current state: scan of 71 feature components found ZERO `sm:/md:/lg:/xl:/2xl:` or breakpoint hooks; `e2e/visual/screens.spec.ts:15-19` + `responsive-layout.spec.ts` pin the finding (fail when responsive appears); only `AppShell.tsx:176 hidden md:block` sidebar.
- Missing: breakpoints + timeline list-degradation on small screens + visual baselines desktop/tablet/mobile × dark/light × LTR/RTL.
- Acceptance criteria: `responsive-layout.spec` green; timeline degrades to list mode <768px; baselines committed.
- Where to implement: `frontend/src/features/**`, `frontend/src/app/layouts/`, `e2e/visual/`
- Depends on: none

### [GAP-024] Admin / enrichment / GPU reads unprovisioned in this deployment
- Plan ref: B-12.19 + B-18.2 + B-19
- Status: PARTIAL (UI correctly degrades; backend surfaces absent)
- Current state: panels render "not provisioned on this backend yet" (`TenantsPanel.tsx:69`, `UsersRolesPanel.tsx:81`, `FlagsPanel.tsx:78`, `RetentionAuditPanel.tsx:89,150`, `LocalGpuPanel.tsx:129`); enrichment reads 404/501 → `NotAvailableState` (`useEnrichmentQueries.ts`); tenants/users/flags/retention/local-GPU/enrichment reads missing.
- Missing: provision reads (or pinning scope decision that this deployment excludes them) + backend endpoint per panel.
- Acceptance criteria: each admin/enrichment panel has a provisioned read or an explicit out-of-scope record; no panel stuck on placeholder in target env.
- Where to implement: `src/DubbingPlatform.Api/Controllers/AdminController.cs`, enrichment/GPU read endpoints, `deploy/` rollout flags
- Depends on: GAP-020 (worker health is one such read)

### [GAP-025] Cost preflight is boolean gate, not an estimate surface
- Plan ref: A-§9 Action 2 + A-§25 Action 6
- Status: PARTIAL
- Priority: P1 (budget UX; 429 without figure)
- Current state: `ICostGate.CanProceedAsync` (`ProcessingController.cs:195`, `ProcessingStartService.cs:49-88`); `CostService.{Estimate,EstimatePipeline,PreflightAsync:422}` exist but no estimate returned at start.
- Missing: `EstimateAsync` surfaced at processing start + before expensive stages; 429 carries budget-vs-estimate.
- Acceptance criteria: start returns estimate; budget-exceed → 429 with figures.
- Where to implement: `Application/Processing/*`, `Infrastructure/Orchestration/CostRateGates.cs:ICostGate`, `Api/Controllers/ProcessingController.cs`
- Depends on: GAP-008 (same surface; implement once)

### [GAP-026] `cancel`/`retry` route shape deviates from plan
- Plan ref: A-§7 (32 endpoints)
- Status: DEVIATED (acceptable; docs-only)
- Priority: P2
- Current state: plan `POST /projects/{id}/cancel|retry` vs impl `processing/cancel`, `processing/retry` (legacy compat) + `processing/{runId}/cancel|retry` (`ProcessingController.cs:358,418,508,557`). 30/32 endpoints DONE; superset behavior.
- Missing: keep legacy but document canonical vs compat routes in OpenAPI/plan.
- Acceptance criteria: OpenAPI marks canonical routes; plan errata note.
- Where to implement: `Api/Controllers/ProcessingController.cs` docs, `Api/OpenApi/`, plan errata
- Depends on: none

### [GAP-027] Contract doc drift: `reviewThreshold` type + `VoicePreviewJob` field names
- Plan ref: B-8.2.2 + B-8.6.1
- Status: DEVIATED (code consistent; docs-only)
- Priority: P2
- Current state: (a) plan example `reviewThreshold:"Default"` vs validator numeric 0–1 (`ProjectProcessingSettingsValidator.cs:123,243`); (b) plan `RequestedText/VoiceProfileId/FailureCategory/ExpiresAt` vs impl `Text/VoiceId/ErrorCode+ErrorMessage/StartedAt+CompletedAt` (+ extras `RequestedByUserId/IdempotencyKey`), `Domain/Entities/VoicePreviewJob.cs`, migration `20260921115016_AddVoicePreviewJobs.cs`.
- Missing: support enum-string OR fix plan example; align `VoicePreviewJob` contract doc with impl (or add missing columns).
- Acceptance criteria: contract doc ↔ impl field-for-field; validator tests pin both.
- Where to implement: `Application/Validation/ProjectProcessingSettingsValidator.cs` or plan §8.2.2/§8.6.1, `docs/api-contract.md`
- Depends on: none

### [GAP-028] Error envelope shape: plan flat vs wire nested
- Plan ref: B-9.12
- Status: DEVIATED (implementation authoritative; docs-only)
- Priority: P2
- Current state: wire `{error:{code,message,correlationId,details}}` everywhere (`Api/Errors/ApiError.cs`, `Middleware/ErrorResponse.cs`, frontend `httpClient.ts`); 65 codes exhaustive (`ErrorCodes.cs:All`, `normalizeError.ts:errorKindByCode`, `recoveryHintByCode`); `ErrorKind` collapses plan's 9 categories to 7 (documented in `kinds.ts`).
- Missing: fix plan snippet to nested shape; confirm 7-kind collapse.
- Acceptance criteria: plan §9.12 matches wire; mapping table covers all 65 codes.
- Where to implement: `implementation_plan-B.md` §9.12
- Depends on: none

### [GAP-029] Timing lead/lag + silence dimensions not tunable
- Plan ref: A-§18 Action 2
- Status: DEVIATED (documented deferral; confirm or implement)
- Priority: P2
- Current state: `TimingOptimizationService` fixes `onsetErr=0`; `TimingOptions` lacks `AllowableLeadLag/AllowableSilence`; defaults ±50/±100ms, ±15%, 1.15x + `syncScore` + `SyncResult` all DONE.
- Missing: options + placement shift, or explicit wont-do with timeline-assembly ownership note.
- Acceptance criteria: options exist and move placement, or plan amendment names owner.
- Where to implement: `Application/Options/TimingOptions.cs`, `Application/Services/TimingOptimizationService.cs`
- Depends on: none

### [GAP-030] 32-bit float working-audio path never produced
- Plan ref: A-§9 Action 7
- Status: PARTIAL (dead code path)
- Priority: P2
- Current state: `FFmpegService.BuildFloatArgs/ExtractCanonicalAudioAsync(needsFloatWork=false)` + `IFFmpegService.cs:42-53` exist; `grep needsFloatWork=true|WorkingAudio` → no caller passes `true`; `AudioPreparationService` never requests float.
- Missing: caller condition + `WorkingAudio` publish path, or removal.
- Acceptance criteria: flag triggers `WorkingAudio` artifact else none; test pins behavior.
- Where to implement: `Application/Services/AudioPreparationService.cs`, `Infrastructure/Media/FFmpegService.cs`
- Depends on: none

### [GAP-031] Real-time progress is poll-SSE; Redis pub/sub (or outbox-driven) push absent
- Plan ref: A-§24 Action 4
- Status: PARTIAL
- Priority: P2 (2s-poll SSE meets UX; push is optimization)
- Current state: `GET progress/stream` SSE exists (`ProcessingController.cs:644-658`) but header comment (`:46-56`) admits 2s poll; Redis key `progress:{tenant}:{project}` "future push optimization" — no pub/sub wired. Frontend treats SSE as invalidation hints + 3s/20s polling fallback (`useProgressStream.ts`) — correct per B-10.5.
- Missing: Redis pub/sub or outbox-driven push; or scope plan to poll-SSE.
- Acceptance criteria: push wired with backpressure, or plan scopes SSE to poll mode.
- Where to implement: `Api/Controllers/ProcessingController.cs`, `Infrastructure/Redis/`, `deploy/k8s/` (Redis HA)
- Depends on: none

### [GAP-032] No repo-wide form harness; per-screen 17-state UX matrix unproven
- Plan ref: B-10.8 + B-11.4
- Status: PARTIAL
- Priority: P2 (polish/consistency)
- Current state: patterns DONE for wizard/translation/settings (`wizard/{validation,draft,serverErrors,wizardStore}`, `useDirtyGuard` + `beforeunload` + dialog, `PreferencesForm`); `*Matrix.test.tsx` per feature assert loading/empty/success/failure/disabled/denied; MSW taxonomy 401/403/404/409/429/500/partial/stale (`mocks/taxonomy.ts`); `RequireAuth/SessionSkeleton/Forbidden/NotFound/LoggedOut` + `ChunkErrorBoundary`.
- Missing: proof every form implements all 8 form states and every screen its relevant 17 UX states; `prefers-reduced-motion` + SR-spam-guard for live progress unproven by grep.
- Acceptance criteria: state-matrix register per screen (or sampled audit) green; motion/SR guards tested.
- Where to implement: `frontend/src/features/**`, `frontend/src/components/ErrorState/`, `e2e/a11y/`
- Depends on: GAP-023 (states differ by breakpoint)

### [GAP-033] Component/token inventory gaps
- Plan ref: B-11.2 + B-11.3
- Status: PARTIAL (MISSING pieces)
- Priority: P2
- Current state: 34 primitives in `components/index.ts` barrel, each with `.stories.tsx+.test.tsx`; tokens single-source (`styles/tokens.css:6-150`, token-backed tailwind, `check-no-hex` gate, dark theme, 8 status pairs).
- Missing: `SplitPane/ResizablePanel`; `ProgressRing` named `Ring` (rename or alias); `PipelineStepper/StageProgress/SpeakerBadge/ArtifactPanel/ProviderExecutionPanel` exact names (equivalents `BacklogCard/StatCard/QualityIssue/SpeakerList` exist — alias or rename); `--icon-size/--control-height/--motion-*` tokens (elevation only via `--shadow-*`).
- Acceptance criteria: inventory checklist maps every plan name to a component (exact or documented alias); tokens cover icon/control/motion.
- Where to implement: `frontend/src/components/`, `frontend/src/styles/tokens.css`, `frontend/tailwind.config.ts`
- Depends on: none

### [GAP-034] Backend frontend-supporting metrics + telemetry field/analytics verb drift
- Plan ref: B-14.1–14.3
- Status: PARTIAL
- Priority: P2 (observability completeness)
- Current state: base meters DONE (`PlatformMetrics.cs`, `StorageMeters`, `MessagingMeters.{DlqDepth,SchemaMismatches,CrossTenantRejects}`, `QuotaMeters`, `deploy/observability/{alerts.yml,dashboards/}`, `slos.md`); redaction DONE (`telemetry/{telemetry,scrub}.ts`, `assertAllowlisted`, kill-switch `VITE_TELEMETRY_ENABLED` + opt-out); analytics separated (`analytics.ts` 17-event allowlist) + `VITE_ENABLE_ANALYTICS`.
- Missing: named meters for SSE active/reconnect, notification-gen failure, read-model latency, upload funnel, review/export/preview latency (`grep SseActive` → zero); explicit `browser/OS/environment` on telemetry events; `export.created/downloaded` vs impl `export.requested` (+ extras `upload.progress/review.commented`) — confirm or rename.
- Acceptance criteria: dashboards cover the 8 supporting metrics or plan scopes them out; analytics verbs match plan or plan errata.
- Where to implement: `Infrastructure/Observability/`, `deploy/observability/dashboards/`, `frontend/src/telemetry/`
- Depends on: none

### [GAP-035] No global offline banner (per-feature only)
- Plan ref: B-12-nav (UX states incl. Offline)
- Status: MISSING (partial coverage exists)
- Priority: P2
- Current state: offline handled per-feature (`isPreferenceOfflineError`, `settingsMatrix2.test.tsx:375`, `ChunkErrorBoundary` chunk/offline); `grep navigator.onLine` global shell → zero.
- Missing: shell-level `navigator.onLine` banner + queue/retry posture.
- Acceptance criteria: shell shows offline state; per-feature handlers remain; test pins banner.
- Where to implement: `frontend/src/app/layouts/AppShell.tsx`, shell store
- Depends on: none

### [GAP-036] UNVERIFIABLE records (manual/CI checks needed — no code change unless they fail)
- Plan ref: A-§1/§2 Validation; B-23.5/24; B-10.10/15.9
- Status: UNVERIFIABLE
- Priority: P1 (release gates)
- Current state / needed check:
  (a) Full-sln clean `dotnet build --nologo -v minimal` exit 0 + warnings count (must be 0) — timed out twice in audit env (>120 s cold restore); incremental build clean.
  (b) Ephemeral-PG migration apply + snake_case table names + unique-constraint probes (duplicate-active-run rejected, `(TenantId,ContentHash)` dedup) — code-level DONE (7 migrations, `UseSnakeCaseNamingConvention`, partial uniques, RLS 53 tables), never executed here.
  (c) Full-journey green run (harness smoke `tests/cross-layer/harness.spec.ts` + product smoke) filed as record — legs exist, no cited green run.
  (d) `deploy/backup/policy.json` `CHANGE_ME` contacts replaced; rollback rehearsal (`rehearse-rollback.sh`, `rollback-rehearsal.json`) vs production record confirmed; a11y waiver register (`e2e/a11y/support/waivers.ts`) + full WCAG 2.2 AA audit artifact; perf budget sign-off (`e2e/perf/support/budgets.ts`).
- Acceptance criteria: CI/release checklist records (a)–(d) with logs.
- Where to implement: `.github/workflows/` (integration job), `deploy/`, `docs/dr/drill-log.md`, `docs/operations/load-log.md`
- Depends on: GAP-018 (media-bomb evidence feeds the same gate)

## 4. Appendix
Full traceability (ID | requirement | status | evidence) lives in `audit/traceability.csv` — 480 rows (DONE 425 | PARTIAL 50 | DEVIATED 3 | UNVERIFIABLE 2). Compact roll-up below; DONE items appear only in the CSV + this table, not in main sections.

| Area | IDs | DONE | Gaps (in main sections) |
|---|---|---|---|
| A-01 scaffolding | A-01.1–A-01.24, V1–V4 | 27 | GAP-036a (V1 unverifiable clean-build log) |
| A-02 domain/schema | A-02.1–A-02.33, V1–V4 | 34 | GAP-010 (A-02.5), GAP-015 (A-02.15), GAP-004 (A-02.23), GAP-036b (V4 live) |
| A-03 cross-cutting | A-03.1–A-03.24, V1–V4 | 27 | GAP-002 (options validation case) |
| A-04 orchestration | A-04.01–A-04.63 | 60 | GAP-003 (A-04.37), GAP-013 (A-04.60) |
| A-05 artifacts | A-05.01–A-05.33 | 30 | GAP-014 (A-05.03/14), GAP-019 (A-05.33) |
| A-06 providers | A-06.1–A-06.17, V1–V8 | 20 | GAP-011 (A-06.9/10), GAP-006 (A-06.6), GAP-012 (A-06.16) |
| A-07 API foundation | A-07.1–A-07.10, V1–V7 | 16 | GAP-026 (A-07.6 routes) |
| A-08 uploads/ingest | A-08.1–A-08.12, V1–V7 | 18 | none (tus addressed as ambiguity) |
| A-09–A-23 pipeline | A-09.x–A-23.x | ~85 | GAP-009, GAP-025/008, GAP-030, GAP-029, GAP-017 |
| A-24 progress/cancel/retry/review | A-24.1–A-24.8 | 7 | GAP-031 |
| A-25 cost/quota | A-25.1–A-25.8 | 6 | GAP-005, GAP-016 |
| A-26 security/privacy | A-26.1–A-26.9 | 9 | none (cloning-flag claim refuted; see §2) |
| A-27 observability | A-27.1–A-27.8 | 8 | none |
| A-28/30 testing/HA-DR | A-28.1–A-28.6, A-30.1–A-30.5 | 9 | GAP-018, GAP-036d |
| A-29 K8s/KEDA/GPU/CI | A-29.1–A-29.6 | 6 | none |
| A-31/32 enrichment/local AI | A-31.1–A-31.4, A-32.1–A-32.5 | 9 | none |
| A-FV final verification | A-FV.1–A-FV.8 | 8 | inherits above |
| B-8 backend extensions | B-8.1.1–B-8.9 | 10 | GAP-027, GAP-020 |
| B-9 API/SSE/errors | B-9.1–B-9.12 | 9 | GAP-001, GAP-021, GAP-028 |
| B-10 frontend arch | B-10.1–B-10.11 | 6 | GAP-022, GAP-032, GAP-034 |
| B-11 UX system | B-11.1–B-11.6 | 2 | GAP-023, GAP-033 |
| B-12 workflows | B-12.1–B-12.19, nav | 15 | GAP-007, GAP-008, GAP-024, GAP-035 |
| B-13/14 security/observability | B-13.1–B-13.8, B-14.1–B-14.4 | 9 | GAP-034 |
| B-15–25 test/CI/deploy/ops | B-15.1–B-25 | ~20 | GAP-023, GAP-024, GAP-036c |
| Audit stopped at: complete (all plan sections covered via 8 parallel section audits + targeted re-verification of cloning-flag, lease-version, tus, crossfade, worker-health, SSE token, i18n baseline, responsive utilities, reviewThreshold, /me timezone, quota caps). |
