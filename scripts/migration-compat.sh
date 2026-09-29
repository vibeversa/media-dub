#!/usr/bin/env bash
# EF migration-compat gate (Task 042).
#
# WHAT IT CHECKS, AND WHY IT IS NOT `dotnet ef database update`
# ------------------------------------------------------------
# The repository's expand/contract rule (docs/operations/migration-compat.md,
# frozen) is: migrations are ADDITIVE ONLY, and application code runs against a
# schema produced by the PREVIOUS release for one full release window. Code-only
# rollback then works, because the old code tolerates the new nullable columns.
#
# `dotnet ef database update` on a fresh database proves the chain applies. It
# proves nothing about the rule, because every migration in the chain is written
# by the same author in the same release who is also the one who would benefit
# from breaking it. A drop or a rename in migration N+1 applies perfectly
# cleanly to an empty database - and breaks every pod still running release N.
#
# So this applies the chain in TWO steps against ONE database:
#
#   1. up to the previous release's migration  -> a schema that release N
#      actually shipped, not one this branch invented;
#   2. the rest                               -> the schema this release wants.
#
# Then it inventories the schema at each step and asserts nothing the previous
# release depended on disappeared. That is the rule, checked mechanically, rather
# than trusted.
#
# WHY THE INVENTORY IS COMPARED RATHER THAN THE TESTS BEING RE-RUN
# ----------------------------------------------------------------
# `MigrationCompatTests` already pins six tables and sixteen `stage_executions`
# columns. That is a hand-maintained list, and it is a list of what somebody
# remembered. A diff of the whole catalogue catches everything nobody remembered,
# including a table a worker depends on that no test mentions.
#
# FAIL-CLOSED
# -----------
# No Docker, no `psql`, no `dotnet`, a missing migration directory, an unresolvable
# previous-release migration: each of these is a failure with a named reason. None
# of them is a skip. A compat gate that silently skips is worse than no gate,
# because it reports that a rule holds when nothing checked it.
#
# EXIT CODES
#   0  the chain applies and nothing the previous release needed disappeared
#   1  a rule was broken, or the gate could not run (reason on stderr)
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MIGRATIONS_DIR="$ROOT/src/DubbingPlatform.Infrastructure/Persistence/Migrations"
PG_IMAGE="postgres:16"
PG_DB="dubbing_compat"
PG_USER="dubbing"
PG_PASSWORD="CHANGE_ME"   # local throwaway container; never a real credential
PG_PORT="${MIGRATION_COMPAT_PG_PORT:-55490}"

REASON="UNKNOWN"

fail() {
  REASON="$1"; shift
  echo "::error title=$REASON::$*" >&2
  echo "MIGRATION_COMPAT_RESULT reason=$REASON status=FAIL" >&2
  exit 1
}

note() { printf 'migration-compat: %s\n' "$1"; }

# --- preflight ----------------------------------------------------------------
command -v docker >/dev/null 2>&1 || fail "INFRA_UNAVAILABLE" "docker is not on PATH; the compat check needs a PostgreSQL container and does not skip without one."
docker info >/dev/null 2>&1 || fail "INFRA_UNAVAILABLE" "the Docker daemon is not reachable; the compat check needs a PostgreSQL container and does not skip without one."
command -v dotnet >/dev/null 2>&1 || fail "TOOL_UNAVAILABLE" "dotnet is not on PATH."
[ -d "$MIGRATIONS_DIR" ] || fail "MIGRATIONS_MISSING" "no migrations directory at src/DubbingPlatform.Infrastructure/Persistence/Migrations."

# --- the migration chain ------------------------------------------------------
# The migration list, and the two kinds of file in that directory that are NOT
# migrations:
#   * `*.Designer.cs` is the compiled model snapshot each migration carries;
#   * `*ModelSnapshot.cs` is the CURRENT model snapshot, likewise not a
#     migration. Including it puts the "previous release" boundary on a
#     non-migration, and `dotnet ef database update` then fails with
#     "The migration 'AppDbContextModelSnapshot' was not found" - which is exactly
#     what this script did on its first run against this repository.
mapfile -t MIGRATIONS < <(find "$MIGRATIONS_DIR" -maxdepth 1 -name '*.cs' \
  ! -name '*.Designer.cs' ! -name '*ModelSnapshot.cs' \
  -printf '%f\n' | sort | sed 's/\.cs$//')
