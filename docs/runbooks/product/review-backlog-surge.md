# "My project has been waiting for review for days"

**What a user says:** "it says awaiting review since Tuesday", "I finished it
last week and nobody looked at it", or the one that is a routing bug wearing a
headcount problem — "my colleague's project was reviewed the same day and mine
hasn't been touched".

The mechanism page is [`../review-surge.md`](../review-surge.md) — it owns the
backlog, the review items and the reviewer model. This page owns the symptom and
it **links the UI path rather than restating it**: a responder who explains the
review studio's controls from memory will describe a UI that does not match the
one the user is looking at. Use [`../../operations/…`] links and the in-product
path; do not paraphrase the UX.

## Signals

| | |
| --- | --- |
| Monitor / dashboard | `ExportSuccessLow` in `deploy/observability/alerts.yml` carries `runbook: docs/runbooks/review-backlog.md` — the **one rule in the repository that points at a review runbook** — and `reviews.json` is the dashboard with the review backlog panel. That is the closest thing to a review signal that exists. `BackendMetrics.ReviewLatency` (`review.latency_ms`) **is** emitted and is not on `reviews.json`. **Owner: 038.** |
| Diagnostics view (036) | `/admin` → ops, and `GET /api/v1/admin/diagnostics/review-backlog` (the 036 diagnostics view, distinct from `GET /api/v1/admin/reviews/backlog` on the older path). `GET /api/v1/admin/usage` carries the backlog counters. |
| Mechanism page | [`../review-surge.md`](../review-surge.md) and [`../review-backlog.md`](../review-backlog.md) |
| Rollback (043B) | [`../../rollout.md`](../../rollout.md) only if a release changed the review routing or the quality gate. A backlog is a **capacity or routing** fault by definition, and neither is fixed by reverting code. |

## Symptoms

- Finished runs sit at "awaiting review" for hours or days.
- The reviews dashboard shows work concentrated in one language pair or one voice
  set while other pairs are idle.
- A project is stuck in review while an identical project was reviewed same-day.
- `review.resolved` events arriving at a lower rate than `review.created`.
- Review items exist that **no reviewer can see**. A permission change or a
  membership change strands a backlog that nobody is assigned, and it looks
  exactly like a headcount problem.
- Items appear and disappear from the queue. That is the reviewer pool rotating
  through them, which is a working system — a small backlog with a short age.

## Five-minute triage

**Step 1: the backlog's AGE distribution, not its size.** This is the single most
important number in the page, and it is the one a dashboard rarely gives you.

```bash
# Count alone is a bad signal. A steady 200 with a 30-minute age is the system
# working, and closing it would be wrong. A small backlog containing a week-old
# item is an incident.
psql "$DB" -c "
  SELECT date_trunc('hour', created_at) AS hour, count(*)
  FROM review_items WHERE status = 'Pending'
  GROUP BY 1 ORDER BY 1 LIMIT 24;"

# The tail, which is the actual SLO.
psql "$DB" -c "
  SELECT
    count(*)                                              AS pending,
    max(now() - created_at)                               AS oldest,
    percentile_cont(0.95) WITHIN GROUP (ORDER BY now() - created_at) AS p95_age
  FROM review_items WHERE status = 'Pending';"
```

**Step 2: is it concentrated?** This separates the two causes, and the two have
opposite responses. Adding reviewers to a queue that is routed by the wrong
predicate does nothing, and it is the most common wasted hour in this whole
directory.

```bash
# Routing or headcount, in one query.
psql "$DB" -c "
  SELECT language_pair, count(*) AS pending,
         max(now() - created_at) AS oldest
  FROM review_items WHERE status = 'Pending'
  GROUP BY 1 ORDER BY 2 DESC;"

# The 036 diagnostics view, for the queue-shaped version of the same question.
curl -sS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/diagnostics/review-backlog"
```

**Concentrated** in one language pair, one voice set, or one reviewer is a
**routing** fault. **Spread evenly** across pairs and reviewers is genuine
**headcount**.

