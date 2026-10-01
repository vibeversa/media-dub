// Task 046: the MSW handler layer.
//
// TWO LAYERS, ON PURPOSE
// ----------------------
// 1. `taxonomyHandlers` - one GET per taxonomy entry on a fixed probe path.
//    These are the eleven *outcomes*, and they exist so a feature suite can ask
//    "what does this API look like when it is a 409?" without inventing an
//    envelope. They are the shared vocabulary (039A's conformance suite proves
//    each one).
//
// 2. `apiHandlers` / `mockApi` - feature-shaped handlers on the real
//    `/api/v1/...` paths a component actually calls. A suite composes these with
//    the taxonomy *bodies*, so the wire format has exactly one definition and a
//    failure-shape test is three lines instead of thirty.
//
// WHY THE PROBE PATHS EXIST AT ALL
// --------------------------------
// Because a taxonomy outcome is a *response*, not a route. `/__mocks__/partial`
// is not something the product serves; it is how a test obtains a `partial: true`
// envelope to assert against. Serving it from a real route would mean every
// outcome needed its own fake product endpoint, and the eleven would drift.
//
// THE FAILURE MODES, NAMED
// -------------------------
// A missing handler must fail as `MSW_HANDLER_MISSING` and name what was
// missing. MSW's own `onUnhandledRequest: 'error'` reports the URL, which is
// enough to find the bug and not enough to tell you the suite expected an
// outcome. `missingHandlerError` below rewrites that message into the taxonomy
// vocabulary so a failure says which outcome a test asked for, and
// `assertHandlerExists` lets a suite assert coverage up front rather than
// discovering it through a timed-out render.

import { http, HttpResponse, type HttpHandler, type JsonBodyType } from 'msw';

import { MOCK_BASE_URL, TAXONOMY, type TaxonomyEntry, type TaxonomyId } from './taxonomy.js';

/** The prefix every taxonomy probe path lives under. */
export const MOCK_PATH_PREFIX = '/__mocks__/taxonomy';

/**
 * One `http.get` per taxonomy entry, in `TAXONOMY` order.
 *
 * Length equals `TAXONOMY.length`; the conformance suite asserts it, so adding
 * an outcome to `taxonomy.ts` without a handler here fails the build rather than
 * producing an entry that no test can reach.
 */
export const taxonomyHandlers: readonly HttpHandler[] = TAXONOMY.map((entry) =>
  http.get(`${MOCK_BASE_URL}${entry.path}`, () => HttpResponse.json(entry.body, { status: entry.status })),
);

/**
 * The error thrown (well: passed to `expect.unreachable`) when a taxonomy entry
 * has no handler.
 *
 * Prefixed so it is greppable, and suffixed with the id so the failure names the
 * missing outcome instead of reporting a generic network error - which is the
 * whole point of Task 046's edge case "MSW handler missing -> test fails with
 * MSW_HANDLER_MISSING naming the taxonomy entry".
 */
export function missingHandlerError(id: TaxonomyId | string, detail = ''): string {
  const suffix = detail.length === 0 ? '' : `\n${detail}`;
  return `MSW_HANDLER_MISSING:${id} — no handler serves the '${id}' outcome. Add it to TAXONOMY (taxonomy.ts) and to handlers.ts.${suffix}`;
}

/**
 * The error reported when a handler answers with the wrong envelope.
 *
 * Distinct from `MSW_HANDLER_MISSING` on purpose: a present-but-wrong handler is
 * a different bug with a different fix, and collapsing the two would send someone
 * to add a handler that already exists.
 */
export function incorrectEnvelopeError(id: TaxonomyId | string, detail = ''): string {
  const suffix = detail.length === 0 ? '' : `\n${detail}`;
  return `MSW_ENVELOPE_INCORRECT:${id} — a handler answered, but not with the documented envelope for '${id}'. Check TAXONOMY[].body in taxonomy.ts.${suffix}`;
}

/**
 * Every taxonomy id that currently has a handler.
 *
 * <b>What this deliberately does not do:</b> inspect the handler list to find
 * out which paths are served. MSW does not expose a handler's resolved path, and
 * MSW 2.x matches the most recently added matching handler rather than a map -
 * so "which ids are covered" cannot be read back off the server. It is derived
 * from `TAXONOMY` instead, and that derivation is only sound because
 * `taxonomyHandlers.length === TAXONOMY.length`, which `handlers.spec.ts`
 * asserts. Read it as "the eleven outcomes are all addressable", not as "this
 * function inspected the server".
 */
