import { newCorrelationId } from '../client/index.js';
import type { ErrorCode } from '../client/index.js';
import { NETWORK_ERROR, NOT_AUTHENTICATED } from './kinds.js';
import type { AppError, ErrorKind } from './kinds.js';
import { isAppError } from './kinds.js';

/**
 * Exhaustive backend-code → kind mapping (65 codes, mirrors
 * `src/DubbingPlatform.Application/Errors/ErrorCodes.cs`). Typed as
 * `Record<ErrorCode, ...>` so a new bundle code without an entry here fails
 * typecheck, exactly like `statusToVariant` in Task 016.
 */
export const errorKindByCode: Record<ErrorCode, ErrorKind> = Object.freeze({
  VALIDATION_FAILED: 'Validation',
  UNAUTHORIZED: 'Auth',
  FORBIDDEN: 'Auth',
  NOT_FOUND: 'Validation',
  CONFLICT: 'Conflict',
  MEDIA_UNSUPPORTED: 'Media',
  MEDIA_CORRUPT: 'Media',
  UPLOAD_INCOMPLETE: 'Validation',
  DUPLICATE_MEDIA: 'Conflict',
  PROVIDER_CONFIGURATION_ERROR: 'Unknown',
  PROVIDER_RATE_LIMITED: 'Quota',
  PROVIDER_TIMEOUT: 'Unknown',
  PROVIDER_INVALID_RESPONSE: 'Unknown',
  PROVIDER_QUOTA_EXHAUSTED: 'Quota',
  PROVIDER_FAILED: 'Unknown',
  QC_BLOCKED: 'Media',
  QUOTA_EXCEEDED: 'Quota',
  RATE_LIMITED: 'Quota',
  RESOURCE_EXHAUSTED: 'Quota',
  ARTIFACT_UNAVAILABLE: 'Media',
  ARTIFACT_CHECKSUM_MISMATCH: 'Media',
  LEASE_LOST: 'Conflict',
  PIPELINE_INVARIANT_VIOLATION: 'Unknown',
  MANUAL_REVIEW_REQUIRED: 'Review',
  EXPORT_NOT_READY: 'Conflict',
  STORAGE_UNAVAILABLE: 'Quota',
  CONSENT_REQUIRED: 'Auth',
  POLICY_DENIED: 'Auth',
  INTERNAL_ERROR: 'Unknown',
  INVALID_CREDENTIALS: 'Auth',
  TOKEN_EXPIRED: 'Auth',
  TOKEN_REUSED: 'Auth',
  USER_DISABLED: 'Auth',
  PREFERENCE_KEY_UNKNOWN: 'Validation',
  PREFERENCE_VALUE_TOO_LARGE: 'Validation',
  TENANT_REQUIRED: 'Auth',
  LANGUAGE_IMMUTABLE: 'Validation',
  SETTINGS_LOCKED_ACTIVE_RUN: 'Conflict',
  PROJECT_NOT_FOUND: 'Validation',
  PROJECT_HAS_ACTIVE_RUN: 'Conflict',
  SETTINGS_VERSION_CONFLICT: 'Conflict',
  IDEMPOTENCY_KEY_REQUIRED: 'Validation',
  IDEMPOTENCY_KEY_REUSED: 'Validation',
  PROJECT_ARCHIVED: 'Conflict',
  RUN_ALREADY_TERMINAL: 'Conflict',
  RUN_ALREADY_ACTIVE: 'Conflict',
  CONFIG_CHANGED_SINCE_RUN: 'Conflict',
  SELECTION_CONFLICT: 'Review',
  VERSION_NOT_FOUND: 'Validation',
  VERSION_SEGMENT_MISMATCH: 'Validation',
  SEGMENT_TEXT_EMPTY: 'Validation',
  SEGMENT_RETRY_ACTIVE: 'Conflict',
  VOICE_INCOMPATIBLE: 'Media',
  VOICE_CONSENT_REQUIRED: 'Auth',
  PREVIEW_QUOTA_EXCEEDED: 'Quota',
  VOICE_NOT_FOUND: 'Validation',
  PREVIEW_TEXT_INVALID: 'Validation',
  REVIEW_VERSION_CONFLICT: 'Review',
  REVIEW_REASON_REQUIRED: 'Validation',
  REVIEW_ALREADY_RESOLVED: 'Review',
  REVIEW_NOT_RESOLVED: 'Review',
  REVIEW_EDIT_EMPTY: 'Validation',
  EXPORT_INCOMPLETE: 'Conflict',
  OUTPUT_INCOMPLETE: 'Conflict',
  URL_EXPIRED: 'Unknown',
});

