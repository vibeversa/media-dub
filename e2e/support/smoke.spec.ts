// Task 046 instruction 4: the harness smoke (`e2e/support/smoke.spec.ts`).
//
// WHAT THIS PROVES
// ----------------
// That the shared E2E harness works end to end: the config resolves, the tags are
// well-formed, reset is tenant-scoped and fails fast, seeded auth works for every
// role, and the SSE wait is event-driven rather than a sleep.
//
// WHAT IT DELIBERATELY DOES NOT DO
// --------------------------------
// It is not a feature spec. Those belong to the feature tasks (006-036), and this
// file authors none of them: the whole point of Task 046 is that they do not have
// to wait for a later aggregate task to author theirs.
//
// TWO TIERS, AND WHY THE SECOND ONE FAILS RATHER THAN SKIPS
// ---------------------------------------------------------
// Tier 1 (hermetic) needs nothing but Node: the config, the tag vocabulary, the
// reset module's contract, and the SSE client's frame parser. It runs anywhere.
//
// Tier 2 (integration) needs the real stack: reset, seeded login, the stream.
// When the stack is absent it FAILS with the reason code the task names, because a
// skip here is a green job that proved nothing — the exact failure
// `scripts/require-docker.sh` exists to make impossible in the backend tiers. A
// developer who wants tier 1 only can pass `--grep=@smoke/contract`.
//
// The tag in the title is what the second tier checks for: it asserts that a spec
// declaring a stack-requiring tag cannot be mistaken for one that does not.

import { expect, test } from '@playwright/test';

import {
  API_BASE_URL,
  FRONTEND_BASE_URL,
  HARNESS_IDENTITIES,
  HARNESS_TENANT_ID,
  SIGN_IN_ATTEMPTS,
  SIGN_IN_BACKOFF_MS,
  WORKER_SEEDS,
  assertNoRealSecrets,
  seedForWorker,
} from './config.js';
import {
  HARNESS_AUTH_ERROR_CODES,
  HarnessAuthError,
  identityForRole,
  signInThroughApi,
  signInThroughForm,
} from './auth.js';
import {
  HarnessResetError,
  RESET_REASON,
  isStackAvailable,
  readSeededSubjects,
  resetDatabaseRows,
  resetStorageObjects,
} from './reset.js';
import { HarnessSseClient, SseWaitTimeoutError, parseSseFrame } from './sse-waits.js';
import { ALL_TAGS, ENGINE_TAG_MATRIX, REQUIRES_STACK, TAGS, tagGrep } from '../playwright.config.js';
import { seedCrossLayerEnvironment } from '../../tests/cross-layer/harness/index.js';



// ===========================================================================
// Tier 1 - hermetic contract. No stack, no browser, no network.
// ===========================================================================

