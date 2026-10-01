# Backup: coverage, retention, restore priority

Task 043C. The backup scope for the durable entities Plan B added, what each one
costs to lose, and the recorded result of the restore drill.

**This page is a scope and a record. It is not the procedure.** The mechanisms
and the PITR commands are Plan A's and are not restated here:
[`dr/backup-restore.md`](dr/backup-restore.md) is *how* to restore,
[`runbooks/backup-restore.md`](runbooks/backup-restore.md) is the operations
index and the drill checklist, and [`dr/drill-log.md`](dr/drill-log.md) is the
running log of every drill including the full-system ones. This page answers a
different question: **for the seven new entity groups, what is covered, for how
long, and in what order do I restore it.**

## The single source of truth

[`deploy/backup/scope.json`](../deploy/backup/scope.json) declares the coverage
as data. This page renders it, the drill reads its table list from it, and
[`tools/backup-coverage.test.mjs`](../tools/backup-coverage.test.mjs) fails when
the three drift apart.

That indirection is not ceremony. The drill originally read its table list from
`scope.json` and checked completeness against the same file — and when a group
was removed from `scope.json` as an experiment, the drill compared nine tables
against nine tables, every count matched, and it printed **PASS**. It had
verified slightly less and reported it in exactly the same way. The required set
is now written down a second time, in `scripts/restore-drill.sh`, where a change
to the data file does not also change the check. That failure is gap **D2** below
and it is the reason this section exists.

## Coverage: the seven new entity groups

| # | Group | Table(s) | Restored from | Retention | Restored first? |
| --- | --- | --- | --- | --- | --- |
| 1 | **Tenant users** | `tenant_users` | PostgreSQL | 365 d | **yes** |
| 2 | **Project memberships** | `project_memberships` | PostgreSQL | 365 d | **yes** |
| 2 | **Extended project metadata** | `dubbing_projects` (9 new columns) | PostgreSQL | 365 d | **yes** |
| 3 | **Preferences** | `user_preferences` | PostgreSQL | 365 d | after auth works |
| 4 | **Notifications** | `notifications` | PostgreSQL | 90 d | after login works |
| 5 | **Activity events** | `activity_events` | PostgreSQL | 90 d | last; it is history |
| 6 | **Voice preview jobs** | `voice_preview_jobs` | PostgreSQL | 30 d | last; terminal rows only |

Priority 1–2 are the **restore-order** set, and the reason is not tidiness. A
restore that brings back notifications without memberships produces a product
where every user is signed in, sees a list of notifications, and cannot open the
project any of them point at. A restore that brings back preferences without
users cannot be read by anyone. The three groups at priority 2 are the ones whose
absence produces a **403 that reads as a broken release**: memberships gone means
every non-owner is forbidden; `owner_user_id` gone means the project has no
owner; `settings_version` gone means the settings-lock check has nothing to
compare against.

### The nine extended project-metadata columns

`dubbing_projects` is not a Plan B table — it predates it. What is new is these
nine columns, added by `20260921093301_AddProductIdentityExtensions`:

| Column | Why losing it is not cosmetic |
| --- | --- |
| `name` | the project list renders `null`; the user has a list of unnamed rows |
| `description` | less bad than `name`, still user-visible |
| `is_archived` | archived projects reappear as active |
| `archived_at` | the archive ordering is gone |
| `owner_user_id` | the project has no owner; ownership queries return nothing |
| `created_by_user_id` | audit attribution for the project is lost |
| `updated_by_user_id` | ditto for the last edit |
| `processing_settings_json` | the run's processing configuration reverts to defaults |
| `settings_version` | the optimistic-concurrency check has no counter |

**This group is why the drill asserts columns and not just counts.** A base
backup taken *before* the expand migration restores `dubbing_projects` without
these nine columns — the table exists, the row count matches, and the application
reads a `null` name and an unset owner. A count-only check passes that restore.
The drill's spot-read step selects exactly these columns by name and fails if any
of them is absent or empty, and that is the step that would catch a restore from
a pre-043C archive.

### What is **not** in PostgreSQL

Media assets, previews, exports, uploaded source media and rendered output live in
object storage under versioning with cross-region replication, 90-day retention
on finals and 30 on intermediates. This is the single most important fact on the
page and it is restated from Plan A deliberately:

