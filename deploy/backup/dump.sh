#!/usr/bin/env bash
# The PostgreSQL backup dump for the CronJob in deploy/k8s/backup-cronjob.yaml
# (Task 047, instruction 1).
#
# WHAT THIS IS
# ------------
# The job that actually produces the SELECTABLE backup artefact: a
# `pg_dump -Fc` archive of the seven new durable-entity groups and their parents,
# uploaded to the versioned object-storage bucket. PostgreSQL base backup plus
# WAL archiving is the primary mechanism and the 5-minute RPO; this exists
# because a PITR needs a provider CLI and a fresh instance, and an operator at
# 03:00 needs something they can restore from without either.
#
# THE TABLE LIST IS THE DELIVERABLE, NOT A DETAIL
# ----------------------------------------------
# Plan B added seven durable entity groups. Before Task 047 there was no job at
# all, so `deploy/backup/scope.json` described a coverage that nothing produced -
# the file's own comment claimed "deploy/k8s/backup-cronjob.yaml dumps exactly
# `tables`" and the file did not exist. The list below is therefore explicit and
# grouped, one block per group, with the group id in a comment. It is duplicated
# in `scope.json` (the coverage record), in the CronJob's annotations (the
# operator-facing checklist) and in `tools/backup-policy.test.mjs` (a
# REQUIRED_GROUPS list that does not come from any of the other two).
# `scripts/check-backup-policy.mjs` fails the release when the four disagree.
# That duplication is deliberate; see docs/backup.md, "The single source of
# truth".
#
# WHY THERE IS NO SQL IN HERE
# ---------------------------
# `pg_dump` is handed `--table=` arguments, never a query, and every invocation
# is an argv array. There is no identifier to validate because there is no
# identifier in a string that a shell could reinterpret.
#
# WHY THE DSN IS PARSED HERE AND NOT IN THE MANIFEST
# ---------------------------------------------------
# The ExternalSecret holds an ADO.NET connection string
# (`dubbing-secrets: maintenance-connection`), because that is what every other
# workload consumes. `pg_dump` speaks libpq. Rather than put a DSN in the
# manifest, or a second secret, this normalises either form into PG* variables
# and REFUSES anything it does not recognise: a silently-misparsed DSN produces
# a dump of the wrong database, which is the worst artefact this job can make.
#
# WHY THE ROLE IS CHECKED BEFORE THE DUMP
# ---------------------------------------
# `pg_restore` reports success after inserting zero rows, and the usual cause is
# restoring or dumping as a role without `BYPASSRLS`: row level security filters
# every COPY to nothing and the archive is empty. That is gap D3 in
# docs/backup.md, found there by a restore. It is cheaper to refuse to produce
# the archive than to discover the emptiness during a restore, so the role's
# BYPASSRLS attribute is asserted before `pg_dump` runs.
#
# USAGE
#   backup-dump --check                 # print the resolved group checklist, contact nothing
#   backup-dump                         # dump, then upload to object storage
#   BACKUP_DRY_RUN=1 backup-dump        # dump, verify the archive, upload nothing
#
# MACHINE-READABLE OUTPUT
#   BACKUP_DUMP_RESULT reason=<REASON> status=<PASS|FAIL> groups=<n> tables=<n> bytes=<n>
#   REASONS is a closed set; docs/ci-branch-protection.md lists it.
set -euo pipefail

REASON="OK"
STATUS="PASS"
BYTES=0

readonly REASONS_OK="OK"
readonly REASONS_INPUT="INPUT_INVALID"
readonly REASONS_CLIENT="CLIENT_UNAVAILABLE"
readonly REASONS_DSN="DSN_UNPARSEABLE"
readonly REASONS_ROLE="RESTORE_ROLE_UNSUITABLE"
readonly REASONS_DUMP="DUMP_FAILED"
readonly REASONS_EMPTY="EMPTY_ARCHIVE"
readonly REASONS_UPLOAD="UPLOAD_FAILED"

die() { REASON="$1"; STATUS="FAIL"; shift; for line in "$@"; do echo "  !! $line" >&2; done; finish; }
note() { echo "  -- $*"; }
ok()   { echo "  ok $*"; }

