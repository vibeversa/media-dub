// Task 040A: seed and reset orchestration for the cross-layer rig.
//
// 040A's edge case list is explicit: "Leftover state from prior run -> reset
// runs before seed, always." `seedCrossLayerEnvironment` therefore always resets
// before seeding; there is no "incremental" path, because an incremental seed is
// exactly how a stale run from a previous session leaks in and makes a seam pass
// for the wrong reason.
//
// The actual row creation lives in tests/cross-layer/seed (a .NET console tool,
// which 040A explicitly permits as an alternative to seed.ts). It is used rather
// than hand-written SQL because it goes through AppDbContext and the domain
// constructors, so a seeded project is valid by the same rules the API enforces.

import { spawn } from 'node:child_process';

import { SEED, SEEDER_CONNECTION_STRING, SEEDER_PROJECT } from './config.js';

export interface SeedResult {
  readonly tenantId: string;
  readonly userId: string;
  readonly projectId: string;
  readonly externalSubject: string;
  readonly projectStatus: string;
}

export class SeedError extends Error {
  readonly output: string;

  constructor(message: string, output: string) {
    super(message);
    this.name = 'SeedError';
    this.output = output;
  }
}

interface SpawnResult {
  readonly code: number | null;
  readonly stdout: string;
  readonly stderr: string;
}

function run(command: string, args: readonly string[]): Promise<SpawnResult> {
  return new Promise((resolve, reject) => {
    // No shell: argument arrays only, so a path with a space or a semicolon in it
    // can never be re-interpreted as syntax.
    const child = spawn(command, [...args], {
      shell: false,
      windowsHide: true,
    });

    let stdout = '';
    let stderr = '';
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      stdout += chunk;
    });
    child.stderr.on('data', (chunk: string) => {
      stderr += chunk;
    });
    child.on('error', reject);
    child.on('close', (code) => resolve({ code, stdout, stderr }));
  });
}

/** Builds the seeder so the first `seed()` does not pay a full compile. */
export async function buildSeeder(): Promise<void> {
  const result = await run('dotnet', ['build', SEEDER_PROJECT, '-v', 'q', '--nologo']);
  if (result.code !== 0) {
    throw new SeedError(
      `Failed to build the cross-layer seeder (${SEEDER_PROJECT}).`,
      `${result.stdout}\n${result.stderr}`,
    );
  }
}

/**
 * Applies migrations, wipes the seeded tenant, and recreates the synthetic
 * tenant/user/project. Always resets first.
 */
export async function seedCrossLayerEnvironment(): Promise<SeedResult> {
  const result = await run('dotnet', [
    'run',
    '--project',
    SEEDER_PROJECT,
    '--no-build',
    '--',
    '--connection',
    SEEDER_CONNECTION_STRING,
    '--tenant',
    SEED.tenantId,
    '--user',
    SEED.userId,
    '--project',
    SEED.projectId,
    '--reset',
  ]);

  if (result.code !== 0) {
    // The seeder's own output is the only diagnosis available: it is the tool
    // that reports the SQLSTATE and the full exception chain. Swallowing it
    // turns every rig failure into "seed failed", which is unactionable.
    const detail = `${result.stdout}\n${result.stderr}`.trim();
    throw new SeedError(
      'Cross-layer seed failed. Is the postgres service healthy and the stack running?\n' +
        `  docker compose -f tests/cross-layer/docker-compose.cross.yml up -d\n\n${detail}`,
      detail,
    );
  }

  // The seeder prints exactly one JSON line on stdout. Take the last non-empty
  // line: MSBuild chatter is not expected with --no-build, but a stray line
  // must not be able to masquerade as the payload.
  const line = result.stdout
    .split(/\r?\n/)
    .map((entry) => entry.trim())
    .filter((entry) => entry.length > 0)
    .at(-1);

  if (line === undefined) {
    throw new SeedError('Cross-layer seed produced no output.', result.stdout);
  }

  let parsed: SeedResult;
  try {
    parsed = JSON.parse(line) as SeedResult;
  } catch {
    throw new SeedError(
      `Cross-layer seed did not emit a JSON payload (got: ${line.slice(0, 200)}).`,
      `${result.stdout}\n${result.stderr}`,
    );
  }

  if (parsed.tenantId !== SEED.tenantId || parsed.userId !== SEED.userId) {
    throw new SeedError(
      'Cross-layer seed returned unexpected identifiers; the rig constants and the ' +
        'seeder output have diverged.',
      line,
    );
  }

  return parsed;
}
