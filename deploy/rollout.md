# The release record

Task 043B. The **procedure** for a release is [`docs/rollout.md`](../docs/rollout.md)
and the **operator runbook** for a rollback is
[`docs/runbooks/rollback.md`](../docs/runbooks/rollback.md). This file is the
record: the numbers an approval is given against.

The distinction is not cosmetic. A procedure is what somebody follows; a record
is what somebody is asked to trust. A procedure that has drifted from reality
still gets followed, and the drift is invisible. So everything in here is read by
a gate:

```bash
node scripts/check-rollout-window.mjs      # ROLLOUT_GATE_RESULT, exit 1 on a finding
bash deploy/verify.sh                       # runs the gate as one of its checks
```

| Requirement | What it is | Gate |
| --- | --- | --- |
| **R1** migration job succeeds before the API is ready; failure blocks the rollout | §1 | `deploy/verify.sh` (one gate binary, maintenance role on both, `maxUnavailable: 0`, the preflight delete) + the `rehearsal` observation in §5 |
| **R2** additive-only migrations during the window | §2 | `scripts/check-rollout-window.mjs` (`MIGRATION_NOT_ADDITIVE`, `LEDGER_INVALID`) |
| **R3** old-code/new-schema and new-code/old-schema smoke passes | §3 | `scripts/check-rollout-window.mjs` (`MATRIX_INCOMPLETE`) |
| **R4** flags documented with defaults; no flag replaces authorization | §4 | `scripts/check-rollout-window.mjs` (`FLAGS_UNDECLARED`) |
| **R5** rollback rehearsed and recorded | §5 | `scripts/check-rollout-window.mjs` (`REHEARSAL_STALE`) |

---

## 1. Migration ordering

`deploy/k8s/migration-job.yaml` runs before the API. There are three mechanisms
and they are not interchangeable, so all three exist:

| Mechanism | Where | Enforced by |
| --- | --- | --- |
| **ArgoCD `PreSync` hook** | `argocd.argoproj.io/hook: PreSync` + `BeforeHookCreation` | ArgoCD refuses to sync the rest of the application until every `PreSync` hook is `Healthy`. Blocked by construction, not by procedure |
| **Helm `pre-upgrade` hook** | `helm.sh/hook: pre-upgrade,pre-install`, weight `-5` | `helm upgrade` fails if the hook fails |
| **`wait-for-migrations` initContainer** | `deploy/k8s/api-deployment.yaml` | a pod never starts against a schema it does not understand. Defense in depth for an out-of-band apply that skipped the Job |
| **plain `kubectl` wait** | the deploy order, `deploy/README.md` §4 | operator procedure. **The weakest of the four, and the one with a trap — see below** |

`backoffLimit: 3`, `activeDeadlineSeconds: 600`, the maintenance-role connection
string from the ExternalSecret (`maintenance-connection`, `BYPASSRLS`, never the
app role), `readOnlyRootFilesystem`, all capabilities dropped, and
`migration-allow` restricting egress to PostgreSQL. It is the only thing in the
platform that can DDL.

### The trap in the plain-`kubectl` path

```bash
kubectl -n dubbing-prod delete job dubbing-migration --ignore-not-found   # <-- this line
kubectl apply -f deploy/k8s/migration-job.yaml
kubectl -n dubbing-prod wait --for=condition=complete --timeout=600s job/dubbing-migration
```

A Job's `spec` is immutable. Applying the manifest against a Job that has already
**completed** is a no-op that exits 0, and the `Complete` condition left over
from the previous release is still on the object — so the `wait` returns
success **immediately**, having observed a migration that never ran against the
current schema.

This is not a theoretical concern about a hypothetical cluster. It was measured:
in every run of the rehearsal it completed in **0–4 seconds**, and the Job's UID
and `Complete` transition time were identical before and after the re-apply.

