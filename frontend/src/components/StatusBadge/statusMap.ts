import type { OutputState, ProviderHealth, ReviewStatus as GeneratedReviewStatus, VoicePreviewStatus as GeneratedVoicePreviewStatus } from '../../api/client/index.js';
import type { BadgeTone } from '../Badge/Badge.js';
import { trackUnknownStatus } from '../../telemetry/telemetry.js';

export type ProviderStatus = NonNullable<ProviderHealth['status']>;
export type CircuitBreakerState = NonNullable<ProviderHealth['circuitBreakerState']>;

/** Domain mirrors for statuses the bundle (still) types as string. Kept in sync with src/DubbingPlatform.Domain/Enums/*.cs. */
export type ProjectStatus = 'Created' | 'Uploading' | 'MediaReady' | 'MediaRejected' | 'Processing' | 'Cancelling' | 'Cancelled' | 'Completed' | 'Failed' | 'ManualReviewRequired';
export type ProcessingRunStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelling' | 'Cancelled' | 'ManualReviewRequired';
export type StageStatus = 'Pending' | 'Scheduled' | 'Running' | 'Completed' | 'Failed' | 'RetryPending' | 'Cancelled' | 'ManualReviewRequired' | 'Skipped';
export type DomainReviewStatus = 'Open' | 'Approved' | 'Rejected' | 'Requeued' | 'ResolvedWithEdit';
export type ExportJobStatus = 'Pending' | 'Running' | 'Completed' | 'Failed' | 'Cancelled';
export type SyncStatus = 'SyncAcceptable' | 'SyncAcceptableWithWarning' | 'SyncRetryable' | 'ManualReviewRequired';
export type QualityStatus = 'Pass' | 'PassWithWarnings' | 'RetryRequired' | 'ManualReviewRequired' | 'Blocked';
export type MediaAssetStatus = 'Pending' | 'Valid' | 'Invalid';
export type UploadStatus = 'Created' | 'InProgress' | 'Completed' | 'Aborted' | 'Expired' | 'Duplicate';
export type ArtifactStatus = 'Pending' | 'Committed' | 'Deleted';
export type ContentObjectStatus = 'Pending' | 'Committed' | 'Orphaned' | 'Deleted';
export type ConsentStatus = 'Granted' | 'Revoked' | 'Expired' | 'Pending';
export type TenantUserStatus = 'Active' | 'Disabled';
export type VoicePreviewConsentState = 'Verified' | 'Blocked';

/**
 * Every backend status the UI may render. Generated-client unions are included
 * by name so a new bundle enum member without a mapping below fails typecheck.
 */
export type BackendStatus =
  | OutputState
  | GeneratedReviewStatus
  | GeneratedVoicePreviewStatus
  | ProviderStatus
  | CircuitBreakerState
  | ProjectStatus
  | ProcessingRunStatus
  | StageStatus
  | DomainReviewStatus
  | ExportJobStatus
  | SyncStatus
  | QualityStatus
  | MediaAssetStatus
  | UploadStatus
  | ArtifactStatus
  | ContentObjectStatus
  | ConsentStatus
  | TenantUserStatus
  | VoicePreviewConsentState;

export type StatusVariant = BadgeTone;

function assertNever(value: never): never {
  throw new Error(`Unhandled status: ${String(value)}`);
}

/** Exhaustive status → token mapping. Add new statuses here; missing cases fail typecheck. */
export function statusToVariant(status: BackendStatus): StatusVariant {
  switch (status) {
    case 'Completed':
    case 'Ready':
    case 'Approved':
    case 'ResolvedWithEdit':
    case 'Healthy':
    case 'Closed':
    case 'Committed':
    case 'Valid':
    case 'Granted':
    case 'Verified':
    case 'Active':
    case 'Pass':
    case 'SyncAcceptable':
      return 'success';
    case 'Pending':
    case 'Scheduled':
    case 'Queued':
    case 'RetryPending':
    case 'RetryRequired':
    case 'PassWithWarnings':
    case 'SyncAcceptableWithWarning':
    case 'SyncRetryable':
    case 'Degraded':
    case 'Unknown':
    case 'Partial':
      return 'warning';
    case 'Processing':
    case 'Running':
    case 'Generating':
    case 'Cancelling':
    case 'InProgress':
    case 'Uploading':
      return 'processing';
    case 'ManualReviewRequired':
    case 'Open':
    case 'Requeued':
      return 'review';
    case 'Failed':
    case 'MediaRejected':
    case 'Invalid':
    case 'Blocked':
    case 'Rejected':
    case 'Unavailable':
    case 'Down':
      return 'error';
    case 'Cancelled':
    case 'Aborted':
    case 'Expired':
    case 'Skipped':
    case 'Deleted':
    case 'Orphaned':
    case 'Disabled':
    case 'Revoked':
    case 'Duplicate':
      return 'cancelled';
    case 'Created':
    case 'MediaReady':
      return 'info';
    default:
      return assertNever(status);
  }
}

export const KNOWN_STATUSES: ReadonlySet<string> = new Set<string>([
  'Ready', 'Generating', 'Failed', 'Partial', 'Unavailable',
  'Open', 'Approved', 'Rejected', 'Requeued', 'ResolvedWithEdit',
  'Queued', 'Running', 'Completed', 'Cancelled', 'Cancelling',
  'Unknown', 'Healthy', 'Degraded', 'Down', 'Closed',
  'Created', 'Uploading', 'MediaReady', 'MediaRejected', 'Processing',
  'Pending', 'Scheduled', 'RetryPending', 'Skipped',
  'SyncAcceptable', 'SyncAcceptableWithWarning', 'SyncRetryable',
  'Pass', 'PassWithWarnings', 'RetryRequired', 'Blocked',
  'Valid', 'Invalid', 'InProgress', 'Aborted', 'Expired', 'Duplicate',
  'Committed', 'Deleted', 'Orphaned', 'Granted', 'Revoked',
  'Active', 'Disabled', 'Verified', 'ManualReviewRequired',
]);

export function isKnownStatus(status: string): boolean {
  return KNOWN_STATUSES.has(status);
}

export function warnUnknownStatus(status: string): void {
  // Task 018: unknown statuses emit telemetry (allowlisted status-only event)
  // in addition to the dev-visible console warning.
  trackUnknownStatus(status);
  console.warn(`[StatusBadge] unknown status: ${status}`);
}