# The seven new durable-entity groups, then the four parents. One entry per
# group, `group-id:table`, in restore-priority order so the archive's table
# order matches the order a responder restores in.
#
# NOT named `GROUPS`. Bash reserves `GROUPS` as a special array holding the
# current user's group ids, and an assignment to it is silently ineffective:
# `GROUPS=("a:b")` leaves `GROUPS` as `(1000)`. The first version of this script
# did exactly that, so the loop below iterated the uid and reported
# `malformed group entry: 1000`. Found by RUNNING the script -- a reader cannot
# see it, and `bash -n` accepts it. It was at least a loud failure; the same
# assignment with a looser entry check would have dumped nothing and reported
# success, which is the shape of every other defect in this repository.
SCOPE_GROUPS=(
  "tenant-users:tenant_users"
  "memberships:project_memberships"
  "project-metadata:dubbing_projects"
  "preferences:user_preferences"
  "notifications:notifications"
  "activity-events:activity_events"
  "voice-preview-jobs:voice_preview_jobs"
)
SCOPE_PARENTS=(
  "tenants"
  "speakers"
  "voice_profiles"
)

# Archive layout under the bucket. Versioned storage keeps every hourly dump for
# the retention window in docs/dr/backup-restore.md; the key is a pure function
# of the run time so an operator can find an archive by timestamp.
BACKUP_DIR="${BACKUP_DIR:-/backup}"
BACKUP_PREFIX="${BACKUP_PREFIX:-postgres/new-entities}"
BACKUP_DRY_RUN="${BACKUP_DRY_RUN:-0}"
CHECK_ONLY=0

# TABLES is resolved by `resolve_tables`, called from the checklist section BELOW
# the function definitions. It was a bare loop here in the first version, and
# `die` calls `finish` - a function that had not been defined yet at that point -
# so the script's first failure died with `finish: command not found` and exit
# 127 instead of reporting the reason that actually went wrong. Same class of bug
# as the `GROUPS` assignment above: nothing about reading the file reveals either
# one, and `bash -n` accepts both. Only running it does.
TABLES=()

finish() {
  echo
  echo "BACKUP_DUMP_RESULT reason=${REASON} status=${STATUS} groups=${#SCOPE_GROUPS[@]} tables=${#TABLES[@]} bytes=${BYTES}"
  [ "$STATUS" = "PASS" ] && exit 0
  exit 1
}

# Resolve the table list once, after every function it might call exists. Every
# later use is an element of TABLES, never a re-parse of a string.
resolve_tables() {
  for entry in "${SCOPE_GROUPS[@]}"; do
    case "$entry" in
      *:*) TABLES+=("${entry#*:}") ;;
      *) die "$REASONS_INPUT" "malformed group entry (expected group-id:table): $entry" ;;
    esac
  done
  for table in "${SCOPE_PARENTS[@]}"; do
    TABLES+=("$table")
  done
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --check) CHECK_ONLY=1 ;;
    -h|--help) sed -n '2,60p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) die "$REASONS_INPUT" "unknown argument: $1" ;;
  esac
  shift
done

# ---- 0. the checklist ---------------------------------------------------------
# Printed on every run, including the failure paths, because this list is what a
# responder reads out of a failed Job log to answer "what was this supposed to
# cover?".
echo "== backup dump (Task 047) =="
resolve_tables
GROUP_COUNT=0
for entry in "${SCOPE_GROUPS[@]}"; do
  GROUP_COUNT=$((GROUP_COUNT + 1))
  echo "  [$GROUP_COUNT/${#SCOPE_GROUPS[@]}] ${entry%%:*} -> ${entry#*:}"
done
echo "  [parents] ${SCOPE_PARENTS[*]}"

if [ "$CHECK_ONLY" = "1" ]; then
  ok "resolved ${#SCOPE_GROUPS[@]} group(s) and ${#TABLES[@]} table(s); no connection was opened"
  finish
fi

# ---- 1. the connection string -------------------------------------------------
# `BACKUP_PG_DSN` is an ADO.NET string (`Host=...;Port=...;Database=...`) or a
# libpq connection string (`host=... port=...`). Both are accepted because both
# appear in this repository; anything else is refused rather than guessed at.
BACKUP_PG_DSN="${BACKUP_PG_DSN:-}"
[ -n "$BACKUP_PG_DSN" ] || die "$REASONS_DSN" \
  "BACKUP_PG_DSN is empty" \
  "it must be the maintenance-role connection string from dubbing-secrets: maintenance-connection"

