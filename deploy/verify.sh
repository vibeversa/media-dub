#!/usr/bin/env bash
# Release and post-deploy verification (Task 042).
#
# TWO MODES, ONE SCRIPT
# ----------------------
#   static  (default)   Structural and contract assertions over deploy/, plus the
#                       kubectl/kubeconform/kustomize/helm checks when those
#                       tools are usable. No environment is contacted. This is
#                       what a developer runs and what PR CI runs.
#
#   post-deploy         The release gate. Health, `/openapi.json` version match
#                       against the deployed tag, the `@smoke` suite against the
#                       deployed environment, and a rollback trigger on any
#                       failure. Requires DEPLOY_URL. This is what runs after a
#                       rollout.
#
# The mode is chosen by `--post-deploy` or by setting `DEPLOY_URL`, and the
# post-deploy mode is also reachable with `--static-only` being absent - there is
# no configuration in which a post-deploy run silently degrades into a static
# run. `VERIFY_REQUIRE_POST_DEPLOY=1` (or `--require-post-deploy`) makes the
# static-only case a FAILURE, which is how the release job is wired so a
# misconfigured release cannot be reported as a verified release.
#
# ROLLBACK
# --------
# A failure in post-deploy mode triggers the rollback command from
# `deploy/README.md` (`kubectl rollout undo`) and records `ROLLBACK_TRIGGERED`
# with the reason that triggered it. The script does not perform hosting or
# rollout decisions - Task 043 owns the runbook - but it does not leave a failed
# release sitting there either, and it always says which command it ran and what
# it expected.
#
# MACHINE-READABLE OUTPUT
# -----------------------
# Every run ends with one line:
#   VERIFY_RESULT reason=<REASON> status=<PASS|FAIL> exit=<n>
# and writes the same, plus the per-check detail, to
# `${VERIFY_RESULT_DIR:-deploy/.artifacts}/verify-<timestamp>.json`.
# `REASONS` are a closed set, documented in docs/ci-branch-protection.md, so a
# release job can branch on one without parsing prose.
#
# EXIT CODES
#   0  every applicable check passed
#   1  a check failed (reason on the last line)
#   2  the script could not run (a tool or input it requires is missing)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
K8S="$ROOT/deploy/k8s"

MODE="static"
REASON="OK"
EXIT_CODE=0
PASS=0
FAIL=0
CHECKS=()
ROLLBACK_NOTE=""

DEPLOY_URL="${DEPLOY_URL:-}"
DEPLOY_TAG="${DEPLOY_TAG:-}"
EXPECTED_OPENAPI_VERSION="${EXPECTED_OPENAPI_VERSION:-}"
ROLLBACK_COMMAND="${ROLLBACK_COMMAND:-}"
REQUIRE_POST_DEPLOY="${VERIFY_REQUIRE_POST_DEPLOY:-0}"
RESULT_DIR="${VERIFY_RESULT_DIR:-$ROOT/deploy/.artifacts}"

# The closed set of failure reasons. Kept together so a reader can see every
# outcome this script can produce, and so docs/ci-branch-protection.md can list
# them without drift.
readonly REASONS_OK="OK"
readonly REASONS_MANIFEST="MANIFEST_CHECK_FAILED"
readonly REASONS_HEALTH="HEALTH_FAILED"
readonly REASONS_OPENAPI_UNREACHABLE="OPENAPI_UNREACHABLE"
readonly REASONS_OPENAPI_VERSION="OPENAPI_VERSION_MISMATCH"
readonly REASONS_SMOKE_MISSING="SMOKE_SPECS_MISSING"
readonly REASONS_SMOKE_RUNNER="SMOKE_RUNNER_UNAVAILABLE"
readonly REASONS_SMOKE_FAILED="SMOKE_FAILED"
readonly REASONS_ROLLBACK="ROLLBACK_TRIGGERED"
readonly REASONS_ROLLBACK_UNAVAILABLE="ROLLBACK_UNAVAILABLE"
readonly REASONS_ROLLBACK_WINDOW="MIGRATION_NOT_ADDITIVE"
readonly REASONS_BACKUP="BACKUP_GAP_BLOCKS_RELEASE"
readonly REASONS_URL_REQUIRED="DEPLOY_URL_REQUIRED"
readonly REASONS_INPUT="INPUT_INVALID"

ok()   { PASS=$((PASS + 1)); CHECKS+=("PASS|$1"); echo "PASS: $1"; }
bad()  { FAIL=$((FAIL + 1)); CHECKS+=("FAIL|$1"); echo "FAIL: $1"; }
skip() { CHECKS+=("SKIP|$1|$2"); echo "SKIP: $1 ($2)"; }
have() { command -v "$1" >/dev/null 2>&1; }

platform() {
  case "$(uname -s)" in
    MINGW*|MSYS*|CYGWIN*) echo "windows" ;;
    Linux) echo "linux" ;;
    Darwin) echo "darwin" ;;
    *) echo "other" ;;
  esac
}

# --- arguments ----------------------------------------------------------------
while [ "$#" -gt 0 ]; do
  case "$1" in
    --post-deploy) MODE="post-deploy"; shift ;;
    --static-only) MODE="static"; shift ;;
    --require-post-deploy) REQUIRE_POST_DEPLOY=1; shift ;;
    --url) DEPLOY_URL="${2:?--url needs a value}"; MODE="post-deploy"; shift 2 ;;
    --tag) DEPLOY_TAG="${2:?--tag needs a value}"; shift 2 ;;
    --openapi-version) EXPECTED_OPENAPI_VERSION="${2:?--openapi-version needs a value}"; shift 2 ;;
    --rollback-command) ROLLBACK_COMMAND="${2:?--rollback-command needs a value}"; shift 2 ;;
    --result-dir) RESULT_DIR="${2:?--result-dir needs a value}"; shift 2 ;;
    -h|--help) sed -n '2,45p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "verify.sh: unknown argument $1" >&2; exit 2 ;;
  esac
done

# A DEPLOY_URL in the environment selects post-deploy on its own, so a release
# job that sets it and forgets the flag still gets the gate it asked for.
[ -n "$DEPLOY_URL" ] && MODE="post-deploy"

