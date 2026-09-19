# Backup and Restore

## Objectives

- **RPO 5 min** for the PostgreSQL system of record (WAL granularity).
- **RTO 1 h** for core service (API + control plane + media pipeline
  serving traffic again after a declared disaster).
- Audit trail retained **365 days**, immutable
  (`RetentionOptions.AuditDays = 365`; `REVOKE UPDATE, DELETE ON
  audit_events FROM app_role` in
  `src/DubbingPlatform.Infrastructure/Persistence/Sql/audit_appendonly.sql` —
  only the `BYPASSRLS` maintenance role deletes after `AuditDays`).

## Schedules

| Data | Schedule | Mechanism |
|---|---|---|
| PostgreSQL base backup | Automated daily (managed service) | Full snapshot, encrypted at rest |
| PostgreSQL WAL | Continuous archiving, 5 min granularity, 7-day PITR window | Point-in-time recovery to any timestamp in the window |
| Object storage finals | Versioning ON (prod) + CRR | Every overwrite creates a version; lifecycle keeps finals 90 days (`RetentionOptions.FinalDays = 90`), intermediates 30 days (`IntermediateDays = 30`) |
| Audit events | Append-only, 365-day retention | No update/delete grants for `app_role`; retention-owned physical delete only |
| Broker queues | Durable + quorum (no backup) | Recovery is redrive-from-outbox, not queue restore (see below) |

Backups are encrypted at rest by the managed services. Restore access is
Service-role only; every drill action is audited; drill logs must never
contain prod secrets (keys, connection strings, token material).

## Restore: PostgreSQL point-in-time

```bash
# 1. Stop writers: scale API + workers to 0 so no new rows land mid-restore.
kubectl -n dubbing-prod scale deploy/api worker-control worker-ai \
  worker-media-prep worker-media-render worker-export worker-gpu --replicas=0

# 2. Point-in-time restore to a fresh instance (managed-service CLI shape;
#    use the provider's documented flags — timestamp must be inside the 7d window).
pg_restore --target-time="2026-09-17T06:00:00Z" --source=prod --target=prod-restored

# 3. Verify RLS + append-only grants survived on the new primary.
psql "$RESTORED" -c "SELECT count(*) FROM pg_policies WHERE schemaname='public';"
psql "$RESTORED" -c "SELECT grantee, privilege_type FROM information_schema.role_table_grants WHERE table_name='audit_events';"

# 4. Repoint the secret-manager connection string at the restored instance,
#    then run the migration Job (forward-only; never roll the schema back).
kubectl apply -f deploy/k8s/migration-job.yaml
kubectl -n dubbing-prod wait --for=condition=complete --timeout=600s job/dubbing-migration

# 5. Replay the outbox AFTER the schema is current: MassTransit redrives
#    `outbox_message` on boot, so start workers only now (order matters —
#    replaying against a stale schema double-applies or misroutes).
kubectl -n dubbing-prod rollout restart deploy/worker-control
kubectl -n dubbing-prod scale deploy/api worker-ai worker-media-prep \
  worker-media-render worker-export --replicas=<per topology>

# 6. Verify counts + spot download.
psql "$RESTORED" -c "SELECT count(*) FROM tenants; SELECT count(*) FROM dubbing_projects; SELECT count(*) FROM processing_runs;"
curl -f -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/exports/<id>/download" -o /tmp/spot-check.bin
```

Count verification mirrors the hermetic/live gate in
`BackupRestoreTests` (`PgDump_Restore_Verifies_Counts`): backup with
`pg_dump -a --inserts`, wipe, restore, assert tenant/project/run counts
are identical before and after.

## Restore: object storage version rollback

```bash
# List versions of an affected key, then promote the last-known-good version.
aws s3api list-object-versions --bucket dubbing-prod --prefix "<tenant>/<run>/"
aws s3api get-object --bucket dubbing-prod --key "<key>" \
  --version-id "<good-version>" /tmp/rollback-check.bin
# Copy the good version over the tip (never delete versions during a drill).
aws s3 cp /tmp/rollback-check.bin s3://dubbing-prod/<key>
```

Verify with a spot download through the export endpoint (step 6 above)
plus artifact lineage (`artifact_parents`) to enumerate affected runs.

## Failover / failback

PostgreSQL (managed):

1. Declare: `/health/ready` 503 with the PostgreSQL check failing for
   > 5 min (runbook `docs/runbooks/db-failover.md`).
2. Promote the standby per the managed-service runbook (RPO 5 min).
   Fencing is by primary election — the old primary is fenced by the
   provider so it cannot accept writes (no split-brain: workers pause
   anyway because readiness fails closed while the connection string
   points at a fenced endpoint).
3. Verify `app_role` grants + `REVOKE UPDATE,DELETE ON audit_events` on
   the new primary (step 3 queries above).
4. Replay outbox on boot, run the migration Job, scale workloads back.
5. Failback (if returning to the original region): treat as a fresh
   PITR restore onto the original instance, then repeat steps 3–4. Never
   re-attach a fenced primary as writable.

Broker (managed RabbitMQ):

1. Failover is DNS/endpoint flip to the mirrored standby; quorum queues
   keep a majority of replicas so no messages are lost.
2. Workers pause via readiness failure while the broker is unreachable;
   the MassTransit outbox buffers publishes and redrives in order on
   reconnect (`RecoveryTests.BrokerOutage_Buffers_Then_Redrives_In_Order`).
3. After flip, confirm queue depths drain (`queues.json` dashboard) and
   `dlq.depth` stays 0 before declaring recovery.

## Restore with active writes

Always PITR to a fixed timestamp first, then replay the outbox after the
schema is current (steps above). Restoring under live writes without
quiescing re-introduces the rows the restore just removed and breaks the
count verification. The drill log (`docs/dr/drill-log.md`) records the
quiesce timestamp, the PITR target, and the measured RTO.

## Drill failure policy

A failed drill blocks prod promotion (see `deploy/README.md` launch
gates). File the failure with metric links, fix forward, and re-run the
full drill — partial re-runs do not satisfy the gate.
