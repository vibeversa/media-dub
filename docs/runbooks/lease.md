# Lease Recovery

## Symptoms

- Alert `LeaseRecoveryRateHigh` (`leases.recovered / stages.started > 1%`).
- Workers log `Lease lost` / `LeaseLostException`.

## Checks

```bash
curl -H "Authorization: Bearer $ADMIN_JWT" \
  "http://localhost:8080/api/v1/admin/leases/status?page=1&pageSize=100"
curl -f http://localhost:8080/metrics | grep -E "leases_recovered|stages_retry"
```

SQL: `SELECT id, stage_type, lease_owner, lease_expires_at, status FROM stage_executions WHERE status='Running' ORDER BY lease_expires_at;`

## Remediation

1. Identify crashed workers (deploy restarts, OOM, 5m TTL expiry).
2. Verify `StageLeaseTimeoutConsumer` recovered units to `RetryPending` and
   the dispatcher requeued them.
3. If recovery stalls, trigger manual retry for the blocked stage (new
   attempt, downstream invalidation preserved).
4. Check worker concurrency and media timeouts (`Media:FfmpegTimeoutSec`).

## Escalation

L1 (15m) → L2 platform (1h). See `escalation.md`.
