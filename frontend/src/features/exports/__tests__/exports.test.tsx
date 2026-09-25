import { readFileSync } from 'node:fs';
import { join } from 'node:path';
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
  EXPORT_FORMAT_ALLOWLIST,
  EXPORT_SCOPE_ALLOWLIST,
  EXPORT_TYPE_ALLOWLIST,
  advertisedScopes,
  containsInternalPath,
  isAllowlistedFormat,
  isSafeDisplayUrl,
  normalizeExportDisplayState,
  normalizeExportFormat,
  normalizeOutputState,
  parseExport,
  parseOutput,
  partialExplanationFor,
  profileForRequest,
  validateExportRequest,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-e33', details: { path: '/mnt/data/secret' } } },
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
      subtitles: [
        { state: 'ready', generationState: 'ready', downloadUrl: 'https://example.com/subs.srt', missing: [], completeness: null },
      ],
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

interface ExportsWorld {
  output: Record<string, unknown>;
  exports: Record<string, unknown>;
  runs: Record<string, unknown>;
  createBehavior: 'ok' | 'conflict';
  downloadBehavior: 'ok' | 'expired-once' | 'expired-twice' | 'not-found';
  createCalls: number;
  createBodies: unknown[];
  downloadCalls: number;
  outputCalls: number;
  exportsCalls: number;
  runsCalls: number;
}

function newWorld(overrides: Partial<ExportsWorld> = {}): ExportsWorld {
  return {
    output: outputFixture(),
    exports: exportsFixture(),
    runs: runsFixture(),
    createBehavior: 'ok',
    downloadBehavior: 'ok',
    createCalls: 0,
    createBodies: [],
    downloadCalls: 0,
    outputCalls: 0,
    exportsCalls: 0,
    runsCalls: 0,
    ...overrides,
  };
}

let world: ExportsWorld = newWorld();
let downloadAttempts = 0;

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
    return JSON.parse(init.body as string) as unknown;
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
    const body = world.createBodies[world.createBodies.length - 1] as Record<string, unknown>;
    const format = typeof body['format'] === 'string' ? (body['format'] as string) : 'srt';
    return jsonResponse(
      { id: 'exp_new_1', projectId: 'prj_1', format, status: 'Pending', isPartial: false, createdAt: '2024-01-16T13:00:00Z', completenessJson: null },
      202,
    );
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
    return jsonResponse(world.exports);
  }
  if (method === 'GET' && url.includes('/output/download')) {
    return jsonResponse({ downloadUrl: 'https://example.com/output.mp4', expiresAt: '2026-09-26T00:00:00Z' });
  }
  if (method === 'GET' && url.includes('/output') && !url.includes('/download')) {
    world.outputCalls += 1;
    return jsonResponse(world.output);
  }
  if (method === 'GET' && url.includes('/processing') && !url.includes('/processing/')) {
    world.runsCalls += 1;
    return jsonResponse(world.runs);
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit', 'export.create', 'processing.retry']);
}

