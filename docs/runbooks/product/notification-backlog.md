# "I never got the notification"

**What a user says:** "I didn't get told the run finished", "the notification
showed up hours later", or the sharpest version — "the badge says 3 unread but
the list is empty".

The backlog is in the database; the delivery channel is separate and may be
perfectly healthy while the backlog is not. Those are two different incidents and
conflating them sends the investigation to the wrong layer.

The mechanism page is [`../notification-backlog.md`](../notification-backlog.md) —
same name, deliberately. It owns the producer, the projector and the queue. This
page owns the user-visible symptom and the one query that separates a delivery
fault from a production fault.

## Signals

| | |
| --- | --- |
| Monitor / dashboard | `PipelineSuccessLow` and `DLQDepthNonZero` in `deploy/observability/alerts.yml` are the adjacent rules; `reviews.json` carries the review backlog. **GAP — there is no notification rule at all.** Nothing in the alerts file measures a notification backlog, and no panel reads `notifications_projection_failures_total`, which **is** emitted (`BackendMetrics.NotificationProjectionFailures` on meter `DubbingPlatform.Observability`). **Owner: 038.** |
| Diagnostics view (036) | `/admin` → ops → **errors** panel is the one that matters here: it lists dead-letter reasons by code. `GET /api/v1/admin/diagnostics/queues` for depth, `GET /api/v1/admin/diagnostics/dlq` for depth and reasons. |
| Mechanism page | [`../notification-backlog.md`](../notification-backlog.md) |
| Rollback (043B) | [`../../rollout.md`](../../rollout.md) only if a release changed the projector. A backlog is a capacity or producer fault; rolling back rarely helps and the outbox ordering makes it a poor first move. |

## Symptoms

- Users report missing notifications, or notifications arriving hours late.
- The notification list is **correct** but new items appear only on a page load —
  the row is written, the user is not told. That is the projection, not delivery.
- The badge count and the list disagree. `GET /api/v1/notifications/unread-count`
  and `GET /api/v1/notifications` disagree for the same user.
- `notification.created` SSE events are absent while `stage.progress` and
  `run.status_changed` are flowing.

That last combination is the diagnostic one and it is the reason SSE is worth
one check: **the transport is up and one event type is missing**, so the producer
is not emitting. Treating it as an SSE problem sends you to the ingress.

- A row with a `project_id` the user cannot open. The notification is real and
  the project is archived or soft-deleted.

## Five-minute triage

**Step 1: is the row in the database?** This single question splits the incident
in half, and it is a `GET` from a user token — not an admin call, not a database
connection.

```bash
# 1a. What the user sees.
curl -sS -H "Authorization: Bearer $USER_JWT" \
  "https://api.<env>/api/v1/notifications?page=1&pageSize=20"

# 1b. What the badge says. Same token, same tenant, one number.
curl -sS -H "Authorization: Bearer $USER_JWT" \
  "https://api.<env>/api/v1/notifications/unread-count"
```

| 1a | 1b | Verdict |
| --- | --- | --- |
| newest notification is recent | matches the list | **not a delivery fault** — the user is not being *pushed* to; this is [`sse-outage.md`](sse-outage.md) |
| newest is hours old | matches | **producer fault**: nothing is being written |
| newest is recent | higher than the list's unread rows | **a projection or expiry fault** — the count and the list disagree |
| 401 | 401 | not this — [`auth-outage.md`](auth-outage.md) |

`1a` recent + `1b` matching is the most common outcome and the most commonly
mis-escalated: the notification **exists** and the user simply was not told. That
is a stream problem, and the fix is in [`sse-outage.md`](sse-outage.md).

**Step 2: unread count against unread rows, for one user.** The list endpoint
filters expired rows (`expires_at`); the count is the one that can drift, because
an expiry that was not applied to the counter leaves a badge that never clears.

```bash
# From an admin session, for the affected user. Synthetic id below.
psql "$DB" -c "
  SELECT
    (SELECT count(*) FROM notifications
      WHERE recipient_user_id = 'de710000-0000-4000-8000-000000000011'
        AND read_at IS NULL)                              AS unread_rows,
    (SELECT count(*) FROM notifications
      WHERE recipient_user_id = 'de710000-0000-4000-8000-000000000011'
        AND read_at IS NULL
        AND (expires_at IS NULL OR expires_at > now()))    AS unread_unexpired,
    (SELECT count(*) FROM notifications
      WHERE recipient_user_id = 'de710000-0000-4000-8000-000000000011'
        AND read_at IS NULL AND expires_at <= now())      AS unread_expired;"
```

`unread_expired > 0` with a badge above `unread_unexpired` is the cause, and it is
a **data** fault, not a code fault: the counter and the filter disagree about
what expired means. Check the retention sweep before looking at the projector.

**Step 3: is the producer emitting?** The `de71…`-prefixed check is for the
diagnostics, not for a user.

```bash
# Backlog shape over 24 h, not just the current number. A steady 200 with a
# 30-minute age is the system working.
psql "$DB" -c "
  SELECT date_trunc('hour', created_at) AS hour, count(*)
  FROM notifications WHERE created_at > now() - interval '24 hours'
  GROUP BY 1 ORDER BY 1;"

# The projection failures the API already counts.
curl -fsS http://localhost:8080/metrics | grep -E '^notifications_' || \
  echo "no notifications_ series exposed"
```

**Step 4: the queue, if the rows are being written but not delivered.** Via
`/admin` → ops → queues, or:

```bash
curl -sS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/diagnostics/queues"
```

Depth rising with rows being written means the consumer is behind. Depth at zero
with no rows means the producer stopped. One number, two opposite responses.

