/**
 * Translation domain view (Task 028).
 *
 * Pure parsing + derivation over the Task 009 segment contract. All shapes
 * are parsed defensively (camelCase/PascalCase, missing sections fall back
 * to neutral defaults, never throw for list rows). Texts render as plain
 * text only — never HTML — and provider/model metadata is display-only
 * (never secrets, tokens, or internal paths).
 *
 * Source text is always the currently-selected transcript version (with a
 * version label); translation candidates stay immutable snapshots — edits
 * create manual versions only.
 */

export const TRANSLATION_PAGE_SIZE = 200;

export const TRANSLATION_MAX_PAGES = 10;

export const MANUAL_PROVIDER = 'manual';

/** Characters-per-second above which the dub window overflows (read-only hint). */
export const SYNC_OVERFLOW_CPS = 20;

export interface TranslationVersionView {
  readonly id: string;
  readonly text: string;
  readonly provider: string;
  readonly model: string;
  readonly isSelected: boolean;
  readonly isManual: boolean;
  readonly score: number | undefined;
  readonly createdAt: string;
  /** 1-based position in creation order (v1 = first candidate). */
  readonly versionNumber: number;
}

export interface GlossaryHit {
  readonly term: string;
  readonly definition: string | undefined;
}

export interface AssignedVoiceView {
  readonly voiceId: string;
  readonly label: string;
}

export interface TranslationSegmentView {
  readonly id: string;
  readonly sequence: number;
  readonly startMs: number;
  readonly endMs: number;
  readonly speakerId: string | undefined;
  readonly speakerLabel: string;
  /** Shared optimistic-concurrency pointer (transcript + translation). */
  readonly selectionVersion: number;
  readonly reviewStatus: string | undefined;
  readonly qualityCodes: readonly string[];
  readonly syncStatus: string | undefined;
  /** Currently-selected transcript text (read-only source). */
  readonly sourceText: string;
  readonly sourceVersionId: string | undefined;
  readonly sourceVersionLabel: string;
  /** Currently-selected translation text. */
  readonly selectedText: string;
  readonly selectedVersionId: string | undefined;
  readonly manualVersionId: string | undefined;
  readonly versions: readonly TranslationVersionView[];
  readonly glossaryHits: readonly GlossaryHit[];
  readonly assignedVoice: AssignedVoiceView | undefined;
  readonly hasCandidates: boolean;
}

function toRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function pick(record: Record<string, unknown> | undefined, ...keys: readonly string[]): unknown {
  if (record === undefined) {
    return undefined;
  }
  for (const key of keys) {
    const value = record[key];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toStringArray(value: unknown): string[] {
  if (!Array.isArray(value)) {
    return [];
  }
  return (value as unknown[]).filter((entry): entry is string => typeof entry === 'string' && entry !== '');
}

function isManualProvider(provider: string): boolean {
  return provider.trim().toLowerCase() === MANUAL_PROVIDER;
}

function parseScore(record: Record<string, unknown>): number | undefined {
  const raw = pick(record, 'score', 'Score', 'qualityScore', 'QualityScore', 'confidence', 'Confidence');
  if (typeof raw === 'number' && Number.isFinite(raw)) {
    return raw;
  }
  return undefined;
}

function parseTranslationText(record: Record<string, unknown>): string {
  const direct = pick(
    record,
    'text',
    'Text',
    'primaryText',
    'PrimaryText',
    'translationText',
    'TranslationText',
    'selectedText',
    'SelectedText',
  );
  return typeof direct === 'string' ? direct : '';
}

/** Parses one translation candidate row (detail `translationVersions[]`). */
export function parseTranslationVersion(raw: unknown, index: number): TranslationVersionView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
  if (id === '') {
    return undefined;
  }
  const provider = toNonEmptyString(pick(record, 'provider', 'Provider')) ?? 'unknown';
  const model = toNonEmptyString(pick(record, 'model', 'Model')) ?? 'unknown';
  return {
    id,
    text: parseTranslationText(record),
    provider,
    model,
    isSelected: pick(record, 'isSelected', 'IsSelected') === true,
    isManual: isManualProvider(provider),
    score: parseScore(record),
    createdAt: toNonEmptyString(pick(record, 'createdAt', 'CreatedAt')) ?? '',
    versionNumber: index + 1,
  };
}

function parseTranslationVersions(raw: unknown): TranslationVersionView[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const out: TranslationVersionView[] = [];
  let number = 0;
  for (const entry of raw as unknown[]) {
    const parsed = parseTranslationVersion(entry, number);
    if (parsed !== undefined) {
      out.push(parsed);
      number += 1;
    }
  }
  return out;
}

interface TranscriptSource {
  readonly text: string;
  readonly versionId: string | undefined;
  readonly versionLabel: string;
}

function parseTranscriptSource(record: Record<string, unknown> | undefined): TranscriptSource {
  if (record === undefined) {
    return { text: '', versionId: undefined, versionLabel: 'transcript —' };
  }
  const rawVersions = pick(record, 'transcriptVersions', 'TranscriptVersions');
  const versions: Array<{ id: string; text: string; isSelected: boolean }> = [];
  if (Array.isArray(rawVersions)) {
    let number = 0;
    for (const entry of rawVersions as unknown[]) {
      const entryRecord = toRecord(entry);
      if (entryRecord === undefined) {
        continue;
      }
      const id = toNonEmptyString(pick(entryRecord, 'id', 'Id'));
      if (id === undefined) {
        continue;
      }
      number += 1;
      const textValue = pick(entryRecord, 'text', 'Text');
      versions.push({
        id,
        text: typeof textValue === 'string' ? textValue : '',
        isSelected: pick(entryRecord, 'isSelected', 'IsSelected') === true,
      });
      void number;
    }
  }
  const explicitSelectedId =
    toNonEmptyString(pick(record, 'selectedTranscriptVersionId', 'SelectedTranscriptVersionId')) ??
    toNonEmptyString(pick(record, 'selectedVersionId', 'SelectedVersionId'));
  const selected =
    explicitSelectedId !== undefined
      ? versions.find((v) => v.id === explicitSelectedId) ?? versions.find((v) => v.isSelected)
      : versions.find((v) => v.isSelected);
  if (selected !== undefined) {
    const position = versions.findIndex((v) => v.id === selected.id);
    return {
      text: selected.text,
      versionId: selected.id,
      versionLabel: position >= 0 ? `transcript v${String(position + 1)}` : 'transcript selected',
    };
  }
  if (versions.length > 0) {
    const fallback = versions[versions.length - 1];
    if (fallback !== undefined) {
      return {
        text: fallback.text,
        versionId: fallback.id,
        versionLabel: `transcript v${String(versions.length)}`,
      };
    }
  }
  const direct = pick(
    record,
    'transcriptText',
    'TranscriptText',
    'sourceText',
    'SourceText',
    'text',
    'Text',
  );
  if (typeof direct === 'string' && direct !== '') {
    return { text: direct, versionId: explicitSelectedId, versionLabel: 'transcript selected' };
  }
  return { text: '', versionId: explicitSelectedId, versionLabel: 'transcript —' };
}

function parseGlossaryHits(raw: unknown): GlossaryHit[] {
  if (!Array.isArray(raw)) {
    // Object map form: { term: definition }.
    const record = toRecord(raw);
    if (record === undefined) {
      return [];
    }
    const out: GlossaryHit[] = [];
    for (const [term, definition] of Object.entries(record)) {
      if (term === '') {
        continue;
      }
      out.push({
        term,
        definition: typeof definition === 'string' && definition !== '' ? definition : undefined,
      });
    }
    return out;
  }
  const out: GlossaryHit[] = [];
  for (const entry of raw as unknown[]) {
    if (typeof entry === 'string') {
      if (entry !== '') {
        out.push({ term: entry, definition: undefined });
      }
      continue;
    }
    const record = toRecord(entry);
    if (record === undefined) {
      continue;
    }
    const term =
      toNonEmptyString(pick(record, 'term', 'Term', 'sourceTerm', 'SourceTerm', 'targetTerm', 'TargetTerm')) ?? '';
    if (term === '') {
      continue;
    }
    const definition = toNonEmptyString(pick(record, 'definition', 'Definition', 'notes', 'Notes', 'target', 'Target'));
    out.push({ term, definition });
  }
  return out;
}

function parseAssignedVoice(raw: unknown): AssignedVoiceView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const voiceId =
    toNonEmptyString(pick(record, 'voiceId', 'VoiceId', 'voiceProfileId', 'VoiceProfileId', 'id', 'Id')) ?? '';
  if (voiceId === '') {
    return undefined;
  }
  const label =
    toNonEmptyString(
      pick(record, 'label', 'Label', 'name', 'Name', 'displayName', 'DisplayName', 'voiceName', 'VoiceName'),
    ) ?? voiceId;
  return { voiceId, label };
}