function renderWithProviders(node: React.ReactNode, initialEntry = '/projects/prj_1/exports'): void {
  const router = createMemoryRouter([{ path: '/projects/:id/exports', element: node }], {
    initialEntries: [initialEntry],
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
  world = newWorld();
  downloadAttempts = 0;
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

describe('pure export helpers', () => {
  it('normalizes all five output states without dropping unknown values', () => {
    expect(normalizeOutputState('Ready')).toBe('Ready');
    expect(normalizeOutputState('generating')).toBe('Generating');
    expect(normalizeOutputState('FAILED')).toBe('Failed');
    expect(normalizeOutputState('partial')).toBe('Partial');
    expect(normalizeOutputState('Unavailable')).toBe('Unavailable');
    expect(normalizeOutputState('nope')).toBe('Unavailable');
  });

  it('maps export job statuses to queued/generating/ready/failed', () => {
    expect(normalizeExportDisplayState('Pending')).toBe('queued');
    expect(normalizeExportDisplayState('Running')).toBe('generating');
    expect(normalizeExportDisplayState('Completed')).toBe('ready');
    expect(normalizeExportDisplayState('Failed')).toBe('failed');
    expect(normalizeExportDisplayState('Cancelled')).toBe('failed');
  });

  it('accepts kebab wire formats and rejects stale bundle binaries', () => {
    expect(normalizeExportFormat('srt')).toBe('srt');
    expect(normalizeExportFormat('Vtt')).toBe('webvtt');
    expect(normalizeExportFormat('json-timeline')).toBe('json-timeline');
    expect(normalizeExportFormat('nope')).toBeUndefined();
    expect(isAllowlistedFormat('srt')).toBe(true);
    expect(isAllowlistedFormat('webvtt')).toBe(true);
    expect(isAllowlistedFormat('mp4')).toBe(false);
  });

  it('derives advertised scopes and kebab profiles without traversal', () => {
    const parsed = parseOutput(outputFixture());
    const scopes = advertisedScopes(parsed);
    expect(scopes).toContain('full');
    expect(scopes).toContain('segment-range');
    expect(scopes).toContain('per-speaker');
    expect(profileForRequest('subtitles', 'full')).toBe('subtitles');
    expect(profileForRequest('subtitles', 'segment-range')).toBe('subtitles-segment-range');
    const validation = validateExportRequest({
      type: EXPORT_TYPE_ALLOWLIST[0] ?? '',
      scope: 'full',
      runId: '',
      format: EXPORT_FORMAT_ALLOWLIST[0] ?? '',
      allowPartial: false,
      advertisedScopes: scopes,
      availableRunIds: ['run_1'],
    });
    expect('body' in validation).toBe(true);
  });

  it('drops internal paths and keeps only safe https URLs', () => {
    expect(containsInternalPath('s3://bucket/key')).toBe(true);
    expect(containsInternalPath('/mnt/data/file')).toBe(true);
    expect(containsInternalPath('clean')).toBe(false);
    expect(isSafeDisplayUrl('https://example.com/file.mp4')).toBe(true);
    expect(isSafeDisplayUrl('s3://bucket/key')).toBe(false);
    expect(isSafeDisplayUrl('https://example.com/file?access_token=abc')).toBe(false);
    const tainted = parseOutput({
      state: 'Ready',
      completeness: { ready: 1, total: 1 },
      items: {
        video: { state: 'ready', downloadUrl: 's3://bucket/key', missing: [] },
      },
      warnings: ['/mnt/data/file', 'review-open:1'],
    });
    expect(tainted.items.find((item) => item.kind === 'video')?.downloadUrl).toBeUndefined();
    expect(tainted.warnings.join(' ')).not.toContain('/mnt/');
  });

  it('quantifies partial availability with a quality explanation', () => {
    const parsed = parseOutput(outputFixture());
    const text = partialExplanationFor(parsed);
    expect(text).toContain('96/100');
    expect(text).toContain('see Quality');
    const failed = parseExport({ id: 'exp_1', format: 'srt', status: 'Failed', reason: 'Render failed' });
    expect(failed?.failureReason).toBe('Render failed');
  });
});

describe('outputs five states', () => {
  it('renders ready/generating/failed/partial/unavailable distinctly with reasons', async () => {
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    expect(await screen.findByTestId('outputs-workspace')).toBeDefined();
    expect(await screen.findByTestId('exports-list')).toBeDefined();
    expect(screen.getByTestId('output-item-video').getAttribute('data-state')).toBe('Ready');
    expect(screen.getByTestId('output-item-audio').getAttribute('data-state')).toBe('Generating');
    expect(screen.getByTestId('output-item-transcript').getAttribute('data-state')).toBe('Failed');
    expect(screen.getByTestId('output-item-translation').getAttribute('data-state')).toBe('Partial');
    expect(screen.getByTestId('output-item-timeline').getAttribute('data-state')).toBe('Unavailable');
    expect(screen.getByTestId('output-missing-timeline').textContent).toContain('NO_RUNS_YET');
    expect(screen.getByTestId('outputs-workspace-key').textContent).toContain('outputs');
    expect(screen.getByTestId('exports-list-key').textContent).toContain('exports');
  });

  it('quantifies partial states with an explanation plus a quality deep link', async () => {
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('outputs-workspace');
    const explanation = await screen.findByTestId('outputs-partial-explanation');
    expect(explanation.textContent).toContain('96/100');
    expect(explanation.textContent).toContain('see Quality');
    expect(screen.getByTestId('outputs-partial-quality-link').getAttribute('href')).toContain('/projects/prj_1/quality');
    expect(screen.getByTestId('output-partial-explanation-translation').textContent).toContain('96/100');
    expect(screen.getByTestId('output-quality-link-translation').getAttribute('href')).toContain('/projects/prj_1/quality');
  });

  it('shows unavailable reasons without bare disabled buttons', async () => {
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    expect(await screen.findByTestId('output-item-timeline')).toBeDefined();
    expect(screen.getByTestId('output-unavailable-timeline').textContent).toContain('Unavailable');
    const workspace = screen.getByTestId('outputs-workspace');
    const disabledButtons = workspace.querySelectorAll('button[disabled]');
    for (const button of Array.from(disabledButtons)) {
      expect(button.textContent).not.toBe('');
    }
  });
});

describe('export dialog allowlist', () => {
  it('offers only backend-allowlisted types/scopes/formats', async () => {
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('outputs-workspace');
    fireEvent.click(screen.getByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    const typeOptions = screen.getByTestId('export-type').querySelectorAll('option');
    expect(typeOptions.length).toBe(EXPORT_TYPE_ALLOWLIST.length);
    for (const option of Array.from(typeOptions)) {
      expect(EXPORT_TYPE_ALLOWLIST).toContain((option as HTMLOptionElement).value);
    }
    const formatOptions = screen.getByTestId('export-format').querySelectorAll('option');
    expect(formatOptions.length).toBe(EXPORT_FORMAT_ALLOWLIST.length);
    for (const option of Array.from(formatOptions)) {
      expect(EXPORT_FORMAT_ALLOWLIST).toContain((option as HTMLOptionElement).value);
    }
    const scopeOptions = screen.getByTestId('export-scope').querySelectorAll('option');
    for (const option of Array.from(scopeOptions)) {
      expect(EXPORT_SCOPE_ALLOWLIST).toContain((option as HTMLOptionElement).value);
    }
    const runOptions = screen.getByTestId('export-run').querySelectorAll('option');
    expect(runOptions.length).toBeGreaterThanOrEqual(3);
  });

  it('contains no hardcoded format options in the dialog module', () => {
    const source = readFileSync(join(process.cwd(), 'src', 'features', 'exports', 'ExportCard.tsx'), 'utf8');
    for (const format of EXPORT_FORMAT_ALLOWLIST) {
      expect(source).not.toContain(`"${format}"`);
      expect(source).not.toContain(`'${format}'`);
      expect(source).not.toContain(`value="${format}"`);
    }
  });

  it('shows an EmptyState with no submit when the format allowlist is empty', async () => {
    renderWithProviders(<ExportCard projectId="prj_1" output={parseOutput(outputFixture())} availableFormats={[]} />);
    fireEvent.click(screen.getByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-empty-formats')).toBeDefined();
    expect(screen.queryByTestId('export-submit')).toBeNull();
  });

  it('submits allowlisted parameters and invalidates the export list', async () => {
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('outputs-workspace');
    fireEvent.click(screen.getByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    const before = world.createCalls;
    fireEvent.click(screen.getByTestId('export-submit'));
    await waitFor(() => {
      expect(world.createCalls).toBeGreaterThan(before);
    });
    const body = world.createBodies[world.createBodies.length - 1] as Record<string, unknown>;
    expect(EXPORT_FORMAT_ALLOWLIST).toContain(body['format'] as string);
  });

  it('collapses a 409 conflict to the existing row with a toast and no duplicate', async () => {
    world.createBehavior = 'conflict';
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('outputs-workspace');
    fireEvent.click(screen.getByTestId('export-open-dialog'));
    expect(await screen.findByTestId('export-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('export-submit'));
    await waitFor(() => {
      expect(world.createCalls).toBe(1);
    });
    await waitFor(() => {
      expect(document.body.textContent).toContain('already generating');
    });
    expect(world.createCalls).toBe(1);
  });
});

describe('export downloads', () => {
  it('fetches the signed URL at click time, never before', async () => {
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('exports-list');
    expect(world.downloadCalls).toBe(0);
    const link = screen.getByTestId('export-download-exp_ready_1');
    expect(link.tagName.toLowerCase()).toBe('a');
    expect(link.hasAttribute('download')).toBe(true);
    fireEvent.click(link);
    await waitFor(() => {
      expect(world.downloadCalls).toBe(1);
    });
  });

  it('refetches once after expiry and resumes the download', async () => {
    world.downloadBehavior = 'expired-once';
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('exports-list');
    fireEvent.click(screen.getByTestId('export-download-exp_ready_1'));
    await waitFor(() => {
      expect(world.downloadCalls).toBe(2);
    });
    expect(screen.queryByTestId('export-download-error-exp_ready_1')).toBeNull();
  });

  it('shows a retryable error after double expiry', async () => {
    world.downloadBehavior = 'expired-twice';
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('exports-list');
    fireEvent.click(screen.getByTestId('export-download-exp_ready_1'));
    expect(await screen.findByTestId('export-download-error-exp_ready_1')).toBeDefined();
    expect(screen.getByTestId('export-download-retry-exp_ready_1')).toBeDefined();
  });

  it('removes deleted rows on refetch with a toast', async () => {
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    expect(await screen.findByTestId('export-row-exp_ready_1')).toBeDefined();
    world.exports = { items: [], page: 1, pageSize: 100, total: 0, hasMore: false };
    await queryClient.invalidateQueries();
    expect(await screen.findByTestId('exports-empty')).toBeDefined();
    await waitFor(() => {
      expect(screen.queryByTestId('export-row-exp_ready_1')).toBeNull();
    });
  });
});

describe('no internal paths', () => {
  it('never renders server paths, buckets, or storage URIs', async () => {
    world.output = outputFixture({
      items: {
        ...(outputFixture()['items'] as Record<string, unknown>),
        video: { state: 'ready', downloadUrl: 's3://bucket/key', missing: [] },
      },
      warnings: ['/mnt/data/file', 'review-open:1'],
    });
    renderWithProviders(<OutputsPage projectId="prj_1" />);
    await screen.findByTestId('outputs-workspace');
    await screen.findByTestId('exports-list');
    const text = document.body.textContent ?? '';
    expect(text).not.toContain('s3://');
    expect(text).not.toContain('/mnt/');
    expect(text).not.toContain('/var/');
    expect(text).not.toContain('bucket');
    expect(text).not.toContain('C:\\');
    expect(screen.queryByTestId('output-download-video')).toBeNull();
  });
});
