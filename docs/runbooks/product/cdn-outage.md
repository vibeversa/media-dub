# The site is blank, or serving the wrong build

**What a user says:** "the page is white", "it loads but nothing works", "I'm on
yesterday's version", or the one that is never reported as a bug and is always
reported as a mystery — "I can see another team's dashboard".

The mechanism page is [`../cdn-cache-poison.md`](../cdn-cache-poison.md). It
owns what to do when the **wrong content is served to the wrong person**, which
is a data-exposure incident and not a performance one. This page owns the triage
and the routing, because the first question is *which hop is broken* and getting
it wrong costs fifteen minutes.

## Signals

| | |
| --- | --- |
| Monitor / dashboard | **GAP — no CDN or frontend availability signal exists.** 043A defined the two signals that would carry it — the origin's `/healthz` and the CDN-served `/version.json` — and neither is on a dashboard or behind an alert. `deploy/observability/alerts.yml` has no rule for the frontend. The CDN is outside the cluster, so no `NetworkPolicy` covers it. **Owner: 038 (dashboard), 043A (origin health).** |
| Diagnostics view (036) | [`cdn-outage.md`](../cdn-cache-poison.md) is the one to open the moment this page routes you there. `/admin` → health (`/api/v1/admin/status`) is the API's own view and says nothing about the edge. |
| Mechanism page | [`../cdn-cache-poison.md`](../cdn-cache-poison.md) — **read this one if anyone can see content that is not theirs** |
| Rollback (043B) | [`../../rollout.md`](../../rollout.md) for a bad frontend release; [`../rollback.md`](../rollback.md) for the decision table. The origin is a `Deployment` with `maxUnavailable: 0`, so `kubectl rollout undo` is the fast path. |

## Symptoms

- Blank page, white screen, or a `404` on `/assets/…`.
- The app loads but every request fails, and the failures are to the **API**
  origin rather than to any path under `/api/` on this host.
- `/version.json` reports a commit that is not the release you just made — users
  are on a stale build and the skew banner is not appearing for them.
- The security headers are missing on some responses. `add_header` **replaces**
  the inherited set rather than adding to it, so a `location` block that declares
  any header of its own silently drops the CSP, `nosniff`, `frame-ancestors` and
  the referrer policy. A page that renders but is missing its CSP is this
  incident, not a cosmetic one.
- A 404 for a hashed asset. Content-hashed names mean a cached HTML document
  referencing a build the origin no longer has — a **skew** between the document
  and the assets, not a missing asset.
- `BROWSER → CDN EDGE → STATIC ORIGIN → API INGRESS` — read
  [`../../topology.md`](../../topology.md) before diagnosing; most "the site is
  down" reports are a `NetworkPolicy` denying a hop someone assumed was allowed.

## Five-minute triage

**Step 1 finds which of the three hops is broken, in two `curl`s and about ten
seconds.** Run it from outside the cluster; a `curl` from inside shares the
origin's network position and will mislead you.

```bash
# 1a. The origin, directly. Bypasses the CDN entirely.
curl -sS -o /dev/null -w 'origin /healthz -> %{http_code}\n' \
  "https://<origin-host>/healthz"

# 1b. The public hostname, through the CDN. Compare the two.
curl -sS -o /dev/null -w 'edge /healthz -> %{http_code}\n' \
  "https://<public-host>/healthz"

# 1c. The version document. The single most useful line in this runbook:
#     it says which build the edge is actually serving, from the edge.
curl -sS "https://<public-host>/version.json"
```

| 1a | 1b | Class | Go to |
| --- | --- | --- | --- |
| 200 | 200 | the edge and the origin are both fine | the report is a **client** problem — step 2 |
| 200 | 5xx / timeout | **the CDN or its origin link is broken** | step 3 |
| 5xx | 5xx | **the origin is down** | step 3, then [`frontend-deploy-failure.md`](frontend-deploy-failure.md) |
| 200 | 200 but `version.json` has an old `commit` | **the edge is serving a stale build** | step 3 |

The `1a=200, 1b=5xx` row is the one that saves the most time. A responder who
starts at "the site is down" and never compares the origin to the edge will spend
twenty minutes in the wrong cluster.

**Step 2: the client.** If both hops answer 200, the fault is between the browser
and the edge, and it is almost always a cached document or an extension.

```bash
# Ask for the same document twice through the edge. A CDN that revalidates will
# return a consistent Cache-Control; one that is misconfigured may not.
curl -sSI "https://<public-host>/" | grep -iE '^(cache-control|age|via|x-cache|cf-cache-status)'
curl -sSI "https://<public-host>/assets/" | grep -iE '^(cache-control|age)'
```

A missing `Age` on a hashed asset means the edge is not caching it, which is a
**cost** problem rather than an outage — note it and move on. A missing
`Cache-Control: no-cache` on `/` is a **correctness** problem: the document is
cacheable, so a stale document can outlive the build it references.

**Step 3: the origin, from inside.** Only after step 1 pointed here.

```bash
kubectl -n <ns> get pods -l app.kubernetes.io/component=frontend
kubectl -n <ns> rollout status deploy/frontend --timeout=30s
kubectl -n <ns> logs deploy/frontend --since=10m | tail -40

# The header set is asserted per `location` block, brace-matched, because
# `add_header` replaces rather than augments. A block that declares its own
# header without including the snippet loses the CSP.
kubectl -n <ns> exec deploy/frontend -- cat /etc/nginx/snippets/security-headers.conf | head -20
```

