import { describe, expect, it } from 'vitest';
import { isAppError } from '../errors/index.js';
import type { AppError } from '../errors/index.js';
import type { ErrorKind } from '../errors/index.js';
import { NETWORK_ERROR, NOT_AUTHENTICATED, errorKindByCode, normalizeError, recoveryHintByCode } from '../errors/index.js';
import type { ErrorCode } from '../client/index.js';

/** All 65 public codes from `ErrorCodes.cs` / the bundle `ErrorCode` enum. */
const ALL_CODES: readonly ErrorCode[] = [
  'VALIDATION_FAILED', 'UNAUTHORIZED', 'FORBIDDEN', 'NOT_FOUND', 'CONFLICT',
  'MEDIA_UNSUPPORTED', 'MEDIA_CORRUPT', 'UPLOAD_INCOMPLETE', 'DUPLICATE_MEDIA',
  'PROVIDER_CONFIGURATION_ERROR', 'PROVIDER_RATE_LIMITED', 'PROVIDER_TIMEOUT',
  'PROVIDER_INVALID_RESPONSE', 'PROVIDER_QUOTA_EXHAUSTED', 'PROVIDER_FAILED',
  'QC_BLOCKED', 'QUOTA_EXCEEDED', 'RATE_LIMITED', 'RESOURCE_EXHAUSTED',
  'ARTIFACT_UNAVAILABLE', 'ARTIFACT_CHECKSUM_MISMATCH', 'LEASE_LOST',
  'PIPELINE_INVARIANT_VIOLATION', 'MANUAL_REVIEW_REQUIRED', 'EXPORT_NOT_READY',
  'STORAGE_UNAVAILABLE', 'CONSENT_REQUIRED', 'POLICY_DENIED', 'INTERNAL_ERROR',
  'INVALID_CREDENTIALS', 'TOKEN_EXPIRED', 'TOKEN_REUSED', 'USER_DISABLED',
  'PREFERENCE_KEY_UNKNOWN', 'PREFERENCE_VALUE_TOO_LARGE', 'TENANT_REQUIRED',
  'LANGUAGE_IMMUTABLE', 'SETTINGS_LOCKED_ACTIVE_RUN', 'PROJECT_NOT_FOUND',
  'PROJECT_HAS_ACTIVE_RUN', 'SETTINGS_VERSION_CONFLICT', 'IDEMPOTENCY_KEY_REQUIRED',
  'IDEMPOTENCY_KEY_REUSED', 'PROJECT_ARCHIVED', 'RUN_ALREADY_TERMINAL',
  'RUN_ALREADY_ACTIVE', 'CONFIG_CHANGED_SINCE_RUN', 'SELECTION_CONFLICT',
  'VERSION_NOT_FOUND', 'VERSION_SEGMENT_MISMATCH', 'SEGMENT_TEXT_EMPTY',
  'SEGMENT_RETRY_ACTIVE', 'VOICE_INCOMPATIBLE', 'VOICE_CONSENT_REQUIRED',
  'PREVIEW_QUOTA_EXCEEDED', 'VOICE_NOT_FOUND', 'PREVIEW_TEXT_INVALID',
  'REVIEW_VERSION_CONFLICT', 'REVIEW_REASON_REQUIRED', 'REVIEW_ALREADY_RESOLVED',
  'REVIEW_NOT_RESOLVED', 'REVIEW_EDIT_EMPTY', 'EXPORT_INCOMPLETE',
  'OUTPUT_INCOMPLETE', 'URL_EXPIRED',
];

function apiFailure(code: string, status: number, correlationId = 'corr-test-1'): unknown {
  return {
    name: 'ApiError',
    status,
    code,
    message: `backend says ${code}`,
    correlationId,
    details: {},
  };
}

