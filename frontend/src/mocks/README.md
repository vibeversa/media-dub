# Frontend mocks (Tasks 039A / 046)

Shared MSW taxonomy for API failure-shape testing. Owned by Task 039A until
Task 046 lands its full harness; feature suites (039B) build on this taxonomy
and must not redefine envelopes.

## Files

- `taxonomy.ts` — the eleven documented outcomes and the envelope contract
  (`isErrorEnvelope`). Single source of truth for paths, statuses, codes.
- `handlers.ts` — one `http.get` per taxonomy entry on the fixed
  `MOCK_BASE_URL` (`http://localhost:5000`, the `httpClient` hermetic
  fallback). No TCP port is bound: `msw/node` intercepts at the request layer,
  so port-collision flakes cannot occur. No retry is configured.
- `server.ts` — the shared `setupServer(...taxonomyHandlers)` instance.
- `conformance.spec.ts` — asserts every entry is served with the documented
  envelope. Missing handler fails as `MSW_HANDLER_MISSING:<id>`; wrong shape
  fails as `MSW_ENVELOPE_INCORRECT:<id>`.

## Taxonomy

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

## Rules

- Fixtures are synthetic only: fixed `corr-taxonomy-*` correlation IDs, plain
  messages, no tenants/users/tokens/URLs/media/transcript content.
- Retry is forbidden in conformance (one fetch per entry); a retry would mask
  a missing or flaky handler.
- `msw` is lockfile-pinned (`2.15.0`); upgrades must keep the
  `MSW_HANDLER_MISSING` failure mode and re-run
  `npm run test --prefix frontend -- src/mocks/conformance.spec.ts`.
