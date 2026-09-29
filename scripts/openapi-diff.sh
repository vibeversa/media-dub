#!/usr/bin/env bash
# OpenAPI breaking-change gate (Task 042).
#
# THE RULE
# --------
# A pull request that changes the API contract must not merge unless:
#   1. it introduces no breaking change, and
#   2. it bumps `info.version` on the bundle.
#
# Both halves are needed and they catch different mistakes. (1) stops a v1 client
# from being broken by a v2 author. (2) stops unversioned accretion - the
# failure mode where a contract collects a dozen minor edits and nobody can say
# which release changed what, so a rollback has no target. (2) fires on an
# *additive* change too, because "additive" is a claim about clients, not a claim
# about nothing having changed.
#
# WHY A PINNED EXTERNAL TOOL
# -------------------------
# `oasdiff` is the authority for (1): it knows the OpenAPI breaking-change rules
# (enum widening, request/response compatibility, `nullable`, `default`, tag
# changes) far better than a hand-rolled comparator, and it is maintained.
#
# But an unpinned diff tool is a silent gate. A new version can add a rule and
# turn every PR red - fixable. Or it can drop one and turn a breaking merge
# green - not fixable, because nothing reports it. So the version AND the
# sha256 of the release archive are pinned, the download is verified BEFORE
# execution, and the running binary's version is asserted on every single run,
# including when it came from `PATH`. A mismatch is a hard failure in all three
# cases; there is no "works well enough" path and no skip.
#
# FAIL-CLOSED, EXPLICITLY
# ----------------------
# The dangerous failure mode for any contract gate is not being red, it is being
# green because it could not run. So: an unreadable document, an unavailable
# tool, an unverified download, a tool that returned something other than the
# pinned format, and a version mismatch are all failures. There is no branch in
# this script that reports PASS without having compared two parsed documents.
#
# USAGE
#   scripts/openapi-diff.sh --base main --head HEAD
#   scripts/openapi-diff.sh --base-file base.json --head-file head.json  # offline
#   scripts/openapi-diff.sh --self-test                                  # hermetic
#
# The document is `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` - the single
# hand-checked contract bundle (docs/api-contract.md). In CI both sides come from
# git, so the comparison is against the real merged `main` and never against a
# checked-in copy that could itself be stale.
#
# EXIT CODES (mirrored by tools/openapi-gate.mjs, and documented in
# docs/ci-branch-protection.md so a required check name is predictable)
#   0  no breaking change, and the version was bumped if anything changed
#   1  a breaking change was found
#   2  a contract change with no version bump, or a version/route incoherence
#   3  the gate could not run (tool, checksum, document, or diff output)
# Every exit prints a final machine-readable line:
#   CONTRACT_GATE_RESULT reason=<REASON> status=<PASS|FAIL> exit=<n> ...
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BUNDLE_REL="src/DubbingPlatform.Api/OpenApi/openapi.v1.json"
ANALYZER="$ROOT/tools/openapi-gate.mjs"

# --- The pin -----------------------------------------------------------------
# Change the version and the digests together, in one reviewed diff. A digest for
# a *different* version is worse than no digest: it would install a binary nobody
# audited and call it verified.
#
# The digests below are the `digest` field GitHub reports for each release asset
# (oasdiff v1.11.7, published 2025-08-21). The upstream project also publishes a
# `checksums.txt` beside them; either source is acceptable, but the value must
# come from the release, never from a local build of this repository - a pin
# recorded from a machine that built the tool itself verifies nothing.
OASDIFF_VERSION="v1.11.7"
OASDIFF_SHA256_linux_amd64="97f1052365f74e6fd6f4d8fa108606e09391aebb8ecbf3b5e7a4059d54327224"
OASDIFF_SHA256_linux_arm64="6a7394ec7129ccfbfcf4837db8426198b79e933341a96adf53b0f33498846b45"
# Upstream ships ONE universal macOS archive (`darwin_all`), not a per-arch pair.
OASDIFF_SHA256_darwin_all="2aab1d33f3b9f9c28cd6c1977f63b1aa43ba83f9ab94887f3097fcac152d20a1"
OASDIFF_SHA256_windows_amd64="5327e48ac9926d8b63c63b6768b7599369f890aac768780621a8315fd3beb2cb"
OASDIFF_SHA256_windows_arm64="f29134d6485636d5a7f49783aef5f15e720a5beae5a39d60f608d8520d0325f7"

REASON="UNKNOWN"
EXIT_CODE=3
ANNOTATIONS=()

note() { printf 'openapi-diff: %s\n' "$1"; }

