# Task 40 — CICD Kubernetes KEDA GPU

## Goal

Automate build/test/scan/publish/deploy with workload-separated Kubernetes manifests, KEDA scaling, GPU scheduling, and safe migration rollout.

## Context

Binding: CI restore→build→unit→integration→contract→E2E-smoke→container-build→scan→SBOM→sign→publish; fail on warnings; cache NuGet/Docker. K8s namespace/API/control/media-prep/media-render/ai/gpu/export/maintenance + secrets/configmaps/ingress/netpol/PDBs. Managed PG/storage/broker/Redis in prod (no self-host stateful default). Probes liveness local/ readiness dep-specific. Resource requests/limits + PVC/high-IOPS scratch for media. KEDA queue-depth + CPU/mem + active-leases + GPU util. Media bounded concurrency. GPU node selector/taints + nvidia.com/gpu. Secrets from external manager. Env-specific config. Migration job before API rollout. Expand/contract compat. Staging+prod. Blue/green-canary later optional.

## Starting State

App builds, tests + fixtures exist, Dockerfiles + compose exist, observability configs exist, hardening docs exist. No CI workflow, no K8s/Helm, no KEDA scalers, no migration job.

## Scope

Must implement: GitHub Actions CI, K8s manifests/Helm, KEDA, GPU config, migration job, env configs, deploy/rollback docs. Must not implement: HA/DR chaos tests (next task), optional enrichment.

## Instructions

1. Create `.github/workflows/ci.yml`: triggers push/PR main; jobs `build` (setup-dotnet 10, cache NuGet, `dotnet restore/build --warnaserror`, `dotnet test Unit`), `integration` (Testcontainers, `dotnet test Integration+Contract`), `e2e` (E2E smoke FullPipeline only), `docker` (build 8 images, Trivy scan `aquasecurity/trivy-action`, SBOM `anchore/sbom-action`, sign `sigstore/cosign-installer`, push to `ghcr.io/<org>/dubbing-*` on main). Fail on warnings; cache layers `type=gha`.
2. Create `deploy/k8s/` YAMLs: `namespace.yaml` (dubbing-prod, dubbing-staging), `api-deployment.yaml` (3 replicas prod, probes /health/live + /ready, resources 500m/1Gi requests, 2CPU/4Gi limits), `workers-*.yaml` per class (control 2, media-prep 3 with `concurrency:2` env + PVC `media-scratch` 50Gi high-IOPS storageClass, media-render 2, ai 3, gpu 0-4 with `nodeSelector: nvidia.com/gpu.present=true`, tolerations + `resources.limits: nvidia.com/gpu:1`, export 2, maintenance 1), `configmap.yaml` (non-secret config per env), `secrets.yaml` placeholder referencing external-secrets (`ExternalSecret` for DB/Rabbit/S3/provider keys — document), `ingress.yaml` (api host, TLS), `networkpolicies.yaml` (default-deny + allows per Task 37 spec), `pdb.yaml` (minAvailable 1 for api/control), `migration-job.yaml` (initContainer `dotnet ef database update` via maintenance image, `backoffLimit:3`, run before api rollout via ArgoCD hook or `helm pre-upgrade` — document), `keda-scalers.yaml` (ScaledObjects: `queueLength: 100` for ai/gpu/export (Rabbit `control.orchestration` etc.), CPU 70% + memory 80% for media, `activeLeases` query for control, `nvidia.com/gpu.utilization` for gpu).
3. Helm alternative: `deploy/helm/dubbing/values-staging.yaml, values-prod.yaml` + Chart referencing same templates (minimal wrapper; document choice: raw YAML primary, Helm values overlay).
4. Env configs: `deploy/k8s/overlays/staging|prod/kustomization.yaml` (replicas, resources, secrets refs, OTLP endpoints).
5. Docs: `deploy/README.md` (deploy order: secrets→migration-job→api→workers→ingress→keda; rollback `kubectl rollout undo` + DB expand/contract note: migrations additive only, old code tolerates new nullable columns for 1 release window).
6. Probes exact: `livenessProbe: httpGet /health/live period 10s`, `readinessProbe: httpGet /health/ready period 15s failureThreshold 3`.

## Requirements

- R1: CI passes with scan/SBOM/sign.
- R2: Migration job succeeds before API ready.
- R3: Workers scale on queue load; media respects concurrency; GPU only on GPU nodes.
- R4: Managed service refs, no prod self-host stateful.
- R5: Staging+prod envs + rollback tested.

## Edge Cases and Error Handling

- Migration fail → block rollout (job must succeed; API Deployment `initContainers` wait).
- Image scan high/critical → fail CI (allowlist via `.trivyignore` with expiry, document).
- KEDA Rabbit down → fallback CPU scaling (document).
- GPU nodes absent → gpu deployment 0 replicas, no scheduling error.

## Security and Safety Requirements

- Secrets via external manager, never in git; images non-root signed + scanned; network deny-all; TLS ingress.

## Testing

Validation is pipeline + `kubectl apply --dry-run=client` + `kubeconform` if available; no C# tests. Provide `deploy/verify.sh` (runs dry-run + kubeconform + `helm lint`).

## Validation

```bash
dotnet build
act -j build || echo "act optional"
kubectl apply --dry-run=client -f deploy/k8s/
bash deploy/verify.sh
```

## Completion Criteria

- CI + manifests + KEDA + GPU + migration + docs complete and dry-run valid.

## Traceability

- Plan Section 29; Assumptions 79–81; Integration checklist K8s/KEDA/GPU/CI/secrets; Security checklist scanning/SBOM/sign/netpol; Deployment checklist (partial).
