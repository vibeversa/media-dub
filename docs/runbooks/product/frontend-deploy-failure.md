# The site is broken after the deploy

**What a user says:** "it was working five minutes ago", "the page loads but
every button is broken", "it says my session expired and logging in doesn't
help", or "I'm on a different version than my colleague".

The release procedure is [`../../rollout.md`](../../rollout.md) and the
one-command rollback is [`../rollback.md`](../rollback.md). The mechanism page is
[`../deploy-failed.md`](../deploy-failed.md), which owns the *pipeline* half.
This page owns the **frontend** half specifically, because the frontend has three
failure shapes the API half does not: a stale document, a missing environment
value, and a header set that a config block silently dropped.

## Signals

| | |
| --- | --- |
| Monitor / dashboard | **GAP — no frontend deploy or error-rate signal.** The gates that *would* catch a bad frontend build (`deploy/tests/hosting.test.sh` → `HOSTING_GATE_RESULT`, `scripts/check-frontend-topology.mjs` → `FRONTEND_TOPOLOGY_RESULT`, `vite-env-audit.sh` → `VITE_ENV_AUDIT_RESULT`) all run in CI and none of them is wired into a release job that builds the image. 043A recorded that no release job calls `deploy/config-inject.sh` or reads `deploy/k8s/frontend/configmap.yaml`. **Owner: 043B (wiring), 038 (post-deploy signal).** |
| Diagnostics view (036) | `GET /api/v1/admin/status` for the API's own health, and `/admin` → flags, to confirm a flag flip is not the cause. Neither says anything about the frontend. |
| Mechanism page | [`../deploy-failed.md`](../deploy-failed.md) |
| Rollback (043B) | [`../../rollout.md`](../../rollout.md) — the whole procedure. [`../rollback.md`](../rollback.md) — the expand/contract decision table, and it is the page that answers "may I roll back this?". |

## Symptoms

- The page loads and every interaction fails, with console errors on the API
  origin. This is the **missing `VITE_*` value** case: the bundle was built
  without `deploy/config-inject.sh`, so the API base URL is a placeholder and
  every call goes nowhere that exists.
- A stale document: `/version.json` and `/` disagree, or a hashed asset 404s
  because the document is from a replaced build. That is
  [`cdn-outage.md`](cdn-outage.md), not this page, unless it started with a
  deploy — in which case it is the same incident.
- The version skew banner is **not** showing when it should. The client requires
  `{version, release, commit, builtAt, builtAtUtc, openapiVersion}`; a CDN
  serving an older four-field document reports `UNKNOWN` rather than a false
  `MATCH`, so the banner stays quiet. Silence here is a fact about the document,
  not a reassurance.
- Missing `Content-Security-Policy`, `X-Content-Type-Options`,
  `X-Frame-Options` or `Referrer-Policy` on a subset of responses. `add_header`
  replaces the inherited set, so one `location` block declaring its own header
  drops all four for that cache class only.
- `502`/`503` from the static origin. The `static-allow` NetworkPolicy admits
  exactly two peers — `cdn-edge` and `ingress-nginx` — and both are
  `namespaceSelector`s. A peer added as a bare `podSelector` matches this
  namespace only; one added as a wrong `namespaceSelector` matches nothing.
- Only some users are affected. Then it is a **cache** problem, not a deploy
  problem, and it is [`cdn-outage.md`](cdn-outage.md).

## Five-minute triage

**Step 1: which build is actually being served, from the edge?** Not from your
shell, not from a terminal inside the cluster — from the public hostname, because
that is where the disagreement is.

```bash
curl -sS "https://<public-host>/version.json"
# {version, release, commit, builtAt, builtAtUtc, openapiVersion}
```

Then the origin's own:

```bash
curl -sS "https://<origin-host>/version.json"
```

| Edge vs origin | Verdict |
| --- | --- |
| identical, and the commit is not the release you made | **no release happened.** The deploy silently did not change the image. |
| identical, and the commit **is** the release | the build shipped; the fault is in what is inside it → step 2 |
| differ | **skew.** [`cdn-outage.md`](cdn-outage.md), and it is a purge, not a rollback |

A missing `version.json` (404) is itself the finding: `vite build` empties
`outDir`, so a document written into `dist/` before the build is deleted by it.
The Dockerfile copies it in afterwards and logs which case it was — **read that
build log line** before anything else. 043A recorded this as defect 1 and the
hosting gate as calling the resulting 404 "expected", because the broken release
path and a deliberately-uninjected local build produced identical output.

**Step 2: did the bundle get its environment?**

```bash
# The allowlist is the single source of truth in frontend/src/config/env.ts;
# this gate proves the shell and the TypeScript copy agree, and that no audited
# value is secret-shaped.
bash scripts/vite-env-audit.sh --allowlist-sync
bash deploy/config-inject.sh --env <env> --release <tag> --check
```

`--check` writes nothing. It fails on a secret, a non-URL origin, a missing
release tag, an unreadable OpenAPI bundle, or a `VITE_ENVIRONMENT` that disagrees
with `--env`.

**Step 3: the header set, per cache class.**

```bash
bash deploy/tests/hosting.test.sh
```

This is a ~3 minute Docker-backed gate and it asserts the headers that come back
from a **running container**, because `add_header` and `try_files` inheritance
depend on which `location` block matched — a config that reads correctly can
serve the wrong thing. The gate reports the offending block and the line. Use it
rather than reading `default.conf` and reasoning about inheritance.

**Step 4: the release gates that were supposed to prevent all of this.**

