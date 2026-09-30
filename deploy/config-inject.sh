#!/usr/bin/env bash
# Per-environment frontend config injection (Task 043, instruction 2).
#
# WHAT THIS DOES
# --------------
# Produces two artefacts from one environment's values:
#
#   1. frontend/.env.production   - the build-time VITE_* variables, and
#   2. frontend/dist/version.json  - the document the CDN serves at
#                                    /version.json, which the running client
#                                    compares against its own baked tag (R2).
#
# Both are GENERATED and neither is committed. That is the point: the failure
# this prevents is a build that bakes one environment's values and is deployed to
# another, and the cheapest way to make that impossible is for the per-
# environment file to have no committed form at all.
#
# WHY ALLOWLISTED, NOT BLACKLISTED
# --------------------------------
# Vite inlines every VITE_* value into the public bundle. The bundle is
# downloadable and CDN-cached, so everything here is public. An allowlist refuses
# a variable nobody has decided is safe; a blacklist only refuses the names
# somebody thought of. The allowlist lives in frontend/src/config/env.ts and
# scripts/vite-env-audit.sh re-checks the output of this script, so the two agree
# by construction rather than by comment.
#
# USAGE
# -----
#   bash deploy/config-inject.sh --env staging --release v1.4.2
#   bash deploy/config-inject.sh --env prod --release v1.4.2 --commit "$SHA"
#   bash deploy/config-inject.sh --env staging --release v1.4.2 --check
#
# Values come from the environment, never from this file. There is no default
# for anything that names a real deployment: a missing value is an error, not a
# fallback, because a fallback here is an index.html pointing at the wrong API.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ENVIRONMENT=""
RELEASE=""
COMMIT="${DUBBING_COMMIT:-}"
OPENAPI_VERSION=""
BUILT_AT=""
CHECK_ONLY=0
OUT_DIR="$ROOT/frontend/dist"

# The allowlist, duplicated from DEPLOY_CONFIG_ALLOWLIST in
# frontend/src/config/env.ts. Duplicated deliberately: this script runs in the
# image build before the node layer exists in some pipelines, so it cannot depend
# on node to read a TypeScript constant. The two are kept in step by
# `scripts/vite-env-audit.sh --allowlist-sync`, which FAILS when they diverge -
# a comment saying "keep in step with" is not a check.
readonly ALLOWLIST=(
  VITE_API_BASE_URL
  VITE_CDN_ORIGIN
  VITE_VERSION_TAG
  VITE_APP_VERSION
  VITE_SSE_ENABLED
  VITE_TELEMETRY_ENABLED
)

# Same list as SECRET_NAME_MARKERS in frontend/src/config/env.ts.
readonly SECRET_NAME_RE='SECRET|KEY|TOKEN|PASSWORD|PASSWD|CREDENTIAL|PRIVATE|SIGNING|PASSPHRASE'

have() { command -v "$1" >/dev/null 2>&1; }

usage() {
  cat <<'EOF'
usage: deploy/config-inject.sh --env <name> [--release <tag>] [options]

  --env <name>           required. staging | prod | whatever label your CDN uses.
  --release <tag>        the build tag. Also read from $DUBBING_RELEASE.
  --commit <sha>         full commit sha. Also read from $DUBBING_COMMIT.
  --openapi-version <v>  the API document version. Read from the committed
                         OpenAPI bundle when omitted, and that is the default on
                         purpose: reading it is what makes R2 mechanical.
  --built-at <iso>       UTC build timestamp. Defaults to now.
  --out-dir <dir>        where version.json is written. Default frontend/dist.
  --check                evaluate everything, write nothing, exit non-zero on a
                         problem. This is what CI runs before the build.
EOF
}

while [ "$#" -gt 0 ]; do
  case "$1" in
    --env) ENVIRONMENT="${2:?--env needs a value}"; shift 2 ;;
    --release) RELEASE="${2:?--release needs a value}"; shift 2 ;;
    --commit) COMMIT="${2:?--commit needs a value}"; shift 2 ;;
    --openapi-version) OPENAPI_VERSION="${2:?--openapi-version needs a value}"; shift 2 ;;
    --built-at) BUILT_AT="${2:?--built-at needs a value}"; shift 2 ;;
    --out-dir) OUT_DIR="${2:?--out-dir needs a value}"; shift 2 ;;
    --check) CHECK_ONLY=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) echo "config-inject.sh: unknown argument $1" >&2; usage >&2; exit 2 ;;
  esac
done

if [ -z "$ENVIRONMENT" ]; then
  echo "::error::--env is required. There is deliberately no default: an env file with no" \
       "environment is how a staging build reaches production." >&2
  exit 2
fi