> **A database restore does not bring back a media file, and a media restore does
> not bring back the row that describes it.**

The drill below is scoped to the new tables and therefore verifies **half** the
system. A project can be restored perfectly and render an empty workspace. The
media half is covered by the full-system drill procedure in
[`runbooks/backup-restore.md`](runbooks/backup-restore.md) §5, and a gap found
there is a release blocker by that page's own rule.

## The restore drill

Re-runnable, and the reason it is a script rather than a checklist is that a
checklist is a document and a script is evidence.

```bash
# Default: starts a disposable postgres:16-alpine, applies nothing, drills what
# is there, tears the server down. Needs a migrated schema; see the gate below.
bash scripts/restore-drill.sh

# Against a real staging instance, with a recorded access window:
DRILL_HOST=<host> DRILL_ALLOW_REMOTE=1 DRILL_DB=dubbing_staging \
DRILL_PGPASSWORD=... bash scripts/restore-drill.sh

# Refuse to create a schema, and fail if there is not one already:
bash scripts/restore-drill.sh --require-schema
```

The schema the drill needs, applied with the **committed** migrations and nothing
else — a hand-written schema would prove that the hand-written schema is
restorable:

```bash
ConnectionStrings__Default="Host=<host>;Port=5432;Database=<db>;Username=<u>;Password=<p>" \
  dotnet ef database update --project src/DubbingPlatform.Infrastructure \
  --startup-project src/DubbingPlatform.Api --context AppDbContext
```

It runs ten checks, in this order, and each one exists because the failure it
catches is silent:

| # | Check | The failure it exists for |
| --- | --- | --- |
| 1 | all seven required groups declared, each with a table, a priority and a retention | a group silently dropped from the scope (**D2**) |
| 2 | table identifiers are bare lowercase | a backup job that interpolates an unvalidated identifier |
| 3 | non-loopback target refused without `DRILL_ALLOW_REMOTE=1` | a drill run against production by accident |
| 4 | schema present, and the migration count reported | verifying a schema nobody migrated |
| 5 | **RLS enabled on every scoped table, before anything is written** | a restore into a database with no tenant isolation (**D1**) |
| 6 | synthetic seed applied, every scoped table non-empty | proving nothing with an empty table |
| 7 | `pg_dump -Fc` over exactly the scoped tables, archive non-zero | an empty archive that "restores" successfully |
| 8 | `TRUNCATE` to zero, verified | a count comparison against a partial wipe |
| 9 | `pg_restore`, then per-table count equality **and non-zero** | an empty restore reported as success (**D3**) |
| 10 | 15 referential checks across the groups and their parents | a partial restore (**D4**) |
| 11 | spot-read one project, one user, one notification, with the Plan B columns | a restore that passes on counts and fails on data (**D5**) |

Result line: `RESTORE_DRILL_RESULT reason=<REASON> status=<PASS|FAIL> tables=<n>
rows=<n> gaps=<n>`. The reason vocabulary is a closed set and is listed in
[`ci-branch-protection.md`](ci-branch-protection.md).

### The three checks that are not obvious

**TRUNCATE, not DELETE.** A `DELETE` that fails partway leaves a half-empty
table, and the count comparison then passes against a wipe that only half
happened. `TRUNCATE` is transactional and the drill asserts the total row count
across the scope is exactly zero before restoring.

**Non-zero is the assertion that catches an empty restore.** `pg_restore` reports
success after inserting zero rows. A restore run as a role without `BYPASSRLS`
against a row-level-security table has every `COPY` filtered to nothing and exits
0 — a clean, successful, completely empty restore, indistinguishable from a good
one by exit code. The drill therefore fails on `after == 0` separately from
`after != before`, with the RLS cause named in the failure message.

**Referential coherence, because the schema cannot do it.** The database has
**no foreign keys** between any of these tables. The only FK constraints in the
entire schema are the MassTransit `outbox_message → outbox_state` and
`outbox_message → inbox_state` pair. Tenant relationships are by convention and by
RLS. So a partial restore is silent: a restored notification whose project was
not restored is accepted by PostgreSQL and produces an empty workspace with no
error anywhere. The drill runs 15 explicit orphan queries, one per relationship in
`scope.json`'s `referentialChecks`.

