/**
 * Cost + quota domain view (Task 035A).
 *
 * Pure parsing + derivation over the Task 007 dashboard aggregate
 * (`cost/quota/storage` sections) and the Task 008 workspace aggregate
 * (`cost` + `media` sections). Every cost figure distinguishes estimated vs
 * actual; every estimate renders with the `Estimate` label. Reservation ids,
 * provider-internal cost keys, and raw telemetry never survive parsing.
 *
 * Canonical location for 035A (split from superseded Task 035). The legacy
 * `features/settings` cost helpers remain as thin duplicates for backward
 * compatibility until 035B cleans the settings split; new code must import
 * from this module.
 */

export type QuotaState = 'available' | 'near' | 'exceeded' | 'reserved';

export interface CostBreakdown {
  readonly estimatedUsd: number;
  readonly actualRunUsd: number | undefined;
  readonly actualMonthUsd: number | undefined;
  readonly currency: string;
  readonly durationMs: number | undefined;
  readonly storageUsedBytes: number | undefined;
  readonly storageQuotaBytes: number | undefined;
  /** Priced provider units (segment count mirror, planning figure only). */
  readonly providerUnits: number | undefined;
}

export interface QuotaView {
  readonly state: QuotaState;
  readonly remaining: number | undefined;
  readonly resetsAt: string | undefined;
  readonly storageRatio: number;
  readonly reservedUsd: number | undefined;
}

function toFiniteNumber(value: unknown): number | undefined {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined;
}

function toNonEmptyString(value: unknown): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

/** Storage usage ratio in [0, +Infinity). Zero on unknown quota. Pure. */
export function storageUsageRatio(usedBytes: number | undefined, quotaBytes: number | undefined): number {
  if (usedBytes === undefined || quotaBytes === undefined || quotaBytes <= 0 || usedBytes <= 0) {
    return 0;
  }
  return usedBytes / quotaBytes;
}

/**
 * Derives the quota state (Task 035A, R3). Priority: `exceeded` (remaining
 * exhausted or storage full) wins, then `reserved` (a held amount awaiting
 * reconciliation, informational only), then `near` (low remaining or
 * storage at warning threshold), else `available`. Pure.
 */
export function deriveQuotaState(input: {
  readonly remaining?: number;
  readonly usedBytes?: number;
  readonly quotaBytes?: number;
  readonly reservedUsd?: number;
}): QuotaState {
  const ratio = storageUsageRatio(input.usedBytes, input.quotaBytes);
  const remaining = input.remaining;
  if ((remaining !== undefined && remaining <= 0) || ratio >= 1) {
    return 'exceeded';
  }
  if (input.reservedUsd !== undefined && input.reservedUsd > 0) {
    return 'reserved';
  }
  if ((remaining !== undefined && remaining <= 3) || ratio >= 0.8) {
    return 'near';
  }
  return 'available';
}

/** Alert tone per quota state (distinct visual treatments, never color alone). Pure. */
export function toneForQuotaState(state: QuotaState): 'success' | 'warning' | 'error' | 'info' {
  switch (state) {
    case 'exceeded':
      return 'error';
    case 'near':
      return 'warning';
    case 'reserved':
      return 'info';
    default:
      return 'success';
  }
}

/** Glyph per quota state (text, never color alone). Pure. */
export function iconForQuotaState(state: QuotaState): string {
  switch (state) {
    case 'available':
      return '✓';
    case 'near':
      return '⚠';
    case 'exceeded':
      return '✕';
    case 'reserved':
      return '◷';
    default:
      return '•';
  }
}

/** True when costly actions must block (only `exceeded` blocks). Pure. */
export function isQuotaBlocking(state: QuotaState): boolean {
  return state === 'exceeded';
}

/**
 * Builds a quota view from dashboard + workspace slices. Missing sections
 * stay undefined (callers render `UnavailableState`, never zero-fill).
 * Reserved amounts are informational only — reservation ids never enter
 * this shape. Pure.
 */
export function buildQuotaView(input: {
  readonly remaining?: unknown;
  readonly resetsAt?: unknown;
  readonly usedBytes?: unknown;
  readonly quotaBytes?: unknown;
  readonly reservedUsd?: unknown;
}): QuotaView {
  const remaining = toFiniteNumber(input.remaining);
  const resetsAt = toNonEmptyString(input.resetsAt);
  const usedBytes = toFiniteNumber(input.usedBytes);
  const quotaBytes = toFiniteNumber(input.quotaBytes);
  const reservedRaw = toFiniteNumber(input.reservedUsd);
  const reservedUsd = reservedRaw !== undefined && reservedRaw > 0 ? reservedRaw : undefined;
  const ratio = storageUsageRatio(usedBytes, quotaBytes);
  const state = deriveQuotaState({ remaining, usedBytes, quotaBytes, reservedUsd });
  return { state, remaining, resetsAt, storageRatio: ratio, reservedUsd };
}

/**
 * Builds the cost breakdown from dashboard actuals plus the workspace run
 * actual and media/storage context. The estimate is always a planning
 * figure supplied by the caller (preflight mirror) and must render with
 * the `Estimate` label; actuals are metered server values. A missing cost
 * section yields undefined actuals (callers show `UnavailableState`).
 * Provider units are the priced segment count mirror (planning figure only).
 * Never carries reservation ids. Pure.
 */
export function buildCostBreakdown(input: {
  readonly estimatedUsd: unknown;
  readonly actualRunUsd?: unknown;
  readonly actualMonthUsd?: unknown;
  readonly currency?: unknown;
  readonly durationMs?: unknown;
  readonly storageUsedBytes?: unknown;
  readonly storageQuotaBytes?: unknown;
  readonly providerUnits?: unknown;
}): CostBreakdown {
  const estimatedRaw = toFiniteNumber(input.estimatedUsd);
  const estimatedUsd = estimatedRaw !== undefined && estimatedRaw >= 0 ? estimatedRaw : 0;
  const actualRunRaw = toFiniteNumber(input.actualRunUsd);
  const actualMonthRaw = toFiniteNumber(input.actualMonthUsd);
  const currency = toNonEmptyString(input.currency) ?? 'USD';
  const durationRaw = toFiniteNumber(input.durationMs);
  const usedRaw = toFiniteNumber(input.storageUsedBytes);
  const quotaRaw = toFiniteNumber(input.storageQuotaBytes);
  const unitsRaw = toFiniteNumber(input.providerUnits);
  return {
    estimatedUsd,
    actualRunUsd: actualRunRaw,
    actualMonthUsd: actualMonthRaw,
    currency,
    durationMs: durationRaw !== undefined && durationRaw > 0 ? durationRaw : undefined,
    storageUsedBytes: usedRaw !== undefined && usedRaw >= 0 ? usedRaw : undefined,
    storageQuotaBytes: quotaRaw !== undefined && quotaRaw > 0 ? quotaRaw : undefined,
    providerUnits: unitsRaw !== undefined && unitsRaw > 0 ? Math.floor(unitsRaw) : undefined,
  };
}

/** True when any rendered cost text would leak a reservation id. Pure. */
export function containsReservationId(values: readonly string[]): boolean {
  for (const value of values) {
    const lowered = value.toLowerCase();
    if (lowered.includes('res_') || lowered.includes('reservation')) {
      return true;
    }
  }
  return false;
}
