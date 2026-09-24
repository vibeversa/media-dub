import { apiClient } from '../../api/client/index.js';

/**
 * Processing-start preflight model (Task 024).
 *
 * The v1 bundle exposes no dedicated estimate endpoint, so the preflight
 * estimate is a client-side mirror of the server formula, computed only from
 * generated-typed fetches and labeled as an estimate everywhere it renders
 * (R2). The server stays authoritative: `CostService.PreflightAsync` enforces
 * the per-project cost budget at start (429 `QUOTA_EXCEEDED`), voice consent
 * at assignment (403 `VOICE_CONSENT_REQUIRED`), media readiness (409), and
 * the active-run guard (409 `RUN_ALREADY_ACTIVE`).
 *
 * Estimate derivation (mirrors `CostService.EstablePipeline` +
 * `PriceTable.Default()` v1.0.0):
 * - segment count = sum of speaker `segmentCount` across the fetched speaker
 *   page (real server data via generated `listSpeakers`); clamped to 1..2000
 *   with a 10-segment fallback, mirroring the server's `durationMs <= 0 → 10`
 *   branch and 2000 clamp.
 * - audio minutes = segments × 5s / 60, the exact inversion of the server's
 *   `ceil(durationMs / 5000)` segment guess.
 * - chars = segments × 100 (the server's documented per-segment guess).
 * - prices: transcription $0.02/min, translation $0.00002/char,
 *   TTS $0.00003/char (`PriceTable.Default()`).
 *
 * Pure helpers are unit-testable without fetch; `loadPreflight` is the single
 * impure fetch used by the dialog. No media bytes, tokens, or URLs are ever
 * attached to these shapes.
 */

/** Price-table mirror of `CostService.PriceTable.Default()` (estimate only). */
export const PREFLIGHT_PRICE_VERSION = '1.0.0';

export const PREFLIGHT_PRICE_TRANSCRIPTION_PER_MIN = 0.02;

export const PREFLIGHT_PRICE_TRANSLATION_PER_CHAR = 0.00002;

export const PREFLIGHT_PRICE_TTS_PER_CHAR = 0.00003;

/** Server's documented per-segment char guess (`PreflightAsync`). */
export const PREFLIGHT_CHARS_PER_SEGMENT = 100;

/** Inversion of the server's `ceil(durationMs / 5000)` segment guess. */
export const PREFLIGHT_SECONDS_PER_SEGMENT = 5;

/** Mirrors the server's `durationMs <= 0 → 10 segments` fallback branch. */
export const PREFLIGHT_FALLBACK_SEGMENTS = 10;

/** Mirrors the server's 2000-segment clamp. */
export const PREFLIGHT_MAX_SEGMENTS = 2000;

/** Speakers fetched per preflight page (bundle max for speakers). */
export const PREFLIGHT_SPEAKER_PAGE_SIZE = 100;

export interface CostEstimate {
  readonly amountUsd: number;
  /** Priced segment count after fallback/clamp. */
  readonly segmentCount: number;
  readonly priceVersion: string;
}

export interface ClonedVoice {
  readonly speakerId: string;
  readonly speakerKey: string;
  readonly displayName: string;
  readonly voiceId: string;
}

export interface PreflightQuota {
  readonly remaining: number;
  readonly resetsAt: string;
}

export interface PreflightActiveRun {
  readonly runId: string;
  readonly status: string;
}

export interface PreflightSnapshot {
  readonly projectId: string;
  readonly projectName: string;
  readonly status: string;
  readonly configHash: string | undefined;
  readonly settingsVersion: number;
  readonly sourceLanguage: string | undefined;
  readonly targetLanguage: string | undefined;
  readonly isArchived: boolean;
  readonly estimate: CostEstimate;
  readonly currency: string;
  readonly monthToDate: number;
  readonly quota: PreflightQuota;
  readonly clonedVoices: readonly ClonedVoice[];
  readonly speakerTotal: number;
  /** True when the project has more speakers than the fetched page covers. */
  readonly speakersTruncated: boolean;
  /** Non-null when a run is already active (pre-start conflict signal). */
  readonly activeRun: PreflightActiveRun | null;
}

/** Blocks that keep the confirm button disabled (R5: never silently enabled). */
export type ConfirmBlock = 'starting' | 'conflict' | 'consent' | 'quota';

export interface ConfirmInputs {
  readonly consentAcknowledged: boolean;
  readonly overrideReason: string;
  readonly starting: boolean;
  /** True when a conflict is known (pre-start active run or 409 on start). */
  readonly inConflict: boolean;
}

/**
 * Pure confirm-gating matrix. The button enables only when every block is
 * clear; callers render one reason line per block (R5).
 */
export function confirmBlocks(snapshot: PreflightSnapshot, inputs: ConfirmInputs): ConfirmBlock[] {
  const blocks: ConfirmBlock[] = [];
  if (inputs.starting) {
    blocks.push('starting');
  }
  if (inputs.inConflict) {
    blocks.push('conflict');
  }
  if (snapshot.clonedVoices.length > 0 && !inputs.consentAcknowledged) {
    blocks.push('consent');
  }
  if (snapshot.quota.remaining <= 0 && inputs.overrideReason.trim() === '') {
    blocks.push('quota');
  }
  return blocks;
}

/** True when the tenant daily quota is exhausted (override reason required). */
export function isQuotaExhausted(remaining: number): boolean {
  return remaining <= 0;
}

/**
 * Mirrors `CostService.EstimatePipeline` over a segment count. Pure and
 * deterministic; the result is a planning figure, never a promise.
 */
