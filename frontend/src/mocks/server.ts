// Task 039A: shared MSW server for taxonomy conformance. Interception-only
// (no TCP port, no retry); tests opt into strict mode via
// `listen({ onUnhandledRequest: 'error' })` so unhandled requests fail loudly.

import { setupServer } from 'msw/node';
import { taxonomyHandlers } from './handlers.js';

/** Serves `taxonomyHandlers`. Lifecycle is owned per spec file (`conformance.spec.ts`). */
export const taxonomyServer = setupServer(...taxonomyHandlers);
