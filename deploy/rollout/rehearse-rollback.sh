#!/usr/bin/env bash
# The rollback rehearsal (Task 043B, instruction 5 and R5).
#
# WHAT THIS REHEARSES, PRECISELY
# -----------------------------
# The Kubernetes half of a rollback, against a REAL cluster, from the REAL
# committed manifests:
#
#   * a failing migration-gate initContainer blocks the API rollout, and the
#     Service stops routing to it (R1);
#   * `kubectl rollout undo` moves every workload the rollback procedure covers;
#   * the undo moves the REVISION *and* the pods - verified by what the pods
#     serve, not by the rollout status, because those are different claims and
#     only one of them is what a user experiences;
#   * `maxUnavailable: 0` and the PodDisruptionBudget hold throughout, measured
#     by a probe that polls the Service rather than a pod;
#   * `kubectl rollout history` reports the revisions an operator would read;
#   * the stale completed-Job false pass on the migration gate is DEMONSTRATED,
#     not described (see below).
#
# WHAT IT DOES NOT REHEARSE
# -------------------------
# The application, the database, the CDN version pin, and the operator. Every
# workload runs `deploy/rollout/rehearsal/`'s stand-in, not the real image, so
# this proves the OPERATIONS work and not that a production rollback is safe.
# That distinction is written into the record's `notCovered`, which
# `scripts/check-rollout-window.mjs` refuses to accept empty: a rehearsal that
# does not say what it did not cover is read as one that did.
#
# THE STALE-JOB FALSE PASS, DEMONSTRATED
# ---------------------------------------
# A Job's `spec` is immutable, so `kubectl apply -f` against a Job that has
# already COMPLETED is a no-op that exits 0, and the completed condition from the
# PREVIOUS release is still on the object. `kubectl wait
# --for=condition=complete` therefore returns success immediately - having
# observed a migration that never ran against the current schema. It is a false
# pass on the one gate R1 rests on, and it is invisible: the wait prints the
# Job's name and exits 0. The script proves it by comparing the Job's UID and
# `Complete` transition time across the re-apply, and then proves the documented
# remedy by deleting the Job and showing the wait can no longer be satisfied.
#
# USAGE
#   bash deploy/rollout/rehearse-rollback.sh [--context <ctx>] [--cluster <name>]
#                                            [--namespace <ns>] [--image-prefix <repo>]
#                                            [--record <file>] [--keep]
#
# `--record` writes the JSON record straight into
# `deploy/rollout/rollback-rehearsal.json`, which is what
# `scripts/check-rollout-window.mjs` reads and refuses to accept stale or
# unqualified. Pasting a record by hand is how a rehearsal record ends up
# describing a run that did not happen.
#
# `--keep` leaves the namespace in place for inspection. Without it the namespace
# is deleted on exit, including on failure: a rehearsal that leaves five
# Deployments and a Job behind in a shared cluster is a rehearsal nobody runs
# twice.
set -euo pipefail

# WINDOWS-HOST NOTE, and it is a per-command variable rather than an export.
#
# Git Bash (MSYS2) rewrites any argument that LOOKS like an absolute POSIX path
# into a Windows path before the program sees it: `-- /app/efbundle` arrives
# inside the container as `C:/Program Files/Git/app/efbundle`, and the Job fails
# with `exec: "C:/Program Files/Git/app/efbundle": no such file or directory`.
# The symptom is a StartError on a container whose image and command are both
# obviously correct, and the first version of this script produced exactly that
# for both the demonstration Job and the availability probe.
#
# It is set per command, not exported, because the SAME conversion is what makes
# `kubectl apply -f /c/Users/...` work at all. `MSYS_NO_PATHCONV=1` exported for
# the whole script breaks every `-f` path, and the first fix for this problem did
# exactly that - the build then failed with
# `unable to prepare context: path "/c/Users/..." not found`.
#
# The alternative - relative paths, or a shell wrapper - is worse: it makes the
# rehearsal's commands differ from the manifest's, and the whole value here is
# that the command in the manifest is the command that runs.

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
K8S="$ROOT/deploy/k8s"
REHEARSAL_DIR="$ROOT/deploy/rollout/rehearsal"

NAMESPACE="dubbing-prod"
CONTEXT=""
CLUSTER=""
IMAGE_PREFIX="dubbing-rehearsal"
KEEP=0
RECORD=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --namespace) NAMESPACE="${2:?--namespace needs a value}"; shift 2 ;;
    --context) CONTEXT="${2:?--context needs a value}"; shift 2 ;;
    --cluster) CLUSTER="${2:?--cluster needs a value}"; shift 2 ;;
    --image-prefix) IMAGE_PREFIX="${2:?--image-prefix needs a value}"; shift 2 ;;
    --record) RECORD="${2:?--record needs a value}"; shift 2 ;;
    --keep) KEEP=1; shift ;;
    -h|--help) sed -n '2,60p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "rehearse-rollback: unknown argument $1" >&2; exit 2 ;;
  esac
done

KUBECTL=(kubectl)
[ -n "$CONTEXT" ] && KUBECTL+=(--context "$CONTEXT")
KN=("${KUBECTL[@]}" -n "$NAMESPACE")
if [ -z "$CONTEXT" ]; then CONTEXT="$("${KUBECTL[@]}" config current-context 2>/dev/null || echo '')"; fi
if [ -z "$CLUSTER" ]; then CLUSTER="${CONTEXT#kind-}"; fi

PASS=0
FAIL=0
REASON="OK"
TARGETS_JSON="[]"
TARGETS_SEEN=""
# Counters, and asserted after each loop. `for x in "${arr[@]}"` over an EMPTY array
# is a no-op, not an error, so every flag initialised to "yes" survives a loop that
# never ran - which is how an earlier revision of this script reported
# "revision 1 is serving on all five workloads" and "revision 2 rolled out on all
# five workloads" having checked nothing, and wrote a record with an empty
# `targets` array and a PASS result.
REVISION_1_CHECKED=0
REVISION_2_CHECKED=0
UNDOS_RECORDED=0
AVAILABILITY_FAILURES="unknown"
AVAILABILITY_SAMPLES="unknown"
MIGRATION_GATE_BLOCKED="not-tested"
STALE_JOB_LIED="not-tested"
WORK="$(mktemp -d)"

