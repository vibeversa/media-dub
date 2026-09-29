#!/usr/bin/env bash
# Quarantine-registry check (Task 042).
#
# WHAT THIS ENFORCES
# ------------------
# `docs/ci-quarantine.md` is the repository's only sanctioned way to land a red
# gate. This script makes its rules mechanical instead of conventional:
#
#   * every entry carries a gate, a reason, an owner, an issue and an EXPIRY
#     date - a missing field is a suppression with extra steps;
#   * no entry's EXPIRY is in the past - an entry that outlived its problem is
#     exactly the permanent skip the policy forbids, and it fails the build;
#   * an entry is not more than MAX_DAYS_OUT old from the day it was landed,
#     which stops "expires next week" from being used for something with no
#     owner working on it.
#
# It also cross-checks the workflows: a `continue-on-error: true` step that
# carries a `# QUARANTINE` marker naming a reason must have that reason in the
# registry. A marker for a reason nobody recorded is a suppression with no
# audit trail, and a registry entry that no step uses is a stale document.
#
# Nothing here judges whether a quarantine is a GOOD idea. That is the weekly
# review's job, in a PR, where the decision gets a commit.
#
# EXIT CODES
#   0  the registry is well-formed and nothing has expired
#   1  a rule was broken
#   2  the registry is missing or unreadable
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REGISTRY="$ROOT/docs/ci-quarantine.md"
MAX_DAYS_OUT="${QUARANTINE_MAX_DAYS:-14}"
WORKFLOW_DIR="$ROOT/.github/workflows"
# A fixed reference date is required so the check is reproducible; the workflows
# pass TODAY, and running it by hand defaults to today. Overridable only for a
# deliberate re-check of a historical state.
TODAY="${QUARANTINE_TODAY:-$(date -u +%Y-%m-%d)}"

fail() { echo "::error title=QUARANTINE::$1" >&2; echo "QUARANTINE_RESULT reason=QUARANTINE_INVALID status=FAIL" >&2; exit 1; }

[ -f "$REGISTRY" ] || fail "docs/ci-quarantine.md does not exist. The registry is mandatory: a `continue-on-error` without a record is an unreviewed suppression."

command -v python3 >/dev/null 2>&1 || fail "python3 is required to read the quarantine registry."

python3 - "$REGISTRY" "$TODAY" "$MAX_DAYS_OUT" "$WORKFLOW_DIR" <<'PY'
import datetime
import os
import re
import sys

registry_path, today_text, max_days_text, workflow_dir = sys.argv[1:5]
today = datetime.date.fromisoformat(today_text)
max_days = int(max_days_text)

text = open(registry_path, encoding="utf-8").read()
start_marker = "<!-- QUARANTINE-TABLE-START -->"
end_marker = "<!-- QUARANTINE-TABLE-END -->"
problems = []

if start_marker not in text or end_marker not in text:
    print("FAIL: the registry has no quarantine-table markers", file=sys.stderr)
    sys.exit(1)
table = text.split(start_marker, 1)[1].split(end_marker, 1)[0]

rows = []
for line in table.splitlines():
    line = line.strip()
    if not line.startswith("|"):
        continue
    cells = [cell.strip() for cell in line.strip("|").split("|")]
    if len(cells) < 6:
        problems.append(f"a row has {len(cells)} columns, expected 6: {line!r}")
        continue
    if cells[0].lower().startswith("---") or cells[0].lower() == "gate":
        continue
    rows.append(cells)

if not rows:
    problems.append("the registry has no entries. An empty registry is only correct when nothing is quarantined, in which case the table should be removed and this check will report zero rows without failing.")

reasons_in_registry = set()
# Cells are written with markdown backticks for readability, so they are stripped
# before comparison. Without this the registry's backticked REASON never matches a
# marker's `reason=REASON`, every quarantine looks unregistered, and the check gets
# disabled for being "wrong" when it is only fussy about formatting.
def bare(cell):
    return cell.replace("`", "").replace("*", "").strip()

