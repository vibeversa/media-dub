# Task 043A — Frontend Hosting, Env Config, CDN

**Required/Optional:** Required
**Complexity:** M

## Goal
Ship the static frontend behind CDN/ingress with safe env injection and correct caching/headers.

## Context
Split from oversized Task 043 (hosting + rollout + runbooks). This task is the hosting slice: static assets, SPA fallback, immutable hashed-asset caching, short-lived HTML, HTTPS/compression/CSP headers, `VITE_*` injection, health/version endpoint. Rollout/rollback moves to 043B; runbooks/backup to 043C. Topology constraint: Browser → CDN/Ingress → Static Host → API; frontend never talks directly to PG/RabbitMQ/Redis/workers.

## Starting State
Depends on Tasks 015 (build output), 042A (basic CI green), 045 (locale bundles ship here). Task 043 (combined) is superseded by 043A + 043B + 043C.

## Scope
Included: `Dockerfile.frontend` (or static-host config), CDN/ingress manifests, env allowlist, caching + headers, health/version endpoint.
Excluded: migration rollout/rollback (043B), runbooks/backup drills (043C), backend CI gates (042B).

## Instructions
1. Add `Dockerfile.frontend` (nginx or equivalent static host): multi-stage (`npm run build` → static root), SPA fallback (`/index.html`), immutable caching for hashed assets (`Cache-Control: public, max-age=31536000, immutable`), short-lived HTML (`no-cache`), gzip/brotli, HTTPS redirect at ingress, CSP headers (from 037 policy), `X-Content-Type-Options: nosniff`.
2. Add `deploy/k8s/frontend/` (or CDN equivalent): `deployment.yaml` (2+ replicas), `service.yaml`, `ingress.yaml` (TLS, frontend host), `configmap.yaml` for non-secret `VITE_*` (`VITE_API_BASE_URL, VITE_ENVIRONMENT, VITE_APP_VERSION, VITE_ENABLE_ANALYTICS, VITE_ENABLE_DIAGNOSTICS, VITE_ENABLE_EXPERIMENTAL_FEATURES, VITE_SENTRY_DSN`); never secrets; document injection at deploy time (build-arg vs runtime-config choice recorded).
3. Add health/version endpoint: `/healthz` (static 200) + `/version.json` (`{version, commit, builtAt}`) where practical; CDN availability + frontend error rate feed 038/043C monitors.
4. Verify topology: frontend calls API origin only (allowlist); add `scripts/check-frontend-topology.mjs` failing on direct DB/broker/redis/worker references in `frontend/src`.
5. Document in `deploy/frontend/README.md`: hosting diagram, cache policy, env table, SPA fallback behavior, rollback pointer (043B).

## Requirements
- R1: Static hosting serves SPA fallback correctly (deep links work).
- R2: Hashed assets immutable-cached; HTML short-lived; HTTPS + compression + CSP headers present.
- R3: Only documented `VITE_*` keys injected; no secrets in image or configmap.
- R4: Topology check passes (no direct infra references from frontend).
- R5: Health/version endpoints reachable through CDN/ingress.

## Edge Cases and Error Handling
- Stale HTML cached at edge → version mismatch banner (API `X-App-Version` vs `/version.json`) prompts reload, never silent breakage.
- Missing `VITE_API_BASE_URL` → build fails closed, not runtime undefined-origin calls.
- CDN outage → 043C runbook owns response; this task only ensures origin serves directly as fallback where configured.

## Security and Safety Requirements
- CSP from 037 enforced at hosting layer; CORS allowlist (backend) matches frontend origin exactly, no wildcard for authed endpoints.
- No source maps with secrets to CDN in prod (or access-restricted where kept).

## Testing
- Config tests: `deploy/frontend/*.test.mjs` (or terratest equivalent) asserting cache headers, fallback routes, header presence, env allowlist.
- Topology: `scripts/check-frontend-topology.mjs` in validation.

## Validation
```bash
npm run build --prefix frontend
docker build -f Dockerfile.frontend -t dubbing-frontend:check .
node scripts/check-frontend-topology.mjs
kubectl apply --dry-run=client -f deploy/k8s/frontend/
```

## Completion Criteria
- Hosting + CDN + env + headers + topology check exist and dry-run valid; supersedes the hosting third of Task 043.

## Traceability
- Plan B §17.1–§17.2. Split from 043; rollout in 043B; runbooks/backup in 043C.
