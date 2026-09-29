#!/usr/bin/env bash
# OpenAPI snapshot-match gate (Task 042, R1's "contract tests (OpenAPI snapshot
# match)").
#
# THE LOOP THIS CLOSES
# --------------------
# The repository already has two halves of an API contract gate and they do not
# meet:
#
#   * `tools/check-api-drift.mjs` proves the GENERATED CLIENT matches the
#     committed bundle (`src/DubbingPlatform.Api/OpenApi/openapi.v1.json`);
#   * `tools/check-api-contract.mjs` proves the committed bundle matches the API's
#     OWN emitted document - and 041A found that it does not, which is why that
#     gate is not yet wired into a suite.
#
# So a bundle can be internally consistent and still describe an API that does
# not exist. The generated client is then a faithful rendering of a fiction, and
# every consumer inherits the fiction.
#
# This runs the second half against a real API and fails on a FATAL divergence,
# reusing the same comparison and the same severity classification as
# `check-api-contract.mjs` rather than reimplementing it. `shape`-level
# divergences and under-described server operations are REPORTED and not fatal:
# the emitter is known to under-describe some `[FromBody]` schemas, and treating
# that as a failure would send the next engineer to "fix" correct client code.
#
# The snapshot is the committed bundle. There is no second file to drift.
#
# EXIT CODES
#   0  no fatal divergence
#   1  a fatal divergence (a request built from the bundle would be rejected)
#   2  the API could not be started or reached
#   3  the bundle could not be read
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
API_PORT="${SNAPSHOT_API_PORT:-58190}"
API_URL="http://127.0.0.1:${API_PORT}"
LOG="$(mktemp)"
WORK="$(mktemp -d)"

fail() {
  echo "::error title=$1::$2" >&2
  echo "CONTRACT_SNAPSHOT_RESULT reason=$1 status=FAIL" >&2
  exit "${3:-1}"
}

note() { printf 'contract-snapshot: %s\n' "$1"; }

cleanup() {
  [ -n "${API_PID:-}" ] && kill "$API_PID" >/dev/null 2>&1 || true
  rm -rf "$WORK" "$LOG"
}
trap cleanup EXIT

command -v dotnet >/dev/null 2>&1 || fail API_UNAVAILABLE "dotnet is not on PATH; the contract snapshot needs a running API to compare against." 2

# --- configuration the API needs to boot at all -------------------------------
# These are the same values the cross-layer rig uses, and the same two are
# load-bearing rather than decorative:
#   * `Auth__SigningKey` has no default and anything under 32 chars makes
#     `AuthService.CreateAccessToken` throw, so every authenticated request 500s
#     with no other signal.
#   * `Providers__DefaultProvider=mock` keeps the gate from reaching a real AI
#     provider, which is the one thing a "just start the API" step must never do.
export ASPNETCORE_URLS="http://127.0.0.1:${API_PORT}"
export DOTNET_ENVIRONMENT=Development
export ConnectionStrings__Default="Host=127.0.0.1;Port=15432;Database=dubbing;Username=dubbing;Password=CHANGE_ME;SSL Mode=Disable"
export Auth__SigningKey="CHANGE_ME-contract-snapshot-hs256-signing-key-placeholder"
export Auth__Audience="dubbing-api"
export Providers__DefaultProvider="mock"
export Transport__Provider="InMemory"
export Messaging__Transport="in-memory"
export CORS__AllowedOrigins__0="http://127.0.0.1:4173"

note "starting the API on ${API_URL}"
set +e
dotnet run --project src/DubbingPlatform.Api --no-launch-profile > "$LOG" 2>&1 &
API_PID=$!
set -e

ready=0
for _ in $(seq 1 90); do
  if ! kill -0 "$API_PID" 2>/dev/null; then
    fail API_START_FAILED "the API process exited before answering. Log tail:
$(tail -40 "$LOG")" 2
  fi
  if curl --silent --fail --max-time 3 "${API_URL}/health/live" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[ "$ready" -eq 1 ] || fail API_START_FAILED "the API did not answer /health/live within 90s. Log tail:
$(tail -40 "$LOG")" 2

note "API is live; comparing the committed bundle against the server's own document"
set +e
node "$ROOT/tools/check-api-contract.mjs" --url "$API_URL"
STATUS=$?
set -e

# `check-api-contract.mjs` already fails closed on an unreachable server and
# prints the reason; its exit code is the gate's.
if [ "$STATUS" -ne 0 ]; then
  fail API_CONTRACT_DIVERGENCE "the committed OpenAPI bundle cannot drive this API. Regenerate the bundle from the server document, then regenerate the client (see docs/api-contract.md)." 1
fi

echo "CONTRACT_SNAPSHOT_RESULT reason=OK status=PASS"
