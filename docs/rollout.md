# Rollout

Task 043, instruction 5. The procedure, and the reasoning behind each step. The
one-command rollback is [`runbooks/rollback.md`](runbooks/rollback.md); the
topology is [`topology.md`](topology.md).

## The shape of it

```
  expand            deploy                verify                contract
  ──────            ──────                ──────                ────────
  additive          migration Job         deploy/verify.sh       drop the
  migrations       → API (backward       --post-deploy         deprecated
  only              compatible with       --require-post-       columns and
                    the previous          deploy                 endpoints
                    frontend)                                    ─ only after
                                                               two green
                                                               releases
```

The rule that makes it safe: **the API deployed at any moment must work with the
frontend that is currently in browsers.** Everything else is sequencing.

## 0. Before the release

Every one of these is a gate, and every one fails closed:

```bash
# 1. The gate that has never run on this repository's frontend image.
bash deploy/tests/hosting.test.sh

# 2. The config that goes into the bundle is allowlisted and secret-free.
VITE_API_BASE_URL="$API" VITE_CDN_ORIGIN="$CDN" \
  bash deploy/config-inject.sh --env "$ENV" --release "$TAG" --check
bash scripts/vite-env-audit.sh --allowlist-sync

# 3. The manifests are structurally valid and the contract has not broken.
bash deploy/verify.sh
```

`config-inject.sh --check` writes nothing. It is the pre-flight version of the
step that actually injects, and it fails on a secret, a non-URL origin, a missing
release tag, or an unreadable OpenAPI bundle.

## 1. Inject the config (build time)

```bash
VITE_API_BASE_URL="https://api.staging.example.com" \
VITE_CDN_ORIGIN="https://cdn.staging.example.com" \
  bash deploy/config-inject.sh --env staging --release v1.4.2 --commit "$GITHUB_SHA"
```

Writes `frontend/.env.production` (the build-time values) and
`frontend/dist/version.json` (what the CDN will serve at `/version.json`). The
OpenAPI version is **read from the committed bundle**, not supplied — that is
what makes `/version.json` and `/version` agree by construction rather than by
two people typing the same string.

The frontend image is then built from that file, and
`scripts/vite-env-audit.sh --bundle` asserts the values it cleared are the values
in the artefact.

## 2. Expand — the migration Job, before the API

```bash
kubectl apply -f deploy/k8s/migration-job.yaml
kubectl -n dubbing-prod wait --for=condition=complete --timeout=600s job/dubbing-migration
```

**The `wait` is the gate. The `apply` is not.** A Job that is merely applied may
be Running, may have failed, may be retrying (`backoffLimit: 3`).

ArgoCD runs this automatically as a `PreSync` hook and refuses to sync the rest
of the application until it reports Healthy. Helm runs it as a `pre-upgrade`
hook. Plain `kubectl` requires the wait, which is why the wait is in the runbook
rather than assumed.

The Job runs `/app/efbundle` with the maintenance-role connection string
(`BYPASSRLS`, never the app role). It is idempotent — concurrent runners
serialize on `__EFMigrationsHistory` — so a re-run is safe.

**A failed migration blocks the release.** The previous release keeps serving, and
the next action is a forward-fix, never a rollback. See §5.

## 3. Deploy the API, backward-compatible

```bash
kubectl apply -f deploy/k8s/api-deployment.yaml
kubectl -n dubbing-prod rollout status deploy/dubbing-api
```

Two things in this step are load-bearing.

**The `wait-for-migrations` initContainer.** The API pod runs the same bundle
before its main container starts, so a pod never begins serving against a schema
it does not understand. It is defense-in-depth for an out-of-band apply that
skipped step 2; the Job is the primary gate.

**Backward compatibility is the author's job, not the deployer's.** The API must
work with the frontend currently in browsers *and* with the one about to ship.
Concretely, in the expand phase:

- new columns are **nullable** or have a default, so old code does not break;
- new endpoints are **additive**; nothing existing changes shape;
- a renamed or removed field is a **new API line**, not a change to this one —
  the contract gate (`scripts/openapi-diff.sh`) enforces that a breaking change
  requires a major bump, and `tools/openapi-compat.mjs` requires the route prefix
  to move with it;
- deprecated fields are **kept and marked**, not removed. Removal is the contract
  phase.

## 4. Flag-gate the new UI

New frontend behaviour ships behind a flag (Task 036's `FlagsPanel`), defaulted
**off**. Off is the only safe default: a flag defaulted on turns a
backend-not-ready deploy into a user-visible failure, and the flag is what makes
the new UI independently revertible without a redeploy.

The compatibility window that makes this work: while the flag is off, the
frontend in browsers is running the OLD UI against the NEW API. That is the
combination the expand phase has to keep working, and it is the combination
nobody tests unless it is written down.

**Flags fail closed to the last-known-good snapshot** if the flag service is
unavailable at boot, and the UI marks flagged areas `DegradedState`. Fail-closed
here means "the flag keeps its last value", not "the flag turns off": a flag
service outage must not silently disable a feature for every tenant, and must not
silently enable one that was not ready.

### 4a. Where the browser reads a flag (Task 048)

There is no flag service in this product: plan §6.9 does not require DB-backed
flags, and `FlagsPanel` reads `GET /admin/feature-flags` only for the admin's
own toggles. A browser reads a flag from exactly two places, in this order:

1. **`GET /me`'s `featureFlags` slice** (`MeFeatureFlags`: `videoIntelligenceEnabled`,
   `lipSyncEnabled`, `localInferenceEnabled`). It is the only server → client flag
   channel an ordinary user can read — the admin list 403s for everyone else, so
   it cannot gate a workspace surface.
