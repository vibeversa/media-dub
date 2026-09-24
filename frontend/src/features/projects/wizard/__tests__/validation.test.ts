import { describe, expect, it } from 'vitest';
import { validateBasics, validateLanguages, validateSettings } from '../validation.js';

describe('basics validation (R1: name required)', () => {
  it('accepts a trimmed name with optional description', () => {
    expect(validateBasics({ name: 'Pilot episode', description: '' })).toEqual({});
  });

  it('rejects blank names', () => {
    expect(validateBasics({ name: '', description: '' })).toHaveProperty('name');
    expect(validateBasics({ name: '   ', description: '' })).toHaveProperty('name');
  });

  it('rejects overlong names and descriptions', () => {
    expect(validateBasics({ name: 'x'.repeat(201), description: '' })).toHaveProperty('name');
    expect(validateBasics({ name: 'ok', description: 'y'.repeat(2001) })).toHaveProperty('description');
  });

  it('accepts boundary lengths', () => {
    expect(validateBasics({ name: 'x'.repeat(200), description: 'y'.repeat(2000) })).toEqual({});
  });
});

describe('language validation', () => {
  it('accepts distinct 2–3 letter codes', () => {
    expect(validateLanguages({ sourceLanguage: 'en', targetLanguage: 'es' })).toEqual({});
  });

  it('rejects identical codes case-insensitively', () => {
    const errors = validateLanguages({ sourceLanguage: 'en', targetLanguage: 'EN' });
    expect(errors['targetLanguage']).toBeDefined();
  });

  it('rejects malformed codes', () => {
    expect(validateLanguages({ sourceLanguage: 'e', targetLanguage: 'es' })).toHaveProperty('sourceLanguage');
    expect(validateLanguages({ sourceLanguage: 'engl', targetLanguage: 'es' })).toHaveProperty('sourceLanguage');
    expect(validateLanguages({ sourceLanguage: 'e1', targetLanguage: 'es' })).toHaveProperty('sourceLanguage');
    expect(validateLanguages({ sourceLanguage: '', targetLanguage: '' })).toHaveProperty('sourceLanguage');
  });
});

describe('settings validation (v1 shape)', () => {
  const valid = {
    sourceSeparationPolicy: 'auto',
    outputProfile: 'standard',
    timingStrictness: 'balanced',
    voicePolicy: 'matched',
    reviewThreshold: 0.7,
    glossary: [],
    styleInstructions: '',
  };

  it('accepts defaults', () => {
    expect(validateSettings(valid)).toEqual({});
  });

  it('rejects out-of-range thresholds', () => {
    expect(validateSettings({ ...valid, reviewThreshold: -0.1 })).toHaveProperty('reviewThreshold');
    expect(validateSettings({ ...valid, reviewThreshold: 1.1 })).toHaveProperty('reviewThreshold');
    expect(validateSettings({ ...valid, reviewThreshold: 0 })).toEqual({});
    expect(validateSettings({ ...valid, reviewThreshold: 1 })).toEqual({});
  });

  it('rejects overlong policy strings', () => {
    expect(validateSettings({ ...valid, voicePolicy: 'v'.repeat(65) })).toHaveProperty('voicePolicy');
  });

  it('validates glossary rows with indexed paths', () => {
    const errors = validateSettings({
      ...valid,
      glossary: [{ sourceTerm: '', targetTerm: 'x', notes: '' }],
    });
    expect(errors['glossary.0.sourceTerm']).toBeDefined();
  });

  it('rejects overlong style instructions', () => {
    expect(validateSettings({ ...valid, styleInstructions: 's'.repeat(4001) })).toHaveProperty(
      'styleInstructions',
    );
  });
});
