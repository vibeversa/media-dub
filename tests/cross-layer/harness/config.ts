// Task 040A: cross-layer rig configuration and fail-closed preflight.
//
// Two rules drive this module.
//
//  1. Ports are fixed and disjoint from the root docker-compose.yml stack, so a
//     running dev environment cannot silently share state with the rig.
//  2. A collision is a hard error naming the holder. 040A's edge case list is
//     explicit: "Port collision -> fixed ports from 046 with collision error
//     naming the holder, not silent skip." A rig that quietly skips is worse
//     than no rig, because 040B then reports green seams that never ran.

import { createConnection } from 'node:net';

export interface CrossLayerPorts {
  readonly postgres: number;
  readonly objectStorageApi: number;
  readonly objectStorageConsole: number;
  readonly api: number;
  readonly frontend: number;
  readonly brokerAmqp: number;
  readonly brokerManagement: number;
}

/**
 * Host ports for the rig. Deliberately disjoint from the root stack's
 * 5432/5672/6379/8080/9000 so both can run at once.
 */
export const PORTS: CrossLayerPorts = {
  postgres: 55432,
  objectStorageApi: 59000,
  objectStorageConsole: 59001,
  api: 58080,
  frontend: 54173,
  brokerAmqp: 55672,
  brokerManagement: 55673,
};

/**
 * Every rig URL uses `127.0.0.1`, and the spelling is load-bearing.
 *
 * Docker Desktop completes an IPv6 TCP handshake to a published port and then
 * resets the connection, while both Chromium and Node resolve `localhost` to
 * `::1` first. A rig URL written as `localhost` therefore fails against a
 * perfectly healthy stack - as an opaque "fetch failed" from Node and as
 * `net::ERR_CONNECTION_RESET` from the browser - which reads like a broken
 * service rather than an address-family problem. The frontend origin, the
 * bundle's `VITE_API_BASE_URL` and the API's CORS allow-list must all agree on
 * `127.0.0.1`; the app's CSP in `frontend/index.html` permits loopback in both
 * spellings.
 */
export const API_BASE_URL = `http://127.0.0.1:${PORTS.api}`;
export const FRONTEND_BASE_URL = `http://127.0.0.1:${PORTS.frontend}`;

/**
 * The seeded project's name, as written by `seed/Program.cs`. The harness
 * resolves the project by name because the seeder knows only the GUID while
 * every route takes the public id.
 */
export const SEEDED_PROJECT_NAME = 'Cross Layer Pilot';

/** Container names the rig's helpers reach into. Disjoint from the dev stack. */
export const CONTAINERS = {
  postgres: 'dubbing-cross-layer-postgres-1',
  minio: 'dubbing-cross-layer-minio-1',
  api: 'dubbing-cross-layer-api-1',
} as const;

/**
 * Object-storage credentials, matching the `CHANGE_ME` placeholders in
 * `docker-compose.cross.yml`. Emulator credentials for a local rig only - never a
 * real key, and the seam reports them as such rather than as a finding.
 */
export const STORAGE_ACCESS_KEY = 'CHANGE_ME';
export const STORAGE_SECRET_KEY = 'CHANGE_ME';
export const STORAGE_BUCKET = 'dubbing';

/**
 * The seeded identity and project. Fixed so a failure is reproducible and a
 * `reset` can address exactly one tenant. `cross-layer.invalid` is an RFC 2606
 * reserved domain, so the seeded email can never reach a real mailbox.
 */
export const SEED = {
  tenantId: '11111111-1111-1111-1111-111111111111',
  userId: '22222222-2222-2222-2222-222222222222',
  projectId: '33333333-3333-3333-3333-333333333333',
  /**
   * A second, fixture-free project.
   *
   * A project has exactly one run slot: a start pins an active run and a second
   * start is 409 RUN_ALREADY_ACTIVE. The 040A harness smoke already starts a run
   * on the pilot project, so a processing seam that shares it is testing the
   * smoke's leftover - the 040B edge case "seam passes alone but fails in full
   * suite". Per-spec isolation is the fix.
   */
  pipelineProjectId: '44444444-4444-4444-4444-444444444444',
  externalSubject: 'cross-layer-owner',
  email: 'owner@cross-layer.invalid',
} as const;

/** The pipeline project's display name, as written by `seed/Program.cs`. */
export const SEEDED_PIPELINE_PROJECT_NAME = 'Cross Layer Pipeline';

