# Deploy failed

A rollout did not complete, or completed and then failed its verification.

## Symptoms

- `kubectl rollout status` times out, or a Deployment sits part-way through a
  rolling update.
- `deploy/verify.sh --post-deploy` exits non-zero. The reason is on the last
  line as `VERIFY_RESULT reason=<REASON> status=FAIL exit=<n>`, and the
  per-check detail is in `deploy/.artifacts/verify-<timestamp>.json`.
- The migration Job failed or is retrying.
- Pods are `CrashLoopBackOff`, `ImagePullBackOff`, or `0/3 Ready`.

## Five-minute triage

**Read the reason before reading anything else.** The reason code decides which
of the four branches below applies, and the three most common ones have opposite
responses:

| Reason | What it means | Do not |
| --- | --- | --- |
| `HEALTH_FAILED` | a probe did not return 200 — the rollout or the migration | do not roll forward on a green liveness alone |
| `OPENAPI_VERSION_MISMATCH` | the wrong artefact is deployed, or the rollout is partial | do not assume the new image is broken |
| `SMOKE_FAILED` | health and version both passed; the behaviour is wrong | do not re-run the deploy — it will fail identically |
| `IMAGE_BUILD_FAILED` | the image never built; nothing was deployed | do not roll anything back; the previous release is still serving |

```bash
# 1. What does the cluster think happened?
kubectl -n <ns> get pods -l app.kubernetes.io/component=api
kubectl -n <ns> describe pod -l app.kubernetes.io/component=api | tail -40

# 2. Did the migration run, and did it complete?
kubectl -n <ns> get job dubbing-migration
kubectl -n <ns> logs job/dubbing-migration --tail=50

# 3. What build is actually running?
curl -fsS https://api.<env>/version | jq
```

Step 2 is the one to do first after reading the reason. The
`wait-for-migrations` initContainer runs the same bundle as the Job, so a
migration failure blocks the pods from starting at all — and the pod's event
message will be an init-container failure, not an application crash. An
investigation that starts in the application logs has skipped the step that
explains the symptom.

## Mitigation

**Migration failed** (`HEALTH_FAILED`, Job not `Complete`):

The previous release keeps serving. Fix the migration forward and re-run the Job
— it is idempotent, so a re-run is safe:

```bash
kubectl -n <ns> delete job dubbing-migration --ignore-not-found
kubectl apply -f deploy/k8s/migration-job.yaml
kubectl -n <ns> wait --for=condition=complete --timeout=600s job/dubbing-migration
```

**Never reverse a migration.** See [`rollback.md`](rollback.md#the-database-never).
The Job failed, so nothing changed; there is nothing to undo.

**Image pull / build failed** (`IMAGE_BUILD_FAILED`, `ImagePullBackOff`):

Nothing was deployed. Fix the build or the tag. Verify the image exists and its
signature verifies before re-applying:

```bash
cosign verify ghcr.io/CHANGE_ME/dubbing-api:$TAG \
  --certificate-identity-regexp 'https://github\.com/CHANGE_ME/media-dub/\.github/workflows/backend\.yml@refs/tags/v.*' \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com
```

`ImagePullBackOff` on a tag that CI published is a registry-credential or
`imagePullPolicy` problem, not a missing image. `imagePullPolicy: Always` with a
`:latest` tag can also pull a *different* image than the one verified — which is
why the prod overlay pins release tags and `:latest` is not used there.

**Wrong image deployed** (`OPENAPI_VERSION_MISMATCH`):

Roll back, then find out how. `rollout undo` moves the Deployment; the `/version`
check is what proves the pods are the intended build:

```bash
kubectl -n <ns> rollout undo deployment/api
kubectl -n <ns> rollout status deployment/api
curl -fsS https://api.<env>/version | jq -r '.version, .commit'
```

**Behaviour wrong** (`SMOKE_FAILED`):

Both health and the version passed, so the deployment is up and is the expected
build. Rolling back hides the bug and leaves the bug. Roll back to restore
service, then diagnose against the previous version with the new one available
for comparison. Do not re-run the deploy.

**Pods never ready**: check the readiness probe's own body, which now includes
migration currency:

```bash
kubectl -n <ns> port-forward deploy/api 8080:8080 &
curl -sS http://127.0.0.1:8080/health | jq
```

`readiness: "Unhealthy"` with `migrationsPending > 0` means the Job did not run.
`"ahead": true` means the schema is newer than this build — the contract phase
ran, and this build must not serve (see [`rollback.md`](rollback.md#decision-table)).

## Escalation

- **L1 (0–30 min)**: everything above. A failed deploy with the previous release
  serving is not an incident; it is a release with an outcome.
- **L2 (1 h)**: a failed deploy that could not be resolved, or a deploy that took
  the API below its minimum replicas. Page platform. If the migration is the
  blocker, the DBA is needed — see [`db-failover.md`](db-failover.md).
- **L3 (4 h)**: prod is degraded (not merely "not updated"), or the same release
  has failed twice.

## Postmortem trigger

Any of:

- a deploy that reached prod and failed verification;
- a migration failure in any environment — a migration that fails staging is a
  migration that will fail prod, and the second one costs a release window;
- a rollback performed;
- a gate that was skipped, or that passed and should not have.

A failed staging deploy that never reached prod still gets one, because the
question worth asking is why it was not caught earlier — and the answer is
usually a step of [`../rollout.md`](../rollout.md) that is written but not
automated.