## Degraded-mode triage (no CDN dashboard)

There is no CDN dashboard, so this is not a fallback — it is the path. It needs
only `curl` and `kubectl`, and it works from a laptop with no access to the
metrics stack at all.

```bash
# 1. Every hop, in one pass, with the cache headers that decide the class.
for host in <origin-host> <public-host>; do
  printf '%s / -> ' "$host"
  curl -sS -o /dev/null -w '%{http_code} cache=%{header_json}\n' "https://$host/" 2>/dev/null \
    | cut -c1-160
done

# 2. A hashed asset that the document references. If the document is current and
#    this 404s, the origin and the edge disagree about the build.
curl -sS "https://<public-host>/version.json" \
  | grep -oE '"assets/[^"]+\.js"' | head -1 \
  | xargs -I{} curl -sS -o /dev/null -w 'asset -> %{http_code}\n' "https://<public-host>/{}"

# 3. The origin's own view of itself. A 200 here with a failing 1a above means
#    the probe path and the serving path disagree.
kubectl -n <ns> exec deploy/frontend -- wget -qO- http://127.0.0.1:8080/healthz; echo
```

Step 2 is the one that turns "the site is blank" into a specific cause. A
document that references an asset the origin does not have is a **skew** — the
edge is serving a document from a build that has been replaced — and the fix is
a cache purge or a redeploy, not a rollback of the API.

## Mitigation

**The origin is not serving:** roll it back first and diagnose second. Two
replicas with `maxUnavailable: 0` means the previous ReplicaSet is still there.

```bash
kubectl -n <ns> rollout undo deploy/frontend
kubectl -n <ns> rollout status deploy/frontend --timeout=120s
```

**The edge is serving a stale build:** purge the cache for `/` and
`/version.json` only. Do **not** purge `/assets/` — those are content-hashed and
immutable for a year, so a purge costs a full origin round-trip for every file
and buys nothing.

**The edge is returning 5xx with the origin healthy:** this is the CDN's origin
link. The origin is a `ClusterIP` Service reachable only from `cdn-edge` and
`ingress-nginx` (`static-allow` in `deploy/k8s/networkpolicies.yaml`). If the CDN
was reconfigured to a different origin address, the symptom is a 5xx from the
edge and a healthy origin — and the fix is the address, not the pods.

**A missing security header on one class of response:** find the `location`
block that declares a header without `include
/etc/nginx/snippets/security-headers.conf`. The hosting gate detects this per
block and reports the line, so `bash deploy/tests/hosting.test.sh` names it
directly. This is a security defect that happens to be visible as a support
ticket; treat the severity as the header's, not as "some caching is odd".

**Never:** disable the CDN to "check whether it is the CDN's fault" without a
recorded maintenance window. It removes the TLS edge and the security headers
with it, and the resulting report is a security incident instead of a caching
one.

## Escalation

- **L1 (0–15 min):** steps 1 and 2. Both hops 200 → a client problem; close it
  with the cache headers you collected.
- **L2 (1 h):** the origin is down, or the edge 5xxs while the origin is
  healthy. Page platform. If the origin came down in a release,
  [`frontend-deploy-failure.md`](frontend-deploy-failure.md) is the page and
  [`../../rollout.md`](../../rollout.md) is the procedure.
- **L3 (4 h):** **anyone can see content that is not theirs**, or a security
  header is missing in production. Page security first and go to
  [`../cdn-cache-poison.md`](../cdn-cache-poison.md) — this stops being an
  availability incident at the moment it becomes an exposure one.

## Postmortem trigger

Any of:

- a frontend outage longer than 15 minutes;
- a stale document served after a release, measured in time from the deploy to
  the first report;
- a missing security header found in production, on any cache class;
- a cache purge of `/assets/` performed, or a CDN bypass performed, under an
  undeclared maintenance window;
- cross-tenant content observed by any user, **even once** — this one is
  non-negotiable and it goes to [`../cdn-cache-poison.md`](../cdn-cache-poison.md)
  immediately.

## Access and audit

- A cache purge and a `rollout undo` are both **privileged, audited changes to a
  production surface**. Record the actor, the time, the reason, and the exact
  paths purged, and have it land in `audit_events`. A purge with no recorded
  reason is indistinguishable from a cache-poisoning investigation having
  succeeded.
- `kubectl` against `dubbing-prod` uses the on-call platform identity, not a
  tenant admin's. Do not borrow a tenant admin's kubeconfig to do it: that makes
  the change unattributable to the team that owns it.
- There is **no** admin panel for the edge. `/admin` → health reports the API,
  not the frontend, and the seven sections it does have (tenants, users, health,
  usage, retention-audit, flags, ops) say nothing about a CDN. Reaching for them
  here wastes a step, and the one thing they *can* tell you — whether a release
  just happened — is what `/version.json` already says. If you do need a
  diagnostics view, they require **`Service` or `TenantAdmin`** under
  `RequireTenantAdmin`: an anonymous caller gets **401** and a `ProjectViewer`
  gets **403** (see
  [`../../operations/support-access.md`](../../operations/support-access.md)).
- No signed URLs, cookies or `Cache-Control` values copied out of a response
  body containing a signature go into a ticket. The hosts above are
  `CHANGE_ME` placeholders on purpose.
