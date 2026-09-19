# Storage Outage

## Symptoms

- `/health/ready` 503 with storage check failing.
- Uploads/exports fail with `STORAGE_UNAVAILABLE`; artifacts unverifiable.

## Checks

```bash
curl -f http://localhost:8080/health/ready
curl -f http://localhost:8080/metrics | grep storage_orphans
```

Check bucket existence, credentials expiry, and MinIO/S3 latency.

## Remediation

1. Verify endpoint/bucket/keys from secret manager (no hardcoded secrets).
2. Retry with backoff; in-flight workers rethrow transient I/O into transport
   retry (no manual replay needed).
3. If objects lost, identify affected runs via artifact lineage and re-run
   from the last committed stage (new run, immutable history preserved).
4. Quarantined blobs under `quarantine/` are safe to delete after 7d.

## Escalation

L2 (1h) → L3 storage vendor (4h). See `escalation.md`.
