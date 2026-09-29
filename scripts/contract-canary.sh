#!/usr/bin/env bash
# Deliberate-break canary for the contract gate (Task 042).
#
# WHY A CANARY, AND WHY IT IS A SCRIPT RATHER THAN A "WE TRIED IT ONCE" NOTE
# -------------------------------------------------------------------------
# The task's testing requirement is a canary that proves the gate is red when it
# should be. A one-time manual canary proves the gate was red on the day somebody
# ran it. It does not prove anything about the next change to
# `scripts/openapi-diff.sh`, the next oasdiff pin, or the next change to the
# version policy - and a contract gate is exactly the kind of thing that gets
# "simplified" into a no-op by someone who never saw it fire.
#
# So the canary is executable, it runs in CI on the same runner as the real gate,
# and every one of its expectations is a deliberate break that must be rejected.
# A class whose gate stops rejecting fails this script.
#
# WHAT IT COVERS (one deliberate break per class)
#   1. no change at all             -> pass
#   2. documentation-only edit      -> pass (no version bump owed)
#   3. additive change + bump       -> pass
#   4. additive change, no bump     -> FAIL exit 2  (the openapi:version check)
#   5. removed path                 -> FAIL exit 1
#   6. request field became required-> FAIL exit 1
#   7. request field stopped being required -> FAIL exit 2 (bump owed, no client break)
#   8. tightened request enum       -> FAIL exit 1
#   9. added auth scope to a route  -> FAIL exit 1
#  10. new major, no new route      -> FAIL exit 2  (incoherent version)
#  11. version moved backwards      -> FAIL exit 2
#  12. diff tool at the wrong version -> FAIL exit 3 (TOOL_VERSION_DRIFT)
#  13. diff tool unobtainable       -> FAIL exit 3 (never a silent pass)
#
# NOTHING HERE TOUCHES THE WORKING TREE. Every mutation is applied to a copy in a
# temp directory and the gate is driven through `--base-file`/`--head-file`. A
# canary that dirties the checkout is a canary that gets skipped.
#
# EXIT CODES OF THIS SCRIPT
#   0  every class behaved as specified
#   1  a class did not
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$ROOT/scripts/openapi-diff.sh"
BUNDLE_REL="src/DubbingPlatform.Api/OpenApi/openapi.v1.json"

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
LOG_DIR="$WORK/logs"
mkdir -p "$LOG_DIR"

PASSED=0
FAILED=0

ok() { PASSED=$((PASSED + 1)); printf 'canary ok     %-24s %s\n' "$1" "$2"; }
bad() {
  FAILED=$((FAILED + 1))
  printf 'canary FAILED %-24s %s\n' "$1" "$2"
  printf '  --- tail of %s ---\n' "${3:-}"
  [ -n "${3:-}" ] && tail -20 "$3" 2>/dev/null | sed 's/^/  | /'
  return 0
}

echo "== contract-gate canary =="
echo "gate:    $GATE"
echo "oasdiff: ${OASDIFF_BIN:-<resolved by the gate: PATH, else the pinned release>}"
echo

# --- 0. The environment, before any document class ---------------------------
# Two tool classes, asserted first. If the diff tool cannot run, every document
# class fails with exit 3 and the expectations below would "pass" against a
# broken environment - the canary would be measuring the environment, not the
# gate. Verifying the tool classes first is what makes the rest of the run mean
# something.
echo "-- tool classes --"

# 12: the gate cannot obtain a diff tool at all. Not merely "none on PATH" - the
# gate is EXPECTED to install its own pinned, checksum-verified copy when PATH
# has none, and that is the correct production behaviour, so removing the PATH
# entry alone would be testing the wrong thing. The download is therefore also
# pointed at an unroutable host, so there is genuinely no way to get a tool.
#
# A wholesale PATH replacement is deliberately NOT used, and neither is `env -i`:
# both strip the MSYS/Cygwin runtime's own library search path on a Git-Bash host,
# the shell then fails to load at all, and the class reports a FAIL that looks
# like the gate working while it actually tested nothing. Removing exactly the
# `oasdiff` directory is precise and portable.
OASDIFF_PATH="$(command -v oasdiff 2>/dev/null || true)"
FILTERED_PATH="$PATH"
if [ -n "$OASDIFF_PATH" ]; then
  OASDIFF_DIR="$(dirname "$OASDIFF_PATH")"
  FILTERED_PATH="$(printf '%s' "$PATH" | tr ':' '\n' | grep -vxF "$OASDIFF_DIR" | paste -sd: -)"
  echo "   (dropped $OASDIFF_DIR from PATH for this class)"
fi
LOG="$LOG_DIR/12-tool-unavailable.log"
set +e
PATH="$FILTERED_PATH" OASDIFF_BIN="" \
  OASDIFF_DOWNLOAD_BASE_URL="https://oasdiff-unroutable.invalid/releases/download/v0.0.0/x.tar.gz" \
  "$GATE" --base-file "$ROOT/$BUNDLE_REL" --head-file "$ROOT/$BUNDLE_REL" > "$LOG" 2>&1