# The client must be there before anything else, and its absence is a FAILURE
# with a reason, not a `command not found` and exit 127.
#
# `set -e` does not help here: a `command not found` from inside a command
# substitution is reported by the shell, and the script died with 127 and no
# `BACKUP_DUMP_RESULT` line at all - so a release job parsing that line finds
# nothing and has to guess. The first version of this script had exactly that
# shape and it was found by running the script on a host without a PostgreSQL
# client, which is a perfectly ordinary developer machine and not the container
# the job actually runs in.
for tool in psql pg_dump curl; do
  command -v "$tool" >/dev/null 2>&1 || die "$REASONS_CLIENT" \
    "$tool is not on PATH in this image" \
    "the job needs the PostgreSQL client and curl; the image is Dockerfile.backup, which installs both"
done

DSN_NORMALISED="${BACKUP_PG_DSN//;/ }"
read -r -a DSN_PARTS <<< "$DSN_NORMALISED"
declare -A DSN_MAP=()
for part in "${DSN_PARTS[@]}"; do
  case "$part" in
    *=*) DSN_MAP["$(printf '%s' "${part%%=*}" | tr '[:upper:]' '[:lower:]')"]="${part#*=}" ;;
    "") ;;
    *) die "$REASONS_DSN" "the connection string has a fragment with no '=': ${part}" ;;
  esac
done
: "${PGHOST:=${DSN_MAP[host]:-}}"
: "${PGPORT:=${DSN_MAP[port]:-5432}}"
: "${PGDATABASE:=${DSN_MAP[database]:-}}"
: "${PGUSER:=${DSN_MAP[username]:-${DSN_MAP[userid]:-}}}"
: "${PGPASSWORD:=${DSN_MAP[password]:-}}"
export PGHOST PGPORT PGDATABASE PGUSER PGPASSWORD
# Never let a proxy or a stray PGOPTIONS change what the dump reaches.
unset PGOPTIONS PGSERVICE PGSSLMODE 2>/dev/null || true
export PGCONNECT_TIMEOUT="${PGCONNECT_TIMEOUT:-10}"

[ -n "$PGHOST" ] && [ -n "$PGDATABASE" ] && [ -n "$PGUSER" ] \
  || die "$REASONS_DSN" "the connection string does not resolve a host, a database and a user" \
     "refusing to guess: a dump of the wrong database is the worst artefact this job can produce"
# The password itself is never echoed, and it is not optional: this job must not
# fall back to a peer/trust connection and dump whatever the local socket serves.
[ -n "$PGPASSWORD" ] || die "$REASONS_DSN" "the connection string carries no password"
note "target ${PGUSER}@${PGHOST}:${PGPORT}/${PGDATABASE} (password not logged)"

# ---- 2. the role must be able to see the rows ---------------------------------
# D3 in docs/backup.md, pre-empted rather than discovered during a restore.
BYPASSRLS="$(psql -v ON_ERROR_STOP=1 -q -t -A -c \
  "SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user;")"
[ "$BYPASSRLS" = "t" ] || die "$REASONS_ROLE" \
  "the connection's role does not have BYPASSRLS" \
  "an archive taken without it is EMPTY on every row-level-security table and pg_restore reports success" \
  "use the maintenance role (dubbing-secrets: maintenance-connection), never the app role"
ok "role ${PGUSER} has BYPASSRLS: the archive will not be silently empty"

# ---- 3. dump ------------------------------------------------------------------
# Custom format (`-Fc`): the only format pg_restore can restore selectively from,
# and the format scripts/restore-drill.sh proves round-trips.
DUMP_ARGS=(--format=custom --data-only --no-owner --no-privileges --compress=6)
for table in "${TABLES[@]}"; do
  case "$table" in
    [a-z_]*) ;;
    *) die "$REASONS_INPUT" "table name is not a bare lowercase identifier: $table" ;;
  esac
  DUMP_ARGS+=(--table="$table")