# --- result artifact ----------------------------------------------------------
mkdir -p "$RESULT_DIR"
RESULT_FILE="$RESULT_DIR/verify-$(date -u +%Y%m%dT%H%M%SZ).json"
: > "$RESULT_FILE.tmp"

finish() {
  local status
  if [ "$EXIT_CODE" -eq 0 ]; then status="PASS"; else status="FAIL"; fi
  {
    printf '{\n'
    printf '  "reason": "%s",\n' "$REASON"
    printf '  "status": "%s",\n' "$status"
    printf '  "exit": %d,\n' "$EXIT_CODE"
    printf '  "mode": "%s",\n' "$MODE"
    printf '  "deployedTag": "%s",\n' "${DEPLOY_TAG:-null}"
    printf '  "deployedUrl": "%s",\n' "${DEPLOY_URL:-null}"
    printf '  "passed": %d,\n' "$PASS"
    printf '  "failed": %d,\n' "$FAIL"
    printf '  "rollbackTriggered": %s,\n' "$([ -n "$ROLLBACK_NOTE" ] && echo true || echo false)"
    printf '  "rollbackNote": "%s",\n' "${ROLLBACK_NOTE:-null}"
    printf '  "checks": [\n'
    local first=1
    for entry in "${CHECKS[@]:-}"; do
      [ -n "$entry" ] || continue
      local verdict="${entry%%|*}"
      local rest="${entry#*|}"
      local label="${rest%%|*}"
      local detail=""
      case "$rest" in *\|*) detail="${rest#*|}";; esac
      if [ "$first" -eq 0 ]; then printf ',\n'; fi
      first=0
      printf '    { "result": "%s", "check": "%s", "detail": "%s" }' \
        "$verdict" "${label//\"/\'}" "${detail//\"/\'}"
    done
    printf '\n  ]\n}\n'
  } > "$RESULT_FILE.tmp"
  mv "$RESULT_FILE.tmp" "$RESULT_FILE"

  echo ""
  echo "== result: $PASS passed, $FAIL failed =="
  [ -n "$ROLLBACK_NOTE" ] && echo "rollback: $ROLLBACK_NOTE"
  echo "artifact: $RESULT_FILE"
  # The single machine-readable line, last.
  echo "VERIFY_RESULT reason=$REASON status=$status exit=$EXIT_CODE"
  exit "$EXIT_CODE"
}

fail_with() { REASON="$1"; EXIT_CODE=1; bad "$2"; shift 2; while [ "$#" -gt 0 ]; do bad "$1"; shift; done; finish; }

