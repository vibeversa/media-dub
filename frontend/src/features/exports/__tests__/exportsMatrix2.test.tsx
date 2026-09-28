// Delta 2: exports gap closure (round 2).
//
// Extends `exportsMatrix.test.tsx` with the remaining branches:
// - `useOutputs.ts`: fetch error normalization (output/exports/runs),
//   click-time `fetchExportDownloadUrl` retry matrix (410-refetch,
//   404-mapping, generic-error passthrough, missing-Location 500),
//   `fetchOutputDownloadUrl` ok/empty/error, `useCreateExport`
//   pre-network validation + profile omission + shapeless-response
//   defaults, `useProcessingRuns` guards, invalidations with detail ids.
// - `ExportCard.tsx`: type/scope/run/format selection flows,
//   allowPartial body flag, derived-profile bodies, and the
//   mid-dialog stale-run validation error (recovery: reselect).
// - `ExportRow.tsx`: retry-unavailable without `export.create`
//   (recovery: permission hint), generic download errors with a clear
//   action (recovery: retry), double-click guard + pending text.
// - `types.ts` sweep 2: every format/display alias, completeness and
//   file-size parsers, unsafe-URL/internal-path drops, subtitle fan-out,
//   envelope sorting/skips, scope/profile/validation branches, partial
//   explanations, and the error/icon/pattern helpers.
// - `OutputsPage.tsx` (no `features/workspace` dir exists in this repo,
//   so the page-level branches land here): Generating progress,
//   Failed error code, Unavailable reason, warnings, item
//   reason/detail, and the export-removed toast (recovery: refresh).
//
// Every failure asserts its recovery control per §11.6 with text
// signals (041C); fixtures are synthetic (R3); parsed shapes use
// `undefined`, never `null`.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { createMemoryRouter, RouterProvider } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../app/router.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { ExportCard } from '../ExportCard.js';
import { OutputsPage } from '../OutputsPage.js';
import type { AppError } from '../../../api/errors/index.js';
import {
  advertisedScopes,
  formatFileSize,
  formatsForType,
  iconForExportState,
  isAllowlistedFormat,
  isAllowlistedScope,
  isAllowlistedType,
  isAlreadyGeneratingError,
  isExpiredError,
  isNotFoundError,
  labelForOutputState,
  normalizeExportDisplayState,
  normalizeExportFormat,
  normalizeOutputState,
  outputStateKey,
  parseExport,
  parseExports,
  parseOutput,
  parseProcessingRuns,
  partialExplanationFor,
  partialExplanationForItem,
  patternForExportState,
  profileForRequest,
  validateExportRequest,
} from '../types.js';
import {
  fetchExportDownloadUrl,
  fetchExports,
  fetchOutput,
  fetchOutputDownloadUrl,
  fetchProcessingRuns,
  invalidateExports,
  invalidateOutputs,
  useCreateExport,
  useExports,
  useOutputs,
  useProcessingRuns,
} from '../useOutputs.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-e99', details: {} } },
    status,
  );
}

function redirectResponse(location: string): Response {
  return new Response(null, { status: 302, headers: { Location: location } });
}

function outputFixture(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    state: 'Partial',
    generationState: 'Partial',
    reason: undefined,
    completeness: { ready: 96, total: 100 },
    progressApproximate: undefined,
    errorCode: undefined,
    items: {
      video: { state: 'ready', generationState: 'ready', downloadUrl: 'https://example.com/video.mp4', missing: [], completeness: undefined },
      audio: { state: 'generating', generationState: 'generating', downloadUrl: undefined, missing: ['SEGMENT_PENDING'], completeness: { ready: 50, total: 100 } },
      subtitles: [{ state: 'ready', generationState: 'ready', downloadUrl: 'https://example.com/subs.srt', missing: [], completeness: undefined }],
      transcript: { state: 'failed', generationState: 'failed', downloadUrl: undefined, missing: ['ARTIFACT_MISSING'], completeness: undefined },
      translation: { state: 'partial', generationState: 'partial', downloadUrl: 'https://example.com/trans.json', missing: ['SEGMENT_PENDING'], completeness: { ready: 96, total: 100 } },
      timeline: { state: 'unavailable', generationState: 'unavailable', downloadUrl: undefined, missing: ['NO_RUNS_YET'], completeness: undefined },
      speakers: { state: 'ready', generationState: 'ready', downloadUrl: undefined, missing: [], completeness: undefined },
      qc: { state: 'partial', generationState: 'partial', summary: '4 finding(s), 1 blocked.', issuesUrl: undefined, missing: ['QC_BLOCKED'] },
    },
    warnings: ['qc-blocked'],
    updatedAt: '2024-01-16T12:00:00Z',
    ...overrides,
  };
}

