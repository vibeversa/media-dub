# DLQ Inspection

## Symptoms

- Alert `DLQDepthNonZero` pages (`dlq.depth > 0 for 5m`).
- Queues dashboard shows `_skipped`/`_error` growth.

## Checks

```bash
curl -f http://localhost:8080/metrics | grep dlq_depth
curl -H "Authorization: Bearer $ADMIN_JWT" \
  "http://localhost:8080/api/v1/admin/dlq/summary"
curl -H "Authorization: Bearer $ADMIN_JWT" \
  "http://localhost:8080/api/v1/admin/leases/status?page=1&pageSize=20"
```

PromQL: `dlq_depth`, `rate(messaging_schema_mismatch_total[15m])`.

## Remediation

1. Classify: schema mismatch (version bump) vs poison (permanent handler
   failure) vs cross-tenant (auth bug).
2. Schema mismatch: deploy reader supporting the new version; replay
   `_skipped` after verification.
3. Poison: fix handler, then replay one message and watch `stages.started`.
4. Never purge without L2 approval and a backup of message bodies.

## Escalation

L1 (15m) → L2 platform (1h) when replay fails or growth exceeds 100 msgs.
See `escalation.md`.