# =============================================================================
# STATIC MODE
# =============================================================================
run_static() {
  echo "== structural checks (python3 + PyYAML) =="
  if have python3; then
    if python3 - "$K8S" <<'PY'
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
    # Task 047: the scheduled backup. Listed so deleting the manifest is a
    # structural failure rather than something only the backup-policy gate
    # notices - and `backup-cronjob.yaml` is a file `scope.json` named for a
    # task before it existed, so "the gate would have caught it" is not an
    # argument for leaving it off this list.
    "backup-cronjob.yaml": ["CronJob"],
    # Task 043A: the static origin, moved out of the flat layout into its own
    # directory alongside the TLS ingress and the build-config ConfigMap.
    "frontend/deployment.yaml": ["Deployment"],
    "frontend/service.yaml": ["Service"],
    "frontend/ingress.yaml": ["Ingress"],
    "frontend/configmap.yaml": ["ConfigMap"],
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
    print(f"{len(fures)} structural failure(s)")
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

# Task 043B (R1): the ordering is a CLAIM, and these are the three ways the
# claim is kept. Each one can be present while the release is still unsafe, so
# each is asserted separately rather than inferred from the presence of another.
#
#   1. the Job's container and the API's `wait-for-migrations` initContainer run
#      the SAME binary, from the same image. Two different mechanisms would be
#      two different definitions of "migrations applied", and the one the runbook
#      documents is not necessarily the one that gates.
#   2. the initContainer is in the API Deployment at all. Without it, an
#      out-of-band apply that skipped the Job would start pods against a schema
#      they do not understand.
#   3. the Job is DELETEd before it is re-applied in the documented order. A
#      Job's spec is immutable, so re-applying a completed Job is a no-op and
#      `kubectl wait --for=condition=complete` is then satisfied by the PREVIOUS
#      release's success. That false pass was demonstrated on a real cluster by
#      deploy/rollout/rehearse-rollback.sh; the assertion here is that the
#      remedy is in the procedure somebody actually reads.
migrate = next(c for c in j["spec"]["template"]["spec"]["containers"] if c["name"] == "migrate")
init = next(c for c in dep["spec"]["template"]["spec"]["initContainers"] if c["name"] == "wait-for-migrations")
assert migrate["command"] == init["command"], (
    "the migration Job and the API's gate run different commands: "
    + str(migrate["command"]) + " vs " + str(init["command"]) + ". There would be two definitions of "
    "'migrations have been applied', and only one of them is documented.")
assert migrate["image"] == init["image"], (
    "the migration Job and the API's gate run different images, so the two can carry different bundles.")
assert init["command"] == ["/app/efbundle"], init["command"]
# Both take the maintenance-role connection string, never the app role: the app
# role has no DDL, so a gate using it would fail for a reason that looks like a
# broken migration.
for container, where in ((migrate, "the migration Job"), (init, "the API migration gate")):
    ref = next(e for e in container["env"] if e["name"] == "ConnectionStrings__Default")["valueFrom"]["secretKeyRef"]
    assert ref["key"] == "maintenance-connection", (
        where + " uses the '" + str(ref["key"]) + "' secret key. It must be 'maintenance-connection' (the "
        "BYPASSRLS role); the app role has no DDL, so the failure reads as a broken migration.")
assert dep["spec"]["strategy"]["rollingUpdate"]["maxUnavailable"] == 0, (
    "the API rolls with maxUnavailable 0, so the previous revision keeps serving while the new one is gated. "
    "A migration failure blocks the rollout; it does not stop traffic.")
print("migration ordering OK: one gate binary, maintenance role on both, maxUnavailable 0")

readme = open(os.path.join(os.path.dirname(base), "README.md"), encoding="utf-8").read()
assert "delete job dubbing-migration" in readme, (
    "deploy/README.md's deploy order does not DELETE the migration Job before applying it. A completed Job's "
    "spec is immutable, so the re-apply is a no-op and 'kubectl wait --for=condition=complete' is then "
    "satisfied by the previous release's success - a false pass on the gate R1 depends on. Demonstrated on a "
    "real cluster; see deploy/rollout.md section 1.")
print("deploy order OK: the migration Job is deleted before it is re-applied")

# Media bounded-concurrency + scratch contract.
prep = list(yaml.safe_load_all(open(os.path.join(base, "workers-media-prep.yaml"), encoding="utf-8")))
pdep = next(d for d in prep if d.get("kind") == "Deployment")
penv = {e["name"]: e.get("value") for e in pdep["spec"]["template"]["spec"]["containers"][0]["env"] if "value" in e}
assert penv.get("Media__MaxConcurrentMediaJobs") == "2", penv
pvc = next(d for d in prep if d.get("kind") == "PersistentVolumeClaim")
assert pvc["spec"]["resources"]["requests"]["storage"] == "50Gi", pvc["spec"]
assert pdep["spec"]["replicas"] == 3, pdep["spec"]
print("media contract OK: concurrency 2, 50Gi scratch PVC, 3 replicas")

# Task 042: the frontend image the frontend pipeline builds, and the nginx
# configuration it is built from. Both are now deployed artifacts, so both belong
# in the release gate's static tier - a manifest that references an image nothing
# builds is a deployment that fails at pull time, in a cluster, during a release.
nginx = os.path.join(os.path.dirname(base), "nginx", "default.conf")
snippet = os.path.join(os.path.dirname(base), "nginx", "security-headers.conf")
if not os.path.isfile(nginx):
    print("FAIL: deploy/nginx/default.conf is missing; Dockerfile.frontend COPYs it")
    sys.exit(1)
if not os.path.isfile(snippet):
    print("FAIL: deploy/nginx/security-headers.conf is missing; Dockerfile.frontend COPYs it and every "
          "location block with its own add_header includes it")
    sys.exit(1)
if not os.path.isfile(os.path.join(os.path.dirname(base), "..", "Dockerfile.frontend")):
    print("FAIL: Dockerfile.frontend is missing; the frontend pipeline builds it")
    sys.exit(1)
nginx_text = open(nginx, encoding="utf-8").read()
# A cached index.html is a white screen after every deploy: the document names
# the current hashed assets, so a stale copy references hashes the deploy has
# already replaced. Asserted here because it is the single most damaging line in
# that file and it is invisible in a smoke test.
if "no-cache" not in nginx_text and "no-store" not in nginx_text:
    print("FAIL: deploy/nginx/default.conf does not disable caching for index.html")
    sys.exit(1)
if "try_files" not in nginx_text:
    print("FAIL: deploy/nginx/default.conf has no SPA fallback; deep links will 404")
    sys.exit(1)
print("frontend delivery contract OK: Dockerfile.frontend present, index.html uncached, SPA fallback present")

# Task 043 (R4) / Task 043A: the static origin must not be reachable except through
# the edge. A second public route to the document serves the identical bytes over
# plain HTTP with no edge policy - no HTTPS redirect, no HSTS, no security headers -
# and that is the failure the Service type exists to prevent.
#
# Task 043A changed the arrangement, so the assertion changed with it. There was no
# Ingress at all; there is now a TLS-terminating one, and the property that matters
# is no longer "there is no Ingress" but "the Service is ClusterIP and the only
# Ingress selecting it is the one in frontend/ingress.yaml, which binds the ORIGIN
# hostname rather than the public one". Asserting the old wording would have made
# the gate pass on a manifest that had been opened to the world.
frontend_dir = os.path.join(base, "frontend")
static = list(yaml.safe_load_all(open(os.path.join(frontend_dir, "deployment.yaml"), encoding="utf-8")))
sdep = next(d for d in static if d.get("kind") == "Deployment")
ssvc = list(yaml.safe_load_all(open(os.path.join(frontend_dir, "service.yaml"), encoding="utf-8")))
ssvc = next(d for d in ssvc if d.get("kind") == "Service")
assert ssvc["spec"]["type"] == "ClusterIP", (
    f"the static Service is {ssvc['spec']['type']}; a NodePort or LoadBalancer is a second public route "
    "to the document that bypasses the CDN and the TLS edge")
assert ssvc["spec"]["ports"][0]["targetPort"] == 8080, ssvc["spec"]["ports"]
assert sdep["spec"]["template"]["spec"]["containers"][0]["ports"][0]["containerPort"] == 8080, (
    "the static origin must serve on 8080; the unprivileged nginx image cannot bind 80")
assert sdep["spec"]["replicas"] >= 2, sdep["spec"]["replicas"]
sc = sdep["spec"]["template"]["spec"]["containers"][0]
assert sc["livenessProbe"]["httpGet"]["path"] == "/healthz", sc["livenessProbe"]
assert sc["readinessProbe"]["httpGet"]["path"] == "/healthz", sc["readinessProbe"]
# The ConfigMap is a BUILD INPUT record. A pod that reads it is a pod that believes
# VITE_* values are runtime, which they are not - and an operator who changes it
# would see nothing happen and conclude the deployment is broken.
assert "env" not in sc, "the static container must not declare runtime env vars"
assert "envFrom" not in sc, "the static container must not declare envFrom"

ingresses = []
for root, _dirs, files in os.walk(base):
    # Overlay files are excluded, and deliberately: an overlay's
    # `frontend-ingress-patch.yaml` is a TRANSFORMATION of the same Ingress, not a
    # second object, and counting it as one would make the correct per-environment
    # host/issuer look like a second public route. Only base manifests are objects
    # in their own right.
    if os.sep + "overlays" + os.sep in os.path.join(root, "") + os.sep:
        continue
    for fname in sorted(files):
        if not fname.endswith((".yaml", ".yml")):
            continue
        for doc in yaml.safe_load_all(open(os.path.join(root, fname), encoding="utf-8")):
            if isinstance(doc, dict) and doc.get("kind") == "Ingress":
                ingresses.append((os.path.relpath(os.path.join(root, fname), base), doc))
selecting_frontend = [
    name for name, doc in ingresses
    for rule in (doc.get("spec", {}).get("rules") or [])
    for path in ((rule.get("http") or {}).get("paths") or [])
    if ((path.get("backend") or {}).get("service") or {}).get("name") == "frontend"
]
assert selecting_frontend == ["frontend" + os.sep + "ingress.yaml"], (
    f"these base Ingress rules route to the frontend Service: {selecting_frontend}. Exactly one may exist "
    "(frontend/ingress.yaml); a second is a second public route to the document.")
fe_ing = next(d for name, d in ingresses if name.endswith("ingress.yaml") and "frontend" in name)
assert fe_ing["spec"].get("tls"), "the frontend ingress terminates no TLS, so plain HTTP is served"
ann = fe_ing["metadata"].get("annotations") or {}
assert ann.get("nginx.ingress.kubernetes.io/force-ssl-redirect") == "true", (
    "the frontend ingress does not force an HTTPS redirect; a user on http:// receives a cleartext "
    "response body before being redirected, and that response was already observable")
for banned in ("configuration-snippet", "server-snippet"):
    assert not any(banned in key for key in ann), (
        f"the frontend ingress uses a {banned} annotation, which ingress-nginx disables by default "
        "(allow-snippet-annotations: false since 1.9) - the header set would silently stop applying")
print("static origin contract OK: ClusterIP, one TLS ingress, 8080, /healthz probes, no runtime env")

# Task 043A: overlay integrity. `kustomize build` cannot be relied on to tell us
# this, for two reasons that are both pre-existing and both silent:
#
#   * the `..` resources entries need `--load-restrictor=LoadRestrictionsNone`,
#     and the default restrictor rejects them before reading anything;
#   * the overlay patches do not match their targets, because the base manifests
#     hard-code `namespace: dubbing-prod` while the kustomizations set the
#     environment namespace and the patches declare none.
#
# So the check that CAN be made is the one that does not need kustomize: every
# path a kustomization references exists, every `images:` name is built by a
# Dockerfile in this repository, and every `replicas:` entry names a Deployment
# that exists. A `resources:` entry pointing at a file nobody added is an overlay
# that silently deploys less than it appears to, and it is invisible in a build
# that already fails for another reason.
import re as _re

# The image references that actually exist, read from the base manifests rather
# than from the Dockerfiles: no Dockerfile in this repository declares an
# `image:` line (CI stamps the tag), so the manifests are the authority on what
# reference a kustomize `images:` transformer can match.
#
# That matters because a kustomize `images:` entry that matches NOTHING is a
# silent no-op: the overlay still applies, the promotion still reports success, and
# the cluster pulls whatever the manifest said - `ghcr.io/CHANGE_ME/...:latest`.
base_image_refs = set()
for root, _dirs, files in os.walk(base):
    for fname in files:
        if not fname.endswith((".yaml", ".yml")):
            continue
        for doc in yaml.safe_load_all(open(os.path.join(root, fname), encoding="utf-8")):
            if not isinstance(doc, dict) or doc.get("kind") not in ("Deployment", "StatefulSet", "Job"):
                continue
            for container in (((doc.get("spec") or {}).get("template") or {}).get("spec") or {}).get("containers") or []:
                if container.get("image"):
                    base_image_refs.add(container["image"])
assert base_image_refs, "no base manifest declares a container image, so the image check below would be vacuous"

def _image_name(ref):
    # kustomize's `images:` transformer matches on the NAME (registry + repository)
    # and sets the tag separately, so `ghcr.io/x/y` in the kustomization and
    # `ghcr.io/x/y:latest` in the manifest are the same entry. Strip the tag, and
    # only when the colon is after the last slash so a registry with a port
    # (`registry.internal:5000/x/y`) is not cut in half.
    slash = ref.rfind("/")
    colon = ref.rfind(":")
    return ref[:colon] if colon > slash else ref

base_image_names = {_image_name(ref) for ref in base_image_refs}
base_names = set()
for root, _dirs, files in os.walk(base):
    for fname in files:
        if not fname.endswith((".yaml", ".yml")):
            continue
        for doc in yaml.safe_load_all(open(os.path.join(root, fname), encoding="utf-8")):
            if isinstance(doc, dict) and doc.get("kind") in ("Deployment", "StatefulSet"):
                base_names.add((doc.get("metadata") or {}).get("name"))

for overlay in ("staging", "prod"):
    kpath = os.path.join(base, "overlays", overlay, "kustomization.yaml")
    if not os.path.isfile(kpath):
        raise AssertionError("deploy/k8s/overlays/" + overlay + "/kustomization.yaml is missing")
    kdoc = yaml.safe_load(open(kpath, encoding="utf-8"))
    here = os.path.join(base, "overlays", overlay)
    for entry in kdoc.get("resources") or []:
        target = os.path.normpath(os.path.join(here, entry))
        assert os.path.isfile(target), (
            "overlays/" + overlay + " lists a resource that does not exist: " + entry +
            ". An overlay that references a missing resource deploys less than it appears to, and a "
            "kustomize build that already fails for another reason will not tell you about this one.")
    for patch in kdoc.get("patches") or []:
        target = os.path.normpath(os.path.join(here, patch.get("path", "")))
        assert os.path.isfile(target), (
            "overlays/" + overlay + " lists a patch that does not exist: " + str(patch.get("path")))
    for entry in kdoc.get("images") or []:
        name = entry.get("name", "")
        assert name in base_image_names, (
            "overlays/" + overlay + " pins the image '" + name + "', which no base manifest declares. A "
            "kustomize `images:` entry that matches nothing is a silent no-op: the overlay still applies, the "
            "promotion still reports success, and the cluster pulls whatever the manifest said - "
            + ", ".join(sorted(base_image_names)) + ".")
    for entry in kdoc.get("replicas") or []:
        assert entry.get("name") in base_names, (
            "overlays/" + overlay + " sets replicas for '" + str(entry.get("name")) + "', which is not a "
            "Deployment in deploy/k8s. The transformer is a no-op and the intended count is never applied.")
print("overlay contract OK: 2 overlays, every resource/patch path exists, every pinned image and replica "
      "target resolves to a base manifest")

# Task 043 (R4): the network topology. The default-deny is what makes every
# allow meaningful, and its absence is the single change that would open the
# data stores to the whole namespace. Asserted here as well as in
# deploy/tests/hosting.test.sh because this is the release gate and that one is
# the hosting gate; a manifest that fails one should fail the other.
#
# `os.walk`, not `os.listdir` (Task 043A). 043 recorded the flat listing as a
# gotcha to pass on; there is now a subdirectory under deploy/k8s, so the
# assumption is not a caveat any more - it is a live way for a policy placed
# beside its workload to be silently skipped by the checker that exists to read
# it. The same change is made in deploy/tests/hosting-topology.py.
policies = []
for root, _dirs, files in os.walk(base):
    for name in sorted(files):
        if not name.endswith((".yaml", ".yml")):
            continue
        for doc in yaml.safe_load_all(open(os.path.join(root, name), encoding="utf-8")):
            if isinstance(doc, dict) and doc.get("kind") == "NetworkPolicy":
                policies.append((name, doc))
assert policies, "no NetworkPolicy in deploy/k8s at all"
default_deny = [
    name for name, doc in policies
    if (doc.get("spec") or {}).get("podSelector") == {}
    and set((doc.get("spec") or {}).get("policyTypes") or []) == {"Ingress", "Egress"}
]
assert default_deny, (
    "no NetworkPolicy has podSelector {} with policyTypes [Ingress, Egress]. Every allow is an addition "
    "to 'everything is permitted' without one")
components = {
    (doc.get("spec") or {}).get("podSelector", {}).get("matchLabels", {}).get("app.kubernetes.io/component")
    for _, doc in policies
}
for required in ("api", "migration", "static"):
    assert required in components, (
        f"no NetworkPolicy selects the {required} component; its traffic is unconstrained")
migration_egress = [
    port
    for _, doc in policies
    if (doc.get("spec") or {}).get("podSelector", {}).get("matchLabels", {}).get("app.kubernetes.io/component") == "migration"
    for rule in ((doc.get("spec") or {}).get("egress") or [])
    for port in sorted({p.get("port") for p in (rule.get("ports") or [])})
]
assert set(migration_egress) <= {5432}, (
    f"the migration job's egress permits ports {sorted(set(migration_egress))}; it needs PostgreSQL (5432) "
    "and DNS only. A migration with broker access can publish.")
print(f"topology contract OK: default-deny ({default_deny[0]}), api/migration/static policies present")
PY
    then
      ok "structural + contract checks"
    else
      fail_with "$REASONS_MANIFEST" "structural + contract checks"
    fi
  else
    # python3 is the one hard dependency of the structural tier, and a release
    # gate that skipped its own structural assertions because of a missing
    # interpreter would be a gate that reports success having checked nothing.
    echo "::error::python3 is required for the structural checks."
    fail_with "$REASONS_INPUT" "structural checks" "python3 is not installed; the structural tier is mandatory and is not skipped"
  fi

  echo ""
  echo "== rollout window (043B) =="
  # A separate gate rather than another block in the structural tier above, and
  # the reason is the same one that keeps the hosting policy out of the shell: the
  # structural tier has a parsed PyYAML view of every manifest, and this gate
  # answers questions about C# migration source and JSON records, which a YAML
  # parser cannot read. One gate per question, in the language that can read it.
  if have node; then
    if node "$ROOT/scripts/check-rollout-window.mjs"; then
      ok "rollout window: migration additivity, compatibility matrix, flag register, rollback rehearsal"
    else
      bad "rollout window: migration additivity, compatibility matrix, flag register, rollback rehearsal"
    fi
  else
    # Fail-closed, for the same reason python3 is: a release gate that skipped its
    # own structural assertions because a tool was missing is a gate that reports
    # success having checked nothing.
    echo "::error::node is required for the rollout-window checks."
    fail_with "$REASONS_INPUT" "rollout window" "node is not installed; the expand/contract window, the flag register and the rehearsal record are not verified without it"
  fi

  echo ""
  echo "== backup policy (047) =="
  # The backup/restore release gate. Task 047's instruction 5 is a rule, and this
  # is where the rule is enforced: `deploy/backup/policy.json` declares which
  # entity groups the backup job covers, what the RPO/RTO are, who to escalate
  # to, and the gap register - and an entry with status=open and blocking=true
  # fails here.
  #
  # It is a SEPARATE block rather than another assertion in the structural tier
  # for the rollout gate's reason: that tier has a parsed PyYAML view of the
  # manifests, and this gate asks questions about a JSON policy, a bash script
  # and what the migrations create, which no YAML parser can answer. One gate per
  # question, in the language that can read it.
  #
  # Fail-closed on a missing node, for the same reason python3 is: a release gate
  # that skipped its own assertions because a tool was missing is a gate that
  # reports success having checked nothing - and this one would report that a
  # release is safe to ship with a durable entity that has never been dumped.
  if have node; then
    if node "$ROOT/scripts/check-backup-policy.mjs"; then
      ok "backup policy: coverage, tier assignment, RPO/RTO, escalation, drill record, release gate"
    else
      bad "backup policy: coverage, tier assignment, RPO/RTO, escalation, drill record, release gate"
      echo "  A failing backup policy is a RELEASE BLOCKER, not a warning. The findings above"
      echo "  name the file to fix: deploy/backup/policy.json, deploy/k8s/backup-cronjob.yaml,"
      echo "  or the gap register in docs/backup.md. See 'The release gate' in docs/backup.md."
    fi
  else
    echo "::error::node is required for the backup-policy checks."
    fail_with "$REASONS_INPUT" "backup policy" "node is not installed; coverage, RPO/RTO, escalation and the gap register are not verified without it"
  fi

  echo ""
  echo "== kubectl dry-run =="
  if have kubectl; then
    if kubectl version --client >/dev/null 2>&1 && kubectl config current-context >/dev/null 2>&1; then
      # `--validate=false`: CRDs (ScaledObject, ExternalSecret) may be unknown to
      # the target cluster version; schema conformance is covered by kubeconform.
      if kubectl apply --dry-run=client --validate=false -f "$K8S" >/dev/null 2>&1; then
        ok "kubectl apply --dry-run=client -f deploy/k8s/"
      else
        bad "kubectl apply --dry-run=client -f deploy/k8s/"
      fi
    else
      skip "kubectl apply --dry-run=client -f deploy/k8s/" "no usable kubeconfig context; runs in a cluster-aware CI job"
    fi
  else
    skip "kubectl apply --dry-run=client -f deploy/k8s/" "kubectl not installed"
  fi

  echo ""
  echo "== kubeconform =="
  if have kubeconform; then
    if kubeconform -summary -ignore-missing-schemas "$K8S"/namespace.yaml "$K8S"/configmap.yaml \
        "$K8S"/api-deployment.yaml "$K8S"/workers-*.yaml "$K8S"/ingress.yaml \
        "$K8S"/networkpolicies.yaml "$K8S"/pdb.yaml "$K8S"/migration-job.yaml \
        "$K8S"/backup-cronjob.yaml >/dev/null 2>&1; then
      ok "kubeconform (core kinds; CRDs excluded)"
    else
      bad "kubeconform"
    fi
  else
    skip "kubeconform" "kubeconform not installed"
  fi

  echo ""
  echo "== kustomize overlays =="
  # `--load-restrictor=LoadRestrictionsNone` (Task 043A). kustomize's default
  # `LoadRestrictionsRootOnly` refuses a `resources:` entry that leaves the
  # kustomization's own directory with `..`, and EVERY entry in these overlays is
  # exactly that - so the overlays have never been buildable as written, on any
  # platform. The restriction is a supply-chain control: it stops a base from
  # reaching outside its own tree. It is disabled here because the base IS the
  # parent directory by design, and the structural tier now asserts the
  # parent-relative integrity the restriction would otherwise have provided: every
  # referenced path must exist, and every `images:` name must be built by a
  # Dockerfile in this repository.
  #
  # The remaining failure on this tier is PRE-EXISTING and is not this flag: the
  # overlay patches do not match their targets, because deploy/k8s/*.yaml hard-codes
  # `namespace: dubbing-prod` while each kustomization sets `namespace:` to the
  # environment and each patch declares none - so the target id is
  # `dubbing-config.[noNs]` and the resource is `dubbing-config.dubbing-prod` at the
  # point patches are applied. See the 043A report, "Open items inherited".
  KUSTOMIZE_ARGS="--load-restrictor=LoadRestrictionsNone"
  # kustomize resolves overlay `resources:` relative to the overlay directory and
  # refuses a parent path once the host normalises separators - which is every
  # Windows host. The overlays are built in CI on ubuntu; a developer on Windows
  # gets an explicit, machine-readable SKIP rather than a FAIL that looks like a
  # broken manifest.
  if [ "$(platform)" = "windows" ]; then
    skip "kustomize build staging + prod" "kustomize overlay builds are not supported on a Windows host; built in CI on ubuntu-latest"
  elif have kubectl; then
    if kubectl kustomize $KUSTOMIZE_ARGS "$K8S/overlays/staging" >/dev/null 2>&1 \
      && kubectl kustomize $KUSTOMIZE_ARGS "$K8S/overlays/prod" >/dev/null 2>&1; then
      ok "kustomize build staging + prod"
    else
      bad "kustomize build staging + prod (see the load-restrictor and patch-target notes above)"
    fi
  elif have kustomize; then
    if kustomize build $KUSTOMIZE_ARGS "$K8S/overlays/staging" >/dev/null 2>&1 \
      && kustomize build $KUSTOMIZE_ARGS "$K8S/overlays/prod" >/dev/null 2>&1; then
      ok "kustomize build staging + prod"
    else
      bad "kustomize build staging + prod (see the load-restrictor and patch-target notes above)"
    fi
  else
    skip "kustomize build" "neither kubectl nor kustomize installed"
  fi

  echo ""
  echo "== helm lint =="
  if have helm; then
    if helm lint "$ROOT/deploy/helm/dubbing" -f "$ROOT/deploy/helm/dubbing/values-prod.yaml" \
      && helm lint "$ROOT/deploy/helm/dubbing" -f "$ROOT/deploy/helm/dubbing/values-staging.yaml" >/dev/null 2>&1; then
      ok "helm lint (prod + staging values)"
    else
      bad "helm lint"
    fi
  else
    skip "helm lint" "helm not installed"
  fi

  echo ""
  echo "== post-deploy gate not run =="
  if [ "$REQUIRE_POST_DEPLOY" = "1" ]; then
    # The release job wires this. Without DEPLOY_URL a release must NOT be
    # reported as verified - a static check is not a deployed-environment check,
    # and a release pipeline that degrades to it would mark releases verified
    # having contacted nothing.
    fail_with "$REASONS_URL_REQUIRED" \
      "post-deploy verification was required (--require-post-deploy / VERIFY_REQUIRE_POST_DEPLOY=1) but no DEPLOY_URL was provided" \
      "Set DEPLOY_URL (and DEPLOY_TAG) so the health, OpenAPI-version and smoke checks run against the deployed environment"
  else
    skip "health / openapi version / smoke / rollback" "static mode; no DEPLOY_URL. Use --post-deploy with DEPLOY_URL and DEPLOY_TAG for the release gate"
  fi

  if [ "$FAIL" -ne 0 ]; then
    # `MIGRATION_NOT_ADDITIVE` is called out separately because it is the one
    # reason that is not about the manifests being wrong. The manifests can be
    # perfectly valid and the release still unshippable, and an operator reading
    # `MANIFEST_CHECK_FAILED` for a broken expand/contract window would go looking
    # in the wrong file.
    #
    # `BACKUP_GAP_BLOCKS_RELEASE` is called out for the same class of reason and
    # the same reason (Task 047): the manifests can be perfect, the release can
    # be a good release, and it is still unshippable because a durable entity has
    # no backup or a restore has never been verified. An operator sent to
    # `deploy/k8s/` for a missing backup entry is sent to the wrong file - the
    # answer is in `deploy/backup/policy.json` and the gap register in
    # `docs/backup.md`.
    #
    # Checked before the rollout-window test on purpose: a release that is
    # blocked on a backup gap AND on a migration is blocked on both, and the
    # backup reason is the one that decides whether the data is recoverable at
    # all. Both are in `CHECKS`, so neither is hidden by the other being named.
    if printf '%s\n' "${CHECKS[@]:-}" | grep -q "FAIL|backup policy"; then
      REASON="$REASONS_BACKUP"
    elif printf '%s\n' "${CHECKS[@]:-}" | grep -q "FAIL|rollout window"; then
      REASON="$REASONS_ROLLBACK_WINDOW"
    else
      REASON="$REASONS_MANIFEST"
    fi
    EXIT_CODE=1
  fi
  finish
}

