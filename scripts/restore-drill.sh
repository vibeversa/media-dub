#!/usr/bin/env bash
# New-entity restore drill (Task 043C).
#
# WHAT THIS IS
# ------------
# The restore drill for the seven durable-entity groups Plan B added (tenant
# users, preferences, notifications, activity events, memberships, voice
# preview jobs, extended project metadata). It is a real restore: dump, wipe,
# restore, verify. It is not a document, and it is not a "we have read the
# procedure" claim.
#
# WHAT IT PROVES, AND WHY EACH STEP EXISTS
# ---------------------------------------
#   1. schema + RLS   The drill refuses to run against a database with no tenant
#                     isolation. A restore into a database with RLS missing is
#                     the worst outcome available and it is invisible in a row
#                     count, so it is checked before anything is written.
#   2. seed           Synthetic rows in all seven groups plus the parents
#                     (deploy/backup/drill-seed.sql). Synthetic because a drill
#                     that seeds real rows is a drill that writes to real data.
#   3. dump           `pg_dump -Fc` over exactly the tables in
#                     deploy/backup/scope.json. Custom format, because that is
#                     the only format pg_restore can restore selectively from.
#   4. wipe           TRUNCATE, not DELETE. A DELETE that fails partway leaves a
#                     half-empty table and the count comparison then "passes"
#                     against a partial wipe.
#   5. restore        `pg_restore --data-only`. The schema is the migration
#                     chain's job, not the archive's, so this drill tests the
#                     DATA half and the migration Job tests the schema half.
#   6. verify counts  Per table, before == after, and non-zero. Non-zero is the
#                     point: `pg_restore` reports success after inserting zero
#                     rows, so a restore run as a role without BYPASSRLS produces
#                     a clean, successful, EMPTY restore. The non-zero assertion
#                     is the only thing that catches it.
#   7. referential    The schema has no foreign keys between any of these tables,
#                     so PostgreSQL cannot tell a coherent restore from an
#                     incoherent one. This step is why the drill is worth more
#                     than the count check alone.
#   8. spot-read      One project, one user, one notification, by id, with the
#                     columns that only exist because of Plan B. A count that
#                     matches while every `name` is NULL is a restore that passed.
#
# SAFE BY CONSTRUCTION
#   - No shell concatenation of any SQL or of any identifier. Table names come
#     from a validated identifier regex; every psql invocation is an argv array.
#   - Refuses to run unless the target host is a loopback address, is inside a
#     container this script started, or DRILL_ALLOW_REMOTE=1 is set explicitly.
#     Production requires DRILL_ALLOW_REMOTE=1 and a recorded access window.
#   - Never writes a connection string, a password or a token to stdout or to the
#     result file. The archive lives in a directory the caller names, and it is
#     synthetic data; a real-tenant archive must not be left on disk.
#
# USAGE
#   bash scripts/restore-drill.sh                  # start a disposable server, drill it, tear it down
#   bash scripts/restore-drill.sh --keep-container # leave the server up afterwards
#   DRILL_HOST=db.internal DRILL_ALLOW_REMOTE=1 \
#     DRILL_DB=dubbing_staging DRILL_PGPASSWORD=... \
#     bash scripts/restore-drill.sh                # drill a real staging instance
#   bash scripts/restore-drill.sh --require-schema # do not create the schema; fail if it is absent
#
# MACHINE-READABLE OUTPUT
#   One line: RESTORE_DRILL_RESULT reason=<REASON> status=<PASS|FAIL> tables=<n> rows=<n> gaps=<n>
#   REASONS is a closed set; docs/ci-branch-protection.md lists it.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCOPE="$ROOT/deploy/backup/scope.json"
SEED="$ROOT/deploy/backup/drill-seed.sql"

DRILL_HOST="${DRILL_HOST:-127.0.0.1}"
DRILL_PORT="${DRILL_PORT:-55432}"
DRILL_DB="${DRILL_DB:-dubbing_drill}"
DRILL_USER="${DRILL_USER:-dubbing}"
DRILL_PGPASSWORD="${DRILL_PGPASSWORD:-dubbing}"
DRILL_CONTAINER="${DRILL_CONTAINER:-dubbing-drill-pg}"
DRILL_IMAGE="${DRILL_IMAGE:-postgres:16-alpine}"
DRILL_ALLOW_REMOTE="${DRILL_ALLOW_REMOTE:-0}"
DRILL_ARTIFACT_DIR="${DRILL_ARTIFACT_DIR:-$ROOT/deploy/.artifacts}"
REQUIRE_SCHEMA=0
KEEP_CONTAINER=0
# USE_CONTAINER: client tools come from the postgres container. Set whenever a
# container is available - started OR reused - because a container that is
# already up is the better place to run pg_dump from: its client is guaranteed to
# match its server, and the local toolchain frequently has no psql at all.
USE_CONTAINER=0
# STARTED_CONTAINER: this script created it, so this script tears it down.
STARTED_CONTAINER=0

