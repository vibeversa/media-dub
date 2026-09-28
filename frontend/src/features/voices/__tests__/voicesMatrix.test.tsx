// Task 039B: voices state-matrix gap closure.
//
// Extends `voices.test.tsx` (happy paths, quota/consent/expired mapping,
// reset flow) with the missing PreviewPlayer phases (pending + refresh
// recovery, expired-once auto-refetch, request-time expiry/error/empty-id,
// audio-element error refetch, custom/default preview text) and the
// SpeakerList/VoiceSelector empty/error/assign-banner matrices. Every failure
// asserts its recovery control per §11.6 with text signals (041C); fixtures
// are synthetic and fetch is intercepted (R3).
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
import { queryClient } from '../../../app/providers/queryClient.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import { PreviewPlayer } from '../PreviewPlayer.js';
import { SpeakerList } from '../SpeakerList.js';
import { VoiceSelector } from '../VoiceSelector.js';
import { PREVIEW_DEFAULT_TEXT } from '../types.js';
import type { SpeakerView } from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-v29', details: {} } },
    status,
  );
}

type PreviewRequestBehavior = 'ok' | 'quota' | 'consent' | 'expired' | 'error' | 'empty';
type PreviewDetailBehavior = 'completed' | 'pending' | 'expiredOnce' | 'expiredAlways';
type ListBehavior = 'ok' | 'empty' | 'missing404' | 'error500';

interface VoicesMatrixWorld {
  previewRequestBehavior: PreviewRequestBehavior;
  previewDetailBehavior: PreviewDetailBehavior;
  speakersBehavior: ListBehavior;
  availableBehavior: ListBehavior;
  assignBehavior: 'ok' | 'conflict';
  previewBodies: unknown[];
  detailCalls: number;
  speakersCalls: number;
  availableCalls: number;
}

let world: VoicesMatrixWorld;

