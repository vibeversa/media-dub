// Delta 2: voices remaining-branch closure.
//
// Supplements voicesMatrix (component PreviewPlayer/SpeakerList/
// VoiceSelector flows) with the types defensive sweep (consent tokens,
// policy/cost notes, speaker/voice parsing, excluded handling, appearance
// formatting, error classifiers) and the useVoices hook branches
// (speaker paging + dedupe, detail fetch + error normalization, available
// fetch errors, assign/preview result fallbacks, preview-detail variants,
// invalidation scopes). Synthetic fixtures, fetch intercepted.
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../api/client/index.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { queryKeys } from '../../../api/queryKeys/index.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../auth/useSession.js';
import {
  PREVIEW_DEFAULT_TEXT,
  PREVIEW_MAX_TEXT_LENGTH,
  consentStateFor,
  costNoteFor,
  defaultVoiceFor,
  formatAppearance,
  isAssignmentConflict,
  isConsentError,
  isExpiredError,
  isQuotaError,
  isVoiceAssignable,
  isVoicesNotFound,
  mapPreviewError,
  parseAvailableVoices,
  parseSpeaker,
  parseSpeakerListItems,
  parseVoiceOption,
  policyMessageFor,
} from '../types.js';
import {
  fetchPreviewDetail,
  invalidateSpeakers,
  useAssignSpeakerVoice,
  useAvailableVoices,
  useRequestVoicePreview,
  useSpeaker,
  useSpeakers,
} from '../useVoices.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function errorEnvelope(code: string, status: number): Response {
  return jsonResponse(
    { error: { code, message: `backend ${code}`, correlationId: 'corr-v29b', details: {} } },
    status,
  );
}

type Mode = 'ok' | 'paged' | 'detail404' | 'available500' | 'shapeless';

interface World {
  mode: Mode;
  speakerCalls: number;
}

let world: World;

function resetWorld(): void {
  world = { mode: 'ok', speakerCalls: 0 };
}

function speakerRow(id: string, overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    id,
    speakerKey: id.toUpperCase(),
    displayName: `Name ${id}`,
    segmentCount: 2,
    firstAppearanceMs: 1000,
    lastAppearanceMs: 5000,
    confidence: 0.9,
    assignedVoice: null,
    ...overrides,
  };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

function methodOf(input: RequestInfo | URL, init?: RequestInit): string {
  if (typeof input !== 'string' && !(input instanceof URL)) {
    const request = input as Request;
    if (typeof request.method === 'string' && request.method !== '') return request.method.toUpperCase();
  }
  return (init?.method ?? 'GET').toUpperCase();
}

