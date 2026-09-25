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
import { ImpactDialog } from '../ImpactDialog.js';
import { PreviewPlayer } from '../PreviewPlayer.js';
import { SpeakerList } from '../SpeakerList.js';
import { VoiceSelector } from '../VoiceSelector.js';
import { VoicesWorkspace } from '../VoicesWorkspace.js';
import {
  consentStateFor,
  costNoteFor,
  defaultVoiceFor,
  formatAppearance,
  isConsentError,
  isExpiredError,
  isQuotaError,
  isVoiceAssignable,
  mapPreviewError,
  parseAvailableVoices,
  parseSpeaker,
  parseSpeakerListItems,
  policyMessageFor,
} from '../types.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number, message?: string): Response {
  return jsonResponse(
    { error: { code, message: message ?? `backend ${code}`, correlationId: 'corr-v29', details: {} } },
    status,
  );
}

function makeSpeakerRow(index: number): Record<string, unknown> {
  if (index === 0) {
    return {
      id: 'spk_alice',
      projectId: 'prj_1',
      speakerKey: 'SPEAKER_00',
      displayName: 'Alice',
      segmentCount: 2,
      firstAppearanceMs: 1000,
      lastAppearanceMs: 5000,
      confidence: 0.97,
      assignedVoice: {
        voiceProfileId: 'voice_1',
        voiceId: 'stock-es-1',
        provider: 'acme',
        language: 'es',
        type: 'Stock',
      },
    };
  }
  if (index === 1) {
    return {
      id: 'spk_bob',
      projectId: 'prj_1',
      speakerKey: 'SPEAKER_01',
      displayName: 'Bob',
      segmentCount: 1,
      firstAppearanceMs: 6000,
      lastAppearanceMs: 9000,
      confidence: 0.91,
      assignedVoice: null,
    };
  }
  return {
    id: 'spk_zero',
    projectId: 'prj_1',
    speakerKey: 'SPEAKER_02',
    displayName: 'Zero',
    segmentCount: 0,
    assignedVoice: null,
  };
}

function speakersBody(): Record<string, unknown> {
  return { items: [makeSpeakerRow(0), makeSpeakerRow(1), makeSpeakerRow(2)], page: 1, pageSize: 100, total: 3, hasMore: false };
}

function availableBody(): Record<string, unknown> {
  return {
    voices: [
      {
        voiceProfileId: 'voice_1',
        voiceId: 'stock-es-1',
        provider: 'acme',
        language: 'es',
        type: 'Stock',
        cloningEnabled: false,
        consentStatus: 'valid',
        isDefault: true,
      },
      {
        voiceProfileId: 'voice_2',
        voiceId: 'stock-es-2',
        provider: 'acme',
        language: 'es',
        type: 'Stock',
        cloningEnabled: false,
        consentStatus: 'valid',
        costNote: 'Estimated cost 0.42 USD.',
      },
      {
        voiceProfileId: 'voice_3',
        voiceId: 'cloned-es-1',
        provider: 'acme',
        language: 'es',
        type: 'Cloned',
        cloningEnabled: true,
        consentStatus: 'consent-required',
        tenantPolicyMessage: 'Tenant policy: cloning consent required for cloned-es-1.',
      },
      {
        voiceProfileId: 'voice_4',
        voiceId: 'cloned-es-2',
        provider: 'acme',
        language: 'es',
        type: 'Cloned',
        cloningEnabled: true,
        consentStatus: 'revoked',
        tenantPolicyMessage: 'Tenant policy: consent revoked for cloned-es-2.',
      },
      {
        voiceProfileId: 'voice_5',
        voiceId: 'stock-es-9',
        provider: 'acme',
        language: 'es',
        type: 'Stock',
        cloningEnabled: false,
        consentStatus: 'unavailable',
        tenantPolicyMessage: 'Tenant policy: voice stock-es-9 unavailable in this region.',
      },
    ],
    excludedCount: 1,
    excluded: [{ voiceId: 'stock-fr-1', reasons: ["LANGUAGE_MISMATCH: voice language 'fr' does not match project target 'es'."] }],
  };
}

type AssignBehavior = 'ok' | 'forbidden' | 'conflict';
type PreviewRequestBehavior = 'ok' | 'quota' | 'consent';
type PreviewDetailBehavior = 'completed' | 'pending' | 'expired';

