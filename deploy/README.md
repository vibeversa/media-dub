# Deploy Guide (Task 40)

CI builds, scans, signs, and publishes all 8 images
(`.github/workflows/ci.yml`); this guide covers cluster rollout with the raw
K8s manifests (primary) or the thin Helm wrapper.

## Prerequisites

- Cluster with ingress-nginx, cert-manager, KEDA, external-secrets, and (for
  GPU) the NVIDIA device plugin + a GPU node pool labeled
  `nvidia.com/gpu.present=true`.
- `ClusterSecretStore dubbing-cluster-store` (see `deploy/k8s/secrets.yaml`
  for the full remote-key list) and all keys populated, including
  `keda-rabbitmq-host` and `keda-postgres-conn` for the KEDA scalers.
- Managed services in prod: PostgreSQL 16, object storage (S3-compatible),
  message broker (RabbitMQ), Redis 7. No self-hosted statefulsets are deployed
  in prod by these manifests (R4). Staging may use managed services or
  in-cluster statefulsets; the NetworkPolicies allow both.
- Prometheus (for the GPU DCGM scaler query) and the NVIDIA DCGM exporter if
  GPU-utilization scaling is wanted; otherwise the `ai.gpu` queue trigger
  alone drives GPU scaling.
- Replace every `CHANGE_ME` (registry org, OTLP endpoint, region, managed
  CIDRs in `networkpolicies.yaml`, example hosts) before applying.
- Task-text mapping: the spec's media `concurrency:2` env is the real
  `Media__MaxConcurrentMediaJobs=2` key (`MediaOptions`, range 1..64); the
  spec's `nvidia.com/gpu.utilization` concept is the DCGM Prometheus query in
  `keda-scalers.yaml`; the spec's `dotnet ef database update` step is the
  self-contained EF bundle `/app/efbundle` (same operation, deploy-safe).

## Deploy order

Apply in this order; each step gates the next:

1. `kubectl apply -f deploy/k8s/namespace.yaml`
2. Secrets: `kubectl apply -f deploy/k8s/secrets.yaml`, then confirm sync:
   `kubectl -n dubbing-prod get externalsecret dubbing-secrets` (SYNCED True)
   and `kubectl -n dubbing-prod get secret dubbing-secrets`.
3. Config: `kubectl apply -f deploy/k8s/configmap.yaml`.
4. Migration job: `kubectl apply -f deploy/k8s/migration-job.yaml`, then
   `kubectl -n dubbing-prod wait --for=condition=complete --timeout=600s job/dubbing-migration`.
   The Job runs `/app/efbundle` (idempotent; concurrent runners serialize on
   `__EFMigrationsHistory`) with the maintenance-role connection string.
   A failed migration BLOCKS the rollout: do not proceed until the Job is
   Complete. (ArgoCD runs this automatically as a PreSync hook; Helm as a
   pre-upgrade hook — annotations are on the Job.)
5. API: `kubectl apply -f deploy/k8s/api-deployment.yaml`. Pods run the
   `wait-for-migrations` initContainer (same bundle) and never start on a
   broken schema. Wait for readiness:
   `kubectl -n dubbing-prod rollout status deploy/api`.
6. Workers: `kubectl apply -f deploy/k8s/workers-*.yaml`, then
   `kubectl -n dubbing-prod rollout status` per deployment.
7. Ingress + policies + PDBs:
   `kubectl apply -f deploy/k8s/ingress.yaml -f deploy/k8s/networkpolicies.yaml -f deploy/k8s/pdb.yaml`.
8. KEDA: `kubectl apply -f deploy/k8s/keda-scalers.yaml`, then
   `kubectl -n dubbing-prod get scaledobjects`.

Shortcut for environments (applies the same objects via kustomize):

- Staging: `kubectl apply -k deploy/k8s/overlays/staging` (1-replica sizes,
  mock providers, staging host/issuer, standard scratch class).
- Prod: `kubectl apply -k deploy/k8s/overlays/prod` (release tags pinned by CI).
- Helm wrapper: `helm upgrade --install dubbing deploy/helm/dubbing -f
  deploy/helm/dubbing/values.yaml -f deploy/helm/dubbing/values-prod.yaml`
  (or `values-staging.yaml`).

Verify with `bash deploy/verify.sh` (dry-run + kubeconform when available +
`helm lint` when available + structural YAML checks).

## Rollback

- Workloads: `kubectl -n <ns> rollout undo deploy/<name>` (or `rollout undo
  ... --to-revision=N`). The API and control PDBs (`minAvailable: 1`) keep
  serving during the rollback.
- KEDA/Ingress/Policies: re-apply the previous overlay revision
  (`kubectl apply -k deploy/k8s/overlays/<env>` from the prior commit).
- Database expand/contract rule (frozen): migrations are ADDITIVE ONLY. Old
  code tolerates new nullable columns for one release window, so rolling back
  application code never requires rolling back the schema. Never roll back a
  migration that already applied anywhere shared; forward-fix instead.

## Launch gates (staging -> prod promotion)

All five gates must be green before any prod promotion. A single failed
drill blocks promotion until a full passing re-run (partial re-runs do
not satisfy the gate). Evidence lives in the linked logs.

1. **Staging green** — full CI (`build -> unit -> integration ->
   contract -> E2E-smoke -> container-build -> Trivy -> SBOM -> cosign`)
   passes on the promotion commit, and staging runs the candidate image
   set (kustomize `overlays/staging`) with no open L1/L2 alerts.
2. **Restore drill pass** — `docs/dr/drill-log.md` has a passing entry
   within RTO 1 h / RPO 5 min (`BackupRestoreTests` green plus the
   quiesce -> PITR -> verify procedure in `docs/dr/backup-restore.md`).
3. **Chaos pass** — `docs/operations/chaos-log.md` has a passing entry:
   `RecoveryTests` green, no lost runs, leases recovered, fencing
   verified (live-kill tier executed in staging).
4. **Rotation drill pass** — `docs/operations/rotation-drill-log.md`
   has a passing entry per `docs/security/secret-rotation.md`
   (redaction + isolation suites green, dual-support window observed).
5. **SLOs met for 7 days** — `docs/observability/slos.md` targets held
   on staging for the trailing 7 days (pipeline success >= 98%, API p95
   < 500 ms, DLQ depth 0 sustained, export success >= 99%), confirmed on
   the on-call dashboards (`deploy/observability/dashboards/`;
   see `docs/operations/support-access.md`).

Also required: compat window verified
(`docs/operations/migration-compat.md`, `MigrationCompatTests` green)
and bomb protection verified (`docs/operations/load-log.md`,
`MediaBombTests` + `QuotaTests` green).

## Scaling notes

- ai/gpu/export scale on queue length 100 (`ai.provider`, `ai.gpu`,
  `export`); media-prep/render on CPU 70% + memory 80%; control on active
  leases (Postgres query, 10 per replica). GPU scales 0->4 only onto GPU
  nodes; with no GPU nodes the deployment sits at 0 with no scheduling error.
- KEDA RabbitMQ-down fallback: every broker-scaled object also has a CPU
  trigger, plus `fallback.replicas` after 3 consecutive scaler failures.
- Media bounded concurrency: 2 FFmpeg jobs per media pod
  (`Media__MaxConcurrentMediaJobs`); raise pod count via KEDA max, not the
  per-pod value, unless the node class grows.
