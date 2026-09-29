#!/usr/bin/env bash
# Visual-baseline gate (Task 042).
#
# THE EDGE CASE THIS IMPLEMENTS
# ------------------------------
# "Visual baseline missing for a new screen -> job marks BASELINE_NEEDED and
# blocks, requires explicit baseline approval PR."
#
# The problem is that "no baseline" and "baseline does not match" are the same
# failure to Playwright. Both are a red test, both say something about screenshots,
# and neither is distinguishable from the exit code. So a job that only looks at
# the status reports a genuinely new screen the same way it reports a regression,
# and the fix for each is opposite: one is "commit a baseline after a human looked
# at it", the other is "revert the change that moved the pixels".
#
# This reads the run's output and turns the first into a named, actionable state.
# It NEVER approves a baseline and NEVER runs `--update-snapshots`. Baselines are
# approved by a human, in a PR, where the diff is reviewable - a job that
# regenerated them in the runner would rewrite history that vanishes with the
# runner, and the "visual regression" would never have happened.
#
# USAGE
#   scripts/visual-baseline-gate.sh <playwright-log>
#
# EXIT CODES
#   0  no missing baseline in the log (the Playwright exit code still governs)
#   1  BASELINE_NEEDED: at least one screenshot had no baseline to compare to
set -euo pipefail

LOG="${1:-}"
[ -n "$LOG" ] || { echo "usage: scripts/visual-baseline-gate.sh <playwright-log>" >&2; exit 1; }
[ -f "$LOG" ] || { echo "visual-baseline-gate: no such log: $LOG" >&2; exit 1; }

# Playwright's own wording, matched loosely because it has changed between
# releases and a gate that depends on an exact sentence stops firing silently.
# The phrases below are the ones the 1.63 runner emits for a missing snapshot.
MISSING_PATTERNS=(
  "A snapshot doesn't exist"
  "snapshot doesn't exist at"
  "snapshot does not exist"
  "Could not find snapshot"
  "missing snapshot"
)

found=0
for pattern in "${MISSING_PATTERNS[@]}"; do
  if grep -Fq "$pattern" "$LOG"; then
    found=1
    echo "matched: ${pattern}"
  fi
done

if [ "$found" -eq 0 ]; then
  echo "visual-baseline-gate: no missing baseline in this run. Any failure here is a real pixel difference, not a new screen."
  echo "CI_GATE_RESULT reason=OK status=PASS"
  exit 0
fi

cat <<'EOF'
::error title=BASELINE_NEEDED::one or more screenshots had no committed baseline to compare against. This is a NEW screen or a new breakpoint, not a regression - the two are otherwise reported identically.

BASELINE_NEEDED: a visual baseline is missing.

WHAT TO DO
  1. Run `npm run test:visual:update` LOCALLY. The runner has the same Chromium
     build pinned by `package.json`, so a locally produced baseline matches CI.
  2. LOOK at the generated PNGs. The point of a baseline is a human deciding that
     these pixels are correct. A baseline generated and committed without anyone
     opening it records whatever the code happened to render, which is the
     opposite of a regression test.
  3. Commit the new PNGs in a SEPARATE pull request that contains nothing else.
     A baseline PR that also changes a component is indistinguishable from a
     regression once merged, and nobody can review the pixels in isolation.
  4. Re-run the PR.

WHAT NOT TO DO
  * Do not add `--update-snapshots` to CI. It rewrites the baselines in the
    runner's working tree, the job goes green, and the change is discarded with
    the runner - so the regression test would never have existed.
  * Do not re-run until it passes. A missing baseline is deterministic.

If a screen was deliberately REMOVED, delete its PNGs in the same PR that removed
the screen, and the `screens.spec.ts` entry with them - an orphaned baseline is
a file nobody reviews and nobody can explain.
EOF
echo "CI_GATE_RESULT reason=BASELINE_NEEDED status=FAIL"
exit 1