done

mkdir -p "$BACKUP_DIR" 2>/dev/null || die "$REASONS_INPUT" "BACKUP_DIR is not writable: $BACKUP_DIR"
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
ARCHIVE="$BACKUP_DIR/new-entities-${STAMP}.dump"
pg_dump "${DUMP_ARGS[@]}" > "$ARCHIVE" || die "$REASONS_DUMP" "pg_dump exited non-zero"

BYTES="$(wc -c < "$ARCHIVE")"
BYTES="${BYTES// /}"
[ "$BYTES" -gt 0 ] || die "$REASONS_EMPTY" "the archive is 0 bytes" \
  "pg_dump can exit 0 with no rows; an empty archive restores 'successfully'"
ok "dumped ${BYTES} bytes over ${#TABLES[@]} table(s)"

# The archive header states the format and version, which is what makes it
# self-describing: an operator can check what they are holding without the
# provider and without this repository.
# `case` rather than a pipe: `head` closing early on a five-byte read is a
# SIGPIPE waiting to happen, and `set -o pipefail` would turn that into a
# failure of a check that passed.
ARCHIVE_MAGIC="$(head -c 5 "$ARCHIVE")"
case "$ARCHIVE_MAGIC" in
  PGDMP*) ;;
  *) die "$REASONS_EMPTY" "the archive does not begin with PGDMP, so it is not a pg_dump custom-format archive" ;;
esac
ok "archive header verified (PGDMP, custom format)"

# ---- 4. upload ----------------------------------------------------------------
# Versioned object storage, one object per run, key is a pure function of the
# timestamp. The PUT is SigV4-signed by curl: an unsigned PUT to a private
# bucket returns 403 and the archive is then only on the pod's ephemeral disk,
# which is exactly the failure this step exists to prevent - so a non-2xx is a
# failure of the job, not a warning in its log.
if [ "$BACKUP_DRY_RUN" = "1" ]; then
  ok "BACKUP_DRY_RUN=1: the archive was not uploaded and the pod is about to discard it"
  finish
fi

: "${BACKUP_STORAGE_ENDPOINT:?BACKUP_STORAGE_ENDPOINT is required unless BACKUP_DRY_RUN=1}"
: "${BACKUP_STORAGE_BUCKET:?BACKUP_STORAGE_BUCKET is required unless BACKUP_DRY_RUN=1}"
: "${BACKUP_STORAGE_ACCESS_KEY:?BACKUP_STORAGE_ACCESS_KEY is required unless BACKUP_DRY_RUN=1}"
: "${BACKUP_STORAGE_SECRET_KEY:?BACKUP_STORAGE_SECRET_KEY is required unless BACKUP_DRY_RUN=1}"
BACKUP_STORAGE_REGION="${BACKUP_STORAGE_REGION:-us-east-1}"
KEY="${BACKUP_PREFIX}/${STAMP}.dump"

HTTP_CODE="$(curl --silent --show-error --output /dev/null --write-out '%{http_code}' \
  --request PUT \
  --aws-sigv4 "aws:amz:${BACKUP_STORAGE_REGION}:s3" \
  --user "${BACKUP_STORAGE_ACCESS_KEY}:${BACKUP_STORAGE_SECRET_KEY}" \
  --header "content-type: application/octet-stream" \
  --data-binary "@${ARCHIVE}" \
  "${BACKUP_STORAGE_ENDPOINT%/}/${BACKUP_STORAGE_BUCKET}/${KEY}")" \
  || die "$REASONS_UPLOAD" "the signed PUT to ${BACKUP_STORAGE_ENDPOINT} did not complete"
case "$HTTP_CODE" in
  2*) ok "uploaded ${BYTES} bytes to ${BACKUP_STORAGE_BUCKET}/${KEY} (HTTP ${HTTP_CODE})" ;;
  403) die "$REASONS_UPLOAD" "HTTP 403: the access key cannot write this bucket" \
         "an unuploaded archive is on ephemeral disk and is gone with the pod" ;;
  *) die "$REASONS_UPLOAD" "HTTP ${HTTP_CODE} uploading ${BACKUP_STORAGE_BUCKET}/${KEY}" ;;
esac
finish
