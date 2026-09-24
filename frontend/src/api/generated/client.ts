/* auto-generated — do not edit; run make generate-api */

import type * as S from './schemas.js';

/** Typed error for non-2xx API responses (uniform envelope). */
export class ApiError extends Error {
  public readonly status: number;
  public readonly code: string;
  public readonly correlationId: string;
  public readonly details: Record<string, unknown>;
  public constructor(status: number, code: string, message: string, correlationId: string, details: Record<string, unknown>) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.code = code;
    this.correlationId = correlationId;
    this.details = details;
  }
}

/** Per-request overrides (headers the bundle documents per operation). */
export interface RequestOptions {
  /** Idempotency-Key header for safe retries. */
  readonly idempotencyKey?: string;
  /** If-Match header for optimistic concurrency (PATCH projects). */
  readonly ifMatch?: string;
  /** Last-Event-ID header for SSE resume. */
  readonly lastEventId?: string;
  /** Abort a request or close a stream. */
  readonly signal?: AbortSignal;
}

export interface ApiClientOptions {
  /** Origin, e.g. https://api.example.com (server prefix /api/v1 is appended). */
  readonly baseUrl: string;
  /** Resolves the current bearer token (never stored or logged by the client). */
  readonly getToken?: () => string | undefined;
}

/** One parsed SSE frame (frozen Task 013 envelope). */
export interface SseFrame {
  readonly id: string;
  readonly event: string;
  readonly data: S.SseEnvelope;
}

/** Typed fetch client generated from openapi v1. */
export class ApiClient {
  private readonly baseUrl: string;
  private readonly getToken?: () => string | undefined;

  public constructor(options: ApiClientOptions) {
    this.baseUrl = options.baseUrl.replace(/\/$/, '');
    this.getToken = options.getToken;
  }

  private buildUrl(path: string, query?: Record<string, string | number | boolean | undefined>): string {
    let url = this.baseUrl + "/api/v1" + path;
    if (query) {
      const params = new URLSearchParams();
      for (const key of Object.keys(query).sort()) {
        const value = query[key];
        if (value !== undefined) {
          params.append(key, String(value));
        }
      }
      const text = params.toString();
      if (text !== '') {
        url += '?' + text;
      }
    }
    return url;
  }

  private buildHeaders(options?: RequestOptions, hasBody?: boolean): Record<string, string> {
    const headers: Record<string, string> = {};
    if (hasBody) {
      headers['Content-Type'] = 'application/json';
    }
    const token = this.getToken ? this.getToken() : undefined;
    if (token) {
      headers['Authorization'] = 'Bearer ' + token;
    }
    if (options?.idempotencyKey) {
      headers['Idempotency-Key'] = options.idempotencyKey;
    }
    if (options?.ifMatch) {
      headers['If-Match'] = options.ifMatch;
    }
    if (options?.lastEventId) {
      headers['Last-Event-ID'] = options.lastEventId;
    }
    return headers;
  }

  private async throwForStatus(response: Response): Promise<never> {
    let code = 'INTERNAL_ERROR';
    let message = 'An unexpected error occurred.';
    let correlationId = '';
    let details: Record<string, unknown> = {};
    try {
      const body = (await response.json()) as { error?: { code?: string; message?: string; correlationId?: string; details?: Record<string, unknown> } };
      if (body && typeof body === 'object' && body.error) {
        if (typeof body.error.code === 'string') { code = body.error.code; }
        if (typeof body.error.message === 'string') { message = body.error.message; }
        if (typeof body.error.correlationId === 'string') { correlationId = body.error.correlationId; }
        if (body.error.details && typeof body.error.details === 'object') { details = body.error.details; }
      }
    } catch {
      // Non-JSON failure: keep the generic envelope.
    }
    throw new ApiError(response.status, code, message, correlationId, details);
  }

  /** Low-level JSON request used by the generated methods. */
  public async request<T>(method: string, path: string, args?: { query?: Record<string, string | number | boolean | undefined>; body?: unknown; options?: RequestOptions }): Promise<T> {
    const response = await fetch(this.buildUrl(path, args?.query), {
      method,
      headers: this.buildHeaders(args?.options, args?.body !== undefined),
      body: args?.body !== undefined ? JSON.stringify(args.body) : undefined,
      signal: args?.options?.signal,
    });
    if (!response.ok) {
      await this.throwForStatus(response);
    }
    if (response.status === 204) {
      return undefined as T;
    }
    return (await response.json()) as T;
  }

