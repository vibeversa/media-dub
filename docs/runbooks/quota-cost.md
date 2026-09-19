# Quota / Cost Incident

## Symptoms

- 429 `QUOTA_EXCEEDED` / `RATE_LIMITED` spikes.
- Cost dashboard shows `cost.reserved` outpacing `cost.reconciled`.

## Checks

```bash
curl -f http://localhost:8080/metrics | grep -E "quota_rejections|ratelimit_rejections|cost_"
```

PromQL: `sum by (dimension) (rate(quota_rejections_total[15m]))`.

## Remediation

1. Identify top tenant/dimension (tenant label is safe, no PII).
2. Quota denials are fail-closed (correct); do not raise limits without L2
   approval. Rate denials are fail-open on Redis outage — check Redis first.
3. Reconcile stuck `Reserved` rows via `CostService.ReconcileAsync` with
   provider-reported actuals.
4. Adjust `Quota:*` / `RateLimit:*` only via config change + deploy.

## Escalation

L2 (1h). See `escalation.md`.
