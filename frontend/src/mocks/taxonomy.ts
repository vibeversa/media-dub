// Task 039A: MSW taxonomy contract (shared with 046 harness, owned here until
// 046 lands). Eleven entries cover every documented API outcome: success plus
// the ten failure shapes feature suites must handle (401/403/404/409/429/500,
// validation, provider-error, partial, stale-conflict).
//
// Wire shapes mirror the frozen backend envelope
// `{ error: { code, message, correlationId, details } }` (Task 013) and the
// generated `ApiError` (Task 014). All fixtures are synthetic: correlation IDs
// are fixed `corr-taxonomy-*` strings, messages are plain text, and no tenant,
// user, token, URL, media, or transcript content appears here.

/** Fixed mock origin. Matches the `httpClient` hermetic fallback so tests never depend on env. */
export const MOCK_BASE_URL = 'http://localhost:5000';

/** Every taxonomy outcome. Adding an outcome here requires a handler in `handlers.ts` and coverage in `conformance.spec.ts`. */
export const TAXONOMY_IDS = [
  'success',
  'unauthorized-401',
  'forbidden-403',
  'not-found-404',
  'conflict-409',
  'rate-limited-429',
  'internal-500',
  'validation',
  'provider-error',
  'partial',
  'stale-conflict',
] as const;

export type TaxonomyId = (typeof TAXONOMY_IDS)[number];

export interface ErrorEnvelopeBody {
  readonly error: {
    readonly code: string;
    readonly message: string;
    readonly correlationId: string;
    readonly details: Record<string, unknown>;
  };
}

export interface TaxonomyEntry {
  readonly id: TaxonomyId;
  /** Path under `MOCK_BASE_URL` served by `handlers.ts`. */
  readonly path: string;
  readonly status: number;
  /** Expected `error.code`; `null` for success envelopes (no `error` key). */
  readonly code: string | null;
  readonly body: Record<string, unknown>;
}

function errorBody(code: string, id: TaxonomyId, message: string, details: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    error: {
      code,
      message,
      correlationId: `corr-taxonomy-${id}`,
      details,
    },
  };
}

/** The eleven documented outcomes. Status/code pairs follow the Task 013 mapping table. */
export const TAXONOMY: readonly TaxonomyEntry[] = [
  {
    id: 'success',
    path: '/__mocks__/taxonomy/success',
    status: 200,
    code: null,
    body: { data: { ok: true }, correlationId: 'corr-taxonomy-success' },
  },
  {
    id: 'unauthorized-401',
    path: '/__mocks__/taxonomy/unauthorized-401',
    status: 401,
    code: 'TOKEN_EXPIRED',
    body: errorBody('TOKEN_EXPIRED', 'unauthorized-401', 'Your session expired. Sign in again.'),
  },
  {
    id: 'forbidden-403',
    path: '/__mocks__/taxonomy/forbidden-403',
    status: 403,
    code: 'FORBIDDEN',
    body: errorBody('FORBIDDEN', 'forbidden-403', 'You do not have permission for this action.'),
  },
  {
    id: 'not-found-404',
    path: '/__mocks__/taxonomy/not-found-404',
    status: 404,
    code: 'PROJECT_NOT_FOUND',
    body: errorBody('PROJECT_NOT_FOUND', 'not-found-404', 'This project may have been deleted or you may lack access.'),
  },
  {
    id: 'conflict-409',
    path: '/__mocks__/taxonomy/conflict-409',
    status: 409,
    code: 'CONFLICT',
    body: errorBody('CONFLICT', 'conflict-409', 'This changed while you worked. Refresh and try again.'),
  },
  {
    id: 'rate-limited-429',
    path: '/__mocks__/taxonomy/rate-limited-429',
    status: 429,
    code: 'RATE_LIMITED',
    body: errorBody('RATE_LIMITED', 'rate-limited-429', 'Too many requests. Wait a moment, then retry.'),
  },
  {
    id: 'internal-500',
    path: '/__mocks__/taxonomy/internal-500',
    status: 500,
    code: 'INTERNAL_ERROR',
    body: errorBody('INTERNAL_ERROR', 'internal-500', 'An unexpected error occurred.'),
  },
  {
    id: 'validation',
    path: '/__mocks__/taxonomy/validation',
    status: 400,
    code: 'VALIDATION_FAILED',
    body: errorBody('VALIDATION_FAILED', 'validation', 'Check the highlighted fields and try again.', {
      title: ['Title is required.'],
    }),
  },
  {
    id: 'provider-error',
    path: '/__mocks__/taxonomy/provider-error',
    status: 502,
    code: 'PROVIDER_FAILED',
    body: errorBody('PROVIDER_FAILED', 'provider-error', 'The provider failed this request.'),
  },
  {
    id: 'partial',
    path: '/__mocks__/taxonomy/partial',
    status: 200,
    code: null,
    body: {
      data: { ok: true },
      partial: true,
      warnings: [{ code: 'SEGMENT_RETRY_ACTIVE', message: 'One segment is still retrying.' }],
      correlationId: 'corr-taxonomy-partial',
    },
  },
  {
    id: 'stale-conflict',
    path: '/__mocks__/taxonomy/stale-conflict',
    status: 409,
    code: 'SELECTION_CONFLICT',
    body: errorBody('SELECTION_CONFLICT', 'stale-conflict', 'Someone else changed this segment. Refresh and reapply your edit.', {
      currentSelectionVersion: 4,
      currentVersionIds: ['ver_2'],
    }),
  },
] as const;

const TAXONOMY_BY_ID: Readonly<Record<TaxonomyId, TaxonomyEntry>> = Object.fromEntries(
  TAXONOMY.map((entry) => [entry.id, entry]),
) as Record<TaxonomyId, TaxonomyEntry>;

export function taxonomyEntry(id: TaxonomyId): TaxonomyEntry {
  return TAXONOMY_BY_ID[id];
}

/** Guards the frozen error envelope: `{ error: { code, message, correlationId, details } }`. */
export function isErrorEnvelope(value: unknown): value is ErrorEnvelopeBody {
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  const error = (value as { error?: unknown }).error;
  if (typeof error !== 'object' || error === null) {
    return false;
  }
  const record = error as Record<string, unknown>;
  return (
    typeof record['code'] === 'string' &&
    typeof record['message'] === 'string' &&
    typeof record['correlationId'] === 'string' &&
    typeof record['details'] === 'object' &&
    record['details'] !== null
  );
}
