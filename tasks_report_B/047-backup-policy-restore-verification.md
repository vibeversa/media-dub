# Task 047 — Backup Policy Execution and Restore Verification

## Status

**COMPLETED.** All five instructions implemented, R1–R5 satisfied, and the task's
`Validation` block passes **verbatim** (three commands). The task's stated
"Testing: operational verification, no unit tests" is what was done first — a
config grep and a drill-log review — and the finding that came out of it is the
headline: **there was no backup job.** `deploy/backup/scope.json` has carried the
line "`deploy/k8s/backup-cronjob.yaml` dumps exactly `tables`" since Task 043C
wrote it, `docs/backup.md` has carried a seven-row coverage table since the same
day, and the file did not exist. Every coverage check passed, because three
sources agreed with each other and none of them was a job.

So this task also builds the job, and the gate that would have caught its
absence. `scripts/check-backup-policy.mjs` is **RED on `main`**, on purpose: two
real gaps are open and blocking, and instruction 5 says an open gap blocks the
release. 66 new tests, the drill re-executed (`spot=7`), the job run end-to-end in
its own image against a real PostgreSQL including a verified SigV4 upload, and
`dotnet build` is 0 warnings / 0 errors.

**Nine defects were found by running the thing, not by reviewing it** (D7–D15 in
`docs/backup.md`). Four of them are the same shape as everything 043C/045/046
found, and one — D13, a bash `GROUPS` special variable — is invisible to
`bash -n` and to a reader.

## Summary

Task 047 is the execution/policy layer on top of 043C's coverage document. The
work splits into three artefacts that answer three different questions and
deliberately derive from each other only by being cross-checked: **what is
covered** (`scope.json`, from 043C), **what we promised** (the new
`deploy/backup/policy.json` — tiers, RPO/RTO, escalation, drill record, gap
register, release rule), and **how it is produced** (the new
`deploy/k8s/backup-cronjob.yaml` + `deploy/backup/dump.sh` +
`Dockerfile.backup`). A fourth declaration of the seven required group ids lives
in the gate itself and a fifth in the gate's tests, because the first three agree
by construction and agreement is not evidence. The restore drill was re-executed
and gained one data-level spot-read per entity group plus a column-set assertion;
running it, and injecting faults into the new checks, found six more defects. The
release gate is wired into `deploy/verify.sh` with its own reason
(`BACKUP_GAP_BLOCKS_RELEASE`) and into `basic-ci.yml`, deliberately **not** as a
`continue-on-error`.

## Files Created/Modified

### Created — the backup job (R1)

| File | What it is |
| --- | --- |
| `deploy/k8s/backup-cronjob.yaml` | `CronJob dubbing-backup`. Hourly at `:17`, `Forbid`, `startingDeadlineSeconds: 600`, `activeDeadlineSeconds: 1800`, `timeZone: Etc/UTC`, uid 1654, read-only root, all caps dropped, `automountServiceAccountToken: false`. The seven groups are **one annotation each** (`dubbing.io/backup-group-<n>-<id>`), each naming the table it dumps. |
| `deploy/backup/dump.sh` | The job's script. Argv-only (`pg_dump` gets `--table=` on an array; there is no SQL), parses the DSN itself and refuses anything it does not recognise, refuses a role without `BYPASSRLS`, verifies the `PGDMP` header, and fails on a non-2xx upload. `--check` prints the group checklist and contacts nothing. `BACKUP_DUMP_RESULT` with a 7-reason vocabulary. |
| `Dockerfile.backup` | `FROM postgres:16-alpine` (the same image the drill runs, so the client version matches the server) + `curl`, non-root uid 1654, read-only-root compatible. Added to the `backend.yml` image matrix. |

### Created — the policy and the gate (R3, R4)

| File | What it is |
| --- | --- |
| `deploy/backup/policy.json` | The policy as data: `coverage.groups` (the seven, 4th declaration), `tableTiers` (**every** migration-created table assigned to `selectable-archive` / `primary-base-backup` / `not-restored` / `infrastructure`), `rpo.targets[3]`, `rto`, `restoreVerification` (7 steps, 7 spot-reads, 7 record fields), `escalation.ladder[3]`, `drills.{newEntity,fullSystem}` with `lastRun`, `gaps.entries[15]`, `releaseGate`. |
| `scripts/check-backup-policy.mjs` | The release gate. 7 checks, 12 reasons, `BACKUP_GATE_RESULT reason=… status=… groups=… tables=… gaps=… open=… blocking=…`. Every finding line is prefixed with **its own** reason, because `reason=` carries only the first. |
| `tools/backup-policy.test.mjs` | **46 tests.** The 5th declaration of the seven groups. Two-hand: 20+ mutations of the *real* files asserting an exact reason, plus 4 assertions that the real tree is green apart from the registered gaps. |

### Modified

