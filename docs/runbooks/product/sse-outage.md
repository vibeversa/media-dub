# The progress bar is frozen

**What a user says:** "the progress bar isn't moving", "it jumped from 20% to
90% all at once", "the page says the run is stuck but I can see it finished".

The system is **still correct** in this state. Progress and notifications are
eventually consistent through the polling path, which is exactly why this gets
left unfixed for days: nothing is broken, something is just no longer live.

The mechanism page is [`../sse-degraded.md`](../sse-degraded.md). It owns the
server-side causes. This page owns the **fallback-polling verification**, which
is the check that decides whether this is an incident or a non-event.

## Signals

| | |
| --- | --- |
| Monitor / dashboard | **GAP, and it is the most consequential gap in this directory.** `deploy/observability/alerts.yml` has no SSE rule, and no panel in `deploy/observability/dashboards/` reads any `sse_*` counter. The counters **are** emitted — `BackendMetrics.SseConnections` / `SseReconnects` on meter `DubbingPlatform.Observability` (`sse.connections_total`, `sse.reconnects_total`) and `SseMetrics.PayloadDropped` (`sse.payload_dropped_total`) — and nothing graphs them. Live values: `curl -fsS http://localhost:8080/metrics \| grep '^sse'`. **Owner: 038.** |
| Diagnostics view (036) | `/admin` → ops → queues/workers panels, for "is the API itself saturated"; there is no stream panel. `GET /api/v1/admin/status` for workspace/run state. |
| Mechanism page | [`../sse-degraded.md`](../sse-degraded.md) |
| Rollback (043B) | [`../../rollout.md`](../../rollout.md) only if a release caused it. A stream degradation is almost never rolled back — it is a timeout, a memory limit or a client proxy, and none of those is a code revert. |

## Symptoms

- The progress bar advances in visible steps, or only on navigation.
- Notifications appear minutes late, or on the next page load.
- The console shows repeated `EventSource` reconnects, or the connection opens
  and closes immediately.
- Users report the page "freezing" on the processing view, then catching up.
- The percentage went **backwards**. That is `sse.payload_dropped_total`: the
  envelope exceeded 64 KB, was dropped, and the stream stayed open — so the
  stream looks healthy and the number is wrong.

## Five-minute triage

**Step 1 is the fallback-polling verification, and it is the assertion that
decides whether this is an incident at all.** The client is documented to fall
back to polling when the stream fails or when replay is truncated, and the
contract is explicit that **"the progress API is the source of truth"** — SSE
frames are invalidation hints. So: does the product still tell the truth without
the stream?

```bash
# 1a. The durable answer. This is what the user is really reading.
curl -sS -H "Authorization: Bearer $PROJECT_JWT" \
  "https://api.<env>/api/v1/projects/prj_01HZYABCDEFG/progress"

# 1b. The live hint. Note the query-parameter token: it is what SSE uses, and
#     using the header form here tests a different code path.
curl -N --max-time 10 \
  -H "Authorization: Bearer $PROJECT_JWT" \
  "https://api.<env>/api/v1/projects/prj_01HZYABCDEFG/progress/stream?access_token=$PROJECT_JWT"
```

| 1a | 1b | Verdict |
| --- | --- | --- |
| 200 with advancing counts | envelopes on the ~2 s cadence | healthy; not this runbook |
| 200 with advancing counts | no envelopes, or immediate close | **degraded, product is correct** — mitigation, not escalation |
| 200 **stale** | no envelopes | **a real defect**: the fallback is not working. This is the case that pages. |
| 5xx | anything | not an SSE incident — the API is failing; [`../db-failover.md`](../db-failover.md) |
| 401 | — | not an SSE incident; [`auth-outage.md`](auth-outage.md) |

The middle-left row is the only one that is a product bug. "The stream is down
and the user is still correct" is an inconvenience; "the stream is down and the
user is looking at stale numbers" is data the user will act on. **`1a` is the
check; `1b` only tells you why.**

**Step 2: is the SSE counter moving?** The counters exist even though no
dashboard reads them, and `curl` is the dashboard.

```bash
curl -fsS http://localhost:8080/metrics | grep -E '^sse_' || \
  echo "no sse_ series exposed on this endpoint"
```

`sse_reconnects_total` climbing with `sse_connections_total` flat is a reconnect
storm — usually a proxy, an extension, or an ingress read timeout closing an idle
stream every 60 s. A `curl` to `/metrics` returning nothing at all means the
scraping endpoint is not what you think it is; check `MapPrometheusScrapingEndpoint("/metrics")`
is actually in the running build.

**Step 3: one user or everyone?** One user with a flat `sse_reconnects_total` is
their network. Do not change the server for one report.

## Degraded-mode triage (logs first)

Written because this runbook was walked as a tabletop and the monitoring gap is
real. No dashboard, no metrics, no alert: the log path is the path.

