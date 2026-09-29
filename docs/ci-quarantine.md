# CI Quarantine Registry (Task 042)

The one sanctioned way to land a red gate.

## The policy

**A gate is never suppressed.** There is no `--skip-audit`, no
`continue-on-error: true` without a record, no threshold that gets lowered to
make a build green, and no `retry: 3`. Each of those is a way of making a red
build look like a green one, and the difference only shows up later, when the
thing that was red was the thing that mattered.

What is allowed instead is a **quarantine entry**: a named, owned, expiring
record of a gate that is known-red for a reason that is not this repository's
fault yet. It exists so that a genuine, already-known defect does not block
every other change while it is being fixed — and it is structured precisely so
that it cannot quietly become permanent.

Every entry carries all four of:

| Field | Rule |
| --- | --- |
| **Gate** | The exact check name and the exact failure reason, so the entry cannot quietly cover a *different* failure that appears later |
| **Owner** | A team, from `docs/ci-branch-protection.md` §3. Not a person; a person leaves. |
| **Issue** | A real tracking issue. An entry without one is a suppression with extra steps. |
| **Expiry** | A date, at most 14 days out. An entry without an expiry is a permanent skip wearing a temporary label. |

Enforced mechanically, not by convention:

- `scripts/quarantine-check.sh` parses this file and **fails when any entry's
  `EXPIRY` is in the past**, and fails when an entry is missing any of the four
  fields. It runs as its own step in `backend.yml` and `frontend.yml`, and the
  result is on every pipeline's record.
- `scripts/workflow-lint.sh` **fails a `continue-on-error: true` step that has no
  `# QUARANTINE` marker carrying an `EXPIRY:` date in its own steps.** A blanket
  `continue-on-error` is the failure mode this registry exists to prevent, so the
  linter treats one as an error rather than a style note.

**Review: weekly**, by the platform team with the quality team, in the PR that
triages it — so the decision has a commit. The review asks one question per
entry: *has the underlying defect been fixed, or has the quarantine simply
outlived the problem?* An entry whose issue is still open at its expiry is
escalated, not renewed.

## Entries

<!-- QUARANTINE-TABLE-START -->
| Gate | Reason | Owner | Issue | EXPIRY | Landed |
| --- | --- | --- | --- | --- | --- |
| `CI / backend` → `contract` → `Start the API and pin the contract` | `API_CONTRACT_DIVERGENCE` | api | #422 | 2026-10-14 | 2026-09-30 |
<!-- QUARANTINE-TABLE-END -->

### Why each entry exists

#### `API_CONTRACT_DIVERGENCE` — the committed bundle cannot drive this API

`scripts/contract-snapshot.sh` boots the real API and compares the committed
bundle (`src/DubbingPlatform.Api/OpenApi/openapi.v1.json`) against the server's
own emitted document, operation by operation, using the comparison and severity
classification from `tools/check-api-contract.mjs`.

**It fails, and the failure is pre-existing.** Verified on 2026-09-30 against the
repository as it stood before this task: `check-api-contract.mjs` reports fatal
divergences and 37 `MISSING` entries (operations the server serves that the
bundle does not declare at all, from `POST /api/v1/auth/logout` through
`PUT /api/v1/projects/{projectId}/speakers/{speakerId}/voice-assignment`).

This is exactly the finding the 041A report recorded and deliberately did not
fix, for a reason that still holds: the bundle is described in
`docs/api-contract.md` as a **hand-checked versioned document** and the output of
Tasks 006–013 as **frozen inputs**. Regenerating it from the server document is
not a mechanical step — it is a 65-endpoint contract revision that also changes
`frontend/src/api/generated/`, the `OpenApiCoverageTests` expected lists, and
every consumer of the generated client. It belongs to the task that owns the
contract, not to the task that adds the gate.

**What it is not:** a suppressed check. The script still fails; the workflow step
records that failure; the entry above names the exact reason it covers, so a
*different* failure in the same step — a new divergence, or the API failing to
start at all — is still red.

**What closes it:** regenerate the bundle from the server document, regenerate
the client (`make generate-api`), update `OpenApiCoverageTests`, and delete the
row above. `scripts/contract-snapshot.sh` then passes and the step's
`continue-on-error` is removed in the same commit.

## Adding an entry

1. Confirm the failure is not a defect you introduced. A quarantine entry for a
   bug you just wrote is a bug you did not fix.
2. Open the issue. Get the number.
3. Add a row inside the table markers, with all four fields and a date at most
   14 days out.
4. If the gate needs `continue-on-error: true` to land, add a `# QUARANTINE` line
   to that step in the workflow carrying `EXPIRY: <the same date>` and the issue
   number. `scripts/workflow-lint.sh` will fail the build without it.
5. Say in the PR description which entry the change is landing under.
