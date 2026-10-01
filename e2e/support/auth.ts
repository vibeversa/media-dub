// Task 046: seeded auth for every role.
//
// WHY THE LOGIN GOES THROUGH THE FORM, NOT THE API
// -------------------------------------------------
// `signInThroughApi()` exists for the specs that need a bearer token for a
// cross-layer assertion. The default is the FORM, because the form is the path
// every user takes, and a session seeded by any other route proves the token
// endpoint works while leaving the sign-in screen - which has its own test ids,
// its own error states and its own 401 handling - untested. 041B's finding that
// "most 019-035 specs still use the pre-041A `auth-tenant` / `auth-password`
// pair" is the shape of that gap: a suite that signs in by API never notices the
// form moved.
//
// WHY ONE SESSION PER FILE
// -----------------------
// `POST /api/v1/auth/login` is limited to 5/min per IP. A file with N specs that
// each signed in would need N/5 minutes of deliberate waiting, or would flake for
// everyone after the fifth. So a session is cached per (browser context, role) and
// reused; `signOut()` exists for the one spec that genuinely needs to observe the
// signed-out state.
//
// The cache is per worker, not global, because a Playwright worker's module
// registry is its own: a global would leak one worker's session into another.

import type { APIRequestContext, BrowserContext, Page } from '@playwright/test';

import {
  API_BASE_URL,
  HARNESS_IDENTITIES,
  HARNESS_TENANT_ID,
  SIGN_IN_ATTEMPTS,
  SIGN_IN_BACKOFF_MS,
  assertNoRealSecrets,
  type HarnessIdentity,
} from './config.js';

/** The `data-testid` of the login form's tenant field. */
export const TENANT_INPUT = 'auth-tenant-id';

/** The `data-testid` of the login form's external-subject field. */
export const SUBJECT_INPUT = 'auth-external-subject';

/** The `data-testid` of the login form's submit button. */
export const SUBMIT_BUTTON = 'auth-submit';

/**
 * The auth error codes the API freezes.
 *
 * Named here so a spec asserts on a constant instead of a string literal. The
 * list is short on purpose: these are the three a sign-in can produce, and a
 * longer one would be a list nobody maintains.
 *
 * `INVALID_CREDENTIALS` is what an unseeded subject gets, which is why the smoke
 * asserts on it: "seeded login works" is also true of an endpoint that signs in
 * anyone, and the negative case is what makes the positive one mean something.
 */
export const HARNESS_AUTH_ERROR_CODES = {
  invalidCredentials: 'INVALID_CREDENTIALS',
  userDisabled: 'USER_DISABLED',
  rateLimited: 'RATE_LIMITED',
} as const;

/** The shell root, present only once authenticated. */
export const SHELL_TEST_ID = 'app-shell';

/** The login page root. Present when signed out. */
export const LOGIN_PAGE_TEST_ID = 'page-login';

/** Thrown when a sign-in cannot be completed. */
export class HarnessAuthError extends Error {
  readonly detail: string;

  constructor(message: string, detail = '') {
    super(detail.length === 0 ? message : `${message}\n${detail}`);
    this.name = 'HarnessAuthError';
    this.detail = detail;
  }
}

/** A session for one role. */
export interface HarnessSession {
  readonly identity: HarnessIdentity;
  readonly tenantId: string;
  /** The minted access token, for cross-layer assertions. */
  readonly accessToken: string;
  readonly page: Page;
}

/** The identity for a role name, or a list of the legal names. */
export function identityForRole(role: string): HarnessIdentity {
  const found = HARNESS_IDENTITIES.find((identity) => identity.role === role);
  if (found === undefined) {
    throw new HarnessAuthError(
      `No seeded identity for role '${role}'.`,
      `Seeded roles: ${HARNESS_IDENTITIES.map((identity) => identity.role).join(', ')}`,
    );
  }
  return found;
}

/**
 * Signs in through the form and returns the shell page.
 *
 * Retries through the login rate limit. Normally a suite signs in once, but
 * Playwright restarts the worker after a test failure and re-runs `beforeAll`, so
 * one early failure can burn the budget and turn a single flaky cell into a suite
 * reporting "117 did not run" - which looks like a dead suite rather than one
 * failure. Backing off keeps a transient 429 from being fatal.
 *
 * @param context - The browser context; the session lives in its storage.
 * @param role - Which seeded role to sign in as.
 * @param page - A page in that context. One is created when omitted.
 * @returns The signed-in page and the minted token.
 * @throws HarnessAuthError When every attempt fails.
 */