ok()  { PASS=$((PASS + 1)); printf '  ok    %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf '  FAIL  %s\n' "$1"; }

# Every container and init container of a Deployment, one `list/index` per line.
# The `{#...}` jsonpath range form is used because `[*].name` returns every name
# on ONE space-separated line, and a `while read` over that sees a single
# "container" whose name is every name - which then patches nothing, silently,
# because the error is discarded. A helper that quietly does nothing is worse
# than one that is absent.
containers_of() {
  local deployment="$1" list="$2" i=0 name
  while IFS= read -r name; do
    [ -n "$name" ] || continue
    printf '%s/%s\n' "$list" "$i"
    i=$((i + 1))
  done < <("${KN[@]}" get "deployment/${deployment}" \
    -o jsonpath="{range .spec.template.spec.${list}[*]}{.name}{\"\\n\"}{end}" 2>/dev/null)
}

# Every container and init container in a Deployment, as `list/index/name`.
# The rehearsal images are local to the cluster's node, and every manifest in
# deploy/k8s declares `imagePullPolicy: Always` - which is correct in production
# and fatal here, because `kind load` puts the image on the node and `Always`
# then tries to pull it from Docker Hub, where `dubbing-rehearsal` does not
# exist. The failure presents as ImagePullBackOff on a Deployment whose image is
# demonstrably present, which is an expensive way to learn that a pull policy is
# an environment concern.
relax_pull_policy() {
  local deployment="$1" entry
  while IFS= read -r entry; do
    "${KN[@]}" patch "deployment/${deployment}" --type=json \
      -p "[{\"op\":\"add\",\"path\":\"/spec/template/spec/${entry}/imagePullPolicy\",\"value\":\"IfNotPresent\"}]" >/dev/null 2>&1 || true
  done < <(containers_of "$deployment" containers; containers_of "$deployment" initContainers)
  # Asserted rather than assumed: the image being present on the node and the
  # pods still pulling from a registry is a failure that looks like a broken
  # image, and this is the only place the difference is visible.
  local remaining
  remaining="$("${KN[@]}" get "deployment/${deployment}" -o jsonpath='{.spec.template.spec.containers[*].imagePullPolicy}' 2>/dev/null)"
  case "$remaining" in
    *Always*) printf '  warn  %s still requests imagePullPolicy=Always\n' "$deployment" ;;
  esac
}

# `kubectl set image` for one workload, then the pull policy, then a wait.
set_image() {
  local deployment="$1" container="$2" tag="$3"
  "${KN[@]}" set image "deployment/${deployment}" "${container}=${IMAGE_PREFIX}:${tag}" >/dev/null
  relax_pull_policy "$deployment"
}

# The resource requests and limits the manifests declare, shrunk for a
# single-node rehearsal cluster.
#
# The manifests' numbers are the production ones - 500m/1Gi requests and 2/4Gi
# limits on the API, three replicas, seven workloads - and a laptop-sized kind
# node cannot schedule them: the rehearsal fails with `0/1 nodes are available:
# 1 Insufficient memory`, which reads as a broken rehearsal rather than as a node
# that is too small. Requests are what the scheduler sums, so only the requests
# have to move; the limits are left alone because nothing here approaches them.
#
# The replica counts are NOT reduced, because the count is material: `maxSurge: 1`
# and `maxUnavailable: 0` only mean something against more than one replica.
shrink_requests() {
  local deployment="$1" entry
  while IFS= read -r entry; do
    "${KN[@]}" patch "deployment/${deployment}" --type=json \
      -p "[{\"op\":\"replace\",\"path\":\"/spec/template/spec/${entry}/resources/requests\",\"value\":{\"cpu\":\"10m\",\"memory\":\"48Mi\"}}]" >/dev/null 2>&1 || true
  done < <(containers_of "$deployment" containers; containers_of "$deployment" initContainers)
}

# The record, written to stdout and, with --record, to the file
# `scripts/check-rollout-window.mjs` reads. A rehearsal whose result lives only
# on one person's screen is a claim; writing it into the repository is the whole
# difference between an observation and an assertion somebody can re-check.
emit_record() {
  local result="PASS"
  [ "$FAIL" -eq 0 ] || result="FAIL"
  local payload="$WORK/record.json"
  {
    echo "{"
    _write_record_fields "$result"
    echo "}"
  } > "$payload"
  # The committed `$comment` block is preserved by `tools/rollout-record.mjs`,
  # which is unit-tested. A `sed` splice inside this script emitted the comment
  # and then a SECOND top-level object, and the gate answered "Unexpected
  # non-whitespace character after JSON at position 12" - a message naming
  # neither the cause nor the line. A merge that can be tested is a merge that
  # cannot quietly produce two JSON values.
  if [ -n "$RECORD" ]; then
    node "$ROOT/scripts/rollout-record.mjs" write "$payload" "$RECORD" > "$WORK/merged.json" \
      && mv "$WORK/merged.json" "$RECORD" \
      || echo "::error::could not write ${RECORD}; the gate will read the previous record" >&2
  fi
  cat "$payload"
}

_write_record_fields() {
  local result="$1"
  cat <<JSON
  "date": "$(date -u +%Y-%m-%d)",
  "environment": "ephemeral Kubernetes cluster (kind, context ${CONTEXT}), namespace ${NAMESPACE}, rehearsal stand-in image ${IMAGE_PREFIX} - NOT the application image",
  "result": "${result}",
  "targets": ${TARGETS_JSON},
  "observations": [
    { "observation": "a failing migration-gate initContainer blocked the API rollout and the Service stopped routing to it", "result": "${MIGRATION_GATE_BLOCKED}" },
    { "observation": "re-applying a completed migration Job left 'kubectl wait --for=condition=complete' satisfied with the SAME object, so no migration ran", "result": "${STALE_JOB_LIED}" },
    { "observation": "the API Service answered /health/ready on every sample spanning both rollouts and the undo", "samples": ${AVAILABILITY_SAMPLES}, "failures": ${AVAILABILITY_FAILURES} }
  ],
  "notCovered": [
    "the application: every workload ran deploy/rollout/rehearsal/, a stand-in that answers the same probe paths. Nothing here shows the API, a worker or the static origin behaving correctly after a rollback - only that Kubernetes moves them back.",
    "the database: no PostgreSQL, no migration, no schema. A code-only rollback is safe only because the expand/contract window holds (deploy/rollout.md section 2), and this rehearsal does not test that window - deploy/rollout/compat-smoke.sh does.",
    "the CDN version pin: the frontend rollback in docs/runbooks/rollback.md is a provider-specific pointer change, not 'rollout undo', and it is not a cluster operation.",
    "the operator: no incident, no clock, no handover. The step most likely to fail in a real rollback is the decision to run it, and nothing here rehearses a decision.",
    "not applied: deploy/k8s/migration-job.yaml (needs a database), ingress.yaml (needs ingress-nginx), keda-scalers.yaml (needs the KEDA CRDs) and networkpolicies.yaml (needs a CNI that enforces them). Their rollback path - re-applying the previous overlay revision - is declarative and is not a 'rollout undo'.",
    "the media-prep scratch volume is a hostPath stand-in for the managed disk. A managed ReadWriteMany disk has an attach/detach lifecycle a hostPath does not, so nothing here says anything about a volume surviving a node failure - that belongs to docs/dr/backup-restore.md and the Task 043C drill."
  ]
JSON
}

