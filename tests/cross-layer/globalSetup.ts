// Task 040A: Playwright global setup for the cross-layer rig.
//
// Everything expensive and everything that must happen exactly once per run
// lives here: the preflight, the seeder build, the frontend bundle, and the
// reset-then-seed. Specs then start from a known-good environment, which is what
// makes R3 (deterministic) achievable without a per-spec boot.
//
// Fail-closed throughout: 040A states that a failing harness blocks 040B, so a
// setup failure aborts the run instead of letting specs execute against a rig
// that was never really up.

import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import path from 'node:path';

import {
  API_BASE_URL,
  ApiClient,
  COMPOSE_FILE,
  FRONTEND_BASE_URL,
  REQUIRED_RUNNING_SERVICES,
  REQUIRED_SERVICES,
  SEED,
  assertRigPortsOpen,
  buildSeeder,
  environmentSnapshotPath,
  formatServiceMatrix,
  isPortListening,
  seedCrossLayerEnvironment,
  writeEnvironmentSnapshot,
} from './harness/index.js';

// Absolute repository root, discovered by walking up from the working
// directory. `import.meta.url` is not used: Playwright transpiles this file and
// may relocate it, and `process.cwd()` is what Playwright actually sets. The
// walk looks for two markers so it cannot latch onto a parent checkout.
const REPO_ROOT = (() => {
  let current = process.cwd();
  for (let depth = 0; depth < 10; depth += 1) {
    if (
      existsSync(path.join(current, 'playwright.config.ts')) &&
      existsSync(path.join(current, 'tests', 'cross-layer'))
    ) {
      return current;
    }
    const parent = path.dirname(current);
    if (parent === current) {
      break;
    }
    current = parent;
  }
  throw new Error(
    `Could not locate the repository root from ${process.cwd()}. ` +
      'Expected a directory containing playwright.config.ts and tests/cross-layer.',
  );
})();

function run(
  command: string,
  args: readonly string[],
  cwd: string,
): Promise<{ code: number | null; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    // Argument arrays only, never a shell string: a path containing `&` or a
    // space must not be able to change the command.
    const child = spawn(command, [...args], { cwd, shell: false, windowsHide: true });
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

// Why the Node CLIs are invoked directly rather than through `npm run`:
// `spawn(..., { shell: false })` will not find `npm` on Windows, where it ships
// as `npm.cmd`, and since the Node fix for CVE-2024-27980 a `.cmd` target is
// rejected outright (`EINVAL`) unless a shell is used. Reaching for `shell: true`
// would re-open argument injection on paths containing `&` or spaces, so the rig
// calls `tsc` and `vite` through `process.execPath` with argument arrays.
// `dotnet` and `docker` are real `.exe` files on both platforms and spawn fine.
const FRONTEND_DIR = path.join(REPO_ROOT, 'frontend');

/** Absolute path to a script inside `frontend/node_modules`. */
function frontendBin(...segments: string[]): string {
  return path.join(FRONTEND_DIR, 'node_modules', ...segments);
}

async function listeningPorts(): Promise<Set<number>> {
  const found = new Set<number>();
  for (const service of REQUIRED_SERVICES) {
    if (await isPortListening(service.port)) {
      found.add(service.port);
    }
  }
  return found;
}

/**
 * Builds `frontend/dist` for the cross-layer profile.
 *
 * Vite inlines `VITE_*` at build time, so the bundle cannot be shared with the
 * default dev profile (which points at :5000). `--mode cross-layer` loads
 * `frontend/.env.cross-layer`, which must agree with the `api` port mapping in
 * the compose file.
 *
 * The steps mirror `frontend`'s own `build` script (`typecheck && vite build`)
 * plus its `prebuild` drift check, invoked as Node CLIs so no shell is involved.
 */
async function buildFrontend(): Promise<void> {
  if (process.env['CROSS_LAYER_SKIP_FRONTEND_BUILD'] === '1') {
    process.stdout.write('cross-layer: skipping frontend build (CROSS_LAYER_SKIP_FRONTEND_BUILD=1)\n');
    return;
  }

  const steps: ReadonlyArray<{ label: string; script: string; args: string[] }> = [
    {
      label: 'check-api-drift',
      script: path.join(REPO_ROOT, 'tools', 'check-api-drift.mjs'),
      args: [],
    },
    {
      label: 'typecheck',
      script: frontendBin('typescript', 'bin', 'tsc'),
      args: ['--noEmit', '-p', 'tsconfig.json'],
    },
    {
      label: 'vite-build',
      script: frontendBin('vite', 'bin', 'vite.js'),
      args: ['build', '--mode', 'cross-layer'],
    },
  ];

  for (const step of steps) {
    process.stdout.write(`cross-layer: frontend ${step.label}\n`);
    // path.join, never `${REPO_ROOT}frontend`: string concatenation silently
    // produced `...media-dubfrontend`, and Node reports a missing working
    // directory as `spawn <command> ENOENT`, which points at the command rather
    // than at the directory.
    const result = await run(process.execPath, [step.script, ...step.args], FRONTEND_DIR);
    if (result.code !== 0) {
      throw new Error(
        `Cross-layer frontend ${step.label} failed:\n` +
          `${result.stdout}\n${result.stderr}`.slice(-4000),
      );
    }
  }
}

/**
 * Waits for the frontend to actually serve the freshly built bundle.
 *
 * The container serves a bind mount of `frontend/dist`, and the build above
 * rewrites that directory in place. Listening on the port is not enough: the
 * first request after a rebuild can still race the write. Polling the root
 * document until it returns 200 is a readiness check on the thing the smoke
 * actually depends on, not a sleep.
 */
async function waitForFrontend(timeoutMs = 60_000): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastStatus = 'no response';

  while (Date.now() < deadline) {
    try {
      // FRONTEND_BASE_URL is 127.0.0.1, not localhost: Docker Desktop resets
      // IPv6 connections to published ports, so the localhost form fails here
      // even when the service is healthy.
      const response = await fetch(FRONTEND_BASE_URL, {
        signal: AbortSignal.timeout(5000),
      });
      lastStatus = String(response.status);
      await response.text();
      if (response.ok) {
        return;
      }
    } catch (error) {
      lastStatus = (error as Error).message;
    }
    await new Promise((resolve) => setTimeout(resolve, 500));
  }

  throw new Error(
    `The frontend never served a 200 on ${FRONTEND_BASE_URL} within ${timeoutMs}ms ` +
      `(last: ${lastStatus}). If the container exited, its logs name the reason; the ` +
      'usual cause is a missing or half-written frontend/dist.',
  );
}

