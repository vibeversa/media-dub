// Task 048 — the product-wide feature-flag hook.
//
// Four things are proven here, and the fourth is the one that a flag hook can
// most easily fake:
//
//   R2  fail-closed             — unknown key, unreadable document, non-boolean
//                                 value, unauthenticated session, failed read:
//                                 every one of them is `false`.
//   R2/R4 source precedence     — `/me` wins where both speak; bootstrap is
//                                 consulted only where `/me` says nothing; the
//                                 default closes everything else.
//   R3  a flag grants nothing   — flag ON with a non-elevated permission list is
//                                 still denied by the real `RequireAdmin` route
//                                 guard and the real admin section guard.
//   R5  both call sites exist   — an admin surface and an enrichment surface
//                                 call `useFeatureFlag`, asserted by scanning
//                                 the tree rather than by trusting the diff.
//
// The `/me` parser matrix that used to live in the Task 044 enrichment suite
// lives here now, because Task 048 moved the parser. It is asserted in full,
// against the one parser that exists, rather than kept as a second copy that
// agrees with the first until somebody changes one of them.

import { QueryClientProvider } from '@tanstack/react-query';
import { Route, Routes } from 'react-router-dom';
import { MemoryRouter } from 'react-router-dom';
import type { ReactNode } from 'react';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../api/client/index.js';
import { queryKeys } from '../../api/queryKeys/index.js';
import { queryClient } from '../../app/providers/queryClient.js';
import { RequireAdmin } from '../../app/guards/RequireAdmin.js';
import {
  DEPLOY_CONFIG_ALLOWLIST,
  resetDeployConfigCache,
} from '../../config/env.js';
import { resetEnvCache } from '../../lib/env.js';
import {
  EMPTY_ME_FEATURE_FLAG_SLICE,
  FEATURE_FLAG_DEFAULTS,
  FEATURE_FLAG_KEYS,
  ME_FEATURE_FLAG_WIRE_KEYS,
  evaluateFeatureFlag,
  evaluateFeatureFlags,
  meFeatureFlagWireKeys,
  readBootstrapFeatureFlags,
  readMeFeatureFlagSlice,
  resolveFeatureFlag,
  resolveFeatureFlagDetailed,
  resolveFeatureFlags,
} from '../../config/featureFlags.js';
import type { FeatureFlagValue, MeFeatureFlagSlice } from '../../config/featureFlags.js';
import { useAdminGuard } from '../../features/admin/adminGuard.js';
import { useAuthStore } from '../../features/auth/authStore.js';
import { resetRestoreStartedForTests } from '../../features/auth/useSession.js';
import { useAppStore } from '../../stores/index.js';
import {
  FeatureFlagProvider,
  useFeatureFlag,
  useFeatureFlagSnapshot,
  useFeatureFlags,
} from '../useFeatureFlag.js';
import { clearBufferedEvents, getBufferedEvents } from '../../telemetry/telemetry.js';

// --- harness -----------------------------------------------------------------

interface FlagWorld {
  /** What the mocked `GET /me` answers with for `featureFlags`. */
  readonly meFlags: MeFeatureFlagSlice | 'fail' | 'error500' | 'unauthorized';
  /** Permission strings the mocked `/me` returns and the stores receive. */
  readonly permissions: readonly string[];
  /** Every `/api/v1` URL the mock saw, in order. */
  readonly seenUrls: string[];
}

function newWorld(overrides: Partial<FlagWorld> = {}): FlagWorld {
  return { meFlags: {}, permissions: ['project.view'], seenUrls: [], ...overrides };
}

let world: FlagWorld = newWorld();

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-flag', details: {} } }, status);
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.href;
  }
  return (input as Request).url;
}