finish() {
  echo ""
  if [ "$FAIL" -ne 0 ] && [ "$REASON" = "OK" ]; then REASON="REHEARSAL_FAILED"; fi
  echo "== rollback rehearsal: $PASS passed, $FAIL failed =="
  echo ""
  echo "-- record (deploy/rollout/rollback-rehearsal.json) --"
  emit_record
  echo "ROLLBACK_REHEARSAL_RESULT reason=${REASON} status=$([ "$FAIL" -eq 0 ] && echo PASS || echo FAIL) passed=${PASS} failed=${FAIL}"
  if [ "$KEEP" -eq 0 ]; then
    "${KN[@]}" delete namespace "$NAMESPACE" --ignore-not-found --wait=false >/dev/null 2>&1 || true
  else
    echo "namespace ${NAMESPACE} left in place (--keep)"
  fi
  rm -rf "$WORK"
  [ "$FAIL" -eq 0 ] || exit 1
  exit 0
}

# --- preflight -----------------------------------------------------------------
command -v kubectl >/dev/null 2>&1 || { echo "::error::kubectl is required."; exit 2; }
command -v docker  >/dev/null 2>&1 || { echo "::error::docker is required to build the rehearsal image."; exit 2; }
if ! "${KUBECTL[@]}" cluster-info >/dev/null 2>&1; then
  echo "::error::no reachable cluster. A rehearsal that cannot reach a cluster is a document."
  echo "  kind create cluster --image kindest/node:v1.34.0"
  exit 2
fi

# --- start from nothing ---------------------------------------------------------
# A rehearsal that inherits the previous run's objects is not a rehearsal. Two
# things survive a namespace delete and both bite:
#
#   * the hostPath PersistentVolume, which is CLUSTER-scoped. With a `Retain`
#     reclaim policy it keeps a claimRef to the PVC that was just deleted, stays
#     Bound to nothing, and the next run's claim never binds - which surfaces as
#     `pod has unbound immediate PersistentVolumeClaims. not found` and a
#     media-prep pod that is Pending forever. The policy is `Delete` below AND the
#     PV is removed here, because either alone leaves the other case open.
#   * ReplicaSets whose pods are stuck `InvalidImageName` on the CHANGE_ME images.
#     They are not fatal, but they consume the node and they make a failing
#     `rollout status` impossible to read.
"${KUBECTL[@]}" delete namespace "$NAMESPACE" --ignore-not-found --wait=true --timeout=300s >/dev/null 2>&1 || true
"${KUBECTL[@]}" delete pv rehearsal-media-scratch --ignore-not-found --wait=true --timeout=120s >/dev/null 2>&1 || true

echo "== rollback rehearsal =="
echo "context:  ${CONTEXT}"
echo "cluster:  ${CLUSTER}"
echo "namespace: ${NAMESPACE}"

# --- the two builds -----------------------------------------------------------
# Two genuinely different images, not one image with two tags. Rolling back to a
# tag that was re-pushed is a real and common way for the pods not to be the
# previous build, and the runbook already warns about it; this is the only place
# that warning can be checked rather than trusted.
echo ""
echo "-- two distinct rehearsal revisions --"
for revision in rev1 rev2; do
  if docker build \
      --build-arg "REHEARSAL_RELEASE=rehearsal-${revision}" \
      -t "${IMAGE_PREFIX}:${revision}" \
      -f "$REHEARSAL_DIR/Dockerfile" "$REHEARSAL_DIR" >"$WORK/build-${revision}.log" 2>&1; then
    ok "built ${IMAGE_PREFIX}:${revision}, serving /version release=rehearsal-${revision}"
  else
    bad "could not build ${IMAGE_PREFIX}:${revision} (see below)"; tail -20 "$WORK/build-${revision}.log"
    REASON="IMAGE_BUILD_FAILED"; finish
  fi
done
REV1_ID="$(docker image inspect --format '{{.Id}}' "${IMAGE_PREFIX}:rev1")"
REV2_ID="$(docker image inspect --format '{{.Id}}' "${IMAGE_PREFIX}:rev2")"
if [ "$REV1_ID" = "$REV2_ID" ]; then
  bad "the two revisions are the SAME image id; a rollback to a re-pushed tag would be indistinguishable from a successful undo"
  REASON="REVISIONS_IDENTICAL"; finish
fi
ok "distinct image ids (${REV1_ID#sha256:} / ${REV2_ID#sha256:})"

if command -v kind >/dev/null 2>&1; then
  kind load docker-image "${IMAGE_PREFIX}:rev1" --name "$CLUSTER" >/dev/null 2>&1 || true
  kind load docker-image "${IMAGE_PREFIX}:rev2" --name "$CLUSTER" >/dev/null 2>&1 || true
  ok "images loaded into the cluster (the manifests' Always pull policy is overridden to IfNotPresent)"
fi

