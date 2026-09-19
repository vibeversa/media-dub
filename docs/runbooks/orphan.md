# Orphan Reconciliation

## Symptoms

- Alert `OrphanRateHigh` (`storage.orphans` growth).
- `OrphanObjectReconciler` logs quarantines.

## Checks

```bash
curl -f http://localhost:8080/metrics | grep -E "storage_orphans"
```

SQL: committed content with zero artifact refs older than 7d.

## Remediation

1. Trigger `OrphanObjectReconciler.ReconcileAsync` manually (daily job is the
   backstop).
2. Verify quarantine prefix `quarantine/` and `Orphaned` content rows.
3. Physical deletion requires retention holds clear + refcount zero + expiry
   (Task 37 `RetentionService.SweepAsync`); never delete by hand.
4. Investigate upload/commit failures causing the spike.

## Escalation

L2 (4h). See `escalation.md`.
