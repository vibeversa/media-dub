/**
 * Admin + diagnostics domain views (Task 036).
 *
 * Pure parsing + derivation over the Task 005/013 read contracts
 * (`GET /admin/usage`, `/admin/quotas`, `/admin/provider-health`,
 * `/admin/provider-routes`, `/admin/diagnostics/queues|dlq|leases|orphans|
 * review-backlog`, `/admin/status`) plus the conventional tenant/user/audit/
 * retention/flag reads (`GET /admin/tenants`, `/admin/users`,
 * `/admin/audit-events`, `/admin/retention`, `/admin/feature-flags`) which the
 * backend may not provision yet — unknown shapes degrade to empty views, and
 * the panels render `EmptyState` (never a crash, never zero-filled secrets).
 *
 * Secret-free by construction: every parser drops keys matching
 * `SECRET_KEY_FRAGMENTS` and every rendered value passes through plain-text
 * rendering only. Connection strings render as masked fingerprints via
 * `maskConnectionString` (provider + masked marker, never reversible).
 * Reservation ids, lease tokens, raw tokens, URLs, and media bytes never
 * survive parsing — see `isForbiddenAdminKey`.
 */

export const ADMIN_SECTIONS = [
  'tenants',
  'users',
  'health',
  'usage',
  'retention',
  'audit',
  'flags',
] as const;

export type AdminSectionId = (typeof ADMIN_SECTIONS)[number];

/** Minimum audit-reason length for role changes and destructive actions. */
export const MIN_AUDIT_REASON_LENGTH = 10;

/** Maximum audit-reason length (display + transport cap, plain text). */
export const MAX_AUDIT_REASON_LENGTH = 500;

/**
 * Platform role hierarchy (mirrors `AuthPolicies`: TenantAdmin > ProjectOwner
 * > ProjectEditor > Reviewer > ProjectViewer; `Service` is a machine role
 * that satisfies every gate). Permission-string aliases (`admin.manage`,
 * `diagnostics.view`, `admin:read`) approximate the same grants for the
 * client-side pre-check — the store keeps permission strings, not role
 * claims, and the server re-authorizes every assignment. Ranks drive the
 * strictly-higher-grant rule for role assignment (R6). Unknown roles rank 0
 * (never grant).
 */
export const ADMIN_ROLE_RANKS: Readonly<Record<string, number>> = Object.freeze({
  Service: 6,
  TenantAdmin: 5,
  Operator: 5,
  'admin.manage': 5,
  'admin:read': 5,
  ProjectOwner: 4,
  ProjectEditor: 3,
  Reviewer: 2,
  'diagnostics.view': 2,
  ProjectViewer: 1,
});

/** Assignable tenant roles, highest grant first. */
export const ASSIGNABLE_ROLES: readonly string[] = Object.freeze([
  'TenantAdmin',
  'ProjectOwner',
  'ProjectEditor',
  'Reviewer',
  'ProjectViewer',
]);

const SECRET_KEY_FRAGMENTS: readonly string[] = [
  'secret',
  'password',
  'passwd',
  'pwd',
  'token',
  'credential',
  'privatekey',
  'private_key',
  'apikey',
  'api_key',
  'clientsecret',
  'client_secret',
  'connectionstring',
  'connection_string',
  'signedurl',
  'signed_url',
  'downloadurl',
  'download_url',
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
  'reservation',
];

/** DLQ row actions the backend may advertise. Anything else never renders. */
const ADVERTISED_DLQ_ACTIONS: ReadonlySet<string> = new Set(['redrive', 'discard']);

/** Advanced audit fields visible behind the per-row expander (Task 035 rule). */
const AUDIT_ADVANCED_ALLOWLIST: ReadonlySet<string> = new Set([
  'run',
  'runid',
  'run_id',
  'processingrunid',
  'processing_run_id',
  'stage',
  'phase',
  'status',
  'attempt',
  'correlationid',
  'correlation_id',
  'provider',
  'project',
  'projectid',
  'project_id',
]);

function normalizeKey(key: string): string {
  return key.toLowerCase().replace(/[^a-z0-9]/g, '');
}

/** True when an admin payload key must never render. Pure. */
export function isForbiddenAdminKey(key: string): boolean {
  const normalized = normalizeKey(key);
  for (const fragment of SECRET_KEY_FRAGMENTS) {
    const flat = fragment.toLowerCase().replace(/[^a-z0-9]/g, '');
    if (flat !== '' && normalized.includes(flat)) {
      return true;
    }
  }
  return false;
}