export function estimateRunCostUsd(segmentCount: number): CostEstimate {
  let segments = Number.isFinite(segmentCount) ? Math.floor(segmentCount) : 0;
  if (segments <= 0) {
    segments = PREFLIGHT_FALLBACK_SEGMENTS;
  }
  segments = Math.min(PREFLIGHT_MAX_SEGMENTS, segments);
  const audioMinutes = (segments * PREFLIGHT_SECONDS_PER_SEGMENT) / 60;
  const chars = segments * PREFLIGHT_CHARS_PER_SEGMENT;
  const amountUsd =
    audioMinutes * PREFLIGHT_PRICE_TRANSCRIPTION_PER_MIN +
    chars * PREFLIGHT_PRICE_TRANSLATION_PER_CHAR +
    chars * PREFLIGHT_PRICE_TTS_PER_CHAR;
  return { amountUsd, segmentCount: segments, priceVersion: PREFLIGHT_PRICE_VERSION };
}

/** Sums finite non-negative speaker `segmentCount` values (floored). */
export function sumSpeakerSegments(items: readonly Record<string, unknown>[]): number {
  let total = 0;
  for (const item of items) {
    const raw = item['segmentCount'];
    if (typeof raw === 'number' && Number.isFinite(raw) && raw > 0) {
      total += Math.floor(raw);
    }
  }
  return total;
}

/**
 * Extracts assigned cloned voices (`assignedVoice.type === 'Cloned'`,
 * mirroring `VoiceType.Cloned` / `VoiceCompatibility.RequiresConsent`) for
 * the consent warnings. Defensive: malformed rows are skipped, never thrown.
 */
export function parseClonedVoices(items: readonly Record<string, unknown>[] | undefined): ClonedVoice[] {
  if (items === undefined) {
    return [];
  }
  const voices: ClonedVoice[] = [];
  items.forEach((item, index) => {
    const assigned = item['assignedVoice'];
    if (typeof assigned !== 'object' || assigned === null) {
      return;
    }
    const record = assigned as Record<string, unknown>;
    if (record['type'] !== 'Cloned') {
      return;
    }
    const voiceId = typeof record['voiceId'] === 'string' ? record['voiceId'] : '';
    if (voiceId === '') {
      return;
    }
    const speakerKey = typeof item['speakerKey'] === 'string' && item['speakerKey'] !== '' ? item['speakerKey'] : `speaker-${index + 1}`;
    const displayName =
      typeof item['displayName'] === 'string' && item['displayName'] !== '' ? item['displayName'] : speakerKey;
    const speakerId = typeof item['id'] === 'string' && item['id'] !== '' ? item['id'] : speakerKey;
    voices.push({ speakerId, speakerKey, displayName, voiceId });
  });
  return voices;
}

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

function isNotFoundError(error: unknown): boolean {
  if (typeof error !== 'object' || error === null) {
    return false;
  }
  const record = error as Record<string, unknown>;
  return record['code'] === 'NOT_FOUND' && record['status'] === 404;
}

/**
 * Loads the full preflight snapshot. All-or-nothing by design (R1 edge: never
 * start blind on a costly op) — any fetch failure, or any invalid
 * cost/quota/speaker section, rejects so the dialog renders the blocked
 * "estimate unavailable" state with retry. The single expected non-failure is
 * the active-run probe 404 (`NOT_FOUND`), which means "no active run".
 */
export async function loadPreflight(projectId: string): Promise<PreflightSnapshot> {
  const [project, summary, speakers] = await Promise.all([
    apiClient.getProject({ path: { projectId } }),
    apiClient.getDashboardSummary(),
    apiClient.listSpeakers({ path: { projectId }, query: { page: 1, pageSize: PREFLIGHT_SPEAKER_PAGE_SIZE } }),
  ]);

  let activeRun: PreflightActiveRun | null = null;
  try {
    const run = await apiClient.getActiveProcessingRun({ path: { projectId } });
    activeRun = { runId: run.runId, status: run.status ?? '' };
  } catch (error) {
    if (!isNotFoundError(error)) {
      throw error;
    }
  }

  const costSection = summary.cost as { monthToDate?: unknown; currency?: unknown } | undefined;
  const monthToDate = costSection === undefined || costSection === null ? undefined : toFiniteNumber(costSection.monthToDate);
  const quotaSection = summary.quota as { remaining?: unknown; resetsAt?: unknown } | undefined;
  const remaining = quotaSection === undefined || quotaSection === null ? undefined : toFiniteNumber(quotaSection.remaining);
  const resetsAt = quotaSection === undefined || quotaSection === null ? undefined : toNonEmptyString(quotaSection.resetsAt);
  if (monthToDate === undefined || remaining === undefined || resetsAt === undefined) {
    throw new Error('Preflight unavailable: the cost/quota sections failed validation.');
  }
  const currency = toNonEmptyString(costSection?.currency) ?? 'USD';

  const items = Array.isArray(speakers.items) ? (speakers.items as Record<string, unknown>[]) : undefined;
  if (items === undefined) {
    throw new Error('Preflight unavailable: the speaker list failed validation.');
  }
  const total = toFiniteNumber(speakers.total) ?? items.length;
  const clonedVoices = parseClonedVoices(items);
  const estimate = estimateRunCostUsd(sumSpeakerSegments(items));

  const settingsVersion = toFiniteNumber(project.settingsVersion) ?? 0;
  return {
    projectId: project.id,
    projectName: project.name,
    status: project.status ?? '',
    configHash: toNonEmptyString(project.configHash),
    settingsVersion,
    sourceLanguage: toNonEmptyString(project.sourceLanguage),
    targetLanguage: toNonEmptyString(project.targetLanguage),
    isArchived: project.isArchived === true,
    estimate,
    currency,
    monthToDate,
    quota: { remaining, resetsAt },
    clonedVoices,
    speakerTotal: total,
    speakersTruncated: total > items.length,
    activeRun,
  };
}
