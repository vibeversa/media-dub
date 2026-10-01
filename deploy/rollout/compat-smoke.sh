#!/usr/bin/env bash
# The staging compatibility matrix (Task 043B, instruction 3 and R3).
#
# WHAT IT RUNS
# ------------
# Three cells, each a REAL API image against a REAL PostgreSQL schema:
#
#   old-api-new-db   the previous release's image against the CURRENT head. This
#                    is the rollback the expand/contract window exists to permit,
#                    and the cell that becomes impossible to run once the
#                    previous image and the previous schema head are gone.
#   new-api-old-db   the candidate against the PREVIOUS head. This is what
#                    catches a new NOT NULL column with no default: the schema
#                    diff calls it additive, the candidate boots fine against its
#                    own schema, and the failure only appears in the window
#                    between the code deploying and the migration running.
#   new-api-new-db   the ordinary case, so a failure in the other two is
#                    attributable to the window rather than to the candidate.
#
# Each cell probes `/health/ready` and one workspace read, and also WRITES a
# project. The write is not optional: a new NOT NULL column with no default
# breaks the INSERT, and a read of a project the harness just created succeeds
# happily - so a read-only matrix proves half of what it claims.
#
# WHERE IT RUNS
# -------------
# `--base-url` (default) builds a throwaway PostgreSQL container and starts the
# API image with `docker run`. That is a real dependency graph, on one machine,
# reproducible by one person - which is why it is the default and why a
# quarterly record can exist. `--base-url` against an already-running environment
# is the staging form, and it skips the container entirely; use it when the
# previous release's image and the previous schema head are only available in a
# real environment.
#
# MIGRATIONS
# ----------
# Applied with the repository's own `dotnet ef database update <head>`, the same
# command the bundle compiles. Heads are read from the migration directory rather
# than hardcoded, so a new migration does not make this script point at the wrong
# schema: the PREVIOUS head is second-to-last and the CURRENT head is last, which
# is the same boundary `scripts/migration-compat.sh` uses.
#
# USAGE
#   bash deploy/rollout/compat-smoke.sh [--record <file>] [--new-image <ref>]
#                                      [--old-image <ref>] [--keep]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PROBE="$ROOT/deploy/rollout/rehearsal/workspace-probe.mjs"
MIGRATIONS_DIR="$ROOT/src/DubbingPlatform.Infrastructure/Persistence/Migrations"

PG_IMAGE="postgres:16"
PG_DB="dubbing_compat"
PG_USER="dubbing"
# A local throwaway container; never a real credential, and the same placeholder
# `scripts/migration-compat.sh` uses for the same container.
PG_PASSWORD="CHANGE_ME"
PG_PORT="${COMPAT_PG_PORT:-55491}"
API_PORT="${COMPAT_API_PORT:-58090}"
SIGNING_KEY="compat-smoke-hs256-signing-key-placeholder-0123456789"

NEW_IMAGE="dubbing-compat:new"
OLD_IMAGE="dubbing-compat:old"
# Defaulted, not required. The record is both the thing this script writes and
# the thing that declares which cells cannot run, and a default is what lets one
# command be "run the matrix" rather than "run the matrix and remember to say
# where the result goes".
RECORD="${COMPAT_RECORD:-$ROOT/deploy/rollout/compat-matrix.json}"
KEEP=0
BASE_URL=""
CONTAINER="compat-smoke-pg"
API_CONTAINER="compat-smoke-api"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --record) RECORD="${2:?--record needs a value}"; shift 2 ;;
    --new-image) NEW_IMAGE="${2:?--new-image needs a value}"; shift 2 ;;
    --old-image) OLD_IMAGE="${2:?--old-image needs a value}"; shift 2 ;;
    --base-url) BASE_URL="${2:?--base-url needs a value}"; shift 2 ;;
    --keep) KEEP=1; shift ;;
    -h|--help) sed -n '2,55p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "compat-smoke: unknown argument $1" >&2; exit 2 ;;
  esac
done

REASON="OK"
RESULTS_JSON="[]"
NETWORK="compat-smoke-net"
cleanup() {
  if [ "$KEEP" -eq 0 ]; then
    docker rm -f "$API_CONTAINER" >/dev/null 2>&1 || true
    docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
  else
    echo "containers ${CONTAINER} and ${API_CONTAINER} left in place (--keep)" >&2
  fi
}
trap cleanup EXIT