# =============================================================================
# POST-DEPLOY MODE - the release gate (R4)
# =============================================================================
http_status() { # url timeout -> the status code, or 000
  # curl already writes `000` to stdout when it cannot connect, and ALSO exits
  # non-zero - so `curl ... || echo 000` yields `000000`. The default is applied
  # only when curl produced nothing at all, which is the case where it was killed
  # before writing.
  local code
  code="$(curl --silent --output /dev/null --write-out '%{http_code}' --max-time "${2:-15}" "$1" 2>/dev/null || true)"
  if [ -z "$code" ]; then code="000"; fi
  printf '%s' "$code"
}

http_body() { # url
  curl --silent --location --max-time 20 "$1" 2>/dev/null || true
}

trigger_rollback() { # triggering_reason
  local command_line="$ROLLBACK_COMMAND"
  echo ""
  echo "== rollback =="
  # The TRIGGERING reason is recorded separately from the verdict reason. They are
  # different facts: `OPENAPI_VERSION_MISMATCH` is what failed the release, and
  # `ROLLBACK_UNAVAILABLE` is what could not be done about it. Overwriting the
  # first with the second would make the artifact say "we could not roll back"
  # for a release that failed for a completely different reason, and a dashboard
  # keyed on `reason` would report rollback problems for every kind of failure.
  ROLLBACK_NOTE="triggered by ${1}"
  # Every caller passes "<REASON_CODE>: <human detail>". The code becomes the
  # verdict reason so the artifact names WHY the release failed; the detail
  # becomes the rollback note. Keeping them in one argument is what stops the two
  # from drifting apart.
  REASON="${1%%:*}"
  if [ -z "$command_line" ]; then
    ROLLBACK_NOTE="${ROLLBACK_NOTE}; no ROLLBACK_COMMAND was configured, so no rollback was performed"
    echo "::error::verification failed with $1 and no ROLLBACK_COMMAND was configured, so no rollback was performed."
    echo "Run the documented rollback by hand - deploy/README.md, 'Rollback':"
    echo "  kubectl -n <namespace> rollout undo deploy/<name>"
    CHECKS+=("FAIL|rollback|not executed: no ROLLBACK_COMMAND configured")
    return 1
  fi
  echo "$ROLLBACK_NOTE"
  echo "running: $command_line"
  # The command is supplied by the operator through ROLLBACK_COMMAND, which is
  # configuration rather than a value derived from the deployment being checked.
  # It is still executed through `bash -c` as a single argument, never through an
  # interpolated string, so a tag or URL containing a space or a semicolon cannot
  # become a second command.
  if DEPLOY_TAG="$DEPLOY_TAG" DEPLOY_URL="$DEPLOY_URL" bash -c "$command_line"; then
    ROLLBACK_NOTE="${ROLLBACK_NOTE}; rollback command completed"
    ok "rollback executed after $1"
    return 0
  fi
  echo "::error::the rollback command failed. This is now a manual incident: deploy/README.md, 'Rollback'."
  ROLLBACK_NOTE="${ROLLBACK_NOTE}; the rollback command FAILED - manual intervention required"
  bad "rollback command failed after $1"
  return 1
}

