# fix_notes.md — P0 gap batch (GAP-001,002,003,004,005,006,007,009)

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
