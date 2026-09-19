#!/usr/bin/env bash
# Task 40 verification: structural YAML checks (always) + kubectl dry-run,
# kubeconform, kustomize build, and helm lint when those tools are available.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
K8S="$ROOT/deploy/k8s"
PASS=0
FAIL=0

ok()   { PASS=$((PASS + 1)); echo "PASS: $1"; }
fail() { FAIL=$((FAIL + 1)); echo "FAIL: $1"; }

have() { command -v "$1" >/dev/null 2>&1; }

echo "== structural checks (python3 + PyYAML) =="
python3 - "$K8S" <<'PY'
import glob, os, sys
import yaml

base = sys.argv[1]
required = {
    "namespace.yaml": ["Namespace"],
    "configmap.yaml": ["ConfigMap"],
    "secrets.yaml": ["ExternalSecret"],
    "api-deployment.yaml": ["Deployment", "Service"],
    "workers-control.yaml": ["Deployment"],
    "workers-media-prep.yaml": ["PersistentVolumeClaim", "Deployment"],
    "workers-media-render.yaml": ["Deployment"],
    "workers-ai.yaml": ["Deployment"],
    "workers-gpu.yaml": ["Deployment"],
    "workers-export.yaml": ["Deployment"],
    "workers-maintenance.yaml": ["Deployment"],
    "ingress.yaml": ["Ingress"],
    "networkpolicies.yaml": ["NetworkPolicy"],
    "pdb.yaml": ["PodDisruptionBudget"],
    "migration-job.yaml": ["Job"],
    "keda-scalers.yaml": ["TriggerAuthentication", "ScaledObject"],
}
failures = []
for fname, kinds in required.items():
    path = os.path.join(base, fname)
    if not os.path.isfile(path):
        failures.append(f"{fname}: missing file")
        continue
    try:
        docs = [d for d in yaml.safe_load_all(open(path, encoding="utf-8")) if d]
    except yaml.YAMLError as exc:
        failures.append(f"{fname}: invalid YAML ({exc})")
        continue
    have = sorted({d.get("kind", "?") for d in docs if isinstance(d, dict)})
    for kind in kinds:
        if kind not in have:
            failures.append(f"{fname}: missing kind {kind} (has {have})")
    for d in docs:
        if isinstance(d, dict) and "apiVersion" not in d:
            failures.append(f"{fname}: document missing apiVersion")
for line in failures:
    print(f"FAIL: {line}")
if failures:
    print(f"{len(failures)} structural failure(s)")
    sys.exit(1)
print(f"structural OK: {len(required)} files, kinds as specified")

# Probe contract: exact liveness/readiness on the API deployment.
api = list(yaml.safe_load_all(open(os.path.join(base, "api-deployment.yaml"), encoding="utf-8")))
dep = next(d for d in api if d.get("kind") == "Deployment")
c = dep["spec"]["template"]["spec"]["containers"][0]
live, ready = c["livenessProbe"], c["readinessProbe"]
assert live["httpGet"]["path"] == "/health/live" and live["periodSeconds"] == 10, live
assert ready["httpGet"]["path"] == "/health/ready" and ready["periodSeconds"] == 15, ready
assert ready["failureThreshold"] == 3, ready
# API resource contract.
assert c["resources"] == {"requests": {"cpu": "500m", "memory": "1Gi"},
                          "limits": {"cpu": "2", "memory": "4Gi"}}, c["resources"]
assert dep["spec"]["replicas"] == 3, dep["spec"]["replicas"]
print("probe/resource/replica contract OK: /health/live 10s, /health/ready 15s FT3, 500m/1Gi -> 2/4Gi, 3 replicas")

# GPU scheduling contract.
gpu = list(yaml.safe_load_all(open(os.path.join(base, "workers-gpu.yaml"), encoding="utf-8")))
gdep = next(d for d in gpu if d.get("kind") == "Deployment")
gspec = gdep["spec"]["template"]["spec"]
gc = gspec["containers"][0]
assert gdep["spec"]["replicas"] == 0, gdep["spec"]["replicas"]
assert gspec["nodeSelector"] == {"nvidia.com/gpu.present": "true"}, gspec["nodeSelector"]
assert any(t.get("key") == "nvidia.com/gpu" and t.get("effect") == "NoSchedule" for t in gspec["tolerations"]), gspec["tolerations"]
assert gc["resources"]["limits"]["nvidia.com/gpu"] == "1", gc["resources"]
print("gpu contract OK: 0 replicas, nodeSelector, toleration, nvidia.com/gpu:1")

