// Task 039B: upload-component state-matrix gap closure.
//
// Drives `MediaUploader` through the control handlers the flow specs leave
// cold (dropzone hover/drop, pause/resume/cancel dialog, retry-part wiring,
// start-over, replace/reattach inputs, server-step display, ready/reject
// toasts) plus direct `UploadProgress` matrices (error retry, missing
// handler, empty/zero-size parts). Seeded sessions keep each case hermetic;
// the mock server is the same synthetic bundle shape as the owning suite.
// Every failure asserts its recovery control per §11.6 with text signals.
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
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
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { MediaUploader } from '../MediaUploader.js';
import { UploadProgress } from '../UploadProgress.js';
import { setSessionFile, useUploadStore } from '../uploadStore.js';
import type { PersistedUpload } from '../uploadStore.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

let failNextPut = false;

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
  const method = (typeof input !== 'string' && !(input instanceof URL) ? (input as Request).method : undefined) ?? init?.method ?? 'GET';
  if (!url.includes('/api/v1/')) {
    if (failNextPut) {
      failNextPut = false;
      return new Response('bad', { status: 500 });
    }
    return new Response(null, { status: 200 });
  }
  const raw = init?.body;
  const parsed = typeof raw === 'string' && raw !== '' ? (JSON.parse(raw) as Record<string, unknown>) : {};
  if (url.endsWith('/uploads') && method === 'POST') {
    return jsonResponse({ id: 'upl_1', status: 'InProgress', receivedParts: [] }, 201);
  }
  if (url.includes('/parts') && method === 'POST') {
    const n = typeof parsed['partNumber'] === 'number' ? (parsed['partNumber'] as number) : 0;
    return jsonResponse({ partNumber: n, url: `https://parts.example/${n}` });
  }
  if (url.includes('/complete') && method === 'POST') {
    return jsonResponse({ id: 'upl_1', status: 'Completed', receivedParts: [1] });
  }
  if (url.includes('/abort') && method === 'POST') {
    return jsonResponse({ id: 'upl_1', status: 'Aborted', receivedParts: [] });
  }
  if (url.includes('/uploads/upl_1') && method === 'GET') {
    return jsonResponse({ id: 'upl_1', status: 'Completed', receivedParts: [1] });
  }
  if (/\/api\/v1\/projects\/[^/]+$/.test(url) && method === 'GET') {
    return jsonResponse({ id: 'prj_1', name: 'Pilot', status: 'MediaReady', settingsVersion: 1 });
  }
  return jsonResponse({});
}