function deriveSelectedText(
  record: Record<string, unknown>,
  versions: readonly TranslationVersionView[],
): { text: string; selectedId: string | undefined } {
  const explicit =
    toNonEmptyString(pick(record, 'selectedTranslationVersionId', 'SelectedTranslationVersionId')) ??
    toNonEmptyString(pick(record, 'selectedVersionId', 'SelectedVersionId'));
  if (explicit !== undefined) {
    const match = versions.find((v) => v.id === explicit);
    if (match !== undefined) {
      return { text: match.text, selectedId: match.id };
    }
  }
  const flagged = versions.find((v) => v.isSelected);
  if (flagged !== undefined) {
    return { text: flagged.text, selectedId: flagged.id };
  }
  const direct = pick(record, 'translationText', 'TranslationText', 'selectedTranslationText', 'SelectedTranslationText');
  if (typeof direct === 'string' && direct !== '') {
    return { text: direct, selectedId: explicit };
  }
  if (versions.length > 0) {
    const last = versions[versions.length - 1];
    if (last !== undefined) {
      return { text: last.text, selectedId: last.id };
    }
  }
  return { text: '', selectedId: explicit };
}

/**
 * Builds a translation segment view from a list-summary row and/or a detail
 * row. Both shapes are accepted: summaries carry timing/speaker/selection/
 * sync flags while details additionally carry `translationVersions[]` +
 * `transcriptVersions[]` (source). Missing texts fall back to `''`.
 */