async function mockFetch(input: RequestInfo | URL): Promise<Response> {
  const url = urlOf(input);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  world.seenUrls.push(url);
  if (/\/me(\?|$)/.test(url)) {
    switch (world.meFlags) {
      case 'fail':
        return Promise.reject(new TypeError('Failed to fetch'));
      case 'error500':
        return errorEnvelope('INTERNAL_ERROR', 500);
      case 'unauthorized':
        return errorEnvelope('UNAUTHORIZED', 401);
      default:
        return jsonResponse({
          userId: 'usr_1',
          tenantId: 'ten_1',
          permissions: world.permissions,
          featureFlags: world.meFlags,
        });
    }
  }
  // Any other gated read. The hook must not care what it answers.
  return errorEnvelope('FORBIDDEN', 403);
}

function meReads(): number {
  return world.seenUrls.filter((url) => /\/me(\?|$)/.test(url)).length;
}

function renderInApp(node: ReactNode): void {
  render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>{node}</MemoryRouter>
    </QueryClientProvider>,
  );
}

function Probe({ flag }: { readonly flag: (typeof FEATURE_FLAG_KEYS)[number] }): ReactNode {
  return <span data-testid="flag">{String(useFeatureFlag(flag))}</span>;
}

function AllFlagsProbe(): ReactNode {
  const flags = useFeatureFlags();
  return <span data-testid="flags">{JSON.stringify(flags)}</span>;
}

function authenticate(permissions: readonly string[]): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

beforeEach(() => {
  world = newWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  clearBufferedEvents();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  authenticate(world.permissions);
  // Bootstrap defaults to all-off unless a test says otherwise. Every test
  // that cares about bootstrap sets the variables explicitly.
  vi.stubEnv('VITE_ENABLE_ANALYTICS', 'false');
  vi.stubEnv('VITE_ENABLE_DIAGNOSTICS', 'false');
  vi.stubEnv('VITE_ENABLE_EXPERIMENTAL_FEATURES', 'false');
  resetEnvCache();
  resetDeployConfigCache();
});

afterEach(() => {
  cleanup();
  vi.unstubAllEnvs();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  resetEnvCache();
  resetDeployConfigCache();
  clearBufferedEvents();
  vi.restoreAllMocks();
});

// =============================================================================
// R2 — fail closed
// =============================================================================

