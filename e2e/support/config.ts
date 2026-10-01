// Task 046: the E2E harness's shared constants.
//
// WHY THIS FILE EXISTS SEPARATE FROM THE RIG'S config.ts
// ------------------------------------------------------
// `tests/cross-layer/harness/config.ts` (040A) describes ONE stack: fixed ports,
// one tenant, one seeder, and fail-closed preflight. This file describes the
// *harness*: where to point, which roles exist, how long a wait may last, and
// which seeds are legal. They overlap on ports deliberately - the harness runs
// against the rig - but they answer different questions, and a feature task
// should not have to read a cross-layer rig document to learn how to sign in.
//
// EVERY SECRET IS A PLACEHOLDER
// -----------------------------
// `CHANGE_ME`, never a real credential. `assertNoRealSecrets()` enforces it at
// import time, which is the earliest point at which a leaked literal can be
// caught before it is committed - the alternative, a grep in CI, is a gate that
// is added after the incident.

/** Loopback, not `localhost`: Docker Desktop resets IPv6 connections to
 * published ports, so Chromium resolves `localhost` to a dead `::1` while the
 * service is healthy. This is the same spelling 040A and 041B settled on. */
export const LOOPBACK_HOST = '127.0.0.1';

/** The API the harness talks to. Overridable so CI can point elsewhere. */
export const API_BASE_URL =
  process.env['E2E_API_BASE_URL'] ?? `http://${LOOPBACK_HOST}:58080`;

/** The frontend the browser loads. */
export const FRONTEND_BASE_URL =
  process.env['E2E_BASE_URL'] ?? `http://${LOOPBACK_HOST}:54173`;

/** The storage emulator's S3 endpoint, used by `reset.ts` to wipe objects. */
export const STORAGE_ENDPOINT =
  process.env['E2E_STORAGE_ENDPOINT'] ?? `http://${LOOPBACK_HOST}:59000`;

/** The bucket the rig writes to. */
export const STORAGE_BUCKET = process.env['E2E_STORAGE_BUCKET'] ?? 'dubbing';

/**
 * Emulator credentials. `CHANGE_ME` placeholders only - these match the rig's
 * compose file and are confined to a local stack. `assertNoRealSecrets()` fails
 * the run if either ever stops being a placeholder.
 */
export const STORAGE_ACCESS_KEY = process.env['E2E_STORAGE_ACCESS_KEY'] ?? 'CHANGE_ME';
export const STORAGE_SECRET_KEY = process.env['E2E_STORAGE_SECRET_KEY'] ?? 'CHANGE_ME';

/**
 * The seeded tenant's id.
 *
 * Fixed, so a failure is reproducible and `reset.ts` can address exactly one
 * tenant. The seeder owns the row; this is only the handle.
 */
export const HARNESS_TENANT_ID = '11111111-1111-1111-1111-111111111111';

/**
 * The seeded identities, one per role (Task 046 instruction 2: "seeded auth for
 * all roles").
 *
 * The subjects are the ones `SyntheticUsers` seeds on the backend. A harness that
 * signed in as an unseeded subject would get `401 INVALID_CREDENTIALS` and read
 * as an auth defect, so the two lists are asserted to agree by
 * `support/smoke.spec.ts`.
 *
 * There is no password anywhere. `POST /api/v1/auth/login` takes a tenant id and
 * an external subject; the product has no local password for these accounts and
 * inventing one here would suggest otherwise.
 */
export interface HarnessIdentity {
  /** The role this identity is seeded to hold. */
  readonly role: string;
  /** The `externalSubject` the login form and `POST /auth/login` take. */
  readonly externalSubject: string;
  /** The display name the seeded row carries. */
  readonly displayName: string;
}