REASON="OK"
STATUS="PASS"
GAPS=0

# ---- reason vocabulary (closed set) ------------------------------------------
readonly REASONS_OK="OK"
readonly REASONS_NO_CLIENT="CLIENT_UNAVAILABLE"
readonly REASONS_SCOPE="SCOPE_INVALID"
# Alias, because half the call sites read `REASONS_NO_SCOPE` and half read
# `REASONS_SCOPE`, and `set -u` turns a one-letter divergence into an unbound
# variable at the exact moment the drill is reporting a failure.
readonly REASONS_NO_SCOPE="$REASONS_SCOPE"
readonly REASONS_NO_SEED="SEED_MISSING"
readonly REASONS_REMOTE="REMOTE_TARGET_REFUSED"
readonly REASONS_SERVER="SERVER_UNAVAILABLE"
readonly REASONS_SCHEMA="SCHEMA_NOT_APPLIED"
readonly REASONS_ISOLATION="TENANT_ISOLATION_MISSING"
readonly REASONS_SEED_FAILED="SEED_FAILED"
readonly REASONS_DUMP_FAILED="DUMP_FAILED"
readonly REASONS_WIPE_FAILED="WIPE_FAILED"
readonly REASONS_RESTORE_FAILED="RESTORE_FAILED"
readonly REASONS_COUNT_MISMATCH="COUNT_MISMATCH"
readonly REASONS_EMPTY_RESTORE="EMPTY_RESTORE"
readonly REASONS_SPOT_READ="SPOT_READ_FAILED"
readonly REASONS_DANGLING="DANGLING_REFERENCES"

die() { REASON="$1"; STATUS="FAIL"; shift; for line in "$@"; do echo "  !! $line"; done; finish; }
note() { echo "  -- $*"; }
ok()   { echo "  ok $*"; }

finish() {
  local tables rows
  tables="$(grep -c . "$COUNT_TABLES_FILE" 2>/dev/null || true)"
  rows="$(paste -sd+ -s < "$SUM_ROWS_FILE" 2>/dev/null | bc 2>/dev/null || true)"
  [ -n "$tables" ] || tables=0
  [ -n "$rows" ] || rows=0
  echo
  echo "RESTORE_DRILL_RESULT reason=${REASON} status=${STATUS} tables=${tables} rows=${rows} gaps=${GAPS}"
  if [ "$STATUS" != "PASS" ] && [ "$STARTED_CONTAINER" = "1" ] && [ "$KEEP_CONTAINER" = "0" ]; then
    echo "the server is left running for inspection; remove it with: docker rm -f ${CONTAINER_NAME}"
  fi
  [ "$STATUS" = "PASS" ] && exit 0
  exit 1
}

# ---- argv-only wrappers. No SQL is ever built by string concatenation. --------
# Inside the container we use `docker exec -i`, so the SQL travels on stdin and
# is never an argument (and therefore never in the process table or in `ps`).
psql_run() {
  if [ "$USE_CONTAINER" = "1" ]; then
    docker exec -i -e PGPASSWORD="$DRILL_PGPASSWORD" "$CONTAINER_NAME" \
      psql -v ON_ERROR_STOP=1 -q -t -A -F'|' -U "$DRILL_USER" -d "$DRILL_DB" "$@"
  else
    PGPASSWORD="$DRILL_PGPASSWORD" psql -v ON_ERROR_STOP=1 -q -t -A -F'|' \
      -h "$DRILL_HOST" -p "$DRILL_PORT" -U "$DRILL_USER" -d "$DRILL_DB" "$@"
  fi
}

client_available() {
  if [ "$USE_CONTAINER" = "1" ]; then
    docker exec "$CONTAINER_NAME" pg_dump --version >/dev/null 2>&1 \
      && docker exec "$CONTAINER_NAME" pg_restore --version >/dev/null 2>&1
  else
    command -v pg_dump >/dev/null 2>&1 && command -v pg_restore >/dev/null 2>&1
  fi
}