```
ok  CONFIRMED FALSE PASS: the wait succeeded in 1s on the SAME Job object
    (uid 924bee0b, complete since 2026-10-01T06:43:09Z) - no migration ran.
    The re-apply was accepted (a no-op: the object already exists and nothing changed).
ok  after the documented preflight delete there is no Job, and the wait can no longer be
    satisfied by a previous release's success
```

The same UID and the same `Complete` transition time before and after the
re-apply are the evidence; a `wait` that returned in a second could not have been
waiting for a fresh pod. `deploy/verify.sh` asserts the `delete` is in the
documented order, and `deploy/rollout/rehearse-rollback.sh` reproduces the whole
thing, including the remedy.

ArgoCD and Helm are not exposed to it — `BeforeHookCreation` /
`before-hook-creation` delete the previous object before creating the new one —
which is why the delete is specific to the plain-`kubectl` path and is not a
step everybody performs.

### What a failed migration does, and does not, do

It **blocks the rollout**. It does **not** stop traffic. The rehearsal measured
both halves:

```
ok    the migration gate exited 1 (Error) and the API pod never reached Running,
      so the rollout is blocked by the initContainer alone
ok    the Service still routes to the PREVIOUS revision (10.244.0.25) and to no pod
      whose gate failed - a blocked rollout is not an outage
```

`maxUnavailable: 0` means the previous revision keeps serving while the new pods
are gated. "The migration failed" and "the site is down" are different
statements, and an assertion that conflates them passes on a broken gate and
fails on a healthy one. The property worth asserting is *no pod whose gate failed
is in the Service*, and that is what the rehearsal asserts.

The next action after a failed migration is a **forward fix**, never a rollback
(§5).

---

## 2. The expand/contract window

**The window is one release.** A migration inside the window may only **expand**:
new tables, new nullable-or-defaulted columns, new indexes, new constraints, new
rows. It may not drop, rename, or re-type anything.

Frozen in [`docs/operations/migration-compat.md`](../docs/operations/migration-compat.md);
`deploy/rollout/contract-ledger.json` records `windowReleases: 1` and the gate
**fails** if it is anything else, because a wider window is a policy change and
belongs in the policy document where a reader of the policy will see it.

### What the gate checks, and why it is not `migration-compat.sh`

`scripts/migration-compat.sh` already applies the chain to a real PostgreSQL in
two steps and asserts nothing the previous release needed disappeared. It is
necessary and it is not sufficient, for two reasons that are both about timing:

* it needs Docker, so it is a release-pipeline gate. A destructive migration is
  cheapest to refuse in the PR that introduces it — hence
  `scripts/check-rollout-window.mjs` is also a step in the always-on early gate
  (`Basic CI / backend-basic`);
* it compares the previous head against the **current** head. A destructive
  migration is allowed exactly once — the contract phase — and the whole point of
  a contract phase is that it is a *deliberate, recorded, separate* release. A
  diff against the previous head cannot tell "this release contracted,
  deliberately, with an approval" from "this release broke the rule on a
  Tuesday".

So the source is classified: `tools/rollout-window.mjs` reads each migration's
`Up` method, strips C# comments and string literals **length-preservingly** (so
braces inside a `Sql("""…""")` body cannot move the end of the method and the
operation scanner still sees the real `name:` arguments), and classifies every
`migrationBuilder` call.

Current state:

```
migration window: 7 migration(s), 276 expand op(s), 2 contract op(s), 0 ledgered
```

The two contract operations are both in `20260912075115_AddProjectSoftDelete`,
which drops `outbox_state.bus_name` and its index in the same change that adds
`dubbing_projects.is_deleted`. That migration is **at or before the ledger's
`baseline`**, which is why the baseline sits where it does: it is the grandfather
line, and refusing the repository's applied past is not a gate, it is a vendetta.
`baselineReviewedOn` is the date a human last confirmed it.

### The three rules that catch what a schema diff cannot

