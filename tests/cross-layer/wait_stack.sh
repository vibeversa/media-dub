#!/usr/bin/env bash
# Cross-layer stack readiness wait (Task 042).
#
# WHY THE E2E JOBS NEED THIS
# --------------------------
# `docker compose up -d` returns as soon as the containers are CREATED. The API
# then has to publish the EF migrations, seed, and answer a probe. A Playwright
# run that starts during that window spends its 180 s timeout on connection
# refused, and the failure it reports - "the harness did not come up" - points at
# the harness rather than at the timing.
#
# So the job waits for a real signal (`/health/live` answering 200) rather than
# for a container to exist, and on failure prints the container's own log. The
# log matters more than the timeout: a stack that never becomes healthy is almost
# always one service failing to start, and "connection refused" from a browser
# three layers away hides that completely.
#
# USAGE
#   tests/cross-layer/wait_stack.sh [--timeout 300] [--api http://127.0.0.1:58080]
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
COMPOSE="${RIG_COMPOSE:-$ROOT/tests/cross-layer/docker-compose.cross.yml}"

TIMEOUT=300
API_URL="http://127.0.0.1:${CROSS_LAYER_API_PORT:-58080}"
FRONTEND_URL="http://127.0.0.1:${CROSS_LAYER_FRONTEND_PORT:-54173}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --timeout) TIMEOUT="${2:?--timeout needs seconds}"; shift 2 ;;
    --api) API_URL="${2:?--api needs a url}"; shift 2 ;;
    --frontend) FRONTEND_URL="${2:?--frontend needs a url}"; shift 2 ;;
    *) echo "wait_stack.sh: unknown argument $1" >&2; exit 2 ;;
  esac
done

log() { printf 'wait_stack: %s\n' "$1"; }

compose() { docker compose -f "$COMPOSE" "$@"; }

# `docker compose ps -q <svc>` is empty when the container was never created, and
# prints the id when it exists - which is the distinction the whole wait rests
# on: "not created yet" means the build is still running, "created but not
# healthy" means something is wrong with it.
container_id() { compose ps -q "$1" 2>/dev/null || true; }

deadline=$(( $(date +%s) + TIMEOUT ))

log "waiting up to ${TIMEOUT}s for the API at ${API_URL}/health/live"
api_ready=0
frontend_ready=0

while [ "$(date +%s)" -lt "$deadline" ]; do
  if [ "$api_ready" -eq 0 ]; then
    api_id="$(container_id api)"
    if [ -n "$api_id" ]; then
      if curl --silent --fail --max-time 5 "${API_URL}/health/live" >/dev/null 2>&1; then
        api_ready=1
        log "api is live"
      fi
    fi
  fi
  if [ "$frontend_ready" -eq 0 ]; then
    frontend_id="$(container_id frontend)"
    if [ -n "$frontend_id" ]; then
      if curl --silent --fail --max-time 5 "${FRONTEND_URL}/" >/dev/null 2>&1; then
        frontend_ready=1
        log "frontend is serving"
      fi
    fi
  fi
  if [ "$api_ready" -eq 1 ] && [ "$frontend_ready" -eq 1 ]; then
    log "stack ready"
    echo "CI_GATE_RESULT reason=OK status=PASS"
    exit 0
  fi
  sleep 2
done

# Timed out. Report WHICH service, and print its log, because "the stack did not
# come up" on its own is not a diagnosis.
echo "::error title=STACK_UNAVAILABLE::the cross-layer rig did not become ready within ${TIMEOUT}s (api ready=${api_ready}, frontend ready=${frontend_ready})." >&2
echo "wait_stack: TIMED OUT after ${TIMEOUT}s (api ready=${api_ready}, frontend ready=${frontend_ready})" >&2
echo "--- compose ps ---" >&2
compose ps >&2 2>&1 || true
for service in api frontend postgres minio rabbitmq control ai; do
  id="$(container_id "$service")"
  if [ -n "$id" ]; then
    echo "--- last 40 lines: $service ---" >&2
    compose logs --no-color --tail 40 "$service" >&2 2>&1 || true
  else
    echo "--- $service: container not created ---" >&2
  fi
done
echo "CI_GATE_RESULT reason=STACK_UNAVAILABLE status=FAIL" >&2
exit 1
