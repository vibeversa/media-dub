import { apiClient } from '../../api/client/index.js';
import type { ListProjectsParams, Project, ProjectListResponse } from '../../api/client/index.js';

/**
 * Project-list data surface (Task 021) over the Task 017 transport.
 *
 * - One server read: `GET /projects` with page-based pagination and
 *   server-side sort/filter. Shapes come from the generated barrel only
 *   (`src/api/client`); no deep `api/generated` imports (enforced by
 *   `no-restricted-imports`).
 * - The v1 bundle documents only page/pageSize/sort/sortDir for the list
 *   endpoint, but `ProjectsController.List` additionally accepts
 *   status/ownerId/search/archived. Those extras are sent via a narrow cast
 *   at the call site (runtime-safe: the transport serializes any query
 *   record); everything else uses generated types exactly.
 * - No backend `actions[]` allowlist exists for projects (only the review
 *   context carries one). Valid-actions-only is therefore a pure function of
 *   project status + archived flag + `/me` permission hints
 *   (`getAllowedActions`) that mirrors the server guards; the server stays
 *   authoritative (403/409 → toast + row refresh).
 */

export type { Project, ProjectListResponse };

/** Archived tri-state. Default `active` excludes archived rows (R4). */
export type ArchivedFilter = 'active' | 'archived' | 'all';

/** UI sort keys. `activity`/`progress` map to server `updatedAt` (approximate). */
export type ProjectSort = 'created' | 'activity' | 'name' | 'progress';

export type SortDir = 'asc' | 'desc';

export interface ProjectFilters {
  readonly status: string;
  readonly targetLanguage: string;
  readonly archived: ArchivedFilter;
  readonly owner: string;
  readonly createdFrom: string;
  readonly createdTo: string;
  readonly sort: ProjectSort;
  readonly sortDir: SortDir;
  readonly page: number;
  readonly pageSize: number;
}

export const DEFAULT_PAGE_SIZE = 20;
export const PAGE_SIZE_OPTIONS: readonly number[] = [10, 20, 50, 100];

export const DEFAULT_FILTERS: ProjectFilters = {
  status: '',
  targetLanguage: '',
  archived: 'active',
  owner: '',
  createdFrom: '',
  createdTo: '',
  sort: 'created',
  sortDir: 'desc',
  page: 1,
  pageSize: DEFAULT_PAGE_SIZE,
};

/** All ten backend project statuses (`ProjectStatus` enum, Task 007). */
export const PROJECT_STATUSES: readonly string[] = [
  'Created',
  'Uploading',
  'MediaReady',
  'MediaRejected',
  'Processing',
  'Cancelling',
  'Cancelled',
  'Completed',
  'Failed',
  'ManualReviewRequired',
];

const ARCHIVED_VALUES: readonly ArchivedFilter[] = ['active', 'archived', 'all'];
const SORT_VALUES: readonly ProjectSort[] = ['created', 'activity', 'name', 'progress'];

function parsePositiveInt(raw: string | null, fallback: number): number {
  if (raw === null || raw === '') {
    return fallback;
  }
  const parsed = Number.parseInt(raw, 10);
  return Number.isInteger(parsed) && parsed >= 1 ? parsed : fallback;
}

function parsePageSize(raw: string | null): number {
  const parsed = parsePositiveInt(raw, DEFAULT_PAGE_SIZE);
  return parsed > 100 ? 100 : parsed;
}

/**
 * Parses URL search params into filters (R1). Unknown/invalid values fall
 * back to defaults so hand-edited links never break the page.
 */
