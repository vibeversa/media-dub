// Task 046 instruction 4: the harness smoke.
//
// This file is the proof that the shared harness works, not a test of product
// behaviour. Everything in the frontend suite stands on these three claims:
//
//   R1  the MSW taxonomy covers all eleven documented states and every handler
//       answers with the envelope the wire contract freezes;
//   R3  the synthetic factories build a tenant-isolated graph that a feature
//       suite can hand straight to a component;
//   R4  the fixtures carry no real PII and no secrets.
//
// The 039A conformance suite already asserts the first of those. It is kept
// (`conformance.spec.ts`) rather than absorbed, because 039A's report and the
// coverage-gap tooling reference it by name; this file adds what 046 owns on top:
// the handler *factories* feature suites compose, the fail-closed lifecycle, and
// the fixture isolation the factories promise.
//
// THE TWO-HAND RULE
// -----------------
// Every property is proved twice: once against a synthetic input that MUST fire
// the rule, then against the real tree. That structure is the repository
// convention (043C, 044, 045 each found a gate that passed on nothing) and it is
// the reason the "scrubber finds nothing" assertions below are evidence rather
// than a debt line.

import { afterAll, afterEach, beforeAll, describe, expect, it } from 'vitest';

import {
  assertHandlerExists,
  coveredTaxonomyIds,
  incorrectEnvelopeError,
  missingHandlerError,
  noContentHandler,
  successHandler,
  taxonomyHandler,
  taxonomyHandlers,
} from './handlers.js';
import {
  buildFixtures,
  buildMe,
  fixtureId,
  mockUrl,
  RESERVED_EMAIL_DOMAIN,
  userForRole,
  WORKER_SEEDS,
  type FrontendFixtures,
} from './fixtures.js';
import {
  installMocks,
  mocksInstalled,
  resetMocks,
  taxonomyServer,
  uninstallMocks,
  useMocks,
} from './server.js';
import { MOCK_BASE_URL, TAXONOMY, TAXONOMY_IDS, isErrorEnvelope } from './taxonomy.js';

beforeAll(() => {
  installMocks();
});

afterEach(() => {
  resetMocks();
});

afterAll(() => {
  uninstallMocks();
});

// ===========================================================================
// R1 - taxonomy coverage and envelopes
// ===========================================================================

describe('R1: MSW taxonomy covers every documented outcome', () => {
  it('declares exactly the eleven states the contract names', () => {
    expect(TAXONOMY_IDS).toEqual([
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
    ]);
    expect(TAXONOMY).toHaveLength(11);
  });

  it('registers one handler per entry and covers every id', () => {
    expect(taxonomyHandlers).toHaveLength(TAXONOMY.length);
    // Sorted, because a Set built from a duplicated list would still report every
    // id - the equality is what makes the count meaningful.
    expect([...coveredTaxonomyIds()].sort()).toEqual([...TAXONOMY_IDS].sort());
  });

  for (const entry of TAXONOMY) {
    it(`serves ${entry.id} with the documented envelope`, async () => {
      assertHandlerExists(entry.id);

      let response: Response;
      try {
        response = await fetch(`${MOCK_BASE_URL}${entry.path}`);
      } catch (error) {
        expect.unreachable(missingHandlerError(entry.id, (error as Error).message));
      }

      expect(response.status).toBe(entry.status);
      const body = (await response.json()) as Record<string, unknown>;

      if (entry.code === null) {
        expect(body['error'], entry.id).toBeUndefined();
        expect(body['correlationId']).toBe(`corr-taxonomy-${entry.id}`);
        expect(body['data']).toBeDefined();
        if (entry.id === 'partial') {
          expect(body['partial']).toBe(true);
          expect(Array.isArray(body['warnings'])).toBe(true);
        } else {
          expect(body['partial'], 'only `partial` carries the flag').toBeUndefined();
        }
        return;
      }

      if (!isErrorEnvelope(body)) {
        expect.unreachable(incorrectEnvelopeError(entry.id, JSON.stringify(body)));
      }
      expect(body.error.code).toBe(entry.code);
      expect(body.error.message.length).toBeGreaterThan(0);
      expect(body.error.correlationId).toBe(`corr-taxonomy-${entry.id}`);
      expect(typeof body.error.details).toBe('object');
    });
  }

  it('maps each failure outcome to the status the contract freezes', () => {
    // Written out rather than derived from the entries: a table restating the
    // contract is what makes the entries' own `status` field auditable. If both
    // were generated from one source, changing the source would change both and
    // the test would pass on a wrong status.
    const expected: Record<string, number> = {
      success: 200,
      'unauthorized-401': 401,
      'forbidden-403': 403,
      'not-found-404': 404,
      'conflict-409': 409,
      'rate-limited-429': 429,
      'internal-500': 500,
      validation: 400,
      'provider-error': 502,
      partial: 200,
      'stale-conflict': 409,
    };
    for (const entry of TAXONOMY) {
      expect(entry.status, entry.id).toBe(expected[entry.id]);
    }
  });

  it('refuses to serve a request no handler matched', async () => {
    // The fail-closed default. With MSW's own default ('warn') this request would
    // escape to the real network and the test would continue - which is how a
    // unit suite passes on a laptop with a dev API up and fails in CI with an
    // opaque ECONNREFUSED.
    await expect(fetch(`${MOCK_BASE_URL}/api/v1/nothing-here`)).rejects.toThrow();
  });
});

