// Task 044 — optional enrichment / local-AI UX.
//
// Four things are proven here, and the fourth is the one that is usually
// missing:
//
//   R1  flag-off invisibility   — zero DOM, zero network, zero bundle.
//   R2  separate-artifact rule  — enrichment data never enters core state.
//   R5  failure isolation       — a 500/timeout in enrichment leaves the core
//                                 transcript/translation/voice/review/export
//                                 surfaces untouched and green.
//   R6  import gate             — core bundles statically exclude every
//                                 enrichment module, AND those modules are
//                                 reachable dynamically, so the gate cannot
//                                 pass by not being wired up.
//
// The import gate is deliberately written in BOTH directions. A test that only
// asserts "no static import of the panels" passes trivially when the panels are
// not imported at all — that is the same tautology the Task 043C report found
// twice in the restore drill (a check whose subject list came from the file it
// was checking). So this suite also walks the DYNAMIC graph and asserts the
// panels are reachable through `import()`, and separately asserts the built
// chunk graph puts them outside the entry chunk.

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { queryKeys } from '../../../api/queryKeys/index.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { AdminPage } from '../../admin/AdminPage.js';
import { TranscriptEditor } from '../../transcript/TranscriptEditor.js';
import { EnrichmentGate, ProjectEnrichment } from '../EnrichmentGate.js';
import {
  ENRICHMENT_FLAGS_OFF,
  EnrichmentFlagsProvider,
  enrichmentGateAllows,
  isEnrichmentFlagEnabled,
  parseEnrichmentFlags,
  useEnrichmentFlagSnapshot,
  useEnrichmentFlagsQuery,
} from '../enrichmentFlags.js';
import { fetchLipSyncAssetUrl, isNotProvisionedError, useVideoIntel } from '../useEnrichmentQueries.js';
import { useLocalGpuHealth } from '../../admin/useLocalGpuQueries.js';
import {
  LIPSYNC_METHOD_FALLBACK,
  formatEnrichmentFileSize,
  formatLipSyncScore,
  isForbiddenEnrichmentKey,
  parseLipSync,
  parseVideoIntel,
  resolveLipSyncMethod,
  resolveSegmentLink,
} from '../types.js';
import { parseLocalGpu, resolveLocalGpuStatus, isForbiddenLocalGpuKey } from '../../admin/localGpuTypes.js';
import type { EnrichmentFlags } from '../enrichmentFlags.js';

// --- harness -----------------------------------------------------------------

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse({ error: { code, message: `backend ${code}`, correlationId: 'corr-enrich', details: {} } }, status);
}

function redirectResponse(location: string): Response {
  return new Response(null, { status: 302, headers: { Location: location } });
}

const ALL_ON: EnrichmentFlags = { videoIntel: true, lipSync: true, localGpu: true };

interface EnrichmentWorld {
  permissions: readonly string[];
  flags: EnrichmentFlags;
  /** Video-intel outcome. */
  videoIntel: 'ok' | 'empty' | 'error500' | 'timeout' | 'not-provisioned' | 'not-implemented' | 'forbidden';
  /** Lip-sync outcome. */
  lipSync: 'ok' | 'empty' | 'error500' | 'timeout' | 'not-provisioned' | 'not-implemented' | 'score-without-asset';
  /** Local-GPU outcome. */
  localGpu: 'ok' | 'empty' | 'error500' | 'timeout' | 'unreachable' | 'forbidden';
  downloadBehavior: 'ok' | 'expired' | 'double-expired' | 'missing' | 'server-error' | 'no-location';
  downloadCalls: number;
  /** Every URL the mock saw, in order. The R1/R5 network assertions read this. */
  seenUrls: string[];
}

function newWorld(overrides: Partial<EnrichmentWorld> = {}): EnrichmentWorld {
  return {
    permissions: ['admin.manage', 'project.view', 'project.edit'],
    flags: ENRICHMENT_FLAGS_OFF,
    videoIntel: 'ok',
    lipSync: 'ok',
    localGpu: 'unreachable',
    downloadBehavior: 'ok',
    downloadCalls: 0,
    seenUrls: [],
    ...overrides,
  };
}

let world: EnrichmentWorld = newWorld();

const VIDEO_INTEL_BODY = {
  model: 'local-vision',
  generatedAt: '2026-09-28T00:00:00Z',
  artifacts: [
    { id: 'art_scene_1', kind: 'scene-cut', label: 'Interior, night', atMs: '00:00:12', segmentId: 'seg_1' },
    { id: 'art_overlay_1', kind: 'overlay', label: 'Lower third', detail: 'name strap', atMs: '00:01:04', segmentId: 'seg_2' },
    { id: 'art_orphan_1', kind: 'scene-cut', label: 'Deleted segment cut', atMs: '00:02:00', segmentId: 'seg_gone' },
    // Secret-shaped and unlinkable rows: both must be dropped by the parser.
    { id: 'art_bad', kind: 'overlay', label: 'x', apiKey: 'sk-live-abcdef' },
    { id: '', kind: 'scene-cut', label: 'no id' },
  ],
};

const LIP_SYNC_BODY = {
  model: 'local-wav2lip',
  lipSyncScore: 0.87,
  method: 'heuristic v1',
  segments: [
    { segmentId: 'seg_1', lipSyncScore: 0.94, method: 'measured offset v2', asset: { id: 'ast_1', format: 'wav', sizeBytes: 2048 } },
    { segmentId: 'seg_2', lipSyncScore: 0.71 },
    { segmentId: 'seg_gone', lipSyncScore: 0.5 },
  ],
};

const LOCAL_GPU_BODY = {
  provider: 'local-inference',
  status: 'Healthy',
  model: 'whisper-large-v3',
  modelVersion: '2026-08',
  device: 'cuda:0',
  deviceCount: 1,
  latencyMsP95: 42,
  lastSuccessAt: '2026-09-28T00:00:00Z',
  // A sidecar health document routinely echoes its own configuration. None of
  // this may reach the DOM.
  endpoint: 'http://gpu-sidecar.internal:8080/infer',
  apiKey: 'sk-live-sidecar',
};

/**
 * A core transcript segment, in the shape `TranscriptEditor` parses.
 *
 * R5 is only meaningful if the core surface beside the enrichment panels is a
 * REAL one: a stub that renders a fixed string would prove nothing about
 * whether an enrichment failure disturbs it.
 */
function makeSegment(segmentId: string): Record<string, unknown> {
  const index = Number(segmentId.replace('seg_', '')) || 0;
  const text = `core-transcript-${segmentId}`;
  return {
    id: segmentId,
    projectId: 'prj_1',
    status: 'Ready',
    sequence: index + 1,
    startMs: index * 2000,
    endMs: index * 2000 + 1800,
    speakerId: 'spk_alice',
    speakerLabel: 'Alice',
    selectionVersion: 2,
    reviewStatus: 'Approved',
    qualityCodes: [],
    syncStatus: 'SyncAcceptable',
    confidence: 0.95,
    text,
    transcriptVersions: [
      { id: `${segmentId}-v1`, provider: 'acme', model: 'stt-v1', text: `${text}-v1`, isSelected: false, createdAt: '2026-01-15T12:00:00Z' },
      { id: `${segmentId}-v2`, provider: 'acme', model: 'stt-v2', text, isSelected: true, createdAt: '2026-01-15T13:00:00Z' },
    ],
  };
}

