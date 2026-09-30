#!/usr/bin/env bash
# Per-environment frontend config injection (Task 043, instruction 2).
#
# WHAT THIS DOES
# --------------
# Produces two artefacts from one environment's values:
#
#   1. frontend/.env.production  - the build-time VITE_* variables, which is the
#                                   file Vite actually consumes.
#   2. frontend/version.json     - the document the origin serves at
#                                   /version.json, which the running client
#                                   compares against its own baked tag (R2).
#
# BOTH ARE GENERATED and neither is committed. That is the point: the failure this
# prevents is a build that bakes one environment's values and is deployed to
# another, and the cheapest way to make that impossible is for the per-
# environment file to have no committed form at all.
#
# WHY version.json IS STAGED OUTSIDE dist/ (Task 043A)
# ---------------------------------------------------
# It used to be written straight into `frontend/dist/`, which is exactly where it
# has to end up - and which `vite build` EMPTIES. `emptyOutDir` defaults to true,
# so a `version.json` placed in `dist/` before the build is deleted by the build,
# and the image shipped without one. The hosting gate reported it as "404, as
# expected for an image built without deploy/config-inject.sh", which read as the
# documented local case and was indistinguishable from the release case: a
# release image that can never carry the document, discovered by a user with a
# stale tab rather than by a gate.
#
# So the document is staged as `frontend/version.json` - a sibling of `.env`, not a
# bundler output - and `Dockerfile.frontend` copies it into `dist/` AFTER
# `npm run build`. One owner, one shape, and a file the bundler deletes is no
# longer a build output. For a local static preview, copy it yourself:
#   cp frontend/version.json frontend/dist/
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
  VITE_ENVIRONMENT
  VITE_ENABLE_ANALYTICS
  VITE_ENABLE_DIAGNOSTICS
  VITE_ENABLE_EXPERIMENTAL_FEATURES
  VITE_SENTRY_DSN
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

# Task 043A. The environment label, the three presentation flags and the Sentry
# DSN. All of them are BUILD-TIME inputs - Vite inlines them and there is no
# runtime indirection - which is why deploy/k8s/frontend/configmap.yaml is a build
# INPUT record and is deliberately not mounted into the pods.
#
# The flags default to `false` and the DSN defaults to the inert sentinel rather
# than to the empty string, and the reason is the same one as everywhere else in
# this script: an empty injected value passes an "is it configured?" check and then
# turns a feature on in nobody's environment. `sentry-disabled` is non-empty, is
# obviously not a DSN to a human reading the rendered ConfigMap, and is what
# `frontend/src/config/env.ts`'s `isSentryDsnConfigured` recognises as off.
# `--env` is the authority for the environment label; `VITE_ENVIRONMENT` must
# either be absent or agree with it. It was read into this same variable first,
# which meant an unset `VITE_ENVIRONMENT` overwrote `--env staging` with the
# default `local` and wrote "local" into both the public bundle and
# /version.json - a staging build that announced itself as local, discovered only
# by reading the served document.
#
# Two authorities for one value is not a default, it is a way for them to
# disagree, so the disagreement is an error rather than a precedence rule.
if [ -n "${VITE_ENVIRONMENT:-}" ] && [ "$VITE_ENVIRONMENT" != "$ENVIRONMENT" ]; then
  echo "::error::VITE_ENVIRONMENT='$VITE_ENVIRONMENT' disagrees with --env '$ENVIRONMENT'." \
       "One value has one authority. Set VITE_ENVIRONMENT to the same label, or unset it and let --env provide it." >&2
  echo "CONFIG_INJECT_RESULT reason=INVALID status=FAIL env=$ENVIRONMENT" >&2
  exit 1
fi
ANALYTICS_ENABLED="${VITE_ENABLE_ANALYTICS:-false}"
DIAGNOSTICS_ENABLED="${VITE_ENABLE_DIAGNOSTICS:-false}"
EXPERIMENTAL_ENABLED="${VITE_ENABLE_EXPERIMENTAL_FEATURES:-false}"
SENTRY_DSN="${VITE_SENTRY_DSN:-sentry-disabled}"

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
  # `[^"']*` rather than `[^;]*` (Task 043A) — see the note in
  # scripts/vite-env-audit.sh. A real connection string has more than two pairs,
  # so the narrower middle class required the password to be the second one.
  if printf '%s' "$value" | grep -Eqi '(Host|Server|Data Source)[[:space:]]*=[^;[:space:]"'"'"']+;[^"'"'"']*(Password|Pwd)[[:space:]]*='; then
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
check_value VITE_ENVIRONMENT "$ENVIRONMENT"
check_value VITE_SENTRY_DSN "$SENTRY_DSN"