export function parseFilters(params: URLSearchParams): ProjectFilters {
  const archivedRaw = params.get('archived');
  const archived: ArchivedFilter =
    archivedRaw === 'archived' || archivedRaw === 'all' ? archivedRaw : 'active';
  const sortRaw = params.get('sort');
  const sort: ProjectSort =
    sortRaw === 'activity' || sortRaw === 'name' || sortRaw === 'progress' ? sortRaw : 'created';
  const dirRaw = params.get('sortDir');
  const sortDir: SortDir = dirRaw === 'asc' ? 'asc' : 'desc';
  const statusRaw = (params.get('status') ?? '').trim();
  return {
    status: PROJECT_STATUSES.includes(statusRaw) ? statusRaw : '',
    targetLanguage: (params.get('targetLanguage') ?? '').trim().slice(0, 8),
    archived,
    owner: (params.get('owner') ?? '').trim().slice(0, 128),
    createdFrom: (params.get('from') ?? '').trim().slice(0, 10),
    createdTo: (params.get('to') ?? '').trim().slice(0, 10),
    sort,
    sortDir,
    page: parsePositiveInt(params.get('page'), 1),
    pageSize: parsePageSize(params.get('pageSize')),
  };
}

/**
 * Serializes filters to shareable search params (R1). Defaults are omitted
 * so links stay minimal; `parseFilters` restores them.
 */
export function serializeFilters(filters: ProjectFilters): URLSearchParams {
  const params = new URLSearchParams();
  if (filters.status !== '') {
    params.set('status', filters.status);
  }
  if (filters.targetLanguage !== '') {
    params.set('targetLanguage', filters.targetLanguage);
  }
  if (filters.archived !== 'active') {
    params.set('archived', filters.archived);
  }
  if (filters.owner !== '') {
    params.set('owner', filters.owner);
  }
  if (filters.createdFrom !== '') {
    params.set('from', filters.createdFrom);
  }
  if (filters.createdTo !== '') {
    params.set('to', filters.createdTo);
  }
  if (filters.sort !== 'created') {
    params.set('sort', filters.sort);
  }
  if (filters.sortDir !== 'desc') {
    params.set('sortDir', filters.sortDir);
  }
  if (filters.page !== 1) {
    params.set('page', String(filters.page));
  }
  if (filters.pageSize !== DEFAULT_PAGE_SIZE) {
    params.set('pageSize', String(filters.pageSize));
  }
  return params;
}

/** True when any non-default filter narrows the result (drives EmptyState copy). */
export function hasActiveFilters(filters: ProjectFilters): boolean {
  return (
    filters.status !== '' ||
    filters.targetLanguage !== '' ||
    filters.archived !== 'active' ||
    filters.owner !== '' ||
    filters.createdFrom !== '' ||
    filters.createdTo !== ''
  );
}

export interface ServerProjectQuery {
  readonly page: number;
  readonly pageSize: number;
  readonly sort: 'createdAt' | 'updatedAt' | 'name';
  readonly sortDir: SortDir;
  readonly status?: string;
  readonly ownerId?: string;
  readonly archived?: string;
}

/**
 * Maps UI filters to the server query (R2: pagination/sort server-side, no
 * client-side slicing of server pages). `activity` and `progress` both map to
 * `updatedAt`: progress ordering is therefore approximate (column tooltip
 * says so; no fake precision). `archived=active` (default) sends no param —
 * the backend excludes archived rows unless `archived=true|all` (R4).
 */
export function toServerQuery(filters: ProjectFilters): ServerProjectQuery {
  const sort = filters.sort === 'name' ? 'name' : filters.sort === 'created' ? 'createdAt' : 'updatedAt';
  const query: ServerProjectQuery = {
    page: filters.page,
    pageSize: filters.pageSize,
    sort,
    sortDir: filters.sortDir,
  };
  const withStatus = filters.status === '' ? query : { ...query, status: filters.status };
  const withOwner = filters.owner === '' ? withStatus : { ...withStatus, ownerId: filters.owner };
  if (filters.archived === 'active') {
    return withOwner;
  }
  return { ...withOwner, archived: filters.archived === 'archived' ? 'true' : 'all' };
}

