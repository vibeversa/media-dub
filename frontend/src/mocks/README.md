# Frontend mocks (Task 046)

The shared MSW harness: the eleven documented API outcomes, the handler
factories feature suites compose, the fixture factories, and the lifecycle
helpers. Owned by Task 046; `conformance.spec.ts` remains 039A's proof that the
taxonomy contract itself holds.

## Files

| File | What it is |
| --- | --- |
| `taxonomy.ts` | The eleven outcomes and the envelope contract (`isErrorEnvelope`). Single source of truth for paths, statuses and codes. |
| `handlers.ts` | `taxonomyHandlers` (one GET per outcome on a probe path) plus `taxonomyHandler` / `successHandler` / `noContentHandler`, which put an outcome on the real route a component calls. |
| `server.ts` | The shared `setupServer` instance and `installMocks` / `useMocks` / `resetMocks` / `uninstallMocks`. |
| `fixtures.ts` | Synthetic tenant / users / project / run / segment / review item, derived from a seed. |
| `handlers.spec.ts` | The harness smoke: taxonomy coverage, handler factories, fixture isolation, PII. |
| `conformance.spec.ts` | 039A's per-entry envelope conformance. Kept. |

## Writing a spec against it

```ts
import { afterAll, afterEach, beforeAll, expect, it } from 'vitest';
import { installMocks, resetMocks, taxonomyHandler, successHandler, uninstallMocks } from './server.js';
import { buildFixtures } from './fixtures.js';

const fixtures = buildFixtures();

beforeAll(() => installMocks([
  successHandler('get', '/api/v1/projects', { items: [fixtures.project], page: 1 }),
  taxonomyHandler('get', '/api/v1/projects/p1', 'forbidden-403'),
]));

afterEach(resetMocks);      // NOT optional: see below
afterAll(uninstallMocks);
```

Three things to get right:

1. **`resetMocks()` in `afterEach`, not only `afterAll`.** MSW's
   `resetHandlers()` restores only the list handed to `setupServer(...)`, so
   `server.ts` tracks what `installMocks` installed and re-applies it. Without
   that, a per-test override survives into the next test and the failure is "this
   test passed with the previous test's response" — which reads as a flake and is
   neither reproducible nor a flake.
2. **Never inline an envelope.** `taxonomyHandler('get', path, 'conflict-409')`
   serves byte-identical JSON to the taxonomy probe. Inlining creates a second
   copy of the envelope, and the two copies drift.
3. **Unmocked requests fail, they do not pass through.** `installMocks` defaults
   `onUnhandledRequest` to `'error'`. MSW's own default is `'warn'`, which lets
   the request escape to the real network — that is how a unit suite passes on a
   laptop with a dev API up and fails in CI with an opaque `ECONNREFUSED`.

## The taxonomy

| id | status | code | envelope |
| --- | --- | --- | --- |
| `success` | 200 | — | `{ data, correlationId }` |
| `unauthorized-401` | 401 | `TOKEN_EXPIRED` | `{ error: { code, message, correlationId, details } }` |
| `forbidden-403` | 403 | `FORBIDDEN` | error envelope |
| `not-found-404` | 404 | `PROJECT_NOT_FOUND` | error envelope |
| `conflict-409` | 409 | `CONFLICT` | error envelope |
| `rate-limited-429` | 429 | `RATE_LIMITED` | error envelope |
| `internal-500` | 500 | `INTERNAL_ERROR` | error envelope (generic message) |
| `validation` | 400 | `VALIDATION_FAILED` | error envelope with field details |
| `provider-error` | 502 | `PROVIDER_FAILED` | error envelope |
| `partial` | 200 | — | `{ data, partial: true, warnings, correlationId }` |
| `stale-conflict` | 409 | `SELECTION_CONFLICT` | error envelope with `{ currentSelectionVersion, currentVersionIds }` |

## Failure modes, named

| Error | Means |
| --- | --- |
| `MSW_HANDLER_MISSING:<id>` | No handler serves that outcome. Thrown by `missingHandlerError` / `assertHandlerExists`. |
| `MSW_ENVELOPE_INCORRECT:<id>` | A handler answered, but not with the documented envelope. |
| "Cannot bypass a request when using the 'error' strategy" | `resetMocks` was not called, so the suite's handlers were dropped. See note 1 above. |

**Call `assertHandlerExists(id)` at the top of a test that composes a failure
shape.** The alternative is discovering the gap because a component rendered
nothing and the assertion on "no error banner" passed — a screen that never
resolves is indistinguishable from one that resolved to nothing.

## Rules

- Fixtures are synthetic only: RFC 2606 reserved email domains
  (`fixtures.invalid`), derived ids, frozen timestamps. `handlers.spec.ts`
  asserts this against the same shapes the backend's `PiiScrubber` enforces.
- Retry is forbidden in conformance (one fetch per entry); a retry would mask a
  missing or flaky handler.
- MSW is lockfile-pinned (`2.15.0`); upgrades must keep the
  `MSW_HANDLER_MISSING` failure mode and re-run
  `npm run test --prefix frontend -- src/mocks`.
- The harness does **not** start MSW globally. `frontend/vite.config.ts`
  deliberately leaves `setupFiles` alone: ~1760 tests use
  `setInnerFetchForTests` and never reach the network, and putting them all
  behind a request interceptor would make an unmocked request in a pure
  state-machine suite fail for a reason that has nothing to do with it.