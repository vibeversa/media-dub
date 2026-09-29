// Task 040B: the seeded environment, shared by all seven seam specs.
//
// 040A's `globalSetup` runs the seeder once per Playwright run and prints the
// result as one JSON line. A seam spec needs those ids (which segment, which
// review item, which voice profile) and re-querying for "a" row of each kind
// would silently retarget a seam if the fixture set ever changed. So the seed
// result is handed to the workers through a file rather than re-derived.
//
// Fail-closed: a missing or unreadable snapshot aborts with a message that names
// the fix, never an empty object that would make every fixture id `undefined`.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';

import { SEED } from './config.js';
import type { SeedResult } from './seed.js';

export interface SeamFixtures {
  readonly completedRunId: string;
  readonly segmentId: string;
  readonly transcriptVersionId: string;
  readonly translationVersionId: string;
  readonly reviewItemId: string;
  readonly speakerId: string;
  readonly voiceProfileAId: string;
  readonly voiceProfileBId: string;
  readonly notificationId: string;
  readonly exportJobId: string;
  readonly exportArtifactContentKey: string;
}

export interface CrossLayerEnvironment extends SeedResult {
  readonly fixtures: SeamFixtures;
}

/** Written by `globalSetup`, read by every seam spec. */
const SNAPSHOT_RELATIVE_PATH = path.join(
  'tests',
  'cross-layer',
  '.artifacts',
  'seed-environment.json',
);

function repositoryRoot(): string {
  let current = process.cwd();
  for (let depth = 0; depth < 10; depth += 1) {
    if (existsSync(path.join(current, 'playwright.config.ts'))) {
      return current;
    }
    const parent = path.dirname(current);
    if (parent === current) {
      break;
    }
    current = parent;
  }
  throw new Error(
    `Could not locate the repository root from ${process.cwd()}; expected a directory ` +
      'containing playwright.config.ts.',
  );
}

export function environmentSnapshotPath(): string {
  return path.join(repositoryRoot(), SNAPSHOT_RELATIVE_PATH);
}

/** Called once by `globalSetup` immediately after the seeder succeeds. */
export function writeEnvironmentSnapshot(result: SeedResult): CrossLayerEnvironment {
  const fixtures = (result as { fixtures?: SeamFixtures }).fixtures;
  if (fixtures === undefined) {
    throw new Error(
      'The seeder returned no seam fixtures. Rebuild the seeder: `dotnet build ' +
        'tests/cross-layer/seed/CrossLayerSeed.csproj`. The 040B seams address exact ' +
        'fixture rows and cannot fall back to searching for a row of some kind.',
    );
  }

  const environment: CrossLayerEnvironment = { ...result, fixtures };
  const target = environmentSnapshotPath();
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, `${JSON.stringify(environment, null, 2)}\n`, 'utf8');
  return environment;
}

let cached: CrossLayerEnvironment | undefined;

export function readEnvironment(): CrossLayerEnvironment {
  if (cached !== undefined) {
    return cached;
  }

  const source = environmentSnapshotPath();
  if (!existsSync(source)) {
    throw new Error(
      `No seed environment snapshot at ${source}. The Playwright globalSetup did not run, ` +
        'which means the rig was never seeded. Run via `npx playwright test` so ' +
        'globalSetup executes; do not invoke a spec file directly.',
    );
  }

  let parsed: CrossLayerEnvironment;
  try {
    parsed = JSON.parse(readFileSync(source, 'utf8')) as CrossLayerEnvironment;
  } catch (error) {
    throw new Error(`Seed environment snapshot at ${source} is not valid JSON: ${(error as Error).message}`);
  }

  if (parsed.tenantId !== SEED.tenantId || parsed.projectId !== SEED.projectId) {
    throw new Error(
      'The seed environment snapshot does not match the rig constants. The snapshot is ' +
        'stale from an earlier run, or the seeder and harness/config.ts have diverged.',
    );
  }

  cached = parsed;
  return parsed;
}