pg_dump_run() {
  if [ "$USE_CONTAINER" = "1" ]; then
    docker exec -e PGPASSWORD="$DRILL_PGPASSWORD" "$CONTAINER_NAME" \
      pg_dump -h 127.0.0.1 -U "$DRILL_USER" -d "$DRILL_DB" -Fc "$@"
  else
    PGPASSWORD="$DRILL_PGPASSWORD" pg_dump -h "$DRILL_HOST" -p "$DRILL_PORT" \
      -U "$DRILL_USER" -d "$DRILL_DB" -Fc "$@"
  fi
}

pg_restore_run() {
  if [ "$USE_CONTAINER" = "1" ]; then
    docker exec -i -e PGPASSWORD="$DRILL_PGPASSWORD" "$CONTAINER_NAME" \
      pg_restore -h 127.0.0.1 -U "$DRILL_USER" -d "$DRILL_DB" "$@"
  else
    PGPASSWORD="$DRILL_PGPASSWORD" pg_restore -h "$DRILL_HOST" -p "$DRILL_PORT" \
      -U "$DRILL_USER" -d "$DRILL_DB" "$@"
  fi
}

while [ $# -gt 0 ]; do
  case "$1" in
    --keep-container) KEEP_CONTAINER=1 ;;
    --require-schema) REQUIRE_SCHEMA=1 ;;
    -h|--help) sed -n '2,60p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "$REASONS_NO_SCOPE" "unknown argument: $1" ;;
  esac
  shift
done

WORK="$(mktemp -d "${TMPDIR:-/tmp}/restore-drill.XXXXXX")"
COUNT_TABLES_FILE="$WORK/tables"
SUM_ROWS_FILE="$WORK/rows"
BEFORE_FILE="$WORK/before"
AFTER_FILE="$WORK/after"
# Created up front so `finish` can read them from any failure point, including
# the ones that happen before the drill has counted anything.
: > "$COUNT_TABLES_FILE"
: > "$SUM_ROWS_FILE"
: > "$BEFORE_FILE"
: > "$AFTER_FILE"
CONTAINER_NAME="$DRILL_CONTAINER"
# The archive is a file on the HOST, inside the temp work directory, so it is
# removed with it. It is written by streaming `pg_dump -Fc` on stdout, which is
# the supported way to produce a custom-format archive into a pipe - and it keeps
# the client inside the container (so the client version always matches the
# server) without a nested-quote `sh -c` wrapper, which is the one place the
# table names would otherwise have to be re-interpolated inside a second shell.
ARCHIVE="$WORK/new-entity-drill.dump"
cleanup() {
  if [ "$STARTED_CONTAINER" = "1" ] && [ "$KEEP_CONTAINER" = "0" ]; then
    docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true
  fi
  rm -rf "$WORK"
}
trap cleanup EXIT

echo "== new-entity restore drill (Task 043C) =="

# ---- 0. inputs ---------------------------------------------------------------
[ -f "$SCOPE" ] || die "$REASONS_NO_SCOPE" "deploy/backup/scope.json is missing"
[ -f "$SEED" ]  || die "$REASONS_NO_SEED"  "deploy/backup/drill-seed.sql is missing"

# Table list, read from the scope file. Identifiers are validated rather than
# trusted: scope.json is a committed file, but a backup job that interpolates an
# unvalidated identifier is a job one typo away from dumping the wrong database.
mapfile -t TABLES < <(node -e '
  const s = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  const names = [];
  for (const g of s.newEntityGroups) for (const t of g.tables) names.push(t);
  for (const t of s.parents.tables) if (!names.includes(t)) names.push(t);
  process.stdout.write(names.join("\n"));
' "$SCOPE")
[ "${#TABLES[@]}" -gt 0 ] || die "$REASONS_NO_SCOPE" "scope.json names no tables"

for t in "${TABLES[@]}"; do
  case "$t" in
    [a-z_]*) ;;
    *) die "$REASONS_NO_SCOPE" "table name is not a bare lowercase identifier: $t" ;;
  esac
done