| File | What changed |
| --- | --- |
| `docs/backup.md` | Rewritten as scope + **policy** + record. New: three-sources-and-a-fourth, the backup job, the tier table, "Verified by running it", restore verification (7 steps, per-group count + spot-read tables, record fields), **RPO and RTO**, **Failure escalation**, **The release gate** (with the 12-reason table), the 2026-10-02 drill record, and gaps D7–D15 + F1–F3. |
| `scripts/restore-drill.sh` | Step 9 becomes **7 per-group spot-reads** with composite predicates, each required to resolve to exactly one row; step 12 asserts the 9 extended columns against exactly `9`; result line gains `spot=<n>`; `SPOT_REQUIRED=7` is a held constant. |
| `tools/backup-coverage.test.mjs` | Asserts the per-group reads, the held constant, the exact `= "9"` comparison, the one-row requirement, the spot-read identifier/value classes and the stdin path. Plus `locateTable(doc, headerCell)` — the gaps table is found **by its header** and bounded at the next heading. |
| `deploy/verify.sh` | New `== backup policy (047) ==` block in the static tier, fail-closed on missing node; `REASONS_BACKUP="BACKUP_GAP_BLOCKS_RELEASE"`; `backup-cronjob.yaml` added to the structural `required` map and to kubeconform's file list. |
| `deploy/k8s/networkpolicies.yaml` | `backup-allow` (egress to PostgreSQL 5432 + object storage 9000/443, **no ingress**, no broker, no provider egress) and `datastores-allow-backup` (the ingress half). |
| `deploy/k8s/secrets.yaml` | New `storage-region` key, in the prerequisites list and the ExternalSecret. |
| `deploy/tests/hosting-topology.py` | The datastore-peer allowlist gains `backup`, with the reason. |
| `docs/ci-branch-protection.md` | Three new sections: the drill's `spot=`, `check-backup-policy.mjs`'s 12 reasons, `dump.sh`'s 7 reasons; `BACKUP_GAP_BLOCKS_RELEASE` added to `verify.sh`'s table; the tools-test table rewritten. |
| `docs/dr/drill-log.md` | The 2026-10-02 entry (index only — the evidence is in `docs/backup.md`). |
| `deploy/README.md` | Launch gate **7** ("no open backup or restore gap", with the explanation of why it is red), and a "The scheduled backup" section. |
| `.github/workflows/basic-ci.yml` | Two steps: the gate and the two test files. **Not** `continue-on-error`. |
| `.github/workflows/backend.yml` | `Dockerfile.backup` → `dubbing-backup` in the image/scan/SBOM/sign matrix. |
| `package.json` | `check:backup-policy` and `test:backup-policy`. |

## Decisions Made

1. **The blocking gaps make the gate red, and I did not engineer around it.** The
   natural move once a gate goes red on a fresh branch is to soften it. I did
   not, because the red *is* the deliverable: instruction 5 says an open gap
   blocks release, `D1a` (the media half has never been restored) and `F1` (no
   full-system drill has ever run) are genuinely open, and both need a staging
   environment this repository does not have. The consequences are deliberate and
   recorded: `basic-ci.yml` is red on `main`, `deploy/verify.sh` reports
   `BACKUP_GAP_BLOCKS_RELEASE`, and `deploy/README.md` launch gate 7 is red. The
   alternative — a quarantine entry — was rejected on the repo's own reasoning
   ("a gate that is always red gets disabled"), and the `docs/backup.md` and
   `deploy/README.md` text says exactly what closes it.

2. **The test file asserts the *narrow* claim, not a pass.** The first version
   asserted the repository passes its own gate, which is false. The version that
   shipped asserts (a) the gate is red, (b) the **only** reason it is red is
   `GAP_BLOCKS_RELEASE`, (c) the count matches the register, and (d) *closing the
   blocking gaps in a mutation turns it green*. (d) is the load-bearing one: (b)
   looks at reason names, so a non-gap failure would slip past it.

3. **`note()` is separated from `fail()`.** The unrun full-system drill is
   reported on every run as `[NOTE] … OVERDUE, not a waiver` and does **not** make
   the gate red on its own — `F1` is what carries the blocking consequence, and
   `drills.fullSystem.blocksPromotion: true` is what makes "overdue" mechanical
   rather than rhetorical. This is the task's edge case implemented literally: a
   drill that cannot run is overdue, and the thing that makes promotion impossible
   is the gap entry, not the note.

4. **`tableTiers` makes "a new table with no backup entry" mechanical.** Checking
   "is this table in `scope.json`?" would read the 40-odd Plan A tables as
   unbacked, which is false — the managed base backup covers the whole database.
   The fix is to assign **every** migration-created table a tier and require the
   assignment to be total in both directions. The alternative — a hand-kept
   "known-unbacked" list in the gate — is the third copy that D2/D6/D7 were about.

5. **The CronJob uses `postgres:16-alpine` + `curl`, not an SDK.** The repository
   root has no AWS SDK, and adding one for a test helper would put an S3 client
   into the dependency surface of a project whose whole point (043A) is that the
   browser never sees one. SigV4 is `curl --aws-sigv4`, and the job's `storage-*`
   keys come from the existing `ExternalSecret`.

6. **`storage-region` is the only `optional: true` secretKeyRef.** The region is a
   *signing* input with a correct default in `dump.sh`, so a cluster whose
   ExternalSecret predates the key still produces backups. The other four are
   fatal when absent: a missing credential is a failed Job, which says "the backup
   did not run" rather than "the backup ran and went nowhere".