## Recorded drill result

### 2026-10-01 — new-entity restore drill

- **Date:** 2026-10-01 (UTC), 07:09–07:10.
- **Command:** `bash scripts/restore-drill.sh`.
- **Target:** a disposable `postgres:16-alpine` (16.15) started by the script, on
  `127.0.0.1:55432`, database `dubbing_drill`, schema created by the **committed**
  migration chain — all 7 migrations, 57 tables, `20260911061428_InitialCreate`
  through `20260922082522_AddRefreshSessions`.
- **Scope:** 10 tables — the seven new-entity groups plus four parents
  (`tenants`, `dubbing_projects`, `speakers`, `voice_profiles`). 28 seeded rows.
- **Result**: PASS.
  - RLS verified enabled on all 9 tenant-scoped tables before any write.
  - `pg_dump -Fc` produced a 6,251-byte archive; `TRUNCATE` reduced all 10 tables
    to exactly 0; `pg_restore --data-only --exit-on-error` reapplied it.
  - All 10 per-table counts identical before and after, none zero:
    `tenant_users` 2, `user_preferences` 4, `notifications` 5, `activity_events`
    4, `project_memberships` 3, `voice_preview_jobs` 2, `dubbing_projects` 3,
    `tenants` 1, `speakers` 2, `voice_profiles` 2.
  - 15 referential checks: 0 dangling. The count is asserted against the scope's
    own declaration (gap **D6**), so a drill that ran fewer checks cannot report
    the same PASS.
  - Spot-read returned `de710000-0000-4000-8000-000000000101` / "Drill Project
    One" / `alice@drill.invalid` / `RunCompleted` — i.e. the extended project
    metadata, the tenant user and the notification all survived with their Plan B
    columns populated.
  - `RESTORE_DRILL_RESULT reason=OK status=PASS tables=10 rows=28 gaps=0`.
- **Gaps recorded by the drill itself:** 1 (the media half, below).

### Gaps this drill found or left open

| # | Gap | Severity | Owner | Status |
| --- | --- | --- | --- | --- |
| **D1** | **The drill had no RLS precondition and would have passed against an unisolated database.** Found by disabling RLS on `notifications` and re-running: a restore into a cross-tenant-readable database produces a green drill, because neither a row count nor a spot-read can see a missing policy. Fixed in this task — check 5 runs before any write and names the table. | HIGH | 043C | **fixed** |
| **D2** | **A group removed from `scope.json` was invisible to the drill.** Found by deleting the `notifications` group and re-running: 9 tables compared against 9 tables, every count matched, **PASS**. Fixed in this task — `REQUIRED_GROUPS` in the script. This is the gap that motivated the file to exist. | HIGH | 043C | **fixed** |
| **D3** | **A restore as a role without `BYPASSRLS` inserts nothing and reports success.** The drill was written to accept `--exit-on-error` and treat exit 0 as done. Fixed in this task — a non-zero post-restore count is a distinct failure reason (`EMPTY_RESTORE`) with the RLS cause in the message. | HIGH | 043C | **fixed** |
| **D4** | **No foreign keys between any of the seven groups and their parents**, so a partial restore is silent. Fixed in this task — 15 explicit orphan queries in `scope.json`'s `referentialChecks`, run on every drill. | HIGH | 043C | **fixed** |
| **D5** | **A count-only check passes a restore from a pre-043C archive**, which restores `dubbing_projects` without its nine new columns. Fixed in this task — the spot-read asserts the columns by name. | MEDIUM | 043C | **fixed** |
| **D6** | **Truncating `referentialChecks` from 15 to 3 produced a PASS identical to a clean run.** The drill ran three orphan queries, found nothing, and reported `status=PASS gaps=0` — the same output as the full run. It is the D2 failure mode one level down, and it is worse than D2 was: at least a removed *group* changes the scope line. Fixed in this task — the drill holds the number of checks it ran against the number the scope declares, with a floor of one per group table. | HIGH | 043C | **fixed** |
| **D1a** | **The media half was not restored.** The drill's scope is the new tables, so object storage was not exercised. A project can restore perfectly and render an empty workspace. | HIGH | 043C / 047 | **open** — the full-system drill in [`runbooks/backup-restore.md`](runbooks/backup-restore.md) §5 covers it; the new-entity drill records the limit rather than claiming coverage |
| **D2a** | **The managed-service PITR tier is unverified.** This drill is a `pg_dump`/`pg_restore` of the data half against a disposable server. WAL archive continuity, the 7-day PITR window and the measured RTO need a real managed instance and are **staging-gated** (`docs/dr/drill-log.md`). | MEDIUM | 043C | **open** — the previous entries in the drill log are explicit that the live tier is deferred, and this does not change that |
| **D3a** | **The PostgreSQL client is a dependency of the drill, not of the product.** The script prefers the client inside the container so the versions always match; a host with neither Docker nor `psql` cannot run it. | LOW | 043C | **open** — a SKIP, not a PASS; a promotion gate must not accept it. `CLIENT_UNAVAILABLE` exits 2 and reports `status=FAIL`, never `status=PASS` |