interface VoicesWorld {
  speakersMissing: boolean;
  assignBehavior: AssignBehavior;
  previewRequestBehavior: PreviewRequestBehavior;
  previewDetailBehavior: PreviewDetailBehavior;
  assignBodies: unknown[];
  previewBodies: unknown[];
  methods: string[];
  speakersCalls: number;
  availableCalls: number;
  detailCalls: number;
}

function newWorld(overrides: Partial<VoicesWorld> = {}): VoicesWorld {
  return {
    speakersMissing: false,
    assignBehavior: 'ok',
    previewRequestBehavior: 'ok',
    previewDetailBehavior: 'completed',
    assignBodies: [],
    previewBodies: [],
    methods: [],
    speakersCalls: 0,
    availableCalls: 0,
    detailCalls: 0,
    ...overrides,
  };
}

let world: VoicesWorld = newWorld();

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
  if (url.includes('/voice-assignment') && method === 'PUT') {
    world.methods.push('PUT assignment');
    world.assignBodies.push(bodyOf(init));
    if (world.assignBehavior === 'forbidden') {
      return errorEnvelope('VOICE_CONSENT_REQUIRED', 403, 'Tenant policy: cloning consent required for cloned-es-1.');
    }
    if (world.assignBehavior === 'conflict') {
      return errorEnvelope('SELECTION_CONFLICT', 409);
    }
    const body = world.assignBodies[world.assignBodies.length - 1] as Record<string, unknown>;
    const voiceId = typeof body['voiceId'] === 'string' ? (body['voiceId'] as string) : 'voice_1';
    return jsonResponse({
      speakerId: 'spk_bob',
      voiceProfileId: voiceId,
      voiceId: voiceId === 'voice_1' ? 'stock-es-1' : voiceId,
      changed: true,
      outputStale: false,
      warningCode: null,
      unusedSpeaker: false,
      oldVoiceProfileId: null,
    });
  }
  if (url.includes('/voice-previews/') && method === 'GET') {
    world.detailCalls += 1;
    world.methods.push('GET preview-detail');
    if (world.previewDetailBehavior === 'expired') {
      return errorEnvelope('URL_EXPIRED', 410);
    }
    if (world.previewDetailBehavior === 'pending') {
      return jsonResponse({ previewId: 'vpv_1', status: 'Queued', downloadUrl: null });
    }
    return jsonResponse({ previewId: 'vpv_1', status: 'Completed', downloadUrl: 'https://example.com/preview.wav' });
  }
  if (url.includes('/voice-previews') && method === 'POST') {
    world.methods.push('POST preview');
    world.previewBodies.push(bodyOf(init));
    if (world.previewRequestBehavior === 'quota') {
      return errorEnvelope('PREVIEW_QUOTA_EXCEEDED', 429);
    }
    if (world.previewRequestBehavior === 'consent') {
      return errorEnvelope('VOICE_CONSENT_REQUIRED', 403, 'Tenant policy: cloning consent required for preview.');
    }
    return jsonResponse({ previewId: 'vpv_1', status: 'Queued', isDuplicate: false }, 202);
  }
  if (url.includes('/available-voices') && method === 'GET') {
    world.availableCalls += 1;
    world.methods.push('GET available');
    return jsonResponse(availableBody());
  }
  if (method === 'GET' && /\/speakers\/spk_/.test(url)) {
    const match = /\/speakers\/(spk_[^/?]+)/.exec(url);
    const speakerId = match?.[1] ?? 'spk_alice';
    const rows: Record<string, Record<string, unknown>> = {
      spk_alice: makeSpeakerRow(0),
      spk_bob: makeSpeakerRow(1),
      spk_zero: makeSpeakerRow(2),
    };
    return jsonResponse(rows[speakerId] ?? makeSpeakerRow(0));
  }
  if (method === 'GET' && url.includes('/speakers')) {
    world.speakersCalls += 1;
    world.methods.push('GET speakers');
    if (world.speakersMissing) {
      return errorEnvelope('NOT_FOUND', 404);
    }
    return jsonResponse(speakersBody());
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
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

function renderWorkspace(): void {
  renderWithProviders(<VoicesWorkspace projectId="prj_1" />);
}

function renderSelector(): void {
  const speaker = parseSpeaker(makeSpeakerRow(1));
  if (speaker === undefined) {
    throw new Error('fixture speaker missing');
  }
  renderWithProviders(<VoiceSelector projectId="prj_1" speaker={speaker} />);
}

beforeEach(() => {
  world = newWorld();
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

describe('pure voice helpers', () => {
  it('parses speakers with segment counts and assigned voices', () => {
    const parsed = parseSpeaker(makeSpeakerRow(0));
    expect(parsed?.segmentCount).toBe(2);
    expect(parsed?.assignedVoice?.voiceId).toBe('stock-es-1');
    expect(parsed?.displayName).toBe('Alice');
    const zero = parseSpeaker(makeSpeakerRow(2));
    expect(zero?.segmentCount).toBe(0);
    expect(zero?.assignedVoice).toBeUndefined();
  });

  it('parses speaker lists defensively and sorts by key', () => {
    const items = parseSpeakerListItems(speakersBody());
    expect(items).toHaveLength(3);
    expect(items[0]?.speakerKey).toBe('SPEAKER_00');
  });

  it('keeps compatible and excluded voices separate', () => {
    const parsed = parseAvailableVoices(availableBody());
    expect(parsed.voices).toHaveLength(5);
    expect(parsed.excludedCount).toBe(1);
    expect(parsed.voices.some((voice) => voice.voiceId === 'stock-fr-1')).toBe(false);
    expect(parsed.excluded[0]?.voiceId).toBe('stock-fr-1');
  });

  it('enforces consent states with verbatim policy messages', () => {
    expect(consentStateFor({ consentStatus: 'valid' })).toBe('valid');
    expect(consentStateFor({ consentStatus: 'consent-required' })).toBe('consent-required');
    expect(consentStateFor({ consentStatus: 'revoked' })).toBe('revoked');
    expect(consentStateFor({ consentStatus: 'unavailable' })).toBe('unavailable');
    expect(consentStateFor({})).toBe('valid');
    expect(policyMessageFor({ tenantPolicyMessage: 'Tenant policy: locked.' })).toBe('Tenant policy: locked.');
    expect(costNoteFor({ costNote: 'Estimated cost 0.42 USD.' })).toBe('Estimated cost 0.42 USD.');
    const parsed = parseAvailableVoices(availableBody());
    const valid = parsed.voices.find((voice) => voice.voiceId === 'stock-es-1');
    const gated = parsed.voices.find((voice) => voice.voiceId === 'cloned-es-1');
    expect(valid !== undefined && isVoiceAssignable(valid)).toBe(true);
    expect(gated !== undefined && isVoiceAssignable(gated)).toBe(false);
  });

  it('maps preview failures to distinct branches', () => {
    expect(isQuotaError({ code: 'PREVIEW_QUOTA_EXCEEDED', status: 429 })).toBe(true);
    expect(isConsentError({ code: 'VOICE_CONSENT_REQUIRED', status: 403 })).toBe(true);
    expect(isExpiredError({ code: 'URL_EXPIRED', status: 410 })).toBe(true);
    expect(mapPreviewError({ code: 'PREVIEW_QUOTA_EXCEEDED', status: 429 })).toBe('quota');
    expect(mapPreviewError({ code: 'VOICE_CONSENT_REQUIRED', status: 403 })).toBe('consent');
    expect(mapPreviewError({ code: 'URL_EXPIRED', status: 410 })).toBe('expired');
    expect(mapPreviewError({ code: 'INTERNAL_ERROR', status: 500 })).toBe('unknown');
  });

  it('formats appearance windows and picks the backend default', () => {
    expect(formatAppearance(1000, 5000)).toContain('00:01');
    expect(formatAppearance(undefined, undefined)).toBe('—');
    const parsed = parseAvailableVoices(availableBody());
    expect(defaultVoiceFor(parsed.voices)?.voiceId).toBe('stock-es-1');
    expect(defaultVoiceFor([])).toBeUndefined();
  });
});

describe('SpeakerList rows', () => {
  it('renders segment counts, appearance, voice chips, and consent badges', async () => {
    authenticate();
    renderWithProviders(<SpeakerList projectId="prj_1" />);
    expect(await screen.findByTestId('voices-row-spk_alice')).toBeDefined();
    expect(screen.getByTestId('voices-segments-spk_alice').textContent).toContain('2 segments');
    expect(screen.getByTestId('voices-appearance-spk_alice').textContent).toContain('00:01');
    expect(screen.getByTestId('voices-voice-spk_alice').textContent).toBe('stock-es-1');
    expect(screen.getByTestId('voices-provider-spk_alice').textContent).toBe('acme');
    expect(screen.getByTestId('voices-type-spk_alice').textContent).toBe('Stock');
    expect(screen.getByTestId('voices-consent-spk_alice')).toBeDefined();
    expect(screen.getByTestId('voices-segments-spk_zero').textContent).toContain('0 segments');
    expect(screen.getByTestId('voices-voice-spk_bob').textContent).toBe('—');
  });

  it('shows an EmptyState with a pipeline link when diarization is missing', async () => {
    world.speakersMissing = true;
    authenticate();
    renderWithProviders(<SpeakerList projectId="prj_1" />);
    expect(await screen.findByTestId('voices-empty')).toBeDefined();
    expect(screen.getByTestId('voices-empty-pipeline-link')).toBeDefined();
  });
});

describe('compatible-only rendering (R1)', () => {
  it('never renders incompatible voices', async () => {
    authenticate();
    renderSelector();
    expect(await screen.findByTestId('voices-option-stock-es-1')).toBeDefined();
    expect(screen.queryByTestId('voices-option-stock-fr-1')).toBeNull();
    expect(document.body.textContent).not.toContain('stock-fr-1');
    const options = screen.getAllByTestId(/^voices-option-stock-es-/);
    expect(options.length).toBeGreaterThan(0);
  });
});

describe('impact dialog paths (R2)', () => {
  it('precedes assign with segments, invalidation, and cost; confirm mutates', async () => {
    authenticate();
    renderSelector();
    expect(await screen.findByTestId('voices-option-stock-es-2')).toBeDefined();
    fireEvent.click(screen.getByTestId('voices-select-voice-stock-es-2'));
    expect(await screen.findByTestId('voices-impact-dialog')).toBeDefined();
    expect(screen.getByTestId('voices-impact-segments').textContent).toContain('1 segment');
    expect(screen.getByTestId('voices-impact-invalidation').textContent).toContain('invalidates');
    expect(screen.getByTestId('voices-impact-cost').textContent).toContain('0.42');
    const callsBefore = world.assignBodies.length;
    fireEvent.click(screen.getByTestId('voices-impact-confirm'));
    await waitFor(() => {
      expect(world.assignBodies.length).toBeGreaterThan(callsBefore);
    });
    const body = world.assignBodies[world.assignBodies.length - 1] as Record<string, unknown>;
    expect(body['voiceId']).toBe('voice_2');
    await waitFor(() => {
      expect(screen.queryByTestId('voices-impact-dialog')).toBeNull();
    });
  });

  it('cancel performs no mutation', async () => {
    authenticate();
    renderSelector();
    expect(await screen.findByTestId('voices-option-stock-es-2')).toBeDefined();
    fireEvent.click(screen.getByTestId('voices-select-voice-stock-es-2'));
    expect(await screen.findByTestId('voices-impact-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('voices-impact-cancel'));
    await waitFor(() => {
      expect(screen.queryByTestId('voices-impact-dialog')).toBeNull();
    });
    expect(world.assignBodies).toHaveLength(0);
  });

  it('notes no affected segments for zero-segment speakers', async () => {
    authenticate();
    render(
      <QueryClientProvider client={queryClient}>
        <LocaleProvider>
          <ToastProvider>
            <MemoryRouter>
              <ImpactDialog
                open
                speakerLabel="Zero"
                segmentCount={0}
                voiceLabel="stock-es-1"
                mode="assign"
                isPending={false}
                onConfirm={() => {}}
                onCancel={() => {}}
              />
            </MemoryRouter>
          </ToastProvider>
        </LocaleProvider>
      </QueryClientProvider>,
    );
    expect(await screen.findByTestId('voices-impact-dialog')).toBeDefined();
    expect(screen.getByTestId('voices-impact-segments').textContent).toContain('No segments');
    expect(screen.queryByTestId('voices-impact-cost')).toBeNull();
  });
});

describe('consent gating (R3)', () => {
  it('blocks every non-valid state with the backend message verbatim', async () => {
    authenticate();
    renderSelector();
    expect(await screen.findByTestId('voices-option-cloned-es-1')).toBeDefined();
    const consentButton = screen.getByTestId('voices-select-voice-cloned-es-1') as HTMLButtonElement;
    expect(consentButton.disabled).toBe(true);
    expect(screen.getByTestId('voices-option-policy-cloned-es-1').textContent).toBe(
      'Tenant policy: cloning consent required for cloned-es-1.',
    );
    const revokedButton = screen.getByTestId('voices-select-voice-cloned-es-2') as HTMLButtonElement;
    expect(revokedButton.disabled).toBe(true);
    expect(screen.getByTestId('voices-option-policy-cloned-es-2').textContent).toBe(
      'Tenant policy: consent revoked for cloned-es-2.',
    );
    const unavailableButton = screen.getByTestId('voices-select-voice-stock-es-9') as HTMLButtonElement;
    expect(unavailableButton.disabled).toBe(true);
    const validButton = screen.getByTestId('voices-select-voice-stock-es-1') as HTMLButtonElement;
    expect(validButton.disabled).toBe(false);
  });

  it('shows a banner, refetches, and clears selection on revoked assign', async () => {
    world.assignBehavior = 'forbidden';
    authenticate();
    renderSelector();
    expect(await screen.findByTestId('voices-option-stock-es-1')).toBeDefined();
    const availableBefore = world.availableCalls;
    expect(availableBefore).toBeGreaterThan(0);
    fireEvent.click(screen.getByTestId('voices-select-voice-stock-es-1'));
    expect(await screen.findByTestId('voices-impact-dialog')).toBeDefined();
    fireEvent.click(screen.getByTestId('voices-impact-confirm'));
    expect(await screen.findByTestId('voices-assign-banner')).toBeDefined();
    expect(screen.getByTestId('voices-assign-message').textContent).toContain('Tenant policy: cloning consent');
    expect(screen.queryByTestId('voices-impact-dialog')).toBeNull();
    await waitFor(() => {
      expect(world.availableCalls).toBeGreaterThan(availableBefore);
    });
    const callsBefore = world.availableCalls;
    fireEvent.click(screen.getByTestId('voices-assign-refresh'));
    await waitFor(() => {
      expect(world.availableCalls).toBeGreaterThan(callsBefore);
    });
    expect(screen.queryByTestId('voices-assign-banner')).toBeNull();
  });
});

describe('preview error mapping (R4/R5)', () => {
  it('plays the signed URL without persisting it', async () => {
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    const audio = (await screen.findByTestId('voices-preview-audio-stock-es-1')) as HTMLAudioElement;
    expect(audio.getAttribute('src')).toBe('https://example.com/preview.wav');
    expect(audio.getAttribute('referrerpolicy')).toBe('no-referrer');
    expect(window.localStorage.getItem('https://example.com/preview.wav')).toBeNull();
  });

  it('maps quota failures to a retry-later message', async () => {
    world.previewRequestBehavior = 'quota';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-quota')).toBeDefined();
    expect(screen.getByTestId('voices-preview-quota').textContent).toContain('Try again later');
  });

  it('maps consent failures to the backend message verbatim', async () => {
    world.previewRequestBehavior = 'consent';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-consent')).toBeDefined();
    expect(screen.getByTestId('voices-preview-consent').textContent).toContain(
      'Tenant policy: cloning consent required for preview.',
    );
  });

  it('maps expired URLs to a refetch prompt', async () => {
    world.previewDetailBehavior = 'expired';
    authenticate();
    renderWithProviders(<PreviewPlayer projectId="prj_1" speakerId="spk_bob" voiceId="stock-es-1" />);
    fireEvent.click(screen.getByTestId('voices-preview-request-stock-es-1'));
    expect(await screen.findByTestId('voices-preview-expired')).toBeDefined();
  });
});

describe('reset flow', () => {
  it('resets to the backend default behind confirm', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('voices-row-spk_alice')).toBeDefined();
    expect(await screen.findByTestId('voices-selector')).toBeDefined();
    const reset = (await screen.findByTestId('voices-reset')) as HTMLButtonElement;
    expect(reset.disabled).toBe(false);
    expect(screen.getByTestId('voices-current-voice').textContent).toBe('stock-es-1');
    fireEvent.click(reset);
    expect(await screen.findByTestId('voices-impact-dialog')).toBeDefined();
    expect(screen.getByTestId('voices-impact-segments').textContent).toContain('2 segments');
    const callsBefore = world.assignBodies.length;
    fireEvent.click(screen.getByTestId('voices-impact-confirm'));
    await waitFor(() => {
      expect(world.assignBodies.length).toBeGreaterThan(callsBefore);
    });
    const body = world.assignBodies[world.assignBodies.length - 1] as Record<string, unknown>;
    expect(body['voiceId']).toBe('voice_1');
  });

  it('disables reset when no voice is assigned', async () => {
    authenticate();
    renderWorkspace();
    expect(await screen.findByTestId('voices-row-spk_bob')).toBeDefined();
    fireEvent.click(screen.getByTestId('voices-select-spk_bob'));
    const reset = (await screen.findByTestId('voices-reset')) as HTMLButtonElement;
    expect(reset.disabled).toBe(true);
    expect(reset.title).toContain('No custom voice');
  });
});
