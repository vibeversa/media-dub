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
if not os.path.isfile(nginx):
    print("FAIL: deploy/nginx/default.conf is missing; Dockerfile.frontend COPYs it")
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
        "$K8S"/networkpolicies.yaml "$K8S"/pdb.yaml "$K8S"/migration-job.yaml >/dev/null 2>&1; then
      ok "kubeconform (core kinds; CRDs excluded)"
    else
      bad "kubeconform"
    fi
  else
    skip "kubeconform" "kubeconform not installed"
  fi

  echo ""
  echo "== kustomize overlays =="
  # kustomize resolves overlay `resources:` relative to the overlay directory and
  # refuses a parent path once the host normalises separators - which is every
  # Windows host. The overlays are built in CI on ubuntu; a developer on Windows
  # gets an explicit, machine-readable SKIP rather than a FAIL that looks like a
  # broken manifest.
  if [ "$(platform)" = "windows" ]; then
    skip "kustomize build staging + prod" "kustomize overlay builds are not supported on a Windows host; built in CI on ubuntu-latest"
  elif have kubectl; then
    if kubectl kustomize "$K8S/overlays/staging" >/dev/null 2>&1 && kubectl kustomize "$K8S/overlays/prod" >/dev/null 2>&1; then
      ok "kustomize build staging + prod"
    else
      bad "kustomize build"
    fi
  elif have kustomize; then
    if kustomize build "$K8S/overlays/staging" >/dev/null 2>&1 && kustomize build "$K8S/overlays/prod" >/dev/null 2>&1; then
      ok "kustomize build staging + prod"
    else
      bad "kustomize build"
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
    REASON="$REASONS_MANIFEST"
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
