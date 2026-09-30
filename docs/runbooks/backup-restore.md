# Backup and restore

Task 043, instruction 8. **This is the operations-facing index and the drill
procedure.** The mechanisms, retention numbers and PITR commands live in
[`../dr/backup-restore.md`](../dr/backup-restore.md), which is the reference for
*how*; this page is *what is covered, how the drill is run, and what a gap does*.

## Coverage

Every table in this list is a table whose loss is a loss of user data. A row
marked "gap" is a release blocker.

| Data | Mechanism | Schedule | RPO | Restorable? |
| --- | --- | --- | --- | --- |
| `tenants`, `dubbing_projects` | PostgreSQL base backup + WAL | daily + continuous 5-min | 5 min | yes |
| `tenant_users`, `project_memberships` | PostgreSQL | as above | 5 min | yes |
| `user_preferences` | PostgreSQL | as above | 5 min | yes |
| `notifications` | PostgreSQL | as above | 5 min | yes |
| `activity_events` | PostgreSQL, append-only | as above | 5 min | yes |
| `cost_reservations`, `provider_executions` | PostgreSQL | as above | 5 min | yes |
| `run_stage_summaries`, `quality_results` | PostgreSQL | as above | 5 min | yes |
| `voice_profiles`, `speaker_voice_assignments` | PostgreSQL | as above | 5 min | yes |
| `artifact_parents` (lineage) | PostgreSQL | as above | 5 min | yes |
| `voice_preview_jobs` (metadata) | PostgreSQL | as above | 5 min | yes |
| `audit_events` | PostgreSQL, append-only, 365-day retention | as above | 5 min | yes |
| **media assets, previews, exports, output** | **object storage versioning + CRR** | continuous, 90-day finals | **15 min (version granularity)** | yes, by version |
| **uploaded source media** | **object storage** | as above | 15 min | yes, by version |

The two rows in bold are not in PostgreSQL, and that is the single most important
fact on this page. A database restore does not bring back a media file, and a
media restore does not bring back the row that describes it. The restore drill
verifies **both halves against each other**, which is why a drill that only
restores the database is a drill that has verified half the system.

## Targets

| Target | Value | Measured by |
| --- | --- | --- |
| RPO (PostgreSQL) | 5 min | WAL archive granularity |
| RPO (object storage) | 15 min | cross-region replication interval |
| RTO (core service serving again) | 1 h | drill log, `docs/dr/drill-log.md` |
| Restore drill cadence | **quarterly** | `docs/dr/drill-log.md` |

A drill that misses RTO is a failed drill. It does not become a passed drill with
a note.

## The drill

