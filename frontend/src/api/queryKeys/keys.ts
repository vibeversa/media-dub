import type { SseEventType } from '../client/index.js';

/**
 * Hierarchical query-key factory (Task 017, R1).
 *
 * Every TanStack `queryKey` in feature code must reference this factory;
 * inline `queryKey: [...]` literals outside this module are forbidden (the
 * `queryKeys.test.ts` R1 scan fails the suite if one appears). Keys nest
 * under their parent scope so prefix invalidation cascades: invalidating
 * `['projects', 'detail', id]` also drops that project's workspace,
 * progress, segments, speakers, and exports entries.
 */
export type QueryKey = readonly unknown[];

export interface PageParams {
  readonly page?: number;
  readonly pageSize?: number;
}

/**
 * Server-side project-list params (Task 021). Extends the page envelope with
 * the filter/sort dimensions `GET /projects` accepts server-side
 * (`ProjectsController.List`: status, ownerId, archived, sort, sortDir).
 * Client-only refinements (target language, created-date range) stay out of
 * the key: they filter the fetched page in memory without refetching.
 */
export interface ProjectListParams extends PageParams {
  readonly status?: string;
  readonly ownerId?: string;
  readonly archived?: string;
  readonly sort?: string;
  readonly sortDir?: 'asc' | 'desc';
}

/**
 * Server-side review-queue params (Task 031). `GET /projects/{id}/reviews`
 * is page-based; the studio sends every active filter as a query param so
 * narrowing happens server-side (the test asserts the query string). Unknown
 * params are ignored by the backend but still partition the cache key.
 */
export interface ReviewListParams extends PageParams {
  readonly severity?: string;
  readonly status?: string;
  readonly type?: string;
  readonly speakerId?: string;
  readonly language?: string;
  readonly age?: string;
}

function withDefaults(params: PageParams | undefined): PageParams {
  return { ...(params ?? {}) };
}