1. **`AddColumn` that is `NOT NULL` with no default is a finding.** It is
   additive, so `migration-compat.sh` sees a new column and passes, and the
   failure lands on the first `INSERT` from a pod still running the previous
   release. This is the single most valuable rule in the file and it is the one
   a schema diff provably cannot catch.
2. **Raw SQL is classified, and anything unclassifiable fails closed.**
   `migrationBuilder.Sql` can drop a table and no operation-name list can see it.
   The recognised shapes are a closed list (`CREATE`, `ALTER TABLE … ADD`,
   `INSERT`, `ENABLE ROW LEVEL SECURITY`, `CREATE POLICY`; and `DROP`, `TRUNCATE`,
   `DELETE`, `UPDATE`, `ALTER TABLE` without `ADD`, `RENAME` as contracts).
   `DO $$ … $$` is **unclassified** and therefore a finding: an operation nobody
   can read is an operation nobody has checked.
3. **`ENABLE ROW LEVEL SECURITY` is additive only on a table the same `Up`
   created.** On a pre-existing table it refuses every write from old code that
   does not set `app.tenant_id`, which is silent — reads return zero rows rather
   than an error. Four of this repository's seven migrations use the pattern and
   all four create their own table, which is why they pass.

### The contract ledger, and the follow-up task

A contract phase is a **two-release split**, never a single-release deletion:

| Release | What ships |
| --- | --- |
| **N** | the code stops writing the old shape. It may also *read* the new column. No schema change to the old column |
| **N+1** (or later) | the migration drops it. Only once release N is running everywhere shared |

The ledger entry is the record of that decision, and it is an **allowlist** for
the same reason the frontend env allowlist is one: a rule that fires only on
operations somebody already thought of misses the one nobody did. Every entry
requires `approvedBy`, `approvedOn`, `reason`, `followUpTask` and `reviewBy`,
and the gate fails if any is missing.

- **`followUpTask`** is required because every deprecation has a second half, and
  a contract phase without one is a deletion whose completion nobody owns. As of
  this writing there are **no** approved contracts, because there are **no**
  deprecations pending contraction — the first entry's `followUpTask` is the first
  thing that needs a number, and it should be a real one.
- **`reviewBy`** is the task's "a flag left on permanently needs an expiry review
  date" edge case, applied to an approval instead of a flag. There is no other
  mechanism: an approval with no date is a deletion nobody revisits. An approval
  past its `reviewBy` is a finding, not a note.

---

## 3. The compatibility matrix

Three cells. Each exists because it catches a different mistake:

| Cell | API image | Schema | What it proves |
| --- | --- | --- | --- |
| `old-api-new-db` | previous release | current head | **the rollback.** The previous release's code tolerates the expanded schema |
| `new-api-old-db` | candidate | previous head | a new column is nullable or defaulted, so the candidate does not wait on its own migration to boot |
| `new-api-new-db` | candidate | current head | the candidate works at all, so a failure in the other two is attributable to the window rather than to the candidate |

`old-api-new-db` is the cell most likely to be skipped, and it is also the first
one that becomes **impossible**: once the previous image is dequeued and the
previous schema head is gone, it can never be produced again. A matrix refreshed
after that point has quietly lost its hardest test. That is why the gate fails a
matrix older than 90 days rather than merely noting it.

Each cell probes three things, and the task's minimum is the first two:

1. **`GET /health`, and the `migration-currency` check within it** — not the
   aggregate code. `/health/ready` is 200 or 503 over PostgreSQL, object storage,
   the broker, Redis *and* migration currency together, so a matrix keyed on it is
   keyed on whether a bucket happens to be reachable. `/health` answers the same
   aggregate **and names each check**, so the verdict this matrix is about can be
   read on its own.
   `absent` is a real answer, distinct from `Unhealthy`: a build that predates the
   check is not a build whose check says the schema is wrong.
2. **`POST /api/v1/projects` → 201.** The **write** is not optional. A new
   `NOT NULL` column with no default breaks the `INSERT`, and a read of the
   project the harness just created succeeds happily — so a read-only matrix
   proves half of what it claims.
