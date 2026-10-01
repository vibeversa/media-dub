# Rollback

Task 043, instruction 6. `deploy/verify.sh --post-deploy` triggers the first
three steps automatically on any failure; this is for a rollback that was not
triggered by the gate, and for the cases where the gate refuses to roll back.

## One command for the API

```bash
kubectl -n <namespace> rollout undo deployment/dubbing-api
```

That is the whole thing for an API rollback. `maxUnavailable: 0` and the API PDB
(`minAvailable: 1`) mean the previous revision is already running and serving
while the new one drains, so the undo is a pointer change rather than a restart.

Confirm it took:

```bash
kubectl -n <namespace> rollout status deployment/dubbing-api
curl -fsS "https://api.<env>/version" | jq -r '.version, .release, .commit'
```

The served `info.version` is the check that matters. `rollout undo` moving the
Deployment does not prove the *pods* are the previous build — an image tag that
was re-pushed is a common way for this to be false — so read `/version` and
compare against the tag you intend.

## One command for the frontend

The frontend rollback is a **CDN version pin**, not a redeploy:

```bash
# Point the document at the previous immutable deployment.
aws cloudfront update-distribution --id "$DIST_ID" \
  --default-cache-behavior "TargetOriginId=static-origin,ViewerProtocolPolicy=https-only,..."
```

The concrete mechanism is provider-specific and is recorded in
`deploy/cdn/origin.json` under `cdnVersionPinning`. What is not provider-specific
is why it is a pin rather than a rebuild: `/assets/*` filenames carry a content
hash, so the previous release's assets are **still on the origin** under exactly
the names the previous document referenced. Restoring a pointer to that document
restores a working application with no rebuild, no re-upload, and no window in
which the assets are missing. A redeploy would be slower and would introduce
exactly that window.

It takes effect at the next document revalidation, which is why the document is
`no-cache` and the assets are `immutable`. No invalidation is needed, and a
release that requires `InvalidatePath` on `/index.html` has chosen the wrong
cache class for something.

## The database: never

**Rollback never reverses a migration.** Forward-fix only.

This is not caution, it is the only safe option. The EF bundle is forward-only
(`dotnet ef database update` has no down path for a bundle), and reversing a
migration that applied anywhere shared deletes data that the application has
been writing since. A migration that dropped a column cannot be un-dropped: the
values are gone, and "restoring" them means restoring from a backup, which is a
disaster-recovery exercise with a 1-hour RTO, not a rollback.

### Decision table

Check the migration state first. It is one command and it decides everything:

```bash
curl -fsS "https://api.<env>/version" | jq '{applied: .migrationHead, defined: .migrationTarget, pending: .migrationsPending}'
```

| `migrationsPending` | Schema vs this build | Allowed? | Do this |
| --- | --- | --- | --- |
| `0` | current | **Yes** | `kubectl rollout undo deployment/dubbing-api` + the CDN pin. Nothing else. |
| `> 0` | build is **ahead** of the schema | **Yes, and nothing is needed for the DB** | The migration Job did not run. `rollout undo` the API if the new pods are failing; the previous build tolerates the expanded schema because every new column is nullable. Then run the Job properly. |
| `0`, but `migrationHead` is **newer** than the build's `migrationTarget` | schema is **ahead** of the build — the contract phase ran | **NO** | The previous build cannot know which of its queries relied on what was removed. `MigrationCurrencyCheck` reports this as `ahead: true` and readiness is false, so the pod never serves. Forward-fix: deploy a build that knows the current schema. Rolling back the *frontend* is still safe — it is a different product. |
| any | the Job **failed** | **No rollback of anything** | The Job is idempotent. Fix the migration and re-run it. The previous release is still serving; nothing has changed. |

The third row is the one worth reading twice. "The database rolled back" is not
on the menu, and the only safe move is forward.

## When the gate refuses to roll back

`deploy/verify.sh` triggers a rollback on `HEALTH_FAILED`,
`OPENAPI_VERSION_MISMATCH` and `SMOKE_FAILED`. It records the triggering reason
separately from the verdict reason, so `SMOKE_FAILED` with a completed rollback
is a different incident from `SMOKE_FAILED` with no rollback configured.

`ROLLBACK_UNAVAILABLE` in the artifact means the rollback command was not
configured. That is a gate-configuration problem, not a rollback problem: run
the one-liners above by hand and then fix the job's `--rollback-command`.

## Incompatible rollback is refused

Before rolling back during a contract window, check the compatibility matrix.
The three questions:

1. Does the target build's `migrationTarget` match the current
   `migrationHead`? If not, the target build is behind the schema and **cannot
   run** — `MigrationCurrencyCheck` will keep it out of the Service.
2. Does the target frontend bundle call any endpoint the current API no longer
   serves? After a contract phase, it might. The frontend pin is only safe while
   the pinned document's API calls are all still present.
3. Is the target release still within the supported window? Two green releases
   is the minimum, per [`../rollout.md`](../rollout.md) §5.

If any answer is no, the rollback is refused **with a reason** — a refusal that
names which of the three failed is worth more than a rollback that starts serving
500s, and it is why this file exists rather than a one-line command.

## After the rollback

1. Confirm `/health` is `Healthy` on the restored version, and that
   `/health/ready` is 200 for all three replicas.
2. Confirm `/version` reports the intended release, not merely *a* release.
3. Re-run the gate on the restored version:
   `bash deploy/verify.sh --post-deploy --require-post-deploy --url ... --tag <restored>`
   and confirm `VERIFY_RESULT reason=OK`. A rollback that is not re-verified is
   an unverified state, not a restored one.
4. File the postmortem. A rollback is a **postmortem trigger**, not a close: the
   gate that should have caught it did not, and that is the finding.
5. Do **not** re-attempt the deploy until the root cause is understood. A deploy
   re-run after a rollback whose cause is unknown reproduces the rollback.

## Everything else

- **Workers**: `kubectl -n <ns> rollout undo deploy/worker-<name>`. Same
  reasoning, same confirmation.
- **KEDA / Ingress / policies**: re-apply the previous overlay revision
  (`kubectl apply -k deploy/k8s/overlays/<env>` from the prior commit). These are
  declarative, so the rollback is the previous manifest rather than a mutation.
- **The migration Job**: `kubectl -n <ns> delete job dubbing-migration`. A
  completed Job is a re-run; `ttlSecondsAfterFinished: 86400` cleans it up anyway.
- **Frontend only, backend fine**: the CDN pin alone. No API change, no gate
  re-run needed beyond confirming the document and its assets load.