/** True for values that look like reservation ids. Pure. */
export function looksLikeReservationId(value: string): boolean {
  const lowered = value.toLowerCase();
  return lowered.includes('res_') || lowered.includes('reservation');
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

function toDisplayText(value: unknown, maxLength = 200): string {
  if (typeof value === 'string') {
    return value.slice(0, maxLength);
  }
  if (typeof value === 'number' && Number.isFinite(value)) {
    return String(value);
  }
  return '';
}

function sanitizeDisplayName(raw: string): string {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return 'Unnamed';
  }
  if (trimmed.includes('@')) {
    return 'Team member';
  }
  if (/^[0-9a-f-]{32,}$/i.test(trimmed) || trimmed.startsWith('sub_') || trimmed.startsWith('usr_')) {
    return 'Team member';
  }
  return trimmed.slice(0, 80);
}

/**
 * Sanitizes free-text audit reasons for display and transport: trims,
 * collapses control characters, caps at 500 chars. Rendered as plain text
 * only (never HTML). Pure.
 */
export function sanitizeReasonText(raw: string): string {
  return raw
    .replace(/[^\S ]+/g, ' ')
    .replace(/\s+/g, ' ')
    .trim()
    .slice(0, MAX_AUDIT_REASON_LENGTH);
}

/** True when the reason satisfies the mandatory audit-reason rule. Pure. */
export function isValidAuditReason(raw: string): boolean {
  return sanitizeReasonText(raw).length >= MIN_AUDIT_REASON_LENGTH;
}

/**
 * Highest grant rank held by the assigner. `Service`/unknown permissions
 * resolve through the same table; unknown strings contribute 0. Pure.
 */
export function assignerRank(assignerRoles: readonly string[]): number {
  let rank = 0;
  for (const role of assignerRoles) {
    const value = ADMIN_ROLE_RANKS[role] ?? 0;
    if (value > rank) {
      rank = value;
    }
  }
  return rank;
}

/**
 * Strictly-higher-grant rule (R6): the assigner may grant `targetRole` only
 * when their best rank strictly exceeds the target rank. `Service` (rank 6)
 * may grant anything; unknown targets are never grantable. Pure.
 */
export function canAssignRole(assignerRoles: readonly string[], targetRole: string): boolean {
  const target = ADMIN_ROLE_RANKS[targetRole] ?? 0;
  if (target <= 0) {
    return false;
  }
  return assignerRank(assignerRoles) > target;
}

/**
 * Masks a connection string as a non-reversible fingerprint: the provider
 * scheme (when parseable) plus a masked marker. The raw value never appears
 * in the output. Pure.
 */
export function maskConnectionString(raw: string): string {
  const trimmed = raw.trim();
  if (trimmed === '') {
    return '—';
  }
  const schemeEnd = trimmed.indexOf('://');
  const provider = schemeEnd > 0 ? trimmed.slice(0, Math.min(schemeEnd, 24)).toLowerCase() : 'stored';
  const safe = /^[a-z][a-z0-9+.-]*$/.test(provider) ? provider : 'stored';
  return `${safe}://•••• (masked)`;
}

/**
 * Scans display strings for secret leakage (`secret|password|
 * connectionString|apiKey|privateKey` and token/credential/reservation
 * fragments). Returns the first offending value, or undefined when clean.
 * Pure. The R5 scan test asserts this finds nothing in rendered admin output.
 */
export function findSecretLeak(values: readonly string[]): string | undefined {
  const fragments = ['secret', 'password', 'connectionstring', 'apikey', 'privatekey', 'credential', 'accesstoken', 'refreshtoken', 'bear ', 'res_', 'reservation', 'leasetoken'];
  for (const value of values) {
    const lowered = value.toLowerCase();
    for (const fragment of fragments) {
      if (lowered.includes(fragment)) {
        return value;
      }
    }
  }
  return undefined;
}

// --- Queue depths -----------------------------------------------------------

export interface QueueDepthView {
  readonly queue: string;
  readonly depth: number;
}

/** Parses `GET /admin/diagnostics/queues` (array of `{queue, depth}`). Pure. */
export function parseQueueDepths(raw: unknown): QueueDepthView[] {
  const items = Array.isArray(raw) ? raw : (toRecord(raw)?.['items'] as unknown);
  if (!Array.isArray(items)) {
    return [];
  }
  const out: QueueDepthView[] = [];
  for (const entry of items) {
    const record = toRecord(entry);
    if (record === undefined) {
      continue;
    }
    const queue = toNonEmptyString(pick(record, 'queue', 'Queue', 'name', 'Name')) ?? '';
    const depthRaw = pick(record, 'depth', 'Depth', 'count', 'Count');
    const depth =
      typeof depthRaw === 'number' && Number.isFinite(depthRaw) && depthRaw >= 0 ? Math.floor(depthRaw) : 0;
    if (queue === '' || isForbiddenAdminKey(queue)) {
      continue;
    }
    out.push({ queue: queue.slice(0, 80), depth });
  }
  return out;
}