3. **`GET /api/v1/projects/{id}/workspace` → 200.** The task's "one workspace
   read". A 200 means the code read the new schema's project columns.

`expect` is **data**: each cell declares the statuses that constitute a pass and
the gate reads them from `compat-matrix.json`, so a run cannot pass by declaring
its own expectations and a cell that acquires a new expectation is a reviewed
diff. A cell marked `PASS` whose recorded statuses contradict its own
declaration is a finding.

Every probe path is checked by the gate against the **committed** OpenAPI
document, because the document puts the version prefix in `servers[0].url` and
the route in `paths`. The hosting endpoints (`/health`, `/version`) are exempt
because they are registered outside the document on purpose — they are
`AllowAnonymous`, and a document that listed them would advertise unauthenticated
paths.

### Running it

```bash
bash deploy/rollout/compat-smoke.sh          # writes deploy/rollout/compat-matrix.json
```

By default it starts a throwaway `postgres:16` container, applies the chain with
the repository's own `dotnet ef database update <head>`, and starts the candidate
API image against it. That is a real dependency graph on one machine, which is
why a record can exist at all. `--base-url https://staging…` runs the same probes
against a running environment, which is the staging form and the only form that
works once the previous image and schema head exist only in production.

### `old-api-new-db` is DECLARED UNAVAILABLE, and that is the honest answer

**There is no previous release.** This repository has never been released, so
there is no previous product to point a previous *image* at. Building one from
"the commit before the newest migration" produces something that is not a
release: that tree has no `WorkspaceController`, no `/health` or `/version`
endpoints and no `MigrationCurrencyCheck`, and its API answers 404 on
`/api/v1/me`, `/api/v1/dashboard`, `/api/v1/notifications` and on the workspace
route. A cell that "passed" against that build would be measuring a different
product; a cell that failed would be blamed on the schema.

So the cell is **declared** rather than quietly dropped, in the `unavailable`
array of `deploy/rollout/compat-matrix.json`, with the reason, the condition that
unblocks it, the risk being accepted and a review date. The gate requires all
four, fails when the date passes, fails when the entry names a cell that does not
exist, and **prints every exemption on every run** — because an exemption that
is invisible in a green run is a suppression with extra steps. The risk being
accepted is recorded: until the first release there is no previous product to be
incompatible with, so a code-only rollback has nothing to roll back *to*. The
rollback **mechanism** is separately rehearsed on a real cluster (§5).

### Last run — 2026-09-30

```
migration heads: previous = 20260921115016_AddVoicePreviewJobs, current = 20260922082522_AddRefreshSessions
candidate image: dubbing-compat:new, built from commit 5bf914a

-- new-api-new-db: candidate against 20260922082522_AddRefreshSessions --
  migration-currency=Healthy (schema is current at 20260922082522_AddRefreshSessions)
  POST /api/v1/projects=201, GET /api/v1/projects/{id}/workspace=200
  ok

-- old-api-new-db: DECLARED UNAVAILABLE, not run --

-- new-api-old-db: candidate against 20260921115016_AddVoicePreviewJobs --
  migration-currency=Unhealthy (1 migration(s) pending; applied head
  20260921115016_AddVoicePreviewJobs, this build defines 20260922082522_AddRefreshSessions)
  POST /api/v1/projects=201, GET /api/v1/projects/{id}/workspace=200
  ok

COMPAT_SMOKE_RESULT reason=OK status=PASS cells=2
```

The second cell is the interesting one. The candidate runs against a schema it is
**not** shipped with, and it both writes and reads successfully — which is
exactly what a nullable-or-defaulted new column buys, and the property a
`NOT NULL` column without a default would take away. Its
`migration-currency=Unhealthy` is not a failure: it is the gate that keeps a pod
out of the Service until its own migration has run, observed rather than assumed.

