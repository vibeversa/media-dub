# Deploy Guide (Tasks 40, 042 and 043)

CI builds, scans, signs, and publishes all 9 images
(`.github/workflows/backend.yml` for the eight backend images,
`.github/workflows/frontend.yml` for the frontend image); this guide covers
cluster rollout with the raw K8s manifests (primary) or the thin Helm wrapper.

`deploy/verify.sh` is the release gate and is documented in its own header and in
`docs/ci-branch-protection.md` §2; see "Verifying a deployment" below.

Task 043 owns the frontend's delivery and the rollout. Start at
[`docs/topology.md`](../docs/topology.md) for what may talk to what,
[`docs/rollout.md`](../docs/rollout.md) for the release procedure, and
[`rollout.md`](rollout.md) for the release *record*: the compatibility window,
the compatibility matrix, the flag register, and the last rollback rehearsal.

| | |
| --- | --- |
| The frontend is a **static SPA behind a CDN**, not a set of routes on the API | [`docs/topology.md`](../docs/topology.md) |
| `deploy/cdn/` + `deploy/nginx/` + `Dockerfile.frontend` | the CDN edge config and the static origin |
| `deploy/config-inject.sh` | writes `frontend/.env.production` and `dist/version.json` per environment |
| `scripts/vite-env-audit.sh` | fails the build on a secret-shaped `VITE_*` key or value |
| `deploy/tests/hosting.test.sh` | **the hosting gate** — builds the image and asserts the real headers |
| `docs/runbooks/index.md` | every runbook, by symptom |
| `deploy/rollout.md` | **the release record** — migration ordering, the expand/contract window, the compatibility matrix, the flag register, the rollback rehearsal log |
| `docs/rollout.md` | the operator procedure (migration-first, backward-compatible, flag-gated, verify, then contract) |

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
4. **Migration job — delete first, then apply, then wait.** This is the gate for
   every step after it.

   ```bash
   kubectl -n dubbing-prod delete job dubbing-migration --ignore-not-found
   kubectl apply -f deploy/k8s/migration-job.yaml
   kubectl -n dubbing-prod wait --for=condition=complete --timeout=600s job/dubbing-migration
   ```

   The Job runs `/app/efbundle` (idempotent; concurrent runners serialize on
   `__EFMigrationsHistory`) with the maintenance-role connection string.
   A failed migration BLOCKS the rollout: do not proceed until the Job is
   Complete. (ArgoCD runs this automatically as a PreSync hook; Helm as a
   pre-upgrade hook — annotations are on the Job, and both of them delete the
   previous Job before creating the new one, which is why the delete below is
   specific to the plain-`kubectl` path.)

   **The `wait` is the gate; the `apply` is not.** A Job that is merely applied
   may be Running, may have failed, may be retrying.

   **The `delete` is part of the gate, not a preclean.** A Job's `spec` is
   immutable, so applying this file against a Job that already *completed* is a
   no-op that exits 0, and the `Complete` condition from the previous release is
   still on the object — so the `wait` returns success immediately, having
   observed a migration that never ran. That false pass was demonstrated on a
   real cluster by `deploy/rollout/rehearse-rollback.sh` (the wait succeeded in
   1 second on the same Job object, uid unchanged, no migration run), and
   `deploy/verify.sh` asserts the delete is in this list. See
   [`deploy/rollout.md`](rollout.md) §1.
5. API: `kubectl apply -f deploy/k8s/api-deployment.yaml`. Pods run the
   `wait-for-migrations` initContainer (same bundle) and never start on a
   broken schema. Wait for readiness:
   `kubectl -n dubbing-prod rollout status deploy/dubbing-api`.
   Readiness now also asserts **migration currency** (`MigrationCurrencyCheck`),
   so a pod whose build is ahead of the schema never takes traffic. Liveness
   stays process-only.
6. Workers: `kubectl apply -f deploy/k8s/workers-*.yaml`, then
   `kubectl -n dubbing-prod rollout status` per deployment.
7. Frontend origin: `kubectl apply -f deploy/k8s/frontend/`. The `frontend`
   Service is `ClusterIP` and the only Ingress selecting it is
   `frontend/ingress.yaml`, which terminates TLS for the **origin** hostname — so
   the CDN stays the only browser-reachable name and there is no second public
   route that bypasses its HTTPS redirect and headers. Point the CDN's origin at
   the ingress. `frontend/configmap.yaml` is a **build input**, not runtime
   configuration: `VITE_*` values are inlined at build time, so it is rendered
   into `frontend/.env.production` by `deploy/config-inject.sh` before the image
   is built and is deliberately not mounted. See `deploy/frontend/README.md`.