/**
 * Asserts every rig service is actually *running*, not merely listening.
 *
 * 040A's edge cases name this directly: "Partial boot (DB up, broker down) ->
 * fail-closed with service matrix, never half-run specs." Ports cannot detect a
 * stopped worker (they expose none) and cannot detect a broker that is up but
 * not consuming - which is the failure that leaves a run stuck in `Pending` and
 * makes the rig look healthy while proving nothing.
 */
async function assertServicesRunning(): Promise<void> {
  const result = await run('docker', [
    'compose',
    '-f',
    COMPOSE_FILE,
    // `--all`: without it compose omits stopped containers entirely, so a
    // stopped worker is indistinguishable from one that was never created - and
    // the matrix is the only diagnostic a partial boot gets.
    'ps',
    '--all',
    '--format',
    '{{.Service}}\t{{.State}}\t{{.Status}}',
  ]);

  if (result.code !== 0) {
    throw new Error(
      `Could not read the rig's service matrix from docker compose:\n${result.stderr}`.trim(),
    );
  }

  const states = new Map<string, string>();
  for (const line of result.stdout.split(/\r?\n/)) {
    const [service, state, ...rest] = line.split('\t');
    if (service !== undefined && service.trim().length > 0) {
      states.set(service.trim(), `${state ?? 'unknown'} (${rest.join('\t').trim()})`);
    }
  }

  const notRunning = REQUIRED_RUNNING_SERVICES.filter(
    (service) => (states.get(service) ?? '').startsWith('running') === false,
  );

  if (notRunning.length > 0) {
    throw new Error(
      `Cross-layer rig is partially booted: ${notRunning.join(', ')} not running.\n` +
        'A half-booted rig must not run seam specs, so this fails closed.\n' +
        'Service matrix:\n' +
        `${formatServiceMatrix(states)}\n` +
        `Bring the rig up with: docker compose -f ${COMPOSE_FILE} up -d`,
    );
  }

  process.stdout.write('cross-layer: service matrix\n' + `${formatServiceMatrix(states)}\n`);
}

async function main(): Promise<void> {
  // The frontend container serves a bind-mounted dist, so the bundle has to
  // exist before the spec asserts the page renders.
  await buildFrontend();
  await waitForFrontend();

  await assertRigPortsOpen(await listeningPorts(), { requireFrontend: true });
  await assertServicesRunning();

  const api = new ApiClient();
  if (!(await api.isAlive())) {
    throw new Error(
      `The API answered nothing on /health/live. Start the rig:\n` +
        `  docker compose -f ${COMPOSE_FILE} up -d`,
    );
  }

  // Reset always precedes seed (040A edge case: leftover state from a prior run).
  await buildSeeder();
  const seeded = await seedCrossLayerEnvironment();

  // Handed to the 040B seam specs. Without this a seam would have to search for
  // "a" review item or "a" voice profile, which silently retargets the seam
  // whenever the fixture set changes.
  const environment = writeEnvironmentSnapshot(seeded);

  process.stdout.write(
    `cross-layer: seeded tenant=${environment.tenantId} project=${environment.projectId} ` +
      `status=${environment.projectStatus}\n` +
      `cross-layer: snapshot ${environmentSnapshotPath()}\n` +
      `cross-layer: api=${API_BASE_URL} frontend=${FRONTEND_BASE_URL}\n` +
      `cross-layer: identity subject=${SEED.externalSubject}\n`,
  );
}

export default async function globalSetup(): Promise<void> {
  await main();
}
