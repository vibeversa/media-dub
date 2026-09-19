# Review Backlog

## Symptoms

- `reviews.opened` outpaces `reviews.resolved`; runs stuck
  `ManualReviewRequired`.
- Export success drops (blocked by unresolved required reviews).

## Checks

```bash
curl -H "Authorization: Bearer $ADMIN_JWT" \
  "http://localhost:8080/api/v1/admin/reviews/backlog?page=1&pageSize=20"
curl -f http://localhost:8080/metrics | grep -E "reviews_opened|reviews_resolved|exports_"
```

## Remediation

1. Triage oldest reviews first (backlog is oldest-first).
2. Approve/Reject/Requeue/Resolve-with-edit via review APIs; reject never
   resumes the run (blocked until requeue/retry) by design.
3. Runs resume `ManualReviewRequired → Running` only at zero open reviews.
4. If backlog is systemic (threshold too strict), adjust quality thresholds
   via config, not by bulk-approving.

## Escalation

L1 (1h) → L2 platform. See `escalation.md`.
