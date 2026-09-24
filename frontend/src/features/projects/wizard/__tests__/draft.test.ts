import { describe, expect, it } from 'vitest';
import {
  DEFAULT_WIZARD_DRAFT,
  SETTINGS_VERSION_NEW,
  buildCreatePayload,
  buildHumanSummary,
  buildProcessingSettings,
  buildProcessingSettingsJson,
  previewConfigHash,
} from '../draft.js';
import type { WizardDraft } from '../draft.js';

function draft(overrides: Partial<WizardDraft> = {}): WizardDraft {
  return { ...DEFAULT_WIZARD_DRAFT, ...overrides };
}

describe('buildProcessingSettings', () => {
  it('always carries schemaVersion 1 with the kept refinements', () => {
    const settings = buildProcessingSettings(draft());
    expect(settings['schemaVersion']).toBe(1);
    expect(settings['reviewThreshold']).toBe(0.7);
    expect(settings['sourceSeparationPolicy']).toBe('auto');
  });

  it('omits emptied optionals so the server sees the minimal shape', () => {
    const settings = buildProcessingSettings(
      draft({ voicePolicy: '', glossary: [], styleInstructions: '   ' }),
    );
    expect('voicePolicy' in settings).toBe(false);
    expect('glossary' in settings).toBe(false);
    expect('styleInstructions' in settings).toBe(false);
  });

  it('keeps valid glossary rows with trimmed terms', () => {
    const settings = buildProcessingSettings(
      draft({ glossary: [{ sourceTerm: '  ship ', targetTerm: ' nave ', notes: '' }] }),
    );
    expect(settings['glossary']).toEqual([{ sourceTerm: 'ship', targetTerm: 'nave' }]);
  });

  it('drops glossary rows missing either term', () => {
    const settings = buildProcessingSettings(
      draft({ glossary: [{ sourceTerm: '', targetTerm: 'nave', notes: '' }] }),
    );
    expect('glossary' in settings).toBe(false);
  });
});

describe('buildProcessingSettingsJson', () => {
  it('is stable regardless of key insertion order', () => {
    const first = buildProcessingSettingsJson(draft({ voicePolicy: 'matched', outputProfile: 'social' }));
    const second = buildProcessingSettingsJson(draft({ outputProfile: 'social', voicePolicy: 'matched' }));
    expect(first).toBe(second);
  });
});

describe('previewConfigHash (R4)', () => {
  it('is deterministic lowercase hex', () => {
    const first = previewConfigHash(draft());
    expect(first).toMatch(/^[0-9a-f]{8}$/);
    expect(previewConfigHash(draft())).toBe(first);
  });

  it('changes with languages and settings', () => {
    const base = previewConfigHash(draft());
    expect(previewConfigHash(draft({ targetLanguage: 'fr' }))).not.toBe(base);
    expect(previewConfigHash(draft({ reviewThreshold: 0.9 }))).not.toBe(base);
  });
});

describe('buildHumanSummary (R4)', () => {
  it('covers every dimension with plain-text values', () => {
    const rows = buildHumanSummary(draft({ name: 'Pilot' }));
    const byId = new Map(rows.map((row) => [row.id, row.value]));
    expect(byId.get('name')).toBe('Pilot');
    expect(byId.get('source')).toBe('en');
    expect(byId.get('target')).toBe('es');
    expect(byId.get('threshold')).toBe('0.7');
    expect(byId.get('glossary')).toBe('0');
    expect(byId.get('upload')).toBe('later');
    expect(SETTINGS_VERSION_NEW).toBe(1);
  });

  it('splices the description only when present', () => {
    expect(buildHumanSummary(draft()).some((row) => row.id === 'description')).toBe(false);
    expect(
      buildHumanSummary(draft({ description: 'hello' })).find((row) => row.id === 'description')?.value,
    ).toBe('hello');
  });

  it('names the attached file for upload-now', () => {
    expect(
      buildHumanSummary(draft({ uploadMode: 'now', uploadFileName: 'clip.mp4' })).find(
        (row) => row.id === 'upload',
      )?.value,
    ).toBe('clip.mp4');
  });
});

describe('buildCreatePayload', () => {
  it('sends generated base fields plus description and processing settings', () => {
    const payload = buildCreatePayload(draft({ name: 'Pilot', description: 'hi' }));
    expect(payload.name).toBe('Pilot');
    expect(payload.sourceLanguage).toBe('en');
    expect(payload.targetLanguage).toBe('es');
    const wire = payload as unknown as Record<string, unknown>;
    expect(wire['description']).toBe('hi');
    expect(wire['processingSettings']).toMatchObject({ schemaVersion: 1 });
  });

  it('trims names and omits blank descriptions', () => {
    const wire = buildCreatePayload(draft({ name: '  Pilot  ' })) as unknown as Record<string, unknown>;
    expect(wire['name']).toBe('Pilot');
    expect('description' in wire).toBe(false);
  });
});
