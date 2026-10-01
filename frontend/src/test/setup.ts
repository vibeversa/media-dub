// Task 046: the shared Vitest harness. Loaded once per test file via
// `setupFiles` in `frontend/vite.config.ts`.
//
// WHAT THIS FILE IS FOR
// --------------------
// Task 046 makes the harness an executable prerequisite rather than an emergent
// property of 039-041. This is the file every frontend suite loads.
//
// It holds exactly one thing, and the reason it holds only one thing is worth
// recording: a harness that installs behaviour nobody asked for is a harness
// whose behaviour eventually conflicts with a suite. An earlier draft of this
// file also registered an `unhandledRejection` listener that was supposed to
// convert an escaped request into a named failure. It was removed because
// `escapedRequests` was never incremented - so the rule had never matched
// anything, and "no unmocked requests" would have been a pass on a codebase with
// no requests in it. That is the same defect class as 045's Finding 1.
//
// The no-network property is enforced where it can actually be enforced: in
// `src/mocks/server.ts`, whose `installMocks()` defaults MSW's
// `onUnhandledRequest` to `'error'`. A suite that opts into the mock server gets
// a named failure for an unmocked request; a suite that does not opt in never had
// one to make.
//
// WHAT IT DELIBERATELY DOES NOT DO
// --------------------------------
// It does not start MSW. `msw/node` intercepts `globalThis.fetch`, and doing
// that for all ~1760 tests would mean every suite - including the pure
// state-machine ones that use `setInnerFetchForTests` and never touch fetch -
// was silently running through a request interceptor. Suites opt in explicitly,
// which is greppable: a reviewer can see which tests talk to a mock server.
//
// The one global installed below is the jsdom Request shim, and it has to be
// here rather than in a helper because it must be in place before any module
// captures `Request` at import time.

/**
 * react-router builds client-side navigations with
 * `new Request(url, { signal })` where the `signal` is jsdom's AbortSignal while
 * `Request` is undici's. Undici rejects the foreign signal, so every
 * `<Navigate>` redirect (RequireAuth/RequireAdmin) blows up with an unhandled
 * rejection under jsdom. When the native constructor rejects a same-realm
 * signal, replace it with a subclass that drops the signal and passes everything
 * else through untouched; otherwise leave the global alone.
 *
 * Feature-detected rather than applied unconditionally: if a future jsdom stops
 * rejecting foreign signals, an unconditional shim would be dead code still
 * shadowing the real `Request`, and the bug it was hiding would come back as a
 * subtler one.
 */
const NativeRequest = globalThis.Request;

let stripSignal = false;
try {
  new NativeRequest('http://localhost/', { signal: new AbortController().signal });
} catch (error) {
  stripSignal = error instanceof TypeError;
}

if (stripSignal) {
  class TestRequest extends NativeRequest {
    public constructor(input: RequestInfo | URL, init?: RequestInit) {
      if (init?.signal === undefined || init.signal === null) {
        super(input, init);
      } else {
        const rest: RequestInit = { ...init };
        delete rest.signal;
        super(input, { ...rest, signal: null });
      }
    }
  }
  globalThis.Request = TestRequest as typeof Request;
}

export {};