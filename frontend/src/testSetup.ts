// Vitest/jsdom quirk harness (Task 018, test-only — never bundled).
//
// react-router builds client-side navigations with
// `new Request(url, { signal })` where the signal is jsdom's AbortSignal
// while `Request` is undici's. Undici rejects the foreign signal, so every
// `<Navigate>` redirect (RequireAuth/RequireAdmin) blows up with an unhandled
// rejection under jsdom. When the native constructor rejects a same-realm
// signal, replace it with a subclass that drops the signal and passes
// everything else through untouched; otherwise leave the global alone.
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