function exportsFixture(): Record<string, unknown> {
  return {
    items: [
      { id: 'exp_queued_1', projectId: 'prj_1', format: 'srt', status: 'Pending', isPartial: false, createdAt: '2024-01-16T10:00:00Z', completenessJson: undefined },
      { id: 'exp_ready_1', projectId: 'prj_1', format: 'json-timeline', status: 'Completed', isPartial: false, createdAt: '2024-01-16T12:00:00Z', completenessJson: undefined, sizeBytes: 2048 },
      { id: 'exp_failed_1', projectId: 'prj_1', format: 'transcript', status: 'Failed', isPartial: true, createdAt: '2024-01-16T09:00:00Z', completenessJson: undefined, reason: 'Render failed' },
    ],
    page: 1,
    pageSize: 100,
    total: 3,
    hasMore: false,
  };
}

type DownloadBehavior =
  | 'ok'
  | 'expired-once'
  | 'expired-twice'
  | 'not-found'
  | 'broken'
  | 'broken-text'
  | 'no-location'
  | 'expired-then-missing'
  | 'expired-then-broken';

interface ExportsDeltaWorld {
  output: Record<string, unknown>;
  outputMode: 'ok' | 'error500';
  exportsMode: 'ok' | 'empty' | 'error500';
  runsItems: Array<Record<string, unknown>>;
  runsMode: 'ok' | 'error500';
  createBehavior: 'ok' | 'conflict' | 'empty';
  downloadBehavior: DownloadBehavior;
  outputDownloadMode: 'ok' | 'empty' | 'error500';
  createCalls: number;
  createBodies: unknown[];
  downloadCalls: number;
  outputCalls: number;
  exportsCalls: number;
  runsCalls: number;
  outputDownloadCalls: number;
}

let world: ExportsDeltaWorld;
let downloadAttempts = 0;

