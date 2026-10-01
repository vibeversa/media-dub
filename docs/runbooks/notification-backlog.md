# Notification backlog

Notification rows are not being delivered, or are being delivered very late.
The backlog is in the database; the delivery channel is separate and may be
healthy while the backlog is not.

## Symptoms

- Users report missing notifications, or notifications arriving hours late.
- `notification_backlog` on the pipeline dashboard is above its SLO target, or
  rising steadily.
- The notification list in the UI is correct but new items do not appear until a
  page load.
- `notification.created` SSE events are absent while other SSE event types
  (`stage.progress`, `run.status_changed`) are flowing.

That last combination is the diagnostic one: SSE is up, and one event type is
missing. It means the *producer* is not emitting, not that the transport is
broken. Treating it as an SSE problem sends the investigation to the wrong layer.

## Five-minute triage

```bash
# 1. How big, and which direction?
#    Rising: the producer is ahead of the drain. Flat and non-zero: the drain
#    stopped. Falling: it is recovering; watch it rather than acting.
curl -fsS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/usage" | jq '.backlog'

# 2. Is the drain running? (The maintenance deployment owns it.)
kubectl -n <ns> get pods -l app.kubernetes.io/component=worker-maintenance
kubectl -n <ns> logs deploy/worker-maintenance --since=15m | tail -40

# 3. Is the SSE channel carrying notification.created at all?
kubectl -n <ns> logs deploy/dubbing-api --since=15m | grep -c 'notification.created'

# 4. Is there a delivery-failure signal?
kubectl -n <ns> logs deploy/worker-maintenance --since=15m | grep -ci 'notify\|delivery.*fail'
```

Step 1 before step 2, because the direction changes what step 2 means. A rising
backlog with a running drain is a **capacity** problem; a flat backlog with a
running drain is a **stuck** problem, and one stuck notification blocks
everything behind it in the queue.

## Mitigation

**A single stuck item blocks the queue behind it.** This is the most common cause
and it is invisible from the metrics, which show a healthy drain rate:

```bash
# Find the oldest undelivered row and look at what it is.
psql "$DB" -c "
  SELECT id, type, created_at, now() - created_at AS age
  FROM notifications
  WHERE delivered_at IS NULL
  ORDER BY created_at ASC LIMIT 5;"
```

If the oldest row is hours old while everything behind it is seconds old, that
row is the block. Inspect it, and if it is genuinely undeliverable, mark it
delivered with its reason recorded — never delete it, because a deleted
notification is a notification a user can no longer be told about.

**If the drain is behind rather than stuck** (row count growing, all rows
recent): scale the drain. It is the `worker-maintenance` deployment, and it is
KEDA-scalable. Raise the replica count before touching anything else.

**If `notification.created` is absent from the SSE stream** while other event
types flow: the emitter is not producing. Check the fan-out worker for the
project's run; the notification row may exist in the database with no
corresponding event, which is a producer bug, not a transport one.

**Never purge the backlog to make the number look better.** A notification table
that has been truncated is a set of users who were never told something. The
metric is the only record that the gap happened.

## Escalation

- **L1 (0–30 min)**: steps 1–2. A rising backlog under an hour is a
  capacity signal, not an incident; record it and watch.
- **L2 (1 h)**: backlog growing for over an hour, or non-zero and flat. Page
  platform. Scale the drain.
- **L3 (4 h)**: notifications lost (not merely late), or a tenant has received
  none for a business day. That is a data-delivery incident.

## Postmortem trigger

Any of:

- the backlog exceeded its SLO target for more than an hour;
- notifications were discarded, purged, or marked delivered without a cause;
- a stuck item blocked the queue for more than 30 minutes;
- the drain was scaled and the new replica count was not recorded in the
  deployment's `resources` — so the next incident re-derives it.

## The product runbook for this

[`product/notification-backlog.md`](product/notification-backlog.md) — or start
from [`product/index.md`](product/index.md), which indexes the set by symptom — is
the same incident indexed by what a **user says** — "I never got the notification", "the
badge says 3 unread but the list is empty". Its first step is the one that splits
this incident in half and is easy to skip: whether the row is in the database at
all. Rows present and the unread count matching means the user is not being
*pushed* to and the fault is in the stream, not in the backlog — a different
runbook. It also covers the expired-row cause for a badge that will not clear,
which is a data fault rather than a projector fault, and states the access
requirement for a DLQ redrive. No mechanism from this page is repeated there.