/** True when the UI sort is approximate (server `updatedAt`, not a real progress rank). */
export function isApproximateSort(sort: ProjectSort): boolean {
  return sort === 'progress';
}

function matchesDateRange(createdAt: string | undefined, from: string, to: string): boolean {
  if (from === '' && to === '') {
    return true;
  }
  if (createdAt === undefined || createdAt === '') {
    return false;
  }
  const day = createdAt.slice(0, 10);
  if (from !== '' && day < from) {
    return false;
  }
  if (to !== '' && day > to) {
    return false;
  }
  return true;
}

/**
 * Client-side refinement of the fetched page. Only dimensions the server
 * cannot filter apply here (target language exact match, created-date
 * range); server dimensions are never re-sliced, so pagination stays
 * server-owned (R2). Review-state filtering is intentionally absent: the
 * bundle `Project` carries no review counts, and inventing them would be
 * fake data.
 */
export function applyClientFilters(
  items: readonly Project[] | undefined,
  filters: ProjectFilters,
): Project[] {
  if (items === undefined) {
    return [];
  }
  const target = filters.targetLanguage.trim().toLowerCase();
  return items.filter((item) => {
    if (target !== '' && (item.targetLanguage ?? '').toLowerCase() !== target) {
      return false;
    }
    return matchesDateRange(item.createdAt, filters.createdFrom, filters.createdTo);
  });
}

/** Display name; the backend defaults missing names to "Untitled project" (Task 007). */
export function getProjectDisplayName(project: Pick<Project, 'name'>): string {
  const name = project.name.trim();
  return name === '' ? 'Untitled project' : name;
}

export type ProjectAction = 'open' | 'cancel' | 'retry' | 'export' | 'delete' | 'archive' | 'unarchive';

/** Rows the server would reject for delete (409 PROJECT_HAS_ACTIVE_RUN). */
const ACTIVE_RUN_STATUSES: ReadonlySet<string> = new Set(['Processing', 'Cancelling', 'Uploading']);

/** Terminal-failure rows a fresh run can retry via `POST .../processing`. */
const RETRYABLE_STATUSES: ReadonlySet<string> = new Set(['Failed', 'MediaRejected', 'Cancelled']);

/**
 * Valid-actions-only allowlist (R3). Render exactly the returned actions —
 * never shown-disabled. Mirrors the server guards (archived blocks
 * processing, active runs block delete, per-endpoint permission policies);
 * the server stays authoritative and every rejection surfaces as toast +
 * row refresh.
 */
export function getAllowedActions(
  project: Pick<Project, 'status' | 'isArchived'>,
  permissions: readonly string[],
): ProjectAction[] {
  const can = (permission: string): boolean => permissions.includes(permission);
  const actions: ProjectAction[] = [];
  const status = project.status ?? '';
  const archived = project.isArchived === true;
  if (can('project.view')) {
    actions.push('open');
  }
  if (!archived && status === 'Processing' && can('processing.cancel')) {
    actions.push('cancel');
  }
  if (!archived && RETRYABLE_STATUSES.has(status) && can('processing.retry')) {
    actions.push('retry');
  }
  if (status === 'Completed' && can('export.create')) {
    actions.push('export');
  }
  if (!ACTIVE_RUN_STATUSES.has(status) && can('project.delete')) {
    actions.push('delete');
  }
  if (can('project.edit')) {
    actions.push(archived ? 'unarchive' : 'archive');
  }
  return actions;
}

export { ARCHIVED_VALUES, SORT_VALUES };

/**
 * Single list fetch. The cast covers only the bundle-undocumented extras
 * (status/ownerId/archived) that `ProjectsController.List` accepts; the
 * documented envelope stays fully typed.
 */
export async function fetchProjects(query: ServerProjectQuery): Promise<ProjectListResponse> {
  const wire = query as unknown as NonNullable<ListProjectsParams['query']>;
  return apiClient.listProjects({ path: {}, query: wire });
}