// ===========================================================================
// Handler factories feature suites compose
// ===========================================================================

describe('handler factories', () => {
  it('serves a taxonomy outcome on a real feature route', async () => {
    installMocks([taxonomyHandler('get', '/api/v1/projects/p1', 'forbidden-403')]);

    const response = await fetch(mockUrl('/api/v1/projects/p1'));
    expect(response.status).toBe(403);
    const body = (await response.json()) as unknown;
    expect(isErrorEnvelope(body)).toBe(true);
    expect((body as { error: { code: string } }).error.code).toBe('FORBIDDEN');
  });

  it('wraps a caller-supplied success body in the shared envelope', async () => {
    const fixtures = buildFixtures();
    installMocks([successHandler('get', '/api/v1/projects', { items: [fixtures.project], page: 1 })]);

    const response = await fetch(mockUrl('/api/v1/projects'));
    expect(response.status).toBe(200);
    const body = (await response.json()) as { data: { items: unknown[] }; correlationId: string };
    expect(body.data.items).toHaveLength(1);
    expect(body.correlationId).toBeTruthy();
  });

  it('serves a 204 with no body rather than a JSON undefined', async () => {
    installMocks([noContentHandler('post', '/api/v1/projects/p1/archive')]);

    const response = await fetch(mockUrl('/api/v1/projects/p1/archive'), { method: 'POST' });
    expect(response.status).toBe(204);
    // A 204 must not carry a body. `HttpResponse.json(undefined)` would produce
    // the literal string "undefined" with a JSON content type, which is not what
    // the API sends and what a suite asserting on the body would then pin.
    expect(await response.text()).toBe('');
    expect(response.headers.get('content-type')).toBeNull();
  });

  it('lets a per-test override replace a default handler and then resets', async () => {
    installMocks([successHandler('get', '/api/v1/projects/p1', { name: 'original' })]);

    const read = async (): Promise<string> => {
      const response = await fetch(mockUrl('/api/v1/projects/p1'));
      const body = (await response.json()) as { data: { name: string } };
      return body.data.name;
    };

    expect(await read()).toBe('original');

    useMocks(successHandler('get', '/api/v1/projects/p1', { name: 'overridden' }));
    expect(await read()).toBe('overridden');

    // The contract `resetMocks()` exists for. Two things are being proved, and
    // the second one is the one that was broken first:
    //   1. a per-test override is gone afterwards, so the next test cannot pass
    //      with this test's response ("passed with the previous test's response"
    //      reads as a flake and is neither reproducible nor a flake);
    //   2. the suite's OWN handlers survive the reset - MSW's `resetHandlers()`
    //      restores only the list handed to `setupServer(...)`, so a bare call
    //      silently dropped them and the next request failed with "Cannot bypass
    //      a request when using the 'error' strategy", which points at MSW's
    //      configuration rather than at the harness.
    resetMocks();
    expect(await read()).toBe('original');

    // And once more, because a single round-trip through the lifecycle would not
    // distinguish "restores the base set" from "happens to work once".
    useMocks(successHandler('get', '/api/v1/projects/p1', { name: 'second-override' }));
    expect(await read()).toBe('second-override');
    resetMocks();
    expect(await read()).toBe('original');
  });

  it('names the missing taxonomy entry rather than reporting a network error', () => {
    expect(missingHandlerError('partial')).toContain('MSW_HANDLER_MISSING:partial');
    expect(missingHandlerError('partial')).toContain('taxonomy.ts');
    expect(incorrectEnvelopeError('conflict-409')).toContain('MSW_ENVELOPE_INCORRECT:conflict-409');

    expect(() =>
      assertHandlerExists('not-a-real-outcome' as (typeof TAXONOMY_IDS)[number]),
    ).toThrow(/MSW_HANDLER_MISSING:not-a-real-outcome/);
  });

  it('tracks installation so a suite can assert its own lifecycle', () => {
    expect(mocksInstalled()).toBe(true);
    expect(taxonomyServer).toBeDefined();
  });
});