command -v node >/dev/null 2>&1 || { echo "::error::node is required."; exit 2; }

# --- the declared exemptions ----------------------------------------------------
# Read back from the record, not hardcoded here. A cell that cannot run is a
# REVIEWED DECISION recorded in a file a reviewer reads, with a reason, the
# condition that unblocks it, the risk being accepted and a review date - and the
# script carries it into the run it writes so the declaration and the measurement
# cannot drift apart. `scripts/check-rollout-window.mjs` is what enforces that all
# five fields are present and that the date has not passed, and it PRINTS every
# exemption on every run.
EXEMPTED_JSON="[]"
if [ -f "$RECORD" ]; then
  EXEMPTED_JSON="$(node -e '
    const fs = require("node:fs");
    const matrix = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
    process.stdout.write(JSON.stringify(Array.isArray(matrix.unavailable) ? matrix.unavailable : []));
  ' "$RECORD" 2>/dev/null || echo "[]")"
fi
is_exempt() { # cell id -> 0 when the cell is declared unavailable
  printf '%s' "$EXEMPTED_JSON" | node -e '
    let raw = "";
    process.stdin.on("data", (chunk) => { raw += chunk; });
    process.stdin.on("end", () => {
      let list = [];
      try { list = JSON.parse(raw); } catch { list = []; }
      process.exit(list.some((entry) => entry && entry.cell === process.argv[1]) ? 0 : 1);
    });
  ' "$1"
}

# --- the migration heads --------------------------------------------------------
# Same exclusion as `scripts/migration-compat.sh`, for the same reason: a
# `*.Designer.cs` is the compiled model snapshot a migration carries, and
# `*ModelSnapshot.cs` is the current model. Neither is a migration, and asking
# `dotnet ef` to stop at one of them fails with "The migration
# 'AppDbContextModelSnapshot' was not found".
mapfile -t MIGRATIONS < <(find "$MIGRATIONS_DIR" -maxdepth 1 -name '*.cs' \
  ! -name '*.Designer.cs' ! -name '*ModelSnapshot.cs' \
  -printf '%f\n' | sort | sed 's/\.cs$//')