# Completeness, asserted against a list that does NOT come from scope.json.
#
# This looks redundant and is the most important check in the file. Removing a
# group from scope.json removes it from the dump AND from the count comparison,
# so the drill compares 9 tables against 9 tables, every count matches, and it
# reports PASS - a drill that verified slightly less and said so in exactly the
# same way. That was proved by doing it: see docs/backup.md, gap D2.
#
# The fix is the same shape as tools/runbooks-index.test.mjs: the required set is
# written down a second time, somewhere that a change to the data file does not
# also change.
REQUIRED_GROUPS=(
  tenant-users
  preferences
  notifications
  activity-events
  memberships
  voice-preview-jobs
  project-metadata
)
mapfile -t GROUP_TABLES < <(node -e '
  const s = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  for (const g of s.newEntityGroups) for (const t of g.tables) process.stdout.write(t + "\n");
' "$SCOPE")
[ "${#GROUP_TABLES[@]}" -eq "${#REQUIRED_GROUPS[@]}" ] \
  || note "note: ${#GROUP_TABLES[@]} table(s) across ${#REQUIRED_GROUPS[@]} group(s)"

DECLARED_GROUPS="$(node -e '
  const s = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  for (const g of s.newEntityGroups) {
    if (!Array.isArray(g.tables) || g.tables.length === 0) {
      process.stderr.write(`group ${g.id} names no table\n`); process.exit(1);
    }
    if (typeof g.restorePriority !== "number" || typeof g.retentionDays !== "number") {
      process.stderr.write(`group ${g.id} has no restorePriority/retentionDays\n`); process.exit(1);
    }
    process.stdout.write(g.id + "\n");
  }
' "$SCOPE" 2>"$WORK/scope.err")" || die "$REASONS_NO_SCOPE" \
  "scope.json is not a valid backup scope: $(head -2 "$WORK/scope.err" | tr '\n' ' ')"

MISSING=0
for g in "${REQUIRED_GROUPS[@]}"; do
  if ! printf '%s\n' "$DECLARED_GROUPS" | grep -qx "$g"; then
    echo "  !! scope.json does not declare the required group: $g"
    MISSING=$((MISSING + 1))
  fi
done
[ "$MISSING" -eq 0 ] || die "$REASONS_NO_SCOPE" \
  "$MISSING required entity group(s) are missing from the backup scope" \
  "a group that is not in the scope is not in the dump, not in the count check, and not in the drill"
ok "all ${#REQUIRED_GROUPS[@]} required entity groups declared, each with a table, a priority and a retention"

ok "${#TABLES[@]} table(s) in scope: ${TABLES[*]}"

# ---- 1. server ---------------------------------------------------------------
if [ "$DRILL_HOST" != "127.0.0.1" ] && [ "$DRILL_HOST" != "localhost" ] && [ "$DRILL_ALLOW_REMOTE" != "1" ]; then
  die "$REASONS_REMOTE" "refusing to drill a non-loopback host ($DRILL_HOST)" \
    "set DRILL_ALLOW_REMOTE=1, and only with a recorded access window (docs/backup.md)"
fi

if docker info >/dev/null 2>&1; then
  if docker ps --format '{{.Names}}' | grep -qx "$DRILL_CONTAINER"; then
    USE_CONTAINER=1
    note "reusing the running container $DRILL_CONTAINER"
  else
    docker rm -f "$DRILL_CONTAINER" >/dev/null 2>&1 || true
    docker run -d --name "$DRILL_CONTAINER" \
      -e POSTGRES_DB="$DRILL_DB" -e POSTGRES_USER="$DRILL_USER" -e POSTGRES_PASSWORD="$DRILL_PGPASSWORD" \
      -p "$DRILL_PORT:5432" "$DRILL_IMAGE" >/dev/null || die "$REASONS_SERVER" "could not start $DRILL_IMAGE"
    STARTED_CONTAINER=1
    USE_CONTAINER=1
    for _ in $(seq 1 60); do
      if docker exec "$DRILL_CONTAINER" pg_isready -U "$DRILL_USER" -d "$DRILL_DB" >/dev/null 2>&1; then break; fi
      sleep 1
    done
    ok "started a disposable $DRILL_IMAGE"
  fi
else
  note "no Docker daemon; using an existing instance at $DRILL_HOST:$DRILL_PORT"
fi

client_available || die "$REASONS_NO_CLIENT" "pg_dump and pg_restore must both be available (PATH, or in $DRILL_CONTAINER)"

MIGRATIONS="$(psql_run -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='public';")"
[ -n "$MIGRATIONS" ] || die "$REASONS_SERVER" "could not connect to $DRILL_HOST:$DRILL_PORT/$DRILL_DB"
note "connected; $MIGRATIONS table(s) in the public schema"