/** Per-code plain-text recovery hints. No HTML, no links with secrets. */
export const recoveryHintByCode: Record<ErrorCode, string> = Object.freeze({
  VALIDATION_FAILED: 'Check the highlighted fields and try again.',
  UNAUTHORIZED: 'Your session expired. Sign in again.',
  FORBIDDEN: 'You do not have permission for this action. Contact your tenant admin.',
  NOT_FOUND: 'This item may have been deleted or you may lack access. Refresh the list and try again.',
  CONFLICT: 'This changed while you worked. Refresh and try again.',
  MEDIA_UNSUPPORTED: 'This media format is not supported. Upload a supported audio or video file.',
  MEDIA_CORRUPT: 'This media file appears to be corrupt. Re-export the source and upload again.',
  UPLOAD_INCOMPLETE: 'The upload did not finish. Resume or restart the upload.',
  DUPLICATE_MEDIA: 'This media was already uploaded to the project. Reuse the existing asset.',
  PROVIDER_CONFIGURATION_ERROR: 'The service is misconfigured. Report this ID to support.',
  PROVIDER_RATE_LIMITED: 'The provider is rate limited. Wait a moment, then retry.',
  PROVIDER_TIMEOUT: 'The provider timed out. Retry; report this ID if it persists.',
  PROVIDER_INVALID_RESPONSE: 'The provider returned an unusable response. Retry; report this ID if it persists.',
  PROVIDER_QUOTA_EXHAUSTED: 'Provider quota is exhausted. Try again later or switch providers in Admin > Provider routes.',
  PROVIDER_FAILED: 'The provider failed this request. Retry; report this ID if it persists.',
  QC_BLOCKED: 'Quality checks blocked this item. Open the quality panel to resolve the flagged codes.',
  QUOTA_EXCEEDED: 'Usage limit reached. Review usage in Admin > Usage, then retry or request more capacity.',
  RATE_LIMITED: 'Too many requests. Wait a moment, then retry.',
  RESOURCE_EXHAUSTED: 'Service capacity is exhausted. Retry shortly; report this ID if it persists.',
  ARTIFACT_UNAVAILABLE: 'This artifact is temporarily unavailable. Retry shortly.',
  ARTIFACT_CHECKSUM_MISMATCH: 'The stored artifact failed integrity verification. Regenerate it and try again.',
  LEASE_LOST: 'Another worker claimed this work. Refresh to see the current state.',
  PIPELINE_INVARIANT_VIOLATION: 'An internal inconsistency was detected. Report this ID to support.',
  MANUAL_REVIEW_REQUIRED: 'This item needs manual review. Open the review queue to resolve it.',
  EXPORT_NOT_READY: 'The export is not ready yet. Wait for it to finish, then retry.',
  STORAGE_UNAVAILABLE: 'Storage is temporarily unavailable. Retry shortly.',
  CONSENT_REQUIRED: 'Voice consent is required before proceeding. Complete the consent step first.',
  POLICY_DENIED: 'Policy denied this action. Contact your tenant admin.',
  INTERNAL_ERROR: 'Something went wrong on our side. Report this ID to support.',
  INVALID_CREDENTIALS: 'Email, password, or tenant is incorrect. Check them and try again.',
  TOKEN_EXPIRED: 'Your session expired. Sign in again.',
  TOKEN_REUSED: 'This session was revoked for safety. Sign in again.',
  USER_DISABLED: 'This account is disabled. Contact your tenant admin.',
  PREFERENCE_KEY_UNKNOWN: 'Unknown preference key. Refresh the page and try again.',
  PREFERENCE_VALUE_TOO_LARGE: 'This preference value is too large. Shorten it and try again.',
  TENANT_REQUIRED: 'No tenant context. Sign in again.',
  LANGUAGE_IMMUTABLE: 'Project languages cannot change after creation.',
  SETTINGS_LOCKED_ACTIVE_RUN: 'Settings are locked while a run is active. Cancel the run first.',
  PROJECT_NOT_FOUND: 'This project may have been deleted or you may lack access. Refresh the list.',
  PROJECT_HAS_ACTIVE_RUN: 'A run is already active for this project. Cancel it before deleting.',
  SETTINGS_VERSION_CONFLICT: 'Project settings changed elsewhere. Reload the project and retry.',
  IDEMPOTENCY_KEY_REQUIRED: 'This request needs an idempotency key. Refresh and try again.',
  IDEMPOTENCY_KEY_REUSED: 'This idempotency key was used with different content. Generate a new request and retry.',
  PROJECT_ARCHIVED: 'This project is archived. Unarchive it before starting work.',
  RUN_ALREADY_TERMINAL: 'This run already finished. Start a new run instead.',
  RUN_ALREADY_ACTIVE: 'A run is already active. Wait for it or cancel it first.',
  CONFIG_CHANGED_SINCE_RUN: 'Configuration changed since this run started. Start a new run.',
  SELECTION_CONFLICT: 'Someone else changed this segment. Refresh to load the current version, then reapply your edit.',
  VERSION_NOT_FOUND: 'This version no longer exists. Refresh to load the current versions.',
  VERSION_SEGMENT_MISMATCH: 'This version belongs to a different segment. Refresh and try again.',
  SEGMENT_TEXT_EMPTY: 'Segment text cannot be empty. Enter text and try again.',
  SEGMENT_RETRY_ACTIVE: 'A retry is already running for this segment. Wait for it to finish.',
  VOICE_INCOMPATIBLE: 'This voice is incompatible with the speaker. Pick a compatible voice from the list.',
  VOICE_CONSENT_REQUIRED: 'Voice consent is required for this voice. Complete the consent step first.',
  PREVIEW_QUOTA_EXCEEDED: 'Preview quota exceeded. Wait for the quota window to reset, then retry.',
  VOICE_NOT_FOUND: 'This voice no longer exists. Pick another voice.',
  PREVIEW_TEXT_INVALID: 'Preview text is invalid. Shorten it and try again.',
  REVIEW_VERSION_CONFLICT: 'This review changed while you worked. Refresh the review context and retry.',
  REVIEW_REASON_REQUIRED: 'A reason is required. Enter a reason and try again.',
  REVIEW_ALREADY_RESOLVED: 'This review is already resolved. Refresh to see the current state.',
  REVIEW_NOT_RESOLVED: 'This review is not resolved yet. Resolve it first.',
  REVIEW_EDIT_EMPTY: 'The manual edit cannot be empty. Enter text and try again.',
  EXPORT_INCOMPLETE: 'The export is incomplete. Retry the export.',
  OUTPUT_INCOMPLETE: 'Output is not fully ready. Wait for processing to finish, then retry.',
  URL_EXPIRED: 'This download link expired. Request a fresh download URL.',
});