The harness runs with no object store, so the aggregate `/health` is 503 on
`storage`. That is precisely why the cell is keyed on the `migration-currency`
check and not on the aggregate — see probe 1 above.

---

## 4. The flag register

`deploy/rollout/flags.json` lists every switch that decides what a user **sees**
or what the application **does**, with the state it is in when nobody has decided
otherwise, and the date by which it must be deleted or re-justified.

The gate reads the **declaration the deploy will actually use** —
`deploy/k8s/frontend/configmap.yaml`, `frontend/.env.example`,
`FeatureOptions.cs` — and fails when the recorded default and the declared
default disagree. That disagreement is the whole failure: an operator reading the
register and a cluster running the other value.

| Flag | Layer | Default | Gates |
| --- | --- | --- | --- |
| `VITE_ENABLE_DIAGNOSTICS` | frontend build | `false` | presentation |
| `VITE_ENABLE_ANALYTICS` | frontend build | `false` | presentation |
| `VITE_ENABLE_EXPERIMENTAL_FEATURES` | frontend build | `false` | presentation |
| `VITE_SENTRY_DSN` | frontend build | `sentry-disabled` | inert sentinel |
| `VITE_SSE_ENABLED` | frontend build | `true` (local) | behaviour |
| `VITE_TELEMETRY_ENABLED` | frontend build | `false` | behaviour |
| `Features__VideoIntelligenceEnabled` | backend config | `false` | behaviour |
| `Features__LipSyncEnabled` | backend config | `false` | behaviour |
| `Features__LocalInferenceEnabled` | backend config | `false` | behaviour |

### A flag is configuration, never authorization (§6.9)

Two rules, both enforced rather than restated:

* every entry asserts `authorization: false`, and `gates` must be one of
  `presentation` / `behaviour` / `rollout` / `inert`. **The vocabulary has no word
  for access on purpose**, so a flag that starts gating a permission has nowhere
  to declare it and the gate fails;
* no file under `src/DubbingPlatform.Api/Auth/`, `…/Security/`, `…/Policies/`,
  `src/DubbingPlatform.Application/Authorization/` or
  `src/DubbingPlatform.Infrastructure/Identity/` may reference `FeatureOptions`,
  `Features__` or `VITE_ENABLE_`. This is the same property checked in the
  **source** rather than in the register, because a register cannot see its own
  readers — 11 authorization source files are scanned on every run.

The argument is three conditions, and they are what a browser-side switch cannot
satisfy: access control has to keep working when the flag is **off**, when the
flag service is **unreachable**, and when somebody flips it **during an
incident**.

The backend flags are dual-gated with the per-project setting on purpose.
`VideoIntelligenceEnabled` and `LipSyncEnabled` decide whether enrichment is
*requested*; a project that does not ask never gets it, so the flag is a rollout
switch and not a permission. `LocalInferenceEnabled` drives a **health check**
that reports `Healthy` when the flag is off, which is why it is safe — a flag
that gated readiness would gate traffic on configuration.

### Expiry review, and what happens when one is missed

Every entry carries `reviewBy`, and an entry past its date is a **gate failure**,
not a note. A flag nobody removes is a permanent, undocumented, untested code
path that exists only because it was cheap to add, and nobody can discover that
while the gate is green. All nine are due **2026-12-29**.

A flag defaulted **off** is the only safe default: a flag defaulted on turns a
backend-not-ready deploy into a user-visible failure, and it is what makes a new
surface revertible without a redeploy. While a flag is off, the frontend in
browsers is running the **old** UI against the **new** API — which is exactly the
combination §2 has to keep working, and the combination nobody tests unless it is
written down. It is written down.

---

## 5. Rollback

### The procedure

```bash
kubectl -n <namespace> rollout undo deployment/dubbing-api
kubectl -n <namespace> rollout undo deployment/worker-control
kubectl -n <namespace> rollout undo deployment/worker-ai
kubectl -n <namespace> rollout undo deployment/worker-media-prep
kubectl -n <namespace> rollout undo deployment/frontend
```

