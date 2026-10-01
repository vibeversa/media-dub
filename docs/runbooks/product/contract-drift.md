# "The page says the field doesn't exist"

**What a user says:** a 400 or 422 naming a field the form does not have, a 404 on
an action the UI offers, a 500 from a screen that worked yesterday, or the sharp
one — **"I'm signed out and logging in doesn't help"**, which is what a
`/version` mismatch can look like when the client bootstraps against an endpoint
it no longer has.

The mechanism page is [`../contract-drift.md`](../contract-drift.md) — same name,
deliberately. It owns the bundle, the generator and the gates. This page owns the
triage order, because the first question is **which two things disagree**, and
there are three candidates: the bundle on disk, the document the running API
serves, and the build a user's browser is holding.

## Signals

| | |
| --- | --- |
| Monitor / dashboard | `ApiP95LatencyHigh` is the only adjacent rule and it is the wrong signal for this. **GAP — no contract canary in production.** `scripts/contract-canary.sh` exists in the repository and **no release job runs it**; the two gates that do exist (`npm run check-drift` → `tools/check-api-drift.mjs`, `scripts/openapi-diff.sh`) are PR-time and CI-time. The production signal that would catch this is a periodic comparison of `/openapi.json` against the committed bundle. **Owner: 042 (wiring), 038 (monitoring).** |
| Diagnostics view (036) | `/admin` → flags, to rule out a flag-gated rollout; `/admin` → health (`GET /api/v1/admin/status`) for the running version. The three `version` values to compare are the two here plus the edge's. |
| Mechanism page | [`../contract-drift.md`](../contract-drift.md) |
| Rollback (043B) | [`../rollback.md`](../rollback.md) — **read the decision table before doing anything.** This is the one incident where a rollback can be *refused*: after the contract phase of a release, rolling back code restores calls to endpoints that no longer exist. [`../../rollout.md`](../../rollout.md) §5 for the expand/contract sequence. |

## Symptoms

- `POST`/`PUT` returns 400 `VALIDATION_FAILED` naming a property the form does
  not send, or one the form sends that the API no longer accepts.
- A 404 on an endpoint that is in the generated client, or a 200 on one that is
  not.
- A 500 on a screen that worked before, with a body that is an empty
  `ErrorResponse` rather than a specific code.
- `GET /version` and the frontend's `/version.json` disagree on
  `openapiVersion`.
- The user is signed out on every page load and re-login does not help — the
  session bootstrap is calling something the deployed API does not have.
- `scripts/contract-snapshot.sh` fails in CI. The committed
  `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` does not match what the
  running API emits.

## Five-minute triage

**Step 1: get all three versions and compare them.** Drift is a disagreement
between three artefacts, and the fastest way to name the drift is to put all
three side by side. Two of the three are anonymous by design precisely so this
can be done during an incident with nobody's session.

```bash
# a. The build a user's browser is holding.
curl -sS "https://<public-host>/version.json"

# b. The API that is actually serving.
curl -sS "https://api.<env>/version"

# c. The bundle that is committed to the repository.
node -e 'const b=require("./src/DubbingPlatform.Api/OpenApi/openapi.v1.json");
         console.log(JSON.stringify({version:b.info?.version, stamp:b["x-stamp"]??b.info?.["x-stamp"]},null,2))'
```

| Comparison | Verdict |
| --- | --- |
| all three agree | **not a contract incident.** The error is a data or state problem; go to the owning feature's runbook |
| a ≠ b, c matches a | **the API is ahead of the edge.** A deploy that reached the API and not the frontend → [`frontend-deploy-failure.md`](frontend-deploy-failure.md) |
| a = b ≠ c | the environment is self-consistent and the **repository** is behind. A source problem, not an outage |
| a ≠ b ≠ c | **three-way drift.** Almost always a partially-applied release; this is L2 immediately |
| `openapiVersion` present in a and b, absent in a | the edge is serving an **older four-field document** |

That last row is 043A's defect class: the client requires
`{version, release, commit, builtAt, builtAtUtc, openapiVersion}` and degrades to
`UNKNOWN` — not to a false `MATCH` — when a field is missing. So a skew banner
that stays *quiet* is not evidence of agreement.

**Step 2: run the two gates that are runnable from where you are.** Both need
only Node and a checkout, no cluster, no Docker.

```bash
npm run check-drift                    # generated client vs the committed bundle
bash scripts/openapi-diff.sh --base main --head HEAD
```

`npm run check-drift` is a **three-way** check, not two: the committed
`frontend/src/api/generated/`, what the bundle generates, and the
`OPENAPI_VERSION` stamp's `sha256` hash. Any one of the three drifting is a
failure, and the hash check is the one that catches "the client was regenerated
but the bundle was not".

**Step 3: find the request that actually failed, from the browser's side.** The
API logs the route template and the status; it does not log bodies, by design.

```bash
kubectl -n <ns> logs deploy/api --since=15m \
  | grep -oE '"(GET|POST|PUT|DELETE) /api/v1/[a-z0-9{}/-]+" [0-9]{3}' \
  | sort | uniq -c | sort -rn | head -20
```

Look for a `404` on a route the UI offers, or a `422`/`400` rate on one route
only. A `404` on `/{*unmatched}` in `AdminController` is the **catch-all**, which
returns 404 for an unknown admin path — so an admin 404 is a route that was
renamed or never registered, not a tenant problem.

**Step 4: the canary, if it exists.** `scripts/contract-canary.sh` is the script
that compares the served document to the committed one against a live
environment. If no release job runs it (and none does), run it by hand:

```bash
DEPLOY_URL="https://api.<env>" bash scripts/contract-canary.sh
```