describe('R2 every unknown or missing flag is off', () => {
  it('reports true and false from /me for every stated flag', async () => {
    // The whole matrix in one test, because the point is that the two answers
    // are per-key: a surface is allowed to be on while its neighbour is off,
    // and a resolver that collapsed them would be a single flag wearing six
    // names.
    world = newWorld({
      meFlags: { videoIntelligenceEnabled: true, lipSyncEnabled: false, localInferenceEnabled: true },
    });
    renderInApp(<AllFlagsProbe />);
    await waitFor(() => {
      expect(JSON.parse(screen.getByTestId('flags').textContent ?? '{}')).toEqual({
        analytics: false,
        diagnostics: false,
        experimentalFeatures: false,
        videoIntelligence: true,
        lipSync: false,
        localInference: true,
      });
    });
    expect(meReads()).toBe(1);
  });

  it('reports false for every key when /me states nothing', async () => {
    world = newWorld({ meFlags: {} });
    renderInApp(<AllFlagsProbe />);
    await waitFor(() => {
      expect(meReads()).toBe(1);
    });
    expect(screen.getByTestId('flags').textContent).toBe(JSON.stringify(FEATURE_FLAG_DEFAULTS));
  });

  it('is off before the read settles, and stays off if the read fails', async () => {
    for (const outcome of ['fail', 'error500', 'unauthorized'] as const) {
      world = newWorld({ meFlags: outcome });
      renderInApp(<Probe flag="videoIntelligence" />);
      // First paint: nothing read yet, and the answer is already `false`.
      expect(screen.getByTestId('flag').textContent).toBe('false');
      await waitFor(() => {
        expect(queryClient.getQueryState(queryKeys.me.featureFlags())?.fetchStatus).toBe('idle');
      });
      // After the failure settles: still `false`, and no crash.
      expect(screen.getByTestId('flag').textContent).toBe('false');
      cleanup();
      queryClient.clear();
    }
  });

  it.each(['fail', 'error500', 'unauthorized'] as const)(
    'never requests a gated surface when /me %s',
    async (outcome) => {
      world = newWorld({ meFlags: outcome });
      renderInApp(<Probe flag="localInference" />);
      await waitFor(() => {
        expect(queryClient.getQueryState(queryKeys.me.featureFlags())).toBeDefined();
      });
      await new Promise((resolve) => setTimeout(resolve, 50));
      // Only `/me` was asked. The flag being off is what stopped the rest.
      expect(world.seenUrls).toEqual(world.seenUrls.filter((url) => /\/me(\?|$)/.test(url)));
    },
  );

  it('fails closed on every unreadable /me document', () => {
    // Every one of these must read as "unstated". The last three are the ones
    // that would be easy to read as "probably fine".
    for (const raw of [
      undefined,
      null,
      {},
      { featureFlags: null },
      { featureFlags: [] },
      { featureFlags: 'true' },
      { featureFlags: { videoIntelligenceEnabled: 'true', lipSyncEnabled: 1, localInferenceEnabled: {} } },
      // Client-side alias spellings are NOT accepted. A parser that honours a
      // key the backend never emits is not fail-closed, it is guessing.
      { featureFlags: { videoIntel: true, lipSync: true, localGpu: true } },
    ]) {
      expect(readMeFeatureFlagSlice(raw)).toEqual(EMPTY_ME_FEATURE_FLAG_SLICE);
      expect(evaluateFeatureFlags(readMeFeatureFlagSlice(raw))).toEqual(FEATURE_FLAG_DEFAULTS);
    }
  });

  it('is off for a key this build has never heard of', () => {
    // The type prevents it in product code, so this is the runtime floor: a
    // string the resolver does not know resolves to the fail-closed default
    // rather than to `undefined` (which reads as "on" at a `!== true` call site
    // that has been written wrong) or to a thrown error.
    const unknown = 'aFlagThisBuildDoesNotHave' as (typeof FEATURE_FLAG_KEYS)[number];
    expect(resolveFeatureFlag({ videoIntelligenceEnabled: true }, unknown, { ...FEATURE_FLAG_DEFAULTS, [unknown]: true })).toBe(
      false,
    );
    expect(FEATURE_FLAG_KEYS).not.toContain(unknown);
  });

  it('is off for a session that is not authenticated', async () => {
    // The seeded/queried slice is ignored without a session, so a shell whose
    // session has expired cannot keep an experimental surface on screen while
    // it routes to /login.
    world = newWorld({ meFlags: { videoIntelligenceEnabled: true } });
    queryClient.setQueryData(queryKeys.me.featureFlags(), { videoIntelligenceEnabled: true });
    useAuthStore.setState({ status: 'expired' });
    renderInApp(<Probe flag="videoIntelligence" />);
    await waitFor(() => {
      expect(screen.getByTestId('flag').textContent).toBe('false');
    });
    // And it never even asks.
    expect(meReads()).toBe(0);
  });

  it('keeps the fail-closed default when the deploy config is invalid', async () => {
    // `getDeployConfig()` throws by design on a mis-set `VITE_*`. A flag
    // evaluation must degrade to off, not take the shell down with it: the
    // config error is reported by `lib/env.ts`'s startup guard and by
    // `scripts/vite-env-audit.sh`, where a human is looking.
    vi.stubEnv('VITE_API_BASE_URL', 'javascript:alert(1)');
    resetEnvCache();
    resetDeployConfigCache();
    expect(readBootstrapFeatureFlags()).toEqual(FEATURE_FLAG_DEFAULTS);

    world = newWorld();
    renderInApp(<Probe flag="experimentalFeatures" />);
    await waitFor(() => {
      expect(meReads()).toBe(1);
    });
    expect(screen.getByTestId('flag').textContent).toBe('false');
  });
});

// =============================================================================
// R2/R4 — the source rule
// =============================================================================

