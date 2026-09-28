// Delta: wizard draft + projects-query remaining-branch closure.
//
// Supplements draft.test (settings shapes, hash, summary) with slice caps
// (style 4000, notes 1024, description 2000), empty-optional omission,
// upload-mode summary rows, whitespace trimming, step guards, and the
// useProjectsQuery error-normalization branch. Synthetic drafts only.
//
// Intentional-exclusion candidates (defensive, unreachable via the typed
// public API without throwing first): `stableStringify`'s null/undefined
// input arm and its `JSON.stringify(...) ?? 'null'` fallback — every
// public builder only feeds defined JSON values (nullish draft fields
// throw on `.trim()` before reaching the serializer).
import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../../api/client/index.js';
import { queryKeys } from '../../../../api/queryKeys/index.js';
import { queryClient } from '../../../../app/providers/queryClient.js';
import { useAppStore } from '../../../../stores/index.js';
import { useAuthStore } from '../../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../../auth/useSession.js';
import { DEFAULT_FILTERS, toServerQuery } from '../../api.js';
import { useProjectsQuery } from '../../useProjectsQuery.js';
import {
  DEFAULT_WIZARD_DRAFT,
  SETTINGS_VERSION_NEW,
  WIZARD_STEPS,
  WIZARD_STORAGE_KEY,
  buildCreatePayload,
  buildHumanSummary,
  buildProcessingSettings,
  buildProcessingSettingsJson,
  isWizardStep,
  previewConfigHash,
} from '../draft.js';
import type { WizardDraft } from '../draft.js';

function draft(overrides: Partial<WizardDraft> = {}): WizardDraft {
  return { ...DEFAULT_WIZARD_DRAFT, ...overrides };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') return input;
  if (input instanceof URL) return input.href;
  return (input as Request).url;
}

async function mockFetch(input: RequestInfo | URL): Promise<Response> {
  const url = urlOf(input);
  if (!url.includes('/api/v1/')) return jsonResponse({});
  if (/\/projects(\?|$)/.test(url)) {
    return jsonResponse(
      { error: { code: 'INTERNAL_ERROR', message: 'backend INTERNAL_ERROR', correlationId: 'corr-d', details: {} } },
      500,
    );
  }
  return jsonResponse({});
}

beforeEach(() => {
  setInnerFetchForTests(mockFetch as typeof fetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  queryClient.clear();
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view']);
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

describe('buildProcessingSettings caps', () => {
  it('omits every emptied optional while keeping the version + threshold', () => {
    const settings = buildProcessingSettings(
      draft({ sourceSeparationPolicy: '  ', outputProfile: '', timingStrictness: '', voicePolicy: '' }),
    );
    expect(settings['schemaVersion']).toBe(1);
    expect(settings['reviewThreshold']).toBe(0.7);
    expect('sourceSeparationPolicy' in settings).toBe(false);
    expect('outputProfile' in settings).toBe(false);
    expect('timingStrictness' in settings).toBe(false);
    expect('voicePolicy' in settings).toBe(false);
  });

  it('caps style instructions at 4000 chars', () => {
    const settings = buildProcessingSettings(draft({ styleInstructions: `  ${'x'.repeat(5000)}  ` }));
    expect((settings['styleInstructions'] as string).length).toBe(4000);
  });

  it('caps glossary notes at 1024 chars and keeps note-less rows', () => {
    const settings = buildProcessingSettings(
      draft({
        glossary: [
          { sourceTerm: 'ship', targetTerm: 'nave', notes: `  ${'n'.repeat(2000)}  ` },
          { sourceTerm: '  dock  ', targetTerm: '  muelle  ', notes: '   ' },
        ],
      }),
    );
    const glossary = settings['glossary'] as Array<Record<string, string>>;
    expect(glossary.length).toBe(2);
    expect(glossary[0]?.['notes']?.length).toBe(1024);
    expect(glossary[1]).toEqual({ sourceTerm: 'dock', targetTerm: 'muelle' });
  });

  it('serializes stably and hashes trimmed languages', () => {
    expect(buildProcessingSettingsJson(draft())).toBe(buildProcessingSettingsJson(draft()));
    expect(previewConfigHash(draft({ sourceLanguage: '  en  ' }))).toBe(previewConfigHash(draft()));
  });
});

describe('buildHumanSummary rows', () => {
  it('dashes every blank dimension', () => {
    const rows = buildHumanSummary(
      draft({ name: '', sourceLanguage: '', targetLanguage: '', sourceSeparationPolicy: '', outputProfile: '', timingStrictness: '', voicePolicy: '', styleInstructions: '' }),
    );
    const byId = new Map(rows.map((row) => [row.id, row.value]));
    expect(byId.get('name')).toBe('—');
    expect(byId.get('source')).toBe('—');
    expect(byId.get('target')).toBe('—');
    expect(byId.get('separation')).toBe('—');
    expect(byId.get('style')).toBe('—');
    expect(byId.get('upload')).toBe('later');
    expect(byId.has('description')).toBe(false);
  });

  it('inserts descriptions and reports upload/style state', () => {
    const rows = buildHumanSummary(
      draft({ description: '  pilot  ', uploadMode: 'now', uploadFileName: 'clip.mp4', styleInstructions: 'warm tone' }),
    );
    const byId = new Map(rows.map((row) => [row.id, row.value]));
    expect(byId.get('description')).toBe('pilot');
    expect(byId.get('upload')).toBe('clip.mp4');
    expect(byId.get('style')).toBe(String('warm tone'.length));
    const missing = buildHumanSummary(draft({ uploadMode: 'now', uploadFileName: '' }));
    expect(new Map(missing.map((row) => [row.id, row.value])).get('upload')).toBe('now-missing');
  });
});

describe('buildCreatePayload + step guards', () => {
  it('carries trimmed descriptions capped at 2000 chars', () => {
    expect(buildCreatePayload(draft({ description: '  hello  ' }))).toMatchObject({ description: 'hello' });
    expect('description' in buildCreatePayload(draft())).toBe(false);
    const long = buildCreatePayload(draft({ description: 'd'.repeat(3000) }));
    expect((long as unknown as { description: string }).description.length).toBe(2000);
  });

  it('recognizes every wizard step and rejects the rest', () => {
    for (const step of WIZARD_STEPS) expect(isWizardStep(step)).toBe(true);
    expect(isWizardStep('bogus')).toBe(false);
    expect(isWizardStep(undefined)).toBe(false);
    expect(isWizardStep(null)).toBe(false);
    expect(WIZARD_STORAGE_KEY).toContain('dubbing.createWizard');
    expect(SETTINGS_VERSION_NEW).toBe(1);
    expect(DEFAULT_WIZARD_DRAFT.reviewThreshold).toBe(0.7);
  });
});

describe('useProjectsQuery error branch', () => {
  it('normalizes list failures to AppError (recovery: retry)', async () => {
    function Probe(): null {
      useProjectsQuery(DEFAULT_FILTERS);
      return null;
    }
    render(
      <QueryClientProvider client={queryClient}>
        <Probe />
      </QueryClientProvider>,
    );
    const key = queryKeys.projects.list(toServerQuery(DEFAULT_FILTERS));
    await waitFor(
      () => {
        const state = queryClient.getQueryState(key);
        expect(state?.error).toMatchObject({ code: 'INTERNAL_ERROR' });
      },
      { timeout: 5000 },
    );
  });
});