**Step 3: can a reviewer actually see the item?** Run it *as a reviewer*. This is
the step that finds the stranded backlog, and it is the one that gets skipped
because an admin session sees everything.

```bash
# A REVIEWER token, not the admin token from step 2. A project Viewer with
# membership on the project must be able to list the item.
curl -sS -H "Authorization: Bearer $REVIEWER_JWT" \
  "https://api.<env>/api/v1/reviews?status=Pending" | jq '.total'
```

An admin total of 400 and a reviewer total of 380 is a **permissions** incident,
and it presents to users as "nobody is reviewing". `project_memberships` is one
of the seven tables the backup drill covers
([`../../backup.md`](../../backup.md)) for exactly this reason: a project whose
memberships were lost shows its members a 403 and its admins an empty queue.

**Step 4: is the pipeline producing faster than people can clear?** A surge and
an outage have different responses, and the ratio is one number.

```bash
psql "$DB" -c "
  SELECT
    (SELECT count(*) FROM activity_events
      WHERE type = 'ReviewRequested' AND occurred_at > now() - interval '24 hours') AS opened_24h,
    (SELECT count(*) FROM activity_events
      WHERE type = 'ReviewResolved'  AND occurred_at > now() - interval '24 hours') AS resolved_24h;"
```

`opened_24h` growing faster than `resolved_24h` is a **surge**: the backlog will
grow no matter how many reviewers are added, and the correct response is a
scheduled catch-up, not an emergency headcount. `opened_24h ≈ resolved_24h` with
a long `oldest` is a **routing** fault. `opened_24h ≈ 0` with a long `oldest` is
a **producer** fault: items are not being created, and the backlog is frozen
rather than growing — the least urgent of the three and the one most often
misread.

## Degraded-mode triage (no reviews dashboard, no metrics)

`reviews.json` exists, so this path is for when Grafana is unreachable — a
datasource outage during the incident you are trying to work. Everything below
is a database session and a log stream.

```bash
# 1. The three questions above, in one pass, no dashboards.
psql "$DB" -c "
  SELECT status, count(*), max(now() - created_at) AS oldest
  FROM review_items GROUP BY 1 ORDER BY 2 DESC;"

# 2. Is a reviewer pool actually assigned? An unassigned backlog is stranded and
#    no amount of throughput fixes it.
psql "$DB" -c "
  SELECT r.id, r.role, count(ri.id) AS pending
  FROM project_memberships r
  LEFT JOIN review_items ri ON ri.project_id = r.project_id AND ri.status = 'Pending'
  WHERE r.role IN ('Reviewer','Editor','Owner')
  GROUP BY 1, 2 ORDER BY 3 ASC LIMIT 20;"

# 3. Were items created and then never resolved, or created and lost? The
#    ReviewRequested/ReviewResolved activity pairs answer it without the
#    review_items table being trustworthy.
psql "$DB" -c "
  SELECT type, count(*) FROM activity_events
  WHERE type IN ('ReviewRequested','ReviewResolved')
    AND occurred_at > now() - interval '48 hours'
  GROUP BY 1;"

# 4. The review latency histogram, if /metrics is up. Emitted, ungraphed.
curl -fsS http://localhost:8080/metrics | grep -E '^review_latency' || \
  echo "no review_latency series exposed"
```

Step 2 is the one that finds the stranded backlog, and it is worth stating why
the query is written with a `LEFT JOIN` and sorted ascending: a reviewer with
**zero** pending items is either idle or cannot see the work. The `NULL`/zero
rows at the top are the interesting ones, and sorting descending would hide them
behind the reviewers who have plenty.

**The user-facing path, linked rather than restated.** Do not explain the review
studio's controls from this page. Point the user at the product: the review
studio is reached from the project's Reviews tab, and the item carries the segment
it is asking about and its context. If the UI does not offer the control you are
about to recommend, that is a product finding — file it, do not work around it by
telling the user to use the API.

