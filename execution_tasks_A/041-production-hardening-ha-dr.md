# Task 41 — Production Hardening HA DR

## Goal

Complete HA topology, backup/restore, RPO/RTO, chaos/load/media-bomb/rotation drills, zero-downtime migrations, and support access controls.

## Context

Binding: HA via managed deps. Backups DB PITR + storage versioning/lifecycle + audit retention (365d). RPO 5min DB, RTO 1h core. Restore/failover/failback tested. Chaos DB/broker/storage/provider/worker-crash/lease-loss/partition. Load large-segments/concurrent-projects/large-media/throttling. Media-bomb protection. Rotation drills. Incident runbooks + on-call dashboards. Zero-downtime expand/contract + old/new compat window. Support diagnostics access controls (Service/TenantAdmin only).

## Starting State

CI/K8s/KEDA/GPU + observability + tests (incl. recovery/load/backup/compat suites from Task 39) exist. No HA/DR docs, no RPO/RTO configs, no chaos/load execution records, no rotation drill evidence.

## Scope

Must implement: HA/DR docs, backup schedules, RPO/RTO verification, chaos/load execution, bomb protection verification, rotation drill, support access verification. Must not implement: optional enrichment.

## Instructions

1. Create `docs/operations/ha-topology.md`: managed PG HA (multi-AZ + PITR 7d), managed object storage versioning + cross-region replication (prod), managed Rabbit quorum queues + mirrored, managed Redis clustered, API/workers multi-replica multi-AZ with PDBs + KEDA.
2. Create `docs/dr/backup-restore.md`: schedules (PG automated daily + WAL PITR 5min granularity, storage versioning + lifecycle 90d finals, audit 365d immutable), RPO 5min / RTO 1h statements, restore steps (PG point-in-time `pg_restore --target-time`, storage version rollback, verification queries counts + spot download), failover/failback steps for PG/broker.
3. Execute/record: run `BackupRestoreTests` + manual restore drill (document output in `docs/dr/drill-log.md` with date, RTO measured, pass/fail); run chaos suite (`RecoveryTests` + broker/storage kill via compose stop, worker kill -9, lease-loss injection) and record in `docs/operations/chaos-log.md`; run load suite (2000 segments, 5 concurrent projects, 2h media, throttled provider via WireMock 429) record throughput + success in `docs/operations/load-log.md`; run media-bomb tests (oversize + sparse) verify RESOURCE_EXHAUSTED/QUOTA_EXCEEDED; run secret-rotation drill per Task 37 doc, record.
4. Zero-downtime: verify expand/contract (migration adds nullable columns first, code tolerates both for 1 release; destructive drops in later release only) + compat window test (`MigrationCompatTests` must pass); document in `docs/operations/migration-compat.md`.
5. Support access: verify Admin diagnostics require Service/TenantAdmin (reuse ObservabilityTests) + document on-call dashboard links + escalation (link Task 38 runbooks).
6. Update `deploy/README.md` launch gates: staging green + restore drill pass + chaos pass + rotation drill pass + SLOs met 7d before prod.

## Requirements

- R1: RPO 5min / RTO 1h defined + restore drill passes within RTO.
- R2: Chaos recovers (no lost runs; leases recovered).
- R3: Load targets met (document numbers).
- R4: Bomb protection verified.
- R5: Rotation drill succeeds; compat window verified.

## Edge Cases and Error Handling

- Restore with active writes → PITR to timestamp, replay outbox after (document order).
- Failover split-brain → fencing via PG primary election, workers pause via readiness fail (document).
- Drill fail → block prod promotion (gate).

## Security and Safety Requirements

- Backups encrypted at rest; restore access Service-only; audit drill actions; no prod secrets in drill logs.

## Testing

This task executes existing suites + drills: `BackupRestoreTests, RecoveryTests, LoadTests (non-soak), MediaBombTests, MigrationCompatTests`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~BackupRestoreTests
dotnet test --filter FullyQualifiedName~RecoveryTests
dotnet test --filter FullyQualifiedName~MigrationCompatTests
cat docs/dr/drill-log.md
```

## Completion Criteria

- HA/DR docs + drill logs + gates complete; tests pass.

## Traceability

- Plan Section 30; Assumptions 79; Deployment checklist backup/DR/rollback/staging; Tests checklist backup/restore/compat/chaos/load.