set -e
if grep -q 'CONTRACT_GATE_RESULT.*status=FAIL' "$LOG" && grep -q 'reason=TOOL_UNAVAILABLE' "$LOG"; then
  ok "12-tool-unavailable" "the gate fails closed when it cannot obtain a diff tool"
else
  bad "12-tool-unavailable" "the gate reported PASS with no obtainable diff tool" "$LOG"
fi

# 11: a diff tool that is not the pinned version. A stub reporting a different
# version stands in for "someone's PATH had a newer oasdiff", which is the drift
# the pin exists to catch - a tool that can add a rule (every PR red) or drop one
# (a breaking merge green) with nothing reporting either.
STUB="$WORK/stub-oasdiff"
printf '#!/bin/sh\necho "oasdiff version 0.0.1-not-the-pin"\nexit 0\n' > "$STUB"
chmod +x "$STUB"
LOG="$LOG_DIR/11-version-drift.log"
set +e
OASDIFF_BIN="$STUB" "$GATE" --base-file "$ROOT/$BUNDLE_REL" --head-file "$ROOT/$BUNDLE_REL" > "$LOG" 2>&1
set -e
if grep -q 'reason=TOOL_VERSION_DRIFT' "$LOG"; then
  ok "11-version-drift" "a wrong tool version is refused before any comparison"
else
  bad "11-version-drift" "a wrong tool version was not refused" "$LOG"
fi

# --- 3. The aggregate names its targets, and its targets are reusable ---------
# `ci.yml` calls the three pipelines with `uses: ./.github/workflows/<file>.yml`,
# and GitHub refuses that call unless the target declares `on: workflow_call`.
# The failure is at parse time and names nothing useful, and the symptom is a push
# to main producing no run of that pipeline at all - the gate vanishes rather than
# going red. So the pairing is asserted, in both directions.
echo
echo "-- aggregator wiring --"
AGGREGATOR="$ROOT/.github/workflows/ci.yml"
log="$LOG_DIR/aggregator-wiring.log"
aggregator_findings=0
if [ ! -f "$AGGREGATOR" ]; then
  bad "aggregator-wiring" "$AGGREGATOR does not exist; nothing subscribes to pull_request for the pipelines"
  aggregator_findings=1
else
  for target in backend frontend contract; do
    if ! grep -q "uses: ./.github/workflows/${target}.yml" "$AGGREGATOR"; then
      bad "aggregator-wiring" "$AGGREGATOR does not call ./.github/workflows/${target}.yml, so that pipeline never runs on a pull request" "$AGGREGATOR"
      aggregator_findings=1
      continue
    fi
    target_file="$ROOT/.github/workflows/${target}.yml"
    if ! grep -qE '^\s*workflow_call:\s*$' "$target_file"; then
      bad "aggregator-wiring" "$target_file does not declare 'on: workflow_call', so the aggregator's call to it is rejected by GitHub and the pipeline never runs" "$target_file"
      aggregator_findings=1
    fi
  done
  # The reverse direction: a pipeline that also subscribes to pull_request runs
  # its jobs twice per PR, which doubles a ~45 minute pipeline and puts two
  # identically-named checks in the list.
  for target in backend frontend contract; do
    target_file="$ROOT/.github/workflows/${target}.yml"
    if grep -qE '^\s*pull_request:\s*$' "$target_file" 2>/dev/null; then
      note_duplicate="  $target.yml subscribes to pull_request directly as well as being called. Every job runs twice per PR. Remove the direct pull_request trigger; ci.yml is the only subscriber."
      echo "$note_duplicate" >> "$LOG_DIR/aggregator-wiring.log"
      bad "aggregator-wiring" "$target.yml both subscribes to pull_request and is called by ci.yml, so its jobs run twice per PR" "$target_file"
      aggregator_findings=1
    fi
  done
  [ "$aggregator_findings" -eq 0 ] && ok "aggregator-wiring" "ci.yml calls all three pipelines, all three declare workflow_call, and none also subscribes directly"
fi

# --- Document classes --------------------------------------------------------
echo
echo "-- document classes --"

BASE="$WORK/base.json"
cp "$ROOT/$BUNDLE_REL" "$BASE"

