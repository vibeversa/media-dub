# 040 — CI/CD Kubernetes KEDA GPU Report

## Status
COMPLETED

## Summary
Delivered the full Task 40 deploy slice: `.github/workflows/ci.yml` (build→unit→integration→contract→E2E-smoke→container-build→Trivy→SBOM→cosign→GHCR on main, warnings-as-errors, NuGet + GHA layer caching) with an empty-expiry-documented `.trivyignore`; 16 raw-YAML manifests under `deploy/k8s/` (namespaces, API 3-replica + Service, 7 worker Deployments, ConfigMap, ExternalSecret, Ingress, NetworkPolicies, PDBs, migration Job, 8 KEDA ScaledObjects/TriggerAuthentications); Helm wrapper chart + staging/prod values; staging/prod kustomize overlays; `deploy/README.md` deploy-order/rollback docs; `deploy/verify.sh` (structural + contract asserts, kubectl/kubeconform/helm when available); and an EF migrations bundle (`/app/efbundle`) built into `Dockerfile.maintenance` so the Job and API initContainer gate actually run. Validation: `dotnet build` 0/0, `verify.sh` PASS exit 0, all 32 files YAML/editorconfig-clean, overlay refs resolve, no secret literals.

## Files Created/Modified
- `.github/workflows/ci.yml` — new: triggers push/PR main; jobs build (setup-dotnet 10, NuGet cache, restore/build --warnaserror, Unit), integration (ffmpeg, fixtures, Integration Category!=Soak + Contract), e2e (FullPipelineTests only), docker (8-image matrix, Buildx GHA cache, Trivy HIGH/CRITICAL + SARIF, anchore SBOM, cosign keyless sign + GHCR push on main only).
- `.trivyignore` — new: empty allowlist with mandatory EXPIRY + justification format documented.
- `deploy/k8s/namespace.yaml` — new: dubbing-prod + dubbing-staging Namespaces.
- `deploy/k8s/configmap.yaml` — new: prod non-secret config (Transport RabbitMq, Providers azure default, Media__MaxConcurrentMediaJobs 2, OTLP/Deployment placeholders, CHANGE_ME org).
- `deploy/k8s/secrets.yaml` — new: ExternalSecret dubbing-secrets (ClusterSecretStore dubbing-cluster-store, 20 remote keys incl. keda-rabbitmq-host/keda-postgres-conn) + operator setup/rotation docs, zero secret material.
- `deploy/k8s/api-deployment.yaml` — new: Deployment (3 replicas, exact probes /health/live 10s + /health/ready 15s FT3, 500m/1Gi→2/4Gi, wait-for-migrations initContainer via /app/efbundle) + ClusterIP Service.
- `deploy/k8s/workers-control.yaml` — new: 2 replicas, DOTNET_WORKER_ROLE=control.
- `deploy/k8s/workers-media-prep.yaml` — new: 3 replicas, Media__MaxConcurrentMediaJobs=2, media-scratch PVC 50Gi RWX managed-high-iops.
- `deploy/k8s/workers-media-render.yaml` — new: 2 replicas, same concurrency + scratch mount.
- `deploy/k8s/workers-ai.yaml` — new: 3 replicas, full provider-key env set.
- `deploy/k8s/workers-gpu.yaml` — new: 0 replicas, nodeSelector nvidia.com/gpu.present=true, NoSchedule toleration, nvidia.com/gpu:1 limit, LocalInference__Device=cuda.
- `deploy/k8s/workers-export.yaml` — new: 2 replicas, role export.
- `deploy/k8s/workers-maintenance.yaml` — new: singleton Recreate, role maintenance, no scaler.
- `deploy/k8s/ingress.yaml` — new: nginx class, cert-manager issuer, TLS, 5g body/600s timeouts, example host.
- `deploy/k8s/networkpolicies.yaml` — new: default-deny-all, DNS egress, api-allow (ingress-nginx+monitoring ingress; backend egress), workers-allow (egress only, api excluded via NotIn), managed-CIDR CHANGE_ME placeholders + Cilium toFQDNs note.
- `deploy/k8s/pdb.yaml` — new: minAvailable 1 for api + worker-control (policy/v1).
- `deploy/k8s/migration-job.yaml` — new: Job backoffLimit 3, /app/efbundle with maintenance-connection, ArgoCD PreSync + helm pre-upgrade/pre-install annotations, 600s deadline, 24h TTL.
- `deploy/k8s/keda-scalers.yaml` — new: 2 TriggerAuthentications + 6 ScaledObjects (ai/gpu/export queueLength 100; media cpu70+mem80; control postgres active-leases threshold 10; gpu DCGM prometheus; cpu fallbacks + fallback.replicas everywhere; gpu 0..4 restoreToOriginal).
- `deploy/helm/dubbing/{Chart.yaml,values.yaml,values-staging.yaml,values-prod.yaml,templates/NOTES.txt}` — new: minimal wrapper (raw YAML primary documented), per-env registry/tag/namespace/host/issuer/OTLP/replicas.
- `deploy/k8s/overlays/{staging,prod}/kustomization.yaml` + `staging/{configmap,ingress,pvc}-patch.yaml` + `prod/configmap-patch.yaml` — new: namespace retarget, 8 image tags (staging main, prod v1.0.0), replica sizes, env patches; resources listed per-file (no base kustomization.yaml, keeping `kubectl apply -f deploy/k8s/` valid).
- `deploy/README.md` — new: prerequisites, 8-step deploy order (secrets→migration→api→workers→ingress→keda), kubectl wait/rollout commands, rollback + additive-only expand/contract rule, scaling notes.
- `deploy/verify.sh` — new: PyYAML structural + contract asserts (probes/resources/replicas/GPU/migration/media), kubectl dry-run --validate=false, kubeconform (core kinds), kustomize builds, helm lint; skips cleanly when tools absent.
- `Dockerfile.maintenance` — modified: build stage runs `dotnet tool restore && dotnet ef migrations bundle --self-contained -r linux-x64 --output /app/efbundle`; final stage copies `/app/efbundle`.

