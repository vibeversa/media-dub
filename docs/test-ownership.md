# Test Ownership (Tasks 046 / 039A)

One page: who authors which specs. Feature tasks own their slices against the
shared harness; aggregate tasks own gap closure, seams, and gates only.

## Rule

- Feature tasks (006–036) author their own unit, component, integration, and
  E2E specs against the harness (`frontend/src/mocks/` taxonomy,
  `frontend/vite.config.ts` coverage, `tests/coverage.runsettings`).
- Tasks 039A/B/C, 040A/B, 041A–D never author feature specs: 039A owns
  coverage config + MSW conformance + gap/presence reporting; 039B/039C close
  the measured gaps; 040A/B own cross-layer seams; 041A–D own
  journeys/visual/a11y/perf gates.

## Frontend areas → owning task → spec location

| Area | Owner | Specs live under |
| --- | --- | --- |
| `src/app` | 018 | `src/app/**/__tests__/` |
| `src/components` | 016 | per-component `*.test.tsx` |
| `src/api` (excl. `generated/`) | 017 | `src/api/__tests__/` |
| `src/hooks` | 026 | `src/hooks/*/__tests__/` |
| `src/i18n` | 018 (framework) / 045 (pseudo, RTL, fallback chain) | `src/i18n/__tests__/`, `src/i18n/pseudo.spec.tsx` |
| `src/lib` | 015 (env) / 045 (`dates/`, `formatting/`) | `src/lib/__tests__/`, `src/lib/dates/dates.spec.ts`, `src/lib/formatting/formatting.spec.ts` |
| `src/mocks` | 039A | `src/mocks/conformance.spec.ts` |
| `src/stores` | 018 | exercised via importing suites (no dedicated spec) |
| `src/telemetry` | 038 | `src/telemetry/__tests__/` |
| `src/features/activity` | 035A | `src/features/activity/__tests__/` |
| `src/features/admin` | 036 | `src/features/admin/__tests__/` |
| `src/features/auth` | 019 | `src/features/auth/__tests__/` |
| `src/features/cost` | 035A | `src/features/cost/__tests__/` |
| `src/features/dashboard` | 020 | `src/features/dashboard/__tests__/` |
| `src/features/enrichment` | 044 | `src/features/enrichment/__tests__/` |
| `src/features/exports` | 033 | `src/features/exports/__tests__/` |
| `src/features/notifications` | 034 | `src/features/notifications/__tests__/` |
| `src/features/processing` | 024 | `src/features/processing/__tests__/` |
| `src/features/projects` (incl. `wizard/`) | 021 / 022 | `src/features/projects/__tests__/` + `wizard/__tests__/` |
| `src/features/quality` | 032 | `src/features/quality/__tests__/` |
| `src/features/review` | 031 | `src/features/review/__tests__/` |
| `src/features/settings` | 035B | `src/features/settings/__tests__/` |
| `src/features/timeline` | 030 | `src/features/timeline/__tests__/` |
| `src/features/transcript` | 027 | `src/features/transcript/__tests__/` |
| `src/features/translation` | 028 | `src/features/translation/__tests__/` |
| `src/features/uploads` | 023 | `src/features/uploads/__tests__/` |
| `src/features/voices` | 029 | `src/features/voices/__tests__/` |

Excluded from the presence gate by policy: `src/api/generated` (generated,
Task 014), `src/types` and `src/styles` (no runtime logic).

## Repository-level gate specs (Task 045)

Not feature areas, and therefore not in the table above:

| Gate | Owner | Spec |
| --- | --- | --- |
| Hard-coded copy extraction + RTL physical sides (`scripts/check-no-hardcoded-copy.mjs`) | 045 | `deploy/frontend/hardcoded-copy.test.mjs` (`npm run check:frontend`) |
| Frontend infrastructure topology (043A) | 043A | `deploy/frontend/topology.test.mjs` |

## Presence gate (039A R4)

`node scripts/presence-gate.mjs` fails CI (`PRESENCE_GAP:<area> owned by
<task>`) when an area above has zero specs and no spec imports it. It authors
nothing — the named owner closes the gap. Backend presence is owned by 039C.

## Shared rules (all tasks)

- Fixtures are synthetic only; the PII scrubber semantics from Task 038 apply
  (no tenant/user ids, tokens, URLs, media bytes, or transcript text in
  fixtures, snapshots, or telemetry payloads).
- Seeded credentials are ephemeral per run, never committed.
- MSW failure shapes come from `frontend/src/mocks/` (eleven taxonomy
  outcomes); feature suites reuse them instead of redefining envelopes.
- No silent retries; quarantine needs owner + issue + expiry.
