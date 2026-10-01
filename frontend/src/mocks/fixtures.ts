// Task 046: synthetic frontend fixtures.
//
// THE MIRRORING RULE
// ------------------
// Every value here that the backend also knows (languages, provider names,
// texts, statuses, envelope shapes) is a *hand-transcribed* counterpart of
// `tests/DubbingPlatform.TestFixtures`. That is a deliberate duplication with a
// named cost, and the cost is paid on purpose: the frontend suite runs in jsdom
// with no API and no database, so it cannot call the C# factories. The
// alternative - generating this file from the C# one - would put a .NET build
// in the path of `npm run test`, which is the coupling this repository has
// spent several tasks removing.
//
// What makes the duplication survivable is that both sides are asserted against
// the same *shape* in their own layer: `handlers.spec.ts` proves these bodies
// satisfy the taxonomy contract and the generated client's response types, and
// `TestFixturesTests` proves the C# graph is referentially sound. A field added
// to one side and not the other fails a test in the layer that gained the field.
//
// SYNTHETIC DATA ONLY
// -------------------
// Every email sits under an RFC 2606 reserved domain and every id is derived,
// never random. The PII scrubber on the backend side (`PiiScrubber`) has the same
// list of rules; these fixtures are written to pass it, and
// `handlers.spec.ts` runs the frontend-visible fields through a matching check.

import { MOCK_BASE_URL } from './taxonomy.js';

/**
 * RFC 2606 reserved domain. Same constant as `SyntheticTenants.ReservedEmailDomain`
 * on the backend side.
 */
export const RESERVED_EMAIL_DOMAIN = 'fixtures.invalid';

/** Source language of every synthetic project. Mirrors `SyntheticProjects`. */
export const SOURCE_LANGUAGE = 'en';

/** Target language of every synthetic project. Mirrors `SyntheticProjects`. */
export const TARGET_LANGUAGE = 'es';

/**
 * The provider name on synthetic versions.
 *
 * `mock`, never a real provider name: a fixture naming a real one would let a
 * test pass while asserting a real provider was called.
 */
export const PROVIDER_NAME = 'mock';

/**
 * Frozen instant every fixture is stamped with.
 *
 * Mirrors `FixtureClock.Now` on the backend. Fixed rather than `Date.now()`
 * because a snapshot or a formatted-date assertion that depends on the day the
 * suite runs is a test that fails on a schedule nobody can explain.
 */
export const FIXTURE_INSTANT = '2026-01-15T12:00:00.000Z';

/** Tenant id used when a fixture is built without an explicit one. */
export const DEFAULT_TENANT_ID = '11111111-1111-1111-1111-111111111111';

/** Per-worker seeds Playwright workers pass so two workers never collide. */
export const WORKER_SEEDS: readonly string[] = [
  '22222222-2222-2222-2222-222222222222',
  '33333333-3333-3333-3333-333333333333',
  '44444444-4444-4444-4444-444444444444',
];

/**
 * A deterministic identifier derived from a seed, an optional scope, and a label.
 *
 * The frontend counterpart of `FixtureIds.Derive`. It is a *readable* derivation
 * rather than a hash: the point is not collision resistance across processes, it
 * is that the same (seed, scope, label) always produces the same id so a
 * screenshot or a snapshot is stable. Hashing in the browser for that would add
 * an async call to every fixture build for no benefit.
 *
 * The `scope` argument exists because two fixtures that share a label but not a
 * scope must not share an id - a project and its run, for instance. It mirrors
 * the backend derivation, where the same mistake produced two rows sharing a
 * primary key.
 *
 * @param seed - Tenant or worker seed.
 * @param label - Stable, human-readable label.
 * @param scope - Optional namespace, typically a project id.
 * @returns A UUID-shaped string. Not RFC 4122 version 4; deliberately stable.
 */