describe('normalizeError backend coverage (R4)', () => {
  it('maps every backend code to a kind with a non-empty hint', () => {
    expect(ALL_CODES.length).toBe(65);
    expect(Object.keys(errorKindByCode).sort()).toEqual([...ALL_CODES].sort());
    expect(Object.keys(recoveryHintByCode).sort()).toEqual([...ALL_CODES].sort());
    for (const code of ALL_CODES) {
      const kind: ErrorKind = errorKindByCode[code];
      const hint = recoveryHintByCode[code];
      expect(typeof kind, code).toBe('string');
      expect(hint, code).toMatch(/.{10,}/);
    }
  });

  it('keeps internal/provider failures Unknown with a report hint', () => {
    const unknownCodes = [
      'PROVIDER_CONFIGURATION_ERROR',
      'PROVIDER_TIMEOUT',
      'PROVIDER_INVALID_RESPONSE',
      'PROVIDER_FAILED',
      'PIPELINE_INVARIANT_VIOLATION',
      'INTERNAL_ERROR',
      'URL_EXPIRED',
    ] as const;
    for (const code of unknownCodes) {
      expect(errorKindByCode[code]).toBe('Unknown');
    }
  });

  it('preserves code, message, correlationId, and conflict details', () => {
    const err = normalizeError(
      {
        name: 'ApiError',
        status: 409,
        code: 'SELECTION_CONFLICT',
        message: 'Stale selection.',
        correlationId: 'corr-9',
        details: { currentSelectionVersion: 4, currentVersionIds: ['ver_2'] },
      },
      { method: 'POST' },
    );
    expect(err.kind).toBe('Review');
    expect(err.code).toBe('SELECTION_CONFLICT');
    expect(err.correlationId).toBe('corr-9');
    expect(err.retryable).toBe(false);
    expect(err.details).toEqual({ currentSelectionVersion: 4, currentVersionIds: ['ver_2'] });
    expect(err.recoveryHint).toContain('Refresh');
  });

  it('maps auth failures to Auth with a re-login hint', () => {
    const err = normalizeError(apiFailure('TOKEN_EXPIRED', 401), { method: 'GET' });
    expect(err.kind).toBe('Auth');
    expect(err.retryable).toBe(false);
    expect(err.recoveryHint).toContain('Sign in again');
  });

  it('maps quota codes to Quota with a manage-usage hint', () => {
    const err = normalizeError(apiFailure('QUOTA_EXCEEDED', 429), { method: 'POST' });
    expect(err.kind).toBe('Quota');
    expect(err.recoveryHint).toContain('Admin > Usage');
  });

  it('falls back to Unknown with a report hint for unknown codes', () => {
    const err = normalizeError(apiFailure('SOMETHING_NEW', 500, 'corr-x'), { method: 'GET' });
    expect(err.kind).toBe('Unknown');
    expect(err.correlationId).toBe('corr-x');
    expect(err.recoveryHint).toContain('Report this ID');
  });

  it('uses the request correlation ID when the envelope has none', () => {
    const err = normalizeError(apiFailure('INTERNAL_ERROR', 500, ''), {
      method: 'GET',
      fallbackCorrelationId: 'req-123',
    });
    expect(err.correlationId).toBe('req-123');
  });
});

describe('normalizeError retryable (GET-only)', () => {
  it('marks GET 502/503/504 retryable, mutations never', () => {
    expect(normalizeError(apiFailure('PROVIDER_FAILED', 502), { method: 'GET' }).retryable).toBe(true);
    expect(normalizeError(apiFailure('PROVIDER_FAILED', 503), { method: 'GET' }).retryable).toBe(true);
    expect(normalizeError(apiFailure('PROVIDER_FAILED', 502), { method: 'POST' }).retryable).toBe(false);
    expect(normalizeError(apiFailure('QUOTA_EXCEEDED', 429), { method: 'GET' }).retryable).toBe(false);
    expect(normalizeError(apiFailure('TOKEN_EXPIRED', 401), { method: 'GET' }).retryable).toBe(false);
  });

  it('marks network failures retryable with an offline hint', () => {
    const err = normalizeError(new TypeError('fetch failed'), { method: 'GET' });
    expect(err.kind).toBe('Unknown');
    expect(err.code).toBe(NETWORK_ERROR);
    expect(err.retryable).toBe(true);
    expect(err.recoveryHint).toContain('offline');
  });

  it('maps NOT_AUTHENTICATED to Auth without retry', () => {
    const err: AppError = normalizeError(
      { name: 'NotAuthenticatedError', code: NOT_AUTHENTICATED, message: 'Not authenticated.', correlationId: 'c1' },
      { method: 'GET' },
    );
    expect(err.kind).toBe('Auth');
    expect(err.retryable).toBe(false);
    expect(isAppError(err)).toBe(true);
  });

  it('passes AppError through untouched', () => {
    const original: AppError = {
      kind: 'Conflict',
      code: 'CONFLICT',
      message: 'm',
      correlationId: 'c',
      retryable: false,
      recoveryHint: 'h',
    };
    expect(normalizeError(original)).toBe(original);
  });
});