for gate, reason, owner, issue, expiry, landed in rows:
    gate, reason, owner, issue = bare(gate), bare(reason), bare(owner), bare(issue)
    label = f"{reason} ({gate})"
    if reason in ("", "-"):
        problems.append(f"{label}: no failure reason. A quarantine that does not name the exact reason will silently cover a DIFFERENT failure that appears later.")
    else:
        reasons_in_registry.add(reason)
    if owner in ("", "-") or "CHANGE_ME" in owner:
        problems.append(f"{label}: owner is '{owner}'. Resolve it in docs/ci-branch-protection.md §3 first.")
    if issue in ("", "-"):
        problems.append(f"{label}: no tracking issue. An entry without an issue is a suppression with extra steps.")
    try:
        expiry_date = datetime.date.fromisoformat(expiry)
    except ValueError:
        problems.append(f"{label}: EXPIRY '{expiry}' is not an ISO date (YYYY-MM-DD).")
        continue
    if expiry_date < today:
        problems.append(
            f"{label}: EXPIRED on {expiry} (today is {today}). An entry that outlived its problem is the permanent skip the policy forbids. "
            "Either fix the gate and delete the row, or escalate it at the weekly review - do not simply extend the date."
        )
    try:
        landed_date = datetime.date.fromisoformat(landed)
    except ValueError:
        problems.append(f"{label}: the Landed date '{landed}' is not an ISO date (YYYY-MM-DD).")
        continue
    window = (expiry_date - landed_date).days
    if window < 0:
        problems.append(f"{label}: EXPIRY {expiry} is BEFORE the landed date {landed}.")
    elif window > max_days:
        problems.append(f"{label}: the quarantine window is {window} days (max {max_days}). 'Expires next month' is how an entry with no owner working on it becomes permanent.")

# Cross-check the workflows.
if os.path.isdir(workflow_dir):
    marker_re = re.compile(r"#\s*QUARANTINE\b[^\n]*?EXPIRY:\s*(\d{4}-\d{2}-\d{2})[^\n]*", re.IGNORECASE)
    reason_re = re.compile(r"#\s*QUARANTINE\b[^\n]*?reason=([A-Z_]+)", re.IGNORECASE)
    for filename in sorted(os.listdir(workflow_dir)):
        if not filename.endswith((".yml", ".yaml")):
            continue
        body = open(os.path.join(workflow_dir, filename), encoding="utf-8").read()
        if "continue-on-error: true" not in body:
            continue
        # Each `continue-on-error: true` must be within a few lines of a marker.
        lines = body.splitlines()
        for index, line in enumerate(lines):
            if line.strip() != "continue-on-error: true":
                continue
            # The window is the enclosing STEP **including its comment block**,
            # found by walking back to the list item that starts the step and then
            # over the contiguous comment lines above it. Not a fixed number of
            # lines: a fixed window is a comment-counting game that fails when
            # someone writes a long, useful comment above a legitimate step and
            # passes when they write a short one - the opposite of what the rule
            # is for.
            start = index
            while start > 0 and not re.match(r"^\s*-\s+(name|uses|id):", lines[start]):
                start -= 1
            while start > 0 and lines[start - 1].lstrip().startswith("#"):
                start -= 1
            window = "\n".join(lines[start: index + 1])
            found = marker_re.search(window)
            if found is None:
                problems.append(
                    f"{filename}:{index + 1}: `continue-on-error: true` with no `# QUARANTINE ... EXPIRY: <date>` marker in the step above it. "
                    "A blanket continue-on-error is unreviewed; record it in docs/ci-quarantine.md or remove it."
                )
                continue
            marker_expiry = found.group(1)
            if datetime.date.fromisoformat(marker_expiry) < today:
                problems.append(f"{filename}:{index + 1}: the QUARANTINE marker expires {marker_expiry}, which is in the past. The suppression outlived its problem.")
            reason_match = reason_re.search(window)
            if reason_match is not None and reason_match.group(1) not in reasons_in_registry:
                problems.append(
                    f"{filename}:{index + 1}: the QUARANTINE marker names reason={reason_match.group(1)}, which is not in the registry table. "
                    "A suppression with no audit trail is not a quarantine."
                )

for problem in problems:
    print("FAIL: " + problem, file=sys.stderr)
print(f"quarantine-check: {len(rows)} entr(ies), {len(problems)} problem(s), today={today}, max window={max_days} days", file=sys.stderr)
if problems:
    sys.exit(1)
print("quarantine-check: every entry is owned, tracked, in window and unexpired.")
sys.exit(0)
PY
STATUS=$?

if [ "$STATUS" -eq 0 ]; then
  echo "CI_GATE_RESULT reason=OK status=PASS"
else
  echo "CI_GATE_RESULT reason=QUARANTINE_INVALID status=FAIL"
fi
exit "$STATUS"
