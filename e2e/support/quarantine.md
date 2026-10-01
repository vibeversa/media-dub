# Flake quarantine policy (Task 046)

The only sanctioned way to land a gate that is not green.

## The rule

**2 failures in 50 runs → quarantine, with an owner and an issue. Never a silent
retry.**

Both halves matter, and the second is the one that gets skipped.

### Why the threshold is 2 in 50

A single failure is information. It might be the product, it might be the
environment, it might be one unlucky scheduling. Quarantining on the first
failure turns a 1-in-50 flake into a permanent skip, and a permanent skip is
indistinguishable from a working gate — from the outside, a quarantined suite and
a green suite look identical.

Two failures in fifty is where "this is a flake" stops being a guess. Below it, you
do not yet know. Above it, you have spent fifty runs' worth of CI time proving the
same thing.

### Why a retry is not a quarantine

Playwright's `retries: 0` in `e2e/playwright.config.ts` is not an oversight.
`retries: 2` is the most common way a flaky gate becomes a *permanently green*
one:

- the first attempt fails, the retry passes, the run is green;
- nobody reads the log, because the log says green;
- the next refactor that touches the same code can break it completely and
  nothing catches it, because the retry hides the first failure too.

A retry is a quarantine with no owner, no issue and no expiry, which is precisely
the suppression this policy exists to forbid. `scripts/quarantine-check.sh` already
fails the build on any `continue-on-error: true` without a marker, and the same
reasoning applies to an in-process retry.

## What a quarantine requires

Three things, all of them, recorded in `docs/ci-quarantine.md`:

| Field | What it must be |
| --- | --- |
| **Reason** | The exact failure, quoted. Not "flaky" — that is not a reason, it is a conclusion. |
| **Owner** | A person or team, resolved in `docs/ci-branch-protection.md` §3 first. `CHANGE_ME` is rejected by the check. |
| **Issue** | A tracking issue. An entry without one is "a suppression with extra steps". |

Plus the two dates the checker enforces:

- **Landed** — the day the quarantine was recorded.
- **Expiry** — at most 14 days after it, and never in the past. `QUARANTINE_MAX_DAYS`
  defaults to 14; a runner that cannot own the fix is not going to fix it in a month.

`scripts/quarantine-check.sh` fails the build when a field is missing, when an
expiry has passed, or when the window exceeds `QUARANTINE_MAX_DAYS`. Run it with:

```bash
bash scripts/quarantine-check.sh          # today defaults to $(date -u +%F)
QUARANTINE_TODAY=2026-02-01 bash scripts/quarantine-check.sh   # reproducible re-check
```

## What is never quarantineable

A quarantine suppresses a *symptom*. These are defects wearing a symptom's
clothes, and quarantining one of them converts a bug into a permanent skip:

- **An assertion that fails for a real reason.** If the test is right and the
  product is wrong, that is the test doing its job.
- **A missing handler or fixture.** `MSW_HANDLER_MISSING` and
  `STORAGE_EMULATOR_UNAVAILABLE` are *named* failures precisely so they cannot be
  mistaken for flakiness. They mean the harness is incomplete, and the fix is to
  finish the harness.
- **A spec that times out on a loaded runner** when it passes locally at the same
  commit. That is a missing `waitFor`, not a flake — see `support/sse-waits.ts`
  for what an event-driven wait looks like.
- **Anything that fails only on the first run of a fresh stack.** The reset did
  not do its job. `support/reset.ts` failing is the diagnosis, not the problem.

## How to decide, in order

1. Re-run it. If it passes twice, it was a one-off: fix nothing, but *say so* in
   the PR. A flake nobody mentions becomes a flake somebody depends on.
2. If it fails again, get the reason. `git log` the spec and the code it exercises;
   a flake introduced by a specific commit is usually a real ordering assumption
   that commit broke.
3. Only if (1) and (2) both come up empty: record the quarantine. Owner, issue,
   reason, expiry.
4. Review the registry at the weekly. Every entry either got fixed or got a
   louder expiry conversation. An entry nobody looks at is a skip with paperwork.

## Per-suite notes

- **`e2e/support/quarantine.md`** is this file. It is policy; the *instances* live
  in `docs/ci-quarantine.md` because `scripts/quarantine-check.sh` reads that one
  and fails closed when it is missing.
- **Visual baselines** (`@visual`) have their own gate,
  `scripts/visual-baseline-gate.sh`, which refuses `--update-snapshots` in CI. A
  baseline is approved by a human in a PR, never by a run.
- **Performance budgets** (`@perf`) have their own in-run quarantine mechanism —
  a single-run spike under the median quarantines with a record instead of
  blocking. It is the one place a quarantine is written automatically, and it is
  the reason the threshold here is about *failures*: a budget spike is not a
  failure.