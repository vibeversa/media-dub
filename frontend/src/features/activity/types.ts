/**
 * Activity timeline domain view (Task 035).
 *
 * Pure parsing + derivation over the Task 008 activity contract
 * (`GET /projects/{id}/activity` page envelope plus the workspace aggregate
 * `activity.recent` slice). The backend projection is thin
 * (`id/summary/occurredAt` only); richer actor/action/advanced fields are
 * parsed defensively when present and fall back to display-safe defaults.
 * Raw telemetry, reservation ids, provider-internal keys, tokens, and URLs
 * are never retained — see `isForbiddenActivityKey`.
 */

export const ACTIVITY_PAGE_SIZE = 20;

export interface ActivityFilters {
  readonly actor: string;
  readonly action: string;
  readonly from: string;
  readonly to: string;
}

export const DEFAULT_ACTIVITY_FILTERS: ActivityFilters = {
  actor: '',
  action: '',
  from: '',
  to: '',
};

export interface ActivityView {
  readonly id: string;
  readonly timestamp: string;
  readonly actor: string;
  readonly action: string;
  readonly summary: string;
  readonly hasAdvanced: boolean;
  readonly advanced: Readonly<Record<string, string>>;
}

export interface ActivityPageView {
  readonly items: readonly ActivityView[];
  readonly page: number;
  readonly pageSize: number;
  readonly total: number;
  readonly hasMore: boolean;
}

const FORBIDDEN_ACTIVITY_KEY_FRAGMENTS: readonly string[] = [
  'reservation',
  'reservationid',
  'secret',
  'password',
  'passwd',
  'pwd',
  'token',
  'credential',
  'private_key',
  'apikey',
  'api_key',
  'client_secret',
  'signedurl',
  'signed_url',
  'downloadurl',
  'download_url',
  'connectionstring',
  'connection_string',
  'internalpath',
  'internal_path',
  'rawpayload',
  'raw_payload',
  'leasetoken',
  'lease_token',
  'costkey',
  'cost_key',
  'providerkey',
  'provider_key',
];

const ADVANCED_ALLOWLIST: ReadonlySet<string> = new Set([
  'run',
  'runid',
  'run_id',
  'stage',
  'phase',
  'status',
  'attempt',
  'correlationid',
  'correlation_id',
  'provider',
  'cost',
  'action',
  'actor',
]);

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

function toDisplayText(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

/** True when an activity payload key must never render. Pure. */
export function isForbiddenActivityKey(key: string): boolean {
  const normalized = key.toLowerCase().replace(/[^a-z0-9]/g, '');
  for (const fragment of FORBIDDEN_ACTIVITY_KEY_FRAGMENTS) {
    const flat = fragment.toLowerCase().replace(/[^a-z0-9]/g, '');
    if (flat !== '' && normalized.includes(flat)) {
      return true;
    }
  }
  return false;
}

/** True when the key is an allowlisted advanced field. Pure. */
export function isAdvancedActivityKey(key: string): boolean {
  return ADVANCED_ALLOWLIST.has(key.toLowerCase().replace(/[^a-z0-9_]/g, ''));
}

function sanitizeActor(raw: string): string {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return 'System';
  }
  if (trimmed.includes('@')) {
    return 'Team member';
  }
  if (/^[0-9a-f-]{32,}$/i.test(trimmed) || trimmed.startsWith('sub_') || trimmed.startsWith('usr_')) {
    return 'Team member';
  }
  return trimmed.slice(0, 80);
}

function sanitizeAction(raw: string): string {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return 'Updated';
  }
  return trimmed.slice(0, 80);
}

function toAdvancedString(value: unknown): string | undefined {
  if (typeof value === 'string' && value !== '') {
    return value.slice(0, 200);
  }
  if (typeof value === 'number' && Number.isFinite(value)) {
    return String(value);
  }
  return undefined;
}