## Decisions Made
- **EF bundle instead of literal `dotnet ef database update` in-cluster**: `dotnet ef` needs SDK + source, absent from runtime images; the bundle is its compiled equivalent (same pending-migration semantics, history-table serialized, redacted logs). Documented in migration-job.yaml, README, and Dockerfile comment. `dotnet tool restore` in the build stage finds `/src/.config/dotnet-tools.json` via upward manifest lookup.
- **API initContainer runs the bundle (idempotent apply-and-wait), not a psql history poll**: avoids parsing the Npgsql-format connection string in shell and needs no RBAC; a broken migration exits non-zero and blocks the rollout. Uses `maintenance-connection` (BYPASSRLS role) per MIGRATION_NOTES, matching the Job.
- **Task-text mappings made explicit**: `concurrency:2` → real `Media__MaxConcurrentMediaJobs=2` (MediaOptions); `nvidia.com/gpu.utilization` → DCGM `avg(DCGM_FI_DEV_GPU_UTIL)` prometheus trigger (slashes illegal in metric names); GPU base replicas 0 so GPU-less clusters schedule nothing.
- **media-scratch is ReadWriteMany (`managed-high-iops`, CHANGE_ME)**: RWO would deadlock 3 prep + 2 render replicas across nodes; object storage stays source of truth. Staging overlay downgrades to `standard`.
- **No base `deploy/k8s/kustomization.yaml`**: a Kustomization object in the base dir would break the prescribed `kubectl apply -f deploy/k8s/`; overlays enumerate the 16 files individually (verified resolvable).
- **Workers get no HTTP probes**: Workers host is `Microsoft.NET.Sdk.Worker` with in-process health checks only (`HealthRegistration.AddWorkerHealthChecks`, no Kestrel); API carries the exact probe contract. KEDA + process supervision cover workers.
- **Combined export+maintenance file split** into `workers-export.yaml` / `workers-maintenance.yaml` for one-class-per-file parity with the task glob.
- **KEDA Rabbit-down fallback is two-layer**: cpu trigger on every broker-scaled object + `fallback.replicas` (failureThreshold 3); documented in keda-scalers.yaml + README.

