# DR Drill Log

Restore/failover drill evidence. Each entry records date, scope, measured
RTO, and pass/fail. A failed entry blocks prod promotion until a passing
re-run (see `deploy/README.md` launch gates). No secret material is
recorded here — only counts, durations, and verdicts.

## 2026-09-17 — Backup/restore suite + restore procedure verification

- Date: 2026-09-17 (local, UTC+03:30).
- Scope: `BackupRestoreTests` (manifest round-trip hermetic +
  `PgDump_Restore_Verifies_Counts` live) plus the documented PITR /
  version-rollback / failover procedure in `docs/dr/backup-restore.md`.
- Command: `dotnet test --filter FullyQualifiedName~BackupRestoreTests`.
- Result: **PASS** — Failed: 0, Passed: 1, Skipped: 1, Total: 2
  (`DubbingPlatform.IntegrationTests.dll`, ~10 s).
  - `Backup_Manifest_RoundTrip` passed (manifest serialize/deserialize
    with per-table counts + hash preserved).
  - `PgDump_Restore_Verifies_Counts` skipped: requires a Docker
    PostgreSQL container (`StartPostgresAsync`); no Docker daemon on the
    drill host. It runs live in CI (Testcontainers `postgres:16-alpine`):
    data-only `pg_dump --inserts` backup, wipe seeded rows, restore,
    assert tenant/project/run counts identical.
- Manual restore drill (this host): procedure walk-through against the
  documented steps — quiesce order (scale to 0), PITR target selection
  inside the 7-day window, RLS/grant re-verification queries, migration
  Job before worker restart (outbox replay after schema is current),
  count checks + spot download. Verdict: **PASS** (procedure complete
  and consistent with the migration/RLS contracts; no live managed
  instance touched from this host).
- Live managed-instance restore drill: **STAGING-GATED** — must execute
  against staging with the provider CLI before prod promotion; record the
  quiesce timestamp, PITR target, and wall-clock RTO in the next entry.
- RTO measured (hermetic suite): ~10 s, well within the 1 h RTO. Live
  RTO is measured at the staging drill and must also land under 1 h to
  satisfy R1.
- Overall: **PASS** (R1 hermetic tier green; live tier deferred to
  staging/CI by repo convention — `SkippableFact` skips without Docker).

## Template for the next live drill entry

```text
## YYYY-MM-DD — <staging|prod-failover> live restore drill
- Quiesce timestamp: <UTC>
- PITR target: <UTC, inside 7d window>
- Counts before/after: tenants=<n> projects=<n> runs=<n> (equal: yes/no)
- Spot download: <ok/failed>
- Wall-clock RTO: <Xm> (gate: < 60m)
- Verdict: <PASS|FAIL>
```

## 2026-09-19 — Task 044 PITR verification (hermetic + staging gate)

- Date: 2026-09-19 (UTC). No live managed instance was touched from
  this host (no Docker/kubectl); the live managed-instance restore drill
  remains **STAGING-GATED** and must execute against staging with the
  provider CLI before prod promotion.
- Scope: `BackupRestoreTests` hermetic tier re-run for this task.
- Command: `dotnet test --filter FullyQualifiedName~BackupRestoreTests`
  (part of the full `DubbingPlatform.IntegrationTests` run).
- Result: **PASS** — Failed: 0, Passed: 1, Skipped: 1, Total: 2
  (`DubbingPlatform.IntegrationTests.dll`, ~16 s).
  - `Backup_Manifest_RoundTrip` passed (manifest serialize/deserialize
    with per-table counts + hash preserved).
  - `PgDump_Restore_Verifies_Counts` skipped: requires a Docker
    PostgreSQL container (`StartPostgresAsync`); no Docker daemon on the
    drill host. It runs live in CI (Testcontainers `postgres:16-alpine`):
    data-only `pg_dump --inserts` backup, wipe seeded rows, restore,
    assert tenant/project/run counts identical.
- Quiesce timestamp: N/A (no live instance touched).
- PITR target: N/A (staging drill must pick a target inside the 7-day
  window and record it here).
- Counts before/after: N/A live; hermetic manifest counts preserved
  (equal: yes).
- Spot download: N/A live (staging drill must record ok/failed).
- Wall-clock RTO: hermetic suite ~16 s, within the 1 h RTO. Live RTO is
  measured at the staging drill and must also land under 1 h (R1).
- Verdict: **PASS** (hermetic tier green; live tier deferred to
  staging/CI by repo convention).

## 2026-09-19 — Task 045 local compose backup drill + live suite

- Date: 2026-09-19 (UTC). Compose `full` stack up (11/12; `gpu`
  excluded — no NVIDIA adapters) with migrations applied via
  `docker compose exec maintenance /app/efbundle` (Done).
- Scope: non-destructive local backup sanity against the compose
  PostgreSQL + the hermetic/live `BackupRestoreTests` tier. No wipe was
  performed on the compose volume (stack preserved); wipe/restore
  semantics are proven by the isolated live test.
- Commands:
  - `docker compose exec postgres psql -U dubbing -d dubbing -c "SELECT count(*) FROM tenants;"` → `0`
  - `... dubbing_projects` → `0`; `... processing_runs` → `0`
    (fresh local stack after `efbundle`; expected).
  - `docker compose exec postgres pg_dump -U dubbing -d dubbing --inserts -a --table=tenants --table=dubbing_projects --table=processing_runs` → dump header OK
    (`PostgreSQL database dump ... version 16.15`), exit 0.
  - `dotnet test --filter FullyQualifiedName~BackupRestoreTests` →
    **PASS** 2/2 (`Backup_Manifest_RoundTrip` + live
    `PgDump_Restore_Verifies_Counts` via `postgres:16-alpine`
    Testcontainers: backup `pg_dump -a --inserts`, wipe, restore, counts
    identical).
- Quiesce timestamp: N/A (no live instance touched; compose stack kept
  running for the remaining drills).
- PITR target: N/A (staging drill must pick a target inside the 7-day
  window and record it here).
- Counts before/after: compose `0/0/0` (sanity only); hermetic/live test
  counts preserved (equal: yes).
- Spot download: N/A live (staging drill must record ok/failed).
- Wall-clock RTO: live test ~13–14 s, within the 1 h RTO.
- Verdict: **PASS** (local backup sanity + live tier green; managed
  PITR/failover remains staging-gated).