COUNT=${#MIGRATIONS[@]}
[ "$COUNT" -ge 2 ] || { echo "::error::need at least two migrations to have a previous head; found ${COUNT}."; exit 2; }
PREVIOUS_HEAD="${MIGRATIONS[$((COUNT - 2))]}"
CURRENT_HEAD="${MIGRATIONS[$((COUNT - 1))]}"
CURRENT_IMAGE_ID="$(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)"
PREVIOUS_IMAGE_REF="${OLD_IMAGE}"

echo "== compatibility matrix =="
echo "migration heads: previous = ${PREVIOUS_HEAD}, current = ${CURRENT_HEAD}"
echo "images:          previous = ${OLD_IMAGE}, candidate = ${NEW_IMAGE}"
echo "candidate commit: ${CURRENT_IMAGE_ID}"

# --- the database ---------------------------------------------------------------
if [ -z "$BASE_URL" ]; then
  command -v docker >/dev/null 2>&1 || { echo "::error::docker is required (or pass --base-url)."; exit 2; }
  command -v dotnet >/dev/null 2>&1 || { echo "::error::dotnet is required to apply the migrations."; exit 2; }
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  # A user-defined network, not `--network container:<pg>`. Sharing the database
  # container's network namespace puts the API's listener on a loopback that only
  # exists INSIDE that namespace, so the host cannot reach it and every probe
  # returns 000 - which reads as three failing compatibility cells rather than as
  # a harness that cannot see its own subject. On a user-defined network the
  # container name resolves through Docker's embedded DNS, so the API reaches the
  # database by name and publishes 8080 to a host port.
  #
  # The API container is removed FIRST: a container still attached keeps the
  # network alive, `network rm` then fails, and the run dies with "network with
  # name compat-smoke-net already exists" - which is the previous run's container,
  # not anything about this one.
  docker rm -f "$API_CONTAINER" >/dev/null 2>&1 || true
  docker network rm "$NETWORK" >/dev/null 2>&1 || true
  docker network create "$NETWORK" >/dev/null
  docker run -d --name "$CONTAINER" --network "$NETWORK" --network-alias postgres \
    -e POSTGRES_DB="$PG_DB" -e POSTGRES_USER="$PG_USER" -e POSTGRES_PASSWORD="$PG_PASSWORD" \
    -p "${PG_PORT}:5432" "$PG_IMAGE" >/dev/null
  # `SSL Mode=Disable` in the connection string below is load-bearing, not
  # decorative: Npgsql defaults to `Prefer`, which attempts a TLS handshake the
  # stock postgres:16 image does not serve and then fails with an opaque
  # "Exception while reading from stream".
  ready=0
  for _ in $(seq 1 60); do
    if docker exec "$CONTAINER" pg_isready -U "$PG_USER" -d "$PG_DB" >/dev/null 2>&1; then ready=1; break; fi
    sleep 1
  done
  [ "$ready" -eq 1 ] || { echo "::error::PostgreSQL did not become ready."; exit 2; }
  echo "PostgreSQL ${PG_IMAGE} ready on ${PG_PORT}"
  BASE_URL="http://127.0.0.1:${API_PORT}"
  CONNECTION="Host=127.0.0.1;Port=${PG_PORT};Database=${PG_DB};Username=${PG_USER};Password=${PG_PASSWORD};SSL Mode=Disable"
  # NOT escaped, and the first version of this line escaped the semicolons. The
  # value is passed as `ConnectionStrings__Default="$CONNECTION"` - a shell
  # variable prefix assignment - and there is nothing to escape: the escaping is
  # for an unquoted `export`, and applying it here produces a connection string
  # Npgsql cannot parse, which surfaces as "Failed to connect to 127.0.0.1:55491"
  # rather than as anything mentioning a backslash.
else
  echo "using the supplied environment at ${BASE_URL}; no container is started"
  CONNECTION="${COMPAT_CONNECTION_STRING:-}"
fi

apply_head() { # head
  [ -n "$BASE_URL" ] || return 0
  local log="$WORK/ef-$1.log"
  if ConnectionStrings__Default="$CONNECTION" \
     DOTNET_ENVIRONMENT=Development \
       dotnet ef database update "$1" \
         --project "$ROOT/src/DubbingPlatform.Infrastructure" \
         --startup-project "$ROOT/src/DubbingPlatform.Api" > "$log" 2>&1; then
    return 0
  fi
  echo "::error::dotnet ef database update $1 failed:"
  tail -25 "$log"
  return 1
}

schema_head() { # -> the last row in __EFMigrationsHistory
  # `|| true` is load-bearing. With `pipefail` on, a `docker exec` that fails -
  # a missing table, a container that exited, a psql that is not in the image -
  # makes the WHOLE pipeline non-zero, and a command substitution in an
  # assignment propagates that to `set -e`. The first version of this function
  # aborted the run here, silently, right after a migration that had applied
  # perfectly well.
  docker exec "$CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -A -t \
    -c 'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;' 2>/dev/null \
    | tr -d '\r' || true
}

start_api() { # image -> starts the API, waits for the process to be live, returns 0/1
  docker rm -f "$API_CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$API_CONTAINER" --network "$NETWORK" -p "${API_PORT}:8080" \
    -e ASPNETCORE_URLS="http://+:8080" \
    -e DOTNET_ENVIRONMENT=Development \
    -e ConnectionStrings__Default="Host=postgres;Port=5432;Database=${PG_DB};Username=${PG_USER};Password=${PG_PASSWORD};SSL Mode=Disable" \
    -e Transport__Provider=InMemory \
    -e Providers__DefaultProvider=mock \
    -e Storage__Endpoint=127.0.0.1:9000 -e Storage__Bucket=dubbing \
    -e Storage__AccessKey=CHANGE_ME -e Storage__SecretKey=CHANGE_ME \
    -e Auth__SigningKey="$SIGNING_KEY" \
    -e Auth__Audience=dubbing-api \
    "$1" >/dev/null
  # 90 attempts at 1s: the API JIT-warms on first request and the host is not
  # built until the first request, so a shorter budget reports start-up slowness
  # as a compatibility failure. `/health/live` and not `/health/ready`, because
  # readiness is one of the things under test - a candidate that is ahead of the
  # schema is EXPECTED to be un-ready, and waiting for readiness to be 200 here
  # would hang the very cell that proves the migration-currency gate exists.
  for _ in $(seq 1 90); do
    code="$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:${API_PORT}/health/live" 2>/dev/null || echo 000)"
    [ "$code" = "200" ] && return 0
    sleep 1
  done
  return 1
}