  /** Low-level raw request (SSE streams, 302 downloads). Never throws for 302. */
  public async requestRaw(method: string, path: string, args?: { query?: Record<string, string | number | boolean | undefined>; options?: RequestOptions }): Promise<Response> {
    const response = await fetch(this.buildUrl(path, args?.query), {
      method,
      headers: this.buildHeaders(args?.options, false),
      signal: args?.options?.signal,
      redirect: 'manual',
    });
    if (!response.ok && response.status !== 302 && response.type !== 'opaqueredirect') {
      await this.throwForStatus(response);
    }
    return response;
  }

  /** Open an SSE stream; resolves frames via onFrame (hint only, refetch HTTP APIs). */
  public async openStream(path: string, args?: { query?: Record<string, string | number | boolean | undefined>; options?: RequestOptions; onFrame?: (frame: SseFrame) => void }): Promise<void> {
    const headers = this.buildHeaders(args?.options, false);
    headers['Accept'] = 'text/event-stream';
    const response = await fetch(this.buildUrl(path, args?.query), { headers, signal: args?.options?.signal });
    if (!response.ok || !response.body) {
      await this.throwForStatus(response as Response);
    }
    const reader = (response.body as ReadableStream<Uint8Array>).getReader();
    const decoder = new TextDecoder();
    let buffer = '';
    for (;;) {
      const { done, value } = await reader.read();
      if (done) {
        break;
      }
      buffer += decoder.decode(value, { stream: true });
      let boundary = buffer.indexOf('\n\n');
      while (boundary >= 0) {
        const chunk = buffer.slice(0, boundary);
        buffer = buffer.slice(boundary + 2);
        const frame = ApiClient.parseFrame(chunk);
        if (frame && args?.onFrame) {
          args.onFrame(frame);
        }
        boundary = buffer.indexOf('\n\n');
      }
    }
  }

  /** Parse one SSE id/event/data chunk into a typed frame. */
  public static parseFrame(chunk: string): SseFrame | null {
    let id = '';
    let event = '';
    let data = '';
    for (const line of chunk.split('\n')) {
      if (line.startsWith('id:')) { id = line.slice(3).trim(); }
      else if (line.startsWith('event:')) { event = line.slice(6).trim(); }
      else if (line.startsWith('data:')) { data += line.slice(5).trim(); }
    }
    if (data === '') {
      return null;
    }
    return { id, event, data: JSON.parse(data) as S.SseEnvelope };
  }