// --- DLQ --------------------------------------------------------------------

export interface DlqReasonView {
  readonly code: string;
  readonly count: number;
  /** Backend-advertised controls for this reason; empty means read-only. */
  readonly actions: readonly string[];
}

export interface DlqView {
  readonly depth: number;
  readonly oldestEnqueuedAt: string;
  readonly oldestEntryAge: string;
  readonly reasons: readonly DlqReasonView[];
}

export interface DlqRowView {
  readonly id: string;
  readonly code: string;
  readonly count: number;
  /** Backend-advertised controls only; empty means read-only row. */
  readonly actions: readonly string[];
}

/** True when the backend advertised this DLQ control. Pure. */
export function isAdvertisedDlqAction(action: string): boolean {
  return ADVERTISED_DLQ_ACTIONS.has(action.trim().toLowerCase());
}

/** Keeps only backend-advertised DLQ actions (allowlist). Pure. */
export function filterAdvertisedDlqActions(raw: unknown): readonly string[] {
  if (!Array.isArray(raw)) {
    return [];
  }
  const out: string[] = [];
  for (const entry of raw) {
    if (typeof entry === 'string' && isAdvertisedDlqAction(entry) && !out.includes(entry.toLowerCase())) {
      out.push(entry.toLowerCase());
    }
  }
  return out;
}

/** Parses `GET /admin/diagnostics/dlq` (zero-shape safe). Pure. */
export function parseDlqSummary(raw: unknown): DlqView {
  const record = toRecord(raw) ?? {};
  const depthRaw = pick(record, 'depth', 'Depth');
  const depth = typeof depthRaw === 'number' && Number.isFinite(depthRaw) && depthRaw >= 0 ? Math.floor(depthRaw) : 0;
  const reasonsRaw = pick(record, 'topReasons', 'TopReasons', 'reasons', 'Reasons');
  const reasons: DlqReasonView[] = [];
  if (Array.isArray(reasonsRaw)) {
    for (const entry of reasonsRaw) {
      const row = toRecord(entry);
      if (row === undefined) {
        continue;
      }
      const code = toNonEmptyString(pick(row, 'code', 'Code')) ?? '';
      const countRaw = pick(row, 'count', 'Count');
      const count = typeof countRaw === 'number' && Number.isFinite(countRaw) && countRaw >= 0 ? Math.floor(countRaw) : 0;
      if (code === '' || isForbiddenAdminKey(code)) {
        continue;
      }
      reasons.push({ code: code.slice(0, 80), count, actions: filterAdvertisedDlqActions(row['actions']) });
    }
  }
  return {
    depth,
    oldestEnqueuedAt: toNonEmptyString(pick(record, 'oldestEnqueuedAt', 'OldestEnqueuedAt')) ?? '',
    oldestEntryAge: toDisplayText(pick(record, 'oldestEntryAge', 'OldestEntryAge'), 80),
    reasons,
  };
}

/** Builds DLQ rows from the summary reasons; row actions come only from the backend `actions` field. Pure. */
export function dlqRowsFromSummary(summary: DlqView, actionsByCode?: Readonly<Record<string, readonly string[]>>): DlqRowView[] {
  return summary.reasons.map((reason, index) => ({
    id: `${reason.code}-${String(index)}`,
    code: reason.code,
    count: reason.count,
    actions: actionsByCode !== undefined ? filterAdvertisedDlqActions(actionsByCode[reason.code] ?? []) : reason.actions,
  }));
}

// --- Leases -----------------------------------------------------------------

export interface LeaseView {
  readonly id: string;
  readonly stageType: string;
  readonly status: string;
  readonly owner: string;
  readonly startedAt: string;
  readonly leaseExpiresAt: string;
  /** Milliseconds since start; undefined when unparseable. */
  readonly ageMs: number | undefined;
}