describe('R4 sources: /me first, bootstrap second, default last', () => {
  it('reads the three /me wire flags and nothing else', () => {
    expect(readMeFeatureFlagSlice({ featureFlags: { videoIntelligenceEnabled: true } })).toEqual({
      videoIntelligenceEnabled: true,
    });
    expect(readMeFeatureFlagSlice({ featureFlags: { lipSyncEnabled: true } })).toEqual({ lipSyncEnabled: true });
    expect(readMeFeatureFlagSlice({ featureFlags: { localInferenceEnabled: true } })).toEqual({
      localInferenceEnabled: true,
    });
    // An already-unwrapped slice is accepted: `apiFetch` hands back the whole
    // body and a caller holding the slice should not have to re-wrap it.
    expect(readMeFeatureFlagSlice({ lipSyncEnabled: true })).toEqual({ lipSyncEnabled: true });
    // An unknown wire key is dropped rather than carried.
    expect(readMeFeatureFlagSlice({ featureFlags: { somethingNewEnabled: true } })).toEqual(
      EMPTY_ME_FEATURE_FLAG_SLICE,
    );
    // A recognised-but-false key is kept, because "off" is an answer.
    expect(readMeFeatureFlagSlice({ featureFlags: { videoIntelligenceEnabled: false } })).toEqual({
      videoIntelligenceEnabled: false,
    });
  });

  it('declares exactly the /me wire vocabulary, and nothing else', () => {
    // The server record `MeFeatureFlags` has three fields. A fourth name here
    // is a client-side invention; a missing one is a flag the backend can turn
    // on that this build would silently ignore.
    expect(meFeatureFlagWireKeys()).toEqual(['lipSyncEnabled', 'localInferenceEnabled', 'videoIntelligenceEnabled']);
    for (const wireKeys of Object.values(ME_FEATURE_FLAG_WIRE_KEYS)) {
      for (const wireKey of wireKeys) {
        expect(meFeatureFlagWireKeys()).toContain(wireKey);
      }
    }
    // `/me` states nothing about the three bootstrap flags today, which is why
    // they are bootstrap-only.
    expect(ME_FEATURE_FLAG_WIRE_KEYS.analytics).toEqual([]);
    expect(ME_FEATURE_FLAG_WIRE_KEYS.diagnostics).toEqual([]);
    expect(ME_FEATURE_FLAG_WIRE_KEYS.experimentalFeatures).toEqual([]);
  });

  it('prefers /me over bootstrap where both speak', async () => {
    // Bootstrap says experimental surfaces are on. `/me` says nothing about
    // them (it has no such field), so bootstrap decides.
    vi.stubEnv('VITE_ENABLE_EXPERIMENTAL_FEATURES', 'true');
    resetEnvCache();
    resetDeployConfigCache();
    expect(readBootstrapFeatureFlags().experimentalFeatures).toBe(true);

    world = newWorld({ meFlags: { videoIntelligenceEnabled: true } });
    renderInApp(
      <>
        <Probe flag="experimentalFeatures" />
        <Probe flag="videoIntelligence" />
      </>,
    );
    await waitFor(() => {
      expect(meReads()).toBe(1);
      expect(screen.getAllByTestId('flag')[1]?.textContent).toBe('true');
    });
    // The bootstrap-only flag follows bootstrap; the /me flag follows /me.
    expect(screen.getAllByTestId('flag')[0]?.textContent).toBe('true');
    expect(screen.getAllByTestId('flag')[1]?.textContent).toBe('true');
  });

  it('lets a recognised /me false override a bootstrap true', async () => {
    // The operator turned the capability off in the admin panel. The bundle
    // happening to be built with the bootstrap switch on must not turn it back
    // on: a recognised-but-off key is an answer, not an absence.
    vi.stubEnv('VITE_ENABLE_EXPERIMENTAL_FEATURES', 'true');
    vi.stubEnv('VITE_ENABLE_ANALYTICS', 'true');
    resetEnvCache();
    resetDeployConfigCache();

    expect(resolveFeatureFlagDetailed({ videoIntelligenceEnabled: false }, 'videoIntelligence', readBootstrapFeatureFlags())).toEqual({
      enabled: false,
      source: 'ME',
    });
    world = newWorld({ meFlags: { videoIntelligenceEnabled: false } });
    renderInApp(<Probe flag="videoIntelligence" />);
    await waitFor(() => {
      expect(meReads()).toBe(1);
    });
    expect(screen.getByTestId('flag').textContent).toBe('false');
  });

  it('names the source that decided each flag', () => {
    const bootstrap: FeatureFlagValue = { ...FEATURE_FLAG_DEFAULTS, experimentalFeatures: true, diagnostics: true };
    expect(resolveFeatureFlagDetailed({ lipSyncEnabled: true }, 'lipSync', bootstrap)).toEqual({
      enabled: true,
      source: 'ME',
    });
    expect(resolveFeatureFlagDetailed({}, 'experimentalFeatures', bootstrap)).toEqual({
      enabled: true,
      source: 'BOOTSTRAP',
    });
    expect(resolveFeatureFlagDetailed({}, 'analytics', bootstrap)).toEqual({
      enabled: false,
      source: 'DEFAULT',
    });
  });

  it('resolves every key, and the map is always total', () => {
    const resolved = resolveFeatureFlags({ lipSyncEnabled: true }, { ...FEATURE_FLAG_DEFAULTS, diagnostics: true });
    expect(Object.keys(resolved).sort()).toEqual([...FEATURE_FLAG_KEYS].sort());
    expect(resolved.lipSync).toBe(true);
    expect(resolved.diagnostics).toBe(true);
    expect(resolved.videoIntelligence).toBe(false);
    // A missing slice is the same as an empty one.
    expect(resolveFeatureFlags(undefined, FEATURE_FLAG_DEFAULTS)).toEqual(FEATURE_FLAG_DEFAULTS);
  });

  it('uses exactly the allowlisted VITE_ENABLE_* family for bootstrap', () => {
    // Bootstrap is the `VITE_ENABLE_*` family and nothing else. A flag key with
    // a bootstrap reader must name a variable the deploy allowlist carries,
    // because a value that is not allowlisted is not inlined into the bundle
    // and would read as permanently off.
    const allowlisted = DEPLOY_CONFIG_ALLOWLIST.filter((key) => key.startsWith('VITE_ENABLE_')).sort();
    expect(allowlisted).toEqual([
      'VITE_ENABLE_ANALYTICS',
      'VITE_ENABLE_DIAGNOSTICS',
      'VITE_ENABLE_EXPERIMENTAL_FEATURES',
    ]);
    // Three bootstrap readers, three allowlisted variables: a fourth reader
    // without a variable would be a flag that can never be on in a real build.
    expect(Object.values(readBootstrapFeatureFlags()).filter((value) => value === true)).toEqual([]);
  });

  it('evaluates a single flag through the impure wrapper', () => {
    vi.stubEnv('VITE_ENABLE_DIAGNOSTICS', 'true');
    resetEnvCache();
    resetDeployConfigCache();
    expect(evaluateFeatureFlag({ lipSyncEnabled: true }, 'lipSync')).toBe(true);
    expect(evaluateFeatureFlag(undefined, 'diagnostics')).toBe(true);
    expect(evaluateFeatureFlag(undefined, 'videoIntelligence')).toBe(false);
    expect(resolveFeatureFlag(undefined, 'diagnostics', undefined)).toBe(false);
  });
});

