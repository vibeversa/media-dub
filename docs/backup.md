# Backup: coverage, policy, retention, restore verification

Tasks 043C and **047**. The backup scope for the durable entities Plan B added,
what each one costs to lose, the policy that makes an open gap block a release,
and the recorded result of every restore drill.

**This page is a scope, a policy and a record. It is not the procedure.** The
mechanisms and the PITR commands are Plan A's and are not restated here:
[`dr/backup-restore.md`](dr/backup-restore.md) is *how* to restore,
[`runbooks/backup-restore.md`](runbooks/backup-restore.md) is the operations
index and the drill checklist, and [`dr/drill-log.md`](dr/drill-log.md) is the
running log of every drill including the full-system ones. This page answers
different questions: **for the seven new entity groups, what is covered, for how
long, in what order do I restore it, what did we promise, and what stops a
release from shipping over an open gap.**

## Three sources, and a fourth that is the point

| File | Holds | Read by |
| --- | --- | --- |
| [`deploy/backup/scope.json`](../deploy/backup/scope.json) | **What is covered** — tables, retention, restore order, referential checks | the drill, this page |
| [`deploy/backup/policy.json`](../deploy/backup/policy.json) | **What we promised** — RPO/RTO, escalation, drill cadence and record, the gap register, the release rule | `scripts/check-backup-policy.mjs` |
| [`deploy/k8s/backup-cronjob.yaml`](../deploy/k8s/backup-cronjob.yaml) + [`deploy/backup/dump.sh`](../deploy/backup/dump.sh) | **How it is produced** — the job, one coverage line per group | the gate, and a responder at 03:00 |
| `REQUIRED_GROUPS` in `scripts/check-backup-policy.mjs` and in `tools/backup-policy.test.mjs` | **What must exist**, written down twice more | the gate and its own tests |

The fourth row is not redundancy. Three sources that all derive from one another
agree with each other by construction, and this repository has now produced four
separate instances of a rule that passed because nothing disagreed:

- **D2** — a group removed from `scope.json` also left the count comparison, and
  the drill compared nine tables against nine tables and printed **PASS**.
- **D6** — `referentialChecks` truncated from 15 to 3 produced a PASS
  byte-identical to a clean run.
- **D7** — `scope.json`'s comment and this page's coverage table both named
  `deploy/k8s/backup-cronjob.yaml`, **which did not exist**. Three sources agreed
  and none of them was a job.
- **D10** — a `$comment` key in `policy.json` was read as a drill track, so the
  gate reported a missing drill record for a key that was documentation.

D7 is the one worth dwelling on, because it is the limit of the pattern. D2 and
D6 were *silently reduced* checks: remove the thing and the drill verifies less
and says the same thing. D7 was not a reduced check at all — it was the absence
of a check, and the three agreeing sources could not detect it because agreement
between a file, a comment about a file, and a page describing the file is exactly
what you get when the file is missing. Hence the fourth row: the required set
lives in the **gate**, which is a different kind of artefact from the data it
policers, and in the gate's own tests.

The drill holds its own copy for the same reason. The generalisable rule is that
**a check whose subject list comes from the same file as the data it checks will
always agree with itself**, so the required set has to be written down somewhere
a change to the data does not reach. In this repository that is now five
declarations, all following the pattern `tools/runbooks-index.test.mjs` set for
runbooks.

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

## The backup job

[`deploy/k8s/backup-cronjob.yaml`](../deploy/k8s/backup-cronjob.yaml), image
[`Dockerfile.backup`](../Dockerfile.backup), script
[`deploy/backup/dump.sh`](../deploy/backup/dump.sh). **This job did not exist
before Task 047** — see gap **D7** below. The coverage table above described a
mechanism that was never written.

It runs hourly at `:17` and produces a `pg_dump -Fc` archive of the ten scoped
tables, uploaded to the versioned bucket. **The schedule is not the RPO.** The
5-minute RPO is WAL archiving in the managed service, which has no CronJob. This
job is the *selectable* artefact: something a responder can restore with
`pg_restore` and nothing else — no provider CLI, no new instance, no approval
cycle. An hourly job that claimed a 5-minute RPO would be a coverage claim its
own schedule does not support.

The seven groups, one checklist line each, as the job declares them:

| # | Group | Table | Line in the manifest |
| --- | --- | --- | --- |
| 1 | **tenant users** | `tenant_users` | `dubbing.io/backup-group-1-tenant-users` |
| 2 | **project memberships** | `project_memberships` | `dubbing.io/backup-group-2-memberships` |
| 3 | **extended project metadata** | `dubbing_projects` | `dubbing.io/backup-group-3-project-metadata` |
| 4 | **user preferences** | `user_preferences` | `dubbing.io/backup-group-4-preferences` |
| 5 | **notifications** | `notifications` | `dubbing.io/backup-group-5-notifications` |
| 6 | **activity events** | `activity_events` | `dubbing.io/backup-group-6-activity-events` |
| 7 | **voice preview jobs** | `voice_preview_jobs` | `dubbing.io/backup-group-7-voice-preview-jobs` |

They are annotations rather than only `pg_dump` arguments because an annotation
is the part of a manifest an operator reads with `kubectl describe`, and a group
that is dropped from the job is then *named* by the gate instead of merely
absent. `scripts/check-backup-policy.mjs` reads all seven lines and fails the
release if one is missing, if a line names a table the scope does not have, or if
`dump.sh` disagrees with the manifest.

### Which tier covers which table

The seven groups above are the *selectable archive*. They are not all the tables
that need backing up, and the distinction is load-bearing — a maintenance policy
that says "these ten tables are covered" and stops there is a claim about a
subset presented as a claim about the database. `policy.json`'s `tableTiers`
assigns **every** table a migration creates to exactly one tier:

| Tier | Meaning | How it is restored |
| --- | --- | --- |
| `selectable-archive` | the 7 groups + 4 parents | `pg_restore` from the CronJob archive |
| `primary-base-backup` | the other 43 tables | the managed base backup + WAL, i.e. **PITR only** |
| `not-restored` | 3 tables, each with a written reason | n/a — queues redrive from the outbox, not from an archive |
| `infrastructure` | `__EFMigrationsHistory`, created by EF outside any migration | the migration Job re-applies forward |

The `primary-base-backup` tier is 43 tables whose only recovery path is a PITR,
and **PITR is unverified** (gaps **F1** and **F2**). That is why the tiers are
recorded separately rather than collapsed into "covered": an operator reading
"backed up" in one column and finding a PITR-only table there would be misled
exactly when it matters. The gate requires the assignment to be **total** over
what the migrations create, so a new table with no backup entry fails the release
until it is classified — the task's edge case, made mechanical.

### What the job refuses to do

Three refusals, each for a failure that is otherwise silent:

1. **It refuses a role without `BYPASSRLS`.** An archive taken as the app role is
   empty on every row-level-security table, and `pg_restore` reports success.
   That is gap **D3**, found in this repository by a restore; the job now refuses
   to produce the artefact rather than leaving it for a restore to discover.
2. **It refuses a connection string it cannot parse.** A silently misparsed DSN
   produces a dump of the wrong database, which is the worst artefact this job
   can make. The `ConnectionStrings__Default` shape is ADO.NET, `pg_dump` speaks
   libpq, and the script normalises or refuses.
3. **It fails on a non-2xx upload.** An archive left on an ephemeral pod volume
   is gone. A 403 from a private bucket is a normal response to a wrong key, and
   a job that logs it and exits 0 is a backup that has not run while reporting
   that it has.

There is no SQL in `dump.sh`. `pg_dump` receives `--table=` arguments as an argv
array and never builds a query, so there is no identifier for a shell to
reinterpret. The one query it runs — the `BYPASSRLS` check — is a literal with
no interpolation at all.

### Verified by running it, not by reading it

The job was built and executed against a real PostgreSQL, inside the image
`Dockerfile.backup` produces, and the artefacts were inspected:

| Check | Result |
| --- | --- |
| `backup-dump --check` prints the seven-group checklist and contacts nothing | 7 groups, 10 tables, `reason=OK` |
| an unparseable `BACKUP_PG_DSN` is refused | `DSN_UNPARSEABLE`, exit 1 |
| a host with no `psql` is refused **with a reason**, not exit 127 | `CLIENT_UNAVAILABLE`, exit 1 |
| a real dump, as uid 1654 in the image, over the ten scoped tables | 10,237 bytes, `PGDMP` header verified |
| `pg_restore -l` on that archive lists exactly the ten scoped tables | the 7 groups + `tenants`, `speakers`, `voice_profiles` |
| the SigV4-signed PUT against a sink that **requires** `AWS4-HMAC-SHA256` | `HTTP 200`, path `…/postgres/new-entities/<stamp>.dump`, 10,237 bytes |
| the same PUT with the signature stripped | `HTTP 403` → `UPLOAD_FAILED`, exit 1 |

The last two are the ones that matter. The upload half was the only code path in
this deliverable that nothing else could reach, and the sink used to check it was
**permissive enough to accept an unsigned request** — gap **D15**. After the sink
was tightened both paths were run, and the unsigned one fails the job instead of
logging a warning. A test double that does not fail on the thing it is testing
for is not a test double; it is a mirror.

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