```bash
bash deploy/verify.sh                                    # manifests, overlay integrity
node scripts/check-frontend-topology.mjs                 # FRONTEND_TOPOLOGY_RESULT
bash scripts/vite-env-audit.sh --allowlist-sync          # VITE_ENV_AUDIT_RESULT
```

`deploy/verify.sh --post-deploy` is the one that would have caught this before a
user did. Its result line is `VERIFY_RESULT reason=<R> status=PASS|FAIL`, and
`reason=ROLLBACK_TRIGGERED` means it already rolled back for you.

## Degraded-mode triage (release pipeline down)

Written for the case where the incident *is* the pipeline: no gate to run, no
dashboard, possibly no cluster access from where you are. The origin's own logs
and the version document are enough to classify it.

```bash
# 1. What is the origin actually serving, from inside the pod?
kubectl -n <ns> logs deploy/frontend --tail=50
kubectl -n <ns> exec deploy/frontend -- ls -la /usr/share/nginx/html | head -20

# 2. The build log's own verdict on the version document. This line is emitted by
#    Dockerfile.frontend and it is the difference between "no config-inject ran"
#    and "it ran and the file was missing".
kubectl -n <ns> get events --sort-by=.lastTimestamp | tail -20

# 3. Did the rollout actually change the image? A successful `rollout status` on
#    an unchanged image digest is a real and confusing failure mode.
kubectl -n <ns> get rs -l app.kubernetes.io/component=frontend \
  -o custom-columns='RS:.metadata.name,IMAGE:.spec.template.spec.containers[0].image,AGE:.metadata.creationTimestamp'

# 4. The topology gate needs no cluster and no Docker; run it from anywhere.
node scripts/check-frontend-topology.mjs
```

Step 3 is the one that resolves "the pipeline said green and nothing changed".
`kubectl rollout status` reports success for a rollout that converged onto the
image it was already running, and reading the `RS` image column settles it in one
call.

## Mitigation

**A bad frontend build is in production:** roll the frontend back. The origin has
`maxUnavailable: 0` and two replicas, so the previous ReplicaSet is present.

```bash
kubectl -n <ns> rollout undo deploy/frontend
kubectl -n <ns> rollout status deploy/frontend --timeout=120s
curl -sS "https://<public-host>/version.json"   # confirm the commit changed
```

**Before rolling back, check [`../rollback.md`](../rollback.md)'s decision
table.** After the *contract* phase of a release, a rollback is **refused**: old
code calling a removed endpoint is worse than new code calling a new one. A
frontend-only revert is almost always safe because migrations are additive and
old code tolerates new nullable columns for at least one release window — but
"almost always" is not "always", and the table is the thing that knows the
difference.

**A missing `VITE_*` value:** rebuild with `deploy/config-inject.sh` and redeploy.
Rolling back does not fix it if the *previous* build was also uninjected.

**A header dropped by a `location` block:** add
`include /etc/nginx/snippets/security-headers.conf` to that block and redeploy.
The gate fails per block, brace-matched, so there is exactly one place to change.

**A peer missing from `static-allow`:** fix the NetworkPolicy. Do **not** widen
it to a bare `podSelector` (matches this namespace only) or an `ipBlock` (matches
nothing useful and is a routing bug waiting to happen). The topology gate asserts
the peers are `namespaceSelector`s for a reason.

**Never:** edit the running container (`kubectl exec … sed -i`). The next
`rollout` deletes it and the fix is lost with no record. The Dockerfile is the
place, and it is committed.

## Escalation

- **L1 (0–15 min):** step 1. If the edge and the origin agree and the commit is
  not the release, the deploy did not happen — that is a pipeline problem, not a
  production one, and nothing needs rolling back.
- **L2 (1 h):** a bad frontend build is serving. Roll back first, diagnose after.
  Page whoever owns the release pipeline; the gate that should have caught it is
  named in step 4 and the reason it did not run is the finding.
- **L3 (4 h):** missing security headers in production, or a rollback that was
  refused by the contract rule and the feature is broken. That needs a decision,
  not a command — [`../escalation.md`](../escalation.md).

## Postmortem trigger

Any of:

- a frontend release that shipped a broken build to production;
- `/version.json` absent or stale on a release image, or the client reporting
  `UNKNOWN` where it should have reported a mismatch;
- a release pipeline that reported success without running the frontend gates
  (this is the standing gap, and a second occurrence makes it a process
  postmortem, not a defect one);
- a security header missing on any cache class in production;
- a `NetworkPolicy` change that altered which pods can reach the origin.

## Access and audit

- A `rollout undo` on the frontend is a **privileged, audited production
  change**. The actor, the time, the release tag being reverted and the reason
  belong in `audit_events` and in the incident record. `deploy/verify.sh
  --post-deploy` records its own `ROLLBACK_TRIGGERED` with a reason; if you roll
  back by hand, that record does not exist and you must write it.
- The `/admin` panels used in step 2 — health and flags — require **`Service` or
  `TenantAdmin`**, enforced by `RequireTenantAdmin`: an anonymous caller gets
  **401** and a `ProjectViewer` gets **403** (see
  [`../../operations/support-access.md`](../../operations/support-access.md)). A
  feature-flag state is often the *cause* of a frontend difference, and reading
  it is privileged even though changing it is more so.
- No pipeline token, registry credential, `VITE_SENTRY_DSN` or signed URL from
  this page goes into a ticket. `deploy/config-inject.sh` fails the build on a
  secret-shaped value precisely so that a value like that never reaches the
  bundle; if one is in a bundle, treat the bundle as compromised and say so.