# Each mutator rewrites a copy of the bundle. Node, because the repository already
# requires Node for every other gate (check-api-drift, the generated client), so
# the canary cannot fail for want of a tool the pipelines do not already have.
mutate() { # class base_json out_json
  node -e '
const fs = require("fs");
const [base, out, name] = process.argv.slice(1);
const doc = JSON.parse(fs.readFileSync(base, "utf8"));
const before = JSON.stringify(doc);
const mutate = {
  // --- must pass ---
  "none": () => {},
  "identical": () => {},
  "docs-only": () => { doc.info.description += " Extra prose, no contract change."; },
  "additive-bumped": () => {
    doc.info.version = "v1.1";
    doc.paths["/canary-probe"] = { get: { operationId: "getCanaryProbe", responses: { 200: { description: "ok" } } } };
  },
  // --- must fail ---
  "additive-unbumped": () => {
    doc.paths["/canary-probe"] = { get: { operationId: "getCanaryProbe", responses: { 200: { description: "ok" } } } };
  },
  "removed-path": () => { delete doc.paths["/projects/{projectId}"]; },
  // Adding a required REQUEST field breaks every existing client that omits it -
  // oasdiff reports `request-property-became-required`. This is the class people
  // mean by "removed a required field", and it is the one that must block.
  "required-added": () => {
    doc.components.schemas.ProjectCreateRequest.required.push("name");
  },
  // Dropping a required request field is the opposite: it makes the server more
  // permissive, so no client breaks. It is still a contract change and still owes
  // a version bump, which is why this class expects exit 2 and not exit 1. The
  // distinction is asserted here so nobody later "fixes" the canary to expect 1.
  "required-removed": () => {
    doc.components.schemas.AuthLoginRequest.required =
      doc.components.schemas.AuthLoginRequest.required.filter((field) => field !== "tenantId");
  },
  "tightened-enum": () => {
    doc.components.schemas.ProjectCreateRequest.properties.sourceLanguage = { type: "string", enum: ["en", "fr", "de", "es"] };
  },
  "auth-scope": () => { doc.paths["/projects"].get.security = [{ Bearer: ["projects.read"] }]; },
  "major-no-route": () => {
    doc.info.version = "v2";
    doc.paths["/canary-probe"] = { get: { operationId: "getCanaryProbe", responses: { 200: { description: "ok" } } } };
  },
  "version-backwards": () => { doc.info.version = "v0.9"; },
};
if (!(name in mutate)) { throw new Error("unknown canary mutation: " + name); }
mutate[name]();
// A mutation that changes nothing would make its class pass for the wrong
// reason - the gate would be reporting "compatible" about a document nobody
// touched. `none` and `identical` are the two classes that are SUPPOSED to be
// inert, so they are asserted inert rather than asserted changed.
const INERT = new Set(["none", "identical"]);
if (INERT.has(name) && JSON.stringify(doc) !== before) { throw new Error("the inert mutation " + name + " changed the document"); }
if (!INERT.has(name) && JSON.stringify(doc) === before) { throw new Error("the mutation " + name + " changed nothing, so it would trivially pass"); }
fs.writeFileSync(out, JSON.stringify(doc, null, 2));
' "$2" "$3" "$1"
}

class_result() { # logfile
  grep -o 'status=[A-Z]*' "$1" 2>/dev/null | tail -1 | cut -d= -f2 || echo NONE
}
class_exit() { # logfile
  grep -o 'CONTRACT_GATE_RESULT.*' "$1" 2>/dev/null | tail -1 | sed -n 's/.*exit=\([0-9]*\).*/\1/p' || echo NONE
}

run_class() { # class expected_status expected_exit description
  local class="$1" want_status="$2" want_exit="$3" description="$4"
  local head="$WORK/$class.json" log="$LOG_DIR/$class.log"
  mutate "$class" "$BASE" "$head"
  set +e
  "$GATE" --base-file "$BASE" --head-file "$head" > "$log" 2>&1
  set -e
  local status exit_code
  status="$(class_result "$log")"
  exit_code="$(class_exit "$log")"
  if [ "$status" = "$want_status" ] && [ "$exit_code" = "$want_exit" ]; then
    ok "$class" "$description (status=$status exit=$exit_code)"
  else
    bad "$class" "$description - expected $want_status/$want_exit, got ${status:-NONE}/${exit_code:-NONE}" "$log"
  fi
}

# 0a. The base against itself. Without this, a base document that already looks
# breaking would invert the whole canary: the broken classes would pass and the
# clean ones would fail.
run_class "identical" PASS 0 "a document compared with itself is compatible"

run_class "none" PASS 0 "no change at all"
run_class "docs-only" PASS 0 "a documentation-only edit owes no version bump"
run_class "additive-bumped" PASS 0 "an additive change with a version bump"

run_class "additive-unbumped" FAIL 2 "a contract change with no version bump"
run_class "removed-path" FAIL 1 "a removed operation"
run_class "required-added" FAIL 1 "a request field that became required"
run_class "required-removed" FAIL 2 "a request field that stopped being required (no client break, bump still owed)"
run_class "tightened-enum" FAIL 1 "a tightened request enum"
run_class "auth-scope" FAIL 1 "a route that now demands a scope it did not"
run_class "major-no-route" FAIL 2 "a new major version with no new route prefix"
run_class "version-backwards" FAIL 2 "a version that moved backwards"

echo
echo "== canary result: $PASSED passed, $FAILED failed =="
if [ "$FAILED" -ne 0 ]; then
  echo "A contract-gate canary class did not behave as specified: the gate is not enforcing what it claims." >&2
  exit 1
fi