# --- the OpenAPI version, read from the committed bundle -------------------------
# Read rather than supplied by default, and that default is load-bearing: the
# value baked into the frontend and the value the API reports then come from the
# same file, so a bundle regenerated without a frontend rebuild cannot produce a
# disagreement by accident. R2 is `/version.json` and `/version` agreeing.
BUNDLE="$ROOT/src/DubbingPlatform.Api/OpenApi/openapi.v1.json"
if [ -z "$OPENAPI_VERSION" ]; then
  if [ ! -f "$BUNDLE" ]; then
    echo "::error::$BUNDLE is missing, so info.version cannot be read and the frontend and" \
         "API /version endpoints cannot be proven to agree." >&2
    exit 2
  fi
  if have node; then
    OPENAPI_VERSION="$(node -e '
      const fs = require("fs");
      try {
        const doc = JSON.parse(fs.readFileSync(process.argv[1], "utf8"));
        process.stdout.write(String((doc && doc.info && doc.info.version) || ""));
      } catch {
        process.stdout.write("");
      }
    ' "$BUNDLE")"
  elif have python3; then
    OPENAPI_VERSION="$(python3 -c '
import json, sys
try:
    with open(sys.argv[1], encoding="utf-8") as handle:
        sys.stdout.write(str(json.load(handle).get("info", {}).get("version", "")))
except Exception:
    sys.stdout.write("")
' "$BUNDLE")"
  else
    echo "::error::reading $BUNDLE needs node or python3." >&2
    exit 2
  fi
fi

if [ -z "$OPENAPI_VERSION" ]; then
  echo "::error::could not read info.version from $BUNDLE (or from --openapi-version)." >&2
  exit 2
fi

# --- environment values ------------------------------------------------------------
# Read with `${VAR:-}` rather than bare `$VAR`: under `set -u` a bare reference
# to an unset variable aborts the script, which turns a missing configuration
# into a crash with no message naming the variable. (The same class of bug is
# recorded in the 042 report as `$TMPDIR: unbound variable`.)
API_BASE_URL="${VITE_API_BASE_URL:-}"
CDN_ORIGIN="${VITE_CDN_ORIGIN:-}"
RELEASE="${RELEASE:-${DUBBING_RELEASE:-}}"
SSE_ENABLED="${VITE_SSE_ENABLED:-true}"
TELEMETRY_ENABLED="${VITE_TELEMETRY_ENABLED:-false}"

if [ -z "$RELEASE" ]; then
  echo "::error::no release tag. Pass --release or set DUBBING_RELEASE. The build tag is what" \
       "the client compares against /version.json, and a build with no tag can never agree." >&2
  exit 2
fi
# The application version tracks the release tag with any leading `v` removed, so
# the footer and the release share one input rather than two that can disagree.
APP_VERSION="${RELEASE#v}"

# An uncommitted build legitimately has no commit. It is recorded as CHANGE_ME
# rather than omitted, so /version.json always has the same shape and a client
# can tell "unstamped" from "field missing".
if [ -z "$COMMIT" ]; then
  COMMIT="CHANGE_ME"
fi
if [ -z "$BUILT_AT" ]; then
  BUILT_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
fi

# --- validation ---------------------------------------------------------------------
PROBLEMS=0
problem() { echo "::error::$1" >&2; PROBLEMS=$((PROBLEMS + 1)); }

require_url() { # name value
  if [ -z "$2" ]; then
    problem "$1 is not set for environment '$ENVIRONMENT'. Every environment must name its own origin; a default here is a bundle pointing at the wrong deployment."
    return
  fi
  case "$2" in
    https://*|http://localhost*|http://127.0.0.1*) : ;;
    *) problem "$1 must be an absolute http(s) URL, got '$2'." ;;
  esac
}

# Value shapes. Same list as SECRET_VALUE_PATTERNS in frontend/src/config/env.ts,
# written as EREs so this script needs no node at build time. Entropy is
# deliberately absent: a hashed bundle filename is high-entropy, and a rule that
# flags entropy fires on every build artefact it is pointed at.
check_value() { # name value
  local name="$1" value="$2"
  if [ -z "$value" ]; then
    problem "$name is empty. An empty injected value passes an 'is it configured' check and then requests no origin at runtime."
    return
  fi
  if printf '%s' "$value" | grep -qF -- '-----BEGIN RSA PRIVATE KEY-----' \
     || printf '%s' "$value" | grep -qF -- 'PRIVATE KEY-----'; then
    problem "$name carries a PEM private key block."
  fi
  if printf '%s' "$value" | grep -Eq '^eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}$'; then
    problem "$name carries a JWT."
  fi
  if printf '%s' "$value" | grep -Eqi '(Host|Server|Data Source)[[:space:]]*=[^;]+;.*(Password|Pwd)[[:space:]]*='; then
    problem "$name carries a connection string with a password."
  fi
  if printf '%s' "$value" | grep -Eq 'amqps?://[^/:@]+:[^/@]+@'; then
    problem "$name carries a broker URI with inline credentials."
  fi
  if printf '%s' "$value" | grep -Eq 's3(a)?://[^/:@]+:[^/@]+@'; then
    problem "$name carries an object-storage URI with inline credentials."
  fi
  if printf '%s' "$value" | grep -Eqi '[?&;](password|pwd|secret|token|api[-_]?key|signature|sig)[[:space:]]*='; then
    problem "$name carries an inline credential parameter."
  fi
  if printf '%s' "$value" | grep -Eq '(AKIA|ASIA)[0-9A-Z]{16}'; then
    problem "$name carries an AWS access key id."
  fi
  if printf '%s' "$value" | grep -Eq 'Bearer [A-Za-z0-9._~+/-]{20,}=*'; then
    problem "$name carries a bearer credential."
  fi
}