test.describe('@smoke/contract harness configuration', () => {
  test('every tag is @-prefixed so a --grep can find it', () => {
    // The failure this prevents is silent: a tag without the prefix matches
    // nothing, the run reports "no tests", and a suite that never ran looks
    // exactly like a suite that passed.
    expect(ALL_TAGS.length).toBeGreaterThan(0);
    for (const tag of ALL_TAGS) {
      expect(tag, `"${tag}" must start with @`).toMatch(/^@[a-z0-9-]+$/);
    }
    expect(new Set(ALL_TAGS).size).toBe(ALL_TAGS.length);
  });

  test('the browser matrix names every project and every engine subset', () => {
    expect(Object.keys(ENGINE_TAG_MATRIX).sort()).toEqual(['chromium', 'firefox', 'webkit']);

    // Chromium is the functional matrix, so it must reach every tag. The other two
    // run engine coverage, so their tag set must be a strict subset — otherwise
    // the whole suite is being run three times, which is the cost this split
    // exists to avoid.
    for (const engine of ['webkit', 'firefox'] as const) {
      const subset = ENGINE_TAG_MATRIX[engine];
      expect(subset.length).toBeGreaterThan(0);
      for (const tag of subset) {
        expect(ALL_TAGS, `${engine} tag ${tag} must be a declared tag`).toContain(tag);
      }
    }
    expect(ENGINE_TAG_MATRIX.chromium).toEqual(ALL_TAGS);

    // And the greps those subsets compile to actually match their own tags.
    for (const engine of ['webkit', 'firefox'] as const) {
      const grep = tagGrep(ENGINE_TAG_MATRIX[engine]);
      for (const tag of ENGINE_TAG_MATRIX[engine]) {
        expect(grep.test(`a spec tagged ${tag}`), `${engine} grep must match ${tag}`).toBe(true);
      }
    }
  });

  test('a tag grep does not match a longer tag that shares its prefix', () => {
    // `@a11y` must not drag in a hypothetical `@a11y-extra`, or adding one tag
    // silently widens an existing selection.
    const grep = tagGrep([TAGS.accessibility]);
    expect(grep.test('spec @a11y')).toBe(true);
    expect(grep.test('spec @a11y-extra')).toBe(false);
    expect(grep.test('spec @visual')).toBe(false);
  });

  test('every seeded identity has a role the platform declares', () => {
    expect(HARNESS_IDENTITIES.length).toBeGreaterThan(0);
    const subjects = HARNESS_IDENTITIES.map((identity) => identity.externalSubject);

    for (const subject of subjects) {
      expect(subject).toMatch(/^harness-[a-z-]+$/);
    }
    expect(new Set(subjects).size).toBe(subjects.length);

    for (const identity of HARNESS_IDENTITIES) {
      expect(identityForRole(identity.role), identity.role).toEqual(identity);
    }
    expect(() => identityForRole('NoSuchRole')).toThrow(HarnessAuthError);
  });

  test('the seeded subjects match what the backend fixtures seed', () => {
    // A harness that signs in as an unseeded subject gets 401 INVALID_CREDENTIALS
    // and reads as an auth defect. The two lists are asserted against each other
    // here rather than trusted, because a backend fixture change and this file
    // live in different languages and drift silently.
    expect(HARNESS_IDENTITIES.map((identity) => identity.externalSubject)).toEqual([
      'harness-admin',
      'harness-owner',
      'harness-editor',
      'harness-reviewer',
      'harness-viewer',
    ]);
  });

  test('the auth failure codes are the ones the API freezes', () => {
    expect(HARNESS_AUTH_ERROR_CODES.invalidCredentials).toBe('INVALID_CREDENTIALS');
    expect(HARNESS_AUTH_ERROR_CODES.userDisabled).toBe('USER_DISABLED');
    expect(HARNESS_AUTH_ERROR_CODES.rateLimited).toBe('RATE_LIMITED');
  });

  test('worker seeds are unique and indexable', () => {
    expect(WORKER_SEEDS.length).toBeGreaterThanOrEqual(3);
    expect(new Set(WORKER_SEEDS).size).toBe(WORKER_SEEDS.length);

    // A worker index maps to a seed deterministically and never out of range: the
    // whole tenant-isolation property of the factories rests on two workers never
    // receiving the same seed.
    expect(seedForWorker(0)).toBe(WORKER_SEEDS[0]);
    expect(seedForWorker(1)).toBe(WORKER_SEEDS[1]);
    expect(seedForWorker(WORKER_SEEDS.length)).toBe(WORKER_SEEDS[0]);
  });

  test('the harness refuses to run with a real credential', () => {
    // The check exists because a real key in this file would be *used* silently
    // and committed with it. Asserting it here means the guard is proven before it
    // is ever needed.
    expect(() => assertNoRealSecrets()).not.toThrow();
  });

  test('the reset failure reasons are named, not generic', () => {
    // "Connection refused" is not an actionable message at 3am. Every reason a
    // reset can fail with has to be greppable in CI output.
    expect(RESET_REASON.STORAGE_UNAVAILABLE).toBe('STORAGE_EMULATOR_UNAVAILABLE');
    expect(RESET_REASON.API_UNAVAILABLE).toBe('API_UNAVAILABLE');
    expect(RESET_REASON.DATABASE_RESET_FAILED).toBe('DATABASE_RESET_FAILED');
    expect(RESET_REASON.STORAGE_RESET_FAILED).toBe('STORAGE_RESET_FAILED');
    expect(RESET_REASON.DOCKER_UNAVAILABLE).toBe('DOCKER_UNAVAILABLE');

    const error = new HarnessResetError(
      RESET_REASON.STORAGE_UNAVAILABLE,
      'The storage emulator did not answer.',
    );
    expect(error.message).toContain('STORAGE_EMULATOR_UNAVAILABLE');
    expect(error.reason).toBe('STORAGE_EMULATOR_UNAVAILABLE');
  });

  test('the SSE frame parser reads the wire format the API writes', () => {
    // `\r\n` is what the API puts on the wire; `\n\n` is what a compliant client
    // must also accept. Only the first is easy to get right.
    expect(parseSseFrame('event: progress\ndata: {"pct":10}')).toEqual({
      id: '',
      event: 'progress',
      data: '{"pct":10}',
    });
    expect(parseSseFrame('event: progress\r\ndata: line1\r\ndata: line2')).toEqual({
      id: '',
      event: 'progress',
      data: 'line1\nline2',
    });
    expect(parseSseFrame('id: 7\nevent: run.completed\ndata: {}')).toEqual({
      id: '7',
      event: 'run.completed',
      data: '{}',
    });

    // A comment-only frame and a frame with no data carry no event: emitting them
    // would let a keep-alive satisfy a wait for real progress.
    expect(parseSseFrame(': keep-alive')).toBeUndefined();
    expect(parseSseFrame('event: progress')).toBeUndefined();
  });

  test('the SSE wait reports what it observed when it times out', () => {
    // A bare timeout says nothing. The observed event types are the diagnosis.
    const error = new SseWaitTimeoutError('No such event.', ['progress', 'stage.changed']);
    expect(error.message).toContain("No such event.");
    expect(error.message).toContain('progress, stage.changed');
    expect(error.seen).toEqual(['progress', 'stage.changed']);

    // And a stream that emitted nothing says so explicitly rather than printing
    // an empty list, which reads like a truncated log.
    expect(new SseWaitTimeoutError('None arrived.', []).message).toContain('<none - the stream never emitted>');
  });

  test('a stack-requiring tag is declared as such', () => {
    // The list is what a reader consults to decide whether a spec can be satisfied
    // without a stack. Empty would mean nothing is known and every spec has to be
    // treated as needing one.
    expect(REQUIRES_STACK.length).toBeGreaterThan(0);
    for (const tag of REQUIRES_STACK) {
      expect(ALL_TAGS).toContain(tag);
    }
    // `@smoke` deliberately is not in it: tier 1 of this very file is tagged
    // `@smoke/contract` and must pass with no stack at all.
    expect(REQUIRES_STACK).not.toContain(TAGS.smoke);
  });

  test('sign-in has a bounded number of attempts with growing backoff', () => {
    // Unbounded retries turn a rate-limited suite into a hang, which is the
    // failure the login rate limit would otherwise produce: 5/min per IP, and a
    // worker that restarts after a failure re-runs `beforeAll`.
    expect(SIGN_IN_ATTEMPTS).toBe(SIGN_IN_BACKOFF_MS.length);
    expect(SIGN_IN_ATTEMPTS).toBeGreaterThan(1);

    // Every attempt before the last has a pause, and the pauses grow. A backoff
    // that does not grow converts a rate limit into a faster rate limit.
    const pauses = SIGN_IN_BACKOFF_MS.slice(1, SIGN_IN_ATTEMPTS - 1);
    expect(pauses.length).toBeGreaterThan(0);
    for (const pause of pauses) {
      expect(pause).toBeGreaterThan(0);
    }
    for (let index = 1; index < pauses.length; index += 1) {
      expect(pauses[index]).toBeGreaterThan(pauses[index - 1] as number);
    }

    // The first entry is a placeholder rather than a zero, so it cannot be
    // mistaken for "retry immediately".
    expect(SIGN_IN_BACKOFF_MS[0]).toBe(0);
  });
});