Full runbook, decision table, and the cases where a rollback is **refused**:
[`docs/runbooks/rollback.md`](../docs/runbooks/rollback.md).

The frontend's preferred rollback is a **CDN version pin**, not a redeploy: the
previous release's assets are still on the origin under exactly the names the
previous document referenced, because `/assets/*` carries a content hash.

### The database: forward-fix only

**A rollback never reverses a migration.** Not caution — the only safe option.
The EF bundle is forward-only by design, and reversing a migration that applied
anywhere shared deletes data the application has been writing since. A migration
that dropped a column cannot be un-dropped; "restoring" it means restoring from a
backup, which is a disaster-recovery exercise with a 1-hour RTO, not a rollback.

A code-only rollback is safe **only because §2's window holds**: the expanded
schema is a superset, so the previous release reads it without complaint. This is
why R2 is a gate and not a convention. A rollback is *refused* when the target
build is behind a contracted schema — `MigrationCurrencyCheck` reports it `ahead`
and readiness is false, so the pod never serves.

**A rollback restores code, not data.** Retention and deletion semantics from
Plan A are preserved by a rollback: a run deleted by a retention policy stays
deleted, and a soft-deleted project stays soft-deleted. A rollback that restored
them would be a data-corruption event wearing the costume of a deploy rollback.

### Rollback during active runs

Runs in flight are **not** cancelled. A run continues on durable backend state
(the `processing_runs` / `stage_executions` rows, the outbox, the queue) and the
UI shows the pre-existing version until the re-rollout. A rollback that cancelled
runs would need to decide, per run, whether the partial work is consistent — a
question the schema answers better than an operator can in the first minute of an
incident.

### The rehearsal

R5 is "rehearsed and recorded, not just documented".

```bash
kind create cluster --image kindest/node:v1.34.0
bash deploy/rollout/rehearse-rollback.sh --record deploy/rollout/rollback-rehearsal.json
```

It applies the **real committed manifests** to a real cluster, builds two
**genuinely different** images (distinct image ids, and `/version` baked at build
time so the two can be told apart by what they serve rather than by what tag they
were given), performs two rollouts and five `rollout undo`s, and measures
downtime with a probe that polls the **Service ClusterIP** rather than a pod.

It is honest about its scope. The workloads run
`deploy/rollout/rehearsal/`'s stand-in, not the application, and the record's
`notCovered` says so — and the gate **refuses to accept an empty `notCovered`**,
because an unqualified pass is read as a complete one and the gap between
"rollout undo works in a kind cluster" and "a production rollback is safe" is
everything: the application, the database, the CDN pin, the operator.

`tools/rollout-record.mjs` writes the record and preserves its committed
`$comment` block, so the documentation survives every run. That is a module with
unit tests rather than a `sed` pipeline, because both runners' first version
spliced the comment in and then printed a **second** top-level JSON object, and
`JSON.parse` answers that with *"Unexpected non-whitespace character after JSON at
position 12"* — a message naming neither the cause nor the line.

### Last rehearsal — 2026-10-01

Full record: `deploy/rollout/rollback-rehearsal.json`. Result **PASS**, 28
assertions, on a kind v1.34.0 single-node cluster in namespace `dubbing-prod`.