finish() {
  if [ "${#ANNOTATIONS[@]}" -gt 0 ]; then
    printf '%s\n' "--- annotations ---"
    for line in "${ANNOTATIONS[@]}"; do printf '%s\n' "$line"; done
  fi
  printf 'CONTRACT_GATE_RESULT reason=%s status=%s exit=%s\n' \
    "$REASON" "$([ "$EXIT_CODE" -eq 0 ] && echo PASS || echo FAIL)" "$EXIT_CODE"
  exit "$EXIT_CODE"
}

# The only way this script reports a verdict: an annotated failure.
fail_with() { # reason exit_code annotation...
  REASON="$1"; EXIT_CODE="$2"; shift 2
  ANNOTATIONS=("$@")
  finish
}

# NOTE: there is deliberately no `pass_with`. The only path to exit 0 is the
# analyzer's own exit 0, reached after it compared two parsed documents. A
# `pass_with` helper would be one more place a future edit could return 0 without
# having compared anything, which is the exact failure this script is built to
# make impossible.

# --- Arguments ---------------------------------------------------------------
BASE_REF="main"
HEAD_REF="HEAD"
BASE_FILE=""
HEAD_FILE=""
SELF_TEST=0
SUMMARY_FILE="${OPENAPI_DIFF_SUMMARY:-}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --base) BASE_REF="${2:?--base needs a ref}"; shift 2 ;;
    --head) HEAD_REF="${2:?--head needs a ref}"; shift 2 ;;
    --base-file) BASE_FILE="${2:?--base-file needs a path}"; shift 2 ;;
    --head-file) HEAD_FILE="${2:?--head-file needs a path}"; shift 2 ;;
    --summary) SUMMARY_FILE="${2:?--summary needs a path}"; shift 2 ;;
    --self-test) SELF_TEST=1; shift ;;
    -h|--help) sed -n '2,60p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *)
      fail_with "BAD_ARGUMENT" 3 "::error::openapi-diff.sh: unknown argument '$1'."
      ;;
  esac
done

if [ "$SELF_TEST" -eq 1 ]; then
  # The gate's own rules, run hermetically. `node --test` exits non-zero on any
  # failure, so this cannot report a green gate on a red test suite.
  exec node --test "$ROOT/tools/openapi-compat.test.mjs" "$ROOT/tools/openapi-gate.test.mjs"
fi

# --- Materialize both documents ---------------------------------------------
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

materialize_from_git() { # spec ref side
  local spec="$1" ref="$2" side="$3"
  if ! git -C "$ROOT" cat-file -e "$ref:$BUNDLE_REL" 2>/dev/null; then
    fail_with "DOCUMENT_MISSING" 3 \
      "::error file=$BUNDLE_REL::git has no $BUNDLE_REL at '$ref' (the $side side)." \
      "The base document is the comparison point. Without it the gate cannot run, and a gate that cannot run must not pass."
  fi
  git -C "$ROOT" show "$ref:$BUNDLE_REL" > "$spec"
}

BASE_SPEC="$WORK/base.json"
HEAD_SPEC="$WORK/head.json"
if [ -n "$BASE_FILE" ]; then
  [ -f "$BASE_FILE" ] || fail_with "DOCUMENT_UNREADABLE" 3 "::error::--base-file '$BASE_FILE' does not exist."
  cp "$BASE_FILE" "$BASE_SPEC"
else
  materialize_from_git "$BASE_SPEC" "$BASE_REF" base
fi
if [ -n "$HEAD_FILE" ]; then
  [ -f "$HEAD_FILE" ] || fail_with "DOCUMENT_UNREADABLE" 3 "::error::--head-file '$HEAD_FILE' does not exist."
  cp "$HEAD_FILE" "$HEAD_SPEC"
else
  materialize_from_git "$HEAD_SPEC" "$HEAD_REF" head
fi

# --- The diff tool -----------------------------------------------------------
sha256_of() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  elif command -v shasum >/dev/null 2>&1; then
    shasum -a 256 "$1" | cut -d' ' -f1
  else
    node -e 'const c=require("crypto"),f=require("fs");process.stdout.write(c.createHash("sha256").update(f.readFileSync(process.argv[1])).digest("hex"))' "$1"
  fi
}