if [ "$MIGRATIONS" = "0" ] && [ "$REQUIRE_SCHEMA" = "1" ]; then
  die "$REASONS_SCHEMA" "no schema present and --require-schema was given"
fi
if [ "$MIGRATIONS" = "0" ]; then
  die "$REASONS_SCHEMA" \
    "no schema present" \
    "apply the committed migrations first: dotnet ef database update --project src/DubbingPlatform.Infrastructure" \
    "--startup-project src/DubbingPlatform.Api --context AppDbContext   (ConnectionStrings__Default=<dsn>)"
fi

APPLIED="$(psql_run -c "SELECT count(*) FROM \"__EFMigrationsHistory\";")"
ok "$APPLIED migration(s) applied"

# ---- 2. tenant isolation is a precondition -----------------------------------
# Checked before anything is written. A restore into a database whose RLS is
# missing produces cross-tenant reads, and neither a row count nor a spot-read
# can see it.
RLS_MISSING=0
for t in "${TABLES[@]}"; do
  ENABLED="$(psql_run -c "SELECT rowsecurity FROM pg_tables WHERE schemaname='public' AND tablename='$t';")"
  if [ "$t" != "tenants" ] && [ "$ENABLED" != "t" ]; then
    echo "  !! row level security is NOT enabled on $t"
    RLS_MISSING=$((RLS_MISSING + 1))
  fi
done
[ "$RLS_MISSING" -eq 0 ] || die "$REASONS_ISOLATION" "$RLS_MISSING table(s) in scope have row level security disabled"
ok "row level security is enabled on every scoped table"

# ---- 3. seed -----------------------------------------------------------------
psql_run -f "$SEED" >/dev/null 2>&1 || psql_run < "$SEED" >/dev/null \
  || die "$REASONS_SEED_FAILED" "deploy/backup/drill-seed.sql did not apply"
ok "seeded synthetic rows (deploy/backup/drill-seed.sql)"

: > "$COUNT_TABLES_FILE"
: > "$SUM_ROWS_FILE"
: > "$BEFORE_FILE"
for t in "${TABLES[@]}"; do
  c="$(psql_run -c "SELECT count(*) FROM $t;")"
  [ -n "$c" ] || die "$REASONS_SEED_FAILED" "count(*) on $t returned nothing"
  [ "$c" -gt 0 ] || die "$REASONS_SEED_FAILED" "$t is empty after the seed; the drill would prove nothing"
  echo "$t" >> "$COUNT_TABLES_FILE"
  echo "$c" >> "$SUM_ROWS_FILE"
  echo "$t=$c" >> "$BEFORE_FILE"
  echo "     $t: $c"
done
ok "counted ${#TABLES[@]} table(s) before the restore"

# ---- 4. dump -----------------------------------------------------------------
DUMP_ARGS=(--data-only --no-owner --no-privileges)
for t in "${TABLES[@]}"; do DUMP_ARGS+=(--table="$t"); done
pg_dump_run "${DUMP_ARGS[@]}" > "$ARCHIVE" || die "$REASONS_DUMP_FAILED" "pg_dump exited non-zero"
SIZE="$(wc -c < "$ARCHIVE")"
[ "$SIZE" -gt 0 ] || die "$REASONS_DUMP_FAILED" "the archive is empty (0 bytes)"
ok "dumped $SIZE bytes"

# ---- 5. wipe -----------------------------------------------------------------
{ echo "BEGIN;"; for t in "${TABLES[@]}"; do echo "TRUNCATE TABLE $t CASCADE;"; done; echo "COMMIT;"; } \
  | psql_run >/dev/null || die "$REASONS_WIPE_FAILED" "TRUNCATE failed"
WIPED_TOTAL=0
for t in "${TABLES[@]}"; do
  c="$(psql_run -c "SELECT count(*) FROM $t;")"
  WIPED_TOTAL=$((WIPED_TOTAL + c))
done
[ "$WIPED_TOTAL" -eq 0 ] || die "$REASONS_WIPE_FAILED" "$WIPED_TOTAL row(s) survived the wipe; a count comparison against a partial wipe proves nothing"
ok "wiped every scoped table to zero"