```
ok  the migration gate exited 1 (Error) and the API pod never reached Running,
    so the rollout is blocked by the initContainer alone
ok  the Service still routes to the PREVIOUS revision (10.244.0.28) and to no pod
    whose gate failed - a blocked rollout is not an outage
ok  revision 1 is serving on all 5 workloads, and it SERVES rehearsal-rev1
ok  availability probe announced itself and is polling the Service ClusterIP
    10.96.147.74
ok  revision 2 rolled out on all 5 workloads, and the pods now SERVE rehearsal-rev2
ok  rollout history lists the revisions an operator reads during an incident
ok  dubbing-api: revision 10 -> 11, template rev2 -> rev1
ok  worker-control: revision 5 -> 6, template rev2 -> rev1
ok  worker-ai: revision 5 -> 6, template rev2 -> rev1
ok  worker-media-prep: revision 5 -> 6, template rev2 -> rev1
ok  frontend: revision 5 -> 6, template rev2 -> rev1
ok  after the undo the API pods SERVE rehearsal-rev1, so the Deployment pointer
    and the running build agree
ok  the API Service answered /health/ready on every one of 200 samples across both
    rollouts and the undo: no downtime
ok  CONFIRMED FALSE PASS: the wait succeeded in 1s on the SAME Job object
    (uid 924bee0b, complete since 2026-10-01T06:43:09Z) - no migration ran.
ok  after the documented preflight delete there is no Job, and the wait can no
    longer be satisfied by a previous release's success
ROLLBACK_REHEARSAL_RESULT reason=OK status=PASS passed=28 failed=0
```

Note what the transcript no longer contains. `revision 10 -> 11` is a revision
the controller wrote; an earlier run of this same script printed
`revision 10 -> 0` and reported `ok`, which is finding 5 below. And
`availability probe announced itself` is a **positive** claim about the probe
being alive, not the absence of a complaint — which is finding 6.

### The six things the rehearsal found that reading the manifests did not

1. **The stale completed Job.** §1. Measured, not reasoned about: the wait
   succeeded in one second on the *same* object.
2. **A blocked rollout is not an outage.** The first version of the R1 assertion
   required the Service to have *no* ready endpoints while the gate failed, which
   is true only on a first deploy. With `maxUnavailable: 0` the previous revision
   is still serving, which is the design working. The assertion was wrong and the
   cluster said so.
3. **The revision annotation increments on an undo.** `rollout undo` does not move
   the Deployment back to revision *N−1*; it records the previous ReplicaSet's
   template as a **new** revision *N+1*, because a rollback is a change like any
   other and the history has to show it happened. The gate's first version read
   the success condition as "revision went down" and rejected a correct
   rehearsal.
4. **A loop over an empty array reports success.** An earlier revision of the
   rehearsal script lost its `targets=(...)` array to a bad edit. Because
   `for x in "${arr[@]}"` over an empty array is a no-op rather than an error,
   every loop ran **zero times**, every flag initialised to "yes" survived, and
   the script printed `ok revision 1 is serving on all five workloads`,
   `ok revision 2 rolled out on all five workloads` and
   `ROLLBACK_REHEARSAL_RESULT … passed=22 failed=0` — and wrote a record whose
   `targets` array was empty. The gate rejected that record (`REHEARSAL_STALE`),
   which is how it was found; the fix counts iterations rather than trusting a
   flag. A rehearsal that reports success from a loop that never executed is
   worse than one that fails, because it is recorded as evidence.
5. **A failed read was recorded as a measurement.** The script's revision reader
   ended in `|| echo 0`. Because the success condition downstream is "the number
   **changed**", a `kubectl get` that failed produced `0`, `0` is not `10`, and a
   real run wrote

   ```
   ok    dubbing-api: revision 10 -> 0, template rev2 -> rev1
   ```

   into the record this document cites as evidence. No controller writes revision
   `0`, so the number was not a revision — it was a failed read wearing one. The
   gate could not have caught it either: `revisionAfter !== revisionBefore` is
   true for `0`. Two fixes, because either alone leaves the hole open: the reader
   now returns non-zero and names why (and the workload is skipped, which fails
   the run through `UNDOS_RECORDED`), and the gate now refuses any revision that
   is not a positive integer (`impossible-revision`). The record is the artefact
   people believe; a default that turns a failed check into a passing measurement
   is the same defect in all five of these.

   The generalisable form, and the reason all six are the same finding: **a
   default that silently converts "could not tell" into "fine".** `|| echo 0`,
   `|| true`, a flag initialised to `yes`, an empty `for` body, a loop that runs
   zero times, a completed Job that nobody re-created and an empty log are seven
   spellings of it.

   The fix for #5 has a second half worth recording, because it is the same bug
   one level up. The reader reported its failure through a variable, and the
   callers read it as `value="$(revision x)"` — which looks equivalent and is not:
   a command substitution runs the function in a **subshell**, so the variable is
   gone by the time the caller reads it, and every failure message would have said
   the same useless thing regardless of what `kubectl` reported. The reader now
   sets a global and the caller reads it after checking the exit status, and the
   five ways it can fail (kubectl exits non-zero, prints nothing, prints a
   non-numeric string, prints `0`, succeeds) were each checked against a stand-in
   before the ten-minute run was spent on them.