# The name check first: a variable whose NAME is forbidden is a decision to make
# by editing the allowlist, and reporting it after a value check would let a
# clean value wave through a name that must not exist.
for name in "${ALLOWLIST[@]}"; do
  upper="$(printf '%s' "$name" | tr '[:lower:]' '[:upper:]')"
  if printf '%s' "$upper" | grep -Eq "$SECRET_NAME_RE"; then
    problem "allowlisted variable $name has a secret-shaped NAME ($SECRET_NAME_RE). Remove it from the allowlist and name the real, non-secret variable."
  fi
done

require_url VITE_API_BASE_URL "$API_BASE_URL"
require_url VITE_CDN_ORIGIN "$CDN_ORIGIN"
check_value VITE_API_BASE_URL "$API_BASE_URL"
check_value VITE_CDN_ORIGIN "$CDN_ORIGIN"
check_value VITE_VERSION_TAG "$RELEASE"
check_value VITE_APP_VERSION "$APP_VERSION"

case "$SSE_ENABLED" in true|false) : ;; *) problem "VITE_SSE_ENABLED must be true or false, got '$SSE_ENABLED'." ;; esac
case "$TELEMETRY_ENABLED" in true|false) : ;; *) problem "VITE_TELEMETRY_ENABLED must be true or false, got '$TELEMETRY_ENABLED'." ;; esac

if [ "$PROBLEMS" -ne 0 ]; then
  echo "config-inject: $PROBLEMS problem(s); nothing was written." >&2
  echo "CONFIG_INJECT_RESULT reason=INVALID status=FAIL env=$ENVIRONMENT" >&2
  exit 1
fi

# --- render ---------------------------------------------------------------------------
# The order is the allowlist order, and the header says the file is generated.
# scripts/vite-env-audit.sh re-parses this output and re-runs the same rules, so
# a hand-edit between this script and the build is caught rather than shipped.
ENV_CONTENT="# Generated by deploy/config-inject.sh for environment '$ENVIRONMENT'. DO NOT EDIT.
# Regenerate with: bash deploy/config-inject.sh --env $ENVIRONMENT --release <tag>
# Only allowlisted VITE_* values may appear here; Vite inlines this file into a
# PUBLIC bundle. See frontend/src/config/env.ts.
VITE_API_BASE_URL=$API_BASE_URL
VITE_CDN_ORIGIN=$CDN_ORIGIN
VITE_VERSION_TAG=$RELEASE
VITE_APP_VERSION=$APP_VERSION
VITE_SSE_ENABLED=$SSE_ENABLED
VITE_TELEMETRY_ENABLED=$TELEMETRY_ENABLED
"

# `environment` is not in the client's zod schema. It is written anyway, and
# deliberately: the schema strips unknown keys rather than failing, so an older
# client reading a newer document still parses, and an operator reading the file
# off the CDN can see which deployment served it. Adding a field is therefore
# backward-compatible while renaming or removing one is not.
VERSION_CONTENT="{
  \"release\": \"$RELEASE\",
  \"commit\": \"$COMMIT\",
  \"openapiVersion\": \"$OPENAPI_VERSION\",
  \"builtAtUtc\": \"$BUILT_AT\",
  \"environment\": \"$ENVIRONMENT\"
}
"

if [ "$CHECK_ONLY" = "1" ]; then
  echo "config-inject: --check, nothing written. '$ENVIRONMENT' would produce:"
  printf '%s' "$ENV_CONTENT" | sed 's/^/  /'
  echo "CONFIG_INJECT_RESULT reason=OK status=PASS mode=check env=$ENVIRONMENT openapi=$OPENAPI_VERSION"
  exit 0
fi

ENV_FILE="$ROOT/frontend/.env.production"
printf '%s' "$ENV_CONTENT" > "$ENV_FILE"
# 0644, not 0600: this file is about to be baked into a public bundle, so a
# restrictive mode is theatre. The audit is the control that matters.

# /version.json goes into the build output so the CDN serves it from the same
# deployment as the bundle it describes. Writing it beside the bundle is what
# makes "the CDN serves N while the client runs N-1" detectable: the two are
# uploaded together or not at all.
mkdir -p "$OUT_DIR"
printf '%s' "$VERSION_CONTENT" > "$OUT_DIR/version.json"

echo "config-inject: wrote frontend/.env.production and ${OUT_DIR#"$ROOT"/}/version.json"
echo "  environment:  $ENVIRONMENT"
echo "  release:      $RELEASE"
echo "  commit:       $COMMIT"
echo "  openapi:      $OPENAPI_VERSION"
echo "  built at:     $BUILT_AT"
echo "CONFIG_INJECT_RESULT reason=OK status=PASS env=$ENVIRONMENT release=$RELEASE openapi=$OPENAPI_VERSION"
