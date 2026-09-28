// Task 039A: MSW handlers for the eleven taxonomy outcomes in `taxonomy.ts`.
// One `http.get` per entry on the fixed `MOCK_BASE_URL`; no TCP port is bound
// (msw/node intercepts at the request layer), so the port-collision flake class
// cannot occur here. No retry is configured or permitted: a missing handler
// surfaces as `MSW_HANDLER_MISSING:<id>` in `conformance.spec.ts`.

import { http, HttpResponse } from 'msw';
import { MOCK_BASE_URL, TAXONOMY } from './taxonomy.js';

/** A handler per taxonomy entry, in `TAXONOMY` order. Length must equal `TAXONOMY.length`. */
export const taxonomyHandlers = TAXONOMY.map((entry) =>
  http.get(`${MOCK_BASE_URL}${entry.path}`, () => HttpResponse.json(entry.body, { status: entry.status })),
);
