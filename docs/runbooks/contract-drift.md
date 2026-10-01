# Contract drift

The committed OpenAPI bundle, the generated TypeScript client, and the API the
server actually serves have diverged. Or: two releases are running against each
other and one of them calls something the other removed.

## Symptoms

- `scripts/contract-snapshot.sh` fails: the committed
  `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` does not match the document
  the running API emits.
- `npm run check-drift` (`tools/check-api-drift.mjs`) fails: the committed
  `frontend/src/api/generated/` does not match what the bundle generates, or the
  `OPENAPI_VERSION` stamp's `bundle=sha256:` hash does not match the bundle.
- A frontend call returns 404 for an endpoint that is in the generated client.
- The contract gate (`scripts/openapi-diff.sh`) reports a breaking change with
  no version bump, or a version bump with no new route prefix.
- `/version.json`'s `openapiVersion` and the API's `/version` disagree.

That last one is a **deployment** problem rather than a source problem, and it is
worth separating early: it means the frontend and the backend were built from
different bundle revisions, which is the compatibility window failing rather than
the contract being wrong.

## Five-minute triage

```bash
# 1. What exactly diverged? The snapshot script names operations.
bash scripts/contract-snapshot.sh

# 2. Is it the source side or the deployment side?
curl -fsS https://api.<env>/version | jq '.openapiVersion'
curl -fsS https://<cdn-host>/version.json | jq '.openapiVersion'
```

If both are the same and the source check still fails, the committed bundle
itself is stale — the drift is in the repository. If they differ, the drift is
between two deployed artefacts, and the fix is a rebuild of whichever one is
behind, not a regeneration.

```bash
# 3. Is anything actually broken, or only the check?
#    A MISSING operation in the committed bundle is a documentation gap. A FATAL
#    divergence is a real contract break. The script reports both separately.
```

Step 3 matters because the two have opposite urgency. A `MISSING` operation means
the bundle under-describes the API; nothing is broken at runtime, and the
generated client simply has fewer calls available. A **fatal** divergence means
the bundle and the server disagree on something structural — a path, a parameter
shape, a security requirement — and clients generated from it will send requests
the server rejects.

## Mitigation

**The bundle is behind the server** (operations present on the server, missing
from the bundle): regenerate and commit.

```bash
make generate-api          # regenerates the client from the server's document
git diff --exit-code src/DubbingPlatform.Api/OpenApi/openapi.v1.json \
                       frontend/src/api/generated/
```

**The client is behind the bundle** (the stamp hash does not match): the bundle
was edited without regenerating. Same command, and commit both.

**The two deployed artefacts disagree**: rebuild the older one. Do **not**
regenerate anything — the source is consistent, and regenerating from a
half-deployed cluster produces a bundle that matches neither revision.

**A breaking change with no version bump**: this is the gate working. Either bump
the version (an additive change owes a bump) or revert the change. The gate's
decision table is in [`../../docs/ci-branch-protection.md`](../../docs/ci-branch-protection.md)
§2; `oasdiff` exits 0 whether or not it found anything unless `--fail-on ERR` is
passed, so a gate written the obvious way reports every breaking merge as
compatible. `scripts/openapi-diff.sh` passes the flag and also takes the verdict
from the parsed output rather than from `$?`.

## Escalation

- **L1 (0–30 min)**: steps 1–3. A source-side regeneration is routine.
- **L2 (1 h)**: the divergence is only visible against a running environment, or
  a fatal divergence affects a shipped client. Page the API owner.
- **L3 (4 h)**: two releases are serving simultaneously against incompatible
  contracts. Roll back per [`rollback.md`](rollback.md#incompatible-rollback-is-refused)
  — and note that this is the case where a rollback may be *refused*, because a
  build behind a contracted schema must not serve.

## Postmortem trigger

Any of:

- a breaking change merged without a version bump (the gate was skipped, or
  bypassed);
- the contract gate bypassed, in any incident;
- two releases found serving concurrently against incompatible contracts;
- a quarantine entry (`docs/ci-quarantine.md`) allowed a divergence to persist
  past its expiry.

`API_CONTRACT_DIVERGENCE` is currently a **live quarantine entry** in this
repository, which means this state is the present one: the committed bundle does
not drive this API, and the entry exists so that known defect does not block
every other change. It has an owner and an expiry, and
`scripts/quarantine-check.sh` fails the build the day the expiry passes. Treat
it as a scheduled postmortem, not as a background condition.

## The product runbook for this

[`product/contract-drift.md`](product/contract-drift.md) — or start from
[`product/index.md`](product/index.md), which indexes the set by symptom — is
the same incident
indexed by what a **user says** — "the page says the field doesn't exist", "I'm
signed out and logging in doesn't help". Its first step is the three-way version
comparison (the edge's `/version.json`, the API's `/version`, the committed
bundle's stamp) that names the drift in one call, and it links this page for the
gate commands rather than repeating them.

It also carries the one point that matters most at 3 a.m. and is easiest to get
wrong: **after the contract phase, a rollback is refused.** Old code calling a
removed endpoint is a worse state than new code calling a new one. The decision
table at [`rollback.md`](rollback.md) is the authority; this page's triage is not
a substitute for reading it.