// =============================================================================
// One read per session
// =============================================================================

describe('the flag read is one request per session', () => {
  it('answers N mounted consumers from a single /me read', async () => {
    world = newWorld({ meFlags: { videoIntelligenceEnabled: true } });
    renderInApp(
      <>
        <Probe flag="videoIntelligence" />
        <Probe flag="videoIntelligence" />
        <Probe flag="lipSync" />
        <AllFlagsProbe />
      </>,
    );
    await waitFor(() => {
      expect(screen.getAllByTestId('flag')[0]?.textContent).toBe('true');
    });
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(meReads()).toBe(1);
  });

  it('issues no read at all when authStore has already seeded the slice', async () => {
    // This is the running application's path: `authStore`'s session resolution
    // reads `/me` for permissions and locale and seeds the same cache entry
    // from that document, so a surface mounting afterwards costs nothing.
    // Seeding it here by hand is the same operation it performs.
    world = newWorld({ meFlags: { lipSyncEnabled: true } });
    queryClient.setQueryData(queryKeys.me.featureFlags(), readMeFeatureFlagSlice({ featureFlags: world.meFlags }));
    renderInApp(<Probe flag="lipSync" />);
    await waitFor(() => {
      expect(screen.getByTestId('flag').textContent).toBe('true');
    });
    expect(meReads()).toBe(0);
  });

  it('keeps a resolved flag when a later refetch fails', async () => {
    world = newWorld({ meFlags: { videoIntelligenceEnabled: true } });
    renderInApp(<Probe flag="videoIntelligence" />);
    await waitFor(() => {
      expect(screen.getByTestId('flag').textContent).toBe('true');
    });

    // The next /me answers 500. A transient blip must not make a working
    // surface disappear mid-session.
    world = { ...world, meFlags: 'error500' };
    await queryClient.refetchQueries({ queryKey: queryKeys.me.featureFlags() });
    expect(screen.getByTestId('flag').textContent).toBe('true');
  });

  it('does not change the flag when a gated action is denied (403/423)', async () => {
    // The task's edge case: flag ON, backend refuses the use, and the flag
    // state is unchanged. The hook has no optimistic local state at all - the
    // only thing a refusal can move is the surface's own error UI.
    world = newWorld({ meFlags: { localInferenceEnabled: true } });
    renderInApp(<Probe flag="localInference" />);
    await waitFor(() => {
      expect(screen.getByTestId('flag').textContent).toBe('true');
    });

    const before = queryClient.getQueryData(queryKeys.me.featureFlags());
    // Every non-`/me` request in this suite answers 403; a freeze is a 423.
    expect(world.seenUrls.filter((url) => url.includes('/admin/'))).toEqual([]);
    await queryClient.refetchQueries({ queryKey: queryKeys.me.featureFlags() });
    expect(queryClient.getQueryData(queryKeys.me.featureFlags())).toEqual(before);
    expect(screen.getByTestId('flag').textContent).toBe('true');
  });

  it('emits no telemetry for a flag decision', async () => {
    world = newWorld({ meFlags: { videoIntelligenceEnabled: true } });
    renderInApp(<Probe flag="videoIntelligence" />);
    await waitFor(() => {
      expect(screen.getByTestId('flag').textContent).toBe('true');
    });
    // A rollout hint is not a failure, a route change or a version skew. The
    // task's security section allows a flag's key and boolean and nothing
    // else; emitting nothing is the only way to keep it that way.
    expect(getBufferedEvents()).toEqual([]);
  });
});

