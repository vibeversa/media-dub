import { create } from 'zustand';
import { DEFAULT_WIZARD_DRAFT, WIZARD_STORAGE_KEY, isWizardStep } from './draft.js';
import type { UploadMode, WizardDraft, WizardGlossaryEntry, WizardStep } from './draft.js';

/**
 * Wizard draft store (Task 022, R3).
 *
 * - Draft + current step persist to `localStorage` on every change and are
 *   restored on load, so a refresh or a login redirect (session expiry
 *   mid-wizard) never loses work: the user resumes where they left off.
 * - Only plain JSON is persisted (the `WizardDraft` shape). The selected
 *   `File` lives in memory only (`uploadFile`) alongside persisted metadata
 *   (`uploadFileName/Size/Type`); after a refresh the file must be
 *   re-attached while every other field survives.
 * - Cleared only on successful submit or explicit discard. Server failures
 *   never clear (R1: draft intact on failure).
 * - Stored payloads are sanitized on load: hand-edited or older-schema
 *   values fall back to defaults field-by-field, never crashing the wizard.
 */

export interface PersistedWizard {
  readonly draft: WizardDraft;
  readonly step: WizardStep;
}

interface WizardState extends PersistedWizard {
  /** In-memory only. Never written to storage (R3: re-attach after refresh). */
  readonly uploadFile: File | null;
  readonly updateDraft: (patch: Partial<WizardDraft>) => void;
  readonly setStep: (step: WizardStep) => void;
  readonly replaceGlossary: (glossary: readonly WizardGlossaryEntry[]) => void;
  readonly setUploadFile: (file: File | null) => void;
  /** Clears memory and storage after submit or discard. */
  readonly clearWizard: () => void;
  /** Test-only reset (memory + storage). Never used in production code. */
  readonly resetWizardForTests: () => void;
}

function asString(value: unknown, max: number): string {
  if (typeof value !== 'string') {
    return '';
  }
  return value.slice(0, max);
}

function asNumber(value: unknown, fallback: number, min: number, max: number): number {
  if (typeof value !== 'number' || !Number.isFinite(value)) {
    return fallback;
  }
  return Math.min(max, Math.max(min, value));
}

function sanitizeGlossary(value: unknown): readonly WizardGlossaryEntry[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const entries: WizardGlossaryEntry[] = [];
  for (const raw of value.slice(0, 1000)) {
    if (typeof raw !== 'object' || raw === null) {
      continue;
    }
    const record = raw as Record<string, unknown>;
    const sourceTerm = asString(record['sourceTerm'], 256);
    const targetTerm = asString(record['targetTerm'], 256);
    if (sourceTerm.trim() === '' || targetTerm.trim() === '') {
      continue;
    }
    entries.push({ sourceTerm, targetTerm, notes: asString(record['notes'], 1024) });
  }
  return entries;
}

function sanitizeUploadMode(value: unknown): UploadMode {
  return value === 'now' ? 'now' : 'later';
}

/** Field-by-field sanitizer for stored drafts (unknown schema → defaults). */
export function sanitizeDraft(raw: unknown): WizardDraft {
  if (typeof raw !== 'object' || raw === null) {
    return { ...DEFAULT_WIZARD_DRAFT };
  }
  const record = raw as Record<string, unknown>;
  return {
    name: asString(record['name'], 200),
    description: asString(record['description'], 2000),
    sourceLanguage: asString(record['sourceLanguage'], 3) === '' ? 'en' : asString(record['sourceLanguage'], 3),
    targetLanguage: asString(record['targetLanguage'], 3) === '' ? 'es' : asString(record['targetLanguage'], 3),
    sourceSeparationPolicy: asString(record['sourceSeparationPolicy'], 64),
    outputProfile: asString(record['outputProfile'], 64),
    timingStrictness: asString(record['timingStrictness'], 64),
    voicePolicy: asString(record['voicePolicy'], 64),
    reviewThreshold: asNumber(record['reviewThreshold'], 0.7, 0, 1),
    glossary: sanitizeGlossary(record['glossary']),
    styleInstructions: asString(record['styleInstructions'], 4000),
    uploadMode: sanitizeUploadMode(record['uploadMode']),
    uploadFileName: asString(record['uploadFileName'], 256),
    uploadFileSize: asNumber(record['uploadFileSize'], 0, 0, Number.MAX_SAFE_INTEGER),
    uploadFileType: asString(record['uploadFileType'], 128),
  };
}

/** Reads the persisted wizard state; defaults when absent or invalid. */
export function loadPersistedWizard(): PersistedWizard {
  const fallback: PersistedWizard = { draft: { ...DEFAULT_WIZARD_DRAFT }, step: 'basics' };
  try {
    const raw = window.localStorage.getItem(WIZARD_STORAGE_KEY);
    if (raw === null || raw === '') {
      return fallback;
    }
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== 'object' || parsed === null) {
      return fallback;
    }
    const record = parsed as Record<string, unknown>;
    return {
      draft: sanitizeDraft(record['draft']),
      step: isWizardStep(record['step']) ? record['step'] : 'basics',
    };
  } catch {
    return fallback;
  }
}

function persist(draft: WizardDraft, step: WizardStep): void {
  try {
    window.localStorage.setItem(WIZARD_STORAGE_KEY, JSON.stringify({ draft, step }));
  } catch {
    // Storage full or unavailable: the draft stays in memory only.
  }
}

function unpersist(): void {
  try {
    window.localStorage.removeItem(WIZARD_STORAGE_KEY);
  } catch {
    // Already gone or unavailable; memory state is still reset below.
  }
}

const initial = loadPersistedWizard();

export const useWizardStore = create<WizardState>()((set) => ({
  draft: initial.draft,
  step: initial.step,
  uploadFile: null,
  updateDraft: (patch) => {
    set((state) => {
      const next: WizardDraft = { ...state.draft, ...patch };
      persist(next, state.step);
      return { draft: next };
    });
  },
  setStep: (step) => {
    set((state) => {
      persist(state.draft, step);
      return { step };
    });
  },
  replaceGlossary: (glossary) => {
    set((state) => {
      const next: WizardDraft = { ...state.draft, glossary: [...glossary] };
      persist(next, state.step);
      return { draft: next };
    });
  },
  setUploadFile: (file) => {
    set((state) => {
      const next: WizardDraft =
        file === null
          ? { ...state.draft, uploadFileName: '', uploadFileSize: 0, uploadFileType: '' }
          : {
              ...state.draft,
              uploadMode: 'now',
              uploadFileName: file.name.slice(0, 256),
              uploadFileSize: file.size,
              uploadFileType: (file.type ?? '').slice(0, 128),
            };
      persist(next, state.step);
      return { draft: next, uploadFile: file };
    });
  },
  clearWizard: () => {
    unpersist();
    set({ draft: { ...DEFAULT_WIZARD_DRAFT }, step: 'basics', uploadFile: null });
  },
  resetWizardForTests: () => {
    unpersist();
    set({ draft: { ...DEFAULT_WIZARD_DRAFT }, step: 'basics', uploadFile: null });
  },
}));
