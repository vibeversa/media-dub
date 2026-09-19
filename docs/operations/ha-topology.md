# HA Topology

Production runs on managed dependencies plus multi-replica stateless
workloads. No stateful component is self-hosted in prod: PostgreSQL,
object storage, the message broker, and Redis are all managed services,
so failover, patching, and AZ spread for state are the provider's
responsibility. The manifests under `deploy/k8s/` only deploy stateless
compute (API + workers), configuration, policies, and autoscaling.

## Managed data plane

| Dependency | Prod posture | HA mechanism |
|---|---|---|
| PostgreSQL 16 (system of record) | Managed instance, multi-AZ standby | Synchronous replica with automatic failover; automated daily base backup + continuous WAL archiving with 7-day PITR window (`docs/dr/backup-restore.md`) |
| Object storage (S3-compatible artifact store) | Managed bucket, prod only | Versioning ON + cross-region replication (CRR) to the DR region; lifecycle rule retains finals for 90 days (`RetentionOptions.FinalDays = 90`) |
| RabbitMQ 3.13 (durable transport) | Managed broker, multi-AZ | Quorum queues for all workload queues (`control.orchestration`, `media.preparation`, `media.render`, `ai.provider`, `ai.gpu`, `export`, `maintenance` per `QueueNames.cs`) + mirrored classic queues where quorum is unavailable |
| Redis 7 (ephemeral only: rate limits, idempotency windows, fair-share counters) | Managed clustered instance | Clustered mode with slot replication; loss is non-destructive — limits fail closed to `QUOTA_EXCEEDED` and rebuild from PostgreSQL on recovery |

Redis is never authoritative. Any Redis loss degrades to safe rejections,
never to lost runs: stage state lives in `stage_executions`, costs in
`cost_reservations`, and audit in `audit_events`.

## Stateless compute

| Deployment (`deploy/k8s/`) | Prod replicas | Scaling (KEDA `keda-scalers.yaml`) |
|---|---|---|
| `api` | 3 | HPA-style static 3; PDB `minAvailable: 1` |
| `worker-control` (saga/dispatcher/sweeper) | 2 | Postgres active-leases query, 10 leases/replica, 1–6; PDB `minAvailable: 1` |
| `worker-media-prep` | 3 | CPU 70% + memory 80%, 2–8; bounded `Media__MaxConcurrentMediaJobs=2` per pod |
| `worker-media-render` | 2 | CPU 70% + memory 80%, 1–6; same per-pod bound |
| `worker-ai` | 3 | `ai.provider` queue length 100, 1–10 |
| `worker-export` | 2 | `export` queue length 100, 1–8 |
| `worker-gpu` | 0 (scales 0–4) | `ai.gpu` queue length 100 + DCGM `dcgm_gpu_utilization` Prometheus trigger; GPU-node-only scheduling |
| `worker-maintenance` (reconciliation/retention) | 1 (`Recreate`) | No scaler by design — singleton |

All rolling deployments use `RollingUpdate` with `maxSurge: 1` and
`maxUnavailable: 0`, so upgrades never reduce serving capacity.
Readiness gates (`/health/ready`, 15 s period, failure threshold 3) remove
unhealthy pods from the Service before they receive traffic, and the
migration initContainer (`/app/efbundle`) blocks API rollout on a broken
schema. During broker outages every broker-scaled object keeps a CPU
trigger plus `fallback.replicas` after 3 consecutive scaler failures, so
compute still scales while the broker scaler errors.

## AZ spread and disruption budgets

- Managed node pools must span at least two availability zones; combined
  with 2–3 replicas per serving deployment, the loss of one AZ leaves
  serving capacity (PDBs `api` and `worker-control`, `minAvailable: 1` in
  `deploy/k8s/pdb.yaml`, keep at least one replica during voluntary
  disruptions such as node drains and upgrades).
- `worker-maintenance` is intentionally a singleton (`Recreate`): its work
  (orphan reconciliation, retention sweeps) is idempotent and re-runs on
  restart; concurrent runners would double-delete.
- Network policy is default-deny (`deploy/k8s/networkpolicies.yaml`); managed
  service CIDRs are allow-listed explicitly (replace the `CHANGE_ME`
  placeholders before first apply).

## What HA does not cover

- Cross-region active/active compute: DR is restore-and-failover, not
  hot-standby (RPO 5 min, RTO 1 h — see `docs/dr/backup-restore.md`).
- GPU pools: `worker-gpu` restores to 0 replicas on clusters without GPU
  nodes by design (`restoreToOriginalReplicaCount: true`).
