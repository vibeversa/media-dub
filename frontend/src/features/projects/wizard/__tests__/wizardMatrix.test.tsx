// Task 039B: wizard step state-matrix gap closure.
//
// Direct prop-matrix specs for the five wizard steps plus the `draft.ts` /
// `languages.ts` helper sweep. Each control asserts its write path
// (non-color text signals per 041C); validation-failure display uses the
// error text, never color alone. No network is used (R3).
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '../../../../i18n/i18n.js';
import { BasicsStep } from '../BasicsStep.js';
import { LanguageStep } from '../LanguageStep.js';
import { ReviewStep } from '../ReviewStep.js';
import { SettingsStep } from '../SettingsStep.js';
import { UploadStep } from '../UploadStep.js';
import {
  DEFAULT_WIZARD_DRAFT,
  buildCreatePayload,
  buildHumanSummary,
  buildProcessingSettings,
  buildProcessingSettingsJson,
  isWizardStep,
  previewConfigHash,
} from '../draft.js';
import type { WizardDraft } from '../draft.js';
import { DEFAULT_SOURCE_LANGUAGE, DEFAULT_TARGET_LANGUAGE, LANGUAGE_OPTIONS, isLanguageCode, isValidLanguagePair } from '../languages.js';

afterEach(() => {
  cleanup();
});

function draftWith(overrides: Partial<WizardDraft> = {}): WizardDraft {
  return {
    ...DEFAULT_WIZARD_DRAFT,
    glossary: [...DEFAULT_WIZARD_DRAFT.glossary],
    ...overrides,
  };
}

describe('BasicsStep matrix', () => {
  it('writes name + description with error text (never color-only)', () => {
    const onChange = vi.fn();
    render(
      <BasicsStep draft={draftWith()} errors={{ name: 'Name is required.', description: 'Too long.' }} onChange={onChange} />,
    );
    fireEvent.change(screen.getByTestId('wizard-name'), { target: { value: 'Pilot' } });
    expect(onChange).toHaveBeenCalledWith({ name: 'Pilot' });
    fireEvent.change(screen.getByTestId('wizard-description'), { target: { value: 'Dub pilot' } });
    expect(onChange).toHaveBeenCalledWith({ description: 'Dub pilot' });
    expect(screen.getByTestId('wizard-step-basics').textContent).toContain('Name is required.');
    expect(screen.getByTestId('wizard-duplicate-hint')).toBeDefined();
  });
});

describe('LanguageStep matrix', () => {
  it('writes both languages with immutable notices (text signals)', () => {
    const onChange = vi.fn();
    render(<LanguageStep draft={draftWith()} errors={{}} onChange={onChange} />);
    fireEvent.change(screen.getByTestId('wizard-source'), { target: { value: 'fr' } });
    expect(onChange).toHaveBeenCalledWith({ sourceLanguage: 'fr' });
    fireEvent.change(screen.getByTestId('wizard-target'), { target: { value: 'de' } });
    expect(onChange).toHaveBeenCalledWith({ targetLanguage: 'de' });
    expect(screen.getByTestId('wizard-source-note').textContent?.length).toBeGreaterThan(0);
    expect(screen.getByTestId('wizard-target-help').textContent?.length).toBeGreaterThan(0);
    expect(screen.getByTestId('wizard-immutable-notice')).toBeDefined();
  });
});

