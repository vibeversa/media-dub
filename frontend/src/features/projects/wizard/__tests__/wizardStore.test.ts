import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { DEFAULT_WIZARD_DRAFT, WIZARD_STORAGE_KEY } from '../draft.js';
import { loadPersistedWizard, sanitizeDraft, useWizardStore } from '../wizardStore.js';

function storedRaw(): string | null {
  return window.localStorage.getItem(WIZARD_STORAGE_KEY);
}

beforeEach(() => {
  window.localStorage.clear();
  useWizardStore.getState().resetWizardForTests();
});

afterEach(() => {
  window.localStorage.clear();
  useWizardStore.getState().resetWizardForTests();
});

describe('draft persistence round-trip (R3)', () => {
  it('starts from defaults when storage is empty', () => {
    const loaded = loadPersistedWizard();
    expect(loaded.draft).toEqual(DEFAULT_WIZARD_DRAFT);
    expect(loaded.step).toBe('basics');
  });

  it('persists draft and step across a store reset (simulated refresh)', () => {
    useWizardStore.getState().updateDraft({ name: 'Pilot episode', targetLanguage: 'fr' });
    useWizardStore.getState().setStep('review');
    expect(storedRaw()).toContain('Pilot episode');

    const reloaded = loadPersistedWizard();
    expect(reloaded.draft.name).toBe('Pilot episode');
    expect(reloaded.draft.targetLanguage).toBe('fr');
    expect(reloaded.step).toBe('review');
  });

  it('never persists File objects — only metadata', () => {
    const file = new File(['bytes'], 'clip.mp4', { type: 'video/mp4' });
    useWizardStore.getState().setUploadFile(file);
    expect(useWizardStore.getState().uploadFile).toBe(file);
    const raw = storedRaw() ?? '';
    expect(raw).toContain('clip.mp4');
    expect(raw).not.toContain('bytes');
    const parsed = JSON.parse(raw) as Record<string, unknown>;
    expect('uploadFile' in parsed).toBe(false);
    const draft = parsed['draft'] as Record<string, unknown>;
    expect(draft['uploadFileName']).toBe('clip.mp4');
    expect(draft['uploadFileSize']).toBe(5);
    expect(draft['uploadFileType']).toBe('video/mp4');
  });

  it('clears storage on submit/discard path', () => {
    useWizardStore.getState().updateDraft({ name: 'temp' });
    expect(storedRaw()).not.toBeNull();
    useWizardStore.getState().clearWizard();
    expect(storedRaw()).toBeNull();
    expect(useWizardStore.getState().draft.name).toBe('');
    expect(useWizardStore.getState().step).toBe('basics');
  });

  it('sanitizes invalid stored payloads field-by-field', () => {
    window.localStorage.setItem(
      WIZARD_STORAGE_KEY,
      JSON.stringify({ draft: { name: 42, reviewThreshold: 99, glossary: 'nope' }, step: 'nope' }),
    );
    const loaded = loadPersistedWizard();
    expect(loaded.draft.name).toBe('');
    expect(loaded.draft.reviewThreshold).toBe(1);
    expect(loaded.draft.glossary).toEqual([]);
    expect(loaded.step).toBe('basics');
  });

  it('sanitizeDraft drops invalid glossary rows but keeps valid ones', () => {
    const draft = sanitizeDraft({
      glossary: [
        { sourceTerm: 'ship', targetTerm: 'nave', notes: 'x' },
        { sourceTerm: '', targetTerm: 'nave', notes: '' },
      ],
    });
    expect(draft.glossary).toEqual([{ sourceTerm: 'ship', targetTerm: 'nave', notes: 'x' }]);
  });
});