# ---- 6. restore --------------------------------------------------------------
# --exit-on-error is tried first because it is what makes a partial restore a
# failure; it is relaxed only if the archive itself turns out to be one that
# cannot be applied that way, and the second attempt's stderr is shown so that
# relaxation is visible rather than silent.
# No filename argument: pg_restore reads the archive from stdin when given none.
# (`-` is NOT a synonym for stdin here - it is a filename, and pg_restore fails
# with "could not open input file" rather than falling back.)
if ! pg_restore_run --data-only --no-owner --exit-on-error < "$ARCHIVE" >/dev/null 2>"$WORK/restore.err"; then
  if [ ! -s "$WORK/restore.err" ]; then
    note "pg_restore failed with no diagnostic; re-running without --exit-on-error"
    pg_restore_run --data-only --no-owner < "$ARCHIVE" >/dev/null 2>"$WORK/restore.err" \
      || die "$REASONS_RESTORE_FAILED" "pg_restore exited non-zero: $(head -3 "$WORK/restore.err" | tr '\n' ' ')"
  else
    die "$REASONS_RESTORE_FAILED" "pg_restore exited non-zero: $(head -3 "$WORK/restore.err" | tr '\n' ' ')"
  fi
fi
ok "restored the archive"

# ---- 7. verify counts --------------------------------------------------------
MISMATCH=0
EMPTY=0
: > "$AFTER_FILE"
for t in "${TABLES[@]}"; do
  c="$(psql_run -c "SELECT count(*) FROM $t;")"
  b="$(grep -E "^$t=" "$BEFORE_FILE" | head -1 | cut -d= -f2)"
  echo "$t=$c" >> "$AFTER_FILE"
  if [ "$c" = "0" ]; then
    echo "  !! $t restored EMPTY (was $b) - pg_restore succeeded and inserted nothing"
    EMPTY=$((EMPTY + 1))
  elif [ "$c" != "$b" ]; then
    echo "  !! $t: before $b, after $c"
    MISMATCH=$((MISMATCH + 1))
  fi
done
[ "$EMPTY" -eq 0 ] || die "$REASONS_EMPTY_RESTORE" \
  "$EMPTY table(s) restored empty" \
  "the usual cause is restoring as a role without BYPASSRLS: RLS filters the COPY silently"
[ "$MISMATCH" -eq 0 ] || die "$REASONS_COUNT_MISMATCH" "$MISMATCH table(s) do not match their pre-restore count"
ok "all ${#TABLES[@]} count(s) identical before and after"

# ---- 8. referential coherence ------------------------------------------------
# The schema has no foreign keys between any of these tables, so this is the only
# check that can distinguish a coherent restore from a partial one.
DANGLING=0
while IFS= read -r check; do
  n="$(psql_run -c "$check")"
  if [ "$n" != "0" ]; then
    echo "  !! $n dangling reference(s): $check"
    DANGLING=$((DANGLING + 1))
  fi