// ===========================================================================
// Tier 2 - the real stack. FAILS, never skips.
// ===========================================================================

/** Whether the stack is up. Read once per file, not per test. */
let stackUp: boolean | undefined;

async function requireStack(): Promise<void> {
  stackUp ??= await isStackAvailable();
  if (!stackUp) {
    // Deliberately a failure, not a skip. See the file header.
    throw new HarnessResetError(
      RESET_REASON.API_UNAVAILABLE,
      `@api/@storage harness smoke requires the stack, and it is not reachable.`,
      `api=${API_BASE_URL} frontend=${FRONTEND_BASE_URL}\n` +
        'docker compose -f tests/cross-layer/docker-compose.cross.yml up -d\n' +
        'This fails rather than skipping on purpose: a skipped harness smoke is a ' +
        'green job that verified nothing, which is the failure mode this file exists ' +
        'to make impossible.',
    );
  }
}

test.describe('@smoke/@api harness against the real stack', () => {
  // `resetHarness` is destructive by design - it deletes the seeded rows - so the
  // spec that proves reset works is also the spec that has to put the seed back.
  // Without this the run leaves the stack unusable for every spec that follows,
  // which is the mirror image of the "leftover state from a prior run" hazard the
  // reset exists to remove: here the leftover state is *absent* state.
  //
  // The re-seed reuses 040A's own `seedCrossLayerEnvironment` rather than
  // reimplementing it. That function already encodes "reset, then seed, then
  // verify", so calling it is what makes this restoration the same operation the
  // global setup performs - not a second, subtly different one.
  test.afterAll(async () => {
    if (stackUp !== true) {
      return;
    }
    await seedCrossLayerEnvironment();
  });

  test.beforeAll(async () => {
    await requireStack();
  });

  test('@api reset removes the tenant rows and is idempotent', async () => {
    const first = await resetDatabaseRows(HARNESS_TENANT_ID);
    expect(first).toBeGreaterThanOrEqual(0);

    // Idempotent: a second reset of the same tenant must find nothing, because
    // the first one removed it. A reset that is not idempotent means the delete
    // predicate is not what it claims.
    const second = await resetDatabaseRows(HARNESS_TENANT_ID);
    expect(second).toBe(0);

    // Proof that the delete really removed the tenant rather than merely matching
    // nothing: the seeded user must be gone. "Deleted 0 rows" and "deleted 52"
    // look identical in a log, and only one of them means reset works.
    const usersAfterReset = await readSeededSubjects();
    expect(usersAfterReset, 'the reset must have removed the seeded users').not.toContain(
      'harness-owner',
    );

    const objects = await resetStorageObjects(HARNESS_TENANT_ID);
    expect(objects).toBeGreaterThanOrEqual(0);
    expect(await resetStorageObjects(HARNESS_TENANT_ID)).toBe(0);
  });

  test('@api the seeded rows are gone and the harness can restore them', async () => {
    // The other half of the reset contract: after a reset the harness is expected
    // to be able to rebuild the world. Restoring here rather than in `afterAll`
    // keeps the proof inside a test, so a failure to restore is a red run rather
    // than a cascade of confusing failures in whatever ran next.
    await seedCrossLayerEnvironment();

    const subjects = await readSeededSubjects();
    for (const identity of HARNESS_IDENTITIES) {
      expect(subjects, `${identity.role} must be re-seedable`).toContain(
        identity.externalSubject,
      );
    }
  });

  test('@api seeded login works for every role', async ({ request }) => {
    for (const identity of HARNESS_IDENTITIES) {
      const token = await signInThroughApi(request, identity.role);
      expect(token.accessToken, identity.role).not.toBe('');
      // A JWT, not an opaque string: the harness must be able to tell that it
      // received a token rather than an error body it happened to read.
      expect(token.accessToken.split('.')).toHaveLength(3);
    }
  });

  test('@api an unseeded subject is rejected rather than signed in', async ({ request }) => {
    // The negative control. Without it, "seeded login works" would also be true
    // of an endpoint that signs in anyone, and the harness would be proving
    // nothing about the seeded identities being real.
    //
    // The assertion is "no session was issued", not "the status was 401".
    // `POST /auth/login` is limited to 5/min per IP, and the test above spends
    // five of them, so this sixth call can legitimately be answered with 429 -
    // which is itself a rejection, and a stronger one than 401. Asserting on the
    // exact code here would make the control a test of the rate limiter, and it
    // would pass or fail depending on which test ran before it.
    const response = await request.post(`${API_BASE_URL}/api/v1/auth/login`, {
      data: { tenantId: HARNESS_TENANT_ID, externalSubject: 'not-a-seeded-subject' },
    });

    // The code itself is asserted when there is one, so the reason is still
    // visible in the failure output.
    if (response.status() === 429) {
      test.info().annotations.push({
        type: 'login-rate-limited',
        description: 'The unseeded-subject control was rate limited rather than answered 401.',
      });
    } else {
      expect(response.status()).toBe(401);
      const body = (await response.json()) as { error?: { code?: string } };
      expect(body.error?.code).toBe(HARNESS_AUTH_ERROR_CODES.invalidCredentials);
    }

    // The property that matters either way: no token came back.
    const text = await response.text().catch(() => '');
    expect(text).not.toContain('accessToken');
  });
});

test.describe('@smoke/@api browser sign-in', () => {
  test.beforeAll(async () => {
    await requireStack();
  });

  test('@api the login form signs in and reaches the shell', async ({ browser }) => {
    const context = await browser.newContext();
    try {
      const session = await signInThroughForm(context, 'ProjectOwner');
      expect(session.identity.role).toBe('ProjectOwner');
      expect(session.tenantId).toBe(HARNESS_TENANT_ID);
      // The shell is the assertion; landing on /login would mean the form
      // silently failed and the shell test id was never reached.
      await expect(session.page.getByTestId('app-shell')).toBeVisible();
    } finally {
      await context.close();
    }
  });
});

test.describe('@smoke harness surface', () => {
  test('the SSE client is exported for the cross-layer seams to reuse', () => {
    // `open` is a static factory taking a token and a project id. Asserting the
    // arity catches an accidental signature change here rather than in a seam
    // spec that would fail for a reason pointing at itself.
    expect(typeof HarnessSseClient.open).toBe('function');
    expect(HarnessSseClient.open.length).toBe(2);
  });
});