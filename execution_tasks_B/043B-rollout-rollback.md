# Task 043B — Expand/Contract Rollout and Rollback

**Required/Optional:** Required
**Complexity:** M

## Goal
Roll out backend extensions with expand/contract compatibility and a rehearsed rollback path.

## Context
Split from Task 043. Backend extensions (001–005, 008) must deploy over the Plan A system without downtime: additive migrations first, backward-compatible API/workers during the window, flag-gated UI exposure, then contraction. This task owns the release-engineering slice; hosting is 043A, runbooks/backup 043C.

## Starting State
Depends on Tasks 001–005 (migrations exist), 042B or 042A (CI green for the release cut), 043A (hosting target exists). Task 043 (combined) is superseded.

## Scope
Included: migration-job-before-API ordering, expand/contract window, backward-compat verification, flag-gated UI, rollback procedure + rehearsal record.
Excluded: hosting/CDN/env (043A), runbooks/backup drills (043C), full CI gates (042B).

## Instructions
1. Add `deploy/k8s/migration-job.yaml` (or reuse Plan A job): runs additive migrations before API rollout (`initContainer` wait or ArgoCD/helm pre-upgrade hook, `backoffLimit:3`); migration failure blocks rollout (API Deployment waits).
2. Enforce expand/contract window of one release: migrations additive-only (nullable columns / new tables); old API/worker code tolerates new schema; new code tolerates old schema without new columns; record the window in `deploy/rollout.md` with the contraction follow-up task.
3. Verify backward compat in CI staging: deploy new DB + old API image smoke (`/health/ready` + one workspace read), then new API; document matrix in `deploy/rollout.md`.
4. Flag-gate new UI surfaces (043A hosting serves both): feature flags are config-driven rollout only, never authorization (per §6.9); list flags + default states in `deploy/rollout.md`.
5. Document + rehearse rollback: `kubectl rollout undo` for API/workers/frontend, DB rollback = forward-fix only (no down-migration on prod data) unless explicitly approved; record last rehearsal date + result in `deploy/rollout.md`.

## Requirements
- R1: Migration job succeeds before API ready; failure blocks rollout.
- R2: Additive-only migrations during the window (CI check or reviewer gate).
- R3: Old-code/new-schema and new-code/old-schema smoke passes in staging.
- R4: Flags documented with defaults; no flag replaces authorization.
- R5: Rollback rehearsed and recorded (not just documented).

## Edge Cases and Error Handling
- Migration needs non-additive change → split into two releases (expand then contract); single-release destructive migration rejected.
- Rollback during active runs → runs continue on durable backend state; UI shows pre-existing version until re-rollout (no run cancellation by rollback).
- Flag left on permanently → expiry review date required for every flag.

## Security and Safety Requirements
- Migration job uses least-privilege DB role; secrets via external manager, never in manifests.
- Rollback does not restore deleted data (retention/deletion semantics from Plan A preserved).

## Testing
- Release tests: staging compat matrix (old-API/new-DB, new-API/old-DB where applicable) + rollback rehearsal log.
- Type: deploy verification, not unit.

## Validation
```bash
kubectl apply --dry-run=client -f deploy/k8s/
bash deploy/verify.sh
kubectl rollout history deployment/dubbing-api
```

## Completion Criteria
- Migration ordering + compat window + flag list + rehearsed rollback exist and are recorded; supersedes the rollout third of Task 043.

## Traceability
- Plan B §2.3, §6.9, §17.3, §23.5 (rollout slice). Split from 043; hosting in 043A; runbooks in 043C.