## Degraded-mode triage (no CI, no checkout)

Written for the responder on a laptop with no repository: a released build, a
broken screen, and no way to run a gate. Two anonymous endpoints are enough to
name the drift, and one `jq` comparison is enough to confirm it.

```bash
# 1. The served document's identity, without downloading the whole thing.
curl -sS "https://api.<env>/version" | jq .

# 2. Does the path the UI is calling exist on the served document? Pick the one
#    from the browser's network tab. `paths` is the whole contract.
curl -sS "https://api.<env>/openapi.json" \
  | jq -r '.paths | has("/api/v1/projects/{projectId}/exports/{exportId}/download")'

# 3. Has the served document changed since the last known-good release? The
#    x-stamp is on the committed bundle; a differing hash here means the served
#    document is not the one that was reviewed.
curl -sS "https://api.<env>/openapi.json" | jq -r '.info.version, .["x-stamp"] // "no stamp"'

# 4. Which routes are actually 4xx-ing, from the API's own log, grouped.
kubectl -n <ns> logs deploy/api --since=15m \
  | grep -oE '/api/v1/[a-zA-Z0-9{}/_-]+' | sort | uniq -c | sort -rn | head -20
```

Step 2 is the one that closes the incident in a normal drift case: the route the
browser called is not in `paths`, so the client's expectation and the server's
document disagree, and no amount of log reading changes that. Note that
`/openapi.json` is served anonymously — check that it is *supposed* to be before
quoting it into a ticket, and do not dump the whole document into one.

## Mitigation

**The client is ahead of the API** (a release reached the frontend and not the
API): roll the frontend back. The client is the half that can be reverted
safely, because migrations are additive and old code tolerates new nullable
columns for at least one release window.

**The API is ahead of the client:** this is the dangerous direction, and it is
the one that can be *refused* a rollback. Check
[`../rollback.md`](../rollback.md)'s decision table:
- `migrationsPending` — the migration Job has not completed, so the API is
  forward-only right now. Fix forward: complete the migration, then deploy the
  matching client.
- After the **contract** phase (columns dropped, endpoints removed): a rollback
  is refused. Old code calling a removed endpoint is a worse state than new code
  calling a new one. The forward-fix is a client release.
- Before the contract phase: a code-only rollback is safe. `kubectl rollout undo
  deploy/api` and leave the schema alone.

**A repository-side drift with a self-consistent environment:** nothing to
mitigate for users. Fix the source — regenerate with `make generate-api`, update
`OpenApiCoverageTests`, and close the registry row. Do **not** suppress the gate
to make an incident go away; `docs/ci-quarantine.md` is where a suppressed gate
is recorded, and this repository has a standing contract divergence
(`API_CONTRACT_DIVERGENCE`, issue `#422`) that is already quarantined. Adding a
second one to make this page quieter is how the first one became a problem.

**An admin 404:** an unknown admin path, caught by the `{*unmatched}` catch-all.
Check the route against `AdminController`'s `[HttpGet]` attributes before
suspecting the network — the catch-all makes every typo look like a routing
failure.

**Never:** edit the committed `openapi.v1.json` by hand to make a gate pass. It is
generated from the running document; hand-editing it makes the next regeneration
a diff nobody can explain.

## Escalation

- **L1 (0–15 min):** step 1 only. If all three versions agree, this is not a
  contract incident — take the error code to the owning feature and close it.
- **L2 (1 h):** two or three of the versions disagree. Page whoever owns the
  release. **Do not roll back until the decision table has been read** — an
  unexamined rollback in a post-contract state is the one action in this
  repository that makes an incident worse.
- **L3 (4 h):** three-way drift, or a client and API that disagree about an
  endpoint in a way that touches authentication. Route via
  [`../escalation.md`](../escalation.md) and involve security if the session
  bootstrap is involved.

## Postmortem trigger

Any of:

- a contract divergence reaching production, whether or not it was user-visible;
- a rollback that was **refused** by the expand/contract rule and the feature was
  left broken while the decision was made;
- a gate suppressed, quarantined or skipped to unblock a release;
- the client reporting `UNKNOWN` skew in production, which means a
  `version.json` field was missing and the safety check was inert;
- an `openapi.json` change reaching an environment without a reviewed bundle
  diff.

## Access and audit

- The `/admin` panels used above — flags and health — require **`Service` or
  `TenantAdmin`**, enforced by `RequireTenantAdmin`: an anonymous caller gets
  **401** and a `ProjectViewer` gets **403** (see
  [`../../operations/support-access.md`](../../operations/support-access.md)). A
  flag-gated rollout is a *cause* of version skew, and reading the flag state is
  privileged even though nothing is changed.
- `kubectl rollout undo` on the API is a **privileged production change** and
  must be attributable in `audit_events` with a named actor. In a post-contract
  state it may be **refused**, which makes the record more important, not less:
  record the actor, the release tags on both sides, and the state of
  `migrationsPending` at the moment of the decision. That state is what the
  decision was based on and it does not survive the incident — a rollback
  decision with no recorded `migrationsPending` cannot be reviewed afterwards,
  which is the whole point of the decision table.
- A feature-flag apply (`POST /api/v1/admin/feature-flags/apply`) is privileged
  and audited. Do not flip a flag to make a contract mismatch disappear: the flag
  changes which contract the client expects, so it moves the drift rather than
  removing it.
- `openapi.json` is a public description of the API. Do not paste the whole
  document into a ticket — quote the one `paths` key you checked. No bearer
  tokens, no cookies, no signed URLs; the ids in the commands above are synthetic
  and the hostnames are `CHANGE_ME` placeholders.