# The environment label is a label, not a URL and not free text. Constrained to a
# token because it ends up in a public bundle and in /version.json, and because a
# value with a space or a quote in it is a rendering bug in every consumer of both
# before it is a security problem.
case "$ENVIRONMENT" in
  ''|*[!A-Za-z0-9._-]*)
    problem "VITE_ENVIRONMENT must be a bare token ([A-Za-z0-9._-]), got '$ENVIRONMENT'. It is inlined into a public bundle and written into /version.json."
    ;;
esac

case "$SSE_ENABLED" in true|false) : ;; *) problem "VITE_SSE_ENABLED must be true or false, got '$SSE_ENABLED'." ;; esac
case "$TELEMETRY_ENABLED" in true|false) : ;; *) problem "VITE_TELEMETRY_ENABLED must be true or false, got '$TELEMETRY_ENABLED'." ;; esac

# Exactly `true` or `false`, for the reason `readBooleanFlag` gives in
# frontend/src/config/env.ts: a typo that reads as "on" is a worse outcome than a
# typo that reads as "off", so `1`, `yes` and `TRUE` are refused here rather than
# being silently normalised. This is the place a human is looking at the output.
for flag in VITE_ENABLE_ANALYTICS:"$ANALYTICS_ENABLED" \
            VITE_ENABLE_DIAGNOSTICS:"$DIAGNOSTICS_ENABLED" \
            VITE_ENABLE_EXPERIMENTAL_FEATURES:"$EXPERIMENTAL_ENABLED"; do
  case "${flag#*:}" in
    true|false) : ;;
    *) problem "${flag%%:*} must be true or false, got '${flag#*:}'." ;;
  esac
done

# The DSN is either the inert sentinel or an absolute https URL. A `javascript:`
# or a protocol-relative value here is a value the browser would act on.
case "$SENTRY_DSN" in
  sentry-disabled) : ;;
  https://*) : ;;
  *) problem "VITE_SENTRY_DSN must be 'sentry-disabled' or an absolute https:// DSN, got '$SENTRY_DSN'." ;;
esac

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
VITE_ENVIRONMENT=$ENVIRONMENT
VITE_ENABLE_ANALYTICS=$ANALYTICS_ENABLED
VITE_ENABLE_DIAGNOSTICS=$DIAGNOSTICS_ENABLED
VITE_ENABLE_EXPERIMENTAL_FEATURES=$EXPERIMENTAL_ENABLED
VITE_SENTRY_DSN=$SENTRY_DSN
"

# `environment` is not in the client's zod schema. It is written anyway, and
# deliberately: the schema strips unknown keys rather than failing, so an older
# client reading a newer document still parses, and an operator reading the file
# off the CDN can see which deployment served it. Adding a field is therefore
# backward-compatible while renaming or removing one is not.
#
# `version` and `builtAt` are the names Task 043A's hosting contract gives the
# document ({version, commit, builtAt}) and they are ADDED ALONGSIDE `release` and
# `builtAtUtc` rather than replacing them, for the same reason. They are written
# from the same two values, so a reader who uses either name gets the same answer
# and there is no second source to drift:
#
#   version    == APP_VERSION == release without its leading `v`
#   builtAt    == builtAtUtc
#
# The client's `parseRuntimeVersion` REQUIRES all six. A CDN still serving a
# four-field document therefore reports UNKNOWN rather than a false MATCH, which
# is the correct direction: a skew that cannot be evaluated must not present as
# agreement.
VERSION_CONTENT="{
  \"release\": \"$RELEASE\",
  \"version\": \"$APP_VERSION\",
  \"commit\": \"$COMMIT\",
  \"openapiVersion\": \"$OPENAPI_VERSION\",
  \"builtAtUtc\": \"$BUILT_AT\",
  \"builtAt\": \"$BUILT_AT\",
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

# /version.json is STAGED as a sibling of the env file, not written into dist/ -
# see the header. `Dockerfile.frontend` copies it into dist/ AFTER the build, and
# that ordering is load-bearing: `vite build` empties `outDir`, so a document
# written into dist/ before the build is gone afterwards. Writing it here and
# copying it later also means the bundle and the document always come from the
# same invocation, which is what makes "the origin serves N while the client runs
# N-1" detectable: the two ship together or not at all.
VERSION_FILE="$ROOT/frontend/version.json"
printf '%s' "$VERSION_CONTENT" > "$VERSION_FILE"

echo "config-inject: wrote frontend/.env.production and frontend/version.json"
echo "  environment:  $ENVIRONMENT"
echo "  release:      $RELEASE"
echo "  commit:       $COMMIT"
echo "  openapi:      $OPENAPI_VERSION"
echo "  built at:     $BUILT_AT"
echo "CONFIG_INJECT_RESULT reason=OK status=PASS env=$ENVIRONMENT release=$RELEASE openapi=$OPENAPI_VERSION"