// =============================================================================
// R3 — a flag grants nothing
// =============================================================================

describe('R3 a flag never grants a permission', () => {
  /**
   * The shape `AdminPage` uses: guard first, flag second. A surface that
   * ordered these the other way round would let a flag decide access, which is
   * exactly the mistake this test is here to prevent.
   */
  function FlaggedAdminSection(): ReactNode {
    const guard = useAdminGuard();
    const experimental = useFeatureFlag('experimentalFeatures');
    if (guard.isPending) {
      return <span data-testid="admin-section-pending">pending</span>;
    }
    if (!guard.allowed) {
      return <span data-testid="admin-section-forbidden">forbidden</span>;
    }
    return <span data-testid="admin-section-experimental">{String(experimental)}</span>;
  }

  it('denies a flag-ON session with a non-elevated permission list', async () => {
    // Every bootstrap flag on, so the flag is unambiguously ON.
    vi.stubEnv('VITE_ENABLE_EXPERIMENTAL_FEATURES', 'true');
    resetEnvCache();
    resetDeployConfigCache();
    world = newWorld({ permissions: ['project.view'] });
    authenticate(['project.view']);
    expect(resolveFeatureFlag(undefined, 'experimentalFeatures', readBootstrapFeatureFlags())).toBe(true);

    renderInApp(<FlaggedAdminSection />);
    // The guard decides, and it says no. The flag is not even consulted.
    expect(screen.getByTestId('admin-section-forbidden')).toBeDefined();
    expect(screen.queryByTestId('admin-section-experimental')).toBeNull();
  });

  it('denies at the route guard too, with the flag ON', async () => {
    vi.stubEnv('VITE_ENABLE_EXPERIMENTAL_FEATURES', 'true');
    resetEnvCache();
    resetDeployConfigCache();
    world = newWorld({ permissions: ['project.view'] });
    authenticate(['project.view']);

    render(
      <QueryClientProvider client={queryClient}>
        <MemoryRouter initialEntries={['/admin']}>
          <Routes>
            <Route path="/admin" element={<RequireAdmin />}>
              <Route index element={<span data-testid="admin-route-secret">secret</span>} />
            </Route>
            <Route path="/403" element={<span data-testid="admin-route-403">forbidden</span>} />
          </Routes>
          <Probe flag="experimentalFeatures" />
        </MemoryRouter>
      </QueryClientProvider>,
    );
    // The flag is genuinely on in the same render tree…
    expect(screen.getByTestId('flag').textContent).toBe('true');
    // …and the guarded route still redirects to /403 instead of rendering.
    await waitFor(() => {
      expect(screen.getByTestId('admin-route-403')).toBeDefined();
    });
    expect(screen.queryByTestId('admin-route-secret')).toBeNull();
  });

  it('still reads the flag for a user the guard denies', async () => {
    // A flag is presentation, so a non-elevated user may legitimately see a
    // rollout hint. What they must not get is the guarded content - and the
    // guard is not the flag's decision to make in either direction.
    vi.stubEnv('VITE_ENABLE_EXPERIMENTAL_FEATURES', 'true');
    resetEnvCache();
    resetDeployConfigCache();
    world = newWorld({ permissions: ['project.view'], meFlags: {} });
    authenticate(['project.view']);
    renderInApp(
      <>
        <FlaggedAdminSection />
        <Probe flag="experimentalFeatures" />
      </>,
    );
    expect(screen.getByTestId('flag').textContent).toBe('true');
    expect(screen.queryByTestId('admin-section-experimental')).toBeNull();
  });
});

