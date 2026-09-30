# Upload surge

Upload volume or concurrent sessions has exceeded what the media path was sized
for. The pipeline is slow, storage is filling, or uploads are being rejected.

## Symptoms

- Uploads fail with 507, `PREVIEW_QUOTA_EXCEEDED`, or a storage-quota error.
- Uploads succeed but processing does not start, or queues are deep on
  `media.prep` / `media.render`.
- `media-prep` and `media-render` pods are at their KEDA maximum.
- `storage_used / storage_quota` is above 0.9 on the cost dashboard.
- Users report uploads that "hang" at a percentage and then fail.
- `MediaBombTests` or the media concurrency limit appears in a recent log.

## Five-minute triage

```bash
# 1. Is this volume, or is something stuck?
#    Both look the same from the user's side: a percentage that does not move.
curl -fsS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/usage" | jq '{storage, activeRuns, backlog}'

# 2. Queue depth per stage, and whether it is growing.
#    (queues.json dashboard; or per-pod claim metrics)
kubectl -n <ns> get pods -l app.kubernetes.io/component=worker-media-prep
kubectl -n <ns> logs deploy/worker-media-prep --since=10m | grep -c 'claim'

# 3. Storage headroom. This is the one that ends the incident on its own.
curl -fsS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/usage" | jq '.storage'

# 4. Per-tenant concentration: one tenant, or everyone?
psql "$DB" -c "
  SELECT tenant_id, count(*) FROM media_assets
  WHERE created_at > now() - interval '1 hour'
  GROUP BY 1 ORDER BY 2 DESC LIMIT 5;"
```

Step 4 decides the response. One tenant at volume is a quota conversation; every
tenant at once is capacity. Treating a single tenant's bulk import as a platform
incident is how an upload quota becomes a platform incident.

**FFmpeg concurrency is a separate limit from replica count.** Each media pod
runs `Media__MaxConcurrentMediaJobs=2`, so three replicas process six files at
once. Adding replicas raises throughput linearly; raising the per-pod value
raises it super-linearly and raises memory and CPU per pod, which is why the
manifest sets it to 2 and the deploy guide says to scale the replica count
instead.

## Mitigation

**Per-tenant over-quota** (step 4 shows one tenant): the quota is doing its job.
Do not raise the global limit. Work with the tenant: they may want
`maxStorageBytes` raised, which is a per-tenant decision with a cost
consequence, and the admin usage endpoint is where it is made.

**Platform-wide, storage-bound** (step 3 above 0.9): storage full is the only
situation where uploads genuinely cannot succeed.

```bash
# Intermediates are the first thing to reclaim, and they are the cheapest:
# finals are what the user paid for, intermediates are what they do not need.
# Lifecycle: intermediates 30 days, finals 90 (RetentionOptions).
# Force the lifecycle now rather than waiting for its schedule.
```

If the lifecycle cannot be forced, the alternative is refusing *new* uploads
cleanly rather than accepting files that then fail during processing — a user
whose upload succeeds and then vanishes at 90% is worse off than one who is told
the quota is full at 0%.

**Platform-wide, CPU-bound** (queues deep, storage fine): scale the media
workers.

```bash
# KEDA scales on CPU 70% + memory 80%; raise the max in keda-scalers.yaml rather
# than hand-scaling, so the change survives a node drain.
kubectl -n <ns> get scaledobject
```

**A single stuck upload holding a scratch PVC**: the media-prep scratch PVC is
50Gi and `ReadWriteOnce`, so a pod that cannot release it blocks a node's
scheduler. Confirm and delete the pod; the PVC is `emptyDir`-backed per pod and
the job is idempotent via the lease.

## Escalation

- **L1 (0–30 min)**: steps 1–4. One tenant is a quota conversation, not an
  incident.
- **L2 (1 h)**: platform-wide surge, or storage above 0.95. Page platform. Begin
  the lifecycle reclaim.
- **L3 (4 h)**: uploads failing for most tenants, or storage is full and cannot
  be reclaimed. This is availability. Consider raising the tenant quota ceiling
  for everyone temporarily — a decision with a cost consequence, so it is L3 and
  recorded.

## Postmortem trigger

Any of:

- storage above 0.95 in any environment;
- a global quota or concurrency limit raised, with the new value recorded
  somewhere durable (the manifest or the options, not only this log);
- uploads accepted and then failed during processing — a quota that admits a file
  it cannot process is a correctness bug, not a capacity one;
- a scratch PVC blocking scheduling.
