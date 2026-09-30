# Review surge

Human review has fallen behind the pipeline's output. Quality items are waiting
on people, and the delay is either a capacity problem (too many reviewers) or a
routing problem (work is not reaching the people who can do it).

## Symptoms

- `pendingReviews` on the admin usage endpoint is rising, or above its SLO.
- `review_backlog_age` (oldest unresolved item) exceeds its target.
- Users report that finished runs sit at "awaiting review" for a long time.
- `review.resolved` events are arriving at a lower rate than `review.created`.
- The reviews dashboard shows work concentrated in one language pair or one
  voice set, with other pairs idle.

That last one is the routing failure and it is the one that gets misdiagnosed as
a headcount problem. Adding reviewers to a queue that is routed by the wrong
predicate does nothing.

## Five-minute triage

```bash
# 1. Size and shape of the backlog.
curl -fsS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/usage" | jq '.backlog'

# 2. Age distribution, not just count. A large backlog of hour-old items is
#    healthy; a small backlog containing a week-old item is not, and the mean
#    hides that.
psql "$DB" -c "
  SELECT date_trunc('hour', created_at) AS hour, count(*)
  FROM review_items WHERE status = 'Pending'
  GROUP BY 1 ORDER BY 1 LIMIT 24;"

# 3. Is it concentrated? Routing bug or headcount?
psql "$DB" -c "
  SELECT language_pair, count(*) FROM review_items
  WHERE status = 'Pending' GROUP BY 1 ORDER BY 2 DESC;"

# 4. Are reviewers actually able to see it? A permission change can strand a
#    backlog that no one is assigned.
curl -fsS -H "Authorization: Bearer $REVIEWER_JWT" \
  "https://api.<env>/api/v1/reviews?status=Pending" | jq '.total'
```

Step 2 is the one that changes the answer. Count alone is a bad signal: a
steady-state backlog of 200 with a 30-minute age is the system working, and
closing it would be wrong. The finding is the **age distribution's tail**.

Step 3 separates the two causes. Concentrated in one language pair is routing —
reviewers are matched on a predicate that no longer matches the incoming work.
Spread evenly is genuine headcount.

## Mitigation

**Concentrated (routing)**: do not add reviewers. Find the predicate.

```bash
# Which pairs have reviewers assigned, and which have pending items?
psql "$DB" -c "
  SELECT r.language_pair, count(DISTINCT r.user_id) AS reviewers,
         count(i.id) FILTER (WHERE i.status = 'Pending') AS pending
  FROM reviewer_assignments r
  LEFT JOIN review_items i ON i.language_pair = r.language_pair
  GROUP BY 1 ORDER BY 3 DESC NULLS LAST;"
```

A pair with pending items and zero reviewers is a configuration gap, not a
hiring problem. Assign reviewers, and check whether a new language pair or voice
set was added without updating the assignment rule.

**Spread evenly (headcount)**: surface the backlog. Reviews are human work with
a latency budget; the mitigation for "too much work" is more people looking, now.

- Post the backlog age in the release channel. A silent backlog is how a
  four-hour delay becomes a four-day one.
- Consider temporarily narrowing what requires review (if quality gates are
  configurable per language pair) rather than letting everything queue. This is
  a quality decision, not an operations one — escalate rather than deciding it
  here.

**A stranded backlog** (step 4 shows zero for a reviewer who should see items):
check permissions before reassigning. A `review:write` scope change or a
tenant membership change strands work silently, and reassigning reviewers papers
over a bug that will recur.

**Never auto-resolve or auto-approve to clear a backlog.** Review decisions are
human judgements recorded in `review_decisions`; a mass resolution destroys the
quality signal the pipeline exists to produce, and the audit trail shows it.

## Escalation

- **L1 (0–1 h)**: steps 1–4. A routing gap found here is a configuration fix and
  needs no escalation.
- **L2 (4 h)**: `review_backlog_age` above target for more than 4 hours, or
  pending count rising for a full business day. Page the product owner — from
  there it is a capacity commitment, not an incident.
- **L3 (next business day)**: backlog age exceeding a day. Escalate to the
  tenant's account contact as a service-quality issue; at that point the delay is
  visible to the customer.

## Postmortem trigger

Any of:

- review backlog age above its SLO target for more than one business day;
- a routing gap that stranded a backlog (a pair with pending items and no
  reviewers);
- review decisions made without a human — whether manual or automatic;
- a reviewer-permission change that removed access to pending work.
