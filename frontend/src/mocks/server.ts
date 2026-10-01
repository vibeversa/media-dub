// Task 046: the shared MSW server and its lifecycle.
//
// WHY LIFECYCLE IS EXPLICIT RATHER THAN GLOBAL
// --------------------------------------------
// `msw/node` intercepts `globalThis.fetch`, and installing that for all 1762
// tests would put every suite - including the pure state-machine suites that use
// `setInnerFetchForTests` and never reach the network - behind a request
// interceptor. So a suite opts in with `installMocks()`, which is greppable: a
// reviewer can see which tests talk to a mock server and which do not, and a
// suite that forgot to install one fails with an unmocked-request error instead
// of quietly hitting a dev API that happens to be running.
//
// THE FAIL-CLOSED DEFAULT
// -----------------------
// `onUnhandledRequest: 'error'` is the default here, deliberately. MSW's default
// is `'warn'`: the request passes through to the real network and the test
// continues, which is how a unit suite ends up passing on a laptop with a dev
// API up and failing in CI with an opaque `ECONNREFUSED`. Task 046's edge case is
// the strict one; `'bypass'` is available per call for the rare suite that must
// reach something real, and it is a call-site decision rather than a default.

import { setupServer } from 'msw/node';

import { assertHandlerExists, missingHandlerError, taxonomyHandlers } from './handlers.js';

export type { JsonBodyType } from 'msw';

/**
 * Every handler the shared server serves.
 *
 * Only the taxonomy probes by default. Feature suites call `installMocks([...])`
 * with their own; a suite that wanted the taxonomy probes *and* its own routes
 * passes `taxonomyHandlers` too. The default is the smallest useful set, because
 * a server serving more than a suite asked for is a server that can answer a
 * request the suite did not mean to make.
 */
export const DEFAULT_HANDLERS = taxonomyHandlers;

/**
 * The MSW handler type.
 *
 * Named rather than written as `Parameters<SetupServerApi['use']>[0]`, which is
 * what this started as: that expression resolves to `SetupServer`, whose
 * `#private` brand makes it unassignable to `SetupServerApi`, so every function
 * here had to be typed with the interface instead of the concrete return value.
 * The brand is an implementation detail of MSW's own class, and nothing outside
 * MSW can see it.
 */
type Handler = Parameters<ReturnType<typeof setupServer>['use']>[0];

/** The shared server instance. Reused across suites; lifecycle is per file. */
export const taxonomyServer = setupServer(...DEFAULT_HANDLERS);

/** Options accepted by {@link installMocks}. */
export interface InstallMocksOptions {
  /**
   * What to do with a request no handler matched.
   *
   * Defaults to `'error'`. `'warn'` and `'bypass'` exist because occasionally a
   * suite genuinely needs an unmocked origin (a CDN asset, a `data:` URL), and
   * the fix for that is one call-site argument rather than a global loosening
   * that every other suite then inherits.
   */
  readonly onUnhandledRequest?: 'error' | 'warn' | 'bypass';
}

let installed = false;

/**
 * The handler set `installMocks` established, which is what `resetMocks()`
 * restores.
 *
 * <b>This exists because MSW's `resetHandlers()` restores the list passed to
 * `setupServer(...)`, not the list added afterwards.</b> `installMocks` uses
 * `use()` (it has to: a suite may install, then add more), so a bare
 * `resetHandlers()` in `afterEach` silently removes the suite's own handlers and
 * the next test fails with "Cannot bypass a request when using the 'error'
 * strategy" - a message that points at MSW's configuration and not at the
 * harness. Found by `handlers.spec.ts`, the only place that asserts the lifecycle
 * rather than merely using it.
 */
let baseHandlers: readonly Handler[] = DEFAULT_HANDLERS;

/**
 * Installs the mock server for the current test file.
 *
 * Idempotent, because calling it from both a `beforeAll` and a helper in a suite
 * is easy to do by accident and a double `listen()` on the same patches is a
 * leak that only shows up as requests being served twice.
 *
 * @param handlers - Handlers for this suite; defaults to the taxonomy probes.
 * @param options - Unhandled-request policy.
 * @returns The shared server, for a suite that wants to `use(...)` more later.
 */
export function installMocks(
  handlers: readonly Handler[] = DEFAULT_HANDLERS,
  options: InstallMocksOptions = {},
): typeof taxonomyServer {
  baseHandlers = handlers;
  if (!installed) {
    taxonomyServer.listen({ onUnhandledRequest: options.onUnhandledRequest ?? 'error' });
    installed = true;
  }
  taxonomyServer.use(...handlers);
  return taxonomyServer;
}

/**
 * Adds handlers to an already-installed server (e.g. per test).
 *
 * These are deliberately *not* part of the base set: `resetMocks()` removes
 * them, which is the point. A per-test override that outlived its test is the
 * failure mode.
 */
export function useMocks(...handlers: Handler[]): void {
  taxonomyServer.use(...handlers);
}

/**
 * Restores the handler set `installMocks` established.
 *
 * Call this in `afterEach`, not only in `afterAll`: without it, a per-test
 * `use()` override survives into the next test and the failure is "this test
 * passed with the previous test's response", which reads as a flake and is
 * neither reproducible nor a flake.
 */
export function resetMocks(): void {
  taxonomyServer.resetHandlers();
  // `resetHandlers` only knows about the `setupServer` list, so the suite's own
  // handlers have to be re-applied. See the note on `baseHandlers`.
  taxonomyServer.use(...baseHandlers);
}

/** Removes the interception. Call in `afterAll`. */
export function uninstallMocks(): void {
  if (!installed) {
    return;
  }
  taxonomyServer.close();
  installed = false;
}

/** Whether the server is currently intercepting. For assertions, not control flow. */
export function mocksInstalled(): boolean {
  return installed;
}

export { assertHandlerExists, missingHandlerError, taxonomyHandlers };