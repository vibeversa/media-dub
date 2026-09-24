import { describe, expect, it } from 'vitest';
import { mapServerErrors } from '../serverErrors.js';

describe('mapServerErrors (R1: server 400 → fields)', () => {
  it('maps detail keys onto wizard fields', () => {
    const mapped = mapServerErrors({ Name: 'Name must be 1..200 chars.' }, 'Name failed.', 'corr-1');
    expect(mapped.fields['name']).toContain('1..200');
    expect(mapped.form).toBeNull();
  });

  it('maps language detail keys case-insensitively', () => {
    const mapped = mapServerErrors({ targetlanguage: 'bad code' }, 'Language failed.', 'corr-2');
    expect(mapped.fields['targetLanguage']).toBe('bad code');
  });

  it('folds processing-settings keys into the settings bucket', () => {
    const mapped = mapServerErrors({ reviewThreshold: 'out of range' }, 'Settings failed.', 'corr-3');
    expect(mapped.fields['settings']).toBe('out of range');
  });

  it('detects the language-pair message without details', () => {
    const mapped = mapServerErrors(
      {},
      'SourceLanguage and TargetLanguage must differ.',
      'corr-4',
    );
    expect(mapped.fields['targetLanguage']).toContain('must differ');
    expect(mapped.form).toBeNull();
  });

  it('falls back to a form error suffixed with the correlation ref', () => {
    const mapped = mapServerErrors({}, 'Something broke.', 'corr-9');
    expect(mapped.fields).toEqual({});
    expect(mapped.form).toBe('Something broke. (ref corr-9)');
  });

  it('omits the ref suffix when no correlation id exists', () => {
    const mapped = mapServerErrors(undefined, 'Something broke.', '');
    expect(mapped.form).toBe('Something broke.');
  });
});