  /** getDiagnosticsDlq — DLQ summary (empty is a 200 zero-shape) */
  public async getDiagnosticsDlq(params?: GetDiagnosticsDlqParams, options?: RequestOptions): Promise<S.DlqSummary> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/diagnostics/dlq".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.DlqSummary>('GET', filled, { query, body: undefined, options });
  }

  /** getDiagnosticsLeases — Stale leases (page-based, default 50, max 200) */
  public async getDiagnosticsLeases(params?: GetDiagnosticsLeasesParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/diagnostics/leases".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** getDiagnosticsOrphans — Orphan artifacts (cursor pagination) */
  public async getDiagnosticsOrphans(params?: GetDiagnosticsOrphansParams, options?: RequestOptions): Promise<S.OrphanPage> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/diagnostics/orphans".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.OrphanPage>('GET', filled, { query, body: undefined, options });
  }

  /** getDiagnosticsQueues — Queue depths (frozen taxonomy) */
  public async getDiagnosticsQueues(params?: GetDiagnosticsQueuesParams, options?: RequestOptions): Promise<(S.QueueDepth)[]> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/diagnostics/queues".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<(S.QueueDepth)[]>('GET', filled, { query, body: undefined, options });
  }

  /** getDiagnosticsReviewBacklog — Review backlog aggregation */
  public async getDiagnosticsReviewBacklog(params?: GetDiagnosticsReviewBacklogParams, options?: RequestOptions): Promise<S.ReviewBacklog> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/diagnostics/review-backlog".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewBacklog>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminDlqSummary — DLQ summary (DB-free operator guidance) */
  public async getAdminDlqSummary(params?: GetAdminDlqSummaryParams, options?: RequestOptions): Promise<S.GetAdminDlqSummaryResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/dlq/summary".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.GetAdminDlqSummaryResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminLeases — Lease status (paginated, max 100) */
  public async getAdminLeases(params?: GetAdminLeasesParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/leases/status".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminProviderExecution — Provider execution detail (ids/hashes only) */
  public async getAdminProviderExecution(params: GetAdminProviderExecutionParams, options?: RequestOptions): Promise<S.GetAdminProviderExecutionResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/provider-executions/{id}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.GetAdminProviderExecutionResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminProviderHealth — Provider health snapshots (secret-free) */
  public async getAdminProviderHealth(params?: GetAdminProviderHealthParams, options?: RequestOptions): Promise<(S.ProviderHealth)[]> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/provider-health".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<(S.ProviderHealth)[]>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminProviderRoutes — Provider routes (names only) */
  public async getAdminProviderRoutes(params?: GetAdminProviderRoutesParams, options?: RequestOptions): Promise<(S.ProviderRoute)[]> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/provider-routes".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<(S.ProviderRoute)[]>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminQuotas — Tenant quota limits (frozen Quota options) */
  public async getAdminQuotas(params?: GetAdminQuotasParams, options?: RequestOptions): Promise<S.AdminQuotasResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/quotas".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.AdminQuotasResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminReviewBacklogLegacy — Review backlog rows (paginated, max 100) */
  public async getAdminReviewBacklogLegacy(params?: GetAdminReviewBacklogLegacyParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/reviews/backlog".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminStage — Stage execution detail (no lease token) */
  public async getAdminStage(params: GetAdminStageParams, options?: RequestOptions): Promise<S.GetAdminStageResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/stages/{execId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.GetAdminStageResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminStatus — Operator status probe */
  public async getAdminStatus(params?: GetAdminStatusParams, options?: RequestOptions): Promise<S.GetAdminStatusResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/status".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.GetAdminStatusResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getAdminUsage — Tenant usage aggregate */
  public async getAdminUsage(params?: GetAdminUsageParams, options?: RequestOptions): Promise<S.AdminUsageResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/admin/usage".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.AdminUsageResponse>('GET', filled, { query, body: undefined, options });
  }

  /** authLogin — Log in with tenant credentials */
  public async authLogin(params: AuthLoginParams, body: S.AuthLoginRequest, options?: RequestOptions): Promise<S.AuthLoginResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/auth/login".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.AuthLoginResponse>('POST', filled, { query, body, options });
  }

  /** authLogout — Revoke the current refresh session */
  public async authLogout(params?: AuthLogoutParams, body?: S.AuthRefreshRequest, options?: RequestOptions): Promise<S.AuthLogoutResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/auth/logout".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.AuthLogoutResponse>('POST', filled, { query, body, options });
  }

  /** authRefresh — Rotate a refresh session */
  public async authRefresh(params: AuthRefreshParams, body: S.AuthRefreshRequest, options?: RequestOptions): Promise<S.AuthLoginResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/auth/refresh".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.AuthLoginResponse>('POST', filled, { query, body, options });
  }

  /** getDashboardSummary — Seven-section tenant summary */
  public async getDashboardSummary(params?: GetDashboardSummaryParams, options?: RequestOptions): Promise<S.DashboardSummary> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/dashboard/summary".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.DashboardSummary>('GET', filled, { query, body: undefined, options });
  }

  /** getMe — Current identity with permissions */
  public async getMe(params?: GetMeParams, options?: RequestOptions): Promise<S.MeResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/me".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.MeResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getPreferences — Read identity preferences */
  public async getPreferences(params?: GetPreferencesParams, options?: RequestOptions): Promise<S.PreferencesResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/me/preferences".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PreferencesResponse>('GET', filled, { query, body: undefined, options });
  }

  /** updatePreferences — Write identity preferences */
  public async updatePreferences(params: UpdatePreferencesParams, body: S.PreferencesResponse, options?: RequestOptions): Promise<S.PreferencesResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/me/preferences".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PreferencesResponse>('PUT', filled, { query, body, options });
  }

  /** listNotifications — Notification inbox (page-based, newest first) */
  public async listNotifications(params?: ListNotificationsParams, options?: RequestOptions): Promise<S.NotificationListResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/notifications".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.NotificationListResponse>('GET', filled, { query, body: undefined, options });
  }

  /** markAllNotificationsRead — Mark all notifications read (idempotent) */
  public async markAllNotificationsRead(params?: MarkAllNotificationsReadParams, options?: RequestOptions): Promise<S.MarkAllNotificationsReadResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/notifications/read-all".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.MarkAllNotificationsReadResponse>('POST', filled, { query, body: undefined, options });
  }

  /** getUnreadNotificationCount — Unread notification count (same scope as list) */
  public async getUnreadNotificationCount(params?: GetUnreadNotificationCountParams, options?: RequestOptions): Promise<S.UnreadCountResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/notifications/unread-count".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.UnreadCountResponse>('GET', filled, { query, body: undefined, options });
  }

  /** markNotificationRead — Mark one notification read (idempotent) */
  public async markNotificationRead(params: MarkNotificationReadParams, options?: RequestOptions): Promise<S.MarkNotificationReadResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/notifications/{notificationId}/read".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.MarkNotificationReadResponse>('POST', filled, { query, body: undefined, options });
  }

  /** listProjects — List projects (page-based) */
  public async listProjects(params?: ListProjectsParams, options?: RequestOptions): Promise<S.ProjectListResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProjectListResponse>('GET', filled, { query, body: undefined, options });
  }

  /** createProject — Create a project */
  public async createProject(params: CreateProjectParams, body: S.ProjectCreateRequest, options?: RequestOptions): Promise<S.Project> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Project>('POST', filled, { query, body, options });
  }

  /** getProject — Project detail */
  public async getProject(params: GetProjectParams, options?: RequestOptions): Promise<S.Project> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Project>('GET', filled, { query, body: undefined, options });
  }

  /** patchProject — Patch project settings (optimistic concurrency) */
  public async patchProject(params: PatchProjectParams, body: S.ProjectPatchRequest, options?: RequestOptions): Promise<S.Project> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Project>('PATCH', filled, { query, body, options });
  }

  /** deleteProject — Delete a project (guarded) */
  public async deleteProject(params: DeleteProjectParams, options?: RequestOptions): Promise<void> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<void>('DELETE', filled, { query, body: undefined, options });
  }

  /** listWorkspaceActivity — Project activity feed (page-based) */
  public async listWorkspaceActivity(params: ListWorkspaceActivityParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/activity".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** archiveProject — Archive a project */
  public async archiveProject(params: ArchiveProjectParams, options?: RequestOptions): Promise<S.Project> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/archive".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Project>('POST', filled, { query, body: undefined, options });
  }

  /** listExports — List export jobs (page-based) */
  public async listExports(params: ListExportsParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/exports".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** createExport — Create an export job (idempotent, 7-day key scope) */
  public async createExport(params: CreateExportParams, body: S.ExportCreateRequest, options?: RequestOptions): Promise<S.Export> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/exports".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Export>('POST', filled, { query, body, options });
  }

  /** getExport — Export job detail */
  public async getExport(params: GetExportParams, options?: RequestOptions): Promise<S.Export> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/exports/{exportId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Export>('GET', filled, { query, body: undefined, options });
  }

  public async downloadExport(params: DownloadExportParams, options?: RequestOptions): Promise<Response> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/exports/{exportId}/download".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.requestRaw('GET', filled, { query, options });
  }

  /** getOutput — Output readiness aggregate */
  public async getOutput(params: GetOutputParams, options?: RequestOptions): Promise<S.OutputResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/output".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.OutputResponse>('GET', filled, { query, body: undefined, options });
  }

  /** downloadOutput — Output download descriptor (200 JSON) */
  public async downloadOutput(params: DownloadOutputParams, options?: RequestOptions): Promise<S.DownloadOutputResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/output/download".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.DownloadOutputResponse>('GET', filled, { query, body: undefined, options });
  }

  /** listProcessingRuns — List processing runs (page-based) */
  public async listProcessingRuns(params: ListProcessingRunsParams, options?: RequestOptions): Promise<S.ProcessingRunListResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRunListResponse>('GET', filled, { query, body: undefined, options });
  }

  /** startProcessing — Start a processing run (idempotent, 24h key scope) */
  public async startProcessing(params: StartProcessingParams, body?: S.ProcessingStartRequest, options?: RequestOptions): Promise<S.ProcessingRun> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRun>('POST', filled, { query, body, options });
  }

  /** getActiveProcessingRun — Active processing run (legacy) */
  public async getActiveProcessingRun(params: GetActiveProcessingRunParams, options?: RequestOptions): Promise<S.ProcessingRun> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing/active".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRun>('GET', filled, { query, body: undefined, options });
  }

  /** cancelActiveProcessingRun — Cancel the active run (legacy) */
  public async cancelActiveProcessingRun(params: CancelActiveProcessingRunParams, options?: RequestOptions): Promise<S.ProcessingRun> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing/cancel".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRun>('POST', filled, { query, body: undefined, options });
  }

  /** retryActiveProcessingRun — Retry the active run (legacy) */
  public async retryActiveProcessingRun(params: RetryActiveProcessingRunParams, options?: RequestOptions): Promise<S.ProcessingRun> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing/retry".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRun>('POST', filled, { query, body: undefined, options });
  }

  /** getProcessingRun — Processing run detail */
  public async getProcessingRun(params: GetProcessingRunParams, options?: RequestOptions): Promise<S.ProcessingRun> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing/{runId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRun>('GET', filled, { query, body: undefined, options });
  }

  /** cancelProcessingRun — Cancel a run (idempotent, 202) */
  public async cancelProcessingRun(params: CancelProcessingRunParams, options?: RequestOptions): Promise<S.ProcessingRun> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing/{runId}/cancel".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRun>('POST', filled, { query, body: undefined, options });
  }

  /** retryProcessingRun — Retry a run as a new linked run */
  public async retryProcessingRun(params: RetryProcessingRunParams, options?: RequestOptions): Promise<S.ProcessingRun> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/processing/{runId}/retry".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProcessingRun>('POST', filled, { query, body: undefined, options });
  }

  /** getProgress — Run progress snapshot */
  public async getProgress(params: GetProgressParams, options?: RequestOptions): Promise<S.ProgressResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/progress".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ProgressResponse>('GET', filled, { query, body: undefined, options });
  }

  public async streamProgress(params: StreamProgressParams, options?: RequestOptions): Promise<Response> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/progress/stream".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.requestRaw('GET', filled, { query, options });
  }

  /** getWorkspaceQuality — Quality projection (counts and codes only) */
  public async getWorkspaceQuality(params: GetWorkspaceQualityParams, options?: RequestOptions): Promise<S.GetWorkspaceQualityResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/quality".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.GetWorkspaceQualityResponse>('GET', filled, { query, body: undefined, options });
  }

  /** listProjectReviews — List project reviews (page-based) */
  public async listProjectReviews(params: ListProjectReviewsParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/reviews".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** getProjectReview — Project-scoped review detail */
  public async getProjectReview(params: GetProjectReviewParams, options?: RequestOptions): Promise<S.ReviewContextResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/reviews/{reviewId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewContextResponse>('GET', filled, { query, body: undefined, options });
  }

  /** listSegments — List segments (default 50, max 200, sort startMs) */
  public async listSegments(params: ListSegmentsParams, options?: RequestOptions): Promise<S.SegmentListResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/segments".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SegmentListResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getSegment — Segment detail with versions */
  public async getSegment(params: GetSegmentParams, options?: RequestOptions): Promise<S.SegmentDetailResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/segments/{segmentId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SegmentDetailResponse>('GET', filled, { query, body: undefined, options });
  }

  /** retrySegment — Retry segment synthesis */
  public async retrySegment(params: RetrySegmentParams, options?: RequestOptions): Promise<S.SegmentMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/segments/{segmentId}/retry".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SegmentMutationResponse>('POST', filled, { query, body: undefined, options });
  }

  /** editTranscript — Submit a manual transcript edit */
  public async editTranscript(params: EditTranscriptParams, body: S.SegmentEditRequest, options?: RequestOptions): Promise<S.SegmentMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/segments/{segmentId}/transcript-edits".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SegmentMutationResponse>('POST', filled, { query, body, options });
  }

  /** selectTranscriptVersion — Select transcript version (optimistic concurrency) */
  public async selectTranscriptVersion(params: SelectTranscriptVersionParams, body: S.SegmentSelectionRequest, options?: RequestOptions): Promise<S.SegmentMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/segments/{segmentId}/transcript-selection".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SegmentMutationResponse>('POST', filled, { query, body, options });
  }

  /** editTranslation — Submit a manual translation edit */
  public async editTranslation(params: EditTranslationParams, body: S.SegmentEditRequest, options?: RequestOptions): Promise<S.SegmentMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/segments/{segmentId}/translation-edits".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SegmentMutationResponse>('POST', filled, { query, body, options });
  }

  /** selectTranslationVersion — Select translation version (optimistic concurrency) */
  public async selectTranslationVersion(params: SelectTranslationVersionParams, body: S.SegmentSelectionRequest, options?: RequestOptions): Promise<S.SegmentMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/segments/{segmentId}/translation-selection".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SegmentMutationResponse>('POST', filled, { query, body, options });
  }

  /** listSpeakers — List speakers (default 20, max 100) */
  public async listSpeakers(params: ListSpeakersParams, options?: RequestOptions): Promise<S.SpeakerListResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/speakers".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SpeakerListResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getSpeaker — Speaker detail */
  public async getSpeaker(params: GetSpeakerParams, options?: RequestOptions): Promise<S.SpeakerDetailResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/speakers/{speakerId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.SpeakerDetailResponse>('GET', filled, { query, body: undefined, options });
  }

  /** listAvailableVoices — Compatible-only voices with exclusion reasons */
  public async listAvailableVoices(params: ListAvailableVoicesParams, options?: RequestOptions): Promise<S.AvailableVoicesResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/speakers/{speakerId}/available-voices".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.AvailableVoicesResponse>('GET', filled, { query, body: undefined, options });
  }

  /** assignSpeakerVoice — Assign a voice to a speaker */
  public async assignSpeakerVoice(params: AssignSpeakerVoiceParams, body: S.VoiceAssignmentRequest, options?: RequestOptions): Promise<S.VoiceAssignmentResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/speakers/{speakerId}/voice-assignment".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.VoiceAssignmentResponse>('PUT', filled, { query, body, options });
  }

  /** unarchiveProject — Unarchive a project */
  public async unarchiveProject(params: UnarchiveProjectParams, options?: RequestOptions): Promise<S.Project> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/unarchive".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Project>('POST', filled, { query, body: undefined, options });
  }

  /** listUploads — List upload sessions */
  public async listUploads(params: ListUploadsParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/uploads".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** initiateUpload — Initiate a multipart upload */
  public async initiateUpload(params: InitiateUploadParams, body: S.UploadInitiateRequest, options?: RequestOptions): Promise<S.UploadSession> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/uploads".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.UploadSession>('POST', filled, { query, body, options });
  }

  /** getUpload — Upload session detail */
  public async getUpload(params: GetUploadParams, options?: RequestOptions): Promise<S.UploadSession> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/uploads/{uploadId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.UploadSession>('GET', filled, { query, body: undefined, options });
  }

  /** abortUpload — Abort a multipart upload */
  public async abortUpload(params: AbortUploadParams, options?: RequestOptions): Promise<S.UploadSession> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/uploads/{uploadId}/abort".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.UploadSession>('POST', filled, { query, body: undefined, options });
  }

  /** completeUpload — Complete a multipart upload */
  public async completeUpload(params: CompleteUploadParams, options?: RequestOptions): Promise<S.UploadSession> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/uploads/{uploadId}/complete".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.UploadSession>('POST', filled, { query, body: undefined, options });
  }

  /** authorizeUploadPart — Authorize one upload part */
  public async authorizeUploadPart(params: AuthorizeUploadPartParams, body: S.AuthorizeUploadPartRequest, options?: RequestOptions): Promise<S.UploadPartAuth> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/uploads/{uploadId}/parts".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.UploadPartAuth>('POST', filled, { query, body, options });
  }

  /** listVoicePreviews — List voice previews (default 20, max 100) */
  public async listVoicePreviews(params: ListVoicePreviewsParams, options?: RequestOptions): Promise<S.PaginatedResult> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/voice-previews".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.PaginatedResult>('GET', filled, { query, body: undefined, options });
  }

  /** requestVoicePreview — Request a voice preview (202 first, 200 duplicate) */
  public async requestVoicePreview(params: RequestVoicePreviewParams, body: S.VoicePreviewRequest, options?: RequestOptions): Promise<S.VoicePreviewResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/voice-previews".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.VoicePreviewResponse>('POST', filled, { query, body, options });
  }

  /** getVoicePreview — Voice preview detail (15-min presigned URL) */
  public async getVoicePreview(params: GetVoicePreviewParams, options?: RequestOptions): Promise<S.VoicePreviewDetailResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/voice-previews/{previewId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.VoicePreviewDetailResponse>('GET', filled, { query, body: undefined, options });
  }

  /** getWorkspace — Workspace aggregate (single call, ten sections) */
  public async getWorkspace(params: GetWorkspaceParams, options?: RequestOptions): Promise<S.Workspace> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/projects/{projectId}/workspace".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.Workspace>('GET', filled, { query, body: undefined, options });
  }

  /** getReview — Review detail */
  public async getReview(params: GetReviewParams, options?: RequestOptions): Promise<S.ReviewContextResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewContextResponse>('GET', filled, { query, body: undefined, options });
  }

  /** approveReview — Approve a review (legacy) */
  public async approveReview(params: ApproveReviewParams, options?: RequestOptions): Promise<S.ReviewMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/approve".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewMutationResponse>('POST', filled, { query, body: undefined, options });
  }

  /** getReviewContext — Frozen review context (eleven sections) */
  public async getReviewContext(params: GetReviewContextParams, options?: RequestOptions): Promise<S.ReviewContextResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/context".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewContextResponse>('GET', filled, { query, body: undefined, options });
  }

  /** dismissReview — Dismiss a review (hardened, idempotent) */
  public async dismissReview(params: DismissReviewParams, body: S.ReviewMutationRequest, options?: RequestOptions): Promise<S.ReviewMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/dismiss".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewMutationResponse>('POST', filled, { query, body, options });
  }

  /** rejectReview — Reject a review (legacy) */
  public async rejectReview(params: RejectReviewParams, options?: RequestOptions): Promise<S.ReviewMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/reject".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewMutationResponse>('POST', filled, { query, body: undefined, options });
  }

  /** reopenReview — Reopen a resolved review (hardened, idempotent) */
  public async reopenReview(params: ReopenReviewParams, body: S.ReviewMutationRequest, options?: RequestOptions): Promise<S.ReviewMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/reopen".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewMutationResponse>('POST', filled, { query, body, options });
  }

  /** requeueReview — Requeue a review (legacy) */
  public async requeueReview(params: RequeueReviewParams, options?: RequestOptions): Promise<S.ReviewMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/requeue".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewMutationResponse>('POST', filled, { query, body: undefined, options });
  }

  /** resolveReview — Resolve a review (hardened, idempotent) */
  public async resolveReview(params: ResolveReviewParams, body: S.ReviewMutationRequest, options?: RequestOptions): Promise<S.ReviewMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/resolve".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewMutationResponse>('POST', filled, { query, body, options });
  }

  /** resolveReviewWithEdit — Resolve with a manual edit (hardened, idempotent) */
  public async resolveReviewWithEdit(params: ResolveReviewWithEditParams, body: S.ReviewMutationRequest, options?: RequestOptions): Promise<S.ReviewMutationResponse> {
    const pathParams = (((params ?? {}) as { path?: Record<string, unknown> }).path ?? {});
    const query = ((params ?? {}) as { query?: Record<string, string | number | boolean | undefined> }).query;
    const filled = "/reviews/{reviewId}/resolve-with-edit".replace(/\{([^}]+)\}/g, (_m, key) => encodeURIComponent(String(pathParams[key as string])));
    return this.request<S.ReviewMutationResponse>('POST', filled, { query, body, options });
  }

}