describe('SettingsStep matrix', () => {
  function renderSettings(draft: WizardDraft, errors: Record<string, string> = {}) {
    const onChange = vi.fn();
    const onGlossaryChange = vi.fn();
    render(<SettingsStep draft={draft} errors={errors} onChange={onChange} onGlossaryChange={onGlossaryChange} />);
    return { onChange, onGlossaryChange };
  }

  it('writes all four policy selects', () => {
    const { onChange } = renderSettings(draftWith());
    fireEvent.change(screen.getByTestId('wizard-separation'), { target: { value: 'music' } });
    expect(onChange).toHaveBeenCalledWith({ sourceSeparationPolicy: 'music' });
    fireEvent.change(screen.getByTestId('wizard-profile'), { target: { value: 'broadcast' } });
    expect(onChange).toHaveBeenCalledWith({ outputProfile: 'broadcast' });
    fireEvent.change(screen.getByTestId('wizard-timing'), { target: { value: 'strict' } });
    expect(onChange).toHaveBeenCalledWith({ timingStrictness: 'strict' });
    fireEvent.change(screen.getByTestId('wizard-voice'), { target: { value: 'expressive' } });
    expect(onChange).toHaveBeenCalledWith({ voicePolicy: 'expressive' });
  });

  it('parses thresholds with finite fallback and writes style text', () => {
    const { onChange } = renderSettings(draftWith());
    fireEvent.change(screen.getByTestId('wizard-threshold'), { target: { value: '0.85' } });
    expect(onChange).toHaveBeenCalledWith({ reviewThreshold: 0.85 });
    fireEvent.change(screen.getByTestId('wizard-threshold'), { target: { value: 'bogus' } });
    expect(onChange).toHaveBeenCalledWith({ reviewThreshold: 0 });
    fireEvent.change(screen.getByTestId('wizard-style'), { target: { value: 'Keep it punchy.' } });
    expect(onChange).toHaveBeenCalledWith({ styleInstructions: 'Keep it punchy.' });
  });

  it('shows threshold + glossary errors as alert text (recovery: fix fields)', () => {
    renderSettings(draftWith(), { reviewThreshold: 'Out of range.', glossary: 'Too many entries.' });
    expect(screen.getByTestId('wizard-step-settings').textContent).toContain('Out of range.');
    expect(screen.getByTestId('wizard-glossary-error').textContent).toBe('Too many entries.');
    expect(screen.getByTestId('wizard-glossary-error').getAttribute('role')).toBe('alert');
  });

  it('manages glossary rows: empty note, add, edit, per-row errors, remove', () => {
    const { onGlossaryChange } = renderSettings(draftWith({ glossary: [] }));
    expect(screen.getByTestId('wizard-glossary-empty')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-glossary-add'));
    expect(onGlossaryChange).toHaveBeenCalledWith([{ sourceTerm: '', targetTerm: '', notes: '' }]);
    cleanup();
    const filled = renderSettings(
      draftWith({ glossary: [{ sourceTerm: 'Pilot', targetTerm: 'Piloto', notes: 'codename' }] }),
      { 'glossary.0.sourceTerm': 'Required.' },
    );
    expect(screen.queryByTestId('wizard-glossary-empty')).toBeNull();
    expect(screen.getByTestId('wizard-step-settings').textContent).toContain('Required.');
    fireEvent.change(screen.getByTestId('wizard-glossary-source-0'), { target: { value: 'Pilot episode' } });
    expect(filled.onGlossaryChange).toHaveBeenCalledWith([
      { sourceTerm: 'Pilot episode', targetTerm: 'Piloto', notes: 'codename' },
    ]);
    fireEvent.change(screen.getByTestId('wizard-glossary-target-0'), { target: { value: 'Episodio' } });
    expect(filled.onGlossaryChange).toHaveBeenCalledWith([
      { sourceTerm: 'Pilot', targetTerm: 'Episodio', notes: 'codename' },
    ]);
    fireEvent.change(screen.getByTestId('wizard-glossary-notes-0'), { target: { value: 'n' } });
    expect(filled.onGlossaryChange).toHaveBeenCalledWith([
      { sourceTerm: 'Pilot', targetTerm: 'Piloto', notes: 'n' },
    ]);
    fireEvent.click(screen.getByTestId('wizard-glossary-remove-0'));
    expect(filled.onGlossaryChange).toHaveBeenCalledWith([]);
  });
});

describe('UploadStep matrix', () => {
  it('switches modes and selects files with reattach states', () => {
    const onModeChange = vi.fn();
    const onFileSelect = vi.fn();
    const { rerender } = render(
      <UploadStep draft={draftWith({ uploadMode: 'later' })} errors={{}} attachedFileName={null} onModeChange={onModeChange} onFileSelect={onFileSelect} />,
    );
    expect(screen.queryByTestId('wizard-upload-picker')).toBeNull();
    fireEvent.click(screen.getByTestId('wizard-upload-now'));
    expect(onModeChange).toHaveBeenCalledWith('now');
    rerender(
      <UploadStep draft={draftWith({ uploadMode: 'now', uploadFileName: '' })} errors={{ uploadFile: 'Too large.' }} attachedFileName={null} onModeChange={onModeChange} onFileSelect={onFileSelect} />,
    );
    expect(screen.getByTestId('wizard-upload-picker')).toBeDefined();
    const file = new File([new Uint8Array([1, 2])], 'clip.mp4', { type: 'video/mp4' });
    fireEvent.change(screen.getByTestId('wizard-file'), { target: { files: [file] } });
    expect(onFileSelect).toHaveBeenCalledTimes(1);
    expect(onFileSelect.mock.calls[0]?.[0]).toBe(file);
    fireEvent.change(screen.getByTestId('wizard-file'), { target: { files: [] } });
    expect(onFileSelect).toHaveBeenCalledWith(null);
    fireEvent.click(screen.getByTestId('wizard-upload-later'));
    expect(onModeChange).toHaveBeenCalledWith('later');
  });

  it('shows attached vs reattach prompts distinctly (text signals)', () => {
    const onModeChange = vi.fn();
    const onFileSelect = vi.fn();
    const { rerender } = render(
      <UploadStep draft={draftWith({ uploadMode: 'now', uploadFileName: '' })} errors={{}} attachedFileName="clip.mp4" onModeChange={onModeChange} onFileSelect={onFileSelect} />,
    );
    expect(screen.getByTestId('wizard-file-attached').textContent).toBe('clip.mp4');
    rerender(
      <UploadStep draft={draftWith({ uploadMode: 'now', uploadFileName: 'clip.mp4' })} errors={{}} attachedFileName={null} onModeChange={onModeChange} onFileSelect={onFileSelect} />,
    );
    expect(screen.getByTestId('wizard-file-reattach')).toBeDefined();
  });
});

describe('ReviewStep matrix', () => {
  function renderReview(overrides: Partial<React.ComponentProps<typeof ReviewStep>> = {}) {
    const onEditStep = vi.fn();
    render(
      <ReviewStep
        draft={draftWith({ glossary: [{ sourceTerm: 'Pilot', targetTerm: 'Piloto', notes: '' }] })}
        serverFields={{ name: 'Taken.' }}
        serverForm={null}
        onEditStep={onEditStep}
        {...overrides}
      />,
    );
    return onEditStep;
  }

  it('summarizes every row with hash preview and edit links', () => {
    const onEditStep = renderReview({ draft: draftWith({ name: 'Pilot', glossary: [{ sourceTerm: 'Pilot', targetTerm: 'Piloto', notes: '' }] }) });
    expect(screen.getByTestId('wizard-summary')).toBeDefined();
    expect(screen.getByTestId('wizard-summary').textContent).toContain('Pilot');
    expect(screen.getByTestId('wizard-config-version').textContent).toContain('1');
    expect(screen.getByTestId('wizard-config-hash').textContent?.length).toBeGreaterThan(0);
    expect(screen.getByTestId('wizard-hash-note')).toBeDefined();
    expect(screen.getByTestId('wizard-immutable-repeat')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-edit-basics'));
    expect(onEditStep).toHaveBeenCalledWith('basics');
  });

  it('renders server errors with field errors preserved (recovery: edit + resubmit)', () => {
    renderReview({ serverForm: 'Server exploded. Ref: corr-1.' });
    expect(screen.getByTestId('wizard-server-error').textContent).toContain('Ref: corr-1.');
    expect(screen.getByTestId('wizard-step-review').textContent).toContain('Taken.');
  });
});

describe('draft helper sweep', () => {
  it('classifies wizard steps with guards', () => {
    expect(isWizardStep('basics')).toBe(true);
    expect(isWizardStep('review')).toBe(true);
    expect(isWizardStep('bogus')).toBe(false);
    expect(isWizardStep(null)).toBe(false);
  });

  it('builds settings, payloads, summaries, and hashes deterministically', () => {
    const draft = draftWith({ name: 'Pilot', sourceLanguage: 'en', targetLanguage: 'es' });
    const settings = buildProcessingSettings(draft);
    expect(typeof settings).toBe('object');
    expect(Object.keys(buildProcessingSettingsJson(draft) === '' ? {} : JSON.parse(buildProcessingSettingsJson(draft)))).toContain('outputProfile');
    expect(previewConfigHash(draft).length).toBeGreaterThan(0);
    expect(previewConfigHash(draft)).toBe(previewConfigHash(draftWith({ name: 'Pilot', sourceLanguage: 'en', targetLanguage: 'es' })));
    const summary = buildHumanSummary(draft);
    expect(summary.length).toBeGreaterThan(0);
    expect(summary.some((row) => String(row.value).includes('Pilot'))).toBe(true);
    const payload = buildCreatePayload(draft);
    expect(payload).toBeDefined();
  });
});

describe('languages helper sweep', () => {
  it('curates options with defaults and validates pairs', () => {
    expect(LANGUAGE_OPTIONS.length).toBeGreaterThan(5);
    expect(LANGUAGE_OPTIONS.some((option) => option.value === 'es')).toBe(true);
    expect(DEFAULT_SOURCE_LANGUAGE).toBe('en');
    expect(DEFAULT_TARGET_LANGUAGE).toBe('es');
    expect(isLanguageCode('en')).toBe(true);
    expect(isLanguageCode('e')).toBe(false);
    expect(isLanguageCode('')).toBe(false);
    expect(isValidLanguagePair('en', 'es')).toBe(true);
    expect(isValidLanguagePair('en', 'en')).toBe(false);
    expect(isValidLanguagePair('', 'es')).toBe(false);
  });
});