run_post_deploy() {
  if [ -z "$DEPLOY_URL" ]; then
    echo "::error::post-deploy verification requires DEPLOY_URL (or --url)."
    fail_with "$REASONS_URL_REQUIRED" "DEPLOY_URL is required in post-deploy mode"
  fi
  DEPLOY_URL="${DEPLOY_URL%/}"
  echo "== post-deploy verification =="
  echo "target: $DEPLOY_URL"
  echo "tag:    ${DEPLOY_TAG:-<not supplied>}"
  if ! have curl; then
    echo "::error::curl is required for the post-deploy gate."
    fail_with "$REASONS_SMOKE_RUNNER" "curl is not installed" "the post-deploy gate needs curl for every check and does not skip them"
  fi

  local failed_reason=""

  # --- 1. health ---------------------------------------------------------------
  # Liveness and readiness are both checked, and they mean different things:
  # `/health/live` answers "the process is up", `/health/ready` answers "it can
  # serve". A release that only checks liveness passes on a pod that is up and
  # unable to reach its database - which is precisely the state a bad migration
  # leaves behind.
  echo ""
  echo "-- health --"
  local endpoint status
  for endpoint in /health/live /health/ready; do
    status="$(http_status "$DEPLOY_URL$endpoint" 10)"
    if [ "$status" = "200" ]; then
      ok "GET $endpoint -> 200"
    else
      bad "GET $endpoint -> $status (expected 200)"
      [ -z "$failed_reason" ] && failed_reason="$REASONS_HEALTH: GET $endpoint returned $status"
    fi
  done
  if [ -n "$failed_reason" ]; then
    trigger_rollback "$failed_reason" || true
    EXIT_CODE=1
    finish
  fi

  # --- 2. the deployed document matches the deployed tag ------------------------
  # This is the check that catches a rollback to the wrong image, a partial
  # rollout, and a deploy whose manifest tag drifted from what was verified in
  # CI. The API serves its document anonymously at `/openapi/v1.json`; the
  # conventional `/openapi.json` is tried first so a future route change does not
  # silently turn this into a 404 that reads as a healthy deployment.
  echo ""
  echo "-- openapi version --"
  local document="" document_url=""
  for candidate in /openapi.json /openapi/v1.json; do
    status="$(http_status "$DEPLOY_URL$candidate" 10)"
    if [ "$status" = "200" ]; then
      document_url="$candidate"
      document="$(http_body "$DEPLOY_URL$candidate")"
      break
    fi
  done
  if [ -z "$document" ]; then
    failed_reason="$REASONS_OPENAPI_UNREACHABLE: no OpenAPI document at /openapi.json or /openapi/v1.json (last status ${status:-000})"
    bad "the deployed API serves no OpenAPI document"
    trigger_rollback "$failed_reason" || true
    EXIT_CODE=1
    finish
  fi
  ok "GET $document_url -> 200"

  local deployed_version
  deployed_version="$(printf '%s' "$document" | node -e '
    let raw = "";
    process.stdin.on("data", (chunk) => { raw += chunk; });
    process.stdin.on("end", () => {
      try {
        const parsed = JSON.parse(raw);
        process.stdout.write(String(parsed?.info?.version ?? ""));
      } catch {
        process.stdout.write("");
      }
    });
' 2>/dev/null || true)"
  if [ -z "$deployed_version" ]; then
    failed_reason="$REASONS_OPENAPI_UNREACHABLE: the served document is not JSON, or has no info.version"
    bad "the served OpenAPI document has no readable info.version"
    trigger_rollback "$failed_reason" || true
    EXIT_CODE=1
    finish
  fi
  echo "   deployed info.version: $deployed_version"

  # Three sources of truth, in order of authority:
  #   1. EXPECTED_OPENAPI_VERSION - what the release pipeline recorded;
  #   2. the version in the committed bundle for the deployed tag, which is what
  #      CI verified;
  #   3. DEPLOY_TAG, whose leading `v<N>` is the API line.
  # Falling back rather than failing is deliberate: an operator who supplies
  # neither still gets a check, and a check that only runs when three optional
  # inputs are present is a check that never runs.
  local expected="$EXPECTED_OPENAPI_VERSION"
  local source="EXPECTED_OPENAPI_VERSION"
  if [ -z "$expected" ] && [ -n "$DEPLOY_TAG" ] && have node; then
    expected="$(git -C "$ROOT" show "$DEPLOY_TAG:src/DubbingPlatform.Api/OpenApi/openapi.v1.json" 2>/dev/null \
      | node -e 'let r="";process.stdin.on("data",c=>r+=c);process.stdin.on("end",()=>{try{process.stdout.write(String(JSON.parse(r)?.info?.version??""))}catch{process.stdout.write("")}})' 2>/dev/null || true)"
    source="openapi.v1.json at $DEPLOY_TAG"
  fi
  if [ -z "$expected" ] && [ -n "$DEPLOY_TAG" ]; then
    expected="${DEPLOY_TAG#v}"
    source="the deployed tag $DEPLOY_TAG"
  fi

  if [ -z "$expected" ]; then
    skip "openapi version match" "no EXPECTED_OPENAPI_VERSION, no resolvable $DEPLOY_TAG, and no DEPLOY_TAG; nothing to compare the served version against"
  elif [ "$deployed_version" = "$expected" ]; then
    ok "served info.version $deployed_version matches $source"
  else
    bad "served info.version '$deployed_version' does not match '$expected' (from $source)"
    failed_reason="$REASONS_OPENAPI_VERSION: the deployed API serves $deployed_version but $source says $expected. The deployment and the verified artefact are different builds."
    trigger_rollback "$failed_reason" || true
    EXIT_CODE=1
    finish
  fi

  # --- 3. the smoke suite against the deployed environment ---------------------
  # The task's "partial failure (health ok, smoke fails) -> rollback +
  # SMOKE_FAILED reason artifact" case, handled explicitly: health and version
  # have already passed and been recorded as PASS before this point, so the
  # artifact shows exactly which half failed.
  echo ""
  echo "-- smoke --"
  if [ ! -d "$ROOT/e2e/smoke" ] && [ ! -d "$ROOT/e2e/journeys" ]; then
    # 041A did not land the smoke spec (it is blocked on the upload protocol
    # divergence). Reporting SMOKE_SPECS_MISSING as a FAILURE is the honest
    # outcome: a release gate that reports "no smoke specs, carrying on" is a
    # gate that reports a release verified on the strength of a health check.
    bad "no @smoke spec is present in this repository"
    failed_reason="$REASONS_SMOKE_MISSING: e2e/smoke/ and e2e/journeys/ do not exist, so the @smoke suite cannot run. A release is not verified without it - see the 041A report for why the spec is not written yet."
    trigger_rollback "$failed_reason" || true
    EXIT_CODE=1
    finish
  fi

  if ! have node; then
    bad "node is required to run Playwright"
    failed_reason="$REASONS_SMOKE_RUNNER: node is not installed on the verification host"
    trigger_rollback "$failed_reason" || true
    EXIT_CODE=1
    finish
  fi

  local smoke_report="$RESULT_DIR/smoke-$DEPLOY_TAG.txt"
  mkdir -p "$RESULT_DIR"
  set +e
  ( cd "$ROOT" && DEPLOY_TARGET="$DEPLOY_URL" npx playwright test --grep="@smoke" --reporter=list ) > "$smoke_report" 2>&1
  local smoke_status=$?
  set -e
  tail -40 "$smoke_report" || true
  if [ "$smoke_status" -eq 0 ]; then
    ok "@smoke passed against $DEPLOY_URL (report: $smoke_report)"
  else
    bad "@smoke failed against $DEPLOY_URL (exit $smoke_status; report: $smoke_report)"
    failed_reason="$REASONS_SMOKE_FAILED: the @smoke suite failed against the deployed environment (exit $smoke_status). Health and the OpenAPI version both passed, so the deployment is up and is the expected build; the failure is in behaviour. Full output: $smoke_report"
    trigger_rollback "$failed_reason" || true
    EXIT_CODE=1
    finish
  fi

  echo ""
  echo "== post-deploy verification passed =="
  REASON="$REASONS_OK"
  EXIT_CODE=0
  finish
}

# --- dispatch -----------------------------------------------------------------
if [ "$MODE" = "post-deploy" ]; then
  run_post_deploy
else
  run_static
fi