```bash
# 1. Is the endpoint being hit at all? A zero here means the client never
#    connected, and the fault is in the client or in front of it.
kubectl -n <ns> logs deploy/api --since=10m | grep -c 'progress/stream'

# 2. Reconnect cadence, per pod. This is the mechanism page's step 2 and it
#    works without any metrics infrastructure.
kubectl -n <ns> logs deploy/api --since=10m | grep -c 'sse.reconnect'

# 3. Did the ingress cut the connection? A 60 s default read timeout closes an
#    idle stream every minute and the client reconnects, which is
#    indistinguishable from an application fault from the outside.
kubectl -n <ns> get ingress -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.spec.rules[*].http.paths[*].backend.service.name}{"\n"}{end}'
grep -rn 'proxy-read-timeout' deploy/k8s/ | head

# 4. The header the client needs in order to fall back correctly. If a proxy
#    strips it, the client keeps a truncated replay and never polls.
curl -sSI -H "Authorization: Bearer $PROJECT_JWT" \
  "https://api.<env>/api/v1/projects/prj_01HZYABCDEFG/progress/stream" \
  | grep -iE '^(cache-control|content-type|x-accel-buffering|replaytruncated)'
```

Step 4 is the check the tabletop found missing from the mechanism page:
`X-Accel-Buffering: no` and `Cache-Control: no-cache` are set by
`ProcessingController.StreamProgress` deliberately, and any intermediary that
rewrites or buffers the response breaks live progress while leaving the endpoint
returning 200. A 200 with `X-Accel-Buffering: stripped` is a **buffering proxy**,
and it produces exactly the "frozen progress bar" report.

## Mitigation

**The ingress is closing the connection:** raise the read timeout. The Ingress
carries `proxy-read-timeout: 600` for this reason; a 60 s default closes an idle
stream every minute, which the client reconnects from, which looks identical to
an application fault.

**The API is the bottleneck:** the cause is almost always per-connection memory.
Each connection holds a subscription and a bounded replay buffer (last 100
envelopes, headers only). Raise the memory limit **before** adding replicas — a
replica costs a database connection, and this is not a connection-count problem.

**Payloads are being dropped:** `sse.payload_dropped_total` above zero means
frames exceeded 64 KB. The stream stays open, so nothing errors. The drop is
caused by an oversized payload, not by a slow network; find the event type
carrying it and reduce its payload. `ProcessingController` documents the 64 KB
bound.

**Replay truncated on resume:** a resume beyond the 100-envelope window sets
`replayTruncated: true` and the client falls back to polling. That is the
designed behaviour, not a fault. Confirm the client actually polls — if it does
not, that is a client defect and the stale-progress row in step 1 is the correct
verdict.

**One user's network:** nothing to fix server-side. The fallback is correct.

**Never disable the polling fallback to "fix" SSE.** It is what makes a degraded
stream an inconvenience instead of an outage, and removing it converts a
degradation into data loss from the user's point of view. If you find it disabled
during an incident, that is a postmortem trigger, not a fix.

## Escalation

- **L1 (0–15 min):** step 1. `1a` advancing → degraded only; tell the user and
  open a ticket. `1a` stale → this is L2.
- **L2 (1 h):** the fallback is not working, or `sse_reconnects_total` is climbing
  across pods. Check memory and the ingress timeout first. Page the pipeline team
  if processing is also degraded — SSE loss often accompanies a worker stall, and
  the two share the API.
- **L3 (4 h):** SSE down and polling degraded too, or the API is OOMKilling pods.
  That is an availability incident: [`../provider-outage.md`](../provider-outage.md)
  for the dependency cases, [`../db-failover.md`](../db-failover.md) if readiness
  is failing.

## Postmortem trigger

Any of:

- an SSE outage longer than 30 minutes;
- the polling fallback disabled, or found to have been disabled during any
  incident;
- a memory limit raised because of connection count — record the new number and
  the request that justified it;
- `Last-Event-ID` resume verified broken by a test rather than by assumption;
- `sse.payload_dropped_total` found non-zero in production and not investigated;
- the monitoring gap above still open at the time of a second SSE incident —
  which is the finding this task records, and it is owned by 038.

## Access and audit

- `$PROJECT_JWT` is a **project Viewer** token, not an admin token. The
  diagnostics you reach for in step 2 are `Service`/`TenantAdmin` only
  (`RequireTenantAdmin`; anonymous 401, `ProjectViewer` 403 —
  `docs/operations/support-access.md`). A 403 from `/admin` while a Viewer token
  is in your shell is the policy working.
- The `?access_token=` form exists because `EventSource` cannot set an
  authorization header. It is a short-TTL, single-use-scoped token and it is
  **never logged**; if you find one in a log line, that is a security defect and
  it belongs in this postmortem's blast radius.
- Raising a memory limit, restarting the API, or draining connections is a
  **privileged production change** and must be attributable in `audit_events`
  with a named actor. The request and the justification go in the change record,
  not in a ticket body; a memory-limit raise with no recorded request is a
  permanent capacity cost that the next incident re-derives from scratch.
- No token, cookie, signed URL or tenant id from this page goes into a ticket.
  The project id above is synthetic (`prj_01HZYABCDEFG`) and `$PROJECT_JWT` is a
  shell variable, never a value.
