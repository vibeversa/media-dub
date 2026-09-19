> **REVIEW FIX — SUPERSEDED (split):** this combined file is superseded by `043A-hosting-env-cdn.md` (hosting/env/CDN) + `043B-rollout-rollback.md` (expand/contract + rollback) + `043C-runbooks-backup.md` (runbooks/backup drills). New work goes to 043A/B/C.

# Task 043 — Frontend Hosting, Env Config, Rollout, Operations

**Required/Optional:** Required
**Complexity:** M

## Goal
Ship the static frontend behind CDN with safe config injection and roll out the backend via migration-job-first deploys with rollback and runbooks.

## Context
CI (Task 042) produces versioned signed images; the frontend is a static SPA (`frontend/dist`), the backend a modular monolith with expand/contract migrations (Tasks 001–004); topology is Browser→CDN→Static→API with no direct DB/broker/Redis access. This task makes releases boring: compatible migrations, flag-gated UI, verified rollout, one-command rollback.

## Starting State
Task 042 done. Depends on Task 042.

## Scope
Included: CDN/static host, `VITE_*` injection, health/version endpoint, topology enforcement, migration-first rollout + compat + flags, rollback, runbooks, backup coverage.
Excluded: pipeline authorship (Task 042), E2E scenarios (Task 041), optional-enrichment surfaces (Task 044).

## Instructions
1. Create `Dockerfile.frontend` + `deploy/cdn/` (or provider equivalent): serve `frontend/dist` with SPA fallback (`/index.html` for unknown non-`/api` routes), hashed-asset immutable cache (`Cache-Control: public, max-age=31536000, immutable`), short HTML cache (`no-cache`), HTTPS + compression + CSP/security headers (matching Task 037 policy, no `unsafe-inline`).
2. Implement safe config injection in `frontend/src/config/env.ts` + `deploy/config-inject.sh`: only `VITE_*` non-sensitive keys baked per environment (API base URL, CDN origin, version tag); secrets rejected at build (`scripts/vite-env-audit.sh` fails on `SECRET|KEY|TOKEN|PASSWORD` in `VITE_*`); runtime version exposed at `/version.json` (version + commit + openapi version).
3. Add `GET /health` + `GET /version` in `src/DubbingPlatform.Api/Endpoints/HealthEndpoints.cs`: liveness (process up), readiness (DB + storage + broker reachable, migration current); readiness failure → container unhealthy, never traffic-serving; version reports build tag + migration head.
4. Enforce topology in `deploy/k8s/`: `networkpolicies.yaml` allowing only CDN→static, browser/CDN→API ingress; no ingress to DB/broker/Redis except from API + migration job; document the data-flow diagram in `docs/topology.md` (Browser→CDN→Static→API→{PG, storage, broker}).
5. Implement rollout in `deploy/k8s/migration-job.yaml` + `docs/rollout.md`: migration job runs before API rollout (expand phase), API deploys with backward compat to previous frontend (contract window ≥ 1 release), new UI behind flags (Task 036 `FlagsPanel`); contract phase (drop deprecated columns/endpoints) only after two green releases.
6. Document rollback in `docs/runbooks/rollback.md`: `kubectl rollout undo deployment/api` + frontend CDN version pin; DB expand/contract note — rollback never reverses a contract migration (forward-fix only) with the decision table for expand-vs-contract states.
7. Write runbooks in `docs/runbooks/`: `auth-outage.md`, `cdn-cache-poison.md`, `sse-degraded.md`, `deploy-failed.md`, `notification-backlog.md`, `contract-drift.md`, `upload-surge.md`, `review-surge.md` — each with symptoms, 5-minute triage, mitigation, escalation, and postmortem trigger.
8. Cover backup in `docs/runbooks/backup-restore.md`: scheduled snapshots for users/prefs/notifications/activity/memberships/previews/metadata with restore drill (quarterly) and RPO/RTO targets; media artifacts covered by storage versioning policy reference.

## Requirements
- R1: SPA fallback + hashed immutable cache + short HTML cache + HTTPS/compression/CSP headers verified by header assertions.
- R2: Only safe `VITE_*` keys injected; secrets in `VITE_*` fail the build; `/version.json` + `/version` agree.
- R3: Readiness gates traffic on DB/storage/broker/migration currency; liveness is process-only.
- R4: Topology enforced by network policy — no direct browser→DB/broker/Redis path exists.
- R5: Migration-job-before-API ordering with backward compat + flag-gated UI; contract phase only after two green releases.
- R6: One-command rollback for API + CDN pin; DB rollback follows expand/contract forward-fix rule.
- R7: All eight runbooks + backup/restore drill docs exist and are linked from the ops index.

## Edge Cases and Error Handling
- Migration job fails → API rollout blocked, previous version keeps serving, alert fires with job logs link.
- CDN serves stale HTML after deploy → version-mismatch banner prompts reload (client compares `/version.json` on focus).
- Flag service unavailable at boot → flags fail closed to last-known-good snapshot, UI marks flagged areas `DegradedState`.
- Backup restore drill finds gap (e.g. previews missing) → gap logged as release-blocker until policy fixed.
- Rollback during contract window → compatibility matrix checked first; incompatible rollback refused with reason.

## Security and Safety Requirements
- No secrets in images, `VITE_*` bundles, CDN configs, or runbooks; header audit in CI (Task 042) covers CSP/CORS.
- Network policies default-deny with explicit allows; migration job uses least-privilege DB role.
- Backup media encrypted at rest; restore drill access logged and time-boxed.

## Testing
- Header/topology assertions in `deploy/tests/hosting.test.sh` (fallback, cache headers, CSP, HTTPS redirect).
- Rollout dry-run: `kubectl apply --dry-run=client -f deploy/k8s/` + migration-job ordering test on ephemeral cluster.
- Runbook drill: tabletop walkthrough recorded per release; findings filed as issues.
- Type: shell assertions + k8s dry-run + drill.

## Validation
```bash
cd frontend && npm run build
docker build -f Dockerfile.frontend .
kubectl apply --dry-run=client -f deploy/k8s/
bash deploy/tests/hosting.test.sh
```

## Completion Criteria
- CDN-hosted SPA with safe config, gated readiness, enforced topology, migration-first rollout, one-command rollback, eight runbooks, and backup drills; build + image + dry-run + hosting tests pass.

## Traceability
- Plan B §17.1–§17.4, §18.1–§18.4, §23.5. Depends on Task 042.