# Migration job contract.
job = list(yaml.safe_load_all(open(os.path.join(base, "migration-job.yaml"), encoding="utf-8")))
j = next(d for d in job if d.get("kind") == "Job")
assert j["spec"]["backoffLimit"] == 3, j["spec"]
assert j["metadata"]["annotations"]["argocd.argoproj.io/hook"] == "PreSync", j["metadata"]
assert "pre-upgrade" in j["metadata"]["annotations"]["helm.sh/hook"], j["metadata"]
print("migration contract OK: backoffLimit 3, ArgoCD PreSync + helm pre-upgrade hooks")

# Media bounded-concurrency + scratch contract.
prep = list(yaml.safe_load_all(open(os.path.join(base, "workers-media-prep.yaml"), encoding="utf-8")))
pdep = next(d for d in prep if d.get("kind") == "Deployment")
penv = {e["name"]: e.get("value") for e in pdep["spec"]["template"]["spec"]["containers"][0]["env"] if "value" in e}
assert penv.get("Media__MaxConcurrentMediaJobs") == "2", penv
pvc = next(d for d in prep if d.get("kind") == "PersistentVolumeClaim")
assert pvc["spec"]["resources"]["requests"]["storage"] == "50Gi", pvc["spec"]
assert pdep["spec"]["replicas"] == 3, pdep["spec"]
print("media contract OK: concurrency 2, 50Gi scratch PVC, 3 replicas")
PY
ok "structural + contract checks"

echo "== kubectl dry-run =="
if have kubectl; then
  # --validate=false: CRDs (ScaledObject, ExternalSecret) may be unknown to the
  # target cluster version; schema conformance is covered by kubeconform below.
  if kubectl apply --dry-run=client --validate=false -f "$K8S"; then
    ok "kubectl apply --dry-run=client -f deploy/k8s/"
  else
    fail "kubectl apply --dry-run=client -f deploy/k8s/"
  fi
else
  echo "SKIP: kubectl not installed (runs in CI)"
fi

echo "== kubeconform =="
if have kubeconform; then
  if kubeconform -summary -ignore-missing-schemas "$K8S"/namespace.yaml "$K8S"/configmap.yaml \
      "$K8S"/api-deployment.yaml "$K8S"/workers-*.yaml "$K8S"/ingress.yaml \
      "$K8S"/networkpolicies.yaml "$K8S"/pdb.yaml "$K8S"/migration-job.yaml; then
    ok "kubeconform (core kinds; CRDs excluded)"
  else
    fail "kubeconform"
  fi
else
  echo "SKIP: kubeconform not installed (runs in CI if available)"
fi

echo "== kustomize overlays =="
if have kubectl; then
  if kubectl kustomize "$K8S/overlays/staging" >/dev/null && kubectl kustomize "$K8S/overlays/prod" >/dev/null; then
    ok "kustomize build staging + prod"
  else
    fail "kustomize build"
  fi
elif have kustomize; then
  if kustomize build "$K8S/overlays/staging" >/dev/null && kustomize build "$K8S/overlays/prod" >/dev/null; then
    ok "kustomize build staging + prod"
  else
    fail "kustomize build"
  fi
else
  echo "SKIP: neither kubectl nor kustomize installed (runs in CI)"
fi

echo "== helm lint =="
if have helm; then
  if helm lint "$ROOT/deploy/helm/dubbing" -f "$ROOT/deploy/helm/dubbing/values-prod.yaml" \
    && helm lint "$ROOT/deploy/helm/dubbing" -f "$ROOT/deploy/helm/dubbing/values-staging.yaml"; then
    ok "helm lint (prod + staging values)"
  else
    fail "helm lint"
  fi
else
  echo "SKIP: helm not installed (runs in CI if available)"
fi

echo "== result: $PASS passed, $FAIL failed =="
[ "$FAIL" -eq 0 ]