export function coveredTaxonomyIds(): readonly TaxonomyId[] {
  return TAXONOMY.map((entry) => entry.id);
}

/**
 * Fails loudly when a suite asks for an outcome that has no handler.
 *
 * Call it at the top of a test that composes a failure shape. The alternative -
 * discovering the gap because a component rendered nothing and the assertion on
 * "no error banner" passed - is the failure mode this exists to prevent: **a
 * screen that never resolves looks exactly like a screen that resolved to
 * nothing**, which is 039B Finding 6 in one sentence.
 *
 * @param id - The taxonomy id the test depends on.
 */
export function assertHandlerExists(id: TaxonomyId): void {
  const entry: TaxonomyEntry | undefined = TAXONOMY.find((candidate) => candidate.id === id);
  if (entry === undefined) {
    throw new Error(
      `MSW_HANDLER_MISSING:${id} — no such taxonomy entry. The eleven documented ids are: ` +
        TAXONOMY.map((candidate) => candidate.id).join(', '),
    );
  }
  if (taxonomyHandlers.length !== TAXONOMY.length) {
    throw new Error(
      `MSW_HANDLER_MISSING:${id} — ${taxonomyHandlers.length} handler(s) for ${TAXONOMY.length} taxonomy ` +
        'entries. Add the missing handler to handlers.ts.',
    );
  }
}

// ---------------------------------------------------------------------------
// Feature-shaped handlers
// ---------------------------------------------------------------------------

/**
 * Builds a handler that answers one method+path with one taxonomy body.
 *
 * The point is that the body comes from `TAXONOMY`, so a component under test
 * receives byte-identical JSON to the taxonomy probe. A suite that inlines
 * `{ error: { code, message, correlationId, details } }` is a suite that has a
 * second copy of the envelope, and the two copies drift.
 *
 * @param method - HTTP method.
 * @param path - Path relative to `MOCK_BASE_URL`, e.g. `/api/v1/projects/p1`.
 * @param id - Which taxonomy outcome to serve.
 * @param overrides - Body fields to merge over the taxonomy body (deep-merged one level for `data`).
 */
export function taxonomyHandler(
  method: 'get' | 'post' | 'put' | 'patch' | 'delete',
  path: string,
  id: TaxonomyId,
  overrides: Record<string, unknown> = {},
): HttpHandler {
  assertHandlerExists(id);
  const entry = TAXONOMY.find((candidate) => candidate.id === id);
  if (entry === undefined) {
    // Unreachable: assertHandlerExists already threw. Present so the non-null
    // narrowing below is a fact rather than a cast.
    throw new Error(missingHandlerError(id));
  }
  const body = { ...entry.body, ...overrides } as JsonBodyType;
  return http[method](`${MOCK_BASE_URL}${path}`, () => HttpResponse.json(body, { status: entry.status }));
}

/**
 * Builds a handler that answers with a caller-supplied success body.
 *
 * Success is the one outcome a suite must supply itself: `TAXONOMY`'s success
 * entry is `{ data: { ok: true } }`, which no feature response resembles. What
 * it still takes from the taxonomy is the envelope - `correlationId` included -
 * so a success response is shaped like every other one.
 *
 * @param method - HTTP method.
 * @param path - Path relative to `MOCK_BASE_URL`.
 * @param data - The `data` payload to wrap.
 * @param status - Success status (200 or 202 for async starts).
 * @param extra - Additional top-level fields (e.g. `partial`, `warnings`).
 */
export function successHandler(
  method: 'get' | 'post' | 'put' | 'patch' | 'delete',
  path: string,
  data: unknown,
  status = 200,
  extra: Record<string, unknown> = {},
): HttpHandler {
  const correlationId = `corr-${method}-${path.replace(/[^a-z0-9]+/gi, '-').replace(/^-|-$/g, '')}`;
  const body = { data, correlationId, ...extra } as JsonBodyType;
  return http[method](`${MOCK_BASE_URL}${path}`, () => HttpResponse.json(body, { status }));
}

/**
 * Builds a handler that answers with the 204 a delete/ack endpoint returns.
 *
 * Its own function because `HttpResponse.json(undefined)` produces the literal
 * body `undefined` with a JSON content type, which is not what a 204 looks like
 * on the wire - and a suite that asserted on the response body of a 204 would be
 * asserting on a shape the API never sends.
 */
export function noContentHandler(
  method: 'get' | 'post' | 'put' | 'patch' | 'delete',
  path: string,
): HttpHandler {
  return http[method](`${MOCK_BASE_URL}${path}`, () => new HttpResponse(null, { status: 204 }));
}