## Degraded-mode triage (no dashboard, no metrics)

There is no notification alert and no panel, so the logs and the database are the
path. Both of these work with a `psql` and a log stream and nothing else.

```bash
# 1. Is the projector running? A projector that threw on every message leaves a
#    gap in its own log and no error anywhere the user can see.
kubectl -n <ns> logs deploy/api --since=15m | grep -ciE 'notification.*(project|dispatch)'

# 2. Dead-letter reasons, by code. This is the admin ops "errors" panel as plain
#    text, and the codes are the public ErrorCodes catalog.
kubectl -n <ns> logs deploy/api --since=15m \
  | grep -oE 'NOTIFICATION_[A-Z_]+|SCHEMA_VERSION_MISMATCH|DLQ' | sort | uniq -c | sort -rn

# 3. The DLQ depth without the diagnostics API. A non-zero depth is the single
#    most common cause and it is one grep away.
curl -fsS http://localhost:8080/metrics | grep -E '^dlq_depth'

# 4. Is a worker holding a lease it is not making progress on? A wedged consumer
#    looks exactly like a slow one from the user's side.
curl -sS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/diagnostics/leases" | head -c 2000
```

Step 2's `SCHEMA_VERSION_MISMATCH` deserves its own line. The outbox carries a
`SchemaVersion`; a consumer built against an older envelope parks the message
rather than failing loudly, and the parked message is invisible to the user and
to every dashboard. It is a **release-ordering** fault, and the fix is the
mechanism page's forward-fix — not a redrive, which will park it again.

## Mitigation

**Rows are not being written (producer):** find the event that should have
produced them. If `stage.progress` and `run.status_changed` are flowing and
`notification.created` is not, the notification branch of the projector is not
firing — a code or flag condition, not a queue condition. Do not drain anything.

**Rows are written and delivered late (consumer behind):** the queue depth tells
you whether to scale or to wait. If depth is climbing, the consumer is
under-provisioned; raise replicas or the concurrency limit, and record the
request. Do not redrive live messages — a redrive of a message that is already
being processed is a duplicate, and `notifications` has a
`(tenant_id, recipient_user_id, source_event_id)` unique index specifically to
absorb that, but the absorb is not free and it turns one duplicate into a
silent one.

**Messages are in the DLQ:** redrive from `/admin` → ops → dlq. This is a
**privileged, audited, destructive-capable action** — read
[`../dlq.md`](../dlq.md) first, and check the reason code before redriving.
A `SCHEMA_VERSION_MISMATCH` redrive is a guaranteed re-park; deploy the consumer
that understands the envelope, then redrive.

**The badge and the list disagree:** the expired-unread rows from step 2 are the
cause. This is a **data** fix. Before deleting anything, note that a physical
delete on a notification row is a user-visible loss; prefer letting the expiry
filter do its job and fixing the counter. If the count query is the wrong one,
the fix is the query, and it belongs in a migration rather than in a manual
`UPDATE`.

**A notification points at an archived project:** not a fault. The row is
correct; the UI is expected to link to an archived project. Confirm with the
user before changing anything.

**Never:** delete notifications to "clear the backlog". The backlog is the
evidence, and the rows are the user's data. A responder who empties
`notifications` to make a dashboard green has destroyed the only record of what
was lost, and the drill in [`../../backup.md`](../../backup.md) will restore
them from an archive while the cause is still unknown.

## Escalation

- **L1 (0–15 min):** step 1. Rows present and the count matches → a push problem,
  hand to [`sse-outage.md`](sse-outage.md) and close with an explanation.
- **L2 (1 h):** the rows are not being written, or the DLQ is non-zero. Page
  platform. If `DLQDepthNonZero` has paged, the mechanism page is the page.
- **L3 (4 h):** the producer is stopped *and* the DLQ is growing. Two
  independent faults, or one fault with two symptoms. Go to
  [`../escalation.md`](../escalation.md) and preserve the DLQ contents before
  redriving — the reason codes are the only thing that explains the backlog.

## Postmortem trigger

Any of:

- a notification backlog that exceeded its SLO target, with the peak recorded;
- any DLQ redrive, with the reason codes and the count recorded;
- the unread count and the list found disagreeing in production;
- a message parked for a schema version and redrived before the consumer was
  upgraded — a redrive that is known to fail is a decision that needs a name
  attached to it;
- notifications deleted to reduce a backlog, for any reason.

## Access and audit

- `/admin` ops panels — queues, dlq, leases, errors — require **`Service` or
  `TenantAdmin`** (`RequireTenantAdmin`; anonymous 401, `ProjectViewer` 403 —
  `docs/operations/support-access.md`). The read-only panels in this page's
  triage are the exception: `/api/v1/notifications` and `/unread-count` are
  scoped to the calling user, which is why step 1 uses a user token and not an
  admin one.
- A **DLQ redrive or discard** is destructive-capable and must be attributable in
  `audit_events` with a named actor, a reason and a count. The UI exposes both
  actions; there is no "just clear it" that is not an audited action.
- Manual `DELETE` on `notifications` is a user-data deletion. It is not covered
  by the retention policy, it is not a retention sweep, and it must be recorded
  as a deletion with its scope.
- A user's notification `title` and `body` are user content, and this runbook's
  commands print neither — `title` and `body` are quoted in the seed only because
  the shape of a notification is part of what is being drilled. Do not paste
  notification rows, tokens, cookies or signed URLs into a ticket: use the count,
  the code, the timestamp and the `correlation_id`, which is the field designed
  for exactly this. The `de71…` id in this page is synthetic and
  `probe@drill.invalid` is a reserved-TLD address that cannot resolve.