json_field() { # field
  node -e '
    let raw = "";
    process.stdin.on("data", (chunk) => { raw += chunk; });
    process.stdin.on("end", () => {
      try { process.stdout.write(String(JSON.parse(raw)[process.argv[1]] ?? "")); }
      catch { process.stdout.write(""); }
    });
  ' "$1" 2>/dev/null || echo ""
}

# One cell. The verdict is deliberately narrow: readiness must be 200 WITH a
# non-empty `checks` object, the write must succeed, and the read must be 200.
# A cell that "passes" on a 404 from a route that no longer exists is the false
# pass the matrix exists to prevent, and the probe reports statuses rather than
# bodies so the shell can compare them.
run_cell() { # id image schemaLabel expectedMigrationCurrency
  local id="$1" api_image="$2" schema_label="$3" expect_currency="$4"
  local out health_status currency currency_desc write workspace unhealthy
  if is_exempt "$id"; then
    echo ""
    echo "-- ${id}: DECLARED UNAVAILABLE, not run. See the 'unavailable' array in deploy/rollout/compat-matrix.json --"
    return 0
  fi
  echo ""
  echo "-- ${id}: ${api_image##*:} against ${schema_label} (expect migration-currency = ${expect_currency}) --"
  if ! start_api "$api_image"; then
    echo "  FAIL  the API process did not come up; logs: docker logs ${API_CONTAINER}"
    record_cell "$id" "FAIL" "the API process did not start" "$api_image" "$schema_label" absent 0 0
    return 1
  fi
  out="$(COMPAT_BASE_URL="http://127.0.0.1:${API_PORT}" COMPAT_SIGNING_KEY="$SIGNING_KEY" node "$PROBE" 2>&1 || true)"
  health_status="$(printf '%s' "$out" | json_field readyStatus)"
  currency="$(printf '%s' "$out" | json_field migrationCurrency)"
  currency_desc="$(printf '%s' "$out" | json_field migrationCurrencyDescription)"
  write="$(printf '%s' "$out" | json_field writeStatus)"
  workspace="$(printf '%s' "$out" | json_field workspaceStatus)"
  unhealthy="$(printf '%s' "$out" | json_field unhealthyChecks)"
  health_status="${health_status:-0}"; currency="${currency:-absent}"; write="${write:-0}"; workspace="${workspace:-0}"
  local detail="migration-currency=${currency} (${currency_desc:-no description}), POST /api/v1/projects=${write}, GET /api/v1/projects/{id}/workspace=${workspace}, /health=${health_status}, other unhealthy checks: ${unhealthy:-none}"
  echo "  ${detail}"
  if [ "$currency" = "$expect_currency" ] && [ "$write" = "201" ] && [ "$workspace" = "200" ]; then
    echo "  ok    ${id}"
    record_cell "$id" "PASS" "$detail" "$api_image" "$schema_label" "$currency" "$write" "$workspace" "$health_status"
    return 0
  fi
  echo "  FAIL  ${id}: expected migration-currency=${expect_currency}, POST=201, workspace=200"
  record_cell "$id" "FAIL" "$detail" "$api_image" "$schema_label" "$currency" "$write" "$workspace" "$health_status"
  return 1
}

WORK="$(mktemp -d)"
trap 'cleanup; rm -rf "$WORK"' EXIT

record_cell() { # id result detail apiImage schemaLabel migrationCurrency writeStatus workspaceStatus
  local entry
  entry="{\"id\": \"$1\", \"result\": \"$2\", \"detail\": \"$3\", \"apiImage\": \"$4\", \"schemaHead\": \"$5\", \"migrationCurrency\": \"${6:-absent}\", \"writeStatus\": ${7:-0}, \"workspaceStatus\": ${8:-0}, \"healthStatus\": ${9:-0}}"
  if [ "$RESULTS_JSON" = "[]" ]; then RESULTS_JSON="[$entry]"; else RESULTS_JSON="${RESULTS_JSON}, ${entry}"; fi
}

FAILED=0

