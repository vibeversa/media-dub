# SSE degraded

`GET .../progress/stream` (`text/event-stream`) is not delivering events, or is
delivering them late, or the client has fallen back to polling.

The system is still correct in this state — progress and notifications are
eventually consistent through the polling path — which is exactly why it is easy
to leave unfixed for days.

## Symptoms

- The progress bar advances in visible steps rather than smoothly, or only on
  navigation.
- Notifications appear minutes late, or on the next page load.
- The browser console shows repeated `EventSource` reconnects, or the
  connection opens and closes immediately.
- `sse_connections` on the API dashboard is well below its baseline while
  `sse_reconnects` is above it.
- Users report the app "freezing" on the processing page, then catching up.

## Five-minute triage

```bash
# 1. Is the stream itself working, from outside the browser?
#    A token is required, and the query-parameter form is what SSE uses.
curl -N -H "Authorization: Bearer $TOKEN" \
  "https://api.<env>/api/v1/projects/$PID/progress/stream?access_token=$TOKEN" \
  --max-time 10

# 2. Connection count and reconnect rate, per pod.
kubectl -n <ns> top pods -l app.kubernetes.io/component=api
kubectl -n <ns> logs deploy/api --since=10m | grep -c 'sse.reconnect'

# 3. Is this one user or everyone? One user is almost always their network.
```

Step 1 is the one that separates "SSE is broken" from "this user's proxy is
breaking SSE". A corporate proxy that does not support long-lived responses, or a
browser extension, produces exactly the same client symptoms as a server fault,
and the server-side evidence (`sse_connections` normal) confirms it in one
number.

**Check `Last-Event-ID` handling if reconnects are the symptom.** A reconnect
that does not resume produces a gap the client fills by re-fetching the
workspace — which works, and looks like "SSE is broken" to a user.

## Mitigation

**If the ingress is closing the connection**, raise the timeouts. The Ingress
carries `proxy-read-timeout: 600` for this reason. A default 60-second read
timeout closes an idle SSE stream every minute, which the client reconnects from,
which looks identical to an application fault:

```bash
# Confirm: does a connection survive longer than the ingress read timeout?
curl -N -H "Authorization: Bearer $TOKEN" \
  "https://api.<env>/api/v1/projects/$PID/progress/stream" --max-time 120
```

**If the API is the bottleneck**, the cause is almost always per-connection
memory: each SSE connection holds a subscription and a bounded replay buffer
(last 100 events, headers only). A pod with many connections needs headroom, and
the API's `500m/1Gi` request is sized for request/response traffic, not for
thousands of long-lived responses. Raise the memory limit before adding replicas
— a replica costs a database connection per pod, and this is not a
connection-count problem.

**If it is one user's network**, there is nothing to fix server-side. The client
already falls back to polling, and the fallback is correct. Tell the user; do not
change the server for one report.

**Never disable the polling fallback to "fix" SSE.** It is what makes a degraded
stream an inconvenience rather than an outage, and removing it converts a
degradation into data loss from the user's point of view.

## Escalation

- **L1 (0–15 min)**: steps 1–3. One user with normal server metrics is a client
  or network issue — close it with an explanation.
- **L2 (1 h)**: server-wide, or `sse_connections` collapsed across pods. Check
  memory and the ingress timeout before anything else. Page the pipeline team if
  processing is also degraded — SSE loss often accompanies a worker stall.
- **L3 (4 h)**: SSE down and polling degraded too, or the API is OOMKilling pods.
  That is an availability incident: see [`provider-outage.md`](provider-outage.md)
  for the dependency cases.

## Postmortem trigger

Any of:

- an SSE outage longer than 30 minutes;
- the polling fallback disabled, or found to have been disabled during any
  incident;
- a memory limit raise caused by connection count — record the new number and
  the request that justified it;
- `Last-Event-ID` resume verified broken by a test rather than by assumption.