/**
 * Connection string for the seeder.
 *
 * `SSLMode=Disable` is required: Npgsql defaults to `Prefer`, attempts a TLS
 * handshake that the stock `postgres:16` image does not answer, and surfaces it
 * as an opaque "Exception while reading from stream".
 *
 * `127.0.0.1` rather than `localhost` is equally load-bearing. On Windows
 * `localhost` resolves to `::1` first; Docker publishes on IPv4 only and the
 * OS silently drops the IPv6 SYN, so Npgsql blocks until its timeout with no
 * hint that the address was the problem.
 */
export const SEEDER_CONNECTION_STRING =
  `Host=127.0.0.1;Port=${PORTS.postgres};Database=dubbing;Username=dubbing;` +
  'Password=CHANGE_ME;SSLMode=Disable';

export const COMPOSE_FILE = 'tests/cross-layer/docker-compose.cross.yml';
export const SEEDER_PROJECT = 'tests/cross-layer/seed/CrossLayerSeed.csproj';

/** Services the rig requires, and the port that proves each one is listening. */
export const REQUIRED_SERVICES: ReadonlyArray<{ name: string; port: number }> = [
  { name: 'postgres', port: PORTS.postgres },
  { name: 'minio', port: PORTS.objectStorageApi },
  { name: 'api', port: PORTS.api },
  { name: 'frontend', port: PORTS.frontend },
];

/**
 * Services that must be *running*, not merely listening.
 *
 * Ports alone are not a sufficient boot check, and 040A's edge-case list calls
 * this out: "Partial boot (DB up, broker down) -> fail-closed with service
 * matrix, never half-run specs." The `control` and `ai` workers expose no port
 * at all, and a broker that is up but not serving leaves a run stuck in
 * `Pending` - a rig that looks healthy and proves nothing.
 */
export const REQUIRED_RUNNING_SERVICES: readonly string[] = [
  'postgres',
  'minio',
  'rabbitmq',
  'api',
  'control',
  'ai',
  'frontend',
];

/** Resolves true when something is already listening on `port`. */
export function isPortListening(port: number, host = '127.0.0.1'): Promise<boolean> {
  return new Promise((resolve) => {
    const socket = createConnection({ host, port });
    const finish = (result: boolean) => {
      socket.destroy();
      resolve(result);
    };
    socket.setTimeout(2000, () => finish(false));
    socket.once('connect', () => finish(true));
    socket.once('error', () => finish(false));
  });
}

export class RigPreflightError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'RigPreflightError';
  }
}

/**
 * Verifies every rig service is listening, and that nothing else has squatted
 * on a rig port.
 *
 * The distinction matters: a rig service that is up is a *collision candidate*,
 * not a problem, because that is the expected state once `docker compose up`
 * has run. What this guards is the case the task calls out - a port held by
 * something the rig did not start - which would silently serve foreign state to
 * every seam. Callers therefore pass the set of ports the rig itself opened.
 */
export async function assertRigPortsOpen(
  listening: ReadonlySet<number>,
  options: { readonly requireFrontend: boolean },
): Promise<void> {
  const required = REQUIRED_SERVICES.filter(
    (service) => options.requireFrontend || service.name !== 'frontend',
  );

  const missing: string[] = [];
  for (const service of required) {
    if (!(await isPortListening(service.port))) {
      missing.push(`${service.name} (:${service.port})`);
    }
  }

  if (missing.length > 0) {
    throw new RigPreflightError(
      `Cross-layer rig is not booted: ${missing.join(', ')} not listening.\n` +
        `Start it with: docker compose -f ${COMPOSE_FILE} up -d\n` +
        'The rig fails closed rather than skipping: a half-booted rig would make ' +
        'every 040B seam assertion meaningless.',
    );
  }

  const unexpected = [...listening].filter(
    (port) => !REQUIRED_SERVICES.some((service) => service.port === port),
  );
  if (unexpected.length > 0) {
    throw new RigPreflightError(
      `Rig port collision on ${unexpected.map((port) => `:${port}`).join(', ')}: held by a ` +
        'process the rig did not start. Seams would run against foreign state. ' +
        'Stop the holder, or move the port map in ' +
        `${COMPOSE_FILE} and the matching constants in tests/cross-layer/harness/config.ts.`,
    );
  }
}

/**
 * Renders the rig's service matrix, so a partial boot is diagnosable from the
 * failure message instead of requiring a second command.
 */
export function formatServiceMatrix(states: ReadonlyMap<string, string>): string {
  const width = Math.max(...REQUIRED_RUNNING_SERVICES.map((name) => name.length));
  return REQUIRED_RUNNING_SERVICES.map((name) => {
    const status = states.get(name) ?? 'not created';
    return `  ${name.padEnd(width)}  ${status}`;
  }).join('\n');
}
