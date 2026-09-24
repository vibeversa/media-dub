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
    list: (params?: PageParams): QueryKey => ['projects', 'list', withDefaults(params)],
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
    list: (projectId: string, params?: PageParams): QueryKey => ['reviews', 'list', projectId, withDefaults(params)],
    details: (): QueryKey => ['reviews', 'detail'],
    detail: (reviewId: string): QueryKey => ['reviews', 'detail', reviewId],
  },
  reviewContext: {
    all: (reviewId: string): QueryKey => ['reviews', 'context', reviewId],
    detail: (reviewId: string): QueryKey => ['reviews', 'context', reviewId],
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
  admin: {
    all: ['admin'] as const,
    section: (name: string, params?: PageParams): QueryKey => ['admin', name, withDefaults(params)],
  },
};

export interface SseKeyIds {
  readonly projectId?: string;
  readonly reviewId?: string;
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
      : [queryKeys.project.detail(ids.projectId), queryKeys.projects.all, queryKeys.dashboard.summary()],
  'run.status_changed': (ids) =>
    ids.projectId === undefined
      ? [queryKeys.projects.all]
      : [queryKeys.progress.detail(ids.projectId), queryKeys.workspace.detail(ids.projectId), queryKeys.project.detail(ids.projectId)],
  'stage.started': (ids) =>
    ids.projectId === undefined ? [] : [queryKeys.progress.detail(ids.projectId), queryKeys.workspace.detail(ids.projectId)],
  'stage.progress': (ids) => (ids.projectId === undefined ? [] : [queryKeys.progress.detail(ids.projectId)]),
  'stage.completed': (ids) =>
    ids.projectId === undefined ? [] : [queryKeys.progress.detail(ids.projectId), queryKeys.workspace.detail(ids.projectId)],
  'stage.failed': (ids) =>
    ids.projectId === undefined ? [] : [queryKeys.progress.detail(ids.projectId), queryKeys.workspace.detail(ids.projectId)],
  'stage.review_required': (ids) =>
    ids.projectId === undefined
      ? [queryKeys.review.lists()]
      : [queryKeys.review.list(ids.projectId), queryKeys.progress.detail(ids.projectId), queryKeys.workspace.detail(ids.projectId)],
  'review.created': (ids) =>
    ids.projectId === undefined
      ? [queryKeys.review.lists(), queryKeys.notifications.unreadCount()]
      : [queryKeys.review.list(ids.projectId), queryKeys.notifications.unreadCount()],
  'review.resolved': (ids) =>
    ids.reviewId === undefined
      ? [queryKeys.review.lists(), queryKeys.notifications.unreadCount()]
      : [queryKeys.review.detail(ids.reviewId), queryKeys.reviewContext.detail(ids.reviewId), queryKeys.notifications.unreadCount()],
  'export.created': (ids) =>
    ids.projectId === undefined ? [] : [queryKeys.exports.list(ids.projectId)],
  'export.completed': (ids) =>
    ids.projectId === undefined
      ? []
      : [queryKeys.exports.list(ids.projectId), queryKeys.workspace.detail(ids.projectId)],
  'export.failed': (ids) => (ids.projectId === undefined ? [] : [queryKeys.exports.list(ids.projectId)]),
  'notification.created': () => [queryKeys.notifications.list(), queryKeys.notifications.unreadCount()],
  'output.ready': (ids) =>
    ids.projectId === undefined
      ? []
      : [queryKeys.workspace.detail(ids.projectId), queryKeys.exports.list(ids.projectId)],
};

/** Resolves the invalidation keys for one SSE frame's event type and IDs. */
export function keysForEvent(eventType: SseEventType, ids?: SseKeyIds): readonly QueryKey[] {
  return queryKeyRegistry[eventType](ids ?? {});
}