export function fixtureId(seed: string, label: string, scope?: string): string {
  const material = `${seed}:${scope ?? '-'}:${label}`;
  let hash = 0x811c9dc5;
  for (let index = 0; index < material.length; index += 1) {
    hash ^= material.charCodeAt(index);
    // FNV-1a, 32-bit. `Math.imul` keeps the multiply in 32-bit space; without
    // it the accumulator leaves the range and the result depends on the engine's
    // double precision.
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  const hex = hash.toString(16).padStart(8, '0').repeat(4);
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-4${hex.slice(13, 16)}-8${hex.slice(17, 20)}-${hex.slice(20, 32)}`;
}

/** A synthetic tenant. */
export interface TenantFixture {
  readonly tenantId: string;
  readonly name: string;
  readonly slug: string;
  readonly createdAt: string;
}

/** A synthetic user, in the shape `/me` and the login form use. */
export interface UserFixture {
  readonly userId: string;
  readonly tenantId: string;
  readonly externalSubject: string;
  readonly email: string;
  readonly displayName: string;
  readonly status: 'Active' | 'Disabled';
}

/** A synthetic project summary, in the shape the project list renders. */
export interface ProjectFixture {
  readonly projectId: string;
  readonly tenantId: string;
  readonly name: string;
  readonly description: string;
  readonly sourceLanguage: string;
  readonly targetLanguage: string;
  readonly status: string;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly isArchived: boolean;
}

/** A synthetic processing run. */
export interface RunFixture {
  readonly runId: string;
  readonly tenantId: string;
  readonly projectId: string;
  readonly attempt: number;
  readonly status: string;
  readonly pipelineVersion: string;
  readonly percentApproximate: number;
  readonly createdAt: string;
  readonly updatedAt: string;
}

/** A synthetic speech segment with its selected versions. */
export interface SegmentFixture {
  readonly segmentId: string;
  readonly tenantId: string;
  readonly projectId: string;
  readonly runId: string;
  readonly sequence: number;
  readonly startMs: number;
  readonly endMs: number;
  readonly durationMs: number;
  readonly status: string;
  readonly speakerKey: string | null;
  readonly transcriptText: string;
  readonly translationText: string;
  readonly confidence: number;
  readonly selectionVersion: number;
  readonly needsReview: boolean;
}

/** A synthetic review item. */
export interface ReviewItemFixture {
  readonly reviewItemId: string;
  readonly tenantId: string;
  readonly projectId: string;
  readonly runId: string;
  readonly segmentId: string;
  readonly status: string;
  readonly reason: string;
  readonly version: number;
  readonly createdAt: string;
}

/** The whole synthetic environment for one worker. */
export interface FrontendFixtures {
  readonly seed: string;
  readonly tenant: TenantFixture;
  readonly users: readonly UserFixture[];
  readonly project: ProjectFixture;
  readonly run: RunFixture;
  readonly segment: SegmentFixture;
  readonly reviewItem: ReviewItemFixture;
}

/**
 * Builds the standard synthetic environment for one worker.
 *
 * The mirror of `SyntheticEnvironments.BuildForWorker`, down to the same two
 * projects and the same roles. Feature specs that need "a dashboard with one
 * project" call this and get ids that match what the seeded backend produced,
 * because both sides derive from the same seed and label strings.
 *
 * @param seed - Worker seed. Defaults to `DEFAULT_TENANT_ID`.
 * @returns The environment.
 */
export function buildFixtures(seed: string = DEFAULT_TENANT_ID): FrontendFixtures {
  const tenantId = fixtureId(seed, 'tenant');
  const projectId = fixtureId(seed, 'project');
  const runId = fixtureId(seed, 'run/anchor', projectId);
  const segmentId = fixtureId(seed, 'segment', projectId);

  const users: readonly UserFixture[] = [
    { subject: 'harness-admin', displayName: 'Harness Administrator' },
    { subject: 'harness-owner', displayName: 'Harness Owner' },
    { subject: 'harness-editor', displayName: 'Harness Editor' },
    { subject: 'harness-reviewer', displayName: 'Harness Reviewer' },
    { subject: 'harness-viewer', displayName: 'Harness Viewer' },
  ].map(({ subject, displayName }) => ({
    userId: fixtureId(seed, `user/${subject}`),
    tenantId,
    externalSubject: subject,
    email: `synthetic+${subject}@${RESERVED_EMAIL_DOMAIN}`,
    displayName,
    status: 'Active' as const,
  }));

  return {
    seed,
    tenant: {
      tenantId,
      name: `Harness tenant ${tenantId.slice(0, 8)}`,
      slug: `harness-${tenantId.slice(0, 8)}`,
      createdAt: FIXTURE_INSTANT,
    },
    users,
    project: {
      projectId,
      tenantId,
      name: 'Harness Anchor',
      description: 'Synthetic harness project.',
      sourceLanguage: SOURCE_LANGUAGE,
      targetLanguage: TARGET_LANGUAGE,
      status: 'MediaReady',
      createdAt: FIXTURE_INSTANT,
      updatedAt: FIXTURE_INSTANT,
      isArchived: false,
    },
    run: {
      runId,
      tenantId,
      projectId,
      attempt: 1,
      status: 'Completed',
      pipelineVersion: '1.0.0',
      percentApproximate: 100,
      createdAt: FIXTURE_INSTANT,
      updatedAt: FIXTURE_INSTANT,
    },
    segment: {
      segmentId,
      tenantId,
      projectId,
      runId,
      sequence: 1,
      startMs: 0,
      endMs: 2000,
      durationMs: 2000,
      status: 'Ready',
      speakerKey: 'spk-1',
      transcriptText: 'synthetic transcript segment',
      translationText: 'synthetic translation segment',
      confidence: 0.95,
      // 1, not 0: mirrors `SyntheticProjects.InitialSelectionVersion`, and it is
      // what a stale-edit test needs - with 0 there is no older version for a
      // second writer to be stale against.
      selectionVersion: 1,
      needsReview: false,
    },
    reviewItem: {
      reviewItemId: fixtureId(seed, 'review-item', projectId),
      tenantId,
      projectId,
      runId,
      segmentId,
      status: 'Open',
      reason: 'low-confidence',
      version: 1,
      createdAt: FIXTURE_INSTANT,
    },
  };
}

/** The signed-in user for a role. Throws for an unseeded role. */
export function userForRole(fixtures: FrontendFixtures, subject: string): UserFixture {
  const found = fixtures.users.find((user) => user.externalSubject === subject);
  if (found === undefined) {
    throw new Error(
      `No synthetic user with subject '${subject}'. Seeded: ${fixtures.users
        .map((user) => user.externalSubject)
        .join(', ')}`,
    );
  }
  return found;
}

/** The `/me` document the shell reads. */
export interface MeFixture {
  readonly userId: string;
  readonly tenantId: string;
  readonly permissions: readonly string[];
  readonly roles: readonly string[];
  readonly locale: string;
  readonly featureFlags: Readonly<Record<string, boolean>>;
}

/**
 * Builds a `/me` document.
 *
 * @param fixtures - The environment.
 * @param subject - Which seeded user is signing in.
 * @param roles - Roles to assert. Defaults to the subject's natural role.
 */
export function buildMe(
  fixtures: FrontendFixtures,
  subject = 'harness-owner',
  roles: readonly string[] = [subject.replace('harness-', '')],
): MeFixture {
  const user = userForRole(fixtures, subject);
  return {
    userId: user.userId,
    tenantId: fixtures.tenant.tenantId,
    permissions: [],
    roles,
    // `en` rather than the user's stored preference: a locale fixture belongs to
    // the i18n suites (045), and a component suite that reads this one should not
    // have its assertions depend on a translation it did not ask for.
    locale: 'en',
    featureFlags: {},
  };
}

/** An absolute mock URL for a path. */
export function mockUrl(path: string): string {
  return `${MOCK_BASE_URL}${path.startsWith('/') ? path : `/${path}`}`;
}