8. Ingress + policies + PDBs:
   `kubectl apply -f deploy/k8s/ingress.yaml -f deploy/k8s/networkpolicies.yaml -f deploy/k8s/pdb.yaml`.
9. KEDA: `kubectl apply -f deploy/k8s/keda-scalers.yaml`, then
   `kubectl -n dubbing-prod get scaledobjects`.

Shortcut for environments (applies the same objects via kustomize):

- Staging: `kubectl apply -k deploy/k8s/overlays/staging` (1-replica sizes,
  mock providers, staging host/issuer, standard scratch class).
- Prod: `kubectl apply -k deploy/k8s/overlays/prod` (release tags pinned by CI).
- Helm wrapper: `helm upgrade --install dubbing deploy/helm/dubbing -f
  deploy/helm/dubbing/values.yaml -f deploy/helm/dubbing/values-prod.yaml`
  (or `values-staging.yaml`).

Verify the manifests with `bash deploy/verify.sh` (structural assertions always;
`kubectl dry-run`, `kubeconform`, `kustomize build` and `helm lint` when those
tools are usable, each reporting a machine-readable SKIP reason when they are
not). The overlays are built in CI on `ubuntu-latest`; a developer on Windows
gets an explicit SKIP for `kustomize` rather than a false failure, because
kustomize refuses a parent-path `resources:` entry once the host normalises path
separators.

## Verifying a deployment (the release gate)

`deploy/verify.sh` is also the POST-DEPLOY gate. It is a different mode, not a
different script, and it is what decides whether a rollout stays.

```bash
bash deploy/verify.sh --post-deploy \
  --url https://staging.example.com \
  --tag v1.2.3 \
  --rollback-command 'kubectl -n dubbing-staging rollout undo deploy/dubbing-api'
```

In order, with a rollback triggered on the first failure:

1. **`/health/live` and `/health/ready` must both return 200.** Both, not just
   liveness: `/health/ready` answers "it can serve", and a release that only
   checks liveness passes on a pod that is up and cannot reach its database -
   which is exactly the state a bad migration leaves behind.
2. **The served `info.version` must match the release.** The API serves its
   document anonymously at `/openapi/v1.json` (the conventional
   `/openapi.json` is tried first). The expected version comes from
   `--openapi-version`, else from the bundle at `--tag`, else from the tag's
   leading `v<N>`. This is the check that catches a rollback to the wrong image
   and a partial rollout.
3. **The `@smoke` suite must pass against the deployed environment.**
4. **Any failure triggers the rollback command** and records the triggering
   reason.

The final line of every run is machine-readable, and the full result is written
to `deploy/.artifacts/verify-<timestamp>.json` (git-ignored):

```
VERIFY_RESULT reason=<REASON> status=<PASS|FAIL> exit=<n>
```

`reason` says why the release failed; `rollbackTriggered` and `rollbackNote` say
what was done about it. They are separate because they are different facts -
`SMOKE_FAILED` with a completed rollback is a different incident from
`SMOKE_FAILED` with no rollback configured, and collapsing them makes a dashboard
report rollback problems for every kind of failure. The full reason vocabulary is
in `docs/ci-branch-protection.md` §2.

**`--require-post-deploy` is what stops a release being reported as verified.**
The release job sets it; without a `--url` the static-only run then *fails* with
`DEPLOY_URL_REQUIRED`, because a release gate that quietly degrades to a manifest
check marks releases verified having contacted nothing.

`SMOKE_SPECS_MISSING` is currently the expected outcome of step 3 on this
repository: 041A did not land the smoke spec (it is blocked on the upload
protocol divergence the 041A report describes). It is reported as a **failure**,
not a skip, because a release verified on the strength of a health check is not
a verification.

## Rollback

`deploy/verify.sh --post-deploy` triggers the first two of these automatically
when a post-deploy check fails, and records the triggering reason.

**The full runbook is [`docs/runbooks/rollback.md`](../docs/runbooks/rollback.md)**,
and it opens with the decision table, because the database question comes first
and it has an answer that is not "roll it back". Summary:

- API: `kubectl -n <ns> rollout undo deployment/dubbing-api`. The API and control PDBs
  (`minAvailable: 1`) keep serving during the rollback, so it is a pointer change
  rather than a restart. Confirm with `/version`, not with the rollout status:
  `rollout undo` moving the Deployment does not prove the pods are the previous
  build.
- **Frontend: a CDN version pin**, not a redeploy. The previous release's assets
  are still on the origin under exactly the names the previous document
  referenced, because `/assets/*` filenames carry a content hash. Restoring a
  pointer restores a working application with no rebuild and no window in which
  the assets are missing.