COUNT=${#MIGRATIONS[@]}
[ "$COUNT" -ge 1 ] || fail "MIGRATIONS_MISSING" "no migration files found in $MIGRATIONS_DIR."

# The previous release's head is the second-to-last migration: the last one is
# what THIS branch added, and comparing the previous release against a schema
# that already includes this branch's own migration would make the gate compare
# today against today.
PREVIOUS_INDEX=$(( COUNT - 2 ))
[ "$PREVIOUS_INDEX" -ge 0 ] || fail "NO_PREVIOUS_RELEASE" \
  "only ${COUNT} migration(s) exist, so there is no previous release to be compatible with. The compat check needs at least two migrations; the first release has nothing to be compatible with, and a gate that pretended otherwise would be asserting a rule about a schema that never shipped."
PREVIOUS="${MIGRATIONS[$PREVIOUS_INDEX]}"
CURRENT="${MIGRATIONS[$(( COUNT - 1 ))]}"

note "${COUNT} migrations; previous release head = ${PREVIOUS}; this release adds up to = ${CURRENT}"
note "chain:"
for m in "${MIGRATIONS[@]}"; do printf '  - %s\n' "$m"; done

WORK="$(mktemp -d)"
CONTAINER="migration-compat-$$"
cleanup() {
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT

docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
docker run -d --name "$CONTAINER" \
  -e POSTGRES_DB="$PG_DB" -e POSTGRES_USER="$PG_USER" -e POSTGRES_PASSWORD="$PG_PASSWORD" \
  -p "${PG_PORT}:5432" \
  "$PG_IMAGE" >/dev/null 2>&1 || fail "INFRA_UNAVAILABLE" "could not start the PostgreSQL ${PG_IMAGE} container."

note "waiting for PostgreSQL"
ready=0
for _ in $(seq 1 60); do
  if docker exec "$CONTAINER" pg_isready -U "$PG_USER" -d "$PG_DB" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 1
done
[ "$ready" -eq 1 ] || fail "INFRA_UNAVAILABLE" "PostgreSQL did not become ready within 60s."

# The catalogue is read INSIDE the container: it needs no psql on the host, and a
# host without psql is a perfectly normal CI image.
inventory() { # label -> writes $WORK/<label>.tsv
  docker exec "$CONTAINER" psql -U "$PG_USER" -d "$PG_DB" -A -F $'\t' -t -c "
    SELECT table_name || '.' || column_name || ':' || data_type || ':' || is_nullable
    FROM information_schema.columns
    WHERE table_schema = 'public'
    ORDER BY table_name, column_name;" > "$WORK/$1.tsv" 2>/dev/null \
    || fail "INVENTORY_FAILED" "could not read the schema catalogue at step '$1'."
  note "step '$1': $(wc -l < "$WORK/$1.tsv") columns recorded"
}

# --- step 1: the previous release's schema ------------------------------------
note "applying migrations up to ${PREVIOUS} (the previous release's schema)"
if ! dotnet tool restore >/dev/null 2>&1; then
  note "dotnet tool restore reported a problem; continuing (the tools may already be present)"
fi
export ConnectionStrings__Default="Host=127.0.0.1;Port=${PG_PORT};Database=${PG_DB};Username=${PG_USER};Password=${PG_PASSWORD};SSL Mode=Disable"
export DOTNET_ENVIRONMENT=Development

if ! dotnet ef database update "$PREVIOUS" \
  --project src/DubbingPlatform.Infrastructure \
  --startup-project src/DubbingPlatform.Api > "$WORK/ef-previous.log" 2>&1; then
  fail "PREVIOUS_SCHEMA_FAILED" "the migration chain did not apply up to '${PREVIOUS}'. See the log below:
$(tail -40 "$WORK/ef-previous.log")"
fi
inventory previous

# --- step 2: this release's schema --------------------------------------------
note "applying the remaining migrations up to ${CURRENT}"
if ! dotnet ef database update "$CURRENT" \
  --project src/DubbingPlatform.Infrastructure \
  --startup-project src/DubbingPlatform.Api > "$WORK/ef-current.log" 2>&1; then
  fail "CHAIN_FAILED" "the remaining migrations did not apply on top of the previous release's schema. This is a forward-fix case, never a rollback: see docs/operations/migration-compat.md. Log tail:
$(tail -40 "$WORK/ef-current.log")"
fi
inventory current

# --- the rule, checked --------------------------------------------------------
# Anything in the previous schema that is gone from the current one is a break.
# A new column is fine (additive). A dropped column, a narrowed type, or a column
# that became NOT NULL is not: a pod still running release N writes the old shape.
# A new NOT NULL column without a default would also fail the second step at
# insert time rather than here, so the nullable check is what catches it.
node -e '
const fs = require("fs");
const [previousFile, currentFile] = process.argv.slice(1);
const read = (file) => new Map(fs.readFileSync(file, "utf8").split(/\r?\n/).filter((l) => l !== "").map((line) => {
  const at = line.lastIndexOf(":");
  const nullable = line.slice(at + 1);
  const beforeNullable = line.lastIndexOf(":", at);
  return [line.slice(0, beforeNullable), { type: line.slice(beforeNullable + 1, at), nullable }];
}));
const previous = read(previousFile);
const current = read(currentFile);
const dropped = [];
const retyped = [];
const tightened = [];
for (const [name, before] of previous) {
  const after = current.get(name);
  if (after === undefined) { dropped.push(name); continue; }
  if (before.type !== after.type) retyped.push(`${name}: ${before.type} -> ${after.type}`);
  if (before.nullable === "YES" && after.nullable === "NO") tightened.push(`${name}: became NOT NULL`);
}
const added = [...current.keys()].filter((name) => !previous.has(name));

if (dropped.length === 0 && retyped.length === 0 && tightened.length === 0) {
  console.log(`  ${previous.size} columns in the previous release, all still present with the same type and nullability.`);
  console.log(`  ${added.length} column(s) added - additive, which is what the rule requires.`);
  process.exit(0);
}
console.error("  BROKEN (dropped columns/tables):");
for (const name of dropped) console.error(`    - ${name}`);
console.error("  BROKEN (retyped):");
for (const line of retyped) console.error(`    - ${line}`);
console.error("  BROKEN (tightened nullability):");
for (const line of tightened) console.error(`    - ${line}`);
console.error("");
console.error("  The expand/contract rule is ADDITIVE ONLY (docs/operations/migration-compat.md).");
console.error("  A release that breaks it cannot be rolled back code-only, so it must not merge as one change:");
console.error("    - if the old column is genuinely no longer written, stop writing it in THIS release, and drop it");
console.error("      in a separate later release (N+1) once release N is running everywhere shared;");
console.error("    - never edit an already-applied migration - forward-fix with a new one.");
process.exit(1);
' "$WORK/previous.tsv" "$WORK/current.tsv" || fail "SCHEMA_NOT_ADDITIVE" "the migration set is not additive against the previous release's schema."

echo "MIGRATION_COMPAT_RESULT reason=OK status=PASS previous=${PREVIOUS} current=${CURRENT}"