export interface GetDiagnosticsDlqParams {
  readonly path: {
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface GetDiagnosticsLeasesParams {
  readonly path: {
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface GetDiagnosticsOrphansParams {
  readonly path: {
  };
  readonly query?: {
    readonly "pageSize"?: number;
    readonly "cursor"?: string;
  };
}

export interface GetDiagnosticsQueuesParams {
  readonly path: {
  };
}

export interface GetDiagnosticsReviewBacklogParams {
  readonly path: {
  };
}

export interface GetAdminDlqSummaryParams {
  readonly path: {
  };
}

export interface GetAdminLeasesParams {
  readonly path: {
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface GetAdminProviderExecutionParams {
  readonly path: {
    readonly "id": string;
  };
}

export interface GetAdminProviderHealthParams {
  readonly path: {
  };
}

export interface GetAdminProviderRoutesParams {
  readonly path: {
  };
}

export interface GetAdminQuotasParams {
  readonly path: {
  };
}

export interface GetAdminReviewBacklogLegacyParams {
  readonly path: {
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface GetAdminStageParams {
  readonly path: {
    readonly "execId": string;
  };
}

export interface GetAdminStatusParams {
  readonly path: {
  };
}

export interface GetAdminUsageParams {
  readonly path: {
  };
}

export interface AuthLoginParams {
  readonly path: {
  };
}

export interface AuthLogoutParams {
  readonly path: {
  };
}

export interface AuthRefreshParams {
  readonly path: {
  };
}

export interface GetDashboardSummaryParams {
  readonly path: {
  };
}

export interface GetMeParams {
  readonly path: {
  };
}

export interface GetPreferencesParams {
  readonly path: {
  };
}

export interface UpdatePreferencesParams {
  readonly path: {
  };
}

export interface ListNotificationsParams {
  readonly path: {
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
    readonly "unreadOnly"?: boolean;
  };
}

export interface MarkAllNotificationsReadParams {
  readonly path: {
  };
}

export interface GetUnreadNotificationCountParams {
  readonly path: {
  };
}

export interface MarkNotificationReadParams {
  readonly path: {
    readonly "notificationId": string;
  };
}

export interface ListProjectsParams {
  readonly path: {
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
    readonly "sort"?: "createdAt" | "updatedAt" | "name";
    readonly "sortDir"?: "asc" | "desc";
  };
}

export interface CreateProjectParams {
  readonly path: {
  };
}

export interface GetProjectParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface PatchProjectParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface DeleteProjectParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface ListWorkspaceActivityParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface ArchiveProjectParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface ListExportsParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface CreateExportParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface GetExportParams {
  readonly path: {
    readonly "projectId": string;
    readonly "exportId": string;
  };
}

export interface DownloadExportParams {
  readonly path: {
    readonly "projectId": string;
    readonly "exportId": string;
  };
}

export interface GetOutputParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface DownloadOutputParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface ListProcessingRunsParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface StartProcessingParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "force"?: boolean;
  };
}

export interface GetActiveProcessingRunParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface CancelActiveProcessingRunParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface RetryActiveProcessingRunParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface GetProcessingRunParams {
  readonly path: {
    readonly "projectId": string;
    readonly "runId": string;
  };
}

export interface CancelProcessingRunParams {
  readonly path: {
    readonly "projectId": string;
    readonly "runId": string;
  };
}

export interface RetryProcessingRunParams {
  readonly path: {
    readonly "projectId": string;
    readonly "runId": string;
  };
}

export interface GetProgressParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface StreamProgressParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "access_token"?: string;
  };
}

export interface GetWorkspaceQualityParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface ListProjectReviewsParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface GetProjectReviewParams {
  readonly path: {
    readonly "projectId": string;
    readonly "reviewId": string;
  };
}

export interface ListSegmentsParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "speakerId"?: string;
    readonly "reviewStatus"?: S.ReviewStatus;
    readonly "qualityFlag"?: string;
    readonly "syncIssue"?: string;
    readonly "text"?: string;
    readonly "startMs"?: number;
    readonly "endMs"?: number;
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface GetSegmentParams {
  readonly path: {
    readonly "projectId": string;
    readonly "segmentId": string;
  };
}

export interface RetrySegmentParams {
  readonly path: {
    readonly "projectId": string;
    readonly "segmentId": string;
  };
}

export interface EditTranscriptParams {
  readonly path: {
    readonly "projectId": string;
    readonly "segmentId": string;
  };
}

export interface SelectTranscriptVersionParams {
  readonly path: {
    readonly "projectId": string;
    readonly "segmentId": string;
  };
}

export interface EditTranslationParams {
  readonly path: {
    readonly "projectId": string;
    readonly "segmentId": string;
  };
}

export interface SelectTranslationVersionParams {
  readonly path: {
    readonly "projectId": string;
    readonly "segmentId": string;
  };
}

export interface ListSpeakersParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface GetSpeakerParams {
  readonly path: {
    readonly "projectId": string;
    readonly "speakerId": string;
  };
}

export interface ListAvailableVoicesParams {
  readonly path: {
    readonly "projectId": string;
    readonly "speakerId": string;
  };
}

export interface AssignSpeakerVoiceParams {
  readonly path: {
    readonly "projectId": string;
    readonly "speakerId": string;
  };
}

export interface UnarchiveProjectParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface ListUploadsParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface InitiateUploadParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface GetUploadParams {
  readonly path: {
    readonly "projectId": string;
    readonly "uploadId": string;
  };
}

export interface AbortUploadParams {
  readonly path: {
    readonly "projectId": string;
    readonly "uploadId": string;
  };
}

export interface CompleteUploadParams {
  readonly path: {
    readonly "projectId": string;
    readonly "uploadId": string;
  };
}

export interface AuthorizeUploadPartParams {
  readonly path: {
    readonly "projectId": string;
    readonly "uploadId": string;
  };
}

export interface ListVoicePreviewsParams {
  readonly path: {
    readonly "projectId": string;
  };
  readonly query?: {
    readonly "page"?: number;
    readonly "pageSize"?: number;
  };
}

export interface RequestVoicePreviewParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface GetVoicePreviewParams {
  readonly path: {
    readonly "projectId": string;
    readonly "previewId": string;
  };
}

export interface GetWorkspaceParams {
  readonly path: {
    readonly "projectId": string;
  };
}

export interface GetReviewParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface ApproveReviewParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface GetReviewContextParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface DismissReviewParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface RejectReviewParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface ReopenReviewParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface RequeueReviewParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface ResolveReviewParams {
  readonly path: {
    readonly "reviewId": string;
  };
}

export interface ResolveReviewWithEditParams {
  readonly path: {
    readonly "reviewId": string;
  };
}
