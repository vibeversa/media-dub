#!/usr/bin/env bash
# Docker preflight (Task 042).
#
# WHY THIS IS A SCRIPT AND NOT A `docker info && ...` LINE
# -------------------------------------------------------
# The task's edge case: "Testcontainers unavailable on a runner -> job fails
# closed with INFRA_UNAVAILABLE, never silently skips integration tests."
#
# The integration tier is built on Testcontainers, and the repository's fixtures
# mark their container-backed tests `[SkippableFact]`. A runner with no Docker
# therefore produces a *green* `dotnet test` that executed nothing: the skip is
# reported, the exit code is 0, and the job passes having proved nothing at all.
# That is the failure this exists to make impossible.
#
# It checks three things, in the order that produces the most useful message:
#   1. the CLI exists,
#   2. the daemon answers and reports a server version,
#   3. the daemon can actually PULL, because a daemon that answers `info` and
#      cannot pull is the exact shape of a rate-limited or disk-full runner, and
#      it would otherwise be discovered 20 minutes into an integration run.
set -euo pipefail

REASON="INFRA_UNAVAILABLE"

fail() {
  echo "::error title=INFRA_UNAVAILABLE::$1"
  echo "INFRA_UNAVAILABLE: $1"
  echo "CI_GATE_RESULT reason=$REASON status=FAIL" >&2
  exit 1
}

pass() {
  echo "Docker preflight ok: $1"
  echo "CI_GATE_RESULT reason=OK status=PASS"
}

command -v docker >/dev/null 2>&1 || fail "the docker CLI is not on PATH. Integration tests spin their own PostgreSQL/RabbitMQ/Redis/MinIO through Testcontainers and cannot run without it; this job fails rather than skipping."

# `mktemp -d` rather than `$TMPDIR`: `set -u` plus a variable the platform does
# not define is a crash in the middle of a preflight, and a preflight that dies
# with "unbound variable" instead of naming the missing prerequisite has already
# spent the one message the reader gets.
WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

if ! docker info --format '{{.ServerVersion}}' > "$WORK/server-version" 2> "$WORK/info.err"; then
  fail "the Docker daemon is not reachable: $(tr '\n' ' ' < "$WORK/info.err" | cut -c1-300). Integration tests are not skipped in its absence - they are reported as failing, because a green job that ran no tests is worse than a red one."
fi

SERVER_VERSION="$(cat "$WORK/server-version" 2>/dev/null || echo unknown)"
[ -n "$SERVER_VERSION" ] || fail "the Docker daemon answered but reported no server version."

# A pull is the only way to know the runner can actually create the containers the
# tests need. The image is pinned to the one the compose rig and the fixtures
# already use, so this is a cache hit on a warm runner and a real download on a
# cold one - never a dependency the build introduced.
PROBE_IMAGE="postgres:16"
if ! docker image inspect "$PROBE_IMAGE" >/dev/null 2>&1; then
  if ! docker pull "$PROBE_IMAGE" > "$WORK/pull.log" 2>&1; then
    fail "the Docker daemon cannot pull $PROBE_IMAGE: $(tr '\n' ' ' < "$WORK/pull.log" | cut -c1-300). This is what a rate-limited or full runner looks like, and it would otherwise surface as 20 minutes of integration test timeouts."
  fi
fi

pass "server $SERVER_VERSION, $PROBE_IMAGE available"