export const queryKeys = {
  dashboard: {
    all: ['dashboard'] as const,
    summary: (): QueryKey => ['dashboard', 'summary'],
  },
  projects: {
    all: ['projects'] as const,
    lists: (): QueryKey => ['projects', 'list'],
    list: (params?: ProjectListParams): QueryKey => ['projects', 'list', withDefaults(params)],
    details: (): QueryKey => ['projects', 'detail'],
    detail: (projectId: string): QueryKey => ['projects', 'detail', projectId],
  },
  project: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId],
    detail: (projectId: string): QueryKey => ['projects', 'detail', projectId],
  },
  workspace: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'workspace'],
    detail: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'workspace'],
  },
  progress: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'progress'],
    detail: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'progress'],
  },
  segments: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'segments'],
    lists: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'segments', 'list'],
    list: (projectId: string, params?: PageParams): QueryKey => [
      'projects',
      'detail',
      projectId,
      'segments',
      'list',
      withDefaults(params),
    ],
    details: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'segments', 'detail'],
    detail: (projectId: string, segmentId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'segments',
      'detail',
      segmentId,
    ],
  },
  segment: {
    all: (projectId: string, segmentId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'segments',
      'detail',
      segmentId,
    ],
    detail: (projectId: string, segmentId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'segments',
      'detail',
      segmentId,
    ],
  },
  speakers: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'speakers'],
    lists: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'speakers', 'list'],
    list: (projectId: string, params?: PageParams): QueryKey => [
      'projects',
      'detail',
      projectId,
      'speakers',
      'list',
      withDefaults(params),
    ],
    details: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'speakers', 'detail'],
    detail: (projectId: string, speakerId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'speakers',
      'detail',
      speakerId,
    ],
  },
  review: {
    all: ['reviews'] as const,
    lists: (): QueryKey => ['reviews', 'list'],
    list: (projectId: string, params?: ReviewListParams): QueryKey => ['reviews', 'list', projectId, withDefaults(params)],
    details: (): QueryKey => ['reviews', 'detail'],
    detail: (reviewId: string): QueryKey => ['reviews', 'detail', reviewId],
  },
  reviewContext: {
    all: (reviewId: string): QueryKey => ['reviews', 'context', reviewId],
    detail: (reviewId: string): QueryKey => ['reviews', 'context', reviewId],
  },
  voices: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'voices'],
    available: (projectId: string, speakerId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'voices',
      'available',
      speakerId,
    ],
    preview: (projectId: string, previewId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'voices',
      'preview',
      previewId,
    ],
  },
  timeline: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'timeline'],
    media: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'timeline', 'media'],
    peaks: (projectId: string, resolution?: number): QueryKey =>
      resolution === undefined
        ? ['projects', 'detail', projectId, 'timeline', 'peaks']
        : ['projects', 'detail', projectId, 'timeline', 'peaks', resolution],
  },
  quality: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'quality'],
    detail: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'quality'],
  },
  outputs: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'outputs'],
    detail: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'outputs'],
  },
  runs: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'runs'],
    lists: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'runs', 'list'],
    list: (projectId: string, params?: PageParams): QueryKey => [
      'projects',
      'detail',
      projectId,
      'runs',
      'list',
      withDefaults(params),
    ],
  },
  exports: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'exports'],
    lists: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'exports', 'list'],
    list: (projectId: string, params?: PageParams): QueryKey => [
      'projects',
      'detail',
      projectId,
      'exports',
      'list',
      withDefaults(params),
    ],
    details: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'exports', 'detail'],
    detail: (projectId: string, exportId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'exports',
      'detail',
      exportId,
    ],
  },
  notifications: {
    all: ['notifications'] as const,
    lists: (): QueryKey => ['notifications', 'list'],
    list: (params?: PageParams): QueryKey => ['notifications', 'list', withDefaults(params)],
    unreadCount: (): QueryKey => ['notifications', 'unread-count'],
  },
  activity: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'activity'],
    lists: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'activity', 'list'],
    list: (projectId: string, params?: PageParams): QueryKey => [
      'projects',
      'detail',
      projectId,
      'activity',
      'list',
      withDefaults(params),
    ],
  },
  preferences: {
    all: ['me', 'preferences'] as const,
    lists: (): QueryKey => ['me', 'preferences'],
    list: (): QueryKey => ['me', 'preferences'],
    detail: (key: string): QueryKey => ['me', 'preferences', key],
  },
  cost: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'cost'],
    detail: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'cost'],
  },
  transcript: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'transcript'],
    list: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'transcript', 'list'],
    detail: (projectId: string, segmentId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'transcript',
      'detail',
      segmentId,
    ],
  },
  translations: {
    all: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'translations'],
    list: (projectId: string): QueryKey => ['projects', 'detail', projectId, 'translations', 'list'],
    detail: (projectId: string, segmentId: string): QueryKey => [
      'projects',
      'detail',
      projectId,
      'translations',
      'detail',
      segmentId,
    ],
  },
  /**
   * Product-wide feature flags (Task 048).
   *
   * Session-scoped under `me`, beside `preferences`: the values come out of the
   * `featureFlags` slice of the same `GET /me` document that resolves
   * permissions and locale, so they share a lifetime and a cache-clearing rule.
   * `authStore`'s session resolution seeds this entry with what it already read,
   * which is what keeps the app at one `/me` read per session rather than two.
   */
  me: {
    all: ['me', 'feature-flags'] as const,
    featureFlags: (): QueryKey => ['me', 'feature-flags'],
  },
  /**
   * Optional enrichment (Task 044, §19.1–§19.3). Deliberately a *sibling*
   * scope, never nested inside transcript/translations/timeline/outputs:
   * video-intel and lip-sync results are separate artifacts that link to core
   * data, so a prefix invalidation of `['projects','detail',id,'transcript']`
   * must not be able to cascade into them, and vice versa. R2 depends on that
   * asymmetry being structural rather than a convention.
   *
   * There is deliberately NO `flags` entry here any more: the flag read moved to
   * `me.featureFlags()` in Task 048, and two keys for one document is one key
   * too many.
   */
  enrichment: {
    all: ['enrichment'] as const,
    videoIntel: (projectId: string): QueryKey => ['enrichment', 'video-intel', projectId],
    lipSync: (projectId: string): QueryKey => ['enrichment', 'lip-sync', projectId],
  },
  admin: {
    all: ['admin'] as const,
    section: (name: string, params?: PageParams): QueryKey => ['admin', name, withDefaults(params)],
  },
  adminTenants: {
    all: ['admin', 'tenants'] as const,
    lists: (): QueryKey => ['admin', 'tenants', 'list'],
    list: (params?: PageParams): QueryKey => ['admin', 'tenants', 'list', withDefaults(params)],
    details: (): QueryKey => ['admin', 'tenants', 'detail'],
    detail: (tenantId: string): QueryKey => ['admin', 'tenants', 'detail', tenantId],
  },
  adminUsers: {
    all: ['admin', 'users'] as const,
    lists: (): QueryKey => ['admin', 'users', 'list'],
    list: (params?: PageParams): QueryKey => ['admin', 'users', 'list', withDefaults(params)],
    details: (): QueryKey => ['admin', 'users', 'detail'],
    detail: (userId: string): QueryKey => ['admin', 'users', 'detail', userId],
  },
  diagnostics: {
    all: ['admin', 'diagnostics'] as const,
    queues: (): QueryKey => ['admin', 'diagnostics', 'queues'],
    dlq: (params?: PageParams): QueryKey => ['admin', 'diagnostics', 'dlq', withDefaults(params)],
    leases: (params?: PageParams): QueryKey => ['admin', 'diagnostics', 'leases', withDefaults(params)],
    orphans: (params?: { readonly pageSize?: number; readonly cursor?: string }): QueryKey => [
      'admin',
      'diagnostics',
      'orphans',
      { pageSize: params?.pageSize, cursor: params?.cursor ?? null },
    ],
    backlog: (): QueryKey => ['admin', 'diagnostics', 'review-backlog'],
    usage: (): QueryKey => ['admin', 'diagnostics', 'usage'],
    quotas: (): QueryKey => ['admin', 'diagnostics', 'quotas'],
    health: (): QueryKey => ['admin', 'diagnostics', 'provider-health'],
    routes: (): QueryKey => ['admin', 'diagnostics', 'provider-routes'],
    status: (): QueryKey => ['admin', 'diagnostics', 'status'],
    // Task 044: operator-only local-GPU health. Under `diagnostics` so the
    // prefix invalidation an operator refresh already performs covers it, and
    // separate from `health` because the two report different subjects (the
    // provider pool vs. one device) and must never be conflated.
    localGpu: (): QueryKey => ['admin', 'diagnostics', 'local-gpu'],
  },
};

