import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import type { ReactNode } from 'react';
import { useVersionWatch } from '../useVersionWatch.js';
import { VersionMismatchBanner } from '../../components/VersionMismatchBanner.js';
import { clearBufferedEvents, getBufferedEvents, setTelemetrySink } from '../../telemetry/telemetry.js';
import { resetEnvCache } from '../../lib/env.js';
import { resetDeployConfigCache } from '../../config/env.js';
// Side-effect import: i18next initialises synchronously here (`initAsync: false`),
// and without it the banner renders the raw key `common:versionMismatch.…`. The
// assertion that the served tag reaches the screen is therefore also an assertion
// that the key resolves rather than echoing.
import '../../i18n/i18n.js';

// No jest-dom in this repository (see `shell.test.tsx`): assertions read
// `textContent` and `getAttribute` directly rather than using the custom
// matchers, so this file does the same rather than introducing the dependency
// for two assertions.

// The `/version.json` document as the CDN serves it. `version` and `builtAt` are
// the aliases Task 043A's hosting contract names ({version, commit, builtAt}); they
// are present here because `parseRuntimeVersion` REQUIRES them, and a fixture
// without them is a document the running client would report as unreadable -
// which is UNKNOWN, and every assertion below would then be asserting a banner
// that is correctly not shown.
const SERVED = {
  release: 'v1.4.2',
  version: '1.4.2',
  commit: '9e107d9d372bb6826bd81d3542a419d6',
  openapiVersion: 'v1',
  builtAtUtc: '2026-09-30T10:00:00Z',
  builtAt: '2026-09-30T10:00:00Z',
};

type FetchStub = typeof fetch & { mock: { calls: [string | URL | Request, RequestInit | undefined][] } };

function stubFetch(impl: (url: string) => Response | Promise<Response>): FetchStub {
  // The `init` argument is forwarded by the real `fetch` even though the stub
  // does not read it, so that the recorded call carries the options the hook
  // passes. Dropping it would make the `no-store`/`omit` assertions below
  // unfalsifiable.
  return vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    void init;
    return impl(String(input));
  }) as unknown as FetchStub;
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
}

