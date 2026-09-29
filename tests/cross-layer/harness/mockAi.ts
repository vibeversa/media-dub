// Task 040A: mock-AI contract and outage handling (R4, instruction 4).
//
// R1 requires the rig to run on mock AI *only* - a seam must never reach a real
// provider. R4 requires the opposite failure to be explicit and fast: when the
// mock provider cannot serve a request, the rig must fail with a named error
// rather than hang until a generic timeout.
//
// `AI_MOCK_UNAVAILABLE` is a harness-level sentinel, not a product error code.
// It is deliberately not added to
// `DubbingPlatform.Application/Errors/ErrorCodes.cs`: the product already has
// precise codes for each mock failure mode (below), and inventing a new public
// error code to describe a test rig would leak test vocabulary into the API
// contract. The sentinel names the *rig's* conclusion - "the mock AI path did
// not deliver, so the seam under test did not run" - and carries the product
// code that caused it.

import type { WorkspaceSnapshot } from './apiClient.js';

/** Rig-level sentinel for "the mock AI path did not deliver". */
export const AI_MOCK_UNAVAILABLE = 'AI_MOCK_UNAVAILABLE' as const;

/**
 * Product error codes that mean the mock provider could not serve a request.
 * These come from `MockBehaviorEvaluator`, which maps the
 * `Providers:Mock:Scenario` values `rate-limited`, `timeout`, `malformed`,
 * `expired` and `quota-exhausted` onto them. Reproduce an outage by setting
 * `Providers__Mock__Scenario` on the `api` service and restarting it.
 */
export const PROVIDER_UNAVAILABLE_CODES: readonly string[] = [
  'PROVIDER_RATE_LIMITED',
  'PROVIDER_TIMEOUT',
  'PROVIDER_INVALID_RESPONSE',
  'PROVIDER_FAILED',
  'PROVIDER_QUOTA_EXHAUSTED',
  'PROVIDER_CONFIGURATION_ERROR',
];

/**
 * Fail-fast budget for one mock-AI call path. Generous enough not to flake on a
 * cold container, far below any generic HTTP timeout, so an outage surfaces as
 * a named rig error rather than as a hung pipeline.
 */
export const MOCK_AI_PROBE_TIMEOUT_MS = 30_000;

export class AiMockUnavailableError extends Error {
  readonly code = AI_MOCK_UNAVAILABLE;
  readonly cause?: string;

  constructor(message: string, cause?: string) {
    super(
      `${AI_MOCK_UNAVAILABLE}: ${message}` +
        (cause === undefined ? '' : `\nUnderlying provider code: ${cause}`),
    );
    this.name = 'AiMockUnavailableError';
    this.cause = cause;
  }
}

/**
 * Extracts a product error code from anything the run/stream surfaced, so a
 * provider failure is reported by its real cause rather than by its position in
 * a log.
 */
export function classifyProviderFailure(value: unknown): string | undefined {
  if (typeof value === 'string') {
    return PROVIDER_UNAVAILABLE_CODES.find((code) => value.includes(code));
  }
  if (value === null || typeof value !== 'object') {
    return undefined;
  }

  const record = value as Record<string, unknown>;
  for (const key of ['code', 'errorCode', 'error', 'reason', 'message', 'status']) {
    const candidate = record[key];
    if (typeof candidate === 'string') {
      const match = PROVIDER_UNAVAILABLE_CODES.find((code) => candidate.includes(code));
      if (match !== undefined) {
        return match;
      }
    }
  }

  for (const nested of Object.values(record)) {
    const match = classifyProviderFailure(nested);
    if (match !== undefined) {
      return match;
    }
  }
  return undefined;
}

/** Run states that mean the pipeline stopped rather than progressed. */
const TERMINAL_FAILURE_STATUSES: readonly string[] = ['Failed', 'Faulted', 'Cancelled'];

/**
 * Converts a terminal or stalled run into a decision: continue, or fail with
 * the mock-AI sentinel. The caller supplies the stall so this stays a pure
 * function and the timeout budget lives in one place.
 */
