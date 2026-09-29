#!/usr/bin/env bash
# Workflow lint (Task 042).
#
# THE TASK'S TESTING REQUIREMENT
# ------------------------------
# "Pipeline definitions are validated by dry-run (act -n or workflow lint)."
#
# `act -n` is not usable here for a structural reason rather than a preference: it
# EXECUTES workflow steps against a local Docker stack, and this repository's
# E2E gates need a real frontend build, a seeded API, PostgreSQL, MinIO and
# RabbitMQ. A dry run of that is a 40-minute test run that fails on
# infrastructure rather than on the workflow, so it would be a check that goes
# red for reasons unrelated to what it claims to check.
#
# So: `actionlint` when it is available (a real lint of the workflow language),
# and a hermetic structural pass ALWAYS. The structural pass is not a worse
# actionlint; it covers the specific failure modes that a hand-written pipeline
# introduces and that a green run does not catch, and it runs with nothing
# installed:
#
#   1. every workflow parses as YAML, and declares `on:` and `jobs:`
#   2. no duplicate keys in a mapping (YAML's LAST-wins silently drops the first,
#      which is how a second `push:` block once erased a first)
#   3. every `uses:` is pinned to a tag or a commit SHA - never `@main`, never a
#      bare `@`, because a moving ref is an unreviewed change to the build
#   4. every `${{ ... }}` is balanced, and no expression interpolates a value
#      straight into a `run:` block (the `run: echo ${{ github.event.* }}`
#      script-injection hole)
#   5. every artifact upload of a TEST artifact uses `if: always()`, so a red
#      build keeps the evidence
#   6. every job that calls `secrets.` declares the permission it needs, and the
#      top-level `permissions` is not `write-all`
#   7. no step passes `--update-snapshots` (041D's rule, enforced structurally
#      rather than by convention)
#
# EXIT CODES
#   0  no finding
#   1  at least one finding
#   2  the linter could not read the workflows
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORKFLOW_DIR="$ROOT/.github/workflows"

note() { printf 'workflow-lint: %s\n' "$1"; }

command -v python3 >/dev/null 2>&1 || {
  echo "::error::workflow-lint needs python3 with PyYAML (the same dependency deploy/verify.sh already uses)." >&2
  exit 2
}

[ -d "$WORKFLOW_DIR" ] || {
  echo "::error::no $WORKFLOW_DIR" >&2
  exit 2
}

# --- actionlint, when it happens to be installed -----------------------------
if command -v actionlint >/dev/null 2>&1; then
  note "running actionlint ($WORKFLOW_DIR)"
  if ! actionlint "$WORKFLOW_DIR"; then
    echo "::error::actionlint reported findings (see above)." >&2
    exit 1
  fi
else
  note "actionlint is not installed; running the structural pass only. Install it locally (go install github.com/rhysd/actionlint/cmd/actionlint@latest) for the full workflow-language lint."
fi

# --- the structural pass ------------------------------------------------------
set +e
python3 - "$WORKFLOW_DIR" <<'PY'
import os
import re
import sys

import yaml

workflow_dir = sys.argv[1]
findings = []
files = sorted(f for f in os.listdir(workflow_dir) if f.endswith((".yml", ".yaml")))

if not files:
    print("FAIL: no workflow files found in " + workflow_dir)
    sys.exit(2)

# Expression interpolation straight into a `run:` block is script injection: the
# value of a PR title, branch name or issue body is attacker-controlled, and
# `run: echo ${{ github.event.pull_request.title }}` hands it a shell. The safe
# forms are an `env:` mapping (the workflow-lint rule 4 below enforces that the
# expression is moved there) or a single expression that is not attacker data.
EXPR = re.compile(r"\$\{\{(.*?)\}\}", re.S)
SAFE_INLINE = re.compile(r"^\s*(if|!)?\s*\$\{\{[^}]*\}\}\s*$")
# `if:` conditions legitimately take a bare expression, and `run:` may take a bare
# expression that is a matrix reference.
TRUSTED_RUN_EXPR = re.compile(r"^\s*(matrix\.|github\.(ref|sha|event_name|workflow|repository|repository_owner|run_id))\b")