## Mitigation

**Concentrated (routing):** do **not** add reviewers. Find the predicate — which
pairs have reviewers assigned and which have pending items (step 2 of the
degraded path answers this directly). A reviewer pool matched on a predicate that
no longer matches the incoming work is a code/config fault; adding headcount to it
raises the cost and does not change the queue. This is the mitigation most likely
to be got wrong, because adding a reviewer is visible and feels like progress.

**Spread evenly (headcount):** two options, and the choice is a product
decision, not an on-call one.
- *Wait it out*, if `opened_24h ≈ resolved_24h` and the age is within the
  team's normal. Closing a healthy backlog is a user-visible change to someone's
  work.
- *Add reviewers*, if the backlog is growing. This is a **privileged change**
  (memberships) and it is audited.

**Stranded by permissions:** restore the membership. `project_memberships` is the
authorization state, and a project whose memberships are missing shows its
members a 403 — which they report as "the review screen is broken", not as "I
lost access". Check the backup drill's coverage of that table
([`../../backup.md`](../../backup.md)) if the memberships went missing rather than
were never set.

**Frozen (producer):** items are not being created. This is upstream of reviews
entirely — the quality gate or the export path is not producing review items. Go
to [`../dlq.md`](../dlq.md) if there are dead-lettered messages, because a parked
message is the most common reason a producer stops.

**A surge:** a scheduled catch-up, agreed with the team, with a date. Do not
promote items to clear the queue. A promotion is a quality decision, it is
recorded in `review_decisions`, and a responder making forty of them at once to
make a dashboard green has made a quality decision nobody asked for.

**Never:** dismiss or auto-approve review items to reduce the backlog. `dismiss`,
`reopen`, `resolve-with-edit` and `approve` are four different actions with four
different audit records, and "clear the queue" is not one of them.

## Escalation

- **L1 (0–15 min):** steps 1 and 2. A steady backlog with a short age is the
  system working — close it with the numbers, not an escalation.
- **L2 (1 h):** the `oldest` is beyond the team's normal and the cause is
  identified as routing or permissions. Page the product owner: a routing
  predicate is their code and their release.
- **L3 (4 h):** a surge the team cannot absorb, a producer that has stopped, or
  memberships lost. Membership loss involves
  [`../../backup.md`](../../backup.md) — it may be a restore question rather than
  an incident.

## Postmortem trigger

Any of:

- a review backlog older than the team's stated normal, with the peak and the
  cause recorded;
- headcount added to a **routing** fault, with the two costs recorded separately
  (the headcount, and the delay the routing fault caused);
- a stranded backlog caused by a membership or permission change, with the number
  of affected projects;
- a surge cleared by promoting or dismissing items rather than by reviewing them;
- `review_decisions` written by an operator session rather than by a reviewer, at
  any volume.

## Access and audit

- `GET /api/v1/admin/diagnostics/review-backlog`, `/reviews/backlog` and
  `/usage` require **`Service` or `TenantAdmin`** (`RequireTenantAdmin`;
  anonymous 401, `ProjectViewer` 403 —
  `docs/operations/support-access.md`). Step 3 deliberately uses a **reviewer**
  token instead, because the question "can a reviewer see this?" cannot be
  answered with a token that can see everything.
- Adding a reviewer is a `project_memberships` write. It is authorization state,
  it is privileged, and it must be attributable in `audit_events` with the
  project, the user and the reason. Granting yourself a membership so you can see
  a backlog is the specific anti-pattern: it changes what you can see *and* what
  you can do, and it is invisible to the people who review the audit later.
- `approve`, `reject`, `resolve`, `dismiss`, `reopen` and `resolve-with-edit` all
  write `review_decisions`. Each is a quality judgement about someone's work and
  each is audited separately. Do not batch them to clear a metric.
- Review content — the segment text, the reviewer notes — is user content. Report
  counts, ages, roles and language pairs; do not paste items into a ticket. The
  `de71…` tenant id above is synthetic.