/** Human age for an orphan lease (`3h 12m`, `45s`, `—`). Pure. */
export function formatLeaseAge(ageMs: number | undefined): string {
  if (ageMs === undefined || !Number.isFinite(ageMs) || ageMs < 0) {
    return '—';
  }
  const totalSeconds = Math.floor(ageMs / 1000);
  if (totalSeconds < 60) {
    return `${String(totalSeconds)}s`;
  }
  const totalMinutes = Math.floor(totalSeconds / 60);
  if (totalMinutes < 60) {
    return `${String(totalMinutes)}m`;
  }
  const hours = Math.floor(totalMinutes / 60);
  if (hours < 48) {
    return `${String(hours)}h ${String(totalMinutes % 60)}m`;
  }
  return `${String(Math.floor(hours / 24))}d ${String(hours % 24)}h`;
}

function leaseAgeMs(startedAt: string, nowMs: number): number | undefined {
  if (startedAt === '') {
    return undefined;
  }
  const parsed = Date.parse(startedAt);
  if (Number.isNaN(parsed)) {
    return undefined;
  }
  const age = nowMs - parsed;
  return age >= 0 ? age : 0;
}

/**
 * Parses `GET /admin/diagnostics/leases` page envelopes. Owner renders as a
 * display hint only; lease tokens are dropped (never parsed, never kept).
 * Pure. `nowMs` is injectable for tests.
 */
export function parseStaleLeases(raw: unknown, nowMs: number = Date.now()): LeaseView[] {
  const record = toRecord(raw);
  const items = Array.isArray(raw) ? raw : (record !== undefined ? pick(record, 'items', 'Items') : undefined);
  if (!Array.isArray(items)) {
    return [];
  }
  const out: LeaseView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(row, 'stageExecutionId', 'StageExecutionId', 'id', 'Id')) ?? '';
    if (id === '' || looksLikeReservationId(id)) {
      continue;
    }
    const ownerRaw = toDisplayText(pick(row, 'ownerHint', 'OwnerHint', 'leaseOwner', 'LeaseOwner', 'owner', 'Owner'), 80);
    const startedAt = toNonEmptyString(pick(row, 'startedAt', 'StartedAt')) ?? '';
    out.push({
      id: id.slice(0, 80),
      stageType: toDisplayText(pick(row, 'stageType', 'StageType'), 80) === '' ? 'Unknown' : toDisplayText(pick(row, 'stageType', 'StageType'), 80),
      status: toDisplayText(pick(row, 'status', 'Status'), 80) === '' ? 'Unknown' : toDisplayText(pick(row, 'status', 'Status'), 80),
      owner: sanitizeDisplayName(ownerRaw),
      startedAt,
      leaseExpiresAt: toNonEmptyString(pick(row, 'leaseExpiresAt', 'LeaseExpiresAt')) ?? '',
      ageMs: leaseAgeMs(startedAt, nowMs),
    });
  }
  return out;
}

// --- Orphans ----------------------------------------------------------------

export interface OrphanView {
  readonly id: string;
  readonly sizeBytes: number | undefined;
  readonly mediaFormat: string;
  readonly contentHashPrefix: string;
  readonly createdAt: string;
}

/**
 * Parses `GET /admin/diagnostics/orphans` cursor pages. Keeps ids, sizes,
 * hashes (truncated prefix), and timestamps only — never storage keys or
 * bytes. Pure.
 */
export function parseOrphans(raw: unknown): OrphanView[] {
  const record = toRecord(raw);
  const items = Array.isArray(raw) ? raw : (record !== undefined ? pick(record, 'items', 'Items') : undefined);
  if (!Array.isArray(items)) {
    return [];
  }
  const out: OrphanView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(row, 'contentObjectId', 'ContentObjectId', 'id', 'Id')) ?? '';
    if (id === '') {
      continue;
    }
    const sizeRaw = pick(row, 'sizeBytes', 'SizeBytes');
    const hash = toNonEmptyString(pick(row, 'contentHash', 'ContentHash')) ?? '';
    out.push({
      id: id.slice(0, 80),
      sizeBytes: typeof sizeRaw === 'number' && Number.isFinite(sizeRaw) && sizeRaw >= 0 ? Math.floor(sizeRaw) : undefined,
      mediaFormat: toDisplayText(pick(row, 'mediaFormat', 'MediaFormat'), 40) === '' ? 'Unknown' : toDisplayText(pick(row, 'mediaFormat', 'MediaFormat'), 40),
      contentHashPrefix: hash === '' ? '—' : hash.slice(0, 12),
      createdAt: toNonEmptyString(pick(row, 'createdAt', 'CreatedAt')) ?? '',
    });
  }
  return out;
}

// --- Review backlog ---------------------------------------------------------

export interface BacklogProjectView {
  readonly projectId: string;
  readonly openCount: number;
}