- Database: **never**. Migrations are additive only; a rollback never reverses a
  migration that applied anywhere shared. Forward-fix. The one case where a
  rollback is *refused* is a build behind a contracted schema — see the decision
  table.
- KEDA/Ingress/Policies: re-apply the previous overlay revision
  (`kubectl apply -k deploy/k8s/overlays/<env>` from the prior commit).

## Launch gates (staging -> prod promotion)

All six gates must be green before any prod promotion. A single failed
drill blocks promotion until a full passing re-run (partial re-runs do
not satisfy the gate). Evidence lives in the linked logs.

1. **Staging green** — full CI (`CI / contract` -> `CI / backend` -> `CI /
   frontend`: build -> unit -> integration -> contract -> migration-compat ->
   container-build -> Trivy -> SBOM -> cosign -> publish, plus lint ->
   typecheck -> coverage -> build -> codegen-verify -> E2E -> visual -> a11y ->
   audit) passes on the promotion commit, **plus the two Task 043 gates**:

   ```bash
   bash deploy/tests/hosting.test.sh          # HOSTING_GATE_RESULT reason=OK
   bash scripts/vite-env-audit.sh --allowlist-sync --bundle frontend/dist
   ```

   The hosting gate is **not** optional and its `status=SKIP`
   (`HOSTING_IMAGE_UNVERIFIED`) does not satisfy this gate: a skip means the
   header matrix was not verified, and a promotion decided on an unverified
   matrix is decided on nothing. CI runs this with Docker present.

   Staging then runs the candidate image set (kustomize `overlays/staging`) with
   no open L1/L2 alerts.
2. **Post-deploy gate green** — `bash deploy/verify.sh --post-deploy
   --require-post-deploy --url <staging> --tag <promoted tag>` passes: health,
   the served `info.version` matching the release, and `@smoke`
   (`docs/ci-branch-protection.md` §2).
3. **Restore drill pass** — `docs/dr/drill-log.md` has a passing entry
   within RTO 1 h / RPO 5 min (`BackupRestoreTests` green plus the
   quiesce -> PITR -> verify procedure in `docs/dr/backup-restore.md`).
   The coverage table and the drill checklist are in
   [`docs/runbooks/backup-restore.md`](../docs/runbooks/backup-restore.md); a
   drill that finds a gap is a **release blocker** until the coverage table says
   what is and is not recoverable.
4. **Chaos pass** — `docs/operations/chaos-log.md` has a passing entry:
   `RecoveryTests` green, no lost runs, leases recovered, fencing
   verified (live-kill tier executed in staging).
5. **Rotation drill pass** — `docs/operations/rotation-drill-log.md`
   has a passing entry per `docs/security/secret-rotation.md`
   (redaction + isolation suites green, dual-support window observed).
6. **SLOs met for 7 days** — `docs/observability/slos.md` targets held
   on staging for the trailing 7 days (pipeline success >= 98%, API p95
   < 500 ms, DLQ depth 0 sustained, export success >= 99%), confirmed on
   the on-call dashboards (`deploy/observability/dashboards/`;
   see `docs/operations/support-access.md`).

Also required: compat window verified
(`docs/operations/migration-compat.md`, `MigrationCompatTests` green)
and bomb protection verified (`docs/operations/load-log.md`,
`MediaBombTests` + `QuotaTests` green). A **contract-phase** migration (dropping
a deprecated column or endpoint) additionally requires two green releases first —
see [`docs/rollout.md`](../docs/rollout.md) §5, and the decision table in
[`docs/runbooks/rollback.md`](../docs/runbooks/rollback.md#decision-table) for
why a rollback is *refused* once one has run.

## Verifying the hosting configuration

```bash
bash deploy/tests/hosting.test.sh
```

Two tiers. The **static** tier runs anywhere and is mandatory: it reads
`deploy/cdn/origin.json`, `deploy/nginx/default.conf`,
`deploy/nginx/security-headers.conf` and the NetworkPolicies, and fails on a
missing file rather than skipping. The **docker** tier builds
`Dockerfile.frontend`, runs it, and asserts the actual response headers — the
document's cache class, a hashed asset's `immutable`, a missing asset's 404, the
security headers, compression, and that `/api/` is not this origin's business.

The headers are read from a live server rather than from the config on purpose:
`add_header` inheritance and `try_files` fallback depend on which `location`
block matched, so a config that reads correctly can serve the wrong thing, and
only a response proves it. Two real defects in this repository were found exactly
that way — see the 043 report.

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