function renderUploader(): void {
  const router = createMemoryRouter([{ path: '/projects/:id/media', element: <MediaUploader projectId="prj_1" /> }], {
    initialEntries: ['/projects/prj_1/media'],
    future: { ...ROUTER_FUTURE_FLAGS },
  });
  render(
    <LocaleProvider>
      <ToastProvider>
        <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
      </ToastProvider>
    </LocaleProvider>,
  );
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

function mediaFile(size = 9): File {
  const bytes = new Uint8Array(size);
  for (let i = 0; i < size; i += 1) {
    bytes[i] = i % 251;
  }
  return new File([bytes], 'clip.mp4', { type: 'video/mp4' });
}

function seedSession(overrides: Partial<PersistedUpload> = {}): void {
  useUploadStore.getState().upsertSession({
    projectId: 'prj_1',
    uploadId: 'upl_1',
    name: 'clip.mp4',
    size: 9,
    contentType: 'video/mp4',
    fingerprint: 'v1:x',
    language: 'es',
    partSize: 4,
    partCount: 3,
    completedParts: [],
    bytesUploaded: 0,
    phase: 'paused',
    autoPaused: false,
    idempotencyKey: 'key-1',
    updatedAt: '2024-01-15T12:00:00Z',
    ...overrides,
  });
}

beforeEach(() => {
  failNextPut = false;
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  authenticate();
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  window.localStorage.clear();
  useUploadStore.getState().resetUploadsForTests();
  vi.restoreAllMocks();
});

describe('dropzone matrix', () => {
  it('tracks drag hover with aria-busy and ignores empty drops', () => {
    renderUploader();
    const zone = screen.getByTestId('upload-dropzone');
    fireEvent.dragOver(zone);
    expect(zone.getAttribute('aria-busy')).toBe('true');
    fireEvent.dragLeave(zone);
    expect(zone.getAttribute('aria-busy')).toBe('false');
    fireEvent.drop(zone, { dataTransfer: { files: [] } });
    expect(useUploadStore.getState().sessions['prj_1']).toBeUndefined();
    expect(screen.getByTestId('upload-dropzone')).toBeDefined();
  });

  it('starts an upload from a drop and toasts on ready (recovery: none needed)', async () => {
    renderUploader();
    fireEvent.drop(screen.getByTestId('upload-dropzone'), { dataTransfer: { files: [mediaFile(5)] } });
    await waitFor(() => expect(screen.getByTestId('upload-ready')).toBeDefined(), { timeout: 5000 });
    expect(screen.getByTestId('upload-open-project').getAttribute('href')).toBe('/projects/prj_1');
  });
});

describe('active controls matrix', () => {
  it('pauses and resumes through the buttons (recovery: resume)', async () => {
    seedSession({ phase: 'uploading' });
    setSessionFile('prj_1', mediaFile());
    renderUploader();
    fireEvent.click(screen.getByTestId('upload-pause'));
    await waitFor(() => expect(screen.getByTestId('upload-resume-prompt')).toBeDefined());
    fireEvent.click(screen.getByTestId('upload-resume'));
    await waitFor(() => expect(screen.getByTestId('upload-ready')).toBeDefined(), { timeout: 8000 });
  });

  it('cancels behind confirmation and dismisses without clearing', async () => {
    seedSession({ phase: 'paused' });
    renderUploader();
    fireEvent.click(screen.getByTestId('upload-cancel'));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(useUploadStore.getState().sessions['prj_1']).toBeDefined();
    fireEvent.click(screen.getByTestId('upload-cancel'));
    const reopened = screen.getByRole('dialog');
    const actions = within(reopened).getAllByRole('button');
    const confirm = actions[actions.length - 1];
    expect(confirm?.textContent).not.toBe('Cancel');
    fireEvent.click(confirm!);
    await waitFor(() => expect(useUploadStore.getState().sessions['prj_1']).toBeUndefined());
    expect(screen.getByTestId('upload-dropzone')).toBeDefined();
  });

  it('retries a failed part from the progress list (recovery: retry-keeps-parts)', async () => {
    seedSession({ phase: 'paused', partCount: 3, completedParts: [] });
    setSessionFile('prj_1', mediaFile());
    failNextPut = true;
    renderUploader();
    fireEvent.click(screen.getByTestId('upload-resume'));
    await waitFor(() => expect(screen.getByTestId('upload-error')).toBeDefined(), { timeout: 8000 });
    const retry = await screen.findByTestId('upload-retry-part-2');
    expect(retry.textContent?.length).toBeGreaterThan(0);
    fireEvent.click(retry);
    await waitFor(() => expect(screen.getByTestId('upload-ready')).toBeDefined(), { timeout: 8000 });
  });
});

describe('rejected panels matrix', () => {
  it('offers start-over for expired uploads (recovery: start-over)', async () => {
    seedSession({ phase: 'rejected', rejectionReason: 'expired' });
    renderUploader();
    expect(screen.getByTestId('upload-rejected-expired')).toBeDefined();
    fireEvent.click(screen.getByTestId('upload-start-over'));
    await waitFor(() => expect(screen.getByTestId('upload-dropzone')).toBeDefined());
  });

  it('replaces the file for corrupt uploads (recovery: replace)', async () => {
    seedSession({ phase: 'rejected', rejectionReason: 'corrupt', correlationId: 'corr-9' });
    renderUploader();
    expect(screen.getByTestId('upload-rejected-corrupt').textContent).toContain('corr-9');
    fireEvent.change(screen.getByTestId('upload-replace-input'), { target: { files: [mediaFile(5)] } });
    await waitFor(() => expect(screen.getByTestId('upload-ready')).toBeDefined(), { timeout: 8000 });
  });
});

describe('reattach prompt matrix', () => {
  it('re-attaches the same file and resumes (recovery: reattach)', async () => {
    const file = mediaFile(9);
    seedSession({ phase: 'paused', completedParts: [] });
    renderUploader();
    expect(screen.getByTestId('upload-reattach-prompt')).toBeDefined();
    expect(screen.getByTestId('upload-resume').hasAttribute('disabled')).toBe(true);
    const { fingerprintFile } = await import('../fingerprint.js');
    const fingerprint = await fingerprintFile(file);
    useUploadStore.getState().updateSession('prj_1', { fingerprint });
    fireEvent.change(screen.getByTestId('upload-reattach-input'), { target: { files: [file] } });
    await waitFor(() => expect(screen.getByTestId('upload-resume').hasAttribute('disabled')).toBe(false));
    fireEvent.click(screen.getByTestId('upload-resume'));
    await waitFor(() => expect(screen.getByTestId('upload-ready')).toBeDefined(), { timeout: 8000 });
  });
});

describe('server-state steps matrix', () => {
  it('lists client then server steps during validation (text signals)', () => {
    seedSession({ phase: 'validating', completedParts: [1, 2, 3], bytesUploaded: 9 });
    renderUploader();
    expect(screen.getByTestId('upload-server-state')).toBeDefined();
    expect(screen.getByTestId('upload-step-client')).toBeDefined();
    expect(screen.getByTestId('upload-steps').textContent?.length).toBeGreaterThan(0);
  });
});

describe('UploadProgress direct matrix', () => {
  it('shows the error state with a working retry (recovery: retry-part)', () => {
    const onRetryPart = vi.fn();
    render(
      <UploadProgress
        parts={[
          { partNumber: 1, status: 'done', bytes: 4 },
          { partNumber: 2, status: 'error', bytes: 4 },
        ]}
        bytesUploaded={4}
        sizeBytes={8}
        onRetryPart={onRetryPart}
      />,
    );
    expect(screen.getByTestId('upload-part-2-status').textContent?.length).toBeGreaterThan(0);
    fireEvent.click(screen.getByTestId('upload-retry-part-2'));
    expect(onRetryPart).toHaveBeenCalledWith(2);
  });

  it('omits retry buttons without a handler and renders empty/zero-size parts', () => {
    render(<UploadProgress parts={[]} bytesUploaded={0} sizeBytes={0} />);
    expect(screen.getByTestId('upload-overall-label').textContent).toContain('0');
    expect(screen.queryByTestId('upload-retry-part-1')).toBeNull();
    cleanup();
    render(
      <UploadProgress parts={[{ partNumber: 1, status: 'pending', bytes: 0 }]} bytesUploaded={0} sizeBytes={0} />,
    );
    expect(screen.getByTestId('upload-part-1-bar').getAttribute('max')).toBe('1');
  });
});