export interface BacklogView {
  readonly totalOpen: number;
  readonly byStatus: Readonly<Record<string, number>>;
  readonly bySeverity: Readonly<Record<string, number>>;
  readonly oldestWaitingAt: string;
  readonly perProject: readonly BacklogProjectView[];
}

function parseCountMap(raw: unknown): Record<string, number> {
  const record = toRecord(raw);
  const out: Record<string, number> = {};
  if (record === undefined) {
    return out;
  }
  for (const key of Object.keys(record)) {
    if (isForbiddenAdminKey(key)) {
      continue;
    }
    const value = record[key];
    if (typeof value === 'number' && Number.isFinite(value) && value >= 0) {
      out[key.slice(0, 40)] = Math.floor(value);
    }
  }
  return out;
}

/** Parses `GET /admin/diagnostics/review-backlog`. Pure. */
export function parseReviewBacklog(raw: unknown): BacklogView {
  const record = toRecord(raw) ?? {};
  const totalRaw = pick(record, 'totalOpen', 'TotalOpen');
  const perProjectRaw = pick(record, 'perProject', 'PerProject');
  const perProject: BacklogProjectView[] = [];
  if (Array.isArray(perProjectRaw)) {
    for (const entry of perProjectRaw) {
      const row = toRecord(entry);
      if (row === undefined) {
        continue;
      }
      const projectId = toNonEmptyString(pick(row, 'projectId', 'ProjectId')) ?? '';
      const openRaw = pick(row, 'openCount', 'OpenCount');
      if (projectId === '') {
        continue;
      }
      perProject.push({
        projectId: projectId.slice(0, 80),
        openCount: typeof openRaw === 'number' && Number.isFinite(openRaw) && openRaw >= 0 ? Math.floor(openRaw) : 0,
      });
    }
  }
  return {
    totalOpen: typeof totalRaw === 'number' && Number.isFinite(totalRaw) && totalRaw >= 0 ? Math.floor(totalRaw) : 0,
    byStatus: parseCountMap(pick(record, 'byStatus', 'ByStatus')),
    bySeverity: parseCountMap(pick(record, 'bySeverity', 'BySeverity')),
    oldestWaitingAt: toNonEmptyString(pick(record, 'oldestWaitingAt', 'OldestWaitingAt')) ?? '',
    perProject,
  };
}

// --- Provider health + routes -----------------------------------------------

export interface ProviderHealthView {
  readonly provider: string;
  readonly status: string;
  readonly latencyMsP95: number | undefined;
  readonly errorRate: number;
  readonly lastSuccessAt: string;
  readonly activeRoutes: readonly string[];
  readonly circuitBreakerState: string;
}

/** Parses `GET /admin/provider-health` (secret-free by contract). Pure. */
export function parseProviderHealth(raw: unknown): ProviderHealthView[] {
  const items = Array.isArray(raw) ? raw : [];
  const out: ProviderHealthView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const provider = toNonEmptyString(pick(row, 'provider', 'Provider')) ?? '';
    if (provider === '' || isForbiddenAdminKey(provider)) {
      continue;
    }
    const latencyRaw = pick(row, 'latencyMsP95', 'LatencyMsP95');
    const errorRaw = pick(row, 'errorRate', 'ErrorRate');
    const routesRaw = pick(row, 'activeRoutes', 'ActiveRoutes');
    const routes: string[] = [];
    if (Array.isArray(routesRaw)) {
      for (const route of routesRaw) {
        if (typeof route === 'string' && route !== '' && !isForbiddenAdminKey(route)) {
          routes.push(route.slice(0, 80));
        }
      }
    }
    out.push({
      provider: provider.slice(0, 80),
      status: toDisplayText(pick(row, 'status', 'Status'), 40) === '' ? 'Unknown' : toDisplayText(pick(row, 'status', 'Status'), 40),
      latencyMsP95: typeof latencyRaw === 'number' && Number.isFinite(latencyRaw) && latencyRaw >= 0 ? latencyRaw : undefined,
      errorRate: typeof errorRaw === 'number' && Number.isFinite(errorRaw) && errorRaw >= 0 ? errorRaw : 0,
      lastSuccessAt: toNonEmptyString(pick(row, 'lastSuccessAt', 'LastSuccessAt')) ?? '',
      activeRoutes: routes,
      circuitBreakerState: toDisplayText(pick(row, 'circuitBreakerState', 'CircuitBreakerState'), 40) === '' ? 'Unknown' : toDisplayText(pick(row, 'circuitBreakerState', 'CircuitBreakerState'), 40),
    });
  }
  return out;
}