It runs twelve checks, in this order, and each one exists because the failure it
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
| 11 | **spot-read, one row per entity group** — seven reads, each by id, each asserting that group's own columns | a restore that passes on counts and fails on data (**D5**) |
| 12 | **all 9 extended project-metadata columns present** after the restore | a restore from a pre-expand archive (**D5**) |

Result line: `RESTORE_DRILL_RESULT reason=<REASON> status=<PASS|FAIL> tables=<n>
rows=<n> gaps=<n> spot=<n>`. `spot=` is how many per-group spot-reads ran, and
it is bounded below by a **constant the script holds** rather than by the length
of the list it iterates — otherwise deleting a read from the list would shorten
the check and the drill would report the same PASS. That is **D6**'s shape, a
third time, in the step that reads the data. The reason vocabulary is a closed
set and is listed in [`ci-branch-protection.md`](ci-branch-protection.md).

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

**Seven spot-reads, not one.** A count cannot tell you the *right rows* came
back, and the seven groups fail in seven different ways under a bad restore.
Before Task 047 the drill read a single row that happened to be a three-table
join, so four of the seven groups — preferences, activity events, memberships and
voice preview jobs — were verified by count alone. That is the **D5** failure one
level down, and it was in the step whose entire purpose was to catch D5.

| Group | Read by | Asserts | The failure it catches |
| --- | --- | --- | --- |
| tenant users | `tenant_users.id` | `email` | a restore of a different tenant's users |
| project memberships | `project_memberships.id` | `role` | restored rows that do not grant what they should |
| extended project metadata | `dubbing_projects.id` | `name` | a matching count with every name NULL |
| user preferences | `(user_id, key)` | `value_json` | two rows summing to the right total, the wrong rows |
| notifications | `notifications.id` | `type` | a restore that lost `expires_at` and made a row permanent |
| activity events | `activity_events.id` | `type` | a history window that silently ended early |
| voice preview jobs | `voice_preview_jobs.id` | `status` | a `Running` job restored as `Completed`, or the reverse |

The column-set check is separate and is the only assertion that survives a
pre-expand archive. A base backup taken *before* `AddProductIdentityExtensions`
restores `dubbing_projects` without the nine new columns; the table exists, the
row count matches, and the spot-read of `name` is satisfied by a `NULL`. Asking
`information_schema.columns` for the nine names by name is what makes that
restore fail.

## Restore verification: the steps and the record

Two tracks, and the difference between them is the difference between a drill
that runs and a drill that has been waived without saying so.