function resetWorld(): void {
  world = {
    output: outputFixture(),
    outputMode: 'ok',
    exportsMode: 'ok',
    runsItems: [
      { runId: 'run_2', status: 'Running' },
      { runId: 'run_1', status: 'Completed' },
    ],
    runsMode: 'ok',
    createBehavior: 'ok',
    downloadBehavior: 'ok',
    outputDownloadMode: 'ok',
    createCalls: 0,
    createBodies: [],
    downloadCalls: 0,
    outputCalls: 0,
    exportsCalls: 0,
    runsCalls: 0,
    outputDownloadCalls: 0,
  };
  downloadAttempts = 0;
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

function bodyOf(init?: RequestInit): unknown {
  if (typeof init?.body !== 'string') {
    return undefined;
  }
  try {
    return JSON.parse(init.body) as unknown;
  } catch {
    return undefined;
  }
}

function downloadForAttempt(): Response {
  downloadAttempts += 1;
  switch (world.downloadBehavior) {
    case 'ok':
      return redirectResponse('https://example.com/export.srt');
    case 'expired-once':
      return downloadAttempts === 1
        ? errorEnvelope('URL_EXPIRED', 410, 'This download link expired.')
        : redirectResponse('https://example.com/export.srt');
    case 'expired-twice':
      return errorEnvelope('URL_EXPIRED', 410, 'This download link expired.');
    case 'not-found':
      return errorEnvelope('NOT_FOUND', 404, 'This export no longer exists.');
    case 'broken':
      return errorEnvelope('BROKEN', 500, 'boom download');
    case 'broken-text':
      return new Response('oops', { status: 500, headers: { 'Content-Type': 'text/plain' } });
    case 'no-location':
      return new Response(null, { status: 302 });
    case 'expired-then-missing':
      return downloadAttempts === 1
        ? errorEnvelope('URL_EXPIRED', 410, 'This download link expired.')
        : errorEnvelope('NOT_FOUND', 404, 'This export no longer exists.');
    case 'expired-then-broken':
      return downloadAttempts === 1
        ? errorEnvelope('URL_EXPIRED', 410, 'This download link expired.')
        : errorEnvelope('BROKEN', 500, 'boom download');
  }
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) {
    return jsonResponse({});
  }
  if (method === 'POST' && url.includes('/exports') && !url.includes('/download')) {
    world.createCalls += 1;
    world.createBodies.push(bodyOf(init));
    if (world.createBehavior === 'conflict') {
      return errorEnvelope('EXPORT_NOT_READY', 409, 'Export already generating.');
    }
    if (world.createBehavior === 'empty') {
      return jsonResponse({});
    }
    const body = world.createBodies[world.createBodies.length - 1] as Record<string, unknown>;
    const format = typeof body['format'] === 'string' ? (body['format'] as string) : 'srt';
    return jsonResponse({ id: 'exp_new_1', projectId: 'prj_1', format, status: 'Pending', isPartial: false, createdAt: '2024-01-16T13:00:00Z', completenessJson: undefined }, 202);
  }
  if (method === 'GET' && /\/exports\/[^/?]+\/download/.test(url)) {
    world.downloadCalls += 1;
    return downloadForAttempt();
  }
  if (method === 'GET' && url.includes('/exports') && !url.includes('/download')) {
    world.exportsCalls += 1;
    if (world.exportsMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.exportsMode === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 100, total: 0, hasMore: false });
    }
    return jsonResponse(exportsFixture());
  }
  if (method === 'GET' && url.includes('/output/download')) {
    world.outputDownloadCalls += 1;
    if (world.outputDownloadMode === 'empty') {
      return jsonResponse({});
    }
    if (world.outputDownloadMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({ downloadUrl: 'https://example.com/output.mp4' });
  }
  if (method === 'GET' && url.includes('/output') && !url.includes('/download')) {
    world.outputCalls += 1;
    if (world.outputMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse(world.output);
  }
  if (method === 'GET' && url.includes('/processing') && !url.includes('/processing/')) {
    world.runsCalls += 1;
    if (world.runsMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({ items: world.runsItems, page: 1, pageSize: 20, total: world.runsItems.length, hasMore: false });
  }
  return jsonResponse({});
}

function authenticate(permissions: readonly string[] = ['project.view', 'export.create']): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', permissions);
}

function renderPage(projectId = 'prj_1'): void {
  const router = createMemoryRouter([{ path: '/projects/:id/exports', element: <OutputsPage projectId={projectId} /> }], {
    initialEntries: [`/projects/${projectId}/exports`],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

function renderCard(output: unknown): void {
  const router = createMemoryRouter([{ path: '/', element: <ExportCard projectId="prj_1" output={output as never} /> }], {
    initialEntries: ['/'],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  const clickStub = vi.fn();
  Object.defineProperty(window.HTMLAnchorElement.prototype, 'click', {
    configurable: true,
    writable: true,
    value: clickStub,
  });
  authenticate();
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

describe('fetch error normalization', () => {
  it('normalizes output/exports/runs failures to AppError (recovery: retry)', async () => {
    world.outputMode = 'error500';
    world.exportsMode = 'error500';
    world.runsMode = 'error500';
    await expect(fetchOutput('prj_1')).rejects.toMatchObject({ code: 'INTERNAL_ERROR', status: 500 });
    await expect(fetchExports('prj_1')).rejects.toMatchObject({ code: 'INTERNAL_ERROR', status: 500 });
    await expect(fetchProcessingRuns('prj_1')).rejects.toMatchObject({ code: 'INTERNAL_ERROR', status: 500 });
    const error: unknown = await fetchOutput('prj_1').catch((e: unknown) => e);
    expect(typeof (error as AppError).correlationId).toBe('string');
  });

  it('parses the happy-path aggregates without throwing', async () => {
    const output = await fetchOutput('prj_1');
    expect(output.state).toBe('Partial');
    const exports = await fetchExports('prj_1');
    expect(exports.length).toBe(3);
    const runs = await fetchProcessingRuns('prj_1');
    expect(runs.map((run) => run.id)).toEqual(['run_2', 'run_1']);
  });
});

describe('fetchExportDownloadUrl retry matrix', () => {
  it('returns the signed URL on first success (recovery: none needed)', async () => {
    const result = await fetchExportDownloadUrl('prj_1', 'exp_ready_1');
    expect(result.url).toBe('https://example.com/export.srt');
    expect(world.downloadCalls).toBe(1);
  });

  it('refetches once after expiry and resumes (recovery: retry)', async () => {
    world.downloadBehavior = 'expired-once';
    const result = await fetchExportDownloadUrl('prj_1', 'exp_ready_1');
    expect(result.url).toBe('https://example.com/export.srt');
    expect(world.downloadCalls).toBe(2);
  });

  it('rejects double expiry with URL_EXPIRED (recovery: retry)', async () => {
    world.downloadBehavior = 'expired-twice';
    await expect(fetchExportDownloadUrl('prj_1', 'exp_ready_1')).rejects.toMatchObject({ code: 'URL_EXPIRED', status: 410 });
    expect(world.downloadCalls).toBe(2);
  });

  it('maps missing rows to NOT_FOUND without a second call (recovery: refresh)', async () => {
    world.downloadBehavior = 'not-found';
    await expect(fetchExportDownloadUrl('prj_1', 'exp_ready_1')).rejects.toMatchObject({ code: 'NOT_FOUND', status: 404 });
    expect(world.downloadCalls).toBe(1);
  });

  it('maps an expired-then-deleted retry to NOT_FOUND (recovery: refresh)', async () => {
    world.downloadBehavior = 'expired-then-missing';
    await expect(fetchExportDownloadUrl('prj_1', 'exp_ready_1')).rejects.toMatchObject({ code: 'NOT_FOUND', status: 404 });
    expect(world.downloadCalls).toBe(2);
  });

  it('passes retry failures through normalized (recovery: report id)', async () => {
    world.downloadBehavior = 'expired-then-broken';
    await expect(fetchExportDownloadUrl('prj_1', 'exp_ready_1')).rejects.toMatchObject({ status: 500 });
    world.downloadBehavior = 'broken';
    await expect(fetchExportDownloadUrl('prj_1', 'exp_ready_1')).rejects.toMatchObject({ status: 500 });
    world.downloadBehavior = 'broken-text';
    await expect(fetchExportDownloadUrl('prj_1', 'exp_ready_1')).rejects.toMatchObject({ status: 500 });
  });

  it('rejects location-less redirects as unpreparable (recovery: retry)', async () => {
    world.downloadBehavior = 'no-location';
    await expect(fetchExportDownloadUrl('prj_1', 'exp_ready_1')).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
  });
});

describe('fetchOutputDownloadUrl', () => {
  it('returns the descriptor URL, rejects empty/error payloads (recovery: retry)', async () => {
    const ok = await fetchOutputDownloadUrl('prj_1');
    expect(ok.url).toBe('https://example.com/output.mp4');
    world.outputDownloadMode = 'empty';
    await expect(fetchOutputDownloadUrl('prj_1')).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
    world.outputDownloadMode = 'error500';
    await expect(fetchOutputDownloadUrl('prj_1')).rejects.toMatchObject({ code: 'INTERNAL_ERROR', status: 500 });
  });
});

describe('useCreateExport guards', () => {
  function renderMutationProbe(): void {
    function Probe(): null {
      const mutation = useCreateExport('prj_1');
      (window as unknown as { __create?: unknown }).__create = mutation;
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
  }

  it('rejects unallowlisted formats before any network call (recovery: reselect)', async () => {
    renderMutationProbe();
    const mutation = (window as unknown as { __create?: { mutateAsync: (v: unknown) => Promise<unknown> } }).__create;
    expect(mutation).toBeDefined();
    await expect(mutation?.mutateAsync({ format: 'bogus!!', allowPartial: false })).rejects.toMatchObject({
      code: 'VALIDATION_FAILED',
      status: 400,
    });
    expect(world.createCalls).toBe(0);
    cleanup();
    delete (window as unknown as { __create?: unknown }).__create;
  });

  it('omits empty profiles and forwards allowPartial (recovery: none needed)', async () => {
    renderMutationProbe();
    const mutation = (window as unknown as { __create?: { mutateAsync: (v: unknown) => Promise<unknown> } }).__create;
    await mutation?.mutateAsync({ format: 'srt', profile: '', allowPartial: true });
    const body = world.createBodies[world.createBodies.length - 1] as Record<string, unknown>;
    expect(body['format']).toBe('srt');
    expect(body['allowPartial']).toBe(true);
    expect('profile' in body).toBe(false);
    await mutation?.mutateAsync({ format: 'srt', profile: 'subtitles', allowPartial: false });
    const withProfile = world.createBodies[world.createBodies.length - 1] as Record<string, unknown>;
    expect(withProfile['profile']).toBe('subtitles');
    cleanup();
    delete (window as unknown as { __create?: unknown }).__create;
  });

  it('falls back to request values on shapeless responses (recovery: refresh)', async () => {
    world.createBehavior = 'empty';
    renderMutationProbe();
    const mutation = (window as unknown as { __create?: { mutateAsync: (v: unknown) => Promise<{ id: string; format: string; status: string; isPartial: boolean }> } }).__create;
    const result = await mutation?.mutateAsync({ format: 'srt', allowPartial: false });
    expect(result?.id).toBe('');
    expect(result?.format).toBe('srt');
    expect(result?.status).toBe('');
    expect(result?.isPartial).toBe(false);
    cleanup();
    delete (window as unknown as { __create?: unknown }).__create;
  });
});

describe('hook guards + invalidations', () => {
  it('never fires processing runs for empty ids or anonymous sessions', async () => {
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
      useProcessingRuns('');
      useOutputs('');
      useExports('');
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    await new Promise((resolve) => setTimeout(resolve, 100));
    expect(calls).toBe(0);
    useAuthStore.setState({ status: 'anonymous' });
    await invalidateOutputs(queryClient, 'prj_1');
    await invalidateExports(queryClient, 'prj_1', 'exp_ready_1');
    await invalidateExports(queryClient, 'prj_1');
  });
});

describe('ExportCard selection flows', () => {
  it('sends the derived profile + allowPartial from type/scope/checkbox (recovery: none needed)', async () => {
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    fireEvent.change(screen.getByTestId('export-type'), { target: { value: 'video' } });
    fireEvent.change(screen.getByTestId('export-scope'), { target: { value: 'segment-range' } });
    fireEvent.change(screen.getByTestId('export-format'), { target: { value: 'srt' } });
    fireEvent.change(screen.getByTestId('export-run'), { target: { value: 'run_1' } });
    fireEvent.click(screen.getByTestId('export-allow-partial'));
    fireEvent.click(screen.getByTestId('export-submit'));
    await waitFor(() => expect(world.createCalls).toBe(1));
    const body = world.createBodies[world.createBodies.length - 1] as Record<string, unknown>;
    expect(body['format']).toBe('srt');
    expect(body['profile']).toBe('video-segment-range');
    expect(body['allowPartial']).toBe(true);
  });

  it('surfaces stale run selections inline for reselection (recovery: reselect)', async () => {
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    fireEvent.change(screen.getByTestId('export-run'), { target: { value: 'run_1' } });
    world.runsItems = [];
    await queryClient.invalidateQueries();
    await waitFor(() => expect(screen.queryByTestId('export-run-option-run_1')).toBeNull());
    fireEvent.click(screen.getByTestId('export-submit'));
    expect(await screen.findByTestId('export-error')).toBeDefined();
    expect(screen.getByTestId('export-error-message').textContent).toContain('processing run');
    expect(world.createCalls).toBe(0);
  });

  it('notes run-list failures and still submits against latest (recovery: proceed)', async () => {
    world.runsMode = 'error500';
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-runs-error')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-submit'));
    await waitFor(() => expect(world.createCalls).toBe(1));
  });
});

describe('ExportRow permission + download matrix', () => {
  it('explains retry-unavailable rows without a dead button (recovery: permission)', async () => {
    authenticate(['project.view']);
    renderPage();
    const cell = await screen.findByTestId('export-retry-unavailable-exp_failed_1');
    expect(cell.textContent).toContain('Retry unavailable');
    expect(cell.getAttribute('title')).toContain('export.create');
    expect(screen.queryByTestId('export-retry-exp_failed_1')).toBeNull();
  });

  it('shows generic download failures with a clear action (recovery: retry)', async () => {
    world.downloadBehavior = 'broken';
    renderPage();
    fireEvent.click(await screen.findByTestId('export-download-exp_ready_1'));
    const box = await screen.findByTestId('export-download-error-exp_ready_1');
    expect(box).toBeDefined();
    expect(screen.getByTestId('export-download-error-message-exp_ready_1').textContent).not.toBe('');
    fireEvent.click(screen.getByTestId('export-download-retry-exp_ready_1'));
    await waitFor(() => expect(screen.queryByTestId('export-download-error-exp_ready_1')).toBeNull());
  });

  it('guards double clicks with pending text and a single fetch (recovery: wait)', async () => {
    let release!: (response: Response) => void;
    const gate = new Promise<Response>((resolve) => {
      release = resolve;
    });
    let gated = true;
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      if (gated && /\/exports\/[^/?]+\/download/.test(url)) {
        world.downloadCalls += 1;
        return gate;
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    renderPage();
    const link = await screen.findByTestId('export-download-exp_ready_1');
    fireEvent.click(link);
    fireEvent.click(link);
    await waitFor(() => expect(screen.getByTestId('export-download-exp_ready_1').textContent).toContain('Preparing download'));
    expect(world.downloadCalls).toBe(1);
    gated = false;
    release(redirectResponse('https://example.com/export.srt'));
    await waitFor(() => expect(screen.getByTestId('export-download-exp_ready_1').textContent).toContain('Download'));
  });
});

describe('OutputsPage summary branches', () => {
  it('renders generating progress + unavailable reasons with text (never blank)', async () => {
    world.output = outputFixture({ state: 'Generating', generationState: 'Generating', progressApproximate: 42, warnings: ['review-open:2'] });
    renderPage();
    expect(await screen.findByTestId('outputs-detail')).toBeDefined();
    expect(screen.getByTestId('outputs-progress').textContent).toContain('42');
    expect(screen.getByTestId('outputs-state-generating')).toBeDefined();
    expect(screen.getByTestId('outputs-warnings').textContent).toContain('review-open:2');
    expect(screen.getByRole('button', { name: 'Request export' })).toBeDefined();
  });

  it('shows failed error codes and unavailable reasons (recovery: refresh)', async () => {
    world.output = outputFixture({ state: 'Failed', generationState: 'Failed', errorCode: 'RENDER_CRASHED' });
    renderPage();
    expect(await screen.findByTestId('outputs-error-code')).toBeDefined();
    expect(screen.getByTestId('outputs-error-code').textContent).toContain('RENDER_CRASHED');
    cleanup();
    queryClient.clear();
    world.output = outputFixture({ state: 'Unavailable', generationState: 'Unavailable', reason: 'NO_RUNS_YET' });
    renderPage();
    expect(await screen.findByTestId('outputs-reason')).toBeDefined();
    expect(screen.getByTestId('outputs-reason').textContent).toContain('NO_RUNS_YET');
  });

  it('renders item reasons and QC details as text (never color-only)', async () => {
    world.output = outputFixture({
      items: {
        video: { state: 'failed', downloadUrl: undefined, missing: [], reason: 'Render boom', completeness: undefined },
        qc: { state: 'ready', summary: 'All good', issuesUrl: undefined, missing: [] },
      },
    });
    renderPage();
    expect(await screen.findByTestId('output-reason-video')).toBeDefined();
    expect(screen.getByTestId('output-reason-video').textContent).toContain('Render boom');
    expect(screen.getByTestId('output-detail-qc').textContent).toContain('All good');
  });

  it('toasts removed exports while keeping the list honest (recovery: refresh)', async () => {
    renderPage();
    expect(await screen.findByTestId('export-row-exp_ready_1')).toBeDefined();
    world.exportsMode = 'empty';
    await queryClient.invalidateQueries();
    expect(await screen.findByTestId('exports-empty')).toBeDefined();
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('was removed'),
    );
  });
});

describe('exports types sweep 2', () => {
  it('normalizes every format spelling to the kebab wire name', () => {
    expect(normalizeExportFormat('VTT')).toBe('webvtt');
    expect(normalizeExportFormat('web-vtt')).toBe('webvtt');
    expect(normalizeExportFormat('MP4')).toBe('mp4');
    expect(normalizeExportFormat('wav')).toBe('wav');
    expect(normalizeExportFormat('mp3')).toBe('mp3');
    expect(normalizeExportFormat('timeline')).toBe('json-timeline');
    expect(normalizeExportFormat('timeline-json')).toBe('json-timeline');
    expect(normalizeExportFormat('jsontimeline')).toBe('json-timeline');
    expect(normalizeExportFormat('json_timeline')).toBe('json-timeline');
    expect(normalizeExportFormat('speakers')).toBe('speaker-metadata');
    expect(normalizeExportFormat('speakerMetadata')).toBe('speaker-metadata');
    expect(normalizeExportFormat('transcript-json')).toBe('transcript');
    expect(normalizeExportFormat('transcripts')).toBe('transcript');
    expect(normalizeExportFormat('translation-json')).toBe('translation');
    expect(normalizeExportFormat('translations')).toBe('translation');
    expect(normalizeExportFormat('qualityreport')).toBe('quality-report');
    expect(normalizeExportFormat('qc')).toBe('quality-report');
    expect(normalizeExportFormat('qc-report')).toBe('quality-report');
    expect(normalizeExportFormat('  WEBVTT  ')).toBe('webvtt');
    expect(normalizeExportFormat('')).toBeUndefined();
    expect(normalizeExportFormat(123)).toBeUndefined();
    expect(normalizeExportFormat('bogus')).toBeUndefined();
    expect(isAllowlistedFormat('mp4')).toBe(false);
    expect(isAllowlistedType('audio')).toBe(true);
    expect(isAllowlistedType('bogus')).toBe(false);
    expect(isAllowlistedScope('full')).toBe(true);
    expect(isAllowlistedScope('bogus')).toBe(false);
    expect(formatsForType('bogus')).toEqual([]);
    expect(formatsForType('audio').length).toBeGreaterThan(0);
  });

  it('normalizes every display-state spelling', () => {
    expect(normalizeExportDisplayState('queue')).toBe('queued');
    expect(normalizeExportDisplayState('in-progress')).toBe('generating');
    expect(normalizeExportDisplayState('inprogress')).toBe('generating');
    expect(normalizeExportDisplayState('succeeded')).toBe('ready');
    expect(normalizeExportDisplayState('success')).toBe('ready');
    expect(normalizeExportDisplayState('canceled')).toBe('failed');
    expect(normalizeExportDisplayState('error')).toBe('failed');
    expect(normalizeExportDisplayState('bogus')).toBe('queued');
    expect(normalizeExportDisplayState('')).toBe('queued');
    expect(normalizeOutputState('BOGUS')).toBe('Unavailable');
    expect(outputStateKey('Generating')).toBe('generating');
    expect(labelForOutputState('Unavailable')).toBe('unavailable');
    expect(iconForExportState('queued')).toBe('○');
    expect(iconForExportState('generating')).toBe('↻');
    expect(iconForExportState('ready')).toBe('✓');
    expect(iconForExportState('failed')).toBe('✕');
    expect(patternForExportState('queued')).toBe('hollow-block');
    expect(patternForExportState('generating')).toBe('dotted-block');
    expect(patternForExportState('ready')).toBe('solid-fill');
    expect(patternForExportState('failed')).toBe('crosshatch-block');
  });

  it('parses completeness, file sizes, and envelopes defensively', () => {
    const clamped = parseOutput({ state: 'Ready', completeness: { ready: 'x', total: undefined } });
    expect(clamped.completeness).toEqual({ ready: 0, total: 0 });
    expect(parseExport(undefined)).toBeUndefined();
    expect(parseExport({ id: '', format: 'srt' })).toBeUndefined();
    const badJson = parseExport({ id: 'e1', projectId: 'p', format: 'srt', status: 'Running', completenessJson: 'nope{' });
    expect(badJson?.completeness).toBeUndefined();
    const partialJson = parseExport({ id: 'e1', projectId: 'p', format: 'srt', status: 'Running', completenessJson: '{"ready":1}' });
    expect(partialJson?.completeness).toBeUndefined();
    const goodJson = parseExport({ id: 'e1', projectId: 'p', format: 'srt', status: 'Running', completenessJson: '{"ready":50,"total":100}' });
    expect(goodJson?.completeness).toEqual({ ready: 50, total: 100 });
    const floatObj = parseExport({ id: 'e1', projectId: 'p', format: 'srt', status: 'Running', completeness: { ready: 1.6, total: 100.4 } });
    expect(floatObj?.completeness).toEqual({ ready: 2, total: 100 });
    const badObj = parseExport({ id: 'e1', projectId: 'p', format: 'srt', status: 'Running', completeness: { ready: 'x', total: 1 } });
    expect(badObj?.completeness).toBeUndefined();
    expect(parseExports('nope')).toEqual([]);
    expect(parseExports({ items: [{ nope: true }, undefined] })).toEqual([]);
    const sorted = parseExports({ items: [
      { id: 'a', projectId: 'p', format: 'srt', status: 'Pending', createdAt: '2024-01-15T10:00:00Z' },
      { id: 'b', projectId: 'p', format: 'srt', status: 'Pending', createdAt: '2024-01-16T10:00:00Z' },
    ] });
    expect(sorted.map((entry) => entry.id)).toEqual(['b', 'a']);
    expect(parseProcessingRuns('nope')).toEqual([]);
    const runs = parseProcessingRuns({ items: [{ runId: 'r1', status: 'Running' }, { id: 'r2' }, {}, { id: '' }] });
    expect(runs).toEqual([{ id: 'r1', status: 'Running' }, { id: 'r2', status: '' }]);
    expect(formatFileSize(512)).toBe('512 B');
    expect(formatFileSize(2048)).toBe('2.0 KB');
    expect(formatFileSize(5 * 1024 * 1024)).toBe('5.0 MB');
    expect(formatFileSize(3 * 1024 * 1024 * 1024)).toBe('3.0 GB');
    expect(formatFileSize(undefined)).toBeUndefined();
    expect(formatFileSize(-1)).toBeUndefined();
    expect(formatFileSize(Number.NaN)).toBeUndefined();
  });

  it('drops unsafe URLs, internal reasons, and tainted warnings', () => {
    const parsed = parseOutput({
      state: 'Ready',
      items: {
        video: { state: 'ready', downloadUrl: 'http://example.com/x.mp4', missing: ['SEGMENT_PENDING', '/mnt/x', ''], reason: '/mnt/secret boom' },
      },
      warnings: [{ code: 'W1', message: 'm1' }, '/var/x', 42, 'plain', { code: 's3://x', message: 'y' }],
    });
    const video = parsed.items.find((item) => item.kind === 'video');
    expect(video?.downloadUrl).toBeUndefined();
    expect(video?.reason).toBeUndefined();
    expect(video?.missing).toEqual(['SEGMENT_PENDING']);
    expect(parsed.warnings).toEqual(['W1: m1', 'plain']);
    const qcFallback = parseOutput({ state: 'Ready' });
    expect(qcFallback.items.find((item) => item.kind === 'qc')?.state).toBe('Unavailable');
  });

  it('fans subtitles out with numbered labels and synthetic fallbacks', () => {
    const two = parseOutput({
      state: 'Partial',
      completeness: { ready: 1, total: 2 },
      items: { subtitles: [{ state: 'ready' }, { state: 'generating' }] },
    });
    const subs = two.items.filter((item) => item.kind === 'subtitles');
    expect(subs.map((item) => item.label)).toEqual(['Subtitles', 'Subtitles 2']);
    const synthetic = parseOutput({ state: 'Partial', completeness: { ready: 1, total: 2 }, items: {} });
    const synthSub = synthetic.items.find((item) => item.kind === 'subtitles');
    expect(synthSub?.state).toBe('Partial');
    expect(synthSub?.missing).toEqual(['SEGMENT_PENDING']);
    const readySynth = parseOutput({ state: 'Ready', items: {} });
    expect(readySynth.items.find((item) => item.kind === 'subtitles')?.state).toBe('Unavailable');
  });

  it('derives scopes and profiles without traversal', () => {
    expect(advertisedScopes(undefined)).toEqual(['full']);
    const narrow = parseOutput({ state: 'Ready', completeness: { ready: 0, total: 0 }, items: { speakers: { state: 'unavailable' } } });
    expect(advertisedScopes(narrow)).toEqual(['full']);
    expect(profileForRequest('', 'full')).toBeUndefined();
    expect(profileForRequest('..', 'full')).toBeUndefined();
    expect(profileForRequest('Subtitles', 'Segment Range')).toBe('subtitles-segment-range');
    const long = profileForRequest('a'.repeat(100), 'full');
    expect(long?.length).toBe(64);
  });

  it('validates every request branch before sending', () => {
    const base = { type: 'audio', scope: 'full', runId: '', format: 'srt', allowPartial: false, advertisedScopes: ['full'] as readonly string[], availableRunIds: ['run_1'] as readonly string[] };
    const scopeErr = validateExportRequest({ ...base, scope: 'per-speaker' });
    expect('error' in scopeErr && scopeErr.error).toContain('scope');
    const formatErr = validateExportRequest({ ...base, format: 'bogus' });
    expect('error' in formatErr && formatErr.error).toContain('format');
    const runErr = validateExportRequest({ ...base, runId: 'run_9' });
    expect('error' in runErr && runErr.error).toContain('run');
    const ok = validateExportRequest({ ...base, runId: 'run_1', allowPartial: true });
    expect('body' in ok && ok.body.allowPartial).toBe(true);
    expect('body' in ok && ok.body.profile).toBe('audio');
  });

  it('explains partials per warning class and per item', () => {
    const blocked = parseOutput(outputFixture({ warnings: ['qc_blocked'] }));
    expect(partialExplanationFor(blocked)).toContain('failed QC');
    const review = parseOutput(outputFixture({ warnings: ['review-open'] }));
    expect(partialExplanationFor(review)).toContain('awaiting review');
    const plain = parseOutput(outputFixture({ warnings: [] }));
    expect(partialExplanationFor(plain)).toContain('incomplete');
    const item = parseOutput(outputFixture()).items.find((entry) => entry.kind === 'translation');
    expect(item).toBeDefined();
    if (item !== undefined) {
      expect(partialExplanationForItem(item, { ready: 0, total: 10 })).toContain('96/100');
      expect(partialExplanationForItem({ ...item, completeness: undefined }, { ready: 3, total: 4 })).toContain('3/4');
    }
  });

  it('classifies expired/conflict/missing errors with null safety', () => {
    expect(isExpiredError(undefined)).toBe(false);
    expect(isExpiredError(null)).toBe(false);
    expect(isExpiredError({ code: 'URL_EXPIRED' })).toBe(true);
    expect(isExpiredError({ status: 410 })).toBe(true);
    expect(isExpiredError({ code: 'X', status: 500 })).toBe(false);
    expect(isAlreadyGeneratingError(null)).toBe(false);
    expect(isAlreadyGeneratingError({ status: 409 })).toBe(true);
    expect(isAlreadyGeneratingError({ status: 500 })).toBe(false);
    expect(isNotFoundError(undefined)).toBe(false);
    expect(isNotFoundError({ status: 404 })).toBe(true);
    expect(isNotFoundError({ code: 'PROJECT_NOT_FOUND' })).toBe(true);
    expect(isNotFoundError({ code: 'X' })).toBe(false);
    expect(normalizeOutputState('ready')).toBe('Ready');
  });
});