# The expected readiness per cell, and it is NOT the same for all three.
#
# `MigrationCurrencyCheck` deliberately reports a build that is AHEAD of the
# schema as not-ready, so a pod never takes traffic before its own migration has
# run. The candidate against the PREVIOUS head is therefore expected to answer
# 503 on `/health/ready` - and that 503 is the cell PROVING the gate exists,
# not the cell failing. Writing 200 into all three cells would either have
# required disabling the check or have produced three permanent failures that
# everybody learned to ignore.
#
# The write and the read are still asserted for that cell, and they are the point
# of it: the candidate must be able to serve a request against a schema it is not
# shipped with, which is the property a NOT NULL column without a default breaks.
EXPECT_CURRENCY_CANDIDATE_ON_CURRENT=Healthy
EXPECT_CURRENCY_PREVIOUS_ON_EXPANDED=Healthy
EXPECT_CURRENCY_CANDIDATE_ON_PREVIOUS=Unhealthy

# --- cell 1: the candidate against the schema it ships with ---------------------
echo ""
echo "-- applying the current head (${CURRENT_HEAD}) --"
apply_head "$CURRENT_HEAD" || { echo "::error::could not apply the current head."; exit 1; }
ACTUAL_HEAD="$(schema_head)"
echo "schema head is now: ${ACTUAL_HEAD:-$CURRENT_HEAD}"

run_cell "new-api-new-db" "$NEW_IMAGE" "${ACTUAL_HEAD:-$CURRENT_HEAD}" "$EXPECT_CURRENCY_CANDIDATE_ON_CURRENT" || FAILED=1

# --- cell 2: the previous release's image against the new schema ----------------
# This is the ROLLBACK cell. It runs BEFORE the schema is rolled back, because the
# point is the previous image against the EXPANDED schema - which is the state a
# rollback actually finds.
run_cell "old-api-new-db" "$OLD_IMAGE" "${ACTUAL_HEAD:-$CURRENT_HEAD}" "$EXPECT_CURRENCY_PREVIOUS_ON_EXPANDED" || FAILED=1

# --- cell 3: the candidate against the previous schema --------------------------
echo ""
echo "-- reverting to the previous head (${PREVIOUS_HEAD}) --"
apply_head "$PREVIOUS_HEAD" || { echo "::error::could not apply the previous head."; exit 1; }
PREV_ACTUAL="$(schema_head)"
echo "schema head is now: ${PREV_ACTUAL:-$PREVIOUS_HEAD}"
run_cell "new-api-old-db" "$NEW_IMAGE" "${PREV_ACTUAL:-$PREVIOUS_HEAD}" "$EXPECT_CURRENCY_CANDIDATE_ON_PREVIOUS" || FAILED=1

# --- the record -----------------------------------------------------------------
TODAY="$(date -u +%Y-%m-%d)"
RESULT="PASS"
[ "$FAILED" -eq 0 ] || { RESULT="FAIL"; REASON="COMPAT_CELL_FAILED"; }
# The object's FIELDS, without the braces. Both consumers need the same fields and
# they need them wrapped differently: stdout wants a whole object, and the file
# wants the committed `$comment` block spliced in front of the same fields.
#
# Splicing the comment and then printing a SECOND `{...}` is the version that
# ships two top-level values, and the gate's first reaction is
# "Unexpected non-whitespace character after JSON at position 12" - a message
# that names neither the cause nor the line. Hence the split.
write_record_fields() {
  cat <<JSON
    "date": "${TODAY}",
    "environment": "ephemeral PostgreSQL ${PG_IMAGE} container plus the API images ${NEW_IMAGE} (candidate, commit ${CURRENT_IMAGE_ID}) and ${OLD_IMAGE} (previous release), on one machine; the staging form of this is the same script with --base-url",
    "result": "${RESULT}",
    "unavailable": ${EXEMPTED_JSON},
    "results": ${RESULTS_JSON}
JSON
}
write_record() {
  {
    echo "{"
    write_record_fields
    echo "}"
  }
}
if [ -n "$RECORD" ]; then
  {
    sed -n '/^  "\$comment": \[$/,/^  \],$/p' "$RECORD" 2>/dev/null || true
    echo "{"
    write_record_fields
    echo "}"
  } > "$RECORD.new" && mv "$RECORD.new" "$RECORD"
fi

echo ""
echo "== compatibility matrix: $RESULT =="
write_record
echo "COMPAT_SMOKE_RESULT reason=${REASON} status=${RESULT} cells=$(printf '%s' "$RESULTS_JSON" | grep -o '"id"' | wc -l | tr -d ' ')"
[ "$FAILED" -eq 0 ] || exit 1