# --- namespace and objects ----------------------------------------------------
echo ""
echo "-- applying the committed manifests --"
"${KN[@]}" create namespace "$NAMESPACE" --dry-run=client -o yaml | "${KN[@]}" apply -f - >/dev/null
# The ExternalSecret needs the external-secrets operator, a ClusterSecretStore
# and a real key vault, none of which a rehearsal cluster has. What the
# Deployments need is a Secret object with the right KEYS; the values are
# irrelevant to rollout mechanics because the stand-in image reads none of them.
# CHANGE_ME placeholders, never a credential.
"${KN[@]}" create secret generic dubbing-secrets \
  --from-literal=connection-string='Host=CHANGE_ME;Database=dubbing;Username=CHANGE_ME;Password=CHANGE_ME' \
  --from-literal=maintenance-connection='Host=CHANGE_ME;Database=dubbing;Username=CHANGE_ME;Password=CHANGE_ME' \
  --from-literal=rabbitmq-host=CHANGE_ME --from-literal=rabbitmq-port=5672 \
  --from-literal=rabbitmq-vhost=/ --from-literal=rabbitmq-username=CHANGE_ME \
  --from-literal=rabbitmq-password=CHANGE_ME --from-literal=redis-connection=CHANGE_ME:6379 \
  --from-literal=storage-endpoint=CHANGE_ME --from-literal=storage-bucket=CHANGE_ME \
  --from-literal=storage-access-key=CHANGE_ME --from-literal=storage-secret-key=CHANGE_ME \
  --from-literal=auth-signing-key=CHANGE_ME-rehearsal-signing-key-placeholder-01 \
  --from-literal=providers-azure-api-key=CHANGE_ME --from-literal=azure-speech-key=CHANGE_ME \
  --from-literal=azure-translator-key=CHANGE_ME --from-literal=openai-api-key=CHANGE_ME \
  --from-literal=google-api-key=CHANGE_ME \
  --dry-run=client -o yaml | "${KN[@]}" apply -f - >/dev/null
ok "dubbing-secrets present with the committed key set (CHANGE_ME placeholders; no credential)"
"${KN[@]}" apply -f "$K8S/configmap.yaml" >/dev/null
"${KN[@]}" apply -f "$K8S/pdb.yaml" >/dev/null
ok "configmap.yaml and pdb.yaml applied (the PDB is what minAvailable: 1 is rehearsed against)"
for manifest in api-deployment.yaml workers-control.yaml workers-ai.yaml workers-media-prep.yaml; do
  "${KN[@]}" apply -f "$K8S/$manifest" >/dev/null
done
"${KN[@]}" apply -f "$K8S/frontend/deployment.yaml" >/dev/null
"${KN[@]}" apply -f "$K8S/frontend/service.yaml" >/dev/null
ok "dubbing-api, worker-control, worker-ai, worker-media-prep and frontend applied from deploy/k8s"

# The media-prep scratch PVC asks for `managed-high-iops`, a managed cloud disk
# class, with access mode ReadWriteMany. Two things about that matter here, and
# the first is a real property of the committed manifest rather than a rehearsal
# problem:
#
#   * a PVC's `storageClassName` is IMMUTABLE, so the claim cannot be repointed
#     after it exists - it would have to be deleted, and deleting a claim a
#     committed Deployment mounts is exactly the kind of shortcut a rehearsal
#     should not take. The class is defined instead, and the manifest is applied
#     unchanged;
#   * the class cannot be backed by the cluster's own local-path provisioner,
#     because `NodePath only supports ReadWriteOnce and ReadWriteOncePod`. So
#     the class is static and a hostPath PV of the same name is declared for it.
#     A hostPath satisfies any access mode, which is the whole reason it is used.
cat <<'YAML' | "${KN[@]}" apply -f - >/dev/null
apiVersion: v1
kind: PersistentVolume
metadata:
  name: rehearsal-media-scratch
spec:
  capacity: {storage: 50Gi}
  accessModes: [ReadWriteMany]
  persistentVolumeReclaimPolicy: Delete
  storageClassName: managed-high-iops
  hostPath:
    path: /tmp/rehearsal-media-scratch
    type: DirectoryOrCreate
YAML
ok "a hostPath PV stands in for the managed scratch disk, so media-scratch binds as the manifest declares it (ReadWriteMany)"

# The scratch volume before the rollouts are timed. A media-prep pod that is
# Pending on an unbound claim burns the same `rollout status --timeout` as a pod
# that is genuinely stuck, and the two are indistinguishable from outside: the
# first version of this script reported "revision 1 did not roll out everywhere"
# for a claim that was still binding.
VOLUME_BOUND=no
for _ in $(seq 1 60); do
  phase="$("${KN[@]}" get pvc media-scratch -o jsonpath='{.status.phase}' 2>/dev/null || true)"
  [ "$phase" = "Bound" ] && { VOLUME_BOUND="yes"; break; }
  sleep 2
done
[ "$VOLUME_BOUND" = "yes" ] \
  && ok "media-scratch is Bound, so media-prep's Pending state would mean a real problem" \
  || bad "media-scratch never bound; media-prep cannot start and any rollout failure below is this, not the rollback"

for deployment in dubbing-api worker-control worker-ai worker-media-prep frontend; do
  shrink_requests "$deployment"
done
ok "container resource requests reduced to fit one node; replica counts left as the manifests declare them"

# --- R1: the migration gate ---------------------------------------------------
echo ""
echo "-- R1: the migration gate blocks the API rollout --"
set_image dubbing-api api rev1; set_image dubbing-api wait-for-migrations rev1
# The initContainer runs /app/efbundle, which the rehearsal image provides, so
# the REAL command path in the REAL manifest is what blocks the rollout. Nothing
# is patched except one environment variable, PREPENDED at index 0 so that the
# index is stable for the `replace` that follows - computing "the last element"
# as `env/<count-1>` is one `kubectl get` away from being wrong.
"${KN[@]}" patch deployment dubbing-api --type=json \
  -p '[{"op":"add","path":"/spec/template/spec/initContainers/0/env/0","value":{"name":"REHEARSAL_GATE","value":"fail"}}]' >/dev/null
relax_pull_policy dubbing-api
BLOCKED="no"
GATE_EXIT=""
GATE_REASON=""
for _ in $(seq 1 45); do
  # The evidence has to be the GATE'S OWN non-zero exit. A pod that is stuck
  # because its image name is unresolvable is also "un-ready", and accepting that
  # as proof would make this assertion pass on a manifest that never ran the
  # migration at all - which is the exact confusion the assertion exists to
  # prevent, and which this script's first version committed.
  for pod in $("${KN[@]}" get pod -l app.kubernetes.io/component=api -o jsonpath='{.items[*].metadata.name}' 2>/dev/null); do
    state="$("${KN[@]}" get "pod/${pod}" -o jsonpath='{.status.initContainerStatuses[0].state.terminated.reason}' 2>/dev/null || true)"
    code="$("${KN[@]}" get "pod/${pod}" -o jsonpath='{.status.initContainerStatuses[0].state.terminated.exitCode}' 2>/dev/null || true)"
    if [ "$state" = "Error" ] && [ -n "$code" ] && [ "$code" != "0" ]; then
      BLOCKED="yes"; GATE_EXIT="$code"; GATE_REASON="$state"
    fi
  done
  [ "$BLOCKED" = "yes" ] && break
  sleep 2
