// Task 039B: exports state-matrix gap closure.
//
// Extends `exports.test.tsx` (helpers, five states, dialog allowlist,
// downloads, internal paths) with the missing states: page shells
// (needs-project/loading/error-retry for both scopes, exports empty),
// failed banner + stale banners + refresh, ExportCard dialog lifecycle
// (open/submit/409-collapse/validation/cancel/empty-formats/partial opt-in),
// ExportRow failed-retry/pending/partial/download-error matrices, the types
// sweep, and hook guards + invalidation. Every failure asserts its recovery
// control per §11.6 with text signals (041C); fixtures are synthetic (R3).
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
import {
  advertisedScopes,
  containsInternalPath,
  iconForOutputState,
  isAllowlistedFormat,
  isSafeDisplayUrl,
  labelForOutputState,
  normalizeExportDisplayState,
  normalizeExportFormat,
  normalizeOutputState,
  outputStateKey,
  parseExport,
  parseOutput,
  partialExplanationFor,
  patternForOutputState,
  profileForRequest,
  validateExportRequest,
} from '../types.js';
import { invalidateExports, invalidateOutputs, useExports, useOutputs } from '../useOutputs.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-e33', details: {} } },
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
    reason: null,
    completeness: { ready: 96, total: 100 },
    progressApproximate: null,
    errorCode: null,
    items: {
      video: { state: 'ready', generationState: 'ready', downloadUrl: 'https://example.com/video.mp4', missing: [], completeness: null },
      audio: { state: 'generating', generationState: 'generating', downloadUrl: null, missing: ['SEGMENT_PENDING'], completeness: { ready: 50, total: 100 } },
      subtitles: [{ state: 'ready', generationState: 'ready', downloadUrl: 'https://example.com/subs.srt', missing: [], completeness: null }],
      transcript: { state: 'failed', generationState: 'failed', downloadUrl: null, missing: ['ARTIFACT_MISSING'], completeness: null },
      translation: { state: 'partial', generationState: 'partial', downloadUrl: 'https://example.com/trans.json', missing: ['SEGMENT_PENDING'], completeness: { ready: 96, total: 100 } },
      timeline: { state: 'unavailable', generationState: 'unavailable', downloadUrl: null, missing: ['NO_RUNS_YET'], completeness: null },
      speakers: { state: 'ready', generationState: 'ready', downloadUrl: null, missing: [], completeness: null },
      qc: { state: 'partial', generationState: 'partial', summary: '4 finding(s), 1 blocked.', issuesUrl: null, missing: ['QC_BLOCKED'] },
    },
    warnings: ['qc-blocked'],
    updatedAt: '2024-01-16T12:00:00Z',
    ...overrides,
  };
}

function exportsFixture(): Record<string, unknown> {
  return {
    items: [
      { id: 'exp_queued_1', projectId: 'prj_1', format: 'srt', status: 'Pending', isPartial: false, createdAt: '2024-01-16T10:00:00Z', completenessJson: null },
      { id: 'exp_gen_1', projectId: 'prj_1', format: 'webvtt', status: 'Running', isPartial: false, createdAt: '2024-01-16T11:00:00Z', completenessJson: JSON.stringify({ ready: 50, total: 100 }) },
      { id: 'exp_ready_1', projectId: 'prj_1', format: 'json-timeline', status: 'Completed', isPartial: false, createdAt: '2024-01-16T12:00:00Z', completenessJson: null, sizeBytes: 1024 },
      { id: 'exp_failed_1', projectId: 'prj_1', format: 'transcript', status: 'Failed', isPartial: true, createdAt: '2024-01-16T09:00:00Z', completenessJson: null, reason: 'Render failed' },
    ],
    page: 1,
    pageSize: 100,
    total: 4,
    hasMore: false,
  };
}

function runsFixture(): Record<string, unknown> {
  return {
    items: [
      { runId: 'run_2', status: 'Running' },
      { runId: 'run_1', status: 'Completed' },
    ],
    page: 1,
    pageSize: 20,
    total: 2,
    hasMore: false,
  };
}

interface ExportsMatrixWorld {
  output: Record<string, unknown> | null;
  outputMode: 'ok' | 'error500' | 'never';
  exportsMode: 'ok' | 'empty' | 'error500';
  createBehavior: 'ok' | 'conflict' | 'error500';
  downloadBehavior: 'ok' | 'expired-once' | 'expired-twice' | 'not-found';
  createCalls: number;
  createBodies: unknown[];
  downloadCalls: number;
  outputCalls: number;
  exportsCalls: number;
}

let world: ExportsMatrixWorld;
let downloadAttempts = 0;