const RETRYABLE_STATUS = new Set([502, 503, 504]);

interface ApiErrorLike {
  readonly status: number;
  readonly code: string;
  readonly message: string;
  readonly correlationId: string;
  readonly details: Record<string, unknown>;
}

function asApiErrorLike(value: unknown): ApiErrorLike | undefined {
  if (typeof value !== 'object' || value === null) {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  if (typeof record['status'] !== 'number' || typeof record['code'] !== 'string') {
    return undefined;
  }
  if (typeof record['message'] !== 'string') {
    return undefined;
  }
  const details = record['details'];
  return {
    status: record['status'] as number,
    code: record['code'] as string,
    message: record['message'] as string,
    correlationId: typeof record['correlationId'] === 'string' ? (record['correlationId'] as string) : '',
    details: typeof details === 'object' && details !== null ? (details as Record<string, unknown>) : {},
  };
}

function isAbortError(value: unknown): boolean {
  return (
    (typeof value === 'object' && value !== null && (value as { name?: unknown }).name === 'AbortError') ||
    (value instanceof DOMException && value.name === 'AbortError')
  );
}

function isNetworkFailure(value: unknown): boolean {
  if (isAbortError(value)) {
    return false;
  }
  if (value instanceof TypeError) {
    return true;
  }
  if (value instanceof Error) {
    return /fetch failed|failed to fetch|networkerror|network request failed|offline|load failed/i.test(value.message);
  }
  return false;
}

function kindForStatus(status: number): ErrorKind {
  switch (status) {
    case 400:
    case 404:
    case 410:
    case 413:
    case 415:
    case 422:
      return 'Validation';
    case 401:
    case 403:
      return 'Auth';
    case 409:
      return 'Conflict';
    case 429:
      return 'Quota';
    default:
      return 'Unknown';
  }
}

function hintForStatus(status: number): string {
  switch (status) {
    case 400:
      return 'Check the highlighted fields and try again.';
    case 401:
      return 'Your session expired. Sign in again.';
    case 403:
      return 'You do not have permission for this action. Contact your tenant admin.';
    case 404:
      return 'This item may have been deleted or you may lack access. Refresh and try again.';
    case 409:
      return 'This changed while you worked. Refresh and try again.';
    case 429:
      return 'Too many requests. Wait a moment, then retry.';
    default:
      return 'Something went wrong on our side. Report this ID to support.';
  }
}

export interface NormalizeOptions {
  /** HTTP method of the failed call; controls `retryable`. Defaults to GET. */
  readonly method?: string;
  /** Request correlation ID used when the server envelope has none. */
  readonly fallbackCorrelationId?: string;
}

/**
 * Normalizes any thrown value into an `AppError`. Pure: never throws, never
 * logs, never emits events (401 emission lives in `httpClient`). Unknown
 * codes fall back to `Unknown` with a report hint; the correlation ID is
 * always preserved so error UI can show "report this ID".
 */
export function normalizeError(error: unknown, options?: NormalizeOptions): AppError {
  const method = (options?.method ?? 'GET').toUpperCase();
  const isGet = method === 'GET';

  if (isAppError(error)) {
    return error;
  }

  if (typeof error === 'object' && error !== null) {
    const marker = (error as { code?: unknown }).code;
    if (marker === NOT_AUTHENTICATED) {
      const record = error as { correlationId?: unknown; message?: unknown };
      return {
        kind: 'Auth',
        code: NOT_AUTHENTICATED,
        message: typeof record.message === 'string' ? record.message : 'Not authenticated.',
        correlationId:
          typeof record.correlationId === 'string' && record.correlationId !== ''
            ? record.correlationId
            : (options?.fallbackCorrelationId ?? newCorrelationId()),
        retryable: false,
        recoveryHint: 'Your session expired. Sign in again.',
      };
    }
  }

  if (isNetworkFailure(error)) {
    return {
      kind: 'Unknown',
      code: NETWORK_ERROR,
      message: 'Network unavailable. Check your connection and try again.',
      correlationId: options?.fallbackCorrelationId ?? newCorrelationId(),
      retryable: true,
      recoveryHint: 'You appear to be offline. Reconnect, then retry.',
    };
  }

  if (isAbortError(error)) {
    return {
      kind: 'Unknown',
      code: 'REQUEST_ABORTED',
      message: 'The request was cancelled.',
      correlationId: options?.fallbackCorrelationId ?? newCorrelationId(),
      retryable: false,
      recoveryHint: 'The request was cancelled. Try again if needed.',
    };
  }

  const api = asApiErrorLike(error);
  if (api !== undefined) {
    const known = (errorKindByCode as Record<string, ErrorKind>)[api.code];
    const hint = (recoveryHintByCode as Record<string, string>)[api.code];
    const kind = known ?? kindForStatus(api.status);
    return {
      kind,
      code: api.code,
      message: api.message !== '' ? api.message : 'An unexpected error occurred.',
      correlationId: api.correlationId !== '' ? api.correlationId : (options?.fallbackCorrelationId ?? newCorrelationId()),
      retryable: isGet && RETRYABLE_STATUS.has(api.status),
      recoveryHint: hint ?? `${hintForStatus(api.status)} Report this ID to support.`,
      status: api.status,
      details: api.details,
    };
  }

  return {
    kind: 'Unknown',
    code: 'UNKNOWN_ERROR',
    message: error instanceof Error && error.message !== '' ? error.message : 'An unexpected error occurred.',
    correlationId: options?.fallbackCorrelationId ?? newCorrelationId(),
    retryable: false,
    recoveryHint: 'Something went wrong. Report this ID to support.',
  };
}