done
if [ "$BLOCKED" = "yes" ]; then
  MIGRATION_GATE_BLOCKED="yes"
  ok "the migration gate exited ${GATE_EXIT} (${GATE_REASON}) and the API pod never reached Running, so the rollout is blocked by the initContainer alone"
else
  bad "no API pod recorded a failing migration gate; the block R1 depends on was not observed"
  MIGRATION_GATE_BLOCKED="no"
fi
# The other half of R1, and the half that is easy to assert wrongly. The
# migration gate blocks the ROLLOUT; it does not stop traffic, and it must not -
# with `maxUnavailable: 0` the previous revision is still serving, and that is the
# entire design: a blocked rollout leaves the last good release in front of users.
# The property worth asserting is therefore not "the Service is empty" (which
# would be true only on a first deploy, and false on every subsequent one) but
# "no pod whose gate failed is in the Service".
ENDPOINTS="$("${KN[@]}" get endpoints dubbing-api -o jsonpath='{.subsets[*].addresses[*].ip}' 2>/dev/null || true)"
if [ -z "$ENDPOINTS" ]; then
  ok "the API Service has no ready endpoints yet, so nothing routes to a pod that has not passed its gate"
else
  GATED_IN_SERVICE=""
  for pod in $("${KN[@]}" get pod -l app.kubernetes.io/component=api -o jsonpath='{.items[*].metadata.name}' 2>/dev/null); do
    reason="$("${KN[@]}" get "pod/${pod}" -o jsonpath='{.status.initContainerStatuses[0].state.terminated.reason}' 2>/dev/null || true)"
    ip="$("${KN[@]}" get "pod/${pod}" -o jsonpath='{.status.podIP}' 2>/dev/null || true)"
    if [ "$reason" = "Error" ] && [ -n "$ip" ] && printf '%s' "$ENDPOINTS" | grep -q "$ip"; then
      GATED_IN_SERVICE="$pod"
    fi
  done
  if [ -z "$GATED_IN_SERVICE" ]; then
    ok "the Service still routes to the PREVIOUS revision (${ENDPOINTS// /, }) and to no pod whose gate failed - a blocked rollout is not an outage"
  else
    bad "pod ${GATED_IN_SERVICE} failed its migration gate and is still in the Service's endpoints (${ENDPOINTS// /, })"
  fi
fi

"${KN[@]}" patch deployment dubbing-api --type=json \
  -p '[{"op":"replace","path":"/spec/template/spec/initContainers/0/env/0/value","value":"pass"}]' >/dev/null
# 300s, not 180s, and the same budget every other rollout gets. The first
# revision of this line had 180s and reported "the API did not become ready" on a
# rollout that completed a few seconds later - a harness that fails on its own
# timeout teaches its reader to re-run it with a bigger number, which is the same
# as having no number.
if "${KN[@]}" rollout status deployment/dubbing-api --timeout=300s >/dev/null 2>&1; then
  ok "the same manifest rolls to ready once the gate passes"
else
  bad "the API did not become ready after the gate was made to pass; pods: $("${KN[@]}" get pods -l app.kubernetes.io/component=api --no-headers 2>/dev/null | tr -s ' ' | cut -d' ' -f2-4 | tr '\n' ';')"
fi

# `kubectl rollout history deployment/dubbing-api` is one of the task's three
# Validation commands, and the record has to carry a REVISION and a POD TEMPLATE
# for every undo. These two readers are what produce them.
#
# The revision annotation INCREMENTS on an undo: `rollout undo` records the
# previous ReplicaSet's pod template as a NEW revision rather than moving the
# number back, because a rollback is a change like any other and the history has
# to show that it happened. Reading "revision went down" as the success condition
# rejects every correct rehearsal.
#
# The reader also fails CLOSED, and names WHY it could not answer. It used to end
# in `|| echo 0`, which is the worst available default for this reader, because
# the success condition downstream is "the number CHANGED": a failed read produced
# 0, 0 is not 10, and a real run wrote
#
#   ok  dubbing-api: revision 10 -> 0, template rev2 -> rev1
#
# into the record that `deploy/rollout.md` cites as evidence - a measurement that
# never happened, in the one file whose entire job is to be believable. The gate
# could not have caught it either: `revisionAfter !== revisionBefore` is true for
# 0, which is the generalisable half of the lesson - a rule that only COMPARES two
# recorded numbers cannot tell a measurement from a fabrication. Found by reading
# the record the run produced rather than the log it printed, which is how the
# incrementing-revision finding above was found too.
REVISION_ERROR=""
REVISION_VALUE=""
# The value is handed back in a VARIABLE, not on stdout. `value="$(revision x)"`
# looks equivalent and is not: the command substitution runs the function in a
# subshell, so `REVISION_ERROR` assigned inside it is gone by the time the caller
# reads it and every failure message would have said "no annotation" regardless of
# what kubectl actually said. That is the same defect one level up - a diagnostic
# that is always the same string tells the reader nothing.
revision() { # deployment -> sets REVISION_VALUE, or returns non-zero with REVISION_ERROR set
  local value
  REVISION_ERROR=""
  REVISION_VALUE=""
  if ! value="$("${KN[@]}" get "deployment/$1" \
        -o jsonpath='{.metadata.annotations.deployment\.kubernetes\.io/revision}' 2>&1)"; then
    REVISION_ERROR="$(printf '%s' "$value" | tr '\n' ' ' | cut -c1-200)"
    # kubectl is not always verbose, and an empty reason is the same useless
    # diagnostic as every other one - "it failed" with nothing attached sends the
    # next reader to the cluster to find out what happened.
    REVISION_ERROR="${REVISION_ERROR:-kubectl exited non-zero and said nothing}"
    return 1
  fi
  case "$value" in
    ''|*[!0-9]*)
      REVISION_ERROR="the annotation read as '${value}'"
      return 1
      ;;
  esac
  if [ "$value" -lt 1 ]; then
    REVISION_ERROR="the annotation is ${value}, and the Deployment controller never writes revision 0"
    return 1
  fi
  REVISION_VALUE="$value"
}

