# Test Ownership (Tasks 046 / 039A)

One page: who authors which specs. Feature tasks own their slices against the
shared harness; aggregate tasks own gap closure, seams, and gates only.

## Rule

- **No feature task is blocked waiting for 039–041 to author its specs.** The
  harness is an executable prerequisite owned by 046, not something the aggregate
  tasks retrofitted. A feature task that needs a failure shape, a synthetic
  graph or a signed-in session has all three today.
- Feature tasks (006–036) author their own unit, component, integration, and
  E2E specs against the harness (`frontend/src/mocks/`, `e2e/support/`,
  `tests/DubbingPlatform.TestFixtures/`, `frontend/vite.config.ts` coverage,
  `tests/coverage.runsettings`).
- Tasks 039A/B/C, 040A/B, 041A–D never author feature specs: 039A owns
  coverage config + MSW conformance + gap/presence reporting; 039B/039C close
  the measured gaps; 040A/B own cross-layer seams; 041A–D own
  journeys/visual/a11y/perf gates. They are **gap closure over what already
  exists**, not the first authorship of anything.

## Frontend areas → owning task → spec location

| Area | Owner | Specs live under |
| --- | --- | --- |
| `src/app` | 018 | `src/app/**/__tests__/` |
| `src/components` | 016 | per-component `*.test.tsx` |
| `src/api` (excl. `generated/`) | 017 | `src/api/__tests__/` |
| `src/hooks` | 026 | `src/hooks/*/__tests__/` |
| `src/i18n` | 018 (framework) / 045 (pseudo, RTL, fallback chain) | `src/i18n/__tests__/`, `src/i18n/pseudo.spec.tsx` |
| `src/lib` | 015 (env) / 045 (`dates/`, `formatting/`) | `src/lib/__tests__/`, `src/lib/dates/dates.spec.ts`, `src/lib/formatting/formatting.spec.ts` |
| `src/mocks` | 046 (harness) / 039A (taxonomy conformance) | `src/mocks/handlers.spec.ts`, `src/mocks/conformance.spec.ts` |
| `src/test` | 046 (harness) | loaded by every suite via `setupFiles`; no spec of its own |
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

## The harness (Task 046)

Feature tasks write their own specs against this. It is an executable
prerequisite, not something a later aggregate task retrofits.

| Surface | Path | What it gives a feature task |
| --- | --- | --- |
| Vitest setup | `frontend/src/test/setup.ts` | The jsdom `Request` shim every suite needs, loaded once via `setupFiles`. |
| MSW taxonomy | `frontend/src/mocks/taxonomy.ts` | The eleven documented outcomes and the frozen envelope contract. **Do not redefine an envelope.** |
| MSW handlers | `frontend/src/mocks/handlers.ts` | `taxonomyHandler` / `successHandler` / `noContentHandler` compose a failure shape onto a real route in three lines. |
| MSW lifecycle | `frontend/src/mocks/server.ts` | `installMocks` / `useMocks` / `resetMocks` / `uninstallMocks`. Opt-in, never global. |
| Synthetic fixtures | `frontend/src/mocks/fixtures.ts` | `buildFixtures(seed)` → tenant, five role users, project, run, segment, review item. Deterministic and tenant-isolated. |
| Playwright config | `e2e/playwright.config.ts` | Three browsers, the `TAGS` vocabulary, `@smoke`/`@visual` selection. |
| E2E support | `e2e/support/{config,auth,reset,sse-waits}.ts` | Seeded auth for every role, tenant-scoped PG + storage reset, event-driven waits. |
| Backend fixtures | `tests/DubbingPlatform.TestFixtures/` | `SyntheticEnvironments.BuildForWorker(seed)` → the same graph as the frontend fixtures, wired and referentially sound. |
| PII scrubber | `PiiScrubber` | `AssertClean(text, where)` on any fixture, log line or captured string. |

### The two rules that are not negotiable

- **MSW failure shapes come from the taxonomy.** A feature suite that inlines
  `{ error: { code, message, correlationId, details } }` has created a second copy
  of the envelope, and the two copies drift. Use `taxonomyHandler(method, path, id)`.
- **A missing handler must fail as `MSW_HANDLER_MISSING:<id>`**, naming the
  taxonomy entry — never as a generic network error. `assertHandlerExists(id)`
  checks it up front rather than discovering it through a timed-out render, and
  the reason it exists is that *a screen that never resolves looks exactly like a
  screen that resolved to nothing*.

## Repository-level gate specs (Tasks 045 / 046)

Not feature areas, and therefore not in the table above:

| Gate | Owner | Spec |
| --- | --- | --- |
| Hard-coded copy extraction + RTL physical sides (`scripts/check-no-hardcoded-copy.mjs`) | 045 | `deploy/frontend/hardcoded-copy.test.mjs` (`npm run check:frontend`) |
| Frontend infrastructure topology (043A) | 043A | `deploy/frontend/topology.test.mjs` |
| Shared test harness (configs, taxonomy, factories, scrubber) | 046 | `frontend/src/mocks/handlers.spec.ts`, `e2e/support/smoke.spec.ts`, `tests/DubbingPlatform.TestFixtures.Tests/TestFixturesTests.cs` |

## Presence gate (039A R4)

`node scripts/presence-gate.mjs` fails CI (`PRESENCE_GAP:<area> owned by
<task>`) when an area above has zero specs and no spec imports it. It authors
nothing — the named owner closes the gap. Backend presence is owned by 039C.

## Shared rules (all tasks)

- Fixtures are synthetic only, and that is enforced rather than agreed:
  `PiiScrubber.AssertClean` runs over every fixture in `TestFixturesTests`, and
  the frontend half in `handlers.spec.ts`. Reserved email domains only, derived
  ids only, no tokens or credentials in any fixture or snapshot.
- Seeded credentials are ephemeral per run, never committed.
  `e2e/support/config.ts` asserts at import time that no storage credential is
  anything other than `CHANGE_ME`.
- MSW failure shapes come from `frontend/src/mocks/` (eleven taxonomy
  outcomes); feature suites reuse them instead of redefining envelopes.
- No silent retries. A retry is a quarantine with no owner and no expiry, which
  is the suppression `e2e/support/quarantine.md` exists to forbid.
- Tenant isolation is per worker: pass `seedForWorker(workerIndex)` (or any
  distinct GUID) so two workers cannot collide on an id, a slug, a storage key
  or an email.
