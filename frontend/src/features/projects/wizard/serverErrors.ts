import type { FieldErrors } from './validation.js';

/**
 * Server-failure → wizard-field mapping (Task 022, R1 companion).
 *
 * The error envelope carries field problems in `details` and a human message
 * plus `correlationId` at the top level. Known detail keys land on their
 * step fields; anything else becomes a form-level message that keeps the
 * correlation id for support. The draft is never touched here — failures
 * preserve state by design. Pure: no React, no storage.
 */

export interface MappedServerErrors {
  readonly fields: FieldErrors;
  readonly form: string | null;
}

const BASICS_KEYS = new Set(['name', 'description']);

const LANGUAGE_KEYS = new Set(['sourcelanguage', 'targetlanguage', 'source', 'target']);

const SETTINGS_KEYS = new Set([
  'processingsettings',
  'settings',
  'schemaversion',
  'sourceseparationpolicy',
  'outputprofile',
  'timingstrictness',
  'voicepolicy',
  'reviewthreshold',
  'glossary',
  'styleinstructions',
]);

function fieldForKey(detailKey: string): string | null {
  const normalized = detailKey.trim().toLowerCase();
  if (BASICS_KEYS.has(normalized)) {
    return normalized === 'description' ? 'description' : 'name';
  }
  if (LANGUAGE_KEYS.has(normalized)) {
    return normalized === 'sourcelanguage' || normalized === 'source' ? 'sourceLanguage' : 'targetLanguage';
  }
  if (SETTINGS_KEYS.has(normalized)) {
    return 'settings';
  }
  return null;
}

function detailMessage(value: unknown, fallback: string): string {
  if (typeof value === 'string' && value.trim() !== '') {
    return value.trim().slice(0, 500);
  }
  return fallback;
}

/**
 * Maps an `AppError` (message + details + correlation id) onto wizard
 * fields. Detail keys win; when details carry nothing field-shaped, the
 * message itself is inspected for the two language-mismatch wordings before
 * falling back to a form-level error.
 */
export function mapServerErrors(
  details: Record<string, unknown> | undefined,
  message: string,
  correlationId: string,
): MappedServerErrors {
  const fields: Record<string, string> = {};
  if (details !== undefined) {
    for (const [key, value] of Object.entries(details)) {
      const field = fieldForKey(key);
      if (field !== null && fields[field] === undefined) {
        fields[field] = detailMessage(value, message);
      }
    }
  }
  if (Object.keys(fields).length > 0) {
    return { fields, form: null };
  }
  const lowered = message.toLowerCase();
  if (lowered.includes('sourcelanguage') && lowered.includes('targetlanguage')) {
    return { fields: { targetLanguage: message }, form: null };
  }
  if (lowered.includes('name must be')) {
    return { fields: { name: message }, form: null };
  }
  const suffix = correlationId === '' ? '' : ` (ref ${correlationId})`;
  return { fields: {}, form: `${message}${suffix}` };
}