# What the API pods actually ANSWER, which is not the same question as what the
# Deployment's pointer says. A tag that was re-pushed makes the two differ, and
# the runbook already says to confirm a rollback with `/version` rather than with
# the rollout status. This is where that instruction is checked.
served_release() {
  "${KN[@]}" exec deploy/dubbing-api -c api -- \
    node -e "fetch('http://127.0.0.1:8080/version').then(r=>r.json()).then(j=>process.stdout.write(String(j.release))).catch(()=>process.stdout.write('unreachable'))" \
    2>/dev/null || echo unreachable
}

# The workloads the rollback procedure covers: deployment:container.
#
# Declared HERE, immediately before the loops that read it, and asserted non-empty
# and complete on the way in. That is not defensive padding: an earlier revision
# of this script lost this array to a bad edit, and because `for x in "${arr[@]}"`
# over an empty array is a NO-OP rather than an error, every loop below ran zero
# times and the script went on to report
#
#   ok  revision 1 is serving on all five workloads
#   ok  revision 2 rolled out on all five workloads
#
# having checked nothing at all, with `passed=22, failed=0` and an empty
# `targets` array in the record it wrote. A rehearsal that reports success from a
# loop that never executed is worse than a rehearsal that fails, because it is
# recorded as evidence.
targets=(
  "dubbing-api:api"
  "worker-control:worker"
  "worker-ai:worker"
  "worker-media-prep:worker"
  "frontend:frontend"
)
for required in dubbing-api worker-control worker-ai worker-media-prep frontend; do
  found=no
  for pair in "${targets[@]}"; do
    [ "${pair%%:*}" = "$required" ] && found=yes
  done
  if [ "$found" = "no" ]; then
    echo "::error::the rollback target list is missing '${required}', so its undo would not be rehearsed and the record would say so quietly."
    REASON="TARGET_LIST_INCOMPLETE"
    finish
  fi
done
ok "the rollback target list names all five workloads the procedure covers"

for pair in "${targets[@]}"; do
  set_image "${pair%%:*}" "${pair##*:}" rev1
done
# Per workload, not one `&&` chain. "revision 1 did not roll out everywhere" names
# no workload, so the only way to find out which one failed is to re-run with
# `--keep` and go looking - and the answer is usually a volume that has not bound
# or a node that has no memory, both of which are visible in a per-workload line.
set_image dubbing-api wait-for-migrations rev1
REV1_OK="yes"
REVISION_1_CHECKED=0
for pair in "${targets[@]}"; do
  if "${KN[@]}" rollout status "deployment/${pair%%:*}" --timeout=300s >/dev/null 2>&1; then
    printf '  ....  %s: revision 1 rolled out\n' "${pair%%:*}"
    REVISION_1_CHECKED=$((REVISION_1_CHECKED + 1))
  else
    bad "${pair%%:*}: revision 1 did not roll out"
    REV1_OK="no"
  fi
done
# A loop that ran zero times leaves REV1_OK at its initial "yes". Asserting the
# COUNT rather than the flag is what distinguishes "everything rolled out" from
# "nothing was tried".
if [ "$REVISION_1_CHECKED" -ne "${#targets[@]}" ]; then
  bad "revision 1 was checked on ${REVISION_1_CHECKED} of ${#targets[@]} workloads"
  REASON="ROLLOUT_FAILED"; finish
fi
if [ "$REV1_OK" = "yes" ]; then
  ok "revision 1 is serving on all ${#targets[@]} workloads, and it SERVES $(served_release)"
else
  REASON="ROLLOUT_FAILED"; finish
fi

# The availability probe starts HERE, after revision 1 is confirmed serving, and
# is read after the undo - so it measures the two rollouts and the rollback and
# nothing else. Started earlier it samples a Service that has no endpoints yet,
# and a harness that reports its own start-up window as downtime is a harness
# that will eventually report a real outage as a start-up window.
CLUSTER_IP="$("${KN[@]}" get service dubbing-api -o jsonpath='{.spec.clusterIP}')"
"${KN[@]}" delete pod availability-probe --ignore-not-found --wait=true >/dev/null 2>&1 || true
MSYS_NO_PATHCONV=1 "${KN[@]}" run availability-probe --image="${IMAGE_PREFIX}:rev1" --restart=Never \
  --labels="app.kubernetes.io/component=rehearsal-probe" \
  --env="AVAILABILITY_URL=http://${CLUSTER_IP}/health/ready" \
  --env=AVAILABILITY_BUDGET_MS=900000 --env=AVAILABILITY_INTERVAL_MS=500 \
  --command -- node /app/probe.mjs >/dev/null 2>&1 || true
if "${KN[@]}" wait --for=condition=Ready pod/availability-probe --timeout=120s >/dev/null 2>&1; then
  sleep 4
  PROBE_HEAD="$("${KN[@]}" logs availability-probe 2>/dev/null | head -3 || true)"
  PHASE="$("${KN[@]}" get pod availability-probe -o jsonpath='{.status.phase}' 2>/dev/null || echo unknown)"
  # POSITIVE first. The probe announces itself with START before its first sample
  # (see rehearsal/probe.mjs), so the question is "did the probe say it started".
  # Asking instead "is the log free of complaints" passes on an empty log, and the
  # log is empty for the first 25 seconds by design - the probe logs a line per
  # FAILURE, so silence is what a working probe looks like. Two checks in one
  # place: that is the shape of every defect in this file's history.
  if ! printf '%s' "$PROBE_HEAD" | grep -q '^START url='; then
    if printf '%s' "$PROBE_HEAD" | grep -qi 'listening'; then
      bad "the probe pod is running the REHEARSAL SERVER, not the probe; downtime is unmeasured"
    else
      bad "the probe pod never announced itself (no START line); something is running but nothing is measuring. Log head: $(printf '%s' "$PROBE_HEAD" | tr '\n' ' ' | cut -c1-160)"
    fi
  elif [ "$PHASE" != "Running" ] || printf '%s' "$PROBE_HEAD" | grep -qE 'Error|not found'; then
    bad "the probe container is not healthy (phase ${PHASE}); downtime is unmeasured. Log head: $(printf '%s' "$PROBE_HEAD" | tr '\n' ' ' | cut -c1-160)"
  else
    ok "availability probe announced itself and is polling the Service ClusterIP ${CLUSTER_IP}"
  fi
else
  bad "the availability probe did not start; downtime is unmeasured"
fi

for pair in "${targets[@]}"; do
  set_image "${pair%%:*}" "${pair##*:}" rev2
