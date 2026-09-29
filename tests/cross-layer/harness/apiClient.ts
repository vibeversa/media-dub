// Task 040A: typed client for the cross-layer rig.
//
// R5 constrains what this client asserts: it proves *seams* (auth -> processing
// -> SSE -> workspace) work across layers, not per-route status codes. Route
// contract coverage stays in tests/DubbingPlatform.IntegrationTests (006-013).
// The helpers therefore fail with a message that names the seam, and they do
// not re-implement a route matrix.

import { API_BASE_URL } from './config.js';

export interface ApiFailure {
  readonly status: number;
  readonly code: string;
  readonly message: string;
  readonly correlationId?: string;
}

export class ApiError extends Error {
  readonly failure: ApiFailure;

  constructor(failure: ApiFailure) {
    super(`API ${failure.status} ${failure.code}: ${failure.message}`);
    this.name = 'ApiError';
    this.failure = failure;
  }
}

export interface TokenPair {
  readonly accessToken: string;
  readonly refreshToken: string;
  readonly expiresIn: number;
}

export interface ProjectSummary {
  readonly id: string;
  readonly status: string;
  readonly name: string | null;
  readonly isArchived: boolean;
}

export interface ProcessingRun {
  readonly runId: string;
  readonly projectId: string;
  readonly status: string;
}

/**
 * Shape returned by `GET /api/v1/projects/{id}/workspace`.
 *
 * Modelled from the live response, not from a guess: the payload has no
 * `segments` collection. Review work is summarised as
 * `review.pendingCount` and per-segment detail is fetched elsewhere, so a helper
 * that reaches for `workspace.segments` would read `undefined` and pass a
 * vacuous assertion.
 */
export interface WorkspaceSnapshot {
  readonly project: {
    readonly id: string;
    readonly status: string;
    readonly name: string | null;
    readonly isArchived: boolean;
  };
  readonly run: { readonly id: string; readonly status: string } | null;
  readonly phase: string;
  readonly stage: string;
  readonly review: { readonly pendingCount: number; readonly oldestWaitingAt: string | null };
  readonly progress: Readonly<Record<string, unknown>>;
  readonly warnings: ReadonlyArray<unknown>;
  readonly activity: ReadonlyArray<unknown>;
  readonly permissions: Readonly<Record<string, unknown>>;
}

interface RequestOptions {
  readonly method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  readonly path: string;
  readonly token?: string;
  readonly body?: unknown;
  readonly headers?: Record<string, string>;
}

async function readFailure(response: Response): Promise<ApiFailure> {
  const text = await response.text();
  try {
    const parsed = JSON.parse(text) as {
      error?: { code?: string; message?: string; correlationId?: string };
      title?: string;
    };
    return {
      status: response.status,
      code: parsed.error?.code ?? parsed.title ?? 'UNKNOWN',
      message: parsed.error?.message ?? text.slice(0, 300),
      correlationId: parsed.error?.correlationId,
    };
  } catch {
    return { status: response.status, code: 'NON_JSON_RESPONSE', message: text.slice(0, 300) };
  }
}

export class ApiClient {
  constructor(private readonly baseUrl: string = API_BASE_URL) {}

  private async request<T>(options: RequestOptions): Promise<T> {
    const response = await fetch(`${this.baseUrl}${options.path}`, {
      method: options.method,
      headers: {
        ...(options.body === undefined ? {} : { 'Content-Type': 'application/json' }),
        ...(options.token === undefined ? {} : { Authorization: `Bearer ${options.token}` }),
        ...options.headers,
      },
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
    });

    if (!response.ok) {
      throw new ApiError(await readFailure(response));
    }

    if (response.status === 204) {
      return undefined as T;
    }

    return (await response.json()) as T;
  }

  /**
   * The auth seam. `POST /api/v1/auth/login` is the only way the rig obtains a
   * token - it never fabricates one - so this call is part of what is under test
   * rather than a setup shortcut.
   */
  async login(tenantId: string, externalSubject: string): Promise<TokenPair> {
    return this.request<TokenPair>({
      method: 'POST',
      path: '/api/v1/auth/login',
      body: { tenantId, externalSubject },
    });
  }

  /** Cross-layer identity read: proves the minted token resolves a tenant. */
  async readIdentity(token: string): Promise<{ user: { id: string }; tenant: { id: string } }> {
    return this.request({
      method: 'GET',
      path: '/api/v1/me',
      token,
    });
  }

  async listProjects(
    token: string,
  ): Promise<{ items: ProjectSummary[]; total: number }> {
    return this.request({ method: 'GET', path: '/api/v1/projects?page=1&pageSize=50', token });
  }

  /**
   * Starts a processing run. `Idempotency-Key` is mandatory - the API rejects a
   * start without one, which is correct behaviour and part of the seam.
   */
  async startProcessing(token: string, projectId: string, idempotencyKey: string): Promise<ProcessingRun> {
    return this.request<ProcessingRun>({
      method: 'POST',
      path: `/api/v1/projects/${projectId}/processing`,
      token,
      body: {},
      headers: { 'Idempotency-Key': idempotencyKey },
    });
  }

  async readWorkspace(token: string, projectId: string): Promise<WorkspaceSnapshot> {
    return this.request<WorkspaceSnapshot>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/workspace`,
      token,
    });
  }

  async readRun(token: string, projectId: string, runId: string): Promise<ProcessingRun> {
    return this.request<ProcessingRun>({
      method: 'GET',
      path: `/api/v1/projects/${projectId}/processing/${runId}`,
      token,
    });
  }

  /** Liveness probe used by the preflight, not a seam assertion. */
  async isAlive(): Promise<boolean> {
    try {
      const response = await fetch(`${this.baseUrl}/health/live`, {
        signal: AbortSignal.timeout(5000),
      });
      return response.ok;
    } catch {
      return false;
    }
  }
}