export function parseTranslationSegment(raw: unknown): TranslationSegmentView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id')) ?? '';
  if (id === '') {
    return undefined;
  }
  const versions = parseTranslationVersions(pick(record, 'translationVersions', 'TranslationVersions'));
  const source = parseTranscriptSource(record);
  const selected = deriveSelectedText(record, versions);
  const manualVersionId = versions.find((v) => v.isManual)?.id;
  const reviewStatus = toNonEmptyString(pick(record, 'reviewStatus', 'ReviewStatus'));
  const qualityCodes = toStringArray(pick(record, 'qualityCodes', 'QualityCodes'));
  const syncStatus = toNonEmptyString(pick(record, 'syncStatus', 'SyncStatus'));
  const speakerId = toNonEmptyString(pick(record, 'speakerId', 'SpeakerId'));
  const speakerLabel =
    toNonEmptyString(pick(record, 'speakerLabel', 'SpeakerLabel', 'speakerName', 'SpeakerName')) ??
    speakerId ??
    'Unknown speaker';
  const startMs = toFiniteNumber(pick(record, 'startMs', 'StartMs')) ?? 0;
  const endMsRaw = toFiniteNumber(pick(record, 'endMs', 'EndMs'));
  const endMs = endMsRaw === undefined || endMsRaw < startMs ? startMs : endMsRaw;
  const selectionVersion = toFiniteNumber(pick(record, 'selectionVersion', 'SelectionVersion')) ?? 0;
  const sequence = toFiniteNumber(pick(record, 'sequence', 'Sequence')) ?? 0;
  const glossaryHits = parseGlossaryHits(
    pick(record, 'glossaryHits', 'GlossaryHits', 'glossaryMatches', 'GlossaryMatches', 'glossary', 'Glossary'),
  );
  const assignedVoice = parseAssignedVoice(
    pick(record, 'assignedVoice', 'AssignedVoice', 'voice', 'Voice', 'voiceAssignment', 'VoiceAssignment'),
  );
  return {
    id,
    sequence,
    startMs: Math.max(0, Math.round(startMs)),
    endMs: Math.max(0, Math.round(endMs)),
    speakerId,
    speakerLabel,
    selectionVersion,
    reviewStatus,
    qualityCodes,
    syncStatus,
    sourceText: source.text,
    sourceVersionId: source.versionId,
    sourceVersionLabel: source.versionLabel,
    selectedText: selected.text,
    selectedVersionId: selected.selectedId,
    manualVersionId,
    versions,
    glossaryHits,
    assignedVoice,
    hasCandidates: versions.length > 0,
  };
}

/** Parses a `SegmentListResponse` page (`{ items, ... }`). Skips bad rows. */
export function parseTranslationListItems(raw: unknown): TranslationSegmentView[] {
  const record = toRecord(raw);
  const items = record !== undefined ? pick(record, 'items', 'Items') : raw;
  if (!Array.isArray(items)) {
    return [];
  }
  const out: TranslationSegmentView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseTranslationSegment(entry);
    if (parsed !== undefined) {
      out.push(parsed);
    }
  }
  return out.sort((a, b) => a.startMs - b.startMs || a.sequence - b.sequence);
}

/** Merges list summaries with detail hydration (detail wins for versions). */
export function mergeTranslationDetail(
  summary: TranslationSegmentView,
  detailRaw: unknown,
): TranslationSegmentView {
  const detail = parseTranslationSegment(detailRaw);
  if (detail === undefined) {
    return summary;
  }
  if (detail.versions.length === 0 && detail.sourceText === '') {
    return { ...summary, selectionVersion: detail.selectionVersion };
  }
  return {
    ...summary,
    sourceText: detail.sourceText !== '' ? detail.sourceText : summary.sourceText,
    sourceVersionId: detail.sourceVersionId ?? summary.sourceVersionId,
    sourceVersionLabel:
      detail.sourceVersionLabel !== 'transcript —' ? detail.sourceVersionLabel : summary.sourceVersionLabel,
    selectedText: detail.selectedText !== '' ? detail.selectedText : summary.selectedText,
    selectionVersion: detail.selectionVersion,
    selectedVersionId: detail.selectedVersionId ?? summary.selectedVersionId,
    manualVersionId: detail.manualVersionId ?? summary.manualVersionId,
    versions: detail.versions.length > 0 ? detail.versions : summary.versions,
    hasCandidates: detail.versions.length > 0 ? true : summary.hasCandidates,
    reviewStatus: detail.reviewStatus ?? summary.reviewStatus,
    qualityCodes: detail.qualityCodes.length > 0 ? detail.qualityCodes : summary.qualityCodes,
    syncStatus: detail.syncStatus ?? summary.syncStatus,
    glossaryHits: detail.glossaryHits.length > 0 ? detail.glossaryHits : summary.glossaryHits,
    assignedVoice: detail.assignedVoice ?? summary.assignedVoice,
  };
}

