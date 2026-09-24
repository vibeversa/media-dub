import type { ProjectCreateRequest } from '../../../api/client/index.js';
import { DEFAULT_SOURCE_LANGUAGE, DEFAULT_TARGET_LANGUAGE } from './languages.js';

/**
 * Wizard draft shapes and builders (Task 022).
 *
 * Everything here is plain JSON: the store persists exactly this shape to
 * `localStorage`, so no `File`, token, or media bytes can leak into it by
 * construction. Builders derive the wire payload, the canonical
 * processing-settings JSON, a client-side hash preview, and the human summary
 * for `ReviewStep`. Pure: no React, no storage, fully unit-testable.
 */

export const WIZARD_STORAGE_KEY = 'dubbing.createWizard.v1';

/** New projects start at settings version 1 (server-assigned on create). */
export const SETTINGS_VERSION_NEW = 1;

export type WizardStep = 'basics' | 'language' | 'settings' | 'upload' | 'review';

export const WIZARD_STEPS: readonly WizardStep[] = ['basics', 'language', 'settings', 'upload', 'review'];

export type UploadMode = 'later' | 'now';

export interface WizardGlossaryEntry {
  readonly sourceTerm: string;
  readonly targetTerm: string;
  readonly notes: string;
}

export interface WizardDraft {
  readonly name: string;
  readonly description: string;
  readonly sourceLanguage: string;
  readonly targetLanguage: string;
  readonly sourceSeparationPolicy: string;
  readonly outputProfile: string;
  readonly timingStrictness: string;
  readonly voicePolicy: string;
  readonly reviewThreshold: number;
  readonly glossary: readonly WizardGlossaryEntry[];
  readonly styleInstructions: string;
  readonly uploadMode: UploadMode;
  /** File metadata only (name/size/type); the bytes never enter the draft. */
  readonly uploadFileName: string;
  readonly uploadFileSize: number;
  readonly uploadFileType: string;
}

export const DEFAULT_WIZARD_DRAFT: WizardDraft = {
  name: '',
  description: '',
  sourceLanguage: DEFAULT_SOURCE_LANGUAGE,
  targetLanguage: DEFAULT_TARGET_LANGUAGE,
  sourceSeparationPolicy: 'auto',
  outputProfile: 'standard',
  timingStrictness: 'balanced',
  voicePolicy: 'matched',
  reviewThreshold: 0.7,
  glossary: [],
  styleInstructions: '',
  uploadMode: 'later',
  uploadFileName: '',
  uploadFileSize: 0,
  uploadFileType: '',
};

export function isWizardStep(value: unknown): value is WizardStep {
  return (
    value === 'basics' ||
    value === 'language' ||
    value === 'settings' ||
    value === 'upload' ||
    value === 'review'
  );
}

function stableStringify(value: unknown): string {
  if (value === null || value === undefined) {
    return 'null';
  }
  if (Array.isArray(value)) {
    return `[${value.map((entry) => stableStringify(entry)).join(',')}]`;
  }
  if (typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, entry]) => entry !== undefined && entry !== '')
      .sort(([a], [b]) => (a < b ? -1 : a > b ? 1 : 0));
    return `{${entries.map(([key, entry]) => `${JSON.stringify(key)}:${stableStringify(entry)}`).join(',')}}`;
  }
  return JSON.stringify(value) ?? 'null';
}

/**
 * Canonical processing-settings object: always `schemaVersion: 1`, plus only
 * the refinements the user kept. Empty strings, empty glossary, and blank
 * style instructions are omitted so the server sees the same minimal shape it
 * would for defaults.
 */
export function buildProcessingSettings(draft: WizardDraft): Record<string, unknown> {
  const settings: Record<string, unknown> = { schemaVersion: 1 };
  if (draft.sourceSeparationPolicy.trim() !== '') {
    settings['sourceSeparationPolicy'] = draft.sourceSeparationPolicy.trim();
  }
  if (draft.outputProfile.trim() !== '') {
    settings['outputProfile'] = draft.outputProfile.trim();
  }
  if (draft.timingStrictness.trim() !== '') {
    settings['timingStrictness'] = draft.timingStrictness.trim();
  }
  if (draft.voicePolicy.trim() !== '') {
    settings['voicePolicy'] = draft.voicePolicy.trim();
  }
  settings['reviewThreshold'] = draft.reviewThreshold;
  const glossary = draft.glossary
    .filter((entry) => entry.sourceTerm.trim() !== '' && entry.targetTerm.trim() !== '')
    .map((entry) => ({
      sourceTerm: entry.sourceTerm.trim(),
      targetTerm: entry.targetTerm.trim(),
      ...(entry.notes.trim() !== '' ? { notes: entry.notes.trim().slice(0, 1024) } : {}),
    }));
  if (glossary.length > 0) {
    settings['glossary'] = glossary;
  }
  if (draft.styleInstructions.trim() !== '') {
    settings['styleInstructions'] = draft.styleInstructions.trim().slice(0, 4000);
  }
  return settings;
}

