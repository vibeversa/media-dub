# 041 — Production Hardening HA DR Report

## Status
COMPLETED

## Summary
Delivered the full Task 41 hardening slice as docs plus executed drill
evidence: `docs/operations/ha-topology.md` (managed-data-plane HA table
plus the real replica/PDB/KEDA counts from `deploy/k8s/`), `docs/dr/
backup-restore.md` (RPO 5 min / RTO 1 h, PITR + version-rollback +
failover/failback procedures with outbox-after-schema ordering and
fencing), `docs/dr/drill-log.md` (2026-09-17 entry, hermetic PASS, RTO
~10 s), `docs/operations/chaos-log.md` (RecoveryTests 9/9 hermetic PASS
plus a 6-row staging-gated live-kill matrix), `docs/operations/load-log.
md` (LoadTests 3/3, MediaBombTests 5/5, QuotaTests 5/2-skip, soak
template), `docs/operations/migration-compat.md` (expand/contract rule +
compat pins), `docs/operations/support-access.md` (Service/TenantAdmin
diagnostics + dashboard/escalation links), `docs/operations/rotation-
drill-log.md` (rotation drill PASS), and a 5-gate staging→prod section in
`deploy/README.md`. No C# or manifest changes; validation suites all
green.

## Files Created/Modified
- `docs/operations/ha-topology.md` — new: managed PG multi-AZ + 7d PITR, storage versioning + CRR prod + 90d finals, Rabbit quorum queues, clustered Redis (ephemeral-only), replica table (api 3, control 2, media-prep 3, media-render 2, ai 3, export 2, gpu 0–4, maintenance singleton), PDBs minAvailable 1, RollingUpdate maxUnavailable 0, broker-down fallback.
- `docs/dr/backup-restore.md` — new: schedules (daily base + WAL 5 min, versioning/lifecycle 90/30d, audit 365d immutable), RPO/RTO statements, `pg_restore --target-time` + RLS/grant re-verification + count/spot-download steps, storage version rollback, PG/broker failover-failback, active-writes ordering, split-brain fencing, drill-fail blocks promotion.
- `docs/dr/drill-log.md` — new: 2026-09-17 BackupRestoreTests entry (1 passed/1 skipped, RTO ~10 s, PASS) + live-drill entry template.
- `docs/operations/chaos-log.md` — new: 2026-09-17 RecoveryTests entry (9 passed/2 skipped, PASS, R2) + C1–C6 staging-gated live-kill matrix (broker/storage/DB kills, kill -9, lease-loss, WireMock 429).
- `docs/operations/load-log.md` — new: 2026-09-17 LoadTests entry (3/3, 200-seg + 5-project + 50/50 fail-closed numbers), 2000-seg/2 h-media staging soak template + scaling argument, media-bomb section (MediaBombTests 5/5 + QuotaTests 5 passed, RESOURCE_EXHAUSTED + QUOTA_EXHAUSTED fail-closed, fixture positive control).
- `docs/operations/migration-compat.md` — new: additive-only expand (N) / contract (N+1) rule, initContainer + PDB + maxUnavailable-0 window mechanics, code-only rollback, MigrationCompatTests entry (1 passed/1 skipped, PASS, R5).
- `docs/operations/support-access.md` — new: RequireTenantAdmin = Service/TenantAdmin only (401/403/200 cases), dashboard table (5 Grafana JSONs + /metrics), escalation L1/L2/L3 + runbook links.
- `docs/operations/rotation-drill-log.md` — new: 2026-09-17 rotation drill per `docs/security/secret-rotation.md` (Unit 4/4 + Integration 11/3-skip, PASS, R5; live rotation staging-gated via secret manager + audit note).
- `deploy/README.md` — modified: added `## Launch gates (staging -> prod promotion)` (staging green + restore pass + chaos pass + rotation pass + SLOs 7d, plus compat + bomb prerequisites; any failed drill blocks promotion).