export interface ProviderRouteView {
  readonly capability: string;
  readonly provider: string;
  readonly priority: number;
  readonly enabled: boolean;
}

/** Parses `GET /admin/provider-routes` (names only). Pure. */
export function parseProviderRoutes(raw: unknown): ProviderRouteView[] {
  const items = Array.isArray(raw) ? raw : [];
  const out: ProviderRouteView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const capability = toNonEmptyString(pick(row, 'capability', 'Capability')) ?? '';
    const provider = toNonEmptyString(pick(row, 'provider', 'Provider')) ?? '';
    if (capability === '' || provider === '' || isForbiddenAdminKey(capability) || isForbiddenAdminKey(provider)) {
      continue;
    }
    const priorityRaw = pick(row, 'priority', 'Priority');
    out.push({
      capability: capability.slice(0, 80),
      provider: provider.slice(0, 80),
      priority: typeof priorityRaw === 'number' && Number.isFinite(priorityRaw) ? Math.floor(priorityRaw) : 0,
      enabled: pick(row, 'enabled', 'Enabled') === true,
    });
  }
  return out;
}

// --- Usage + quotas ---------------------------------------------------------

export interface UsageView {
  readonly storageUsedBytes: number | undefined;
  readonly storageQuotaBytes: number | undefined;
  readonly monthCostUsd: number | undefined;
  readonly projectsTodayRemaining: number | undefined;
  readonly activeRuns: number;
  readonly pendingReviews: number;
  readonly totalProjects: number;
}

/** Parses `GET /admin/usage` (counts and bytes only). Pure. */
export function parseUsage(raw: unknown): UsageView {
  const record = toRecord(raw) ?? {};
  const count = (value: unknown): number => {
    return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? Math.floor(value) : 0;
  };
  const optional = (value: unknown): number | undefined => {
    return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
  };
  return {
    storageUsedBytes: optional(pick(record, 'storageUsedBytes', 'StorageUsedBytes')),
    storageQuotaBytes: optional(pick(record, 'storageQuotaBytes', 'StorageQuotaBytes')),
    monthCostUsd: optional(pick(record, 'monthCostUsd', 'MonthCostUsd')),
    projectsTodayRemaining: optional(pick(record, 'projectsTodayRemaining', 'ProjectsTodayRemaining')),
    activeRuns: count(pick(record, 'activeRuns', 'ActiveRuns')),
    pendingReviews: count(pick(record, 'pendingReviews', 'PendingReviews')),
    totalProjects: count(pick(record, 'totalProjects', 'TotalProjects')),
  };
}

export interface QuotasView {
  readonly maxActiveProjects: number | undefined;
  readonly maxProjectsPerDay: number | undefined;
  readonly maxCostPerProject: number | undefined;
  readonly maxStorageBytes: number | undefined;
  readonly maxConcurrentStagesPerTenant: number | undefined;
}

/** Parses `GET /admin/quotas` (frozen limits, never secrets). Pure. */
export function parseQuotas(raw: unknown): QuotasView {
  const record = toRecord(raw) ?? {};
  const optional = (value: unknown): number | undefined => {
    return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? value : undefined;
  };
  return {
    maxActiveProjects: optional(pick(record, 'maxActiveProjects', 'MaxActiveProjects')),
    maxProjectsPerDay: optional(pick(record, 'maxProjectsPerDay', 'MaxProjectsPerDay')),
    maxCostPerProject: optional(pick(record, 'maxCostPerProject', 'MaxCostPerProject')),
    maxStorageBytes: optional(pick(record, 'maxStorageBytes', 'MaxStorageBytes')),
    maxConcurrentStagesPerTenant: optional(pick(record, 'maxConcurrentStagesPerTenant', 'MaxConcurrentStagesPerTenant')),
  };
}

/** Usage ratio in [0, +Infinity) for usage-vs-quota bars. Zero on unknown quota. Pure. */
export function usageRatio(used: number | undefined, quota: number | undefined): number {
  if (used === undefined || quota === undefined || quota <= 0 || used <= 0) {
    return 0;
  }
  return used / quota;
}

// --- Tenants + users --------------------------------------------------------

export interface TenantView {
  readonly id: string;
  readonly name: string;
  readonly slug: string;
}