// =============================================================================
// The provider seam
// =============================================================================

describe('the test/e2e provider seam', () => {
  it('replaces the resolved map and never consults /me', async () => {
    world = newWorld({ meFlags: {} });
    renderInApp(
      <FeatureFlagProvider value={{ ...FEATURE_FLAG_DEFAULTS, videoIntelligence: true }}>
        <Probe flag="videoIntelligence" />
        <Probe flag="lipSync" />
      </FeatureFlagProvider>,
    );
    expect(screen.getAllByTestId('flag')[0]?.textContent).toBe('true');
    expect(screen.getAllByTestId('flag')[1]?.textContent).toBe('false');
    // The provider short-circuits the read: no request, and no `/me` answer
    // can contradict it.
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(meReads()).toBe(0);
  });

  it('reports the provider as the snapshot source', () => {
    function SnapshotProbe(): ReactNode {
      const snapshot = useFeatureFlagSnapshot();
      return (
        <span data-testid="snapshot">
          {`${snapshot.source}:${String(snapshot.flags.videoIntelligence)}:${String(snapshot.resolved)}`}
        </span>
      );
    }
    world = newWorld();
    renderInApp(
      <FeatureFlagProvider value={{ ...FEATURE_FLAG_DEFAULTS, videoIntelligence: true }}>
        <SnapshotProbe />
      </FeatureFlagProvider>,
    );
    expect(screen.getByTestId('snapshot').textContent).toBe('PROVIDER:true:true');
  });

  it('reports ME once the read settles and UNRESOLVED before it does', async () => {
    world = newWorld({ meFlags: { lipSyncEnabled: true } });
    function SnapshotProbe(): ReactNode {
      const snapshot = useFeatureFlagSnapshot();
      return (
        <span data-testid="snapshot">
          {`${snapshot.source}:${String(snapshot.flags.lipSync)}:${String(snapshot.resolved)}`}
        </span>
      );
    }
    renderInApp(<SnapshotProbe />);
    expect(screen.getByTestId('snapshot').textContent).toBe('UNRESOLVED:false:false');
    await waitFor(() => {
      expect(screen.getByTestId('snapshot').textContent).toBe('ME:true:true');
    });
  });
});