2. **Build-time bootstrap config**, the `VITE_ENABLE_*` family
   (`VITE_ENABLE_ANALYTICS`, `_DIAGNOSTICS`, `_EXPERIMENTAL_FEATURES`), baked into
   the bundle by `deploy/config-inject.sh`.

The rule, in one sentence: **a flag is on when `/me` states it on, or — when
`/me` says nothing about it — when bootstrap config states it on; everything
else is off.** So `/me` wins where both speak, a recognised-but-off `/me` key is
an answer rather than an absence, and every default is `false`.

Evaluation happens in exactly one place, `useFeatureFlag`
(`frontend/src/hooks/useFeatureFlag.ts`), over the vocabulary and the
fail-closed resolver in `frontend/src/config/featureFlags.ts`. A grep gate
(`frontend/src/hooks/useFeatureFlag.gate.test.tsx`) fails the suite if any other
module names a `/me` wire spelling, reads a `VITE_ENABLE_*` variable, or issues
its own `/me` read — so "is this rollout on?" has one answer, not one per module.

Two consequences for this document's §4 promise:

- **"Fail closed" is both halves.** Before any value is known (no session, no
  `/me`, an unreadable document, a flag key this build has never heard of) every
  flag is off. After a value is known, a later failed read keeps it — which is
  the "last-known-good snapshot" above, implemented as react-query keeping the
  resolved slice rather than as a degraded-state banner. A flag that flickers
  mid-session is worse than one that is five minutes stale.
- **A flag grants nothing.** Flags decide what is *shown*; `/me`'s permission
  strings, `RequireAdmin`, and the server's own authorization decide what is
  *permitted*. A flag on with no permission still renders the denial.

## 5. Verify, then keep the contract

```bash
bash deploy/verify.sh --post-deploy --require-post-deploy \
  --url "https://api.staging.example.com" \
  --tag v1.4.2 \
  --openapi-version "$(jq -r .info.version < src/DubbingPlatform.Api/OpenApi/openapi.v1.json)" \
  --rollback-command "kubectl -n dubbing-staging rollout undo deploy/dubbing-api"
```

In order, with a rollback triggered on the first failure:

1. `/health/live` and `/health/ready` both 200. Readiness includes migration
   currency, so this fails if the Job silently did not run.
2. The served `info.version` matches the release. This is what catches a
   rollback to the wrong image and a partial rollout.
3. The `@smoke` suite passes.

`--require-post-deploy` is what stops a release being reported as verified
without a `DEPLOY_URL`. Without it, a misconfigured release job degrades to a
manifest check and marks the release verified having contacted nothing.

**The contract phase happens only after two green releases.** Two, not one: the
reason for a contract migration is that some release is still running old code
against the schema. After one green release, the release before it is still a
supported client. After two, it is not.

Before the contract phase:

- every deprecated field/endpoint has been deprecated for **at least one full
  release window**;
- `/version.json`'s `openapiVersion` and the API's agree, so a client can tell
  which contract it is talking to;
- the compatibility matrix says the previous release is no longer supported.

## The expand/contract rule

**Migrations are additive only.** Old code tolerates new nullable columns for at
least one release window, so rolling back application code never requires rolling
back the schema.

| State | Rollback allowed? | Why |
| --- | --- | --- |
| Expand migration applied, new release not yet deployed | **Yes**, and nothing is needed | the new columns are nullable; the old code ignores them |
| Expand applied, new release deployed, previous still supported | **Yes** | the new release ran only against the expanded schema, which is a superset |
| Contract applied (columns/rows dropped) | **No** | the data is gone. Forward-fix only. |

The decision table in [`runbooks/rollback.md`](runbooks/rollback.md#decision-table)
is the operational version of this.

**Never roll back a migration that applied anywhere shared.** The EF bundle is
forward-only by design: `dotnet ef database update` has no down path for a
bundle, and that is intentional. A rollback that reverses a migration is a data
loss event wearing the costume of a deploy rollback.

## Failure cases

| What failed | What happens | Next |
| --- | --- | --- |
| Migration Job fails | API rollout blocked; previous release keeps serving; alert fires with the Job's logs | forward-fix the migration; re-run the Job |
| API pods never become ready | rollout stalls at `rollout status`; no traffic to the new pods | read the initContainer log; it is the migration gate |
| `/health/ready` 503 after deploy | pods removed from the Service; the previous revision keeps serving | check `migration-currency` in the `/health` body |
| Served `info.version` differs | `OPENAPI_VERSION_MISMATCH`; the gate triggers the rollback | wrong image or partial rollout |
| `@smoke` fails | `SMOKE_FAILED`; the gate triggers the rollback | health and version both passed, so the behaviour is wrong |

## What has never been run

- **`deploy/verify.sh --post-deploy` against a real environment.** The `@smoke`
  spec does not exist in this repository (041A did not write it; see that
  report), so the post-deploy gate currently fails with `SMOKE_SPECS_MISSING`,
  which is correct. The first real run is the first full one.
- **The release job that calls it.** Nothing invokes
  `--require-post-deploy` yet. When it is wired, it must use the exact command
  above, because `--require-post-deploy` is the flag that makes a missing
  `DEPLOY_URL` a failure instead of a silent pass.
- **ArgoCD and Helm.** The annotations are in the manifests and are asserted by
  `deploy/verify.sh`'s migration-contract check, but neither tool has run here.
- **A contract phase.** Nothing has been dropped yet, so the "two green releases"
  rule has not been exercised.