/** Parses `GET /admin/tenants` (id/name/slug only). Unknown shapes → []. Pure. */
export function parseTenants(raw: unknown): TenantView[] {
  const record = toRecord(raw);
  const items = Array.isArray(raw) ? raw : (record !== undefined ? pick(record, 'items', 'Items', 'tenants', 'Tenants') : undefined);
  if (!Array.isArray(items)) {
    return [];
  }
  const out: TenantView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(row, 'id', 'Id', 'tenantId', 'TenantId')) ?? '';
    if (id === '') {
      continue;
    }
    out.push({
      id: id.slice(0, 80),
      name: toDisplayText(pick(row, 'name', 'Name'), 120) === '' ? 'Unnamed tenant' : toDisplayText(pick(row, 'name', 'Name'), 120),
      slug: toDisplayText(pick(row, 'slug', 'Slug', 'tenantSlug', 'TenantSlug'), 80),
    });
  }
  return out;
}

export interface AdminUserView {
  readonly id: string;
  readonly displayName: string;
  readonly roles: readonly string[];
}

/** Parses `GET /admin/users` (ids, display hints, role names only). Pure. */
export function parseAdminUsers(raw: unknown): AdminUserView[] {
  const record = toRecord(raw);
  const items = Array.isArray(raw) ? raw : (record !== undefined ? pick(record, 'items', 'Items', 'users', 'Users') : undefined);
  if (!Array.isArray(items)) {
    return [];
  }
  const out: AdminUserView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const id = toNonEmptyString(pick(row, 'id', 'Id', 'userId', 'UserId')) ?? '';
    if (id === '') {
      continue;
    }
    const rolesRaw = pick(row, 'roles', 'Roles');
    const roles: string[] = [];
    if (Array.isArray(rolesRaw)) {
      for (const role of rolesRaw) {
        if (typeof role === 'string' && role !== '' && !isForbiddenAdminKey(role)) {
          roles.push(role.slice(0, 40));
        }
      }
    }
    out.push({
      id: id.slice(0, 80),
      displayName: sanitizeDisplayName(toDisplayText(pick(row, 'displayName', 'DisplayName', 'name', 'Name', 'email', 'Email'), 80)),
      roles,
    });
  }
  return out;
}

// --- Audit events -----------------------------------------------------------

export interface AuditEventView {
  readonly id: string;
  readonly timestamp: string;
  readonly actor: string;
  readonly action: string;
  readonly summary: string;
  readonly hasAdvanced: boolean;
  readonly advanced: Readonly<Record<string, string>>;
}

function extractAuditAdvanced(record: Record<string, unknown>): Record<string, string> {
  const out: Record<string, string> = {};
  for (const key of Object.keys(record)) {
    if (isForbiddenAdminKey(key)) {
      continue;
    }
    if (!AUDIT_ADVANCED_ALLOWLIST.has(normalizeKey(key).replace(/_/g, '')) && !AUDIT_ADVANCED_ALLOWLIST.has(key.toLowerCase())) {
      continue;
    }
    const text = toDisplayText(record[key], 200);
    if (text !== '' && !looksLikeReservationId(text) && !text.includes('http')) {
      out[key] = text;
    }
  }
  return out;
}

/** Parses one audit row (timestamp/actor/action/summary + allowlisted advanced). Pure. */
export function parseAuditEvent(raw: unknown): AuditEventView | undefined {
  const record = toRecord(raw);
  if (record === undefined) {
    return undefined;
  }
  const id = toNonEmptyString(pick(record, 'id', 'Id', 'eventId', 'EventId')) ?? '';
  if (id === '') {
    return undefined;
  }
  const summary = toDisplayText(pick(record, 'summary', 'Summary', 'message', 'Message', 'description', 'Description'));
  if (summary === '') {
    return undefined;
  }
  const actorRaw = toDisplayText(pick(record, 'actor', 'Actor', 'actorName', 'ActorName', 'subject', 'Subject', 'userId', 'UserId'));
  const actionRaw = toDisplayText(pick(record, 'action', 'Action', 'type', 'Type', 'eventType', 'EventType'));
  const advanced = extractAuditAdvanced(record);
  return {
    id: id.slice(0, 80),
    timestamp: toNonEmptyString(pick(record, 'timestamp', 'Timestamp', 'occurredAt', 'OccurredAt', 'createdAt', 'CreatedAt')) ?? '',
    actor: sanitizeDisplayName(actorRaw),
    action: actionRaw.trim() === '' ? 'Updated' : actionRaw.trim().slice(0, 80),
    summary: summary.slice(0, 280),
    hasAdvanced: Object.keys(advanced).length > 0,
    advanced,
  };
}