export interface SseKeyIds {
  readonly projectId?: string;
  readonly reviewId?: string;
}

/**
 * Tenant-scoped cache keys (Task 037, R2).
 *
 * Additive helper: existing feature keys stay project-scoped for prefix
 * invalidation, and callers that cache per-tenant data wrap them with
 * `scopedTenantKey(tenantId, key)` so caches never cross tenants. The tenant
 * segment leads the key (`['tenant', tenantId, ...key]`) so tenant prefix
 * invalidation cascades without touching project nesting.
 */
export function scopedTenantKey(tenantId: string, key: QueryKey): QueryKey {
  if (tenantId.trim() === '') {
    throw new Error('tenantId must not be empty.');
  }
  return ['tenant', tenantId, ...key];
}

/** Whether a key carries the given tenant scope. Pure. */
export function isTenantScopedKey(key: QueryKey, tenantId: string): boolean {
  return key.length >= 2 && key[0] === 'tenant' && key[1] === tenantId;
}

/**
 * SSE event-type → query-key mapping consumed by Task 026 invalidation.
 * Every frozen `SseEventType` has an entry; a new bundle event without one
 * here fails typecheck via the `Record<SseEventType, ...>` annotation.
 */
export const queryKeyRegistry: Record<SseEventType, (ids: SseKeyIds) => readonly QueryKey[]> = {
  'project.status_changed': (ids) =>
    ids.projectId === undefined
      ? [queryKeys.projects.all, queryKeys.dashboard.summary()]
      : [
          queryKeys.project.detail(ids.projectId),
          queryKeys.projects.all,
          queryKeys.dashboard.summary(),
          queryKeys.activity.all(ids.projectId),
        ],
  'run.status_changed': (ids) =>
    ids.projectId === undefined
      ? [queryKeys.projects.all]
      : [
          queryKeys.progress.detail(ids.projectId),
          queryKeys.workspace.detail(ids.projectId),
          queryKeys.quality.detail(ids.projectId),
          queryKeys.outputs.detail(ids.projectId),
          queryKeys.exports.list(ids.projectId),
          queryKeys.project.detail(ids.projectId),
          queryKeys.transcript.list(ids.projectId),
          queryKeys.translations.list(ids.projectId),
          queryKeys.activity.all(ids.projectId),
          queryKeys.cost.detail(ids.projectId),
        ],
  'stage.started': (ids) =>
    ids.projectId === undefined
      ? []
      : [
          queryKeys.progress.detail(ids.projectId),
          queryKeys.workspace.detail(ids.projectId),
          queryKeys.quality.detail(ids.projectId),
          queryKeys.outputs.detail(ids.projectId),
          queryKeys.transcript.list(ids.projectId),
          queryKeys.translations.list(ids.projectId),
          queryKeys.activity.all(ids.projectId),
          queryKeys.cost.detail(ids.projectId),
        ],
  'stage.progress': (ids) =>
    ids.projectId === undefined ? [] : [queryKeys.progress.detail(ids.projectId), queryKeys.activity.all(ids.projectId)],
  'stage.completed': (ids) =>
    ids.projectId === undefined
      ? []
      : [
          queryKeys.progress.detail(ids.projectId),
          queryKeys.workspace.detail(ids.projectId),
          queryKeys.quality.detail(ids.projectId),
          queryKeys.outputs.detail(ids.projectId),
          queryKeys.exports.list(ids.projectId),
          queryKeys.transcript.list(ids.projectId),
          queryKeys.translations.list(ids.projectId),
          queryKeys.activity.all(ids.projectId),
          queryKeys.cost.detail(ids.projectId),
        ],
  'stage.failed': (ids) =>
    ids.projectId === undefined
      ? []
      : [
          queryKeys.progress.detail(ids.projectId),
          queryKeys.workspace.detail(ids.projectId),
          queryKeys.quality.detail(ids.projectId),
          queryKeys.outputs.detail(ids.projectId),
          queryKeys.transcript.list(ids.projectId),
          queryKeys.translations.list(ids.projectId),
          queryKeys.activity.all(ids.projectId),
          queryKeys.cost.detail(ids.projectId),
        ],
  'stage.review_required': (ids) =>
    ids.projectId === undefined
      ? [queryKeys.review.lists()]
      : [
          queryKeys.review.list(ids.projectId),
          queryKeys.progress.detail(ids.projectId),
          queryKeys.workspace.detail(ids.projectId),
          queryKeys.quality.detail(ids.projectId),
          queryKeys.outputs.detail(ids.projectId),
          queryKeys.transcript.list(ids.projectId),
          queryKeys.translations.list(ids.projectId),
          queryKeys.activity.all(ids.projectId),
          queryKeys.cost.detail(ids.projectId),
        ],
  'review.created': (ids) =>
    ids.projectId === undefined
      ? [queryKeys.review.lists(), queryKeys.notifications.unreadCount()]
      : [queryKeys.review.list(ids.projectId), queryKeys.notifications.unreadCount(), queryKeys.activity.all(ids.projectId)],
  'review.resolved': (ids) =>
    ids.reviewId === undefined
      ? [queryKeys.review.lists(), queryKeys.notifications.unreadCount()]
      : [queryKeys.review.detail(ids.reviewId), queryKeys.reviewContext.detail(ids.reviewId), queryKeys.notifications.unreadCount()],
  'export.created': (ids) =>
    ids.projectId === undefined
      ? []
      : [queryKeys.exports.list(ids.projectId), queryKeys.outputs.detail(ids.projectId), queryKeys.activity.all(ids.projectId)],
  'export.completed': (ids) =>
    ids.projectId === undefined
      ? []
      : [
          queryKeys.exports.list(ids.projectId),
          queryKeys.outputs.detail(ids.projectId),
          queryKeys.workspace.detail(ids.projectId),
          queryKeys.transcript.list(ids.projectId),
          queryKeys.translations.list(ids.projectId),
          queryKeys.activity.all(ids.projectId),
          queryKeys.cost.detail(ids.projectId),
        ],
  'export.failed': (ids) =>
    ids.projectId === undefined
      ? []
      : [queryKeys.exports.list(ids.projectId), queryKeys.outputs.detail(ids.projectId), queryKeys.activity.all(ids.projectId)],
  'notification.created': () => [queryKeys.notifications.list(), queryKeys.notifications.unreadCount()],
  'output.ready': (ids) =>
    ids.projectId === undefined
      ? []
      : [
          queryKeys.workspace.detail(ids.projectId),
          queryKeys.quality.detail(ids.projectId),
          queryKeys.outputs.detail(ids.projectId),
          queryKeys.exports.list(ids.projectId),
          queryKeys.transcript.list(ids.projectId),
          queryKeys.translations.list(ids.projectId),
          queryKeys.activity.all(ids.projectId),
          queryKeys.cost.detail(ids.projectId),
        ],
};

/** Resolves the invalidation keys for one SSE frame's event type and IDs. */
export function keysForEvent(eventType: SseEventType, ids?: SseKeyIds): readonly QueryKey[] {
  return queryKeyRegistry[eventType](ids ?? {});
}