Run quarterly. Log the result in [`../dr/drill-log.md`](../dr/drill-log.md) with
the timestamps, the measured RTO, and any gap found. The full command sequence is
[`../dr/backup-restore.md`](../dr/backup-restore.md#restore-postgresql-point-in-time);
this is the checklist the drill is scored against.

### 0. Before touching anything

```bash
# Access is time-boxed and audited. Record who, when, and for which window,
# BEFORE the first restore command - not after.
#   - who is running it
#   - the declared window (start + expected end)
#   - the target instance
#   - confirmation that drill logs will not contain prod secrets
```

A drill that runs against production without a recorded access window is an
unauthorised production change, and the audit log will show it as one.

### 1. Quiesce

```bash
kubectl -n dubbing-prod scale deploy/api worker-control worker-ai \
  worker-media-prep worker-media-render worker-export worker-gpu --replicas=0
```

Record the quiesce timestamp. It is the point from which RTO is measured, and it
is the PITR target's upper bound — a restore to a time after quiesce reintroduces
writes that the verification will then fail on.

### 2. Restore PostgreSQL to a **fresh** instance

Never onto the primary. A drill that restores over the live database has no
rollback if the restore is wrong, and the counts it then verifies are the counts
it just wrote.

Full commands: [`../dr/backup-restore.md`](../dr/backup-restore.md#restore-postgresql-point-in-time).

### 3. Verify the schema, not just the rows

```bash
psql "$RESTORED" -c "SELECT count(*) FROM pg_policies WHERE schemaname='public';"
psql "$RESTORED" -c "SELECT grantee, privilege_type FROM information_schema.role_table_grants WHERE table_name='audit_events';"
```

These two are the ones that matter most and the ones most often skipped. A
restore that brought back the rows but not the **RLS policies** has restored data
that the application role can now read across tenants; a restore that brought
back `UPDATE`/`DELETE` on `audit_events` has restored an audit trail that
history can be edited out of. Both are silent, both are severe, and neither is
visible in a row count.

### 4. Run the migration, forward-only

```bash
kubectl apply -f deploy/k8s/migration-job.yaml
kubectl -n dubbing-prod wait --for=condition=complete --timeout=600s job/dubbing-migration
```

Forward-only, always. A restore to a point in time lands on a schema from that
moment, and the current code needs migrations applied on top. See
[`rollback.md`](rollback.md#the-database-never).

### 5. Verify counts **and** the media half

```bash
psql "$RESTORED" -c "SELECT count(*) FROM tenants; SELECT count(*) FROM dubbing_projects; SELECT count(*) FROM processing_runs;"

# The media half. A database restore with a healthy count and missing files is
# the failure this step exists to catch.
aws s3api list-object-versions --bucket dubbing-prod --prefix "$PREFIX" | head
curl -f -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/exports/$EXPORT_ID/download" -o /tmp/spot-check.bin
```

Pick the export id from the **restored** database, not from before the drill.
Downloading a file the old instance could serve verifies nothing.

### 6. Replay the outbox, then scale back

Order matters: the MassTransit outbox is replayed on worker boot, and replaying
against a stale schema double-applies or misroutes.

```bash
kubectl -n dubbing-prod rollout restart deploy/worker-control
kubectl -n dubbing-prod scale deploy/api worker-ai worker-media-prep \
  worker-media-render worker-export --replicas=<per topology>
```

### 7. Measure and record

```bash
date -u +%Y-%m-%dT%H:%M:%SZ   # drill complete
```

RTO is complete-minus-quiesce. Write the drill log entry **with the number**,
including when it missed.

## A gap found by a drill is a release blocker

The task is explicit, and the reasoning is worth stating: a restore drill exists
to discover what a restore *cannot* recover. A gap that is filed and deferred is
not a gap that was found, it is a gap that has been documented and will be
rediscovered during an actual incident, with less time.

1. Log it in `docs/dr/drill-log.md` with what could not be restored.
2. File an issue, owned, with a date.
3. **Block promotion** until fixed or until the coverage row above is amended to
   say what is and is not recoverable. The launch gates in
   [`../../deploy/README.md`](../../deploy/README.md) already require a passing
   drill; a passing drill *with a recorded gap* does not satisfy that gate.
4. Amend the coverage table on this page. A table that says "restorable: yes" for
   something the drill could not restore is worse than no table.

## Encryption and access

- Backups are **encrypted at rest** by the managed services. A backup that is not
  encrypted is a full copy of the database in whatever store holds it.
- Restore access is **service-role only**, and every drill action is audited.
- **Drill logs must never contain prod secrets**: no keys, no connection
  strings, no token material. The counts, the timestamps and the RTO are the
  whole of what a drill log needs.
- Object-storage media is covered by the same encryption; versioning is what makes
  a single overwritten key recoverable, and cross-region replication is what
  makes it recoverable in the other region.

## The known weak point

`RetentionOptions.FinalDays = 90` means finals older than 90 days are gone. A
restore to a PITR point inside the 7-day WAL window can therefore find rows whose
media has aged out — the row is restored and the file is not. This is a
**documented** consequence of the retention policy, not a defect, and the drill's
job is to confirm the boundary is where it is documented to be. If a drill finds
a gap *wider* than this, the retention policy has changed without this page being
updated, and that is the finding.