function extractAdvanced(record: Record<string, unknown>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const key of Object.keys(record)) {
    if (isForbiddenActivityKey(key)) {
      continue;
    }
    const lowered = key.toLowerCase();
    const isAdvancedCandidate =
      lowered === 'run' ||
      lowered === 'runid' ||
      lowered === 'run_id' ||
      lowered === 'stage' ||
      lowered === 'phase' ||
      lowered === 'status' ||
      lowered === 'attempt' ||
      lowered === 'correlationid' ||
      lowered === 'correlation_id' ||
      lowered === 'provider' ||
      lowered === 'cost' ||
      lowered === 'processingrunid' ||
      lowered === 'processing_run_id';
    if (!isAdvancedCandidate) {
      continue;
    }
    const text = toAdvancedString(record[key]);
    if (text !== undefined && !text.toLowerCase().includes('res_') && !text.includes('http')) {
      out[key] = text;
    }
  }
  return out;
}

/**
 * Parses one activity row defensively. Keeps only display fields
 * (`timestamp/actor/action/summary`) plus allowlisted advanced fields.
 * Returns undefined for rows without an id. Never throws. Pure.
 */
export function parseActivityEvent(raw: unknown): ActivityView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id', 'eventId', 'EventId')) ?? '';
  if (id === '') {
    return undefined;
  }
  const timestamp =
    toNonEmptyString(pick(record, 'occurredAt', 'OccurredAt', 'timestamp', 'Timestamp', 'createdAt', 'CreatedAt')) ?? '';
  const actorRaw = toDisplayText(pick(record, 'actor', 'Actor', 'actorName', 'ActorName', 'actorType', 'ActorType', 'displayName', 'DisplayName'));
  const actionRaw = toDisplayText(pick(record, 'action', 'Action', 'type', 'Type', 'eventType', 'EventType'));
  const summary = toDisplayText(pick(record, 'summary', 'Summary', 'message', 'Message', 'description', 'Description'));
  if (summary === '') {
    return undefined;
  }
  const advanced = extractAdvanced(record);
  return {
    id,
    timestamp,
    actor: sanitizeActor(actorRaw === '' ? inferActorFromSummary(summary) : actorRaw),
    action: sanitizeAction(actionRaw),
    summary,
    hasAdvanced: Object.keys(advanced).length > 0,
    advanced,
  };
}

function inferActorFromSummary(summary: string): string {
  if (summary.toLowerCase().includes('system')) {
    return 'System';
  }
  return '';
}

/**
 * Parses a page envelope (`{ items, page, pageSize, total, hasMore }`).
 * Accepts the workspace `activity.recent` shape as `{ recent: [...] }`.
 * Skips bad rows, dedupes by id, preserves server order (newest first).
 * Never throws. Pure.
 */
export function parseActivityEvents(raw: unknown): ActivityView[] {
  const record = toRecord(raw);
  let items: unknown = raw;
  if (record !== undefined) {
    const inner = pick(record, 'items', 'Items', 'recent', 'Recent');
    if (Array.isArray(inner)) {
      items = inner;
    } else if (!Array.isArray(raw)) {
      return [];
    }
  }
  if (!Array.isArray(items)) {
    return [];
  }
  const seen = new Set<string>();
  const out: ActivityView[] = [];
  for (const entry of items as unknown[]) {
    const parsed = parseActivityEvent(entry);
    if (parsed !== undefined && !seen.has(parsed.id)) {
      seen.add(parsed.id);
      out.push(parsed);
    }
  }
  return out;
}