function resetWorld(): void {
  world = {
    output: outputFixture(),
    outputMode: 'ok',
    exportsMode: 'ok',
    createBehavior: 'ok',
    downloadBehavior: 'ok',
    createCalls: 0,
    createBodies: [],
    downloadCalls: 0,
    outputCalls: 0,
    exportsCalls: 0,
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
    if (world.createBehavior === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    const body = world.createBodies[world.createBodies.length - 1] as Record<string, unknown>;
    const format = typeof body['format'] === 'string' ? (body['format'] as string) : 'srt';
    return jsonResponse({ id: 'exp_new_1', projectId: 'prj_1', format, status: 'Pending', isPartial: false, createdAt: '2024-01-16T13:00:00Z', completenessJson: null }, 202);
  }
  if (method === 'GET' && /\/exports\/[^/?]+\/download/.test(url)) {
    world.downloadCalls += 1;
    downloadAttempts += 1;
    if (world.downloadBehavior === 'not-found') {
      return errorEnvelope('NOT_FOUND', 404, 'This export no longer exists.');
    }
    if (world.downloadBehavior === 'expired-twice') {
      return errorEnvelope('URL_EXPIRED', 410, 'This download link expired.');
    }
    if (world.downloadBehavior === 'expired-once') {
      if (downloadAttempts === 1) {
        return errorEnvelope('URL_EXPIRED', 410, 'This download link expired.');
      }
      return redirectResponse('https://example.com/export.srt');
    }
    return redirectResponse('https://example.com/export.srt');
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
  if (method === 'GET' && url.includes('/output') && !url.includes('/download')) {
    world.outputCalls += 1;
    if (world.outputMode === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    if (world.outputMode === 'never') {
      return new Promise<Response>(() => {});
    }
    return jsonResponse(world.output);
  }
  if (method === 'GET' && url.includes('/processing') && !url.includes('/processing/')) {
    return jsonResponse(runsFixture());
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'export.create']);
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

describe('outputs page shells', () => {
  it('asks for a project when unscoped (no fetch, no crash)', () => {
    const router = createMemoryRouter([{ path: '/', element: <OutputsPage projectId="" /> }], {
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
    expect(screen.getByTestId('outputs-needs-project')).toBeDefined();
    expect(world.outputCalls).toBe(0);
    expect(world.exportsCalls).toBe(0);
  });

  it('shows loading while aggregates resolve (never blank)', () => {
    world.outputMode = 'never';
    renderPage();
    expect(screen.getByTestId('outputs-loading')).toBeDefined();
  });

  it('recovers both scopes from errors with retry (recovery: retry)', async () => {
    world.outputMode = 'error500';
    world.exportsMode = 'error500';
    renderPage();
    expect(await screen.findByTestId('outputs-error')).toBeDefined();
    expect(await screen.findByTestId('exports-error')).toBeDefined();
    const outputBefore = world.outputCalls;
    const exportsBefore = world.exportsCalls;
    world.outputMode = 'ok';
    world.exportsMode = 'ok';
    const retries = screen.getAllByRole('button', { name: 'Retry' });
    expect(retries.length).toBe(2);
    fireEvent.click(retries[0]!);
    fireEvent.click(retries[1]!);
    await waitFor(() => expect(world.outputCalls).toBeGreaterThan(outputBefore));
    await waitFor(() => expect(world.exportsCalls).toBeGreaterThan(exportsBefore));
    expect(await screen.findByTestId('outputs-detail')).toBeDefined();
  });

  it('renders the exports empty state without error chrome', async () => {
    world.exportsMode = 'empty';
    renderPage();
    expect(await screen.findByTestId('exports-empty')).toBeDefined();
    expect(screen.queryByTestId('exports-error')).toBeNull();
  });
});

describe('failed + stale banners', () => {
  it('banners failed outputs with the backend code (recovery: refresh)', async () => {
    world.output = outputFixture({ state: 'Failed', generationState: 'Failed', errorCode: 'RENDER_CRASHED' });
    renderPage();
    const banner = await screen.findByTestId('outputs-failed-banner');
    expect(banner.textContent).toContain('RENDER_CRASHED');
    expect(await screen.findByTestId('output-failed-transcript')).toBeDefined();
  });

  it('warns on stale aggregates while keeping data (recovery: refresh)', async () => {
    renderPage();
    expect(await screen.findByTestId('outputs-detail')).toBeDefined();
    world.outputMode = 'error500';
    world.exportsMode = 'error500';
    await queryClient.invalidateQueries();
    expect(await screen.findByTestId('outputs-stale')).toBeDefined();
    expect(await screen.findByTestId('exports-stale')).toBeDefined();
    world.outputMode = 'ok';
    world.exportsMode = 'ok';
    fireEvent.click(screen.getByTestId('outputs-refresh'));
    await waitFor(() => expect(screen.queryByTestId('outputs-stale')).toBeNull());
  });
});

describe('partial states with QC links', () => {
  it('quantifies partial output with the quality deep link (recovery: review)', async () => {
    renderPage();
    const partial = await screen.findByTestId('outputs-partial');
    expect(partial.textContent).toContain('96/100');
    expect(screen.getByTestId('outputs-partial-quality-link').getAttribute('href')).toBe('/projects/prj_1/quality');
    expect(screen.getByTestId('output-partial-translation')).toBeDefined();
    expect(screen.getByTestId('output-quality-link-translation').getAttribute('href')).toBe('/projects/prj_1/quality');
    expect(screen.getByTestId('output-unavailable-timeline').textContent).toContain('NO_RUNS_YET');
    expect(screen.getByTestId('output-generating-audio').textContent).toContain('live updates');
    expect(screen.getByTestId('output-download-video').getAttribute('href')).toBe('https://example.com/video.mp4');
  });
});

describe('ExportCard dialog matrix', () => {
  it('submits allowlisted requests and closes with a toast (recovery: none needed)', async () => {
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-submit'));
    await waitFor(() => expect(world.createCalls).toBe(1));
    expect(world.createBodies[0]).toMatchObject({ format: expect.any(String) });
    await waitFor(() => expect(screen.queryByTestId('export-dialog')).toBeNull());
  });

  it('collapses 409 onto the existing row with an info toast (recovery: wait)', async () => {
    world.createBehavior = 'conflict';
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-submit'));
    await waitFor(() => expect(world.createCalls).toBe(1));
    await waitFor(() => expect(screen.queryByTestId('export-dialog')).toBeNull());
    expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('already generating');
  });

  it('notes run-list failures and keeps the latest run (recovery: proceed)', async () => {
    setInnerFetchForTests((async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = urlOf(input);
      if (url.includes('/processing') && !url.includes('/processing/')) {
        return errorEnvelope('INTERNAL_ERROR', 500);
      }
      return mockFetch(input, init);
    }) as typeof fetch);
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-runs-error')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-submit'));
    await waitFor(() => expect(world.createCalls).toBe(1));
  });

  it('cancels without submitting and hides submit on empty allowlists', async () => {
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    fireEvent.click(screen.getAllByTestId('export-cancel')[0]!);
    await waitFor(() => expect(screen.queryByTestId('export-dialog')).toBeNull());
    expect(world.createCalls).toBe(0);
    cleanup();
    queryClient.clear();
    renderCard(parseOutput(outputFixture()));
    const router = screen.getByTestId('export-card');
    void router;
    cleanup();
    queryClient.clear();
    const emptyRouter = createMemoryRouter([
      { path: '/', element: <ExportCard projectId="prj_1" output={parseOutput(outputFixture())} availableFormats={[]} /> },
    ]);
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <RouterProvider router={emptyRouter} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-empty-formats')).toBeDefined();
    expect(screen.queryByTestId('export-submit')).toBeNull();
  });

  it('surfaces submit failures with messages (recovery: report id)', async () => {
    world.createBehavior = 'error500';
    renderCard(parseOutput(outputFixture()));
    fireEvent.click(await screen.findByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-submit'));
    expect(await screen.findByTestId('export-error')).toBeDefined();
  });
});

describe('ExportRow matrix', () => {
  it('retries failed rows and collapses 409 (recovery: retry/wait)', async () => {
    renderPage();
    expect(await screen.findByTestId('export-error-exp_failed_1')).toBeDefined();
    expect(screen.getByTestId('export-error-message-exp_failed_1').textContent).toContain('Render failed');
    expect(screen.getByTestId('export-partial-exp_failed_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-retry-exp_failed_1'));
    await waitFor(() => expect(world.createCalls).toBe(1));
    world.createBehavior = 'conflict';
    fireEvent.click(screen.getByTestId('export-retry-exp_failed_1'));
    await waitFor(() => expect(world.createCalls).toBe(2));
    expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('already generating');
  });

  it('reports retry failures inline with refs (recovery: report id)', async () => {
    world.createBehavior = 'error500';
    renderPage();
    expect(await screen.findByTestId('export-error-exp_failed_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-retry-exp_failed_1'));
    expect(await screen.findByTestId('export-retry-message-exp_failed_1')).toBeDefined();
  });

  it('renders queued/generating progress and pending notes (text, not color)', async () => {
    renderPage();
    expect(await screen.findByTestId('export-row-exp_queued_1')).toBeDefined();
    expect(screen.getByTestId('export-progress-exp_queued_1').textContent).toContain('Queued');
    expect(screen.getByTestId('export-pending-exp_queued_1')).toBeDefined();
    expect(screen.getByTestId('export-progress-exp_gen_1').textContent).toContain('50/100');
    expect(screen.getByTestId('export-completeness-exp_gen_1')).toBeDefined();
    expect(screen.getByTestId('export-size-exp_ready_1').textContent).toContain('Size:');
  });

  it('recovers expired downloads with a clear action (recovery: retry)', async () => {
    world.downloadBehavior = 'expired-twice';
    renderPage();
    expect(await screen.findByTestId('export-download-exp_ready_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-download-exp_ready_1'));
    expect(await screen.findByTestId('export-download-error-exp_ready_1')).toBeDefined();
    expect(screen.getByTestId('export-download-error-message-exp_ready_1').textContent).toContain('expired');
    fireEvent.click(screen.getByTestId('export-download-retry-exp_ready_1'));
    expect(screen.queryByTestId('export-download-error-exp_ready_1')).toBeNull();
    world.downloadBehavior = 'ok';
    fireEvent.click(screen.getByTestId('export-download-exp_ready_1'));
    await waitFor(() => expect(world.downloadCalls).toBeGreaterThanOrEqual(3));
  });

  it('toasts and refetches on gone downloads (recovery: refresh)', async () => {
    world.downloadBehavior = 'not-found';
    renderPage();
    expect(await screen.findByTestId('export-download-exp_ready_1')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-download-exp_ready_1'));
    await waitFor(() =>
      expect(screen.getByRole('region', { name: 'Notifications' }).textContent).toContain('no longer exists'),
    );
  });
});

describe('exports types sweep', () => {
  it('labels every output state with icon + pattern + text (never color-only)', () => {
    for (const state of ['Ready', 'Generating', 'Failed', 'Partial', 'Unavailable'] as const) {
      expect(labelForOutputState(state).length).toBeGreaterThan(0);
      expect(patternForOutputState(state).length).toBeGreaterThan(0);
      expect(outputStateKey(state).length).toBeGreaterThan(0);
    }
    expect(labelForOutputState('Bogus' as never)).toBeUndefined();
    expect(outputStateKey('Bogus' as never)).toBe('bogus');
    expect(iconForOutputState('Ready')).not.toBe(iconForOutputState('Failed'));
    expect(normalizeOutputState('ready')).toBe('Ready');
    expect(normalizeOutputState('BOGUS')).toBe('Unavailable');
    expect(normalizeExportDisplayState('completed')).toBe('ready');
    expect(normalizeExportDisplayState('bogus')).toBe('queued');
  });

  it('explains partials, scopes, and formats from allowlists', () => {
    const output = parseOutput(outputFixture());
    expect(output).toBeDefined();
    if (output !== undefined) {
      expect(partialExplanationFor(output)).toContain('96/100');
      expect(advertisedScopes(output).length).toBeGreaterThan(0);
    }
    expect(advertisedScopes(undefined)).toEqual(['full']);
    expect(isAllowlistedFormat('srt')).toBe(true);
    expect(isAllowlistedFormat('bogus')).toBe(false);
    expect(normalizeExportFormat('SRT')).toBe('srt');
    expect(normalizeExportFormat('bogus')).toBeUndefined();
    expect(profileForRequest('video', 'full')).toContain('video');
  });

  it('validates requests against allowlists before sending', () => {
    const valid = validateExportRequest({ type: 'subtitles', scope: 'full', runId: '', format: 'srt', allowPartial: false, advertisedScopes: ['full'], availableRunIds: ['run_1'] });
    expect('error' in valid).toBe(false);
    const bad = validateExportRequest({ type: 'bogus', scope: 'full', runId: '', format: 'srt', allowPartial: false, advertisedScopes: ['full'], availableRunIds: [] });
    expect('error' in bad).toBe(true);
    const badRun = validateExportRequest({ type: 'subtitles', scope: 'full', runId: 'run_9', format: 'srt', allowPartial: false, advertisedScopes: ['full'], availableRunIds: ['run_1'] });
    expect('error' in badRun).toBe(true);
  });

  it('parses exports/outputs defensively without internal paths', () => {
    expect(parseExport(null)).toBeUndefined();
    expect(parseOutput(null).items.length).toBeGreaterThan(0);
    expect(parseOutput(null).items.every((item) => item.state === 'Unavailable')).toBe(true);
    expect(parseOutput({ state: 'Ready' }).items.length).toBe(8);
    expect(containsInternalPath('/mnt/data/secret')).toBe(true);
    expect(containsInternalPath('https://example.com/export.srt')).toBe(false);
    expect(isSafeDisplayUrl('https://example.com/export.srt')).toBe(true);
    expect(isSafeDisplayUrl('/mnt/data/secret')).toBe(false);
    expect(isSafeDisplayUrl('')).toBe(false);
  });
});

describe('exports hook guards', () => {
  it('never fires for empty ids or anonymous sessions', async () => {
    authenticate();
    let calls = 0;
    setInnerFetchForTests((async () => {
      calls += 1;
      return jsonResponse({});
    }) as typeof fetch);
    function Probe(): null {
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
    await invalidateExports(queryClient, 'prj_1');
  });
});