# The asset name and the digest are selected by the SAME case arms, deliberately.
# An earlier version of this script derived the digest from a platform key and
# the asset name from a different one, so a macOS runner could verify the
# windows digest against the darwin archive - which always mismatches, and is
# indistinguishable in the log from a supply-chain compromise.
pinned_asset() { # echoes "<digest> <filename>"
  local sys arch
  sys="$(uname -s)"
  arch="$(uname -m)"
  case "$sys" in
    Linux)
      case "$arch" in
        x86_64|amd64) echo "$OASDIFF_SHA256_linux_amd64 oasdiff_${OASDIFF_VERSION#v}_linux_amd64.tar.gz"; return 0 ;;
        arm64|aarch64) echo "$OASDIFF_SHA256_linux_arm64 oasdiff_${OASDIFF_VERSION#v}_linux_arm64.tar.gz"; return 0 ;;
      esac
      ;;
    Darwin)
      case "$arch" in
        x86_64|arm64|amd64|aarch64) echo "$OASDIFF_SHA256_darwin_all oasdiff_${OASDIFF_VERSION#v}_darwin_all.tar.gz"; return 0 ;;
      esac
      ;;
    MINGW*|MSYS*|CYGWIN*)
      case "$arch" in
        x86_64|amd64) echo "$OASDIFF_SHA256_windows_amd64 oasdiff_${OASDIFF_VERSION#v}_windows_amd64.tar.gz"; return 0 ;;
        arm64|aarch64) echo "$OASDIFF_SHA256_windows_arm64 oasdiff_${OASDIFF_VERSION#v}_windows_arm64.tar.gz"; return 0 ;;
      esac
      ;;
  esac
  echo " "
}

assert_pinned_version() { # binary
  local reported pinned
  # oasdiff prints "oasdiff version 1.11.7"; older builds printed "oasdiff 1.11.7".
  # Both are accepted, and anything else is drift - a version string this gate
  # cannot read is a version string it cannot pin.
  reported="$("$1" --version 2>/dev/null | tr -d '\r' | tr -s ' ' | sed 's/^ *//; s/ *$//' || true)"
  reported="${reported#oasdiff}"
  reported="${reported# version}"
  reported="${reported#version}"
  reported="${reported# }"
  reported="${reported#v}"
  pinned="${OASDIFF_VERSION#v}"
  if [ "$reported" != "$pinned" ]; then
    fail_with "TOOL_VERSION_DRIFT" 3 \
      "::error::oasdiff reports '$reported' but scripts/openapi-diff.sh pins '$pinned'." \
      "A different diff tool is a different gate: it can add a rule (every PR goes red) or drop one (a breaking merge goes green) with nothing reporting either. Re-pin the version and the archive digest together, or point OASDIFF_BIN at the pinned build."
  fi
}

install_oasdiff() {
  local asset expected filename archive url actual bin
  asset="$(pinned_asset)"
  expected="${asset%% *}"
  filename="${asset##* }"

  if [ "$filename" = " " ] || [ -z "$expected" ] || [ "$expected" = " " ]; then
    fail_with "TOOL_UNAVAILABLE" 3 \
      "::error::no pinned oasdiff build for $(uname -s)/$(uname -m)." \
      "Supported today: linux amd64/arm64, darwin (universal), windows amd64/arm64. Add the release digest to scripts/openapi-diff.sh to support another platform - the gate does not fall back to an unpinned download."
    return 1
  fi

  command -v curl >/dev/null 2>&1 || {
    fail_with "TOOL_UNAVAILABLE" 3 "::error::curl is required to fetch the pinned oasdiff."; return 1; }

  archive="$WORK/oasdiff.tar.gz"
  # The base URL is overridable so an air-gapped runner can point at a mirror
  # that serves the SAME archives - the sha256 pin still applies, so a mirror
  # cannot substitute a different binary. It is not a way to skip verification.
  url="${OASDIFF_DOWNLOAD_BASE_URL:-https://github.com/oasdiff/oasdiff/releases/download}/${OASDIFF_VERSION}/${filename}"
  note "fetching pinned oasdiff $OASDIFF_VERSION ($filename)"
  if ! curl --fail --silent --show-error --location --max-time 120 --output "$archive" "$url"; then
    fail_with "TOOL_UNAVAILABLE" 3 "::error::could not download the pinned oasdiff from $url."
    return 1
  fi
  actual="$(sha256_of "$archive")"
  if [ "$actual" != "$expected" ]; then
    fail_with "CHECKSUM_MISMATCH" 3 \
      "::error::the $filename sha256 is $actual; the pin in scripts/openapi-diff.sh says $expected." \
      "The download was NOT executed. Investigate the release before re-running - an unverified binary from a pinned URL is still an unverified binary."
    return 1
  fi
  note "sha256 verified against the pin"
  tar -xzf "$archive" -C "$WORK" || { fail_with "TOOL_UNAVAILABLE" 3 "::error::could not unpack the verified oasdiff archive."; return 1; }
  bin="$WORK/oasdiff"
  [ -f "$bin" ] || bin="$WORK/oasdiff.exe"
  [ -f "$bin" ] || { fail_with "TOOL_UNAVAILABLE" 3 "::error::the verified oasdiff archive contained no oasdiff binary."; return 1; }
  chmod +x "$bin" 2>/dev/null || true
  assert_pinned_version "$bin"   # exits on drift
  OASDIFF_BIN="$bin"
}