6. **"The probe is measuring" was decided by silence.** The downtime measurement
   polls the Service ClusterIP and logs one line per **failure**, which is the
   right design — but it means the probe's log is *empty* for its first 50 samples.
   The check that the probe was the process actually running asked "is the log free
   of complaints and is the phase `Running`", and an empty log is exactly what a
   healthy probe looks like for 25 seconds. The check passed because nothing had
   gone wrong yet, not because something had gone right. The probe now writes one
   `START url=…` line before its first sample and the check asks for it: silence is
   then evidence *against* the probe, which is what it should always have been.
   Found while auditing the script for other `|| true`-shaped holes after fixing
   #5 — the same defect, in the one place it had been hiding in plain sight.

   The two fixes together are the whole lesson of this document: **read what you
   assert on, not what is absent.** Findings 1, 3, 5 and 6 are all a check that
   succeeded without evidence, and each one was invisible precisely because it
   reported success.


---

## Where this is not

Stated here rather than discovered later:

- **No CI job calls `deploy/rollout/compat-smoke.sh` or
  `deploy/rollout/rehearse-rollback.sh`.** Both need Docker and a cluster, so they
  are release-pipeline and quarterly jobs. What runs in the always-on early gate
  is the *record* check, which is what makes a stale matrix or an unrehearsed
  rollback visible on a PR.
- **`old-api-new-db` cannot run until the first tagged release** (§3). It is
  declared, dated, and printed on every gate run. The first release cut has to
  build the previous tag's image; nothing about this task can substitute for a
  release existing.
- **The kustomize overlays still do not build** (043A's open item). The base
  manifests hard-code `namespace: dubbing-prod` and the patches declare none, so
  the patch targets have no namespace. `deploy/verify.sh` asserts overlay
  integrity without kustomize, which is why the gap is visible, but the fix
  changes the flat `kubectl apply -f deploy/k8s/` path and is a design decision
  that belongs with whoever owns the overlays.
- **The `/version.json` and the flag register are build-time inputs** and no
  release job yet renders `deploy/k8s/frontend/configmap.yaml` into
  `frontend/.env.production` (043A's open item, and the highest-value wiring
  left).
- **The `CHANGE_ME` values in `deploy/k8s/frontend/ingress.yaml` and the two
  overlay ingress patches must become real hostnames and a real TLS `secretName`
  before a deploy.** So must the registry org in every manifest.
- **`deploy/k8s/migration-job.yaml` has never been applied to a cluster with a
  database.** The rehearsal proved the *ordering mechanism* and the false pass;
  the bundle itself is exercised by the integration tests, not by this rehearsal,
  and the record says so.
- **The compatibility matrix runs with no object store**, so the aggregate
  `/health` is 503 on `storage` in every cell. That is why the cell is keyed on
  the `migration-currency` check. If a future change makes a cell's pass condition
  depend on the aggregate, the harness needs a real bucket first.
- **The rehearsal shrinks container resource requests** to fit one node and
  stands a hostPath PV in for the managed scratch disk. Both are recorded in
  `notCovered`. A rehearsal against production-sized requests needs a
  production-sized node, which is a different exercise.