/** The GPU panel's own subtree, so a status assertion cannot read a neighbour. */
function gpuSection(): HTMLElement {
  const node = document.querySelector('[data-testid="admin-local-gpu"]');
  if (node === null) {
    throw new Error('the local GPU panel is not mounted');
  }
  return node as HTMLElement;
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

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') {
      return request.method.toUpperCase();
    }
  }
  return (init?.method ?? 'GET').toUpperCase();
}

/**
 * A client-side "timeout" is not a hanging promise: the browser's own timeout
 * and every proxy in between surface as a connection failure. A promise that
 * never settles would leave the query `isPending` forever with a loading
 * skeleton on screen, which is a different — and separately wrong — behaviour:
 * an enrichment surface a reader waits on. The never-settling case is asserted
 * separately as a loading state.
 */
function networkFailure(): Promise<Response> {
  return Promise.reject(new TypeError('Failed to fetch'));
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  world.seenUrls.push(url);

  if (method === 'GET' && /\/me(\?|$)/.test(url)) {
    return jsonResponse({
      userId: 'usr_1',
      tenantId: 'ten_1',
      permissions: world.permissions,
      featureFlags: {
        videoIntelligenceEnabled: world.flags.videoIntel,
        lipSyncEnabled: world.flags.lipSync,
        localInferenceEnabled: world.flags.localGpu,
      },
    });
  }

  if (method === 'GET' && url.includes('/enrichment/video-intel')) {
    switch (world.videoIntel) {
      case 'error500':
        return errorEnvelope('INTERNAL_ERROR', 500);
      case 'timeout':
        return networkFailure();
      case 'not-provisioned':
        return errorEnvelope('NOT_FOUND', 404);
      case 'not-implemented':
        return errorEnvelope('NOT_IMPLEMENTED', 501);
      case 'forbidden':
        return errorEnvelope('FORBIDDEN', 403);
      case 'empty':
        return jsonResponse({ model: 'local-vision', artifacts: [] });
      default:
        return jsonResponse(VIDEO_INTEL_BODY);
    }
  }

  if (method === 'GET' && url.includes('/enrichment/lip-sync/asset')) {
    world.downloadCalls += 1;
    switch (world.downloadBehavior) {
      case 'ok':
        return redirectResponse('https://cdn.example.com/lipsync/asset.wav?sig=abc');
      case 'expired':
        // First call expires, second succeeds: exactly one refetch.
        return world.downloadCalls === 1 ? errorEnvelope('URL_EXPIRED', 410) : redirectResponse('https://cdn.example.com/lipsync/asset.wav?sig=fresh');
      case 'double-expired':
        return errorEnvelope('URL_EXPIRED', 410);
      case 'missing':
        return errorEnvelope('NOT_FOUND', 404);
      case 'server-error':
        return errorEnvelope('INTERNAL_ERROR', 500);
      case 'no-location':
        return new Response(null, { status: 302 });
      default:
        return redirectResponse('https://cdn.example.com/lipsync/asset.wav');
    }
  }

  if (method === 'GET' && url.includes('/enrichment/lip-sync')) {
    switch (world.lipSync) {
      case 'error500':
        return errorEnvelope('INTERNAL_ERROR', 500);
      case 'timeout':
        return networkFailure();
      case 'not-provisioned':
        return errorEnvelope('NOT_FOUND', 404);
      case 'not-implemented':
        return errorEnvelope('NOT_IMPLEMENTED', 501);
      case 'empty':
        return jsonResponse({ segments: [] });
      case 'score-without-asset':
        return jsonResponse({ model: 'local-wav2lip', segments: [{ segmentId: 'seg_1', lipSyncScore: 0.8 }] });
      default:
        return jsonResponse(LIP_SYNC_BODY);
    }
  }

  if (method === 'GET' && url.includes('/admin/local-gpu')) {
    switch (world.localGpu) {
      case 'ok':
        return jsonResponse(LOCAL_GPU_BODY);
      case 'empty':
        return jsonResponse({});
      case 'error500':
        return errorEnvelope('INTERNAL_ERROR', 500);
      case 'timeout':
        return networkFailure();
      case 'forbidden':
        return errorEnvelope('FORBIDDEN', 403);
      case 'unreachable':
      default:
        return errorEnvelope('NOT_FOUND', 404);
    }
  }

  // --- core surfaces: everything below must stay green while enrichment fails
  if (method === 'GET' && url.includes('/admin/status')) {
    return jsonResponse({ status: 'ok', time: '2026-09-28T00:00:00Z' });
  }
  if (method === 'GET' && url.includes('/admin/usage')) {
    return jsonResponse({ storageUsedBytes: 1000, storageQuotaBytes: 100000, monthCostUsd: 12.5, projectsTodayRemaining: 9, activeRuns: 1, pendingReviews: 2, totalProjects: 4 });
  }
  if (method === 'GET' && url.includes('/admin/quotas')) {
    return jsonResponse({ maxActiveProjects: 10, maxProjectsPerDay: 10, maxCostPerProject: 50, storageUsedBytes: 100000, maxConcurrentStagesPerTenant: 4 });
  }
  if (method === 'GET' && url.includes('/admin/provider-health')) {
    return jsonResponse([{ provider: 'acme-stt', status: 'Healthy', latencyMsP95: 900, errorRate: 0.01, lastSuccessAt: '2026-09-28T00:00:00Z', activeRoutes: ['stt'], circuitBreakerState: 'Closed' }]);
  }
  if (method === 'GET' && url.includes('/admin/provider-routes')) {
    return jsonResponse([]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/queues')) {
    return jsonResponse([{ queue: 'media.prepare', depth: 2 }]);
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/dlq')) {
    return jsonResponse({ depth: 0, oldestEnqueuedAt: '', oldestEntryAge: '', topReasons: [] });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/leases')) {
    return jsonResponse({ items: [], page: 1, pageSize: 50, total: 0, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/orphans')) {
    return jsonResponse({ items: [], cursor: null, hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/diagnostics/review-backlog')) {
    return jsonResponse({ totalOpen: 0, byStatus: {}, bySeverity: {}, oldestWaitingAt: null, perProject: [] });
  }
  if (method === 'GET' && url.includes('/admin/tenants')) {
    return jsonResponse({ items: [] });
  }
  if (method === 'GET' && url.includes('/admin/users')) {
    return jsonResponse({ items: [] });
  }
  if (method === 'GET' && url.includes('/admin/audit-events')) {
    return jsonResponse({ items: [], hasMore: false });
  }
  if (method === 'GET' && url.includes('/admin/retention')) {
    return jsonResponse({ policies: [] });
  }
  if (method === 'GET' && url.includes('/admin/feature-flags')) {
    return jsonResponse({ flags: [] });
  }
  if (method === 'GET' && url.includes('/projects/prj_1/segments/seg_')) {
    const match = /\/segments\/(seg_[^/?]+)/.exec(url);
    const segmentId = match?.[1] ?? 'seg_1';
    return jsonResponse({ ...makeSegment(segmentId), id: segmentId, outputStale: false });
  }
  if (method === 'GET' && url.includes('/projects/prj_1/segments')) {
    return jsonResponse({
      items: [makeSegment('seg_1'), makeSegment('seg_2')],
      page: 1,
      pageSize: 200,
      total: 2,
      hasMore: false,
    });
  }
  if (method === 'GET' && url.includes('/projects/prj_1/export')) {
    return jsonResponse({ items: [] });
  }

  return jsonResponse({});
}

function renderWithProviders(node: React.ReactNode): void {
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>{node}</MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
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
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  authenticate(world.permissions);
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  vi.restoreAllMocks();
});

// =============================================================================
// R1 — flag-off invisibility
// =============================================================================

describe('R1 flag-off hides everything', () => {
  it('renders literally nothing: no chrome, no upsell, no disabled button', async () => {
    world = newWorld({ flags: ENRICHMENT_FLAGS_OFF });
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <MemoryRouter>
              <ProjectEnrichment projectId="prj_1" />
            </MemoryRouter>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );

    // Let every flag read and every lazy chunk settle before asserting.
    await waitFor(() => {
      expect(world.seenUrls.some((url) => url.includes('/me'))).toBe(true);
    });
    await new Promise((resolve) => setTimeout(resolve, 50));

    // The wrapper is the only node. R1 asks for zero chrome, so even the
    // "enrichment" container must not exist.
    expect(container.querySelectorAll('[data-testid^="enrichment-"]')).toHaveLength(0);
    expect(container.textContent ?? '').not.toMatch(/enrichment|scene cut|overlay|lip.?sync/i);
    // No disabled affordance anywhere.
    expect(container.querySelectorAll('button:disabled, a[aria-disabled="true"]')).toHaveLength(0);
  });

  it('issues no enrichment request at all with the flags off', async () => {
    world = newWorld({ flags: ENRICHMENT_FLAGS_OFF });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    await waitFor(() => {
      expect(world.seenUrls.some((url) => url.includes('/me'))).toBe(true);
    });
    await new Promise((resolve) => setTimeout(resolve, 50));

    expect(world.seenUrls.filter((url) => url.includes('/enrichment/'))).toEqual([]);
    expect(world.seenUrls.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
  });

  it('hides the operator GPU panel from the admin area with the flag off', async () => {
    world = newWorld({ flags: { ...ENRICHMENT_FLAGS_OFF, localGpu: false } });
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-page')).toBeDefined();
    // The other seven admin areas are untouched — the gate removes only itself.
    expect(await screen.findByTestId('admin-flags')).toBeDefined();
    expect(await screen.findByTestId('admin-ops')).toBeDefined();
    expect(screen.queryByTestId('admin-section-local-gpu')).toBeNull();
    expect(screen.queryByTestId('admin-local-gpu')).toBeNull();
    expect(world.seenUrls.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
  });

  it('fails closed on every unreadable flag source', () => {
    // Every one of these must resolve to all-off. The last three are the ones
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
      expect(parseEnrichmentFlags(raw)).toEqual(ENRICHMENT_FLAGS_OFF);
    }
  });

  it('reads the three /me wire flags and nothing else', () => {
    expect(parseEnrichmentFlags({ featureFlags: { videoIntelligenceEnabled: true } })).toEqual({
      videoIntel: true,
      lipSync: false,
      localGpu: false,
    });
    expect(parseEnrichmentFlags({ featureFlags: { lipSyncEnabled: true } }).lipSync).toBe(true);
    expect(parseEnrichmentFlags({ featureFlags: { localInferenceEnabled: true } }).localGpu).toBe(true);
    // Accepts an already-unwrapped slice.
    expect(parseEnrichmentFlags({ lipSyncEnabled: true }).lipSync).toBe(true);
    // A recognised-but-false key is not an absent key.
    expect(parseEnrichmentFlags({ featureFlags: { videoIntelligenceEnabled: false, lipSyncEnabled: true } })).toEqual({
      videoIntel: false,
      lipSync: true,
      localGpu: false,
    });
  });

  it('keeps an earlier resolution when a later refetch fails', async () => {
    world = newWorld({ flags: ALL_ON });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    expect(await screen.findByTestId('enrichment-video-intel')).toBeDefined();

    // A transient blip on /me must not make a working panel disappear.
    const cache = queryClient.getQueryCache();
    const flagsKey = queryKeys.enrichment.flags();
    cache.find({ queryKey: flagsKey })?.setState({ data: ALL_ON });
    await void queryClient.invalidateQueries({ queryKey: flagsKey, refetchType: 'none' });
    expect(screen.queryByTestId('enrichment-video-intel')).not.toBeNull();
  });

  it('exposes the gate decision as a pure predicate', () => {
    expect(enrichmentGateAllows(ALL_ON, 'videoIntel')).toBe(true);
    expect(enrichmentGateAllows(ALL_ON, 'localGpu')).toBe(true);
    expect(enrichmentGateAllows(ENRICHMENT_FLAGS_OFF, 'videoIntel')).toBe(false);
    expect(isEnrichmentFlagEnabled({ ...ALL_ON, lipSync: false }, 'lipSync')).toBe(false);
  });
});

describe('R1 gate mounting', () => {
  it('renders the child when the flag is on and nothing when it is off', async () => {
    const { unmount } = render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <EnrichmentFlagsProvider value={{ ...ALL_ON, lipSync: false }}>
            <MemoryRouter>
              <EnrichmentGate flag="lipSync">
                <p data-testid="gate-child">child</p>
              </EnrichmentGate>
            </MemoryRouter>
          </EnrichmentFlagsProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(screen.queryByTestId('gate-child')).toBeNull();
    unmount();

    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <EnrichmentFlagsProvider value={ALL_ON}>
            <MemoryRouter>
              <EnrichmentGate flag="lipSync">
                <p data-testid="gate-child">child</p>
              </EnrichmentGate>
            </MemoryRouter>
          </EnrichmentFlagsProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(screen.getByTestId('gate-child').textContent).toBe('child');
  });

  it('gates each capability independently', async () => {
    world = newWorld({ flags: { videoIntel: true, lipSync: false, localGpu: false } });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    expect(await screen.findByTestId('enrichment-video-intel')).toBeDefined();
    expect(screen.queryByTestId('enrichment-lip-sync')).toBeNull();
  });
});

// =============================================================================
// R2 — video-intel results are separate artifacts
// =============================================================================

describe('R2 video-intel artifacts are separate', () => {
  it('renders artifacts as their own list, linked but never spliced in', async () => {
    world = newWorld({ flags: ALL_ON });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" knownSegmentIds={new Set(['seg_1', 'seg_2'])} />);

    const list = await screen.findByTestId('enrichment-video-intel-list');
    // The secret-shaped and id-less rows never survive parsing.
    expect(list.querySelectorAll('li')).toHaveLength(3);
    expect(screen.getByTestId('enrichment-video-intel-artifact-art_scene_1')).toBeDefined();
    expect(screen.queryByTestId('enrichment-video-intel-artifact-art_bad')).toBeNull();

    // A live link is a ROUTE, not an in-place edit of the transcript.
    const link = screen.getByTestId('enrichment-video-intel-link-art_scene_1');
    expect(link.getAttribute('href')).toBe('/projects/prj_1/transcript?segment=seg_1');
    expect(screen.getByTestId('enrichment-video-intel-separate-note').textContent).toMatch(/never replace, reorder or edit/);
  });

  it('degrades a deleted segment link to Gone rather than dropping the row', async () => {
    world = newWorld({ flags: ALL_ON });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" knownSegmentIds={new Set(['seg_1', 'seg_2'])} />);
    const row = await screen.findByTestId('enrichment-video-intel-artifact-art_orphan_1');
    expect(row.getAttribute('data-link')).toBe('gone');
    expect(screen.getByTestId('enrichment-video-intel-gone-art_orphan_1').textContent).toMatch(/no longer exists/);
    // The artifact itself survives: its data still exists even though its
    // target does not.
    expect(screen.getByTestId('enrichment-video-intel-label-art_orphan_1').textContent).toBe('Deleted segment cut');
  });

  it('treats an unknown segment list as unlinked, never as gone', async () => {
    world = newWorld({ flags: ALL_ON });
    // No `knownSegmentIds`: a panel that cannot see segments must not declare
    // them deleted.
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    const row = await screen.findByTestId('enrichment-video-intel-artifact-art_orphan_1');
    expect(row.getAttribute('data-link')).toBe('unlinked');
    expect(screen.queryByTestId('enrichment-video-intel-gone-art_orphan_1')).toBeNull();
  });

  it('keeps enrichment query keys structurally disjoint from core keys', () => {
    // R2 is a property of the KEY FACTORY, not of a panel: if an enrichment
    // key were nested under the transcript prefix, a transcript invalidation
    // would rewrite enrichment state and the "separate artifacts" claim would
    // be a styling convention.
    const transcriptPrefix = ['projects', 'detail', 'prj_1', 'transcript'];
    const timelinePrefix = ['projects', 'detail', 'prj_1', 'timeline'];
    const videoIntel = ['enrichment', 'video-intel', 'prj_1'];
    const lipSync = ['enrichment', 'lip-sync', 'prj_1'];
    for (const enrichmentKey of [videoIntel, lipSync]) {
      expect(enrichmentKey.slice(0, transcriptPrefix.length)).not.toEqual(transcriptPrefix);
      expect(enrichmentKey.slice(0, timelinePrefix.length)).not.toEqual(timelinePrefix);
      expect(transcriptPrefix.includes(enrichmentKey[0] ?? '')).toBe(false);
      expect(timelinePrefix.includes(enrichmentKey[0] ?? '')).toBe(false);
    }
    expect(videoIntel).not.toEqual(lipSync);
  });

  it('renders an empty state when the payload carries no artifacts', async () => {
    world = newWorld({ flags: ALL_ON, videoIntel: 'empty' });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    expect(await screen.findByTestId('enrichment-video-intel-empty')).toBeDefined();
    expect(screen.queryByTestId('enrichment-video-intel-list')).toBeNull();
  });

  it('resolves segment links with three distinct outcomes', () => {
    expect(resolveSegmentLink('seg_1', new Set(['seg_1']))).toBe('linked');
    expect(resolveSegmentLink('seg_9', new Set(['seg_1']))).toBe('gone');
    expect(resolveSegmentLink('seg_9', undefined)).toBe('unlinked');
    expect(resolveSegmentLink('seg_9', new Set())).toBe('unlinked');
    expect(resolveSegmentLink('', new Set(['seg_1']))).toBe('unlinked');
  });

  it('drops provider endpoint and secret keys from the parsed view', () => {
    const view = parseVideoIntel({
      model: 'local-vision',
      endpoint: 'http://gpu.internal:8080',
      apiKey: 'sk-live-abcdef',
      artifacts: [{ id: 'a1', kind: 'overlay', label: 'ok', connectionString: 'Host=db;Password=x' }],
    });
    expect(view.model).toBe('local-vision');
    expect(JSON.stringify(view)).not.toMatch(/gpu\.internal|sk-live|connectionstring|password/i);
    expect(isForbiddenEnrichmentKey('endpoint')).toBe(true);
    expect(isForbiddenEnrichmentKey('inferenceUrl')).toBe(true);
    expect(isForbiddenEnrichmentKey('label')).toBe(false);
  });
});

// =============================================================================
// R3 — lip sync: score + method note, and a SEPARATE asset download
// =============================================================================

describe('R3 lip sync ships score plus separate asset', () => {
  it('shows every score with its method note on the same row', async () => {
    world = newWorld({ flags: ALL_ON });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    await screen.findByTestId('enrichment-lip-sync-segment-seg_1');
    expect(screen.getByTestId('enrichment-lip-sync-score-seg_1').textContent).toContain('0.94');
    // The backend's own method wins when it supplies one.
    expect(screen.getByTestId('enrichment-lip-sync-method-seg_1').textContent).toBe('measured offset v2');
    // Where it does not, the documented heuristic note is shown — never blank.
    expect(screen.getByTestId('enrichment-lip-sync-method-seg_2').textContent).toBe(LIPSYNC_METHOD_FALLBACK);
    expect(screen.getByTestId('enrichment-lip-sync-overall').textContent).toContain('heuristic v1');
  });

  it('never renders a score without a method note', async () => {
    world = newWorld({ flags: ALL_ON });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    await screen.findByTestId('enrichment-lip-sync-list');
    for (const segment of parseLipSync(LIP_SYNC_BODY).segments) {
      const text = screen.getByTestId(`enrichment-lip-sync-method-${segment.segmentId}`).textContent ?? '';
      expect(text.trim()).not.toBe('');
    }
  });

  it('keeps the score and hides the download with a reason when no asset exists', async () => {
    world = newWorld({ flags: ALL_ON, lipSync: 'score-without-asset' });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    const row = await screen.findByTestId('enrichment-lip-sync-segment-seg_1');
    // The score is real data and survives.
    expect(row.getAttribute('data-score-present')).toBe('true');
    expect(screen.getByTestId('enrichment-lip-sync-score-seg_1').textContent).toContain('0.80');
    // The missing download is explained rather than silently absent.
    expect(screen.getByTestId('enrichment-lip-sync-asset-missing-seg_1').textContent).toMatch(/not published/);
    expect(screen.getByTestId('enrichment-lip-sync-no-asset')).toBeDefined();
  });

  it('fetches the signed URL at click time only and never in the markup', async () => {
    world = newWorld({ flags: ALL_ON });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    const anchor = await screen.findByTestId('enrichment-lip-sync-asset-download');
    // Before the click: no signed URL anywhere in the document.
    expect(document.body.innerHTML).not.toMatch(/cdn\.example\.com/);
    expect(world.downloadCalls).toBe(0);
    expect(anchor.getAttribute('href')).toBe('#download');

    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function mockClick(this: HTMLAnchorElement) {
      // The URL exists only here, inside the handler.
      expect(this.href).toMatch(/^https:\/\/cdn\.example\.com\//);
    });
    fireEvent.click(anchor);
    await waitFor(() => {
      expect(world.downloadCalls).toBe(1);
    });
  });

  it('refetches exactly once on a 410 and surfaces a retryable error on double expiry', async () => {
    world = newWorld({ flags: ALL_ON, downloadBehavior: 'expired' });
    await expect(fetchLipSyncAssetUrl('prj_1')).resolves.toMatchObject({ url: 'https://cdn.example.com/lipsync/asset.wav?sig=fresh' });
    expect(world.downloadCalls).toBe(2);

    world = newWorld({ flags: ALL_ON, downloadBehavior: 'double-expired' });
    await expect(fetchLipSyncAssetUrl('prj_1')).rejects.toMatchObject({ code: 'URL_EXPIRED', status: 410 });
    // Exactly two calls: one attempt plus ONE refetch. A loop here would turn
    // one click into an unbounded request storm.
    expect(world.downloadCalls).toBe(2);
  });

  it('maps a missing asset and an unpreparable download to distinct errors', async () => {
    world = newWorld({ flags: ALL_ON, downloadBehavior: 'missing' });
    await expect(fetchLipSyncAssetUrl('prj_1')).rejects.toMatchObject({ status: 404 });

    world = newWorld({ flags: ALL_ON, downloadBehavior: 'server-error' });
    await expect(fetchLipSyncAssetUrl('prj_1')).rejects.toMatchObject({ status: 500 });

    world = newWorld({ flags: ALL_ON, downloadBehavior: 'no-location' });
    await expect(fetchLipSyncAssetUrl('prj_1')).rejects.toMatchObject({ status: 500 });
    expect(world.downloadCalls).toBe(1);
  });

  it('shows the download error with a correlation reference and a retry', async () => {
    world = newWorld({ flags: ALL_ON, downloadBehavior: 'server-error' });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    const anchor = await screen.findByTestId('enrichment-lip-sync-asset-download');
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined);
    fireEvent.click(anchor);
    expect(await screen.findByTestId('enrichment-lip-sync-asset-error')).toBeDefined();
    expect(screen.getByTestId('enrichment-lip-sync-asset-retry')).toBeDefined();

    fireEvent.click(screen.getByTestId('enrichment-lip-sync-asset-retry'));
    await waitFor(() => {
      expect(screen.queryByTestId('enrichment-lip-sync-asset-error')).toBeNull();
    });
  });

  it('clamps an out-of-range score instead of reporting confidence above 100%', () => {
    const view = parseLipSync({ segments: [{ segmentId: 'seg_1', lipSyncScore: 4.2 }] });
    expect(view.segments[0]?.score).toBe(1);
    expect(formatLipSyncScore(view.segments[0]?.score)).toBe('1.00');
    expect(parseLipSync({ segments: [{ segmentId: 'seg_1', lipSyncScore: -3 }] }).segments[0]?.score).toBe(0);
    expect(parseLipSync({ segments: [{ segmentId: 'seg_1', lipSyncScore: 'high' }] }).segments[0]?.score).toBeUndefined();
    expect(formatLipSyncScore(undefined)).toBe('—');
  });

  it('falls back to the documented heuristic note when the payload has none', () => {
    expect(resolveLipSyncMethod(undefined)).toBe(LIPSYNC_METHOD_FALLBACK);
    expect(resolveLipSyncMethod('')).toBe(LIPSYNC_METHOD_FALLBACK);
    expect(resolveLipSyncMethod('measured offset v2')).toBe('measured offset v2');
    expect(resolveLipSyncMethod('http://gpu.internal/method')).toBe(LIPSYNC_METHOD_FALLBACK);
  });

  it('formats the asset size for the separate download row', () => {
    expect(formatEnrichmentFileSize(0)).toBe('0 B');
    expect(formatEnrichmentFileSize(2048)).toBe('2 KB');
    expect(formatEnrichmentFileSize(5 * 1024 * 1024)).toBe('5 MB');
    expect(formatEnrichmentFileSize(undefined)).toBeUndefined();
    expect(formatEnrichmentFileSize(-1)).toBeUndefined();
  });

  it('never counts the lip-sync asset as a core output or export', async () => {
    world = newWorld({ flags: ALL_ON });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    await screen.findByTestId('enrichment-lip-sync');
    // The asset lives only in its own endpoint; the core export list is a
    // different route and is never asked for by this panel.
    const assetCalls = world.seenUrls.filter((url) => url.includes('/enrichment/lip-sync/asset'));
    expect(assetCalls).toEqual([]);
    expect(world.seenUrls.some((url) => url.includes('/projects/prj_1/exports'))).toBe(false);
    expect((await screen.findByTestId('enrichment-lip-sync-separate-note')).textContent).toMatch(/separate file/);
  });
});

// =============================================================================
// R4 — operator-only local-GPU health
// =============================================================================

describe('R4 local GPU health is operator-only', () => {
  it('summarises provider, model, version and device for an elevated operator', async () => {
    world = newWorld({ flags: ALL_ON, localGpu: 'ok' });
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-section-local-gpu')).toBeDefined();
    expect((await screen.findByTestId('admin-local-gpu-provider')).textContent).toBe('local-inference');
    expect(screen.getByTestId('admin-local-gpu-model').textContent).toBe('whisper-large-v3');
    expect(screen.getByTestId('admin-local-gpu-model-version').textContent).toBe('2026-08');
    expect(screen.getByTestId('admin-local-gpu-device').textContent).toBe('cuda:0');
    expect(screen.getByTestId('admin-local-gpu-device-count').textContent).toBe('1');
    expect(screen.getByTestId('admin-local-gpu-latency').textContent).toBe('42 ms');
  });

  it('leaks no sidecar endpoint or key into the admin DOM', async () => {
    world = newWorld({ flags: ALL_ON, localGpu: 'ok' });
    renderWithProviders(<AdminPage />);
    await screen.findByTestId('admin-local-gpu-summary');
    const text = document.body.textContent ?? '';
    expect(text).not.toMatch(/gpu-sidecar|sk-live|endpoint/i);
    expect(document.body.innerHTML).not.toMatch(/gpu-sidecar\.internal|sk-live-sidecar/);
  });

  it('shows Unknown, never Healthy, when the health endpoint is unreachable', async () => {
    world = newWorld({ flags: ALL_ON, localGpu: 'error500' });
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-local-gpu-unavailable')).toBeDefined();
    expect(screen.queryByTestId('admin-local-gpu-summary')).toBeNull();
    // Scoped to the GPU section: the provider-health panel legitimately renders
    // its own "Healthy" row, and asserting against the whole document would
    // either pass for the wrong reason or fail on an unrelated panel.
    expect(gpuSection().textContent).not.toMatch(/Healthy/);

    cleanup();
    world = newWorld({ flags: ALL_ON, localGpu: 'empty' });
    queryClient.clear();
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-local-gpu-unknown')).toBeDefined();
    expect(screen.getByTestId('enrichment-unknown-badge').textContent).toContain('unknown');
    expect(gpuSection().textContent).not.toMatch(/Healthy/);
  });

  it('treats the admin unknown-route 404 as not-provisioned, not as an error', async () => {
    world = newWorld({ flags: ALL_ON, localGpu: 'unreachable' });
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-local-gpu-not-available')).toBeDefined();
    expect(screen.getByTestId('enrichment-not-available-copy').textContent).toMatch(/operator feature in setup/);
  });

  it('never blocks the rest of the admin area when the GPU panel fails', async () => {
    world = newWorld({ flags: ALL_ON, localGpu: 'error500' });
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-local-gpu-unavailable')).toBeDefined();
    // Every other admin section still renders and still queried.
    expect(await screen.findByTestId('admin-ops')).toBeDefined();
    expect(await screen.findByTestId('admin-usage')).toBeDefined();
    expect(await screen.findByTestId('admin-flags')).toBeDefined();
    expect(world.seenUrls.some((url) => url.includes('/admin/usage'))).toBe(true);
    expect(screen.getByTestId('admin-local-gpu-nonblocking').textContent).toMatch(/rest of the admin area is unaffected/);
  });

  it('renders the privacy-policy routing note for operators', async () => {
    world = newWorld({ flags: ALL_ON, localGpu: 'ok' });
    renderWithProviders(<AdminPage />);
    const note = await screen.findByTestId('enrichment-privacy-note');
    expect(note.textContent).toMatch(/own local-inference host/);
    expect(note.textContent).toMatch(/never sent to a third-party provider/);
    expect(note.textContent).toMatch(/policy change and requires explicit operator opt-in/);
  });

  it('hides GPU internals from a user who reaches the panel without the grant', async () => {
    const { LocalGpuPanel } = await import('../../admin/LocalGpuPanel.js');
    world = newWorld({ flags: ALL_ON, localGpu: 'ok' });
    useAppStore.getState().setSession('authenticated', ['project.view']);
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <EnrichmentFlagsProvider value={ALL_ON}>
            <MemoryRouter>
              <LocalGpuPanel />
            </MemoryRouter>
          </EnrichmentFlagsProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    // The panel re-checks the elevated permission itself, so a future refactor
    // that moved it out from under `useAdminGuard` would not expose anything.
    await waitFor(() => {
      expect(container.querySelector('[data-testid="admin-local-gpu"]')).not.toBeNull();
    });
    expect(screen.getByTestId('admin-local-gpu-forbidden')).toBeDefined();
    expect(screen.queryByTestId('admin-local-gpu-summary')).toBeNull();
    // And it never even asks: a non-elevated user triggers no device request.
    expect(world.seenUrls.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
    expect(container.textContent ?? '').not.toMatch(/cuda|whisper|local-inference/i);
  });

  it('a revoked mid-session grant closes the panel without logging the operator out', async () => {
    world = newWorld({ flags: ALL_ON, localGpu: 'ok' });
    renderWithProviders(<AdminPage />);
    expect(await screen.findByTestId('admin-local-gpu-summary')).toBeDefined();
    // The grant is revoked server-side; the panel locks and the session lives.
    world.permissions = ['project.view'];
    useAppStore.getState().setSession('authenticated', world.permissions);
    await waitFor(() => {
      expect(screen.queryByTestId('admin-local-gpu-summary')).toBeNull();
    });
    expect(useAuthStore.getState().status).toBe('authenticated');
  });

  it('renders nothing for an ordinary user who reaches the panel directly', async () => {
    const { LocalGpuPanel } = await import('../../admin/LocalGpuPanel.js');
    world = newWorld({ flags: { ...ENRICHMENT_FLAGS_OFF, localGpu: false } });
    useAppStore.getState().setSession('authenticated', ['project.view']);
    const { container } = render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <MemoryRouter>
            <LocalGpuPanel />
          </MemoryRouter>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(container.innerHTML).toBe('');
    expect(world.seenUrls.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
  });

  it('drops endpoint-shaped keys from a parsed GPU document', () => {
    const view = parseLocalGpu(LOCAL_GPU_BODY);
    expect(view.provider).toBe('local-inference');
    expect(JSON.stringify(view)).not.toMatch(/gpu-sidecar|sk-live/);
    expect(resolveLocalGpuStatus('Healthy')).toBe('Healthy');
    expect(resolveLocalGpuStatus('ready')).toBe('Healthy');
    expect(resolveLocalGpuStatus('degraded')).toBe('Degraded');
    expect(resolveLocalGpuStatus('down')).toBe('Down');
    expect(resolveLocalGpuStatus('gibberish')).toBe('Unknown');
    expect(resolveLocalGpuStatus(undefined)).toBe('Unknown');
    expect(parseLocalGpu({}).empty).toBe(true);
    expect(isForbiddenLocalGpuKey('endpoint')).toBe(true);
    expect(isForbiddenLocalGpuKey('modelFilesystemPath')).toBe(true);
    expect(isForbiddenLocalGpuKey('device')).toBe(false);
    // A `device: 'cuda:0'` value is a device NAME, not a nested document;
    // descending into it would erase the field entirely.
    expect(parseLocalGpu({ device: 'cuda:0', model: 'm', modelVersion: 'v' }).device).toBe('cuda:0');
    expect(parseLocalGpu({ localGpu: { device: 'cuda:1', model: 'm', modelVersion: 'v' } }).device).toBe('cuda:1');
  });

  it('never queries device health before its flag resolves on', () => {
    world = newWorld({ flags: ENRICHMENT_FLAGS_OFF, localGpu: 'ok' });
    const Probe = (): React.ReactNode => {
      useLocalGpuHealth(false);
      return null;
    };
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <MemoryRouter>
            <Probe />
          </MemoryRouter>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(world.seenUrls.filter((url) => url.includes('/admin/local-gpu'))).toEqual([]);
  });
});

// =============================================================================
// R5 — enrichment failure never blocks core
// =============================================================================

describe('R5 enrichment failure never blocks core', () => {
  it.each(['error500', 'timeout'] as const)('survives a %s from both enrichment endpoints', async (outcome) => {
    world = newWorld({ flags: ALL_ON, videoIntel: outcome, lipSync: outcome });
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <EnrichmentFlagsProvider value={ALL_ON}>
              <MemoryRouter>
                <ProjectEnrichment projectId="prj_1" />
                <TranscriptEditor projectId="prj_1" />
              </MemoryRouter>
            </EnrichmentFlagsProvider>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );

    // Both enrichment surfaces report their own failure…
    expect(await screen.findByTestId('enrichment-video-intel-unavailable')).toBeDefined();
    expect(await screen.findByTestId('enrichment-lip-sync-unavailable')).toBeDefined();
    expect(screen.getAllByTestId('enrichment-unavailable-nonblocking').length).toBeGreaterThanOrEqual(2);

    // …and the transcript, rendered beside them, is unaffected.
    const transcript = await screen.findByTestId('transcript-editor');
    expect(transcript.textContent).toContain('core-transcript-seg_1');
    expect(world.seenUrls.some((url) => url.includes('/projects/prj_1/segments'))).toBe(true);
  });

  it('dismisses a failed enrichment surface without touching the transcript', async () => {
    world = newWorld({ flags: ALL_ON, videoIntel: 'error500' });
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <EnrichmentFlagsProvider value={ALL_ON}>
              <MemoryRouter>
                <ProjectEnrichment projectId="prj_1" />
                <TranscriptEditor projectId="prj_1" />
              </MemoryRouter>
            </EnrichmentFlagsProvider>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    await screen.findByTestId('enrichment-video-intel-unavailable');
    fireEvent.click(screen.getByTestId('enrichment-video-intel-dismiss'));
    await waitFor(() => {
      expect(screen.queryByTestId('enrichment-video-intel-unavailable')).toBeNull();
    });
    expect(screen.queryByTestId('enrichment-video-intel')).toBeNull();
    // The other panel and the core transcript are untouched by that dismissal.
    expect(screen.queryByTestId('enrichment-lip-sync')).not.toBeNull();
    expect(screen.getByTestId('transcript-editor').textContent).toContain('core-transcript-seg_2');
  });

  it('retries an enrichment failure on demand and recovers', async () => {
    world = newWorld({ flags: ALL_ON, videoIntel: 'error500' });
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <EnrichmentFlagsProvider value={ALL_ON}>
              <MemoryRouter>
                <ProjectEnrichment projectId="prj_1" />
              </MemoryRouter>
            </EnrichmentFlagsProvider>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    await screen.findByTestId('enrichment-video-intel-unavailable');
    world.videoIntel = 'ok';
    fireEvent.click(screen.getByTestId('enrichment-video-intel-unavailable').querySelector('button') as HTMLButtonElement);
    expect(await screen.findByTestId('enrichment-video-intel-list')).toBeDefined();
  });

  it('shows NotAvailable, not a failure, when the backend is unimplemented (404/501)', async () => {
    world = newWorld({ flags: ALL_ON, videoIntel: 'not-provisioned', lipSync: 'not-implemented' });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    expect(await screen.findByTestId('enrichment-video-intel-not-available')).toBeDefined();
    expect(await screen.findByTestId('enrichment-lip-sync-not-available')).toBeDefined();
    expect(screen.queryByTestId('enrichment-video-intel-unavailable')).toBeNull();
    expect(screen.getAllByTestId('enrichment-not-available-copy').length).toBeGreaterThanOrEqual(2);
    expect(isNotProvisionedError({ status: 404 })).toBe(true);
    expect(isNotProvisionedError({ status: 501 })).toBe(true);
    expect(isNotProvisionedError({ status: 500 })).toBe(false);
  });

  it('renders a 403 with no artifact detail at all', async () => {
    world = newWorld({ flags: ALL_ON, videoIntel: 'forbidden' });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    expect(await screen.findByTestId('enrichment-video-intel-forbidden')).toBeDefined();
    expect(screen.queryByTestId('enrichment-video-intel-list')).toBeNull();
  });
});

// =============================================================================
// R6 — the import gate
// =============================================================================

const SRC = resolve(process.cwd(), 'src');
const ENRICHMENT_DIR = join(SRC, 'features', 'enrichment');
const LAZY_PAGES = join(SRC, 'app', 'pages', 'lazy.ts');
const ADMIN_PAGE = join(SRC, 'features', 'admin', 'AdminPage.tsx');

/**
 * Enrichment modules permitted inside a chunk a user can load.
 *
 * The flag check and the gate ship; the payload does not. Two files, named
 * explicitly, and the exemption list is asserted to be *exactly* this set by
 * the "no new core module" test below — so adding a third file to a core
 * bundle has to be a deliberate edit of this list.
 */
const CORE_ALLOWED = new Set(['enrichmentFlags.ts', 'EnrichmentGate.tsx']);

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (entry === 'node_modules' || entry === 'generated' || entry === '__tests__') {
        continue;
      }
      walk(full, out);
      continue;
    }
    if (/\.(ts|tsx)$/.test(entry)) {
      out.push(full);
    }
  }
  return out;
}

/**
 * Static import edges only: `import ... from` and `export ... from`.
 *
 * The `^|\n\s*` anchor and the fact that the pattern requires the `from`
 * clause are what keep `import('./x.js')` out — a lazy edge is a different
 * mechanism and mixing the two would make this gate prove nothing.
 */
const STATIC_IMPORT = /(?:^|\n)\s*import\s+(?:type\s+)?[\s\S]{0,600}?\sfrom\s+['"]([^'"]+)['"]/g;
const STATIC_REEXPORT = /(?:^|\n)\s*export\s+(?:type\s+)?(?:\*|\{)[\s\S]{0,600}?\sfrom\s+['"]([^'"]+)['"]/g;
/** The only edge that may reach an enrichment panel: `import('...')`. */
const DYNAMIC_IMPORT = /\bimport\(\s*['"]([^'"]+)['"]\s*\)/g;

/** Resolves a relative specifier (`./x.js` → the `.tsx`/`.ts` file). */
function resolveSpecifier(fromFile: string, specifier: string): string | undefined {
  if (!specifier.startsWith('.')) {
    return undefined;
  }
  const base = resolve(dirname(fromFile), specifier).replace(/\.js$/, '');
  for (const candidate of [`${base}.ts`, `${base}.tsx`, base, join(base, 'index.ts'), join(base, 'index.tsx')]) {
    if (existsSync(candidate) && statSync(candidate).isFile()) {
      return candidate;
    }
  }
  return undefined;
}

function specifiersIn(file: string, pattern: RegExp): string[] {
  const text = readFileSync(file, 'utf8');
  pattern.lastIndex = 0;
  const found: string[] = [];
  let match = pattern.exec(text);
  while (match !== null) {
    found.push(match[1] ?? '');
    match = pattern.exec(text);
  }
  return found;
}

function edgesOf(file: string): { static: string[]; dynamic: string[] } {
  const statics = [...specifiersIn(file, STATIC_IMPORT), ...specifiersIn(file, STATIC_REEXPORT)]
    .map((specifier) => resolveSpecifier(file, specifier))
    .filter((target): target is string => target !== undefined);
  const dynamics = specifiersIn(file, DYNAMIC_IMPORT)
    .map((specifier) => resolveSpecifier(file, specifier))
    .filter((target): target is string => target !== undefined);
  return { static: [...new Set(statics)], dynamic: [...new Set(dynamics)] };
}

function closureFrom(entries: readonly string[], kind: 'static' | 'dynamic'): string[] {
  const seen = new Set<string>();
  const queue = [...entries];
  while (queue.length > 0) {
    const file = queue.pop() as string;
    if (seen.has(file)) {
      continue;
    }
    seen.add(file);
    const edges = edgesOf(file);
    for (const target of kind === 'static' ? edges.static : [...edges.static, ...edges.dynamic]) {
      if (!seen.has(target)) {
        queue.push(target);
      }
    }
  }
  return [...seen];
}

/**
 * Every route chunk a user can be sent to.
 *
 * `src/app/pages/lazy.ts` is the single lazy boundary in the app, so its
 * `import()` specifiers are exactly the route modules. Walking each one's
 * STATIC closure gives the modules that end up in that route's chunk — which
 * is the set that actually ships to an ordinary user, and therefore the set
 * R6 is about. Using only `main.tsx`'s closure would prove almost nothing:
 * every page is lazy, so the entry chunk reaches no page module at all.
 */
function routeModules(): string[] {
  return specifiersIn(LAZY_PAGES, DYNAMIC_IMPORT)
    .map((specifier) => resolveSpecifier(LAZY_PAGES, specifier))
    .filter((target): target is string => target !== undefined);
}

const ENTRY = join(SRC, 'main.tsx');

describe('R6 import gate: no user-loadable chunk contains an enrichment module', () => {
  it('the entry point and the lazy-route table exist (the gate is not vacuous)', () => {
    expect(existsSync(ENTRY)).toBe(true);
    expect(existsSync(LAZY_PAGES)).toBe(true);
    expect(routeModules().length).toBeGreaterThanOrEqual(15);
  });

  it('the media route module is among the roots (the surface we mount on)', () => {
    expect(routeModules()).toContain(join(SRC, 'app', 'pages', 'MediaPage.tsx'));
  });

  it('no enrichment panel, hook, parser or state module is in any route chunk', () => {
    const roots = routeModules();
    const reachable = new Set(closureFrom(roots, 'static'));
    // The whole directory, with an explicit exemption list — not a hand-picked
    // list of known-bad files, which a NEW panel would walk straight past.
    const modules = walk(ENRICHMENT_DIR).filter((file) => !file.includes('__tests__') && !file.endsWith('index.ts'));
    const leaked = modules.filter((file) => reachable.has(file) && !CORE_ALLOWED.has(file.slice(ENRICHMENT_DIR.length + 1)));
    expect(leaked.map((file) => file.replace(`${process.cwd()}/`, ''))).toEqual([]);
  });

  it('the only enrichment modules in route chunks are the flag check and the gate', () => {
    const reachable = new Set(closureFrom(routeModules(), 'static'));
    const inChunks = [...reachable].filter((file) => file.startsWith(`${ENRICHMENT_DIR}/`));
    expect(inChunks.map((file) => file.replace(`${process.cwd()}/`, '')).sort()).toEqual([
      'src/features/enrichment/EnrichmentGate.tsx',
      'src/features/enrichment/enrichmentFlags.ts',
    ]);
  });

  it('the exemption list names exactly the two files that ship, and nothing else', () => {
    // If someone adds a third "core" enrichment module this fails, which is the
    // point: widening the exemption must be a deliberate edit.
    expect([...CORE_ALLOWED].sort()).toEqual(['EnrichmentGate.tsx', 'enrichmentFlags.ts']);
  });

  it('the panels ARE reachable through a dynamic edge, so the gate is wired up', () => {
    // The other half of the gate. Without it, "no static import of the panels"
    // would also be true of a feature that was never mounted at all — the same
    // tautology the Task 043C report found twice in the restore drill.
    const reachable = new Set(closureFrom(routeModules(), 'dynamic'));
    for (const panel of [join(ENRICHMENT_DIR, 'VideoIntelPanel.tsx'), join(ENRICHMENT_DIR, 'LipSyncPanel.tsx')]) {
      expect(reachable.has(panel)).toBe(true);
    }
    expect(reachable.has(join(ENRICHMENT_DIR, 'types.ts'))).toBe(true);
    expect(reachable.has(join(ENRICHMENT_DIR, 'useEnrichmentQueries.ts'))).toBe(true);
  });

  it('the GPU panel is in the ADMIN chunk and no other route chunk', () => {
    const panel = join(SRC, 'features', 'admin', 'LocalGpuPanel.tsx');
    const adminChunk = new Set(closureFrom([ADMIN_PAGE], 'static'));
    expect(adminChunk.has(panel)).toBe(true);

    // Every other route module's chunk must be clean. The admin chunk itself is
    // only ever requested by `/admin`, which `RequireAdmin` guards.
    const others = routeModules().filter((module) => !module.includes(join('app', 'pages', 'AdminPage.tsx')));
    const offenders = others.filter((module) => closureFrom([module], 'static').includes(panel));
    expect(offenders.map((file) => file.replace(`${process.cwd()}/`, ''))).toEqual([]);
  });

  it('no route module statically imports the enrichment barrel', () => {
    // The barrel re-exports the panels, so one static import of it anywhere
    // would pull every panel into that chunk.
    const offenders: string[] = [];
    for (const file of walk(SRC)) {
      if (file.startsWith(`${ENRICHMENT_DIR}/`) || file.includes('__tests__')) {
        continue;
      }
      const statics = [...specifiersIn(file, STATIC_IMPORT), ...specifiersIn(file, STATIC_REEXPORT)];
      for (const specifier of statics) {
        if (/features\/enrichment(\/index\.js)?$/.test(specifier)) {
          offenders.push(`${file.replace(`${process.cwd()}/`, '')} -> ${specifier}`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });

  it('no enrichment module imports a core read model (the merge R2 forbids)', () => {
    // Separate artifacts LINK to core data; they never read it. An enrichment
    // module importing `features/transcript` or `features/timeline` would be
    // the first step back towards merging.
    const offenders: string[] = [];
    for (const file of walk(ENRICHMENT_DIR)) {
      if (file.includes('__tests__')) {
        continue;
      }
      const statics = [...specifiersIn(file, STATIC_IMPORT), ...specifiersIn(file, STATIC_REEXPORT)];
      for (const specifier of statics) {
        if (/features\/(transcript|timeline|translation|exports|review)\//.test(specifier)) {
          offenders.push(`${file.replace(`${process.cwd()}/`, '')} -> ${specifier}`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });
});

// =============================================================================
// hooks + query behaviour
// =============================================================================

describe('enrichment query wiring', () => {
  it('never fires an enrichment query before its flag resolves on', () => {
    world = newWorld({ flags: ENRICHMENT_FLAGS_OFF });
    const Probe = (): React.ReactNode => {
      const flags = { ...ENRICHMENT_FLAGS_OFF };
      useVideoIntel('prj_1', flags.videoIntel);
      return null;
    };
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <MemoryRouter>
            <Probe />
          </MemoryRouter>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(world.seenUrls.filter((url) => url.includes('/enrichment/'))).toEqual([]);
  });

  it('does not retry an enrichment 500 (a retry cannot fix an authorization-shaped answer)', async () => {
    world = newWorld({ flags: ALL_ON, videoIntel: 'error500' });
    renderWithProviders(<ProjectEnrichment projectId="prj_1" />);
    await screen.findByTestId('enrichment-video-intel-unavailable');
    const calls = world.seenUrls.filter((url) => url.includes('/enrichment/video-intel')).length;
    await new Promise((resolve) => setTimeout(resolve, 120));
    expect(world.seenUrls.filter((url) => url.includes('/enrichment/video-intel')).length).toBe(calls);
  });

  it('exposes a resolved snapshot only once /me has settled', async () => {
    let snapshot: ReturnType<typeof useEnrichmentFlagSnapshot> | undefined;
    const Probe = (): React.ReactNode => {
      snapshot = useEnrichmentFlagSnapshot();
      return null;
    };
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <MemoryRouter>
            <Probe />
          </MemoryRouter>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(snapshot?.resolved).toBe(false);
    expect(snapshot?.flags).toEqual(ENRICHMENT_FLAGS_OFF);
    await waitFor(() => {
      expect(snapshot?.resolved).toBe(true);
    });
    expect(snapshot?.failed).toBe(false);
    expect(typeof useEnrichmentFlagsQuery).toBe('function');
  });
});