// ===========================================================================
// R3 - synthetic factories build tenant-isolated graphs
// ===========================================================================

describe('R3: synthetic factories build isolated, deterministic graphs', () => {
  it('builds the same graph twice for the same seed', () => {
    const first = buildFixtures();
    const second = buildFixtures();

    expect(second.tenant.tenantId).toBe(first.tenant.tenantId);
    expect(second.project.projectId).toBe(first.project.projectId);
    expect(second.segment.segmentId).toBe(first.segment.segmentId);
    expect(second.reviewItem.reviewItemId).toBe(first.reviewItem.reviewItemId);
  });

  it('builds disjoint graphs for different worker seeds', () => {
    // Task 046's edge case: "Parallel workers share tenant -> factories issue
    // isolated tenant per worker (no cross-test leakage)". Asserted, not assumed.
    const first = buildFixtures(WORKER_SEEDS[0] as string);
    const second = buildFixtures(WORKER_SEEDS[1] as string);

    expect(second.tenant.tenantId).not.toBe(first.tenant.tenantId);
    expect(second.tenant.slug).not.toBe(first.tenant.slug);
    expect(second.project.projectId).not.toBe(first.project.projectId);
    expect(second.run.runId).not.toBe(first.run.runId);
    expect(second.segment.segmentId).not.toBe(first.segment.segmentId);
    expect(second.reviewItem.reviewItemId).not.toBe(first.reviewItem.reviewItemId);

    const firstIds = collectIds(first);
    const secondIds = collectIds(second);
    expect(firstIds.filter((id) => secondIds.includes(id))).toEqual([]);
  });

  it('wires every foreign key the feature screens read', () => {
    const fixtures = buildFixtures();

    expect(fixtures.project.tenantId).toBe(fixtures.tenant.tenantId);
    expect(fixtures.run.projectId).toBe(fixtures.project.projectId);
    expect(fixtures.segment.projectId).toBe(fixtures.project.projectId);
    expect(fixtures.segment.runId).toBe(fixtures.run.runId);
    expect(fixtures.reviewItem.projectId).toBe(fixtures.project.projectId);
    expect(fixtures.reviewItem.segmentId).toBe(fixtures.segment.segmentId);
    expect(fixtures.segment.durationMs).toBe(fixtures.segment.endMs - fixtures.segment.startMs);
    expect(fixtures.users.every((user) => user.tenantId === fixtures.tenant.tenantId)).toBe(true);
  });

  it('seeds one user per role and refuses an unseeded one', () => {
    const fixtures = buildFixtures();

    expect(fixtures.users.map((user) => user.externalSubject)).toEqual([
      'harness-admin',
      'harness-owner',
      'harness-editor',
      'harness-reviewer',
      'harness-viewer',
    ]);
    expect(userForRole(fixtures, 'harness-reviewer').displayName).toBe('Harness Reviewer');
    expect(() => userForRole(fixtures, 'nobody')).toThrow(/No synthetic user/);
  });

  it('starts the selection version at 1 so a stale writer has something to hold', () => {
    // Mirrors `SyntheticProjects.InitialSelectionVersion`. At 0 the first edit
    // bumps it to 1 and there is no older version for a second writer to be stale
    // against, so a stale-edit test would have nothing to test.
    expect(buildFixtures().segment.selectionVersion).toBe(1);
  });

  it('builds a /me document for each role', () => {
    const fixtures = buildFixtures();
    for (const subject of ['harness-admin', 'harness-owner', 'harness-viewer']) {
      const me = buildMe(fixtures, subject);
      expect(me.userId).toBe(userForRole(fixtures, subject).userId);
      expect(me.tenantId).toBe(fixtures.tenant.tenantId);
      expect(me.roles.length).toBeGreaterThan(0);
    }
    expect(buildMe(fixtures, 'harness-owner').roles).toEqual(['owner']);
  });

  it('scopes a derived id by its scope so two labels cannot share a primary key', () => {
    expect(fixtureId('seed', 'run', 'project-a')).not.toBe(fixtureId('seed', 'run', 'project-b'));
    expect(fixtureId('seed', 'run', 'project-a')).toBe(fixtureId('seed', 'run', 'project-a'));
    expect(fixtureId('seed-a', 'run')).not.toBe(fixtureId('seed-b', 'run'));
  });
});