export const HARNESS_IDENTITIES: readonly HarnessIdentity[] = [
  { role: 'TenantAdmin', externalSubject: 'harness-admin', displayName: 'Harness Administrator' },
  { role: 'ProjectOwner', externalSubject: 'harness-owner', displayName: 'Harness Owner' },
  { role: 'ProjectEditor', externalSubject: 'harness-editor', displayName: 'Harness Editor' },
  { role: 'Reviewer', externalSubject: 'harness-reviewer', displayName: 'Harness Reviewer' },
  { role: 'ProjectViewer', externalSubject: 'harness-viewer', displayName: 'Harness Viewer' },
];

/**
 * Per-worker seeds.
 *
 * Task 046's edge case is "Parallel workers share tenant -> factories issue
 * isolated tenant per worker (no cross-test leakage)". These are the seeds a
 * worker passes to the factories; each derives a disjoint graph.
 */
export const WORKER_SEEDS: readonly string[] = [
  '22222222-2222-2222-2222-222222222222',
  '33333333-3333-3333-3333-333333333333',
  '44444444-4444-4444-4444-444444444444',
];

/**
 * The seed for the current Playwright worker.
 *
 * Indexed by `workerIndex` modulo the seed list, so two workers never share a
 * seed and a worker restart lands on a known one.
 */
export function seedForWorker(workerIndex: number): string {
  const seed = WORKER_SEEDS[workerIndex % WORKER_SEEDS.length];
  if (seed === undefined) {
    // Unreachable for any real index; present so the non-null narrowing below is
    // a fact rather than a cast.
    throw new Error(`WORKER_SEED_EXHAUSTED: no seed for worker index ${workerIndex}.`);
  }
  return seed;
}

/**
 * `POST /api/v1/auth/login` is limited to 5/min per IP.
 *
 * The visual matrix measured this the hard way: 144 cells signing in individually
 * needed half an hour of deliberate waiting. Every helper here shares ONE session
 * per file for the same reason, and signs in again only after a failure.
 */
export const LOGIN_RATE_LIMIT_PER_MINUTE = 5;

/** Sign-in attempts, and the pause before each retry. Index 0 is unused. */
export const SIGN_IN_BACKOFF_MS: readonly number[] = [0, 2_000, 15_000, 30_000];

/** Attempts allowed, including the first. */
export const SIGN_IN_ATTEMPTS = SIGN_IN_BACKOFF_MS.length;

/**
 * Ceiling on any event-driven wait.
 *
 * A deadline is a FAILURE REPORT, not a retry policy: it bounds how long a broken
 * stream is waited on so the run reports "no such event, here is what did arrive"
 * instead of hanging until the runner's own timeout with no diagnosis.
 */
export const DEFAULT_WAIT_TIMEOUT_MS = 60_000;

/** How long to wait for the API's liveness probe during preflight. */
export const HEALTH_TIMEOUT_MS = 30_000;

/** How often a preflight re-probes while waiting. */
export const HEALTH_POLL_MS = 1_000;

/**
 * Fails fast when a credential is not a placeholder.
 *
 * Called at import time by `reset.ts` and by `auth.ts`. A real key that reached
 * this file would otherwise be used silently and would be committed with it; the
 * check makes the failure a boot-time crash instead.
 *
 * @throws When either storage credential is set to something other than `CHANGE_ME`.
 */
export function assertNoRealSecrets(): void {
  const offenders: string[] = [];
  if (STORAGE_ACCESS_KEY !== 'CHANGE_ME') {
    offenders.push('E2E_STORAGE_ACCESS_KEY');
  }
  if (STORAGE_SECRET_KEY !== 'CHANGE_ME') {
    offenders.push('E2E_STORAGE_SECRET_KEY');
  }
  if (offenders.length > 0) {
    throw new Error(
      `HARNESS_REAL_SECRET: ${offenders.join(', ')} is set to a real value. The harness only ` +
        'ever runs against a local emulator and must never carry a live credential. Use ' +
        'CHANGE_ME, or unset the variable to take the placeholder.',
    );
  }
}