export async function signInThroughForm(
  context: BrowserContext,
  role: string,
  page?: Page,
): Promise<HarnessSession> {
  assertNoRealSecrets();
  const identity = identityForRole(role);
  const target = page ?? (await context.newPage());

  let lastReason = 'no attempt was made';

  for (let attempt = 1; attempt <= SIGN_IN_ATTEMPTS; attempt += 1) {
    await target.goto('/login', { waitUntil: 'domcontentloaded' });
    await target.getByTestId(LOGIN_PAGE_TEST_ID).waitFor({ state: 'visible', timeout: 30_000 });

    await target.getByTestId(TENANT_INPUT).fill(HARNESS_TENANT_ID);
    await target.getByTestId(SUBJECT_INPUT).fill(identity.externalSubject);
    await target.getByTestId(SUBMIT_BUTTON).click();

    const signedIn = await target
      .getByTestId(SHELL_TEST_ID)
      .waitFor({ state: 'visible', timeout: 30_000 })
      .then(() => true)
      .catch(() => false);

    if (signedIn) {
      return { identity, tenantId: HARNESS_TENANT_ID, accessToken: await readAccessToken(context), page: target };
    }

    // The failure page's own text is the diagnosis. Without it the next message
    // is "could not sign in", which is equally true of a 401, a 429 and a stack
    // that is not up.
    lastReason = await readFailureReason(target);

    if (attempt < SIGN_IN_ATTEMPTS) {
      await target.waitForTimeout(SIGN_IN_BACKOFF_MS[attempt] ?? 30_000);
    }
  }

  throw new HarnessAuthError(
    `Could not sign in as ${identity.role} (${identity.externalSubject}) after ${SIGN_IN_ATTEMPTS} attempts.`,
    `Last failure: ${lastReason}\nurl=${target.url()}\n` +
      `Is the stack up and seeded? docker compose -f tests/cross-layer/docker-compose.cross.yml up -d`,
  );
}

/**
 * Signs in through the API and returns the minted token.
 *
 * For the spec that needs a bearer for a cross-layer assertion. Not the default:
 * see the module header. It never fabricates a token, because a fabricated one
 * would make the auth seam - the thing 040A exists to prove - untested.
 *
 * @param request - A Playwright API request context bound to the stack.
 * @param role - Which seeded role to sign in as.
 * @returns The access token and refresh token.
 */
export async function signInThroughApi(
  request: APIRequestContext,
  role: string,
): Promise<{ readonly accessToken: string; readonly refreshToken: string }> {
  assertNoRealSecrets();
  const identity = identityForRole(role);

  const response = await request.post(`${API_BASE_URL}/api/v1/auth/login`, {
    data: { tenantId: HARNESS_TENANT_ID, externalSubject: identity.externalSubject },
  });

  if (!response.ok()) {
    throw new HarnessAuthError(
      `API sign-in failed for ${identity.role}: ${response.status()}.`,
      (await response.text()).slice(0, 400),
    );
  }

  const body = (await response.json()) as { accessToken?: string; refreshToken?: string };
  if (typeof body.accessToken !== 'string' || body.accessToken.length === 0) {
    throw new HarnessAuthError(
      `API sign-in for ${identity.role} returned no accessToken.`,
      'The response did not match the frozen token envelope.',
    );
  }
  return { accessToken: body.accessToken, refreshToken: body.refreshToken ?? '' };
}

/**
 * Clears the session so the next assertion observes the signed-out state.
 *
 * The tokens live in memory only and the store writes a logout *timestamp* to
 * localStorage, so "sign out" is a UI action rather than a storage edit. Reading
 * the access token out of storage would be reaching past the product's own
 * mechanism, and the token is not there to begin with.
 */
export async function signOut(page: Page): Promise<void> {
  const control = page.getByTestId('nav-sign-out');
  if ((await control.count()) === 0) {
    throw new HarnessAuthError(
      `No sign-out control on ${page.url()}.`,
      'The signed-in shell must expose one; a missing control is a product defect, ' +
        'not a harness problem, so this fails rather than skipping.',
    );
  }
  await control.click();
  await page.getByTestId(LOGIN_PAGE_TEST_ID).waitFor({ state: 'visible', timeout: 30_000 });
}

/**
 * The access token currently held by the store.
 *
 * Read out of the running app rather than stored by the harness: the harness
 * never writes a token, so there is nothing of its own to read. Returns an empty
 * string when the app holds none, which is the honest answer after a sign-out.
 */
export async function readAccessToken(context: BrowserContext): Promise<string> {
  const page = context.pages()[0];
  if (page === undefined) {
    return '';
  }
  const token = await page.evaluate(() => {
    const store = (globalThis as { __dubbingAuthStore?: { getState?: () => { accessToken?: string } } })
      .__dubbingAuthStore;
    return store?.getState?.().accessToken ?? '';
  });
  return typeof token === 'string' ? token : '';
}

/** Reads whatever the login page is showing as the reason for a failure. */
async function readFailureReason(page: Page): Promise<string> {
  for (const testId of ['auth-error', 'auth-error-message', 'page-login']) {
    const node = page.getByTestId(testId);
    if ((await node.count()) > 0) {
      const text = (await node.first().innerText()).trim();
      if (text.length > 0) {
        return `${testId}: ${text.slice(0, 200)}`;
      }
    }
  }
  return 'the shell never appeared and the login page reported no error';
}