export function assertMockAiDelivered(input: {
  readonly runStatus: string;
  readonly runDetail: unknown;
  readonly stalledMs: number;
}): void {
  const cause = classifyProviderFailure(input.runDetail);

  if (cause !== undefined) {
    throw new AiMockUnavailableError(
      `The mock AI provider failed the run (status '${input.runStatus}') within ` +
        `${input.stalledMs}ms. Recreate the outage with ` +
        "Providers__Mock__Scenario=rate-limited on the api service.",
      cause,
    );
  }

  if (TERMINAL_FAILURE_STATUSES.includes(input.runStatus)) {
    throw new AiMockUnavailableError(
      `The run reached '${input.runStatus}' before the mock AI stage produced output, ` +
        `after ${input.stalledMs}ms. The mock AI path is the only provider on this rig, ` +
        'so no seam downstream of it can be trusted.',
    );
  }

  if (input.stalledMs >= MOCK_AI_PROBE_TIMEOUT_MS) {
    throw new AiMockUnavailableError(
      `The mock AI path produced no deliverable within ${MOCK_AI_PROBE_TIMEOUT_MS}ms. ` +
        'Failing fast rather than hanging: a silent stall is indistinguishable from a ' +
        'green run that never executed.',
    );
  }
}

/**
 * Deterministic transcript expectation, mirroring
 * `MockTranscriptionProvider`: text is
 * `mock transcript seg {artifactId} [{language}]` at confidence 0.95 unless the
 * low-confidence scenario is selected, and word timings are fixed offsets.
 */
export function expectedTranscriptText(artifactId: string, language: string): string {
  return `mock transcript seg ${artifactId} [${language}]`;
}

export const EXPECTED_MOCK_CONFIDENCE = 0.95;

/**
 * Cross-layer consistency check for the mock-AI path.
 *
 * <para>
 * This deliberately does <em>not</em> require the pipeline to run to completion.
 * Reaching segments and exports needs the FFmpeg-backed media workers and a real
 * uploaded media file, which is media-pipeline integration rather than harness
 * scope. What it does require is that the rig never reports progress the layers
 * did not actually deliver:
 * </para>
 * <list type="bullet">
 * <item>a terminal run failure is surfaced as the mock-AI sentinel (R4),</item>
 * <item>a workspace that has advanced past media validation must show review
 * work or completed units - a run that claims to be transcribing while nothing
 * exists anywhere is the hollow green this whole rig exists to prevent, and</item>
 * <item>a still-early run is fine and is not treated as a failure.</item>
 * </list>
 */
export function assertMockPipelineConsistent(input: {
  readonly runStatus: string;
  readonly workspace: WorkspaceSnapshot;
}): void {
  const { runStatus, workspace } = input;

  if (TERMINAL_FAILURE_STATUSES.includes(runStatus)) {
    throw new AiMockUnavailableError(
      `The run reached '${runStatus}'. The mock AI path is the only provider on this ` +
        'rig, so no seam downstream of it can be trusted. Reproduce a provider outage ' +
        "with Providers__Mock__Scenario=rate-limited on the api and ai services.",
    );
  }

  const stage = (workspace.stage ?? '').toLowerCase();
  const pending = workspace.review?.pendingCount ?? 0;
  const progress = workspace.progress ?? {};
  const completedUnits = Number(progress['completedUnits'] ?? 0);
  const expectedUnits = Number(progress['expectedUnits'] ?? 0);

  const claimsWork = stage !== '' && !stage.includes('mediavalidation') && !stage.includes('upload');
  if (claimsWork && pending === 0 && completedUnits === 0) {
    throw new AiMockUnavailableError(
      `The workspace reports stage '${workspace.stage}' but shows no review work and no ` +
        'completed units. The pipeline claims to have progressed while nothing was ' +
        'produced, which would otherwise read as a passing seam.',
      undefined,
    );
  }

  if (completedUnits > 0 && expectedUnits > 0 && completedUnits > expectedUnits) {
    throw new AiMockUnavailableError(
      `The workspace reports ${completedUnits} completed units against ${expectedUnits} ` +
        'expected. Progress accounting is inconsistent across layers.',
    );
  }
}