| Track | Runs where | Cadence | Blocks |
| --- | --- | --- | --- |
| **new-entity** (`scripts/restore-drill.sh`) | anywhere with Docker — a disposable PostgreSQL the script starts itself | every release, at least monthly | the release gate |
| **full-system** (Plan A's procedure) | staging, with a recorded access window | quarterly | **promotion** |

The new-entity drill needs a disposable database and touches nothing else, which
is why it can be a release gate. The full-system drill needs a real instance with
WAL archiving and quiesces a deployment, which is why it is a promotion input.
`policy.json`'s `drills.fullSystem.blocksPromotion` is asserted by the gate: a
track that has never run and does not declare that it blocks promotion is
indistinguishable from a track nobody needed, and the gate treats that as a
finding.

### The target is staging

A drill runs against **staging**. Production requires an explicitly approved,
time-boxed access window, recorded *before* the first restore command and not
after — a drill against production without a recorded window is an unauthorised
production change and the audit log shows it as one. Never onto the primary: a
restore over the live database has no rollback, and the counts it then verifies
are the counts it just wrote.

### The steps

The commands are Plan A's and are **not** restated here. This is the checklist
and the record shape; `policy.json`'s `restoreVerification.newEntitySteps` is the
machine-readable copy the gate checks.

| # | Step | Why it is a step |
| --- | --- | --- |
| 1 | Confirm the target is staging and the window is recorded | an unrecorded drill is an unauthorised change |
| 2 | Restore to a **fresh** instance, never over the primary | the counts you verify must not be the counts you wrote |
| 3 | Count-check every scoped table, **non-zero** | a zero count after a "successful" restore is the D3 signature |
| 4 | Spot-read one row **per group**, by id, with that group's columns | a count cannot tell you the right rows came back |
| 5 | Verify RLS policies and the `audit_events` append-only grants survived | a restore that brought back rows but not the **policies** has restored data the app role can now read across tenants — the worst outcome available, and invisible in a count |
| 6 | **Verify the media half** — download an export whose id came from the *restored* database | downloading a file the old instance could serve verifies nothing |
| 7 | Measure the RTO as complete-minus-quiesce and record it **even when it missed** | a drill that misses the RTO is a failed drill, not a passed drill with a note |

Step 6 is the one the new-entity drill does not perform, because its scope is the
new tables. It is the reason gap **D1a** is *blocking* rather than a note, and it
is why a green new-entity drill means the data half is recoverable and says
nothing about the media half.

### The record

Every drill produces a record with these fields, and the gate reads them:

| Field | What goes in it |
| --- | --- |
| `date` | ISO date, UTC |
| `target` | what was drilled — staging, or a disposable instance, named |
| `scope` | tables and rows, and how many spot-reads and referential checks ran |
| `result` | `PASS`, `FAIL`, or `NOT_RUN` with a reason |
| `gaps` | count, and each one as an entry in the register below |
| `owner` | the task or team that ran it |
| `rtoMeasured` | the measured value, or an explicit statement that none was measured |

**Partial success is recorded as a failure with its loss scope and a
forward-fix.** It is never a pass with a note attached. A drill that restored six
of seven groups and recorded `PASS` with "6/7" in the description is a drill that
will be trusted next time when it is wrong.

An **overdue** drill — one that could not run because the environment was
unavailable — is recorded as overdue, with a date and an owner. It is not waived
by not running. The gate computes staleness from the track's own declared cadence
and fails on it, which is what makes "overdue, not waived" mechanical rather than
a sentence.

## RPO and RTO

**The values are team-chosen, not plan-derived.** Plan B §18.4 specifies no RPO,
no RTO, no job path and no escalation contact. Plan A §30 proposes RPO 5 min /
RTO 1 h and those are the numbers carried forward; what Task 047 adds is which of
them is a **target**, which has been **measured**, and **who to call**. Replace
every `CHANGE_ME` in `policy.json` before this policy gates a real release.

| Objective | Scope | Value | Measured? | Mechanism |
| --- | --- | --- | --- | --- |
| RPO | PostgreSQL | **5 min** | no | WAL archiving, 7-day PITR window |
| RPO | object storage | **15 min** | no | cross-region replication, versioning ON |
| RPO | selectable archive | **60 min** | yes, 2026-10-02 | hourly `pg_dump -Fc` |
| RTO | core platform serving again | **60 min** | **no** | quiesce-complete, per Plan A |

The `measured` column is the one that matters, and three of the four entries say
**no**. An RPO presented as a measurement is a different claim from an RPO
presented as a target, and the difference shows up the first time a WAL archive
turns out to have stopped at 02:00.

Two consequences worth stating plainly:

- **The 43 `primary-base-backup` tables have an RPO of 5 minutes and a recovery
  path of PITR only — and PITR is unverified** (gaps **F1**, **F2**). Their
  stated RPO is a property of the managed service's configuration, not something
  this repository has observed working.
- **The RTO has never been measured.** Nothing here has timed a full restore.
  `policy.json` carries `"measured": false` and the gate fails if that flips to
  `true` with no date, because a target that claims a measurement it does not
  have is worse than an honest unmeasured one.

A drill that misses the RTO is a **failed** drill. It does not become a passed
drill with a note.

## Failure escalation

The ladder is the deliverable; the **contacts are placeholders**. It is the
backup-specific slice of [`runbooks/escalation.md`](runbooks/escalation.md) and
does not restate it, and the on-call reference is
[`operations/support-access.md`](operations/support-access.md).

| Level | Role | SLA | Contact | Triggers |
| --- | --- | --- | --- | --- |
| **L1** | on-call platform engineer | 15 min | `CHANGE_ME` | a backup run failed; the newest archive is >2 h old; a drill returned a reason other than `OK` |
| **L2** | platform lead / DBA on call | 60 min | `CHANGE_ME` | two consecutive failures; a drill failed; a durable entity has no backup entry; the RPO was exceeded |
| **L3** | head of platform / incident commander | 240 min | `CHANGE_ME` | actual or suspected data loss; a restore that cannot complete in the RTO; an archive the current `pg_dump` cannot read |

What each level may and may not do is in `policy.json`, and the boundaries are
the interesting part: L1 may re-run a job and run the drill but **may not**
restore into production or close a gap; L2 may open a gap and approve a staging
window but **may not** close one or promote over one; only L3 may close a gap or
authorise a production restore, and L3's authority to waive a drill is exercised
as a **recorded, dated decision in the gap register**, never as a silence.

Two situations the ladder has to answer explicitly, because both have historically
been handled by not answering them:

- **A drill cannot run because the environment is unavailable.** The drill is
  **overdue**, with a date and an owner. It is not waived. The gate reports it,
  and `drills.fullSystem.blocksPromotion` is what keeps the deferral from being
  invisible.
- **A restore partially fails.** Record the loss scope — which groups, which
  tables, how many rows — and the forward-fix, which is almost never a rollback.
  A restored `Running` preview job is a claim on a worker that does not exist; the
  fix is a reaper. The gate stays red until the gap is closed.

## The release gate

**An open backup or restore gap blocks the release.** Stated in
`policy.json`'s `releaseGate.rule` so the rule and its enforcement cannot drift,
and enforced by `scripts/check-backup-policy.mjs`:

```bash
npm run check:backup-policy
# BACKUP_GATE_RESULT reason=<REASON> status=<PASS|FAIL> groups=<n> tables=<n> gaps=<n> open=<n> blocking=<n>
```

Wired into `deploy/verify.sh` in the static tier — so the release gate itself
fails — and into `backend.yml`. A blocking gap therefore stops a release, and
`deploy/verify.sh` reports it as its **own** reason,
`BACKUP_GAP_BLOCKS_RELEASE`, rather than as `MANIFEST_CHECK_FAILED`: the
manifests can be perfect and the release still unshippable because a durable
entity has no backup, and an operator sent to `deploy/k8s/` for a missing backup
entry is sent to the wrong file.

The gate checks that the seven groups are in the **job** and not only in a
document; that every migration-created table has a declared tier; that RPO and
RTO are declared and no target claims a measurement it lacks; that the escalation
ladder is callable; that a drill is **recorded** with a date, a result and its
gaps; and that no open blocking gap exists. A missing input file is a **failure**,
never a skip — a gate that read nothing has cleared nothing.

| Reason | Meaning |
| --- | --- |
| `OK` | every check passed |
| `COVERAGE_GROUPS_MISSING` | one of the seven groups is absent from `scope.json` or `policy.json` (**D2**) |
| `BACKUP_JOB_MISSING` | the CronJob or `dump.sh` is absent — **the state before Task 047** (**D7**) |
| `COVERAGE_DRIFT` | the job, the script, `scope.json` and `policy.json` disagree about what is dumped |
| `DURABLE_TABLE_UNCLASSIFIED` | a table a migration creates has no tier, or a tier names a table that does not exist |
| `RPO_UNDECLARED` / `RTO_UNDECLARED` | no number, no mechanism, no source, or a measurement claim with no date |
| `ESCALATION_INCOMPLETE` | a level with no role, SLA, trigger or contact, or the "overdue not waived" rule is missing |
| `DRILL_RECORD_MISSING` | no record, a record missing a required field, or an unrun track that does not block promotion |
| `DRILL_OVERDUE` | the last run is older than the track's own cadence |
| `DRILL_FAILED` | the last run is recorded `FAIL` |
| `GAP_BLOCKS_RELEASE` | an open blocking gap, a durable entity with no backup, or no release rule at all |
| `INPUT_INVALID` | a file could not be read or parsed |

Two of these deserve a note. `BACKUP_JOB_MISSING` is the reason this task
exists: before it, the file the scope named was absent and the gate would have
reported PASS. And `GAP_BLOCKS_RELEASE` is deliberately **not** overridable by an
environment variable — a release gate whose blocking behaviour can be turned off
by an env var is a gate that is off. L3's authority to waive a drill is a dated
row in the register, which the gate reads, so a waiver is visible rather than
invisible.

## Recorded drill results

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

### 2026-10-02 — Task 047 re-execution of the new-entity restore drill

- **Date:** 2026-10-02 (UTC), 03:09–03:15.
- **Command:** `bash scripts/restore-drill.sh`, re-run on top of 043C's drill
  with the changes this task made to it.
- **Target:** the same disposable `postgres:16-alpine` (16.15) on
  `127.0.0.1:55432`, `dubbing_drill`, schema created by the **committed**
  migration chain — 7 migrations, 56 tables.
- **Scope:** 10 tables, 28 seeded rows — unchanged from 043C, so the counts are
  comparable run to run. What is new is step 11 and step 12.
- **Result**: PASS — `RESTORE_DRILL_RESULT reason=OK status=PASS tables=10 rows=28 gaps=0 spot=7`
  - RLS verified enabled on all 9 tenant-scoped tables before any write.
  - `pg_dump -Fc` 6,251 bytes → `TRUNCATE` to 0 across all 10 →
    `pg_restore --data-only --exit-on-error`.
  - All 10 per-table counts identical before and after, none zero.
  - 15 referential checks, 0 dangling.
  - **7 per-group spot-reads**, one per entity group, each by its own predicate
    and each asserting that group's own column — a change from 043C's single
    three-table join, which left four of the seven groups count-only.
  - All 9 extended project-metadata columns present after the restore.
- **Gaps recorded by the drill itself:** 0. The media-half gap (**D1a**) is
  reported by the run **against 043C's disposable instance** and is recorded as
  blocking regardless; the run re-used an instance the script found already up,
  so the drill's own counter did not increment. That disagreement between the
  counter and the register is itself worth stating: the counter counts what this
  *run* could not close, and the register is the record of what is open. They are
  not the same number and the doc must not present them as one.
- **Gaps found by injecting faults into the new checks**, not by reading them:
  the preference read keyed on half of a composite key returned 3 rows and
  `grep -q dark` passed on the concatenation (the **D5** failure inside the check
  written to catch **D5**); a seed change restoring `"solarized"` instead of
  `"dark"` was **not** caught by the first version, and both are recorded as
  **D11**.

### Gaps this drill found or left open

| # | Gap | Severity | Owner | Status |
| --- | --- | --- | --- | --- |
| **D1** | **The drill had no RLS precondition and would have passed against an unisolated database.** Found by disabling RLS on `notifications` and re-running: a restore into a cross-tenant-readable database produces a green drill, because neither a row count nor a spot-read can see a missing policy. Fixed in this task — check 5 runs before any write and names the table. | HIGH | 043C | **fixed** |
| **D2** | **A group removed from `scope.json` was invisible to the drill.** Found by deleting the `notifications` group and re-running: 9 tables compared against 9 tables, every count matched, **PASS**. Fixed in this task — `REQUIRED_GROUPS` in the script. This is the gap that motivated the file to exist. | HIGH | 043C | **fixed** |
| **D3** | **A restore as a role without `BYPASSRLS` inserts nothing and reports success.** The drill was written to accept `--exit-on-error` and treat exit 0 as done. Fixed in this task — a non-zero post-restore count is a distinct failure reason (`EMPTY_RESTORE`) with the RLS cause in the message. | HIGH | 043C | **fixed** |
| **D4** | **No foreign keys between any of the seven groups and their parents**, so a partial restore is silent. Fixed in this task — 15 explicit orphan queries in `scope.json`'s `referentialChecks`, run on every drill. | HIGH | 043C | **fixed** |
| **D5** | **A count-only check passes a restore from a pre-043C archive**, which restores `dubbing_projects` without its nine new columns. Fixed in this task — the spot-read asserts the columns by name. | MEDIUM | 043C | **fixed** |
| **D6** | **Truncating `referentialChecks` from 15 to 3 produced a PASS identical to a clean run.** The drill ran three orphan queries, found nothing, and reported `status=PASS gaps=0` — the same output as the full run. It is the D2 failure mode one level down, and it is worse than D2 was: at least a removed *group* changes the scope line. Fixed in this task — the drill holds the number of checks it ran against the number the scope declares, with a floor of one per group table. | HIGH | 043C | **fixed** |
| **D7** | **There was no backup job.** `scope.json`'s comment said "`deploy/k8s/backup-cronjob.yaml` dumps exactly `tables`" and this page carried a seven-row coverage table, and **the file did not exist**. Every coverage check passed, because three sources agreed with each other and none of them was a job. This is the limit of the "write the required set down again" pattern: D2 and D6 were *reduced* checks, and D7 was the *absence* of one, which agreement between a file, a comment about a file and a page describing the file cannot detect. Fixed in this task — the job, the image, the script, and `REQUIRED_GROUPS` in the gate. | HIGH | 047 | **fixed** |
| **D8** | **The backup job's network policy was one-sided.** `backup-allow` granted egress to PostgreSQL; no policy granted PostgreSQL **ingress** from the backup component, so under the default-deny the job could not connect — and the symptom is a connection timeout, not a message about the policy. A NetworkPolicy is symmetric and each half looks locally correct, so a manifest review cannot find it. Fixed in this task — `datastores-allow-backup`. | MEDIUM | 047 | **fixed** |
| **D9** | **The migration Job has D8's asymmetry too, and it is not fixed here.** `migration-allow` grants egress; nothing grants the datastores ingress from the `migration` component. Its own comment reads "`migration-allow` … restricts this Job's egress to PostgreSQL", which is true and sounds complete. Deliberately not fixed in a backup task: widening the reach of the one workload that can DDL is a security change with its own review, and it belongs in a commit whose subject is that. | MEDIUM | security review | **open** |
| **D10** | **A `$comment` key was read as a drill track.** `policy.json` carries `$comment` keys in every object for documentation, and the gate iterated `Object.entries(policy.drills)` without filtering, so it reported `drill track "$comment" has no lastRun record` — a false positive on a documentation key. Found by reading the gate's output rather than its exit code, and the same category as **D11**. | LOW | 047 | **fixed** — `$`-prefixed keys are skipped, and a test asserts it |
| **D11** | **The new per-group spot-read could not tell the right row from the right characters.** The first version keyed `user_preferences` on `user_id` alone — half of its composite key — read back all three of that user's preferences, and passed on `grep -q dark` against the concatenation. The check written to catch **D5** (a restore that passes on counts and fails on data) could not detect a restore that brought back the wrong rows. Found by **reading the drill's own output**: it printed three rows and reported success. Fixed in this task — composite predicates, and each read must resolve to exactly one row. | HIGH | 047 | **fixed** |
| **D12** | **Four of the seven groups were verified by count only.** 043C's spot-read was a single three-table join, so preferences, activity events, memberships and voice preview jobs had no data-level assertion at all. It was the D5 failure one level down, in the step whose entire purpose was to catch D5, and it was invisible because the step reported success. Fixed in this task — one read per group, seven of them, plus a column-set assertion. | MEDIUM | 047 | **fixed** |
| **D13** | **The backup job dumped nothing, because its table list was assigned to `GROUPS`.** Bash reserves `GROUPS` as a special array of the current user's group ids and an assignment to it is silently ineffective: `GROUPS=("a:b")` leaves `GROUPS` as `(1000)`. The job iterated the uid and reported `malformed group entry: 1000`. `bash -n` accepts it and no reader sees it. The same run found a second defect: the table-resolution loop sat above the function definitions, so the script's first failure died with `finish: command not found` and exit 127 instead of the reason that went wrong. Fixed — renamed to `SCOPE_GROUPS`/`SCOPE_PARENTS`, and the gate parses those exact names and treats an empty parse as a finding. | HIGH | 047 | **fixed** |
| **D14** | **A missing PostgreSQL client produced no result line at all.** `psql: command not found`, exit 127, no `BACKUP_DUMP_RESULT` — so a release job parsing that line finds nothing and has to guess. `set -e` does not help, because a command-not-found inside a command substitution is reported by the shell rather than by the script. Fixed — a preflight for `psql`, `pg_dump` and `curl` reporting `CLIENT_UNAVAILABLE`. | MEDIUM | 047 | **fixed** |
| **D15** | **The S3 sink used to verify the signed PUT was permissive enough to pass an unsigned one.** It answered 200 to any request with a non-empty `Authorization` header, and `curl --user` sends Basic auth when `--aws-sigv4` is absent — so the harness "verified" a request a real bucket rejects. The sink now requires the `AWS4-HMAC-SHA256` prefix, and both paths were then run against it: signed → `HTTP 200`, stripped → `HTTP 403` and `reason=UPLOAD_FAILED`. The lesson generalises past this task: a test double that does not fail on the thing it is testing for is not a test double, it is a mirror. | MEDIUM | 047 | **fixed** |
| **D1a** | **The media half was not restored.** The drill's scope is the new tables, so object storage was not exercised. A project can restore perfectly and render an empty workspace. | HIGH | 043C / 047 | **open** — the full-system drill in [`runbooks/backup-restore.md`](runbooks/backup-restore.md) §5 covers it, and it has never run; see **F1** |
| **D2a** | **The managed-service PITR tier is unverified.** This drill is a `pg_dump`/`pg_restore` of the data half against a disposable server. WAL archive continuity, the 7-day PITR window and the measured RTO need a real managed instance and are **staging-gated** (`docs/dr/drill-log.md`). | MEDIUM | 043C | **open** — the previous entries in the drill log are explicit that the live tier is deferred, and this does not change that |
| **D3a** | **The PostgreSQL client is a dependency of the drill, not of the product.** The script prefers the client inside the container so the versions always match; a host with neither Docker nor `psql` cannot run it. | LOW | 043C | **open** — a SKIP, not a PASS; a promotion gate must not accept it. `CLIENT_UNAVAILABLE` exits 2 and reports `status=FAIL`, never `status=PASS` |
| **F1** | **No full-system restore drill has ever run.** The three earlier entries in [`dr/drill-log.md`](dr/drill-log.md) are a hermetic suite, a procedure walk-through and a compose sanity check; the live managed-instance tier is `STAGING-GATED` in every one. There is no date, and an absent date is what the gate reads. | HIGH | 047 | **open** — blocking **promotion**, not every release. This is what makes "overdue, not waived" mechanical: `drills.fullSystem` has `lastRun.date: null`, `result: "NOT_RUN"` and `blocksPromotion: true`, and a test asserts that dropping `blocksPromotion` fails the gate |
| **F2** | **The RTO is a target with no measurement behind it.** Nothing in this repository has timed a full restore. `policy.json` carries `"measured": false` and the gate fails if it flips to `true` without a date. | MEDIUM | 047 | **open** — three of the four RPO/RTO rows read `measured: no`, which is the honest state |
| **F3** | **The 43 `primary-base-backup` tables are recoverable only by PITR, and PITR is unverified.** Their stated 5-minute RPO is a property of the managed service's configuration, not something observed here. This is why the tiers are recorded separately instead of collapsed into "covered". | MEDIUM | 047 | **open** — closes with **F1** |

**D1 through D6 were found by running the drill against broken inputs, not by
reviewing it.** Each fault was injected, the drill was re-run, and the output is
reproduced in `tasks_report_B/043C-runbooks-backup.md`. **D7 through D12 were
found the same way in this task** — the missing job by looking for the file
`scope.json` named, the one-sided NetworkPolicy by reasoning about what the
default-deny does to a policy that only grants egress, the preferences
spot-read by **reading the drill's printed output** and noticing it had returned
three rows for a read that should resolve to one.

D2 and D6 are the same defect at two levels, and **D7 is the limit of the fix for
both**. D2 and D6 were *silently reduced* checks: remove the thing from the data
file and the drill verifies less and reports it in exactly the same way. D7 was
not a reduced check — it was the **absence** of one, and three sources that all
agreed could not detect it, because agreement between a data file, a comment
about a data file, and a page describing the data file is exactly what you get
when the file is missing. The generalisable rule is therefore two-part:

1. **A check whose subject list comes from the same file as the data it checks
   will always agree with itself.** The required set is written down a second
   time somewhere a change to the data does not reach.
2. **A claim that a file exists is not a check that it does.** Anything asserted
   in prose about an artefact has to be read off the artefact by something
   mechanical, and the thing being asserted must itself be a subject of the check
   — not a comment about it.

In this repository that is now six declarations, all following the pattern
`tools/runbooks-index.test.mjs` set for runbooks: `REQUIRED_GROUPS` in
`scripts/restore-drill.sh`, `REQUIRED_GROUPS` in
`tools/backup-coverage.test.mjs`, `REQUIRED_GROUPS` in
`scripts/check-backup-policy.mjs`, `REQUIRED_GROUPS` in
`tools/backup-policy.test.mjs`, and `PRODUCT_RUNBOOKS` in
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

The same rule applies to the job's own log, which is read far more often than any
drill record. `dump.sh` prints the target as `user@host:port/database` and says
`password not logged`; the connection string, the object-storage secret and the
access key are never echoed. A test asserts this, because the reason a credential
ends up in a log is that somebody printed the thing they were debugging.

## Related

- [`dr/backup-restore.md`](dr/backup-restore.md) — the PITR and failover
  mechanisms, Plan A's, not restated here
- [`runbooks/backup-restore.md`](runbooks/backup-restore.md) — the operations
  index and the drill checklist, including the media half
- [`runbooks/escalation.md`](runbooks/escalation.md) — the full escalation
  matrix; the ladder above is the backup slice of it
- [`operations/support-access.md`](operations/support-access.md) — on-call
  dashboards and the L1/L2/L3 SLAs
- [`dr/drill-log.md`](dr/drill-log.md) — the running drill log
- [`../deploy/backup/scope.json`](../deploy/backup/scope.json) — the coverage as
  data
- [`../deploy/backup/policy.json`](../deploy/backup/policy.json) — the policy as
  data: tiers, RPO/RTO, escalation, drill record, gap register, release rule
- [`../deploy/k8s/backup-cronjob.yaml`](../deploy/k8s/backup-cronjob.yaml) and
  [`../deploy/backup/dump.sh`](../deploy/backup/dump.sh) — the job
- [`../scripts/restore-drill.sh`](../scripts/restore-drill.sh) — the drill
- [`../scripts/check-backup-policy.mjs`](../scripts/check-backup-policy.mjs) —
  the release gate
- [`../ci-branch-protection.md`](ci-branch-protection.md) — the closed reason
  vocabularies for both gates
- [`security/secret-rotation.md`](security/secret-rotation.md) — the
  dual-support window, for a restore that needs a new credential