/** Stable JSON for the canonical processing settings (hash input + wire shape). */
export function buildProcessingSettingsJson(draft: WizardDraft): string {
  return stableStringify(buildProcessingSettings(draft));
}

/**
 * Client-side hash preview (FNV-1a, 8 lowercase hex chars) over
 * source/target plus the canonical processing-settings JSON.
 *
 * This is a preview only: the server computes the authoritative
 * configuration hash on create (SHA-256 over the same dimensions) and
 * returns it on the created project. Displayed with that caveat, never as
 * the final value. Deterministic and dependency-free so unit tests stay
 * hermetic (no `SubtleCrypto` async surface).
 */
export function previewConfigHash(draft: WizardDraft): string {
  const input = `${draft.sourceLanguage.trim()}|${draft.targetLanguage.trim()}|${buildProcessingSettingsJson(draft)}`;
  let hash = 0x811c9dc5;
  for (let i = 0; i < input.length; i += 1) {
    hash ^= input.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return (hash >>> 0).toString(16).padStart(8, '0');
}

export interface SummaryRow {
  /** Stable id; the component resolves the label via i18n. */
  readonly id: string;
  /** Plain-text value (user data, rendered as text only). */
  readonly value: string;
}

/**
 * Human-readable summary rows for `ReviewStep` (R4 companion to the hash
 * preview). Values are raw user text; the component translates labels and
 * renders values as plain text.
 */
export function buildHumanSummary(draft: WizardDraft): readonly SummaryRow[] {
  const rows: SummaryRow[] = [
    { id: 'name', value: draft.name.trim() === '' ? '—' : draft.name.trim() },
    { id: 'source', value: draft.sourceLanguage.trim() === '' ? '—' : draft.sourceLanguage.trim() },
    { id: 'target', value: draft.targetLanguage.trim() === '' ? '—' : draft.targetLanguage.trim() },
    { id: 'separation', value: draft.sourceSeparationPolicy.trim() === '' ? '—' : draft.sourceSeparationPolicy.trim() },
    { id: 'profile', value: draft.outputProfile.trim() === '' ? '—' : draft.outputProfile.trim() },
    { id: 'timing', value: draft.timingStrictness.trim() === '' ? '—' : draft.timingStrictness.trim() },
    { id: 'voice', value: draft.voicePolicy.trim() === '' ? '—' : draft.voicePolicy.trim() },
    { id: 'threshold', value: String(draft.reviewThreshold) },
    { id: 'glossary', value: String(draft.glossary.length) },
    {
      id: 'style',
      value: draft.styleInstructions.trim() === '' ? '—' : `${draft.styleInstructions.trim().length}`,
    },
    {
      id: 'upload',
      value:
        draft.uploadMode === 'now'
          ? draft.uploadFileName === ''
            ? 'now-missing'
            : draft.uploadFileName
          : 'later',
    },
  ];
  if (draft.description.trim() !== '') {
    rows.splice(1, 0, { id: 'description', value: draft.description.trim() });
  }
  return rows;
}

/**
 * Wire body for `POST /projects`.
 *
 * The versioned bundle types `ProjectCreateRequest` as
 * `{ name?, sourceLanguage, targetLanguage }`; the Task 007 controller
 * additionally accepts `description` and `processingSettings` (folded into
 * the server config hash). Those extras travel via a narrow cast at this
 * single call site — the same pattern Task 021 uses for the list filters —
 * while every other line uses generated types exactly.
 */
export function buildCreatePayload(draft: WizardDraft): ProjectCreateRequest {
  const base: ProjectCreateRequest = {
    name: draft.name.trim(),
    sourceLanguage: draft.sourceLanguage.trim(),
    targetLanguage: draft.targetLanguage.trim(),
  };
  const wire = {
    ...base,
    ...(draft.description.trim() !== '' ? { description: draft.description.trim().slice(0, 2000) } : {}),
    processingSettings: buildProcessingSettings(draft),
  };
  return wire as unknown as ProjectCreateRequest;
}