/** Parses audit pages; skips bad rows, dedupes by id, preserves order. Pure. */
export function parseAuditEvents(raw: unknown): AuditEventView[] {
  const record = toRecord(raw);
  const items = Array.isArray(raw) ? raw : (record !== undefined ? pick(record, 'items', 'Items') : undefined);
  if (!Array.isArray(items)) {
    return [];
  }
  const seen = new Set<string>();
  const out: AuditEventView[] = [];
  for (const entry of items) {
    const parsed = parseAuditEvent(entry);
    if (parsed !== undefined && !seen.has(parsed.id)) {
      seen.add(parsed.id);
      out.push(parsed);
    }
  }
  return out;
}

// --- Retention --------------------------------------------------------------

export interface RetentionPolicyView {
  readonly scope: string;
  readonly retentionDays: number | undefined;
  readonly description: string;
}

/** Parses `GET /admin/retention` (scopes + day counts only). Pure. */
export function parseRetentionPolicies(raw: unknown): RetentionPolicyView[] {
  const record = toRecord(raw);
  const items = Array.isArray(raw)
    ? raw
    : (record !== undefined ? pick(record, 'policies', 'Policies', 'items', 'Items') : undefined);
  if (!Array.isArray(items)) {
    return [];
  }
  const out: RetentionPolicyView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const scope = toNonEmptyString(pick(row, 'scope', 'Scope')) ?? '';
    if (scope === '' || isForbiddenAdminKey(scope)) {
      continue;
    }
    const daysRaw = pick(row, 'retentionDays', 'RetentionDays', 'days', 'Days');
    out.push({
      scope: scope.slice(0, 80),
      retentionDays:
        typeof daysRaw === 'number' && Number.isFinite(daysRaw) && daysRaw >= 0 ? Math.floor(daysRaw) : undefined,
      description: toDisplayText(pick(row, 'description', 'Description'), 200),
    });
  }
  return out;
}

// --- Feature flags ----------------------------------------------------------

export interface FeatureFlagView {
  readonly key: string;
  readonly enabled: boolean;
  readonly description: string;
  /** True when the backend reports a rollout freeze (toggle reverts, 423 explains). */
  readonly frozen: boolean;
}

/** Parses `GET /admin/feature-flags` (keys + booleans only). Pure. */
export function parseFeatureFlags(raw: unknown): FeatureFlagView[] {
  const record = toRecord(raw);
  const items = Array.isArray(raw)
    ? raw
    : (record !== undefined ? pick(record, 'flags', 'Flags', 'items', 'Items') : undefined);
  if (!Array.isArray(items)) {
    return [];
  }
  const out: FeatureFlagView[] = [];
  for (const entry of items) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    const key = toNonEmptyString(pick(row, 'key', 'Key', 'name', 'Name')) ?? '';
    if (key === '' || isForbiddenAdminKey(key)) {
      continue;
    }
    out.push({
      key: key.slice(0, 80),
      enabled: pick(row, 'enabled', 'Enabled') === true,
      description: toDisplayText(pick(row, 'description', 'Description'), 200),
      frozen: pick(row, 'frozen', 'Frozen') === true,
    });
  }
  return out;
}

// --- Error predicates -------------------------------------------------------

interface ErrorShape {
  readonly status?: unknown;
  readonly code?: unknown;
}

function errorShape(error: unknown): ErrorShape {
  if (typeof error === 'object' && error !== null) {
    return error as ErrorShape;
  }
  return {};
}

/** True for elevated-gating denials (403 / FORBIDDEN), mid-session included. Pure. */
export function isAdminForbiddenError(error: unknown): boolean {
  const shape = errorShape(error);
  return shape.status === 403 || shape.code === 'FORBIDDEN' || shape.code === 'USER_DISABLED';
}

/** True for DLQ redrive races (409 already redriven → refresh + toast). Pure. */
export function isAdminConflictError(error: unknown): boolean {
  const shape = errorShape(error);
  return shape.status === 409 || (typeof shape.code === 'string' && shape.code.includes('CONFLICT'));
}

/** True for rollout-freeze denials (423 → dialog explains, toggle reverts). Pure. */
export function isAdminFreezeError(error: unknown): boolean {
  const shape = errorShape(error);
  return shape.status === 423 || shape.code === 'ROLLOUT_FROZEN';
}

/** True for unprovisioned conventional reads (404 unknown admin route). Pure. */
export function isAdminUnknownRouteError(error: unknown): boolean {
  const shape = errorShape(error);
  if (shape.status !== 404) {
    return false;
  }
  if (typeof shape.code === 'string' && shape.code === 'NOT_FOUND') {
    return true;
  }
  return shape.code === undefined || shape.code === '';
}