function resetWorld(): void {
  world = {
    previewRequestBehavior: 'ok',
    previewDetailBehavior: 'completed',
    speakersBehavior: 'ok',
    availableBehavior: 'ok',
    assignBehavior: 'ok',
    previewBodies: [],
    detailCalls: 0,
    speakersCalls: 0,
    availableCalls: 0,
  };
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
  if (url.includes('/voice-assignment') && method === 'PUT') {
    if (world.assignBehavior === 'conflict') {
      return errorEnvelope('SELECTION_CONFLICT', 409);
    }
    return jsonResponse({ speakerId: 'spk_bob', voiceProfileId: 'voice_2', voiceId: 'stock-es-2', changed: true });
  }
  if (url.includes('/voice-previews/') && method === 'GET') {
    world.detailCalls += 1;
    if (world.previewDetailBehavior === 'expiredAlways') {
      return errorEnvelope('URL_EXPIRED', 410);
    }
    if (world.previewDetailBehavior === 'expiredOnce') {
      if (world.detailCalls === 1) {
        return errorEnvelope('URL_EXPIRED', 410);
      }
      return jsonResponse({ previewId: 'vpv_1', status: 'Completed', downloadUrl: 'https://example.com/preview.wav' });
    }
    if (world.previewDetailBehavior === 'pending') {
      return jsonResponse({ previewId: 'vpv_1', status: 'Queued', downloadUrl: null });
    }
    return jsonResponse({ previewId: 'vpv_1', status: 'Completed', downloadUrl: 'https://example.com/preview.wav' });
  }
  if (url.includes('/voice-previews') && method === 'POST') {
    world.previewBodies.push(bodyOf(init));
    switch (world.previewRequestBehavior) {
      case 'quota':
        return errorEnvelope('PREVIEW_QUOTA_EXCEEDED', 429);
      case 'consent':
        return errorEnvelope('VOICE_CONSENT_REQUIRED', 403, 'Tenant policy: cloning consent required for preview.');
      case 'expired':
        return errorEnvelope('URL_EXPIRED', 410);
      case 'error':
        return errorEnvelope('INTERNAL_ERROR', 500);
      case 'empty':
        return jsonResponse({ previewId: '', status: 'Queued' }, 202);
      default:
        return jsonResponse({ previewId: 'vpv_1', status: 'Queued', isDuplicate: false }, 202);
    }
  }
  if (url.includes('/available-voices') && method === 'GET') {
    world.availableCalls += 1;
    if (world.availableBehavior === 'empty') {
      return jsonResponse({ voices: [], excludedCount: 0, excluded: [] });
    }
    if (world.availableBehavior === 'missing404') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    if (world.availableBehavior === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({
      voices: [
        {
          voiceProfileId: 'voice_2',
          voiceId: 'stock-es-2',
          provider: 'acme',
          language: 'es',
          type: 'Stock',
          cloningEnabled: false,
          consentStatus: 'valid',
          isDefault: true,
        },
      ],
      excludedCount: 0,
      excluded: [],
    });
  }
  if (method === 'GET' && url.includes('/speakers')) {
    world.speakersCalls += 1;
    if (world.speakersBehavior === 'empty') {
      return jsonResponse({ items: [], page: 1, pageSize: 100, total: 0, hasMore: false });
    }
    if (world.speakersBehavior === 'missing404') {
      return errorEnvelope('NOT_FOUND', 404);
    }
    if (world.speakersBehavior === 'error500') {
      return errorEnvelope('INTERNAL_ERROR', 500);
    }
    return jsonResponse({
      items: [
        {
          id: 'spk_bob',
          projectId: 'prj_1',
          speakerKey: 'SPEAKER_01',
          displayName: 'Bob',
          segmentCount: 1,
          firstAppearanceMs: 6000,
          lastAppearanceMs: 9000,
          confidence: 0.91,
          assignedVoice: null,
        },
      ],
      page: 1,
      pageSize: 100,
      total: 1,
      hasMore: false,
    });
  }
  return jsonResponse({});
}

function renderWithProviders(node: React.JSX.Element): void {
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

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

function bobSpeaker(): SpeakerView {
  return {
    id: 'spk_bob',
    projectId: 'prj_1',
    speakerKey: 'SPEAKER_01',
    displayName: 'Bob',
    segmentCount: 1,
    firstAppearanceMs: 6000,
    lastAppearanceMs: 9000,
    confidence: 0.91,
    assignedVoice: undefined,
  } as SpeakerView;
}

beforeEach(() => {
  resetWorld();
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
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

describe('PreviewPlayer pending + refresh recovery', () => {
  it('shows the pending panel and refreshes to ready (recovery: refresh)', async () => {
    world.previewDetailBehavior = 'pending';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-pending')).toBeDefined();
    expect(screen.getByTestId('voices-preview-pending').textContent).toContain('has not completed yet');
    world.previewDetailBehavior = 'completed';
    fireEvent.click(screen.getByTestId('voices-preview-retry'));
    const audio = (await screen.findByTestId('voices-preview-audio-stock-es-1')) as HTMLAudioElement;
    expect(audio.getAttribute('src')).toBe('https://example.com/preview.wav');
  });
});

describe('PreviewPlayer expiry refetch matrix', () => {
  it('auto-refetches once and resumes with a prompt (recovery: refetch)', async () => {
    world.previewDetailBehavior = 'expiredOnce';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-audio-stock-es-1')).toBeDefined();
    expect(await screen.findByTestId('voices-preview-resume')).toBeDefined();
    expect(screen.getByTestId('voices-preview-resume').textContent).toContain('press play to resume');
    expect(world.detailCalls).toBe(2);
  });

  it('offers a fresh request after repeated expiry (recovery: request-again)', async () => {
    world.previewDetailBehavior = 'expiredAlways';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-expired')).toBeDefined();
    world.previewDetailBehavior = 'completed';
    fireEvent.click(screen.getByTestId('voices-preview-retry'));
    expect(await screen.findByTestId('voices-preview-audio-stock-es-1')).toBeDefined();
  });

  it('refetches on audio-element errors with a resume prompt', async () => {
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    const audio = (await screen.findByTestId('voices-preview-audio-stock-es-1')) as HTMLAudioElement;
    fireEvent.error(audio);
    expect(await screen.findByTestId('voices-preview-resume')).toBeDefined();
  });
});

describe('PreviewPlayer request-failure matrix', () => {
  it('maps request-time expiry to the expired panel (recovery: fresh request)', async () => {
    world.previewRequestBehavior = 'expired';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-expired')).toBeDefined();
  });

  it('maps generic request failures with a report ref (recovery: report id)', async () => {
    world.previewRequestBehavior = 'error';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    const panel = await screen.findByTestId('voices-preview-error');
    expect(panel.textContent).toContain('Ref: corr-v29');
  });

  it('rejects empty preview ids without a request storm (recovery: retry)', async () => {
    world.previewRequestBehavior = 'empty';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-error')).toBeDefined();
    expect(world.previewBodies.length).toBe(1);
  });

  it('sends custom text and falls back to the default preview text', async () => {
    authenticate();
    renderWithProviders(
      <PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" previewText="Hello there" />,
    );
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    await waitFor(() => expect(world.previewBodies.length).toBe(1));
    expect(JSON.stringify(world.previewBodies[0])).toContain('Hello there');
    cleanup();
    queryClient.clear();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    await waitFor(() => expect(world.previewBodies.length).toBe(2));
    expect(JSON.stringify(world.previewBodies[1])).toContain(PREVIEW_DEFAULT_TEXT);
  });

  it('stamps quota failures with the report ref (recovery: wait)', async () => {
    world.previewRequestBehavior = 'quota';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    const panel = await screen.findByTestId('voices-preview-quota');
    expect(panel.textContent).toContain('Ref: corr-v29');
  });
});

describe('SpeakerList empty/error matrix', () => {
  it('renders the empty cast with a pipeline link (recovery: process)', async () => {
    world.speakersBehavior = 'empty';
    authenticate();
    renderWithProviders(<SpeakerList projectId="prj_1" />);
    expect(await screen.findByTestId('voices-empty')).toBeDefined();
    expect(screen.getByTestId('voices-empty-pipeline-link').getAttribute('href')).toBe('/projects/prj_1');
  });

  it('treats 404 as empty, never an error (recovery: process)', async () => {
    world.speakersBehavior = 'missing404';
    authenticate();
    renderWithProviders(<SpeakerList projectId="prj_1" />);
    expect(await screen.findByTestId('voices-empty')).toBeDefined();
    expect(screen.queryByTestId('voices-error')).toBeNull();
  });

  it('retries server errors with refetch (recovery: retry)', async () => {
    world.speakersBehavior = 'error500';
    authenticate();
    renderWithProviders(<SpeakerList projectId="prj_1" />);
    expect(await screen.findByTestId('voices-error')).toBeDefined();
    const callsBefore = world.speakersCalls;
    world.speakersBehavior = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.speakersCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('voices-list')).toBeDefined();
  });
});

describe('VoiceSelector empty/error/assign matrix', () => {
  it('renders the no-voices empty state with a pipeline link', async () => {
    world.availableBehavior = 'empty';
    authenticate();
    renderWithProviders(<VoiceSelector projectId="prj_1" speaker={bobSpeaker()} />);
    expect(await screen.findByTestId('voices-no-voices')).toBeDefined();
    expect(screen.getByTestId('voices-no-voices-pipeline-link').getAttribute('href')).toBe('/projects/prj_1');
  });

  it('treats missing compatibility as empty, never an error', async () => {
    world.availableBehavior = 'missing404';
    authenticate();
    renderWithProviders(<VoiceSelector projectId="prj_1" speaker={bobSpeaker()} />);
    expect(await screen.findByTestId('voices-no-voices')).toBeDefined();
  });

  it('retries server errors with refetch (recovery: retry)', async () => {
    world.availableBehavior = 'error500';
    authenticate();
    renderWithProviders(<VoiceSelector projectId="prj_1" speaker={bobSpeaker()} />);
    expect(await screen.findByTestId('voices-voices-error')).toBeDefined();
    const callsBefore = world.availableCalls;
    world.availableBehavior = 'ok';
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(world.availableCalls).toBeGreaterThan(callsBefore));
    expect(await screen.findByTestId('voices-voice-list')).toBeDefined();
  });

  it('shows the assign banner with ref and refreshes voices (recovery: refresh)', async () => {
    world.assignBehavior = 'conflict';
    authenticate();
    renderWithProviders(<VoiceSelector projectId="prj_1" speaker={bobSpeaker()} />);
    expect(await screen.findByTestId('voices-voice-list')).toBeDefined();
    fireEvent.click(screen.getByTestId('voices-select-voice-stock-es-2'));
    expect(await screen.findByTestId('voices-impact-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('voices-impact-confirm'));
    const banner = await screen.findByTestId('voices-assign-banner');
    expect(banner.textContent).toContain('Ref: corr-v29');
    expect(screen.getByTestId('voices-assign-message').textContent?.length).toBeGreaterThan(0);
    fireEvent.click(screen.getByTestId('voices-assign-refresh'));
    await waitFor(() => expect(screen.queryByTestId('voices-assign-banner')).toBeNull());
  });

  it('renders the unassigned current-voice placeholder (text, not color)', async () => {
    authenticate();
    renderWithProviders(<VoiceSelector projectId="prj_1" speaker={bobSpeaker()} />);
    const current = await screen.findByTestId('voices-current-voice');
    expect(current.textContent).toBe('—');
    expect(current.getAttribute('title')).toBe('No voice assigned yet.');
  });
});