## Decisions Made
- **Docs-only, no C# or manifest edits**: the task's Scope/Instructions list docs, drill execution, verification, and gates — all test tiers from Task 39 already exist and pass. Editing `deploy/k8s/` YAML without kubectl to dry-run would risk breaking Task 40's verified contracts for zero task-required benefit.
- **AZ spread documented as managed-node-pool assumption, not new affinity rules**: same reasoning — manifests stay byte-identical to the Task 40 verified state; the topology doc states the ≥2-AZ pool requirement explicitly so it is auditable.
- **Live tiers (Docker containers, cluster kills, managed PITR, live credential rotation, 2000-seg soak) recorded as STAGING-GATED, not faked**: this host has no Docker daemon (`docker: not recognized`) and no cluster tooling, so `SkippableFact` live tests skip by repo convention. Each log marks exactly what ran hermetic (PASS with counts) vs what staging/CI must run, with the exact command and a fill-in template. Inventing RTO numbers for restores never executed would be fabrication.
- **Bomb evidence split across two suites**: `RESOURCE_EXHAUSTED` via `MediaBombTests` (DiskSpaceChecker/MediaValidator) and `QUOTA_EXHAUSTED` (429) via `QuotaTests` + the throttled-load test — matches how the codebase separates disk exhaustion (`ErrorCodes.ResourceExhausted` → 503) from budget exhaustion (`QuotaExceededException` → 429).
- **Rotation drill log as its own file** (`rotation-drill-log.md`) rather than folding into `drill-log.md`: the task assigns no filename; a dedicated auditable record linked from the launch gates keeps restore vs rotation evidence independently gateable.

## Build/Test Results
- `dotnet build --nologo -v q` (last 4):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:12.29
```
- `dotnet test --filter FullyQualifiedName~BackupRestoreTests` (last 1):
```
Passed!  - Failed:     0, Passed:     1, Skipped:     1, Total:     2, Duration: 10 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~RecoveryTests` (last 1):
```
Passed!  - Failed:     0, Passed:     9, Skipped:     2, Total:    11, Duration: 13 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~MigrationCompatTests` (last 1):
```
Passed!  - Failed:     0, Passed:     1, Skipped:     1, Total:     2, Duration: 10 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- Extra (bomb/support evidence): `LoadTests` 3/3 passed (~1 s); `MediaBombTests` 5/5 passed (~260 ms); `QuotaTests` 5 passed/2 skipped (~11 s); `SecretRedactionTests|TenantIsolationTests|ObservabilityTests` Unit 4/4 passed + Integration 11 passed/3 skipped (~48 s). All skips are Docker-gated `SkippableFact`s (no daemon on host; live in CI).
- `docs/dr/drill-log.md` readable (first 10 lines verified via read; file renders with the 2026-09-17 entry + next-drill template).

## Recommendations for Next Agent (042)
- Repo state: builds 0/0 (TreatWarningsAsErrors). Task 41 added NO code — 8 new markdown docs (`docs/operations/ha-topology.md`, `chaos-log.md`, `load-log.md`, `migration-compat.md`, `support-access.md`, `rotation-drill-log.md`, `docs/dr/backup-restore.md`, `drill-log.md`) + one `deploy/README.md` section. `deploy/k8s/` manifests are byte-identical to Task 40; `deploy/verify.sh` still the local gate. Not a git repo. No Docker/kubectl/helm on this host — same skips apply.
- Key files for 042 (optional video/lipsync enrichment, gated disabled): pipeline tip is `src/DubbingPlatform.Workers/Consumers/RenderWorker.cs` (final render) + `src/DubbingPlatform.Application/Options/FeatureOptions.cs` (feature-flag pattern — check `SectionName = "Features"` and how prior optionals are gated), `src/DubbingPlatform.Domain/Enums/FailureCategory.cs` (isolated-failure category for enrichment), `deploy/k8s/workers-gpu.yaml` (0-base GPU pool 042 may reuse), `deploy/k8s/workers-media-render.yaml` (render worker it plugs into). Enrichment must fail isolated (never block core audio path) per the dependency overview.
- Gotchas: NEVER run two `dotnet test` concurrently (MSBuild/testhost contention). 042 must default the enrichment flag OFF and keep `dotnet build` 0/0. Do not touch `docs/dr/` or `docs/operations/` filenames from 041 — link to them if needed (launch gates reference them). Keep `CHANGE_ME` placeholders intact.
- Conventions: error codes via `src/DubbingPlatform.Application/Errors/ErrorCodes.cs` + `ErrorCodeException` (never raw HTTP codes in services); options classes with `SectionName` const + `IValidateOptions<>` fail-fast validator; queue names frozen in `Contracts/Messages/QueueNames.cs`; pod security `runAsUser: 1654` + readOnlyRootFilesystem.
- Config keys likely for 042: `Features__*` (new flag, default false), `LocalInference__Device` (already `cuda` on gpu worker), `Media__MaxConcurrentMediaJobs=2` (media pods — enrichment must not bypass this bound).