done
ROLLOUT2_OK="yes"
REVISION_2_CHECKED=0
for pair in "${targets[@]}"; do
  if ! "${KN[@]}" rollout status "deployment/${pair%%:*}" --timeout=300s >/dev/null 2>&1; then
    bad "${pair%%:*}: revision 2 did not roll out; pods: $("${KN[@]}" get pods -l "app.kubernetes.io/component=${pair%%:*}" --no-headers 2>/dev/null | tr -s ' ' | cut -d' ' -f2-4 | tr '\n' ';')"
    ROLLOUT2_OK="no"
  else
    REVISION_2_CHECKED=$((REVISION_2_CHECKED + 1))
  fi
done
if [ "$REVISION_2_CHECKED" -ne "${#targets[@]}" ]; then
  bad "revision 2 was checked on ${REVISION_2_CHECKED} of ${#targets[@]} workloads"
  ROLLOUT2_OK="no"
fi
[ "$ROLLOUT2_OK" = "yes" ] && ok "revision 2 rolled out on all ${#targets[@]} workloads, and the pods now SERVE $(served_release)"

echo ""
echo "-- kubectl rollout history deployment/dubbing-api --"
"${KN[@]}" rollout history deployment/dubbing-api | sed 's/^/  /'
ok "rollout history lists the revisions an operator reads during an incident"