7. **D9 (the migration Job's one-sided NetworkPolicy) is recorded, not fixed.**
   It is the identical defect to D8 and it is pre-existing. Widening the network
   reach of the one workload that can DDL is a security change with its own
   review, and it does not belong in a commit whose subject is backups. It is a
   MEDIUM open gap owned by "security review".

8. **The doc's "Escalation"/"RPO"/"RTO" gate check is case-insensitive.** The
   case-sensitive version failed on `## Failure escalation` while the page plainly
   had the section. A rule that fails on capitalisation is a rule that gets
   deleted.

9. **The gaps table is located by its header, not by a row pattern.** Widening the
   row pattern to `[A-Z]\d+[a-z]?` made it match the *escalation* table's `**L1**`
   rows. On a page with three tables of the same shape, "a row that looks like a
   gap row" is not a definition of a gap row.

10. **`docs/backup.md` does not restate the procedure.** R5: the commands are
    Plan A's. It links `dr/backup-restore.md`, `runbooks/backup-restore.md`,
    `dr/drill-log.md`, `runbooks/escalation.md` and
    `operations/support-access.md`, and `backup-coverage.test.mjs` still asserts
    the PITR command is absent from the page.

11. **`gaps=0` in the drill record is *not* the number of open gaps, and the file
    says so.** The counter is what the run could not close; the run reused a
    container the script found already up, so the media-half gap did not increment
    it. `D1a` is blocking in the register regardless. Presenting one number as the
    other is how a green counter comes to stand in for a green register.

12. **The SigV4 verification harness was fixed before it was believed.** The sink
    answered 200 to any request with a non-empty `Authorization` header, and
    `curl --user` sends Basic auth when `--aws-sigv4` is absent — so it
    "verified" an unsigned upload. It now requires `AWS4-HMAC-SHA256`, and both
    paths were run: signed → 200, stripped → 403 → `UPLOAD_FAILED`.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ grep -E "tenant users|preferences|notifications|activity|memberships|voice preview|project metadata" docs/backup.md
| 1 | **tenant users** | `tenant_users` | `dubbing.io/backup-group-1-tenant-users` |
| 2 | **project memberships** | `project_memberships` | `dubbing.io/backup-group-2-memberships` |
| 3 | **extended project metadata** | `dubbing_projects` | `dubbing.io/backup-group-3-project-metadata` |
| 4 | **user preferences** | `user_preferences` | `dubbing.io/backup-group-4-preferences` |
| 5 | **notifications** | `notifications` | `dubbing.io/backup-group-5-notifications` |
| 6 | **activity events** | `activity_events` | `dubbing.io/backup-group-6-activity-events` |
| 7 | **voice preview jobs** | `voice_preview_jobs` | `dubbing.io/backup-group-7-voice-preview-jobs` |
  ... 24 further matching lines (the coverage table, the per-group spot-read
  table, the priority prose and the gap register)
EXIT=0

$ grep -E "RPO|RTO|Escalation" docs/backup.md
## RPO and RTO
| RPO | PostgreSQL | **5 min** | no | WAL archiving, 7-day PITR window |
| RPO | object storage | **15 min** | no | cross-region replication, versioning ON |
| RPO | selectable archive | **60 min** | yes, 2026-10-02 | hourly `pg_dump -Fc` |
| RTO | core platform serving again | **60 min** | **no** | quiesce-complete, per Plan A |
| **L2** | platform lead / DBA on call | 60 min | `CHANGE_ME` | … |
| **L3** | head of platform / incident commander | 240 min | `CHANGE_ME` | … |
  ... 17 further matching lines
EXIT=0

$ ls docs/backup.md
docs/backup.md
EXIT=0
```

### The restore drill, re-executed (R2)

```
$ bash scripts/restore-drill.sh
== new-entity restore drill (Task 043C) ==
  ok 10 table(s) in scope: tenant_users user_preferences notifications activity_events project_memberships voice_preview_jobs dubbing_projects tenants speakers voice_profiles
  -- connected; 56 table(s) in the public schema
  ok 7 migration(s) applied
  ok row level security is enabled on every scoped table
  ok seeded synthetic rows (deploy/backup/drill-seed.sql)
  ok counted 10 table(s) before the restore
  ok dumped 6251 bytes
  ok wiped every scoped table to zero
  ok restored the archive
  ok all 10 count(s) identical before and after
  ok no dangling references across 7 group(s); all 15 declared relationship(s) ran
     tenant-users (tenant user): email='alice@drill.invalid'
     memberships (project membership): role='Owner'
     project-metadata (project row): name='Drill Project One'
     preferences (user preference): value_json='"dark"'
     notifications (notification): type='RunCompleted'
     activity-events (activity event): type='StageCompleted'
     voice-preview-jobs (voice preview job): status='Completed'
  ok spot-read 7 row(s), one per entity group, with each group's Plan B columns
  ok all 9 extended project-metadata columns present after the restore

  date: 2026-10-02T03:41:46Z
  scope: 10 tables
  gaps recorded: 0

RESTORE_DRILL_RESULT reason=OK status=PASS tables=10 rows=28 gaps=0 spot=7
EXIT=0
```

### The backup job, run end-to-end in its own image (R1, proved)

```
$ bash deploy/backup/dump.sh --check
== backup dump (Task 047) ==
  [1/7] tenant-users -> tenant_users
  [2/7] memberships -> project_memberships
  [3/7] project-metadata -> dubbing_projects
  [4/7] preferences -> user_preferences
  [5/7] notifications -> notifications
  [6/7] activity-events -> activity_events
  [7/7] voice-preview-jobs -> voice_preview_jobs
  [parents] tenants speakers voice_profiles
  ok resolved 7 group(s) and 10 table(s); no connection was opened
BACKUP_DUMP_RESULT reason=OK status=PASS groups=7 tables=10 bytes=0
EXIT=0

$ BACKUP_PG_DSN="not-a-connection-string" bash deploy/backup/dump.sh
  !! the connection string has a fragment with no '=': not-a-connection-string
BACKUP_DUMP_RESULT reason=DSN_UNPARSEABLE status=FAIL groups=7 tables=10 bytes=0
EXIT=1

$ BACKUP_PG_DSN="Host=…;Password=z" bash deploy/backup/dump.sh     # host without psql
  !! psql is not on PATH in this image
  !! the job needs the PostgreSQL client and curl; the image is Dockerfile.backup, which installs both
BACKUP_DUMP_RESULT reason=CLIENT_UNAVAILABLE status=FAIL groups=7 tables=10 bytes=0
EXIT=1

$ docker run --rm --network host -e BACKUP_PG_DSN="Host=127.0.0.1;Port=55432;…" \
    -e BACKUP_STORAGE_ENDPOINT=http://127.0.0.1:45999 -e BACKUP_STORAGE_BUCKET=dubbing-backups … \
    dubbing-backup:047
  -- target dubbing@127.0.0.1:55432/dubbing_drill (password not logged)
  ok role dubbing has BYPASSRLS: the archive will not be silently empty
  ok dumped 10237 bytes over 10 table(s)
  ok archive header verified (PGDMP, custom format)
  ok uploaded 10237 bytes to dubbing-backups/postgres/new-entities/20261002T033637Z.dump (HTTP 200)
BACKUP_DUMP_RESULT reason=OK status=PASS groups=7 tables=10 bytes=10237
EXIT=0

$ pg_restore -l <archive> | grep TABLE DATA | sed 's/.*public //; s/ .*//' | sort
activity_events  dubbing_projects  notifications  project_memberships  speakers
tenant_users     tenants  user_preferences  voice_preview_jobs  voice_profiles
# exactly the seven groups + three parents; 10 tables.

$ <the same job with --aws-sigv4 removed, against a sink that REQUIRES AWS4-HMAC-SHA256>
  !! HTTP 403: the access key cannot write this bucket
  !! an unuploaded archive is on ephemeral disk and is gone with the pod
BACKUP_DUMP_RESULT reason=UPLOAD_FAILED status=FAIL groups=7 tables=10 bytes=10237
EXIT=1
```

### The gates

```
$ npm run check:backup-policy
  ok scope.json declares all 7 required entity groups
  ok policy.json lists all 7 required entity groups
  ok the CronJob declares a coverage line for each of the 7 groups
  ok dump.sh dumps 10 table(s) (7 groups + parents)
  ok the CronJob is scheduled, forbids concurrency, has deadlines, and uses the maintenance role
  ok all 56 migration-created tables have a declared tier (10 selectable, 43 primary, 3 not restored)
  ok 3 RPO target(s) declared, each with a mechanism and a source
  ok RTO target is 60 min, measured=false, with a stated consequence for missing it
  ok escalation ladder has 3 levels, each with a role, an SLA, a trigger and a contact field
  ok newEntity drill recorded 2026-10-02 as PASS (0 days old, cadence 30)
  -- [NOTE] drill track "fullSystem" has never run (…); promotion is blocked by gap F1 and this is an OVERDUE drill, not a waiver
  !! [GAP_BLOCKS_RELEASE] gap D1a is open and blocking: The media half was not restored. …
  !! [GAP_BLOCKS_RELEASE] gap F1 is open and blocking: No full-system restore drill has ever run. …
  ok 12 gap(s) recorded, 7 open, 2 blocking
  ok docs/backup.md carries the same drill record the policy reports

BACKUP_GATE_RESULT reason=GAP_BLOCKS_RELEASE status=FAIL groups=7 tables=10 gaps=15 open=7 blocking=2
EXIT=1        # <- ON PURPOSE. See Decisions Made #1.

$ node --test tools/backup-coverage.test.mjs tools/backup-policy.test.mjs
# tests 66
# pass 66
# fail 0
EXIT=0

$ npm run test:tools
1..409
# tests 409
# pass 408
# fail 1        <- tools/npm-audit-gate.test.mjs, PRE-EXISTING (see below). 343 before this task.
EXIT=1
```

### The release gate

```
$ bash deploy/verify.sh
PASS: structural + contract checks
PASS: rollout window: migration additivity, compatibility matrix, flag register, rollback rehearsal
== backup policy (047) ==
FAIL: backup policy: coverage, tier assignment, RPO/RTO, escalation, drill record, release gate
  A failing backup policy is a RELEASE BLOCKER, not a warning. The findings above
  name the file to fix: deploy/backup/policy.json, deploy/k8s/backup-cronjob.yaml,
  or the gap register in docs/backup.md. See 'The release gate' in docs/backup.md.
SKIP: kubectl apply --dry-run=client -f deploy/k8s/ (no usable kubeconfig context)
SKIP: kubeconform (kubeconform not installed)
FAIL: kustomize build staging + prod      <- PRE-EXISTING, 043A Finding 6, still open
PASS: helm lint (prod + staging values)
== result: 3 passed, 2 failed ==
VERIFY_RESULT reason=BACKUP_GAP_BLOCKS_RELEASE status=FAIL exit=1
EXIT=1
```

### Everything else

```
$ dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:01:17.63
EXIT=0

$ dotnet test tests/DubbingPlatform.TestFixtures.Tests
Passed!  - Failed: 0, Passed: 16, Skipped: 0, Total: 16, Duration: 728 ms
EXIT=0

$ npm run test --prefix frontend
 Test Files  151 passed (151)
      Tests  1793 passed (1793)      # unchanged from the 046 report
EXIT=0

$ npm run typecheck --prefix frontend        EXIT=0
$ npm run lint --prefix frontend --max-warnings=0   EXIT=0
$ npm run check:frontend        # node --test deploy/frontend/*.test.mjs    EXIT=0
$ npm run typecheck:e2e         EXIT=0
$ npm run check:rollout         ROLLOUT_GATE_RESULT reason=OK status=PASS migrations=7 findings=0   EXIT=0
$ npm run check:unit-containers CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440   EXIT=0
$ npm run check:vite-env        VITE_ENV_AUDIT_RESULT reason=OK status=PASS files=2 keys=15   EXIT=0
$ node scripts/check-no-hardcoded-copy.mjs   COPY_GATE_RESULT reason=OK status=PASS files=217 findings=939 new=0   EXIT=0
$ bash scripts/quarantine-check.sh  quarantine-check: 1 entr(ies), 0 problem(s)   EXIT=0
$ bash scripts/workflow-lint.sh     workflow-lint: 5 workflow file(s), 0 finding(s)   EXIT=0
$ bash scripts/migration-compat.sh  MIGRATION_COMPAT_RESULT reason=OK status=PASS   EXIT=0
$ bash deploy/tests/hosting.test.sh  HOSTING_GATE_RESULT reason=OK status=PASS exit=0 static=PASS docker=PASS   EXIT=0
$ python3 deploy/tests/hosting-topology.py deploy/k8s
  ok    default-deny present: networkpolicies.yaml (empty podSelector / Ingress+Egress)
  ok    8 NetworkPolicy/policies checked
```

### Red and pre-existing — do not "fix" by suppressing

- `tools/npm-audit-gate.test.mjs` → 1 failure (`a shell-free candidate must exist
  before the shell fallback is reached`). Byte-identical to the 045 and 046
  reports. Unrelated to backup; no file it reads was touched here.
- `kustomize build staging + prod` in `deploy/verify.sh` → still
  `MANIFEST_CHECK_FAILED`-class. 043A Finding 6, open since 043A. Note that
  `BACKUP_GAP_BLOCKS_RELEASE` now takes precedence in the result line, so the
  kustomize failure is visible in the `checks` array of the artifact rather than
  in `reason=`.

## Findings

Every one of these was found by running the thing.

### 1. The backup job did not exist (D7) — the headline

`deploy/backup/scope.json` line 5: *"`deploy/k8s/backup-cronjob.yaml` dumps
exactly `tables`"*. `docs/backup.md`: a seven-row coverage table. The file: **absent**.
`tools/backup-coverage.test.mjs` compared the scope against the prose and against
the drill, all three agreed, and the backup gate did not exist.

This is the **limit** of 043C's "write the required set down again" pattern, and
the limit is worth more than the pattern. D2 and D6 were *silently reduced*
checks — remove the thing, verify less, report the same. D7 was not a reduced
check; it was the **absence** of one. Agreement between a data file, a comment
about a data file, and a page describing a data file is exactly what you get when
the file is missing, so no amount of cross-agreement among those three could have
detected it.

The generalised rule now has two parts, and both are in `docs/backup.md`: (1) a
check whose subject list comes from the same file as the data it checks will
always agree with itself; (2) **a claim that a file exists is not a check that it
does** — anything asserted in prose about an artefact has to be read off the
artefact, and the artefact must itself be a subject of the check.

### 2. `GROUPS` is a bash special variable (D13)

The job's table list was `GROUPS=( "tenant-users:tenant_users" … )`. Bash
reserves `GROUPS` as the current user's group-id array and **an assignment to it
is silently ineffective**: after `GROUPS=("a:b")`, `${GROUPS[@]}` is `(1000)`.
The job iterated the uid, and the run said:

```
  !! malformed group entry (expected group-id:table): 1000
deploy/backup/dump.sh: line 74: finish: command not found
EXIT=127
```

`bash -n` accepts it. A reader cannot see it. The same run found the second half
of the bug: the table-resolution loop sat *above* the function definitions, so
`die` called a `finish` that did not exist yet and the script died with exit 127
and no `BACKUP_DUMP_RESULT` line at all. Renamed to `SCOPE_GROUPS`/`SCOPE_PARENTS`,
and the gate now parses those exact names and treats an empty parse as a finding
rather than as "no gaps".

### 3. A composite key, read on half of it (D11) — D5 inside the check for D5

The first per-group spot-read keyed `user_preferences` on `user_id` alone, got
all three of that user's rows back, and passed on `grep -q dark` against the
concatenation:

```
     preferences (user preference): value_json='"dark"
"en-GB"
{"threshold": 0.85}'
  ok spot-read 7 row(s), one per entity group, with each group's Plan B columns
```

A spot-read that cannot distinguish "the right row is there" from "some row
somewhere contains the right characters". Found by **reading the drill's printed
output** — it exited 0. The drill now takes composite predicates and requires each
read to resolve to exactly one row; re-seeding `ui.theme` as `"solarized"` and
re-running now produces:

```
  !! user_preferences.value_json for user preference (user_id = 'de71…' AND key = 'ui.theme') did not survive the restore: expected 'dark', got '"solarized"'
RESTORE_DRILL_RESULT reason=SPOT_READ_FAILED status=FAIL … spot=3
```

### 4. A NetworkPolicy is symmetric and neither half looks wrong (D8)

`backup-allow` granted the job egress to PostgreSQL. **No policy granted
PostgreSQL ingress from the backup component**, so under the existing
`default-deny-all` the job could not connect — and the symptom is a connection
timeout, not a message about a policy. Each file is locally correct. Fixed with
`datastores-allow-backup`; the migration Job has the identical asymmetry (**D9**)
and is deliberately **not** fixed here, because widening the reach of the one
workload that can DDL is a security change with its own review.

### 5. A `$comment` is not a drill (D10)

The gate iterated `Object.entries(policy.drills)` and reported
`drill track "$comment" has no lastRun record`. A false positive on a
documentation key, on a gate whose every other line was correct. It is the same
category as D11: the exit code was right and the output was noise, and a gate that
reports noise trains people to skim it.

### 6. The verification harness was a mirror (D15)

The S3 sink used to verify the signed PUT answered 200 to any request with a
non-empty `Authorization` header. `curl --user k:s` **without** `--aws-sigv4`
sends Basic auth — so the sink "verified" an unsigned upload, which is exactly the
regression it existed to catch. After requiring `AWS4-HMAC-SHA256`, the signed
path returns 200 and the stripped path returns 403 → `UPLOAD_FAILED`, exit 1. The
lesson is not about S3: **a test double that does not fail on the thing it is
testing for is not a test double.**

### 7. A check that fires on capitalisation (and one that never fired at all)

Both found while writing the new assertions, both the same shape as the rest:

- The doc check compared `'Escalation'` case-sensitively and failed on
  `## Failure escalation`. Now case-insensitive.
- The gap-row owner pattern was `/\| \d{3}[A-Z]?[^|]*\|/ || /043C/`, which
  required the owner to be the **second** cell. Widening it to `\b\d{3}[A-Z]?\b`
  then made it match the *escalation* table's `**L1**` rows — so the gaps table is
  now located by its header and bounded at the next heading.
- The gate's `tablesInDumpScript` used `[a-z-]+` for a group id, excluding `_`, so
  the "never in the selectable archive" prohibition silently did not apply to any
  table whose group id had an underscore. A pattern that had never matched
  anything.
- `assert.ok(/information_schema\.columns/.test(drill))` passed when
  `[ "$COLUMNS" = "9" ]` was weakened to `[ "$COLUMNS" -ge 0 ]` — the right query
  present, compared against nothing. The test now asserts the comparison.
- Every injection test asserted `resultOf(stdout)[1] === REASON`, i.e. the single
  `reason=` on the result line. With two blocking gaps open, that field is
  `GAP_BLOCKS_RELEASE` on **every** run, so all 20 injection tests were passing
  for the wrong reason. Fixed by prefixing every finding line with its own reason
  and matching on that. This one is the most embarrassing and the most worth
  writing down: a test file written specifically to prevent "a rule that has never
  matched anything" contained one.

## Incomplete integration points (mine, explicitly)

- **G1 — the release is blocked, by design.** `D1a` and `F1` are open and
  blocking, so `npm run check:backup-policy` is red, `deploy/verify.sh` reports
  `BACKUP_GAP_BLOCKS_RELEASE`, `basic-ci.yml` is red, and launch gate 7 in
  `deploy/README.md` is red. **Owner: whoever can get a staging environment with
  WAL archiving and a recorded access window.** Nothing in a code change closes
  it. The escalation ladder says L3 may waive it as a dated row in the register;
  the register is what the gate reads, so a waiver has to be written down.
- **G2 — the image is not published.** `Dockerfile.backup` builds and runs (it was
  built and executed here), and it is in `backend.yml`'s matrix, but
  `ghcr.io/CHANGE_ME/dubbing-backup:latest` does not exist and `CHANGE_ME` is the
  same placeholder as every other image in the tree.
- **G3 — `storage-region` is a new secret key.** Added to
  `deploy/k8s/secrets.yaml` and marked `optional: true` in the manifest, so a
  cluster without it still produces backups (the region has a default). It does
  need populating in the secret manager for a correctly-signed upload in a
  non-default region.
- **G4 — the seven group ids are declared five times** (`scope.json`,
  `policy.json`, the CronJob's annotations, the gate, the gate's tests) plus a
  sixth implicit one (`dump.sh`'s arrays, which the gate parses). This is
  deliberate and is the load-bearing part of the design, but it means adding a
  group is a six-place change and a five-place one fails the gate. **Owner:
  whoever adds the eighth group.**
- **G5 — the S3 verification harness is not committed.** The sink used to prove
  the signed PUT lives at `/tmp/opencode/s3sink.py` and is not in the repository.
  It is a *verification* artefact, not a test, because the job's real upload target
  is a bucket. If a future task wants a committed test for the upload path it
  needs a real S3 emulator (MinIO is blocked on this host — see the 046 report).
- **G6 — the CronJob has no `PodDisruptionBudget` or alerting.** A backup that
  silently stops running for a week is caught by the escalation ladder's L1
  trigger ("the newest archive is >2 h old") and nothing else; there is no metric
  for it. **Owner: 038** (observability), same class as 043C's
  `BackendMetrics.SseConnected` finding.
- **G7 — the drill is not wired into any pipeline.** Unchanged from 043C. It is a
  promotion input via `drills.newEntity.cadenceDays: 30`, and the gate fails on
  staleness, but nothing *runs* it.

## Environment

- **The .NET 10 SDK is not on this host's `PATH` and `/tmp/opencode` is wiped on
  a server restart** (the 046 report's warning, hit again). Reinstalled with:
  ```bash
  mkdir -p /tmp/opencode && cd /tmp/opencode
  curl -sSL https://dot.net/v1/dotnet-install.sh -o dotnet-install.sh
  bash dotnet-install.sh --channel 10.0 --install-dir /tmp/opencode/dotnet10 --no-path
  export PATH="/tmp/opencode/dotnet10:$PATH"
  ```
  **`--channel 10` fails; `--channel 10.0` works.** `dotnet tool restore` is
  needed before `dotnet ef`.
- **The restore drill needs a migrated schema**, which the script does not create
  (deliberately: a drill that migrates its own target verifies a schema nobody
  migrated). To re-run it from scratch:
  ```bash
  docker run -d --name dubbing-drill-pg -e POSTGRES_DB=dubbing_drill \
    -e POSTGRES_USER=dubbing -e POSTGRES_PASSWORD=dubbing -p 55432:5432 postgres:16-alpine
  ConnectionStrings__Default="Host=127.0.0.1;Port=55432;Database=dubbing_drill;Username=dubbing;Password=dubbing" \
    dotnet ef database update --project src/DubbingPlatform.Infrastructure \
    --startup-project src/DubbingPlatform.Api --context AppDbContext
  bash scripts/restore-drill.sh
  ```
  The drill *reuses* a container it finds already up, so it does not tear it down
  — that is why `gaps=0` and not `gaps=1`.
- **MinIO is blocked on this host** (046 report), and `tests/cross-layer` is
  therefore not runnable end to end. Irrelevant to this task; noted so nobody
  reads the absence of a cross-layer run as a regression.
- **`postgres:16-alpine` has `pg_dump`, `pg_restore`, `bash` and `sh`, and
  neither `curl` nor `openssl`.** That is why `Dockerfile.backup` installs `curl`
  and why SigV4 is `curl --aws-sigv4` (curl 8.22 in the image, which supports it).
- **The frontend suite needs four env vars** or 32 tests fail on
  `VITE_API_BASE_URL: Required`:
  ```bash
  export VITE_API_BASE_URL=http://localhost:5000 VITE_CDN_ORIGIN=http://localhost:5173 \
         VITE_ENVIRONMENT=local VITE_APP_VERSION=0.1.0-dev
  ```

## Recommendations for Next Agent (048)

### Repo state

- `main` carries … → 044 → 045 → 046 → **this task**. `HEAD` before it was
  `82676e0` ("test(harness): land the shared test harness and synthetic fixtures
  (Task 046)").
- **Six new files**, everything else modified in place:
  - `Dockerfile.backup`
  - `deploy/k8s/backup-cronjob.yaml`
  - `deploy/backup/dump.sh`, `deploy/backup/policy.json`
  - `scripts/check-backup-policy.mjs`
  - `tools/backup-policy.test.mjs`
- **Green:** `dotnet build` 0/0; `TestFixturesTests` 16/16; frontend typecheck,
  lint, **1793/1793**; `check:frontend`, `typecheck:e2e`, `check:rollout`,
  `check:unit-containers`, `check:vite-env`, `check:no-hardcoded-copy` (939
  baselined, 0 new), `quarantine-check`, `workflow-lint`, `migration-compat`,
  `deploy/tests/hosting.test.sh` (static **and** docker), `hosting-topology.py`
  (8 policies), `backup-coverage.test.mjs` + `backup-policy.test.mjs` **66/66**,
  the restore drill (`spot=7`), and the backup job end-to-end in its image.
- **Red, on purpose:** `npm run check:backup-policy` →
  `reason=GAP_BLOCKS_RELEASE status=FAIL … blocking=2`. Do **not** "fix" this by
  closing `D1a`/`F1` in prose, deleting the entries, or adding
  `continue-on-error`; Decisions Made #1 in the 047 report explains why, and
  `deploy/README.md` launch gate 7 says what closes it.
- **Red, pre-existing:** `tools/npm-audit-gate.test.mjs` (1); `kustomize build
  staging + prod` (043A Finding 6).

### Names worth knowing

- `npm run check:backup-policy` → `scripts/check-backup-policy.mjs` →
  `BACKUP_GATE_RESULT reason=<R> status=PASS|FAIL groups=<n> tables=<n> gaps=<n> open=<n> blocking=<n>`.
  12 reasons: `OK`, `COVERAGE_GROUPS_MISSING`, `BACKUP_JOB_MISSING`,
  `COVERAGE_DRIFT`, `DURABLE_TABLE_UNCLASSIFIED`, `RPO_UNDECLARED`,
  `RTO_UNDECLARED`, `ESCALATION_INCOMPLETE`, `DRILL_RECORD_MISSING`,
  `DRILL_OVERDUE`, `DRILL_FAILED`, `GAP_BLOCKS_RELEASE`, `INPUT_INVALID`.
  **Every finding line is prefixed `[REASON]`** — match on that, not on `reason=`,
  which carries only the first failure.
- `npm run test:backup-policy` → `tools/backup-policy.test.mjs` (46 tests).
- `bash deploy/backup/dump.sh --check` prints the seven-group checklist and
  contacts nothing. `BACKUP_DRY_RUN=1` dumps and does not upload.
  `BACKUP_DUMP_RESULT reason=<R> status=<PASS|FAIL> groups=<n> tables=<n> bytes=<n>`;
  7 reasons: `OK`, `INPUT_INVALID`, `CLIENT_UNAVAILABLE`, `DSN_UNPARSEABLE`,
  `RESTORE_ROLE_UNSUITABLE`, `DUMP_FAILED`, `UPLOAD_FAILED`.
- `deploy/backup/policy.json` keys: `coverage.groups[7]`,
  `coverage.job`, `tableTiers.{selectableArchive, primaryBaseBackup, notRestored, infrastructure}`,
  `rpo.targets[3]`, `rto`, `restoreVerification.{target, newEntitySteps, spotReads, recordFields}`,
  `escalation.ladder[3]`, `drills.{newEntity, fullSystem}.lastRun`,
  `gaps.entries[15]`, `releaseGate`.
- `scripts/restore-drill.sh`: steps 1–12; `SPOT_CHECKS` (7 records,
  `group|label|table|predicate|marker|marker-column`, predicate is
  `col=val` joined by `+`), `readonly SPOT_REQUIRED=7`, `SPOT_READS` on the result
  line.
- `tools/backup-coverage.test.mjs`: `REQUIRED_GROUPS` and
  `locateTable(doc, headerCell)` — use `locateTable` for any new table assertion
  on `docs/backup.md`; the page has three same-shaped tables.

### If you add an entity group, a table, or a gap

- **A group:** add it to `scope.json`'s `newEntityGroups`, `policy.json`'s
  `coverage.groups` and `tableTiers`, the CronJob's annotations, `dump.sh`'s
  `SCOPE_GROUPS`, and the gate's `REQUIRED_GROUPS` — then to
  `policy.restoreVerification.spotReads`, `SPOT_CHECKS` in the drill,
  `SPOT_REQUIRED`, and a `drill-seed.sql` row. Six places, deliberately; five of
  them failing the gate is the design.
- **A table:** add it to `policy.json`'s `tableTiers` — one of the four lists.
  `DURABLE_TABLE_UNCLASSIFIED` fires otherwise, in **both** directions (a tier for
  a table that does not exist fails too).
- **A gap:** `policy.json`'s `gaps.entries` **and** the table in `docs/backup.md`.
  The gate checks the former; `backup-coverage.test.mjs` checks the latter. The
  two are not compared automatically — that is G-something worth doing, and it is
  the most likely next defect in this area.
- **Never** put `outbox_message`, `inbox_state`, `outbox_state` or
  `__EFMigrationsHistory` in the selectable archive; the gate reads the list from
  `NEVER_IN_SELECTABLE_ARCHIVE` and checks the real script.

### Gotchas that cost time

1. **Bash reserves `GROUPS`.** An assignment to it is silently ineffective and
   `bash -n` accepts it. Cost one debug cycle (D13). Check
   `UID`/`EUID`/`GROUPS`/`USERS`/`GROUPS`/`HOSTNAME`/`SECONDS`/`RANDOM`/`LINENO`
   before naming a variable.
2. **`die` must be defined below every loop that can call it**, or the first
   failure exits 127 with no result line (D13's second half).
3. **Assert the comparison, not the query's presence.** Checking that
   `information_schema.columns` appears in a script passes when the count
   comparison next to it is `[ "$COLUMNS" -ge 0 ]`.
4. **On a page with several same-shaped tables, locate a table by its header.**
   `locateTable()` exists for this; do not write a row pattern.
5. **`\b\d{3}\b` does not match `043C`.** Task owners in this repository are
   written `0NN` with a letter suffix, so the boundary is on the wrong side.
6. **Anything asserted in prose about a file must be read off the file.** That is
   D7, and it is the one this repository keeps re-learning in a new costume.
7. **`verify.sh` now reports `BACKUP_GAP_BLOCKS_RELEASE` ahead of
   `MANIFEST_CHECK_FAILED`.** If you are debugging the kustomize failure, read the
   `checks` array in `deploy/.artifacts/verify-*.json`, not `reason=`.
8. **The frontend suite needs the four `VITE_*` env vars**; without them 32 tests
   fail on `VITE_API_BASE_URL: Required`.