// ===========================================================================
// R4 - synthetic PII only
// ===========================================================================

describe('R4: fixtures carry synthetic PII only', () => {
  // The frontend half of the scrubber. `PiiScrubber` (backend) owns the rules and
  // the masking; this asserts the same shapes against the values a component test
  // can actually put on screen, because the backend scrubber never sees them.

  const EMAIL = /[A-Za-z0-9._%+-]+@([A-Za-z0-9.-]+\.[A-Za-z]{2,})/g;
  const JWT = /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/;
  const AWS_KEY = /\b(?:AKIA|ASIA|AIDA|AROA)[0-9A-Z]{16}\b/;
  const E164 = /(?<![\w-])\+\d{10,15}(?![\w-])/;

  function textValues(fixtures: FrontendFixtures): string[] {
    return [
      ...Object.values(fixtures.tenant).map(String),
      ...fixtures.users.flatMap((user) => Object.values(user).map(String)),
      ...Object.values(fixtures.project).map(String),
      ...Object.values(fixtures.run).map(String),
      ...Object.values(fixtures.segment).map(String),
      ...Object.values(fixtures.reviewItem).map(String),
    ];
  }

  it('puts every address under the RFC 2606 reserved domain', () => {
    const fixtures = buildFixtures();

    for (const value of textValues(fixtures)) {
      for (const match of value.matchAll(EMAIL)) {
        expect(match[1], `non-reserved domain in ${value}`).toMatch(
          /\.(invalid|test|localhost|example)$/i,
        );
      }
    }

    expect(RESERVED_EMAIL_DOMAIN.endsWith('.invalid')).toBe(true);
  });

  it('carries no secret shape anywhere in the fixture text', () => {
    const fixtures = buildFixtures();
    for (const value of textValues(fixtures)) {
      expect(JWT.test(value), `JWT-shaped value: ${value}`).toBe(false);
      expect(AWS_KEY.test(value), `AWS key-shaped value: ${value}`).toBe(false);
      expect(E164.test(value), `phone-shaped value: ${value}`).toBe(false);
    }
  });

  it('has no mock handler body carrying a token or a credential', async () => {
    // The control for the rule above. If the pattern had never matched anything,
    // "no findings" would be evidence of nothing - which is the 045 Finding 1
    // failure mode, so the positive case is asserted first.
    expect(JWT.test('eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk')).toBe(true);
    expect(AWS_KEY.test('AKIAIOSFODNN7EXAMPLE')).toBe(true);
    expect(E164.test('+14155552671')).toBe(true);

    for (const entry of TAXONOMY) {
      const serialised = JSON.stringify(entry.body);
      expect(JWT.test(serialised), `${entry.id} body`).toBe(false);
      expect(AWS_KEY.test(serialised), `${entry.id} body`).toBe(false);
      expect(/password|secret|token=/i.test(serialised), `${entry.id} body`).toBe(false);
    }
  });
});

// ===========================================================================

function collectIds(fixtures: FrontendFixtures): string[] {
  return [
    fixtures.tenant.tenantId,
    fixtures.project.projectId,
    fixtures.run.runId,
    fixtures.segment.segmentId,
    fixtures.reviewItem.reviewItemId,
    ...fixtures.users.map((user) => user.userId),
  ];
}