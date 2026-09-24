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
import { setSessionFile, useUploadStore } from '../uploadStore.js';
import type { PersistedUpload } from '../uploadStore.js';
import type { RejectionReason } from '../uploadStates.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

let receivedParts: number[];
let projectStatus: string;
let completeCode: string | null;
const puts: number[] = [];

function resetServer(): void {
  receivedParts = [];
  projectStatus = 'MediaReady';
  completeCode = null;
  puts.length = 0;
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : (input as Request).url;
  const method = (typeof input !== 'string' && !(input instanceof URL) ? (input as Request).method : undefined) ?? init?.method ?? 'GET';
  if (!url.includes('/api/v1/')) {
    const part = Number.parseInt(url.split('/').pop() ?? '0', 10);
    puts.push(part);
    await new Promise((resolve) => {
      setTimeout(resolve, 0);
    });
    if (!receivedParts.includes(part)) {
      receivedParts.push(part);
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
    if (completeCode !== null) {
      return jsonResponse(
        { error: { code: completeCode, message: 'Rejected by server.', correlationId: 'corr-9', details: {} } },
        400,
      );
    }
    return jsonResponse({ id: 'upl_1', status: 'Completed', receivedParts: [...receivedParts] });
  }
  if (url.includes('/abort') && method === 'POST') {
    return jsonResponse({ id: 'upl_1', status: 'Aborted', receivedParts: [] });
  }
  if (url.includes('/uploads/upl_1') && method === 'GET') {
    return jsonResponse({ id: 'upl_1', status: 'Completed', receivedParts: [...receivedParts] });
  }
  if (/\/api\/v1\/projects\/[^/]+$/.test(url) && method === 'GET') {
    return jsonResponse({ id: 'prj_1', name: 'Pilot', status: projectStatus, settingsVersion: 1 });
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

function seedSession(overrides: Partial<PersistedUpload> = {}): PersistedUpload {
  const session: PersistedUpload = {
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
  };
  useUploadStore.getState().upsertSession(session);
  return session;
}

beforeEach(() => {
  resetServer();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
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

describe('idle picker', () => {
  it('starts an upload from the file picker and reaches ready', async () => {
    authenticate();
    renderUploader();
    expect(await screen.findByTestId('upload-dropzone')).toBeDefined();
    fireEvent.change(screen.getByTestId('upload-file-input'), { target: { files: [mediaFile()] } });
    expect(await screen.findByTestId('upload-active')).toBeDefined();
    expect(await screen.findByTestId('upload-ready')).toBeDefined();
    expect(screen.getByTestId('upload-open-project').getAttribute('href')).toBe('/projects/prj_1');
  });

  it('starts an upload from drag-drop', async () => {
    authenticate();
    renderUploader();
    expect(await screen.findByTestId('upload-dropzone')).toBeDefined();
    fireEvent.drop(screen.getByTestId('upload-dropzone'), { dataTransfer: { files: [mediaFile()] } });
    expect(await screen.findByTestId('upload-ready')).toBeDefined();
  });

  it('rejects zero-byte files without any request', async () => {
    authenticate();
    renderUploader();
    expect(await screen.findByTestId('upload-dropzone')).toBeDefined();
    fireEvent.change(screen.getByTestId('upload-file-input'), { target: { files: [mediaFile(0)] } });
    expect(await screen.findByTestId('upload-local-error')).toBeDefined();
    expect(puts).toHaveLength(0);
  });
});

describe('pause and resume controls', () => {
  it('pauses in-flight parts and resumes to ready', async () => {
    authenticate();
    renderUploader();
    expect(await screen.findByTestId('upload-dropzone')).toBeDefined();
    fireEvent.change(screen.getByTestId('upload-file-input'), { target: { files: [mediaFile(25)] } });
    expect(await screen.findByTestId('upload-pause')).toBeDefined();
    fireEvent.click(screen.getByTestId('upload-pause'));
    expect(await screen.findByTestId('upload-resume-prompt')).toBeDefined();
    fireEvent.click(screen.getByTestId('upload-resume'));
    expect(await screen.findByTestId('upload-ready')).toBeDefined();
  });
});

describe('refresh recovery prompt', () => {
  it('shows the reattach prompt with intact part counts when the file is gone', async () => {
    authenticate();
    seedSession({ phase: 'paused', completedParts: [1], bytesUploaded: 4 });
    renderUploader();
    const prompt = await screen.findByTestId('upload-reattach-prompt');
    expect(prompt.textContent).toContain('1');
    expect(prompt.textContent).toContain('3');
    expect(screen.getByTestId('upload-resume')).toBeDefined();
    expect((screen.getByTestId('upload-resume') as HTMLButtonElement).disabled).toBe(true);
  });

  it('re-attaching the same file resumes without re-uploading completed parts', async () => {
    authenticate();
    receivedParts = [1];
    const seeded = seedSession({ phase: 'paused', completedParts: [1], bytesUploaded: 4, fingerprint: 'v1:9:video/mp4:x:y' });
    void seeded;
    renderUploader();
    expect(await screen.findByTestId('upload-reattach-prompt')).toBeDefined();
    const fp = (await import('../fingerprint.js')).fingerprintFile;
    const file = mediaFile();
    const digest = await fp(file);
    useUploadStore.getState().updateSession('prj_1', { fingerprint: digest });
    fireEvent.change(screen.getByTestId('upload-reattach-input'), { target: { files: [file] } });
    expect(await screen.findByTestId('upload-resume')).toBeDefined();
    fireEvent.click(screen.getByTestId('upload-resume'));
    await waitFor(() => {
      expect(useUploadStore.getState().sessions['prj_1']?.phase).toBe('ready');
    });
    expect(puts).not.toContain(1);
  });
});

describe('rejected guidance (R4)', () => {
  const cases: { reason: RejectionReason; testid: string }[] = [
    { reason: 'duplicate', testid: 'upload-rejected-duplicate' },
    { reason: 'unsupported', testid: 'upload-rejected-unsupported' },
    { reason: 'corrupt', testid: 'upload-rejected-corrupt' },
    { reason: 'expired', testid: 'upload-rejected-expired' },
    { reason: 'unknown', testid: 'upload-rejected-unknown' },
  ];

  for (const { reason, testid } of cases) {
    it(`renders guidance for ${reason}`, async () => {
      authenticate();
      seedSession({ phase: 'rejected', rejectionReason: reason, correlationId: 'corr-9' });
      renderUploader();
      const panel = await screen.findByTestId(testid);
      expect(panel.textContent ?? '').not.toBe('');
    });
  }

  it('duplicate offers the use-existing link; expired offers start-over', async () => {
    authenticate();
    seedSession({ phase: 'rejected', rejectionReason: 'duplicate' });
    renderUploader();
    expect(await screen.findByTestId('upload-use-existing')).toBeDefined();
    expect(screen.getByTestId('upload-use-existing').getAttribute('href')).toBe('/projects/prj_1');
    cleanup();

    seedSession({ phase: 'rejected', rejectionReason: 'expired' });
    renderUploader();
    expect(await screen.findByTestId('upload-start-over')).toBeDefined();
  });

  it('server rejection on complete maps to the duplicate panel', async () => {
    authenticate();
    completeCode = 'DUPLICATE_MEDIA';
    renderUploader();
    expect(await screen.findByTestId('upload-dropzone')).toBeDefined();
    fireEvent.change(screen.getByTestId('upload-file-input'), { target: { files: [mediaFile()] } });
    expect(await screen.findByTestId('upload-rejected-duplicate')).toBeDefined();
  });
});

describe('cancel confirmation', () => {
  it('clears the session behind confirmation', async () => {
    authenticate();
    const file = mediaFile();
    seedSession({ phase: 'uploading' });
    setSessionFile('prj_1', file);
    renderUploader();
    expect(await screen.findByTestId('upload-active')).toBeDefined();
    fireEvent.click(screen.getByTestId('upload-cancel'));
    const dialog = screen.getByRole('dialog');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Cancel upload' }));
    await waitFor(() => {
      expect(useUploadStore.getState().sessions['prj_1']).toBeUndefined();
    });
    expect(await screen.findByTestId('upload-dropzone')).toBeDefined();
  });
});

describe('server validation display', () => {
  it('distinguishes server phases from client phases', async () => {
    authenticate();
    projectStatus = 'Uploading';
    seedSession({ phase: 'validating', completedParts: [1, 2, 3], bytesUploaded: 9 });
    setSessionFile('prj_1', mediaFile());
    renderUploader();
    expect(await screen.findByTestId('upload-server-state')).toBeDefined();
    expect(screen.getByTestId('upload-step-validating')).toBeDefined();
    expect(screen.getByTestId('upload-step-client')).toBeDefined();
    projectStatus = 'MediaReady';
  });
});