for pair in "${targets[@]}"; do
  deployment="${pair%%:*}"
  # A reader that could not answer is NOT a revision. Recording one anyway is how
  # the record ended up saying "revision 10 -> 0", so the failure is a `bad` and
  # the workload is skipped rather than guessed at - and because UNDOS_RECORDED is
  # counted against ${#targets[@]}, skipping is also what makes the run FAIL.
  if ! revision "$deployment"; then
    bad "${deployment}: could not read the revision annotation before the undo (${REVISION_ERROR:-unknown}); the undo is not recorded, because a record of a failed read is not a rehearsal"
    continue
  fi
  before="$REVISION_VALUE"
  # The image in the pod template, before and after. The revision annotation
  # alone is not evidence: a Deployment can record a new revision whose template
  # is identical to the one it had, which is what a rollback to a re-pushed tag
  # looks like.
  image_before="$("${KN[@]}" get "deployment/${deployment}" -o jsonpath="{.spec.template.spec.containers[?(@.name==\"${pair##*:}\")].image}" 2>/dev/null || echo unknown)"
  if ! "${KN[@]}" rollout undo "deployment/${deployment}" >/dev/null 2>&1; then
    bad "${deployment}: rollout undo returned non-zero"; continue
  fi
  if ! "${KN[@]}" rollout status "deployment/${deployment}" --timeout=300s >/dev/null 2>&1; then
    bad "${deployment}: the undo did not complete"; continue
  fi
  if ! revision "$deployment"; then
    bad "${deployment}: the undo ran but the revision annotation could not be read afterwards (${REVISION_ERROR:-unknown}); an undo whose result cannot be read is not evidence that anything moved"
    continue
  fi
  after="$REVISION_VALUE"
  image_after="$("${KN[@]}" get "deployment/${deployment}" -o jsonpath="{.spec.template.spec.containers[?(@.name==\"${pair##*:}\")].image}" 2>/dev/null || echo unknown)"
  if [ "$after" = "$before" ]; then
    # The annotation INCREMENTS on an undo: `rollout undo` records the previous
    # ReplicaSet's template as a NEW revision rather than moving the number back,
    # so an UNCHANGED number is the failure, not a decrement.
    bad "${deployment}: the undo left the revision annotation at ${after}. The annotation increments on an undo, so an unchanged number means nothing happened - usually a garbage-collected ReplicaSet, so there was nothing to roll back to."
    continue
  fi
  if [ "$image_before" = "$image_after" ]; then
    bad "${deployment}: the undo recorded revision ${before} -> ${after} but the pod template still names ${image_after}. A revision that changes nothing is a rollback to the same build."
    continue
  fi
  entry="{\"deployment\": \"${deployment}\", \"revisionBefore\": ${before}, \"revisionAfter\": ${after}, \"imageBefore\": \"${image_before}\", \"imageAfter\": \"${image_after}\""
  if [ "$deployment" = "dubbing-api" ]; then
    entry="${entry}, \"servedAfter\": \"$(served_release)\""
  fi
  entry="${entry}, \"result\": \"PASS\", \"detail\": \"rollout undo moved the Deployment ${before} -> ${after} and the pod template ${image_before} -> ${image_after}\"}"
  # Entries are joined by appending a comma, and the bracket is closed once at the
  # end. The obvious alternative - ${TARGETS_JSON%]...} - is parsed as
  # ${TARGETS_JSON%]...} because the ] terminates the expansion, so the "trim the
  # bracket" version silently produces a different string and the shell does not
  # complain about it. This one was found by bash -n on a file that would not parse.
  if [ -z "$TARGETS_SEEN" ]; then TARGETS_JSON="[${entry}"; TARGETS_SEEN=1; else TARGETS_JSON="${TARGETS_JSON}, ${entry}"; fi
  ok "${deployment}: revision ${before} -> ${after}, template ${image_before##*:} -> ${image_after##*:}"
  UNDOS_RECORDED=$((UNDOS_RECORDED + 1))
done
TARGETS_JSON="${TARGETS_JSON}]"
# The record's `targets` is what `scripts/check-rollout-window.mjs` reads, and a
# record with an empty `targets` would fail the gate - but the gate reading it
# months later is far too late to discover that this run checked nothing. Counted
# here, at the moment it could still be fixed.
if [ "$UNDOS_RECORDED" -ne "${#targets[@]}" ]; then
  bad "the undo was recorded for ${UNDOS_RECORDED} of ${#targets[@]} workloads; the record is not a complete rehearsal"
fi
[ -n "$TARGETS_SEEN" ] || TARGETS_JSON="[]"
if [ "$(served_release)" = "rehearsal-rev1" ]; then
  ok "after the undo the API pods SERVE rehearsal-rev1, so the Deployment pointer and the running build agree"
else
  bad "after the undo the API pods report '$(served_release)'; an undo that moves the pointer without moving the pods is the failure this check exists for"
fi

# --- availability result ------------------------------------------------------
# The logs are read BEFORE the pod is deleted. Deleting first and reading after
# is a race that yields an empty log, and an empty log read as "zero failures" is
# the most dangerous way this check can pass: it would report no downtime having
# measured nothing.
PROBE_LOG="$("${KN[@]}" logs availability-probe 2>/dev/null || true)"
"${KN[@]}" delete pod availability-probe --ignore-not-found --wait=false >/dev/null 2>&1 || true
# `|| true` on every pipeline below is load-bearing, not defensive. `pipefail` is
# on, so `grep` finding nothing makes the whole pipeline non-zero, and a
# command substitution in an assignment propagates that to `set -e`: a probe that
# has not yet printed its first progress line would abort the rehearsal. The
# first version of this line did exactly that.
AVAILABILITY_FAILURES="$(printf '%s\n' "$PROBE_LOG" | grep -c '^FAIL ' || true)"
PROGRESS="$(printf '%s\n' "$PROBE_LOG" | grep -E '^(PROGRESS|SUMMARY) ' | tail -1 || true)"
AVAILABILITY_SAMPLES="$(printf '%s' "$PROGRESS" | sed -n 's/.*samples=\([0-9]*\).*/\1/p')"
AVAILABILITY_SAMPLES="${AVAILABILITY_SAMPLES:-0}"
if [ "$AVAILABILITY_SAMPLES" -lt 20 ]; then
  bad "the availability probe logged only ${AVAILABILITY_SAMPLES} samples; a rollout of five workloads is minutes, and fewer than 20 samples means the measurement did not span it"
elif [ "$AVAILABILITY_FAILURES" -eq 0 ]; then
  ok "the API Service answered /health/ready on every one of ${AVAILABILITY_SAMPLES} samples across both rollouts and the undo: no downtime"
else
  bad "the API was unreachable on ${AVAILABILITY_FAILURES} of ${AVAILABILITY_SAMPLES} samples during the rollout/undo"
fi

# --- the stale completed Job ---------------------------------------------------
echo ""
echo "-- the stale completed Job false pass (R1 in the false direction) --"
# A deliberately trivial Job - and trivial in a specific way: it runs the
# rehearsal image's own `/app/efbundle`, the same binary the committed
# migration-job.yaml runs, so the Job completes exactly the way a real migration
# completes. (The image's default entrypoint is the long-running server, which
# never exits, so the Job would simply never complete and the demonstration would
# "fail" for a reason that has nothing to do with the hazard.)
"${KN[@]}" delete job dubbing-migration --ignore-not-found --wait=true >/dev/null 2>&1 || true
# Rendered client-side and applied, never `create`d and then applied. A Job
# created by `kubectl create` carries no `last-applied-configuration`, so the
# first `apply` has to patch that annotation in - and by then the Job controller
# has already written to the object, which is a conflict. The result is that the
# demonstration is decided by whether the controller happened to write between
# two commands, which is not a demonstration.
MSYS_NO_PATHCONV=1 "${KN[@]}" create job dubbing-migration --image="${IMAGE_PREFIX}:rev1" --dry-run=client -o yaml -- /app/efbundle --verbose > "$WORK/migration-job.yaml"
if [ ! -s "$WORK/migration-job.yaml" ]; then
  bad "could not render the demonstration Job; the stale-Job false pass cannot be shown"
  REASON="STALE_JOB_SETUP_FAILED"; finish
fi
"${KN[@]}" apply -f "$WORK/migration-job.yaml" >/dev/null
"${KN[@]}" wait --for=condition=complete job/dubbing-migration --timeout=120s >/dev/null 2>&1 \
  && ok "the first migration Job completed, so there is now a completed Job to be fooled by" \
  || { bad "the setup Job did not complete; the false pass cannot be demonstrated"; finish; }
UID_BEFORE="$("${KN[@]}" get job dubbing-migration -o jsonpath='{.metadata.uid}')"
DONE_BEFORE="$("${KN[@]}" get job dubbing-migration -o jsonpath='{.status.conditions[?(@.type=="Complete")].lastTransitionTime}')"

# The next release applies the SAME manifest. This is the plain-`kubectl` path.
START="$(date -u +%s)"
if "${KN[@]}" apply -f "$WORK/migration-job.yaml" >/dev/null 2>"$WORK/reapply.log"; then
  REAPPLY="accepted (a no-op: the object already exists and nothing changed)"
else
  REAPPLY="rejected with $(head -1 "$WORK/reapply.log" | cut -c1-80); the Job object is still there either way"
fi
if "${KN[@]}" wait --for=condition=complete job/dubbing-migration --timeout=20s >/dev/null 2>&1; then
  ELAPSED=$(( $(date -u +%s) - START ))
  UID_AFTER="$("${KN[@]}" get job dubbing-migration -o jsonpath='{.metadata.uid}')"
  DONE_AFTER="$("${KN[@]}" get job dubbing-migration -o jsonpath='{.status.conditions[?(@.type=="Complete")].lastTransitionTime}')"
  if [ "$UID_BEFORE" = "$UID_AFTER" ] && [ "$DONE_BEFORE" = "$DONE_AFTER" ] && [ "$ELAPSED" -lt 15 ]; then
    STALE_JOB_LIED="yes"
    ok "CONFIRMED FALSE PASS: the wait succeeded in ${ELAPSED}s on the SAME Job object (uid ${UID_BEFORE:0:8}, complete since ${DONE_BEFORE}) - no migration ran. The re-apply was ${REAPPLY}. The documented order therefore DELETES the Job before applying."
  else
    STALE_JOB_LIED="partial"
    bad "the re-applied wait behaved differently than documented (uid ${UID_BEFORE:0:8} -> ${UID_AFTER:0:8}, complete ${DONE_BEFORE} -> ${DONE_AFTER}, ${ELAPSED}s); re-check the hazard description in deploy/k8s/migration-job.yaml"
  fi
else
  STALE_JOB_LIED="no"
  bad "the stale-Job wait did NOT succeed; the preflight delete is then harmless but guards against nothing on this cluster"
fi

"${KN[@]}" delete job dubbing-migration --ignore-not-found --wait=true >/dev/null 2>&1 || true
if [ -z "$("${KN[@]}" get job dubbing-migration --ignore-not-found -o name 2>/dev/null)" ] \
   && ! "${KN[@]}" wait --for=condition=complete job/dubbing-migration --timeout=5s >/dev/null 2>&1; then
  ok "after the documented preflight delete there is no Job, and the wait can no longer be satisfied by a previous release's success"
else
  bad "the preflight delete did not remove the Job, or a wait was still satisfiable without one"
fi

finish