async function mockFetch(input: RequestInfo | URL, init?: RequestInit): Promise<Response> {
  const url = urlOf(input);
  const method = methodOf(input, init);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (url.includes('/voice-assignment') && method === 'PUT') {
    return jsonResponse({ changed: false, outputStale: false });
  }
  if (url.includes('/voice-previews') && method === 'POST') {
    return jsonResponse({ ok: true });
  }
  if (url.includes('/voice-previews/') && method === 'GET') {
    return jsonResponse({ status: 'Completed' });
  }
  if (url.includes('/available-voices') && method === 'GET') {
    if (world.mode === 'available500') return errorEnvelope('INTERNAL_ERROR', 500);
    return jsonResponse({ voices: [], excludedCount: 0, excluded: [] });
  }
  if (method === 'GET' && /\/speakers\/spk_/.test(url)) {
    if (world.mode === 'detail404') return errorEnvelope('NOT_FOUND', 404);
    return jsonResponse(speakerRow('spk_bob'));
  }
  if (method === 'GET' && url.includes('/speakers')) {
    world.speakerCalls += 1;
    if (world.mode === 'paged') {
      const page = Number.parseInt(new URL(url).searchParams.get('page') ?? '1', 10);
      if (page === 1) {
        return jsonResponse({ items: [speakerRow('spk_bob'), speakerRow('spk_alice')], page: 1, pageSize: 100, total: 3, hasMore: true });
      }
      return jsonResponse({ items: [speakerRow('spk_bob')], page: 2, pageSize: 100, total: 3, hasMore: false });
    }
    return jsonResponse({ items: [speakerRow('spk_bob')], page: 1, pageSize: 100, total: 1, hasMore: false });
  }
  return jsonResponse({});
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
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

describe('consent/policy/cost helpers', () => {
  it('maps every consent vocabulary to its state', () => {
    expect(consentStateFor(undefined)).toBe('valid');
    expect(consentStateFor(null)).toBe('valid');
    expect(consentStateFor('granted')).toBe('valid');
    expect(consentStateFor({ consentStatus: 'approved' })).toBe('valid');
    expect(consentStateFor({ ConsentState: 'verified' })).toBe('valid');
    expect(consentStateFor({ consent: 'missing' })).toBe('unavailable');
    expect(consentStateFor({ consentStatus: 'not-found' })).toBe('unavailable');
    expect(consentStateFor({ consentStatus: 'unknown-token-xyz' })).toBe('valid');
    expect(consentStateFor({ consentStatus: 'pending' })).toBe('consent-required');
    expect(consentStateFor({ consentStatus: 'REQUIRES_CONSENT' })).toBe('consent-required');
    expect(consentStateFor({ consentStatus: 'awaiting-consent' })).toBe('consent-required');
    expect(consentStateFor({ consentStatus: 'withdrawn' })).toBe('revoked');
    expect(consentStateFor({ consentStatus: 'expired' })).toBe('revoked');
    expect(consentStateFor({ consentRevoked: true })).toBe('revoked');
    expect(consentStateFor({ revoked: true })).toBe('revoked');
    expect(consentStateFor({ requiresConsent: true })).toBe('consent-required');
    expect(consentStateFor({ needsConsent: true })).toBe('consent-required');
    expect(consentStateFor({ consentUnavailable: true })).toBe('unavailable');
    expect(consentStateFor({})).toBe('valid');
  });

  it('reads policy messages and cost notes verbatim or not at all', () => {
    expect(policyMessageFor(undefined)).toBeUndefined();
    expect(policyMessageFor(null)).toBeUndefined();
    expect(policyMessageFor({ policyMessage: 'Tenant policy applies.' })).toBe('Tenant policy applies.');
    expect(policyMessageFor({ Policy: 'P.' })).toBe('P.');
    expect(policyMessageFor({})).toBeUndefined();
    expect(costNoteFor(undefined)).toBeUndefined();
    expect(costNoteFor({ costNote: 'Cloning costs extra.' })).toBe('Cloning costs extra.');
    expect(costNoteFor({ price: '$1' })).toBe('$1');
    expect(costNoteFor({ costUsd: 2.5 })).toBe('Estimated cost 2.5 USD.');
    expect(costNoteFor({})).toBeUndefined();
    expect(isVoiceAssignable({ consentState: 'valid' })).toBe(true);
    expect(isVoiceAssignable({ consentState: 'revoked' })).toBe(false);
    expect(PREVIEW_DEFAULT_TEXT.length).toBeGreaterThan(0);
    expect(PREVIEW_MAX_TEXT_LENGTH).toBe(500);
  });
});

describe('speaker parsing branches', () => {
  it('rejects id-less rows and falls back names and counts', () => {
    expect(parseSpeaker(undefined)).toBeUndefined();
    expect(parseSpeaker(null)).toBeUndefined();
    expect(parseSpeaker({})).toBeUndefined();
    expect(parseSpeaker({ id: '' })).toBeUndefined();
    const bare = parseSpeaker({ id: 'spk_1' });
    expect(bare?.speakerKey).toBe('spk_1');
    expect(bare?.displayName).toBe('spk_1');
    expect(bare?.segmentCount).toBe(0);
    expect(bare?.assignedVoice).toBeUndefined();
    const assigned = parseSpeaker({
      id: 'spk_1',
      speakerKey: 'S1',
      name: 'Sam',
      segmentCount: 2.7,
      firstAppearanceMs: 100,
      lastAppearanceMs: 200,
      confidence: 0.5,
      assignedVoice: { voiceId: 'v_1', provider: 'acme', language: 'es', type: 'stock' },
    });
    expect(assigned?.segmentCount).toBe(2);
    expect(assigned?.assignedVoice?.voiceProfileId).toBe('v_1');
    expect(parseSpeaker({ id: 's', assignedVoice: {} })?.assignedVoice).toBeUndefined();
    expect(parseSpeaker({ id: 's', assignedVoice: 7 })?.assignedVoice).toBeUndefined();
  });

  it('parses lists with sorting and skips bad rows', () => {
    expect(parseSpeakerListItems(null)).toEqual([]);
    expect(parseSpeakerListItems({ items: 'nope' })).toEqual([]);
    expect(parseSpeakerListItems('nope')).toEqual([]);
    const items = parseSpeakerListItems({ items: [speakerRow('spk_b'), speakerRow('spk_a'), null, {}] });
    expect(items.map((s) => s.id)).toEqual(['spk_a', 'spk_b']);
  });
});

describe('voice option parsing branches', () => {
  it('rejects id-less options and fills every fallback', () => {
    expect(parseVoiceOption(undefined)).toBeUndefined();
    expect(parseVoiceOption({})).toBeUndefined();
    expect(parseVoiceOption({ voiceProfileId: '', voiceId: '' })).toBeUndefined();
    const bare = parseVoiceOption({ voiceId: 'v_1' });
    expect(bare?.voiceProfileId).toBe('v_1');
    expect(bare?.provider).toBe('unknown');
    expect(bare?.language).toBe('unknown');
    expect(bare?.voiceType).toBe('unknown');
    expect(bare?.label).toBe('v_1');
    expect(bare?.cloningEnabled).toBe(false);
    expect(bare?.isDefault).toBe(false);
    const full = parseVoiceOption({
      voiceProfileId: 'vp_1',
      voiceId: 'v_1',
      provider: 'acme',
      language: 'es',
      voiceType: 'stock',
      cloningEnabled: true,
      isDefault: true,
      label: 'Elena',
      consentStatus: 'valid',
      policyMessage: 'P',
      costNote: 'C',
    });
    expect(full?.consentState).toBe('valid');
    expect(full?.isDefault).toBe(true);
  });

  it('parses available-voices with excluded accounting', () => {
    expect(parseAvailableVoices(undefined)).toEqual({ voices: [], excludedCount: 0, excluded: [] });
    expect(parseAvailableVoices(null)).toEqual({ voices: [], excludedCount: 0, excluded: [] });
    expect(parseAvailableVoices({ voices: 'nope', excluded: 'nope' }).voices).toEqual([]);
    const view = parseAvailableVoices({
      voices: [{ voiceId: 'v_b' }, { voiceId: 'v_a' }, null, {}],
      excluded: [{ voiceId: 'v_x', reasons: ['policy'] }, { voiceId: '' }, null],
      excludedCount: 3,
    });
    expect(view.voices.map((v) => v.voiceId)).toEqual(['v_a', 'v_b']);
    expect(view.excluded).toEqual([{ voiceId: 'v_x', reasons: ['policy'] }]);
    expect(view.excludedCount).toBe(3);
    expect(parseAvailableVoices({ voices: [{ voiceId: 'v_1' }] }).excludedCount).toBe(0);
    const withDefault = parseAvailableVoices({
      voices: [{ voiceId: 'v_1' }, { voiceId: 'v_2', isDefault: true }],
    });
    expect(defaultVoiceFor(withDefault.voices)?.voiceId).toBe('v_2');
    expect(defaultVoiceFor([])).toBeUndefined();
  });
});

describe('appearance + error classifiers', () => {
  it('formats appearance windows with clamping', () => {
    expect(formatAppearance(undefined, undefined)).toBe('—');
    expect(formatAppearance(61000, 90000)).toContain('→');
    expect(formatAppearance(61000, undefined)).toBe('01:01.000');
    expect(formatAppearance(undefined, 90000)).toBe('01:30.000');
  });

  it('classifies quota/consent/expired/conflict/not-found exhaustively', () => {
    expect(isQuotaError(undefined)).toBe(false);
    expect(isQuotaError(null)).toBe(false);
    expect(isQuotaError({ code: 'PREVIEW_QUOTA_EXCEEDED' })).toBe(true);
    expect(isQuotaError({ code: 'QUOTA_EXCEEDED' })).toBe(true);
    expect(isQuotaError({ code: 'PROVIDER_QUOTA_EXHAUSTED' })).toBe(true);
    expect(isQuotaError({ code: 'RATE_LIMITED' })).toBe(true);
    expect(isQuotaError({ status: 429 })).toBe(true);
    expect(isQuotaError({ code: 'X', status: 500 })).toBe(false);
    expect(isConsentError({ code: 'VOICE_CONSENT_REQUIRED' })).toBe(true);
    expect(isConsentError({ code: 'POLICY_DENIED' })).toBe(true);
    expect(isConsentError({ status: 403 })).toBe(true);
    expect(isConsentError(undefined)).toBe(false);
    expect(isExpiredError({ code: 'URL_EXPIRED' })).toBe(true);
    expect(isExpiredError({ status: 410 })).toBe(true);
    expect(isExpiredError(null)).toBe(false);
    expect(isAssignmentConflict({ code: 'SELECTION_CONFLICT' })).toBe(true);
    expect(isAssignmentConflict({ status: 409 })).toBe(true);
    expect(isAssignmentConflict(undefined)).toBe(false);
    expect(isVoicesNotFound({ code: 'NOT_FOUND' })).toBe(true);
    expect(isVoicesNotFound({ code: 'PROJECT_NOT_FOUND' })).toBe(true);
    expect(isVoicesNotFound({ status: 404 })).toBe(true);
    expect(isVoicesNotFound(null)).toBe(false);
    expect(mapPreviewError({ code: 'PREVIEW_QUOTA_EXCEEDED' })).toBe('quota');
    expect(mapPreviewError({ code: 'VOICE_CONSENT_REQUIRED' })).toBe('consent');
    expect(mapPreviewError({ status: 410 })).toBe('expired');
    expect(mapPreviewError({ code: 'X' })).toBe('unknown');
    expect(mapPreviewError(undefined)).toBe('unknown');
  });
});

describe('useVoices hook branches', () => {
  function renderProbe(node: React.JSX.Element): void {
    render(<QueryClientProvider client={queryClient}>{node}</QueryClientProvider>);
  }

  it('pages, dedupes, and sorts speaker lists', async () => {
    world.mode = 'paged';
    function Probe(): null {
      useSpeakers('prj_1');
      return null;
    }
    renderProbe(<Probe />);
    await waitFor(
      () => {
        const cached = queryClient.getQueryData<readonly { id: string }[]>(queryKeys.speakers.list('prj_1'));
        expect(cached?.map((s) => s.id)).toEqual(['spk_alice', 'spk_bob']);
      },
      { timeout: 5000 },
    );
    expect(world.speakerCalls).toBe(2);
  });

  it('hydrates one speaker and normalizes detail failures', async () => {
    function Probe(): null {
      useSpeaker('prj_1', 'spk_bob');
      useSpeaker('prj_1', undefined);
      useSpeaker('', 'spk_bob');
      return null;
    }
    renderProbe(<Probe />);
    await waitFor(
      () => {
        const cached = queryClient.getQueryData<{ id: string }>(queryKeys.speakers.detail('prj_1', 'spk_bob'));
        expect(cached?.id).toBe('spk_bob');
      },
      { timeout: 5000 },
    );
    cleanup();
    queryClient.clear();
    world.mode = 'detail404';
    function Failing(): null {
      useSpeaker('prj_1', 'spk_missing');
      return null;
    }
    renderProbe(<Failing />);
    await waitFor(
      () => {
        const state = queryClient.getQueryState(queryKeys.speakers.detail('prj_1', 'spk_missing'));
        expect(state?.error).toBeDefined();
      },
      { timeout: 5000 },
    );
  });

  it('normalizes available-voices failures', async () => {
    world.mode = 'available500';
    function Probe(): null {
      useAvailableVoices('prj_1', 'spk_bob');
      useAvailableVoices('prj_1', undefined);
      return null;
    }
    renderProbe(<Probe />);
    await waitFor(
      () => {
        const state = queryClient.getQueryState(queryKeys.voices.available('prj_1', 'spk_bob'));
        expect(state?.error).toBeDefined();
      },
      { timeout: 5000 },
    );
  });

  it('falls back to request values on shapeless assign/preview responses', async () => {
    world.mode = 'shapeless';
    type AssignResult = { voiceId: string; changed: boolean; warningCode: string | undefined } | undefined;
    type PreviewResult = { previewId: string; status: string; isDuplicate: boolean } | undefined;
    function Probe(): null {
      const assign = useAssignSpeakerVoice('prj_1');
      const preview = useRequestVoicePreview('prj_1');
      (window as unknown as { __v?: unknown }).__v = { assign, preview };
      return null;
    }
    renderProbe(<Probe />);
    await waitFor(() => expect((window as unknown as { __v?: unknown }).__v).toBeDefined());
    const hooks = (window as unknown as { __v: { assign: { mutateAsync: (v: unknown) => Promise<AssignResult> }; preview: { mutateAsync: (v: unknown) => Promise<PreviewResult> } } }).__v;
    const emptyReason = await hooks.assign.mutateAsync({ speakerId: 'spk_bob', voiceId: 'stock-es-2', reason: '' });
    expect(emptyReason?.voiceId).toBe('stock-es-2');
    expect(emptyReason?.changed).toBe(false);
    expect(emptyReason?.warningCode).toBeUndefined();
    const withReason = await hooks.assign.mutateAsync({ speakerId: 'spk_bob', voiceId: 'stock-es-2', reason: 'sounds right' });
    expect(withReason?.voiceId).toBe('stock-es-2');
    const previewResult = await hooks.preview.mutateAsync({ speakerId: 'spk_bob', voiceId: 'v_1', text: 'hi' });
    expect(previewResult?.previewId).toBe('');
    expect(previewResult?.isDuplicate).toBe(false);
  });

  it('resolves preview details with url/id fallbacks and errors', async () => {
    const completed = await fetchPreviewDetail('prj_1', 'vpv_1');
    expect(completed.status).toBe('Completed');
    expect(completed.previewId).toBe('vpv_1');
    expect(completed.downloadUrl).toBeUndefined();
    setInnerFetchForTests((async () => errorEnvelope('INTERNAL_ERROR', 500)) as typeof fetch);
    await expect(fetchPreviewDetail('prj_1', 'vpv_9')).rejects.toMatchObject({ code: 'INTERNAL_ERROR' });
  });

  it('invalidates speaker scopes with and without ids', async () => {
    await invalidateSpeakers(queryClient, 'prj_1', 'spk_bob');
    await invalidateSpeakers(queryClient, 'prj_1');
    await invalidateSpeakers(queryClient, 'prj_1', '');
  });
});
