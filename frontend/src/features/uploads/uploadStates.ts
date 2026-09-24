/**
 * Upload phase model and rejection guidance (Task 023).
 *
 * Client transport phases (`uploading`, `paused`, `error`, `aborted`) are kept
 * visually distinct from server validation phases (`uploaded`,
 * `server-validation`, `analysis`, `ready`, `rejected`): the UI renders them
 * as separate step groups so users never confuse "bytes in flight" with
 * "server verdict". All server snapshots come from generated bundle types
 * (`UploadSession.status`, `Project.status`); this module only maps the raw
 * strings, so unknown future statuses degrade to the current phase instead of
 * crashing. Pure: no React, no storage, fully unit-testable.
 */

/** Persisted client/server phase for one project upload. */
export type UploadPhase =
  | 'uploading'
  | 'paused'
  | 'uploaded'
  | 'validating'
  | 'analyzing'
  | 'ready'
  | 'rejected'
  | 'aborted'
  | 'error';

/** Terminal phases: the engine stops polling and the session may be cleared. */
export const TERMINAL_PHASES: readonly UploadPhase[] = ['ready', 'rejected', 'aborted'];

/** Phases owned by the client transport (bytes in flight or held locally). */
export const CLIENT_PHASES: readonly UploadPhase[] = ['uploading', 'paused', 'error'];

/** Phases owned by the server (bytes accepted, verdict pending or given). */
export const SERVER_PHASES: readonly UploadPhase[] = [
  'uploaded',
  'validating',
  'analyzing',
  'ready',
  'rejected',
];

/** Machine-readable rejection cause. Every value has user guidance (R4). */
export type RejectionReason = 'duplicate' | 'unsupported' | 'corrupt' | 'expired' | 'unknown';

export type RejectionAction = 'use-existing' | 'pick-format' | 'reupload' | 'start-over' | 'contact-support';

export interface RejectionGuide {
  readonly reason: RejectionReason;
  /** Next action the UI offers (link/button kind). */
  readonly action: RejectionAction;
}

/**
 * Reason → next action (R4). Titles and guidance copy live in the `uploads`
 * i18n namespace under `rejected.<reason>.*`; this table stays code-only so
 * copy changes never touch logic.
 */
export const REJECTION_GUIDES: Record<RejectionReason, RejectionGuide> = {
  duplicate: { reason: 'duplicate', action: 'use-existing' },
  unsupported: { reason: 'unsupported', action: 'pick-format' },
  corrupt: { reason: 'corrupt', action: 'reupload' },
  expired: { reason: 'expired', action: 'start-over' },
  unknown: { reason: 'unknown', action: 'contact-support' },
};

/** All reasons with guides (exhaustive by construction; tests assert size). */
export const ALL_REJECTION_REASONS: readonly RejectionReason[] = [
  'duplicate',
  'unsupported',
  'corrupt',
  'expired',
  'unknown',
];

/**
 * Maps a backend error code (envelope `error.code`, `details.reason`) to a
 * rejection reason. Unknown codes fall back to `unknown` with the correlation
 * id preserved by the caller for support.
 */
export function reasonFromErrorCode(code: string): RejectionReason {
  switch (code) {
    case 'DUPLICATE_MEDIA':
      return 'duplicate';
    case 'MEDIA_UNSUPPORTED':
      return 'unsupported';
    case 'MEDIA_CORRUPT':
      return 'corrupt';
    default:
      return 'unknown';
  }
}

/**
 * Maps an `UploadSession.status` string to a rejection reason when it denotes
 * failure, else null. `Aborted` is a terminal client state, not a rejection.
 */
export function reasonFromSessionStatus(status: string): RejectionReason | null {
  switch (status) {
    case 'Duplicate':
      return 'duplicate';
    case 'Expired':
      return 'expired';
    default:
      return null;
  }
}

export interface ServerSnapshot {
  /** Raw `UploadSession.status` (`Created|InProgress|Completed|Aborted|Expired|Duplicate`). */
  readonly uploadStatus: string;
  /** Raw `Project.status` (`Uploading|MediaReady|MediaRejected|...`). */
  readonly projectStatus: string;
}

export type ValidationOutcome =
  | { readonly kind: 'validating' }
  | { readonly kind: 'analyzing' }
  | { readonly kind: 'ready' }
  | { readonly kind: 'rejected'; readonly reason: RejectionReason }
  | { readonly kind: 'aborted' }
  | { readonly kind: 'waiting' };

/**
 * Derives the validation outcome from one poll round.
 *
 * The bundle exposes no dedicated "analysis running" flag, so the split is
 * derived honestly from progression: a freshly completed session reads
 * `validating`; after `analysisAfterPolls` consecutive non-terminal rounds it
 * reads `analyzing` (media analysis runs server-side). Terminal project and
 * session states always win regardless of poll count.
 */
export function deriveValidationOutcome(
  snapshot: ServerSnapshot,
  completedPolls: number,
  analysisAfterPolls = 3,
): ValidationOutcome {
  const sessionReason = reasonFromSessionStatus(snapshot.uploadStatus);
  if (sessionReason !== null) {
    return { kind: 'rejected', reason: sessionReason };
  }
  if (snapshot.uploadStatus === 'Aborted') {
    return { kind: 'aborted' };
  }
  if (snapshot.projectStatus === 'MediaRejected') {
    return { kind: 'rejected', reason: 'unknown' };
  }
  if (snapshot.projectStatus === 'MediaReady') {
    return { kind: 'ready' };
  }
  if (snapshot.uploadStatus === 'Completed') {
    return completedPolls >= analysisAfterPolls ? { kind: 'analyzing' } : { kind: 'validating' };
  }
  return { kind: 'waiting' };
}