## Build/Test Results
- `dotnet build --nologo -v q` (last 4):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:18.93
```
- `bash deploy/verify.sh` (exit 0, last 12):
```
probe/resource/replica contract OK: /health/live 10s, /health/ready 15s FT3, 500m/1Gi -> 2/4Gi, 3 replicas
gpu contract OK: 0 replicas, nodeSelector, toleration, nvidia.com/gpu:1
migration contract OK: backoffLimit 3, ArgoCD PreSync + helm pre-upgrade hooks
media contract OK: concurrency 2, 50Gi scratch PVC, 3 replicas
PASS: structural + contract checks
== kubectl dry-run ==
SKIP: kubectl not installed (runs in CI)
== kubeconform ==
SKIP: kubeconform not installed (runs in CI if available)
== kustomize overlays ==
SKIP: neither kubectl nor kustomize installed (runs in CI)
== helm lint ==
SKIP: helm not installed (runs in CI if available)
== result: 1 passed, 0 failed ==
```
- `act -j build` → `act: command not found` (optional path per task); `kubectl apply --dry-run=client -f deploy/k8s/` → `kubectl: command not found` locally (no cluster tooling on this host; runs in CI).
- YAML parse: ci.yml + 4 helm + 6 overlay files all OK; `file deploy/verify.sh` = Bourne-Again, 0 CR bytes; editorconfig-clean (LF/final-newline/no-trailing-ws): 32 files; overlay `resources`/`patches` all resolve; secret-literal grep clean.

## Recommendations for Next Agent (041)
- Repo state: builds 0/0 (TreatWarningsAsErrors + EnforceCodeStyleInBuild). Task 40 added NO C# code — only `.github/`, `deploy/`, `.trivyignore`, and `Dockerfile.maintenance` (+efbundle lines). Not a git repo (`git status` → fatal). kubectl/helm/kubeconform/kustomize/act/trivy/cosign all ABSENT on this host — 041 validation needing them must run in CI or document skips as above; `deploy/verify.sh` is the local gate (extends easily with new asserts).
- Key files for 041 (HA/DR, chaos/load — reuse, do not rename): `deploy/k8s/api-deployment.yaml` (Deployment `api`, PDB `api` minAvailable 1 in `pdb.yaml`), `deploy/k8s/workers-*.yaml` (7 Deployments; maintenance is Recreate singleton, gpu base 0), `deploy/k8s/migration-job.yaml` (Job `dubbing-migration`, backoffLimit 3), `deploy/k8s/keda-scalers.yaml` (ScaledObjects worker-{ai,gpu,export,media-prep,media-render,control}), `deploy/k8s/networkpolicies.yaml` (default-deny + CHANGE_ME managed CIDRs + Cilium toFQDNs note), `deploy/k8s/overlays/{staging,prod}/kustomization.yaml` (namespace transformer, images, replicas), `deploy/helm/dubbing/values-{staging,prod}.yaml`, `.github/workflows/ci.yml` (add nightly chaos/soak jobs here: SoakTests use `[Trait("Category","Soak")]` + `RUN_SOAK=1`/`SOAK_ITERATIONS=n`, currently excluded via `Category!=Soak`).
- Gotchas: NEVER run two `dotnet test` concurrently on this host (MSBuild/testhost contention mimics hangs). `Dockerfile.maintenance` bundle step (`dotnet ef migrations bundle --self-contained -r linux-x64`) is UNVERIFIED locally (no Docker daemon; absent locally per 039 too) — CI `docker` job builds it; if CI fails there, first suspect bundle flags/paths (`--project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api` from `/src` workdir). `kubectl apply -f deploy/k8s/` is non-recursive (overlays/ ignored — intentional); do NOT add a base kustomization.yaml. `helm lint` needs only Chart.yaml + values (templates/ has NOTES.txt only — by design, raw YAML is primary). Kustomization `patches:` (target-less path form) needs kustomize ≥4.1/kubectl ≥1.22 — fine for CI images.
- Conventions: images `ghcr.io/CHANGE_ME/dubbing-*` (8: api, worker-control, worker-media-preparation, worker-media-render, worker-ai, worker-gpu, worker-export, maintenance); secret keys lowercase-dash in `dubbing-secrets` mapped to `__`-nested env (e.g. `ConnectionStrings__Default`←`connection-string`, `Providers__OpenAIApiKey`←`openai-api-key` — note the exact `OpenAI`/`Google`/`Azure` property casing in `Application/Options/*Options.cs`); pod security `runAsUser: 1654` (dotnet `app` uid) + `readOnlyRootFilesystem` + `/tmp` emptyDir everywhere; queue names frozen in `Contracts/Messages/QueueNames.cs` (`control.orchestration`, `media.preparation`, `media.render`, `ai.provider`, `ai.gpu`, `export`, `maintenance`).
- Config keys: base ConfigMap `Transport__Provider=RabbitMq`, `Providers__DefaultProvider=azure` (staging overlay → `mock`), `Media__MaxConcurrentMediaJobs=2`, `Media__CpuThreads=4`, `Observability__OtlpEndpoint` (staging `http://otel-collector.observability.svc.cluster.local:4317`, prod `https://otlp.CHANGE_ME:4317`), `Deployment__Environment/Region`. Incomplete integration points for 041: replace all `CHANGE_ME` (registry org, OTLP, region, managed CIDRs `198.51.100.0/24`/`203.0.113.0/24`/`192.0.2.0/24`, example hosts), create `ClusterSecretStore dubbing-cluster-store` + 20 remote keys, provision cert-manager issuers, install KEDA/external-secrets/DCGM exporter + Prometheus for the gpu prometheus trigger.