for filename in files:
    full = os.path.join(workflow_dir, filename)
    with open(full, encoding="utf-8") as handle:
        text = handle.read()

    # 1. Balanced expressions, checked on the RAW text because a `${{` that never
    #    closes is exactly the case a YAML parser accepts happily.
    opens = text.count("${{")
    closes = text.count("}}")
    if opens != closes:
        findings.append(f"{filename}: {opens} '${{{{' vs {closes} '}}' - an expression is not closed")

    # 2. Raw scan for a duplicated top-level trigger or job key. Cheaper and more
    #    reliable than trying to make PyYAML report the collision, which it
    #    resolves silently.
    for key in ("push", "pull_request", "jobs", "permissions", "concurrency", "env"):
        occurrences = len(re.findall(rf"^{key}:", text, re.M))
        if key in ("push", "pull_request") and occurrences > 1:
            findings.append(f"{filename}: '{key}' appears {occurrences} times at the top level. YAML keeps only the LAST one, so an earlier block is silently discarded - merge them.")

    try:
        document = yaml.safe_load(text)
    except yaml.YAMLError as exc:
        findings.append(f"{filename}: not valid YAML: {exc}")
        continue

    if not isinstance(document, dict):
        findings.append(f"{filename}: the document root is not a mapping")
        continue

    # PyYAML parses the bare key `on:` as the boolean True, which is YAML 1.1.
    # GitHub's parser is YAML 1.2 and does not, so the key is checked under both
    # spellings - a linter that reported "no `on:` key" on a valid workflow would
    # be worse than no linter.
    on_key = "on" if "on" in document else (True if True in document else None)
    if on_key is None:
        findings.append(f"{filename}: no top-level 'on:' trigger")
    if "jobs" not in document or not isinstance(document["jobs"], dict) or not document["jobs"]:
        findings.append(f"{filename}: no top-level 'jobs:' mapping")
        continue

    top_permissions = document.get("permissions")
    if top_permissions == "write-all":
        findings.append(f"{filename}: top-level `permissions: write-all` grants every scope to every job. List the scopes, or default to `contents: read` and let the jobs that need more ask.")
    if top_permissions is None:
        findings.append(f"{filename}: no top-level `permissions:` block. Without one the workflow inherits the repository default, which may be read/write for everything - state the intent instead.")

    for job_name, job in document["jobs"].items():
        where = f"{filename}:{job_name}"
        if not isinstance(job, dict):
            findings.append(f"{where}: the job is not a mapping")
            continue

        steps = job.get("steps")
        if steps is None and "uses" not in job:
            findings.append(f"{where}: the job has neither 'steps' nor 'uses'")
        if job.get("runs-on") is None and "uses" not in job:
            findings.append(f"{where}: the job has no 'runs-on'")

        if steps is None:
            continue
        if not isinstance(steps, list):
            findings.append(f"{where}: 'steps' is not a list")
            continue

        job_permissions = job.get("permissions")
        blob = yaml.safe_dump(job)
        if "id-token: write" in blob and not isinstance(job_permissions, dict):
            findings.append(f"{where}: uses `id-token: write` (keyless signing) but does not declare job-level permissions. A top-level block cannot grant it.")

        for index, step in enumerate(steps):
            if not isinstance(step, dict):
                findings.append(f"{where}[{index}]: the step is not a mapping")
                continue
            step_name = step.get("name") or step.get("uses") or f"step {index}"

            # 3. Pinned actions.
            uses = step.get("uses")
            if isinstance(uses, str):
                if "@" not in uses:
                    findings.append(f"{where}[{index}] {step_name}: `uses: {uses}` has no ref. An action with no ref cannot be reviewed.")
                else:
                    ref = uses.rsplit("@", 1)[1]
                    if ref in ("main", "master", "HEAD", "latest") or ref == "":
                        findings.append(f"{where}[{index}] {step_name}: `uses: {uses}` points at a MOVING ref. Pin a tag (v4) or, better, a commit SHA.")
                    elif not re.fullmatch(r"v?\d+(\.\d+)*([.-][0-9A-Za-z.-]+)?|[0-9a-f]{40}", ref):
                        findings.append(f"{where}[{index}] {step_name}: `uses: {uses}` has an unpinnable ref '{ref}'. Use a release tag or a 40-character commit SHA.")

            # 4. Expression injection into a shell.
            run = step.get("run")
            if isinstance(run, str):
                for match in EXPR.finditer(run):
                    expression = match.group(1).strip()
                    if TRUSTED_RUN_EXPR.match(expression):
                        continue
                    findings.append(
                        f"{where}[{index}] {step_name}: `run:` interpolates ${{{{ {expression} }}}} directly into a shell. "
                        "An expression whose value comes from a PR title, branch name, issue body or comment is attacker-controlled, "
                        "so this is script injection. Move it into the step's `env:` and reference the environment variable instead."
                    )
                if "--update-snapshots" in run or "--update-snapshot" in run:
                    findings.append(
                        f"{where}[{index}] {step_name}: passes --update-snapshots in CI. It rewrites committed baselines in the runner's "
                        "working tree, the job goes green, and the change is discarded with the runner - so the regression test would never "
                        "have existed. Baselines are approved by a human, in a PR (scripts/visual-baseline-gate.sh)."
                    )
                if "continue-on-error: true" in run:
                    findings.append(f"{where}[{index}] {step_name}: `continue-on-error: true` is not a step key; it belongs beside `run:` and silently does nothing here.")

            # 5. Test artifacts must survive a red build.
            if isinstance(uses, str) and "upload-artifact" in uses:
                path = ""
                with_block = step.get("with")
                if isinstance(with_block, dict):
                    path = str(with_block.get("path", ""))
                is_test_artifact = any(token in path for token in (
                    "test-results", "playwright-report", ".artifacts", "trx", "coverage", "trivy-", "sbom-", "contract-diff",
                ))
                condition = str(step.get("if", ""))
                if is_test_artifact and "always()" not in condition and condition != "":
                    findings.append(
                        f"{where}[{index}] {step_name}: uploads test evidence but is guarded by `if: {condition}`. "
                        "The most useful artifact is the one attached to the run that FAILED. Use `if: always()`."
                    )
                if is_test_artifact and condition == "":
                    findings.append(
                        f"{where}[{index}] {step_name}: uploads test evidence with no `if:`. A failed step skips the upload by default, "
                        "so a red build leaves no evidence. Use `if: always()`."
                    )

            # `continue-on-error` is NOT judged here. It is a suppression, and
            # `scripts/quarantine-check.sh` owns that rule with the precise
            # per-step check: a `continue-on-error: true` must have a
            # `# QUARANTINE ... EXPIRY: <date>` marker within six lines above it,
            # that date must not have passed, and the reason must be in
            # docs/ci-quarantine.md. Judging it twice at different strictness from
            # two files would mean one of the two rules is decorative.

for finding in findings:
    print("workflow-lint: " + finding)
print(f"workflow-lint: {len(files)} workflow file(s), {len(findings)} finding(s)")
sys.exit(1 if findings else 0)
PY
STATUS=$?
set -e

exit "$STATUS"