/** `mm:ss.mmm` timestamp for header rows (ms-accurate). */
export function formatTimestamp(ms: number): string {
  const clamped = Math.max(0, Math.round(ms));
  const minutes = Math.floor(clamped / 60_000);
  const seconds = Math.floor((clamped % 60_000) / 1000);
  const millis = clamped % 1000;
  return `${String(minutes).padStart(2, '0')}:${String(seconds).padStart(2, '0')}.${String(millis).padStart(3, '0')}`;
}

/** Window duration in ms (never negative). Pure. */
export function windowDurationMs(segment: Pick<TranslationSegmentView, 'startMs' | 'endMs'>): number {
  return Math.max(0, segment.endMs - segment.startMs);
}

/** Reading-speed hint: chars + chars/sec over the dub window. Pure. */
export function readingSpeedHint(text: string, durationMs: number): string {
  const chars = [...text].length;
  if (durationMs <= 0) {
    return `${String(chars)} chars · window —`;
  }
  const seconds = durationMs / 1000;
  const cps = chars / seconds;
  return `${String(chars)} chars · ${cps.toFixed(1)} chars/sec`;
}

/** Sync tone for the read-only badge: in-window, overflow, or unknown. Pure. */
export function syncToneFor(
  segment: Pick<TranslationSegmentView, 'syncStatus'>,
  text: string,
  durationMs: number,
): 'in-window' | 'overflow' | 'unknown' {
  const status = segment.syncStatus?.toLowerCase() ?? '';
  if (status.includes('overflow') || status.includes('exceed') || status.includes('out-of-sync') || status.includes('issue')) {
    return 'overflow';
  }
  if (status.includes('acceptable') || status.includes('ok') || status.includes('in-window') || status.includes('sync')) {
    // Explicit acceptable status wins, but an extreme overrun still overflows.
    if (durationMs > 0 && [...text].length / (durationMs / 1000) > SYNC_OVERFLOW_CPS) {
      return 'overflow';
    }
    return 'in-window';
  }
  if (durationMs <= 0) {
    return 'unknown';
  }
  return [...text].length / (durationMs / 1000) > SYNC_OVERFLOW_CPS ? 'overflow' : 'in-window';
}

/** True for 409 conflict errors (stale selection version). Pure. */
export function isTranslationConflict(
  error: { readonly code?: string; readonly status?: number } | undefined | null,
): boolean {
  if (error === undefined || error === null) {
    return false;
  }
  return error.code === 'SELECTION_CONFLICT' || error.status === 409;
}

/**
 * Splits text on glossary terms (case-insensitive) for `<mark>` highlighting.
 * Terms with missing definitions still highlight (no tooltip, no crash).
 * Pure — returns ordered `{ text, term? }` runs.
 */
export function splitGlossaryRuns(
  text: string,
  hits: readonly GlossaryHit[],
): ReadonlyArray<{ readonly text: string; readonly term: GlossaryHit | undefined }> {
  const terms = hits.map((h) => h.term).filter((t) => t !== '');
  if (text === '' || terms.length === 0) {
    return [{ text, term: undefined }];
  }
  const ordered = [...terms].sort((a, b) => b.length - a.length);
  const escaped = ordered.map((t) => t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));
  const pattern = new RegExp(`(${escaped.join('|')})`, 'gi');
  const runs: Array<{ text: string; term: GlossaryHit | undefined }> = [];
  let lastIndex = 0;
  for (const match of text.matchAll(pattern)) {
    const index = match.index ?? 0;
    if (index > lastIndex) {
      runs.push({ text: text.slice(lastIndex, index), term: undefined });
    }
    const matched = match[0] ?? '';
    const hit = hits.find((h) => h.term.toLowerCase() === matched.toLowerCase());
    runs.push({ text: matched, term: hit });
    lastIndex = index + matched.length;
  }
  if (lastIndex < text.length) {
    runs.push({ text: text.slice(lastIndex), term: undefined });
  }
  return runs.length > 0 ? runs : [{ text, term: undefined }];
}