if [ -z "${OASDIFF_BIN:-}" ]; then
  if command -v oasdiff >/dev/null 2>&1; then
    OASDIFF_BIN="$(command -v oasdiff)"
    note "using oasdiff from PATH: $OASDIFF_BIN"
  else
    install_oasdiff || true
  fi
fi
[ -n "${OASDIFF_BIN:-}" ] || fail_with "TOOL_UNAVAILABLE" 3 "::error::no usable oasdiff; the contract comparison did not run and does not pass."
# Asserted for a PATH binary too: a developer's machine must not produce a
# different verdict than CI for the same document pair.
assert_pinned_version "$OASDIFF_BIN"

# --- The breaking diff -------------------------------------------------------
# `--format json` because the reasons must become GitHub annotations. A text diff
# cannot be rendered without guessing at its grammar, and a gate whose output has
# to be guessed at eventually goes unread - which is the same as not having it.
#
# `--fail-on ERR` is REQUIRED, and this is the single most dangerous default in
# the whole gate. Without it oasdiff exits 0 whether or not it found breaking
# changes - verified against the pinned build, where a document with a removed
# path still exits 0 - and the only thing that distinguishes a clean comparison
# from a broken one is the JSON body. A gate that trusted that exit status would
# report every breaking merge as compatible. The flag makes the status honest, and
# the status is still only a cross-check: the verdict below comes from the parsed
# changes, never from `$?`.
DIFF_JSON="$WORK/breaking.json"
ERR_LOG="$WORK/breaking.err"
set +e
"$OASDIFF_BIN" breaking "$BASE_SPEC" "$HEAD_SPEC" --format json --fail-on ERR > "$DIFF_JSON" 2> "$ERR_LOG"
DIFF_STATUS=$?
set -e

if [ ! -s "$DIFF_JSON" ] && [ -s "$ERR_LOG" ]; then
  fail_with "TOOL_FAILED" 3 \
    "::error::oasdiff produced no comparison output: $(tr '\n' ' ' < "$ERR_LOG" | cut -c1-400)"
fi
# oasdiff exits 0 when nothing at ERR was found, 1 when --fail-on ERR tripped, and
# 102 on a load error. Anything outside {0,1} is a tool failure and must not be
# reported as "compatible" - which is exactly what a bare `if ! oasdiff` would do.
if [ "$DIFF_STATUS" -ne 0 ] && [ "$DIFF_STATUS" -ne 1 ]; then
  fail_with "TOOL_FAILED" 3 "::error::oasdiff exited $DIFF_STATUS (0 = clean, 1 = breaking found, 102 = spec load failure). The comparison did not complete."
fi

# --- The verdict -------------------------------------------------------------
# Everything the decision needs is handed to the analyzer: two parsed documents,
# oasdiff's own parsed output, and the refs for the annotations. It prints the
# annotations, the report and the result line, and its exit code is the gate's.
ANALYZER_ARGS=(--base "$BASE_SPEC" --head "$HEAD_SPEC"
               --breaking-json "$DIFF_JSON"
               --base-ref "$BASE_REF" --head-ref "$HEAD_REF")
[ -n "$SUMMARY_FILE" ] && ANALYZER_ARGS+=(--summary "$SUMMARY_FILE")

set +e
node "$ANALYZER" "${ANALYZER_ARGS[@]}"
ANALYZER_STATUS=$?
set -e

# The analyzer already printed its annotations, its report and a
# `CONTRACT_GATE_RESULT` line for every one of these four outcomes, so the
# script's own `finish` is not run again: a second result line saying something
# different from the first is worse than no second line. A status the analyzer
# does not define is the one case the script must speak for itself.
case "$ANALYZER_STATUS" in
  0|1|2) exit "$ANALYZER_STATUS" ;;
  *) fail_with "GATE_ERROR" 3 "::error::tools/openapi-gate.mjs exited $ANALYZER_STATUS, which is not one of its documented outcomes (0 pass, 1 breaking, 2 bump required, 3 gate error)." ;;
esac