function Wrapper({ children }: { readonly children: ReactNode }): ReactNode {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

function Probe({ fetchImpl, focusIntervalMs }: { readonly fetchImpl: FetchStub; readonly focusIntervalMs?: number }): ReactNode {
  const watch = useVersionWatch({ fetchImpl, ...(focusIntervalMs === undefined ? {} : { focusIntervalMs }) });
  return <span data-testid="state">{`${watch.match}:${watch.servedTag ?? 'none'}`}</span>;
}

const originalReload = window.location.reload;

beforeEach(() => {
  vi.stubEnv('VITE_API_BASE_URL', 'https://api.dubbing.example.com');
  vi.stubEnv('VITE_APP_VERSION', '1.4.1');
  vi.stubEnv('VITE_VERSION_TAG', 'v1.4.1');
  vi.stubEnv('VITE_CDN_ORIGIN', 'https://cdn.dubbing.example.com');
  vi.stubEnv('VITE_SSE_ENABLED', 'true');
  vi.stubEnv('VITE_TELEMETRY_ENABLED', 'true');
  resetEnvCache();
  resetDeployConfigCache();
  clearBufferedEvents();
  setTelemetrySink(undefined);
});

afterEach(() => {
  // `cleanup()` is explicit rather than relying on RTL's auto-cleanup: this
  // repository does not enable `globals` in the vitest config, so the automatic
  // afterEach hook is not registered, and without this a `getByTestId` in the
  // second test finds the first test's banner still mounted.
  cleanup();
  vi.unstubAllEnvs();
  resetEnvCache();
  resetDeployConfigCache();
  setTelemetrySink(undefined);
  clearBufferedEvents();
  vi.restoreAllMocks();
});

describe('useVersionWatch', () => {
  it('reports MATCH when the CDN serves the release the bundle was built with', async () => {
    // The bundle was stubbed to v1.4.1 in beforeEach; the CDN answers v1.4.2.
    const fetchImpl = stubFetch(() => jsonResponse({ ...SERVED, release: 'v1.4.1' }));

    render(
      <Wrapper>
        <Probe fetchImpl={fetchImpl} />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('MATCH:v1.4.1'));
  });

  it('reports MISMATCH when the CDN has moved on', async () => {
    // The edge case the whole mechanism exists for: the user is running release
    // N's bundle while the origin only has N+1's assets.
    const fetchImpl = stubFetch(() => jsonResponse(SERVED));

    render(
      <Wrapper>
        <Probe fetchImpl={fetchImpl} />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('MISMATCH:v1.4.2'));
  });

  it('reports UNKNOWN when the document is not reachable', async () => {
    // Offline, a proxy that strips it, or a deploy that predates the file. Not a
    // mismatch, and certainly not a pass.
    const fetchImpl = stubFetch(() => {
      throw new TypeError('offline');
    });

    render(
      <Wrapper>
        <Probe fetchImpl={fetchImpl} />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('UNKNOWN:none'));
  });

  it('reports UNKNOWN on a non-2xx response', async () => {
    const fetchImpl = stubFetch(() => jsonResponse({}, 404));

    render(
      <Wrapper>
        <Probe fetchImpl={fetchImpl} />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('UNKNOWN:none'));
  });

  it('requests /version.json with no-store and no credentials', async () => {
    // `no-store` because the browser caching this is precisely the bug; `omit`
    // because the document is public and a cookie would leak a session to the
    // CDN for no reason.
    const fetchImpl = stubFetch(() => jsonResponse(SERVED));

    render(
      <Wrapper>
        <Probe fetchImpl={fetchImpl} />
      </Wrapper>,
    );

    await waitFor(() => expect(fetchImpl.mock.calls.length).toBeGreaterThan(0));
    const [url, init] = fetchImpl.mock.calls[0]!;
    expect(String(url)).toBe('/version.json');
    expect(init?.cache).toBe('no-store');
    expect(init?.credentials).toBe('omit');
  });

  it('re-checks on focus once the interval has elapsed', async () => {
    const fetchImpl = stubFetch(() => jsonResponse(SERVED));

    render(
      <Wrapper>
        <Probe fetchImpl={fetchImpl} focusIntervalMs={0} />
      </Wrapper>,
    );

    await waitFor(() => expect(screen.getByTestId('state').textContent).toBe('MISMATCH:v1.4.2'));

    await act(async () => {
      window.dispatchEvent(new Event('focus'));
    });

    await waitFor(() => expect(fetchImpl.mock.calls.length).toBe(2));
  });

  it('honours the focus interval so a tab-switch does not become a poll', async () => {
    const fetchImpl = stubFetch(() => jsonResponse(SERVED));

    function RateLimitedProbe(): ReactNode {
      useVersionWatch({ fetchImpl, focusIntervalMs: 60_000 });
      return null;
    }

    render(
      <Wrapper>
        <RateLimitedProbe />
      </Wrapper>,
    );

    await waitFor(() => expect(fetchImpl.mock.calls.length).toBe(1));

    await act(async () => {
      window.dispatchEvent(new Event('focus'));
    });

    // Inside the window: not re-checked. Every tab-switch in a fleet running this
    // would otherwise be a request.
    expect(fetchImpl.mock.calls.length).toBe(1);
  });

  it('does nothing at all when disabled', async () => {
    const fetchImpl = stubFetch(() => jsonResponse(SERVED));

    function DisabledProbe(): ReactNode {
      useVersionWatch({ fetchImpl, enabled: false });
      return null;
    }

    render(
      <Wrapper>
        <DisabledProbe />
      </Wrapper>,
    );

    expect(fetchImpl.mock.calls.length).toBe(0);
  });

  it('records exactly one telemetry event per page load', async () => {
    const fetchImpl = stubFetch(() => jsonResponse(SERVED));

    render(
      <Wrapper>
        <Probe fetchImpl={fetchImpl} />
      </Wrapper>,
    );

    await waitFor(() => {
      expect(getBufferedEvents().filter((event) => event.type === 'version_mismatch')).toHaveLength(1);
    });

    await act(async () => {
      window.dispatchEvent(new Event('focus'));
    });
    await act(async () => {
      window.dispatchEvent(new Event('focus'));
    });

    // A skew that re-reports on every focus is one incident with a hundred
    // samples, which makes the ratio unreadable.
    expect(getBufferedEvents().filter((event) => event.type === 'version_mismatch')).toHaveLength(1);
  });

  it('clears the query cache once on a detected skew', async () => {
    // A reloaded client re-hydrating from release N's cached responses shows the
    // user the same wrong data after a "successful" reload.
    const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
    const fetchImpl = stubFetch(() => jsonResponse(SERVED));

    render(
      <QueryClientProvider client={client}>
        <Probe fetchImpl={fetchImpl} />
      </QueryClientProvider>,
    );

    client.setQueryData(['seeded'], { value: 1 });
    expect(client.getQueryData(['seeded'])).toEqual({ value: 1 });

    await waitFor(() => expect(client.getQueryData(['seeded'])).toBeUndefined());
  });
});

describe('VersionMismatchBanner', () => {
  const state = (match: 'MATCH' | 'MISMATCH' | 'UNKNOWN', servedTag: string | null, reload = () => {}) => ({
    match,
    bakedTag: 'v1.4.1',
    servedTag,
    reload,
  });

  it('renders nothing on MATCH', () => {
    render(
      <Wrapper>
        <VersionMismatchBanner state={state('MATCH', 'v1.4.1')} />
      </Wrapper>,
    );

    expect(screen.queryByTestId('version-mismatch-banner')).toBeNull();
  });

  it('renders nothing on UNKNOWN', () => {
    // The load-bearing decision. A banner that appears on an inconclusive check
    // trains people to dismiss it, and the one time it matters is when it is
    // dismissed.
    render(
      <Wrapper>
        <VersionMismatchBanner state={state('UNKNOWN', null)} />
      </Wrapper>,
    );

    expect(screen.queryByTestId('version-mismatch-banner')).toBeNull();
  });

  it('offers a reload on MISMATCH and names the served release', () => {
    render(
      <Wrapper>
        <VersionMismatchBanner state={state('MISMATCH', 'v1.4.2')} />
      </Wrapper>,
    );

    expect(screen.getByTestId('version-mismatch-banner').textContent).toContain('v1.4.2');
    expect(screen.getByTestId('version-mismatch-reload')).toBeDefined();
  });

  it('calls reload when the button is pressed', async () => {
    const reload = vi.fn();
    render(
      <Wrapper>
        <VersionMismatchBanner state={state('MISMATCH', 'v1.4.2', reload)} />
      </Wrapper>,
    );

    await act(async () => {
      screen.getByTestId('version-mismatch-reload').click();
    });

    expect(reload).toHaveBeenCalledTimes(1);
  });

  it('uses role=status with a polite live region, not role=alert', () => {
    // An alert interrupts what the user is doing, which is the thing they would
    // have to redo. The skew is real but not urgent.
    render(
      <Wrapper>
        <VersionMismatchBanner state={state('MISMATCH', 'v1.4.2')} />
      </Wrapper>,
    );

    const banner = screen.getByRole('status');
    expect(banner.getAttribute('aria-live')).toBe('polite');
    expect(screen.queryByRole('alert')).toBeNull();
  });

  it('falls back to generic wording when the served tag is unknown', () => {
    render(
      <Wrapper>
        <VersionMismatchBanner state={state('MISMATCH', null)} />
      </Wrapper>,
    );

    expect(screen.getByTestId('version-mismatch-banner').textContent).toContain('a newer version');
  });

  it('does not reload by itself, only on the button', () => {
    // A forced reload mid-edit loses the user's work. The banner that fixes a
    // white screen must not be able to lose a draft.
    const reload = vi.fn();
    render(
      <Wrapper>
        <VersionMismatchBanner state={state('MISMATCH', 'v1.4.2', reload)} />
      </Wrapper>,
    );

    expect(reload).not.toHaveBeenCalled();
    expect(typeof originalReload).toBe('function');
  });
});