**D1 through D6 were found by running the drill against broken inputs, not by
reviewing it.** Each fault was injected, the drill was re-run, and the output is
reproduced in `tasks_report_B/043C-runbooks-backup.md`.

D2 and D6 are the same defect at two levels, and together they are the argument
for this page existing. Both are *silently reduced* checks: remove the thing from
the data file and the drill verifies less and reports it in exactly the same way.
The generalisable rule is that **a check whose subject list comes from the same
file as the data it checks will always agree with itself**, so the required set
has to be written down a second time somewhere a change to the data does not
reach. In this repository that is now three files, all following the pattern
`tools/runbooks-index.test.mjs` set for runbooks:
`REQUIRED_GROUPS` in `scripts/restore-drill.sh`, `REQUIRED_GROUPS` in
`tools/backup-coverage.test.mjs`, and `PRODUCT_RUNBOOKS` in
`tools/product-runbooks.test.mjs`.

### One more thing the drill made visible

`voice_preview_jobs` was seeded with one `Completed` row and one deliberately
left `Running`, and `notifications` with one row past its `expires_at`. Both are
restored exactly as they were — the `Running` job is still `Running`, the expired
notification is still present in the table.

That is correct, and it is worth stating because it is the kind of state a
responder meets on day one of a real restore and has to know about:

- A restored `Running` preview job is a claim on a worker that does not exist.
  The forward-fix is a reaper, not a rollback and not a re-run.
- An expired notification is filtered by the list endpoint, not deleted. The
  unread **count** and the list are two different queries, which is why
  [`runbooks/product/notification-backlog.md`](runbooks/product/notification-backlog.md)
  compares them and a badge can disagree with a list after a restore.

## Retention summary

| Class | Mechanism | Retention | Who may restore |
| --- | --- | --- | --- |
| The seven new groups | PostgreSQL base backup + WAL | 30–365 d per group, above | service role (BYPASSRLS) |
| Audit trail | `audit_events`, append-only | 365 d (`RetentionOptions.AuditDays`) | maintenance role only, after `AuditDays` |
| Media, previews, exports | object storage versioning + CRR | 90 d finals, 30 d intermediates | service role |
| Queues | durable + quorum, **not backed up** | n/a | recovery is redrive-from-outbox, never a queue restore |

Backups are encrypted at rest by the managed services. Restore access is
service-role only. **Every drill action is audited, and no drill log may contain
a connection string, a token, a signed URL or a real tenant id** — the counts,
the timestamps, the measured RTO and the verdicts are the whole of what a drill
record needs, and the seed's ids begin `de71` precisely so that a row in a drill
log is recognisable as synthetic.

## Related

- [`dr/backup-restore.md`](dr/backup-restore.md) — the PITR and failover
  mechanisms, Plan A's, not restated here
- [`runbooks/backup-restore.md`](runbooks/backup-restore.md) — the operations
  index and the drill checklist, including the media half
- [`dr/drill-log.md`](dr/drill-log.md) — the running drill log
- [`../deploy/backup/scope.json`](../deploy/backup/scope.json) — the scope as
  data
- [`../scripts/restore-drill.sh`](../scripts/restore-drill.sh) — the drill
- [`security/secret-rotation.md`](security/secret-rotation.md) — the
  dual-support window, for a restore that needs a new credential