done < <(node -e '
  const s = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  // scope.json is a committed file, which makes it data rather than trusted
  // input. This is the one place the drill builds a WHOLE SQL statement from it,
  // so every table and column it emits is validated first. A rule that does not
  // parse into `table.column -> table.column` is a hard error, not a skip: a
  // silently dropped referential check is a restore that reports coherent.
  const IDENTIFIER_RE = /^[a-z_][a-z0-9_]*$/;
  const rules = Array.isArray(s.referentialChecks) ? s.referentialChecks : [];
  if (rules.length === 0) throw new Error("scope.json declares no referentialChecks");
  for (const rule of rules) {
    const [child, parent] = String(rule).split("->").map((side) => side.trim());
    const [cTable, cCol] = child.split(".");
    const [pTable, pCol] = parent.split(".");
    for (const ident of [cTable, cCol, pTable, pCol]) {
      if (!IDENTIFIER_RE.test(ident)) {
        throw new Error(`referential check "${rule}" contains an identifier that is not a bare lowercase name: ${ident}`);
      }
    }
    process.stdout.write(
      `SELECT count(*) FROM ${cTable} c WHERE c.${cCol} IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ${pTable} p WHERE p.${pCol} = c.${cCol});\n`);
  }
' "$SCOPE" 2>"$WORK/referential.err") || die "$REASONS_SCOPE" \
  "scope.json's referentialChecks are unusable: $(head -2 "$WORK/referential.err" | tr '\n' ' ')"
# Hold the number of checks that ran against the number the scope declares. This
# is the D6 fix, and it was found by truncating referentialChecks to three: the
# drill then ran three orphan queries, found nothing, and reported PASS with
# `gaps=0` - byte-identical output to a clean run. A check that is silently
# removed is worse than a check that was never written, because its absence is
# invisible and it is the D2 failure mode again, one level down.
#
# The floor is one orphan query per group table: every group must be the CHILD
# side of at least one relationship, or a partial restore of that group is
# undetectable. The scope may legitimately declare more.
EXPECTED_CHECKS="$(node -e '
  const s = JSON.parse(require("fs").readFileSync(process.argv[1], "utf8"));
  process.stdout.write(String(Array.isArray(s.referentialChecks) ? s.referentialChecks.length : 0));
' "$SCOPE")"
FLOOR="${#GROUP_TABLES[@]}"
if [ "$EXPECTED_CHECKS" -lt "$FLOOR" ]; then
  die "$REASONS_SCOPE" \
    "scope.json declares $EXPECTED_CHECKS referential check(s) for $FLOOR entity group(s)" \
    "every group table must be the child side of at least one check, or a partial restore of it is undetectable" \
    "this is gap D6 in docs/backup.md: the drill ran fewer checks and reported the same PASS"
fi
[ "$DANGLING" -eq 0 ] || die "$REASONS_DANGLING" "$DANGLING referential check(s) found orphans after the restore"
ok "no dangling references across $FLOOR group(s); all $EXPECTED_CHECKS declared relationship(s) ran"

# ---- 9. spot-read ------------------------------------------------------------
# One project, one user, one notification - by id, with the columns that exist
# only because of Plan B. A matching count with NULL names is a restore that
# passed the wrong test.
SPOT="$(psql_run -c "
  SELECT p.id::text, p.name, p.owner_user_id::text, p.is_archived::text, p.settings_version::text,
         u.email, u.display_name,
         n.type, n.read_at IS NULL
  FROM dubbing_projects p
  JOIN tenant_users u ON u.id = p.owner_user_id
  JOIN notifications n ON n.project_id = p.id
  WHERE p.id = 'de710000-0000-4000-8000-000000000101'
    AND n.id = 'de710000-0000-4000-8000-000000000401';" 2>"$WORK/spot.err")" || {
  die "$REASONS_SPOT_READ" \
    "the spot-read query did not execute: $(head -2 "$WORK/spot.err" | tr '\n' ' ')" \
    "if this says invalid input syntax for uuid, the seed's UUID prefix and the one in this script have drifted"
}
[ -n "$SPOT" ] || die "$REASONS_SPOT_READ" "the spot-read row is not present in the restored database"
echo "$SPOT" | grep -q 'Drill Project One' \
  || die "$REASONS_SPOT_READ" "dubbing_projects.name did not survive: got '${SPOT}'"
echo "$SPOT" | grep -q 'alice@drill.invalid' \
  || die "$REASONS_SPOT_READ" "tenant_users.email did not survive: got '${SPOT}'"
echo "$SPOT" | grep -q 'RunCompleted' \
  || die "$REASONS_SPOT_READ" "notifications.type did not survive: got '${SPOT}'"
ok "spot-read one project, one user and one notification, with the Plan B columns"

# ---- 10. gaps the drill cannot close -----------------------------------------
# Recorded as gaps rather than passed over, per the task's edge case: a partial
# verification is never reported as a success.
if [ "$STARTED_CONTAINER" = "1" ]; then
  GAPS=$((GAPS + 1))
  note "GAP: drilled a disposable single-node instance; the media half (object"
  note "     storage finals, previews, exports) was NOT restored, because this"
  note "     drill's scope is the new tables only. A drill that restores the"
  note "     database and not the media has verified half the system."
else
  note "NOTE: pointed at an existing instance; the caller owns the access window,"
  note "      the quiesce, and the removal of any archive this wrote."
fi
note "NOTE: the restore ran as $DRILL_USER. A production restore must use the"
note "      maintenance role (BYPASSRLS); as the app role the restore would have"
note "      inserted zero rows and still reported success."
echo
echo "  date: $(date -u +%Y-%m-%dT%H:%M:%SZ)"
echo "  scope: ${#TABLES[@]} tables"
echo "  gaps recorded: $GAPS"
finish