/** Parses the full page envelope with paging metadata. Never throws. Pure. */
export function parseActivityPage(raw: unknown): ActivityPageView {
  const record = toRecord(raw) ?? {};
  const items = parseActivityEvents(raw);
  const toPageNumber = (value: unknown, fallback: number): number => {
    if (typeof value === 'number' && Number.isFinite(value) && value >= 1) {
      return Math.floor(value);
    }
    return fallback;
  };
  const page = toPageNumber(pick(record, 'page', 'Page'), 1);
  const pageSize = toPageNumber(pick(record, 'pageSize', 'PageSize'), ACTIVITY_PAGE_SIZE);
  const totalRaw = pick(record, 'total', 'Total');
  const total =
    typeof totalRaw === 'number' && Number.isFinite(totalRaw) && totalRaw >= 0
      ? Math.floor(totalRaw)
      : items.length;
  const hasMoreRaw = pick(record, 'hasMore', 'HasMore');
  const hasMore = hasMoreRaw === true || page * pageSize < total;
  return { items, page, pageSize, total, hasMore };
}

/** True when filters equal the shareable defaults. Pure. */
export function isDefaultActivityFilters(filters: ActivityFilters): boolean {
  return filters.actor === '' && filters.action === '' && filters.from === '' && filters.to === '';
}

/** Parses URL search params into activity filters. Never throws. Pure. */
export function parseActivityFiltersFromSearch(search: string): ActivityFilters {
  let params: URLSearchParams;
  try {
    params = new URLSearchParams(search.startsWith('?') ? search.slice(1) : search);
  } catch {
    return { ...DEFAULT_ACTIVITY_FILTERS };
  }
  const actor = (params.get('actor') ?? '').slice(0, 80);
  const action = (params.get('action') ?? '').slice(0, 80);
  const from = (params.get('from') ?? '').slice(0, 20);
  const to = (params.get('to') ?? '').slice(0, 20);
  return { actor, action, from, to };
}

/** Serializes filters to a shareable query string (empty for defaults). Pure. */
export function serializeActivityFilters(filters: ActivityFilters): string {
  const params = new URLSearchParams();
  if (filters.actor !== '') {
    params.set('actor', filters.actor);
  }
  if (filters.action !== '') {
    params.set('action', filters.action);
  }
  if (filters.from !== '') {
    params.set('from', filters.from);
  }
  if (filters.to !== '') {
    params.set('to', filters.to);
  }
  const text = params.toString();
  return text === '' ? '' : `?${text}`;
}

/**
 * Client-side filter over one fetched page (the backend activity contract
 * has no filter params; unknown query keys are ignored server-side, so
 * narrowing happens in memory without refetching — mirroring the project
 * list client-only refinements). Matches actor/action case-insensitively
 * (substring) and clamps to the inclusive date range. Pure.
 */
export function filterActivityEvents(items: readonly ActivityView[], filters: ActivityFilters): ActivityView[] {
  const actorNeedle = filters.actor.trim().toLowerCase();
  const actionNeedle = filters.action.trim().toLowerCase();
  const fromMs = filters.from !== '' ? Date.parse(filters.from) : Number.NaN;
  const toMs = filters.to !== '' ? Date.parse(filters.to) : Number.NaN;
  const hasFrom = Number.isFinite(fromMs);
  const hasTo = Number.isFinite(toMs);
  return items.filter((item) => {
    if (actorNeedle !== '' && !item.actor.toLowerCase().includes(actorNeedle)) {
      return false;
    }
    if (actionNeedle !== '' && !item.action.toLowerCase().includes(actionNeedle)) {
      return false;
    }
    if (hasFrom || hasTo) {
      const time = Date.parse(item.timestamp);
      if (Number.isNaN(time)) {
        return false;
      }
      if (hasFrom && time < (fromMs as number)) {
        return false;
      }
      if (hasTo) {
        const end = (toMs as number) + (filters.to.length <= 10 ? 86_400_000 - 1 : 0);
        if (time > end) {
          return false;
        }
      }
    }
    return true;
  });
}

/** Lowercase testid key for an action name. Pure. */
export function activityActionKey(action: string): string {
  const normalized = action.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return normalized === '' ? 'unknown' : normalized;
}

/** True when text looks like a reservation id that must never render. Pure. */
export function looksLikeReservationId(value: string): boolean {
  const lowered = value.toLowerCase();
  return lowered.includes('res_') || lowered.includes('reservation');
}
