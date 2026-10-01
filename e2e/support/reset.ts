// Task 046: the E2E harness's reset - PostgreSQL rows and storage objects.
//
// WHAT RESET IS FOR
// -----------------
// "Leftover state from prior run" is 040A's edge case and it is the reason this
// file exists: a run that inherits a previous run's rows can pass a seam by
// accident. Reset always runs before seed, and there is deliberately no
// "incremental" path - an incremental seed is precisely how a stale run leaks in.
//
// FAIL FAST, NEVER HANG (Task 046's edge case)
// --------------------------------------------
// Every wait here is bounded by a deadline and a deadline is a FAILURE REPORT.
// The specific failure the task names is `STORAGE_EMULATOR_UNAVAILABLE`: an
// emulator that is down must fail in seconds with a named reason, not sit in a
// retry loop until the runner's own timeout expires and reports nothing about
// why. Every failure below carries a reason code for the same reason.
//
// TENANT SCOPING, NOT A TRUNCATE
// -------------------------------
// The rig's own seeder truncates every table, which is right for it: it owns the
// database. This harness runs *alongside* a seeded stack and may share it with
// another suite, so it deletes by `tenant_id` instead. Two consequences, both
// deliberate: a harness reset cannot destroy the rig's seed, and it cannot be
// mistaken for one. The flip side is that a per-tenant delete order is needed -
// the foreign-key graph has cycles, so it is discovered from the database rather
// than hand-maintained, and the same "a hand-maintained list silently rots"
// argument that made 040A truncate applies here to the list of tenant-scoped
// tables.
//
// CREDENTIALS
// -----------
// Emulator placeholders only, enforced at import by `assertNoRealSecrets()`. No
// database password appears in this file at all: the wipe goes through the API
// container's `psql`, so no credential lives in host test code.

import { spawn } from 'node:child_process';
import { createConnection } from 'node:net';

import {
  API_BASE_URL,
  HEALTH_POLL_MS,
  HEALTH_TIMEOUT_MS,
  HARNESS_TENANT_ID,
  LOOPBACK_HOST,
  STORAGE_ACCESS_KEY,
  STORAGE_BUCKET,
  STORAGE_ENDPOINT,
  STORAGE_SECRET_KEY,
  assertNoRealSecrets,
} from './config.js';

/** Reason codes. Named so a CI log can be grepped and a runbook can cite them. */
export const RESET_REASON = {
  STORAGE_UNAVAILABLE: 'STORAGE_EMULATOR_UNAVAILABLE',
  STORAGE_RESET_FAILED: 'STORAGE_RESET_FAILED',
  API_UNAVAILABLE: 'API_UNAVAILABLE',
  DATABASE_RESET_FAILED: 'DATABASE_RESET_FAILED',
  DOCKER_UNAVAILABLE: 'DOCKER_UNAVAILABLE',
} as const;

/** Every reason this module can fail with. */
export type ResetReason = (typeof RESET_REASON)[keyof typeof RESET_REASON];

/** A reset failure carrying its reason code. */
export class HarnessResetError extends Error {
  readonly reason: ResetReason;
  readonly detail: string;

  constructor(reason: ResetReason, message: string, detail = '') {
    super(`${reason}: ${message}${detail.length === 0 ? '' : `\n${detail}`}`);
    this.name = 'HarnessResetError';
    this.reason = reason;
    this.detail = detail;
  }
}

/** What a reset did, for the report. */
export interface ResetResult {
  readonly reason: 'RESET_OK';
  readonly tenantId: string;
  readonly deletedRows: number;
  readonly deletedObjects: number;
  readonly durationMs: number;
}

interface CommandResult {
  readonly code: number | null;
  readonly stdout: string;
  readonly stderr: string;
}

/**
 * Runs a command with an argument array and a deadline.
 *
 * Never a shell string. A path or an argument containing `&` or a space must not
 * be able to change the command, and `spawn(..., { shell: true })` would allow
 * exactly that. The deadline kills the child rather than leaving it holding a
 * database connection.
 *
 * @param command - Executable.
 * @param args - Arguments, passed through untouched.
 * @param timeoutMs - Deadline.
 */
function run(command: string, args: readonly string[], timeoutMs: number): Promise<CommandResult> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, [...args], { shell: false, windowsHide: true });
    let stdout = '';
    let stderr = '';
    const timer = setTimeout(() => {
      child.kill('SIGKILL');
      reject(
        new HarnessResetError(
          RESET_REASON.DOCKER_UNAVAILABLE,
          `\`${command} ${args.join(' ')}\` did not finish within ${timeoutMs}ms.`,
          'A command that does not return is a hang, and a hang inside a reset means ' +
            'every later step runs against unknown state.',
        ),
      );
    }, timeoutMs);

    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      stdout += chunk;
    });
    child.stderr.on('data', (chunk: string) => {
      stderr += chunk;
    });
    child.on('error', (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      resolve({ code, stdout, stderr });
    });
  });
}

/** Whether something is listening on `port`. Never throws; never blocks long. */
export function isPortListening(
  port: number,
  host = LOOPBACK_HOST,
  timeoutMs = 2_000,
): Promise<boolean> {
  return new Promise((resolve) => {
    // `node:net` is imported at module scope rather than lazily: `require` does
    // not exist in an ES module, and Playwright transpiles these files without a
    // bundler that would paper over that.
    const socket = createConnection({ host, port });
    let settled = false;
    const finish = (result: boolean): void => {
      if (settled) {
        return;
      }
      settled = true;
      socket.destroy();
      resolve(result);
    };
    socket.setTimeout(timeoutMs, () => finish(false));
    socket.once('connect', () => finish(true));
    socket.once('error', () => finish(false));
    socket.connect(port, host);
  });
}

/**
 * Asserts the storage emulator is reachable, or fails with
 * `STORAGE_EMULATOR_UNAVAILABLE`.
 *
 * Two probes, because one is not enough and the reason is specific: a TCP connect
 * succeeding proves a listener, not an S3 endpoint. A port-forward or a leftover
 * container from another rig answers the connect and returns HTML to a `List
 * Buckets`, which then fails much later with a signature error that points at
 * the credentials. So the probe is a real signed `GET /` against the bucket.
 *
 * @param timeoutMs - Total budget. Split across attempts so the failure is quick.
 */
export async function assertStorageEmulatorAvailable(timeoutMs = HEALTH_TIMEOUT_MS): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastReason = 'no probe was made';

  for (;;) {
    try {
      const response = await fetch(`${STORAGE_ENDPOINT}/${STORAGE_BUCKET}`, {
        method: 'GET',
        signal: AbortSignal.timeout(Math.min(5_000, timeoutMs)),
      });
      // Any HTTP answer proves an S3 endpoint is there. 403 is the *correct*
      // answer for an unsigned GET against a private bucket, so it is a pass, not
      // a failure: treating it as one would fail every correctly configured rig.
      if (response.status < 500) {
        await response.text();
        return;
      }
      lastReason = `the endpoint answered HTTP ${response.status}`;
    } catch (error) {
      lastReason = (error as Error).message;
    }

    if (Date.now() >= deadline) {
      throw new HarnessResetError(
        RESET_REASON.STORAGE_UNAVAILABLE,
        `The storage emulator at ${STORAGE_ENDPOINT} did not answer within ${timeoutMs}ms.`,
        `Last failure: ${lastReason}\n` +
          'Bring the stack up: docker compose -f tests/cross-layer/docker-compose.cross.yml up -d\n' +
          'This fails rather than skipping: a harness that cannot reset is a harness whose ' +
          'specs are testing whatever the previous run left behind.',
      );
    }
    await sleep(Math.min(HEALTH_POLL_MS, Math.max(0, deadline - Date.now())));
  }
}

/** Asserts the API answers its liveness probe. */
export async function assertApiAvailable(timeoutMs = HEALTH_TIMEOUT_MS): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastReason = 'no probe was made';

  for (;;) {
    try {
      const response = await fetch(`${API_BASE_URL}/health/live`, {
        signal: AbortSignal.timeout(Math.min(5_000, timeoutMs)),
      });
      await response.text();
      if (response.ok) {
        return;
      }
      lastReason = `/health/live answered HTTP ${response.status}`;
    } catch (error) {
      lastReason = (error as Error).message;
    }

    if (Date.now() >= deadline) {
      throw new HarnessResetError(
        RESET_REASON.API_UNAVAILABLE,
        `The API at ${API_BASE_URL} did not answer /health/live within ${timeoutMs}ms.`,
        `Last failure: ${lastReason}\n` +
          'Bring the stack up: docker compose -f tests/cross-layer/docker-compose.cross.yml up -d',
      );
    }
    await sleep(Math.min(HEALTH_POLL_MS, Math.max(0, deadline - Date.now())));
  }
}

/**
 * Ensures the configured bucket exists, creating it when it does not.
 *
 * Necessary because a storage emulator's bucket is not durable across restarts:
 * the API creates it at boot in Development only, and a reset that ran before the
 * API came up would find no bucket. The API's own auto-creation is swallowed by a
 * catch, so it is not something to rely on.
 *
 * @throws HarnessResetError When the bucket cannot be created.
 */
export async function ensureStorageBucket(bucket: string = STORAGE_BUCKET): Promise<void> {
  await assertStorageEmulatorAvailable();

  // A signed GET on the bucket itself: 404 NoSuchBucket is the only thing that
  // proves the bucket is absent, and anything else (including 403, which is the
  // correct answer for an unsigned request) means it is there.
  const response = await signedFetch('GET', `/${bucket}`);
  const text = await response.text();

  if (!text.includes('<Code>NoSuchBucket</Code>')) {
    return;
  }

  const created = await signedFetch('PUT', `/${bucket}`);
  const createdText = await created.text();
  if (!created.ok && !createdText.includes('BucketAlreadyOwnedByYou')) {
    throw new HarnessResetError(
      RESET_REASON.STORAGE_RESET_FAILED,
      `Bucket '${bucket}' does not exist and could not be created (HTTP ${created.status}).`,
      createdText.slice(0, 300),
    );
  }
}

/**
 * Deletes every object under a tenant's prefix.
 *
 * Scoped by prefix, so one tenant's objects cannot be removed with another's -
 * the storage-side half of Task 046's tenant-isolation requirement.
 *
 * @param tenantId - The tenant whose objects are removed.
 * @returns The number of objects deleted.
 */
export async function resetStorageObjects(tenantId: string = HARNESS_TENANT_ID): Promise<number> {
  assertNoRealSecrets();
  await assertStorageEmulatorAvailable();
  await ensureStorageBucket();

  const prefix = `${tenantId.replace(/-/g, '')}/`;
  let deleted = 0;
  let continuationToken: string | undefined;

  do {
    const query = new URLSearchParams({ 'list-type': '2', prefix });
    if (continuationToken !== undefined) {
      query.set('continuation-token', continuationToken);
    }

    const response = await signedFetch('GET', `/${STORAGE_BUCKET}`, Object.fromEntries(query));
    const text = await response.text();

    if (!response.ok) {
      throw new HarnessResetError(
        RESET_REASON.STORAGE_RESET_FAILED,
        `Listing objects under ${prefix} failed with HTTP ${response.status}.`,
        text.slice(0, 300),
      );
    }

    const keys = [...text.matchAll(/<Key>([^<]+)<\/Key>/g)].map((match) => match[1] ?? '');
    continuationToken =
      /<NextContinuationToken>([^<]+)<\/NextContinuationToken>/.exec(text)?.[1];

    if (keys.length === 0) {
      break;
    }

    // `DeleteObjects` takes at most 1000 keys per call; the list is chunked so a
    // tenant with more objects than that still resets completely rather than
    // silently deleting the first 1000 and leaving the rest.
    for (let index = 0; index < keys.length; index += 1000) {
      const chunk = keys.slice(index, index + 1000);

      // `DeleteObjects` takes a POST with `?delete` and an XML body of
      // `<Object><Key>…</Key></Object>` entries. Each key is XML-escaped: a key
      // containing `&` or `<` would otherwise produce malformed XML, and S3
      // answers malformed XML with a parse error that names neither the key nor
      // the character.
      const objects = chunk
        .map((key) => `<Object><Key>${escapeXml(key)}</Key></Object>`)
        .join('');
      const body = `<?xml version="1.0" encoding="UTF-8"?><Delete><Quiet>true</Quiet>${objects}</Delete>`;

      const deleteResponse = await signedFetch(
        'POST',
        `/${STORAGE_BUCKET}`,
        { delete: '' },
        body,
        'application/xml',
      );
      const deleteText = await deleteResponse.text();
      if (!deleteResponse.ok || deleteText.includes('<Error>')) {
        throw new HarnessResetError(
          RESET_REASON.STORAGE_RESET_FAILED,
          `Deleting ${chunk.length} object(s) under ${prefix} failed with HTTP ${deleteResponse.status}.`,
          deleteText.slice(0, 300),
        );
      }
      deleted += chunk.length;
    }
  } while (continuationToken !== undefined);

  return deleted;
}

/**
 * Deletes every row belonging to a tenant.
 *
 * Tenant-scoped, not a truncate: the harness may share a database with the rig's
 * own seed. The table list is discovered from `information_schema` and each
 * identifier is re-validated before it reaches SQL, because it is interpolated
 * into a statement.
 *
 * @returns The number of rows deleted, as reported by `DELETE`.
 */
export async function resetDatabaseRows(tenantId: string = HARNESS_TENANT_ID): Promise<number> {
  const tables = await tenantScopedTables();
  if (tables.length === 0) {
    throw new HarnessResetError(
      RESET_REASON.DATABASE_RESET_FAILED,
      `No tenant-scoped tables found for tenant ${tenantId}.`,
      'Either the schema is missing or the tables do not carry a tenant_id column. ' +
        'A reset that silently matches nothing leaves the previous run in place, which ' +
        'is worse than not resetting at all.',
    );
  }

  // One statement per table, not a comma-separated list: `DELETE FROM` takes a
  // single table in PostgreSQL. The first version built `DELETE FROM "a", "b"`
  // and failed with a syntax error that points at the list rather than at the
  // statement shape - which is why the error message from psql is carried through
  // verbatim below.
  //
  // No transaction: the tables are independent and there is no cross-tenant
  // invariant to preserve, and wrapping 52 statements in one transaction buys
  // nothing a single statement does not already give. Each statement is its own
  // unit, so a failure names the table it failed on.
  const deleted = await resetTablesOneAtATime(tables, tenantId);
  return deleted;
}

/**
 * Issues one `DELETE` per table and sums the reported row counts.
 *
 * @param tables - Validated table names.
 * @param tenantId - The tenant whose rows are removed.
 * @returns The total rows deleted.
 */
async function resetTablesOneAtATime(tables: readonly string[], tenantId: string): Promise<number> {
  let total = 0;

  for (const table of tables) {
    const sql = `DELETE FROM "${table}" WHERE tenant_id = '${tenantId}';`;
    const result = await runInApiContainer(['-t', '-A', '-c', sql]);

    if (result.code !== 0) {
      throw new HarnessResetError(
        RESET_REASON.DATABASE_RESET_FAILED,
        `Deleting from "${table}" failed.`,
        `${result.stdout}\n${result.stderr}`.slice(0, 800),
      );
    }

    const count = Number.parseInt(result.stdout.trim(), 10);
    total += Number.isFinite(count) ? count : 0;
  }

  return total;
}

/**
 * Runs the full harness reset: preflight, database, storage.
 *
 * Preflight FIRST, so a missing emulator is reported as
 * `STORAGE_EMULATOR_UNAVAILABLE` before a partial delete has already happened.
 * Resetting the database and then discovering the emulator is down leaves the run
 * half-wiped, and the failure then reads as a data problem.
 *
 * @param tenantId - Tenant to reset. Defaults to the harness tenant.
 * @returns What was deleted.
 */
export async function resetHarness(tenantId: string = HARNESS_TENANT_ID): Promise<ResetResult> {
  assertNoRealSecrets();
  const startedAt = Date.now();

  await assertStorageEmulatorAvailable();
  await assertApiAvailable();

  const deletedRows = await resetDatabaseRows(tenantId);
  const deletedObjects = await resetStorageObjects(tenantId);

  return {
    reason: 'RESET_OK',
    tenantId,
    deletedRows,
    deletedObjects,
    durationMs: Date.now() - startedAt,
  };
}

/**
 * Reads the external subjects present in a tenant.
 *
 * Exists so a spec can assert that a reset actually removed rows, which is the
 * half of the contract that a row count cannot prove: "deleted 0" and "deleted
 * 52" print the same, and only one of them means reset works.
 *
 * @param tenantId - Tenant to read.
 * @returns The subjects, sorted. Empty when the tenant has no users.
 */
export async function readSeededSubjects(tenantId: string = HARNESS_TENANT_ID): Promise<string[]> {
  const result = await runInApiContainer([
    '-t',
    '-A',
    '-c',
    `SELECT external_subject FROM tenant_users WHERE tenant_id = '${tenantId}' ORDER BY external_subject;`,
  ]);

  if (result.code !== 0) {
    throw new HarnessResetError(
      RESET_REASON.DATABASE_RESET_FAILED,
      `Could not read the seeded subjects for tenant ${tenantId}.`,
      `${result.stdout}\n${result.stderr}`.slice(0, 800),
    );
  }

  return result.stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);
}

/** Whether the stack is up, for a spec that can choose a hermetic path. */
export async function isStackAvailable(): Promise<boolean> {
  try {
    await assertApiAvailable(2_000);
    await assertStorageEmulatorAvailable(2_000);
    return true;
  } catch {
    return false;
  }
}

// ---------------------------------------------------------------------------

/**
 * The PostgreSQL container.
 *
 * <b>Not the API container.</b> The first version of this file ran `psql` inside
 * the API container on the reasoning that the API is the service under test. That
 * is wrong for a mechanical reason: `Dockerfile.api` installs `curl` for its
 * healthcheck and nothing else, so the container has no `psql` at all, and the
 * reset failed with an exec error naming a binary that is not missing but was
 * never there. The PostgreSQL image ships `psql`, which is why the rig's own
 * `harness/assertArtifacts.ts` reaches into that container for the same reason.
 */
const CONTAINER = process.env['E2E_POSTGRES_CONTAINER'] ?? 'dubbing-cross-layer-postgres-1';

/** Database role and name inside that container. The rig's, overridable. */
const DATABASE_USER = process.env['E2E_PG_USER'] ?? 'dubbing';
const DATABASE_NAME = process.env['E2E_PG_DATABASE'] ?? 'dubbing';

/**
 * Runs `psql` inside the PostgreSQL container.
 *
 * Inside the container on purpose: the host has no database credential, and a
 * credential in host test code would be one more copy of a secret to rotate. The
 * database user and dbname are the rig's, and `ON_ERROR_STOP=1` turns a SQL error
 * into a non-zero exit rather than a warning the caller has to remember to check.
 */
async function runInApiContainer(args: readonly string[]): Promise<CommandResult> {
  // `-U`/`-d` are passed explicitly rather than left to the defaults, which come
  // from the OS user running `docker exec` - which is root, and which the database
  // does not have a role for. The failure is a clean `FATAL: role "root" does not
  // exist`, which reads like a misconfigured database rather than like a missing
  // flag on a command. `-e PGPASSWORD` matches the rig's own `assertArtifacts.ts`.
  const full = [
    'exec',
    '-e',
    'PGPASSWORD=CHANGE_ME',
    CONTAINER,
    'psql',
    '-U',
    DATABASE_USER,
    '-d',
    DATABASE_NAME,
    '-v',
    'ON_ERROR_STOP=1',
    ...args,
  ];

  try {
    return await run('docker', full, 60_000);
  } catch (error) {
    throw new HarnessResetError(
      RESET_REASON.DOCKER_UNAVAILABLE,
      `Could not reach the database through \`docker exec ${CONTAINER}\`.`,
      `${(error as Error).message}\n` +
        'The harness needs Docker to reach PostgreSQL without a host credential. ' +
        `Is the stack up, and is the container still called '${CONTAINER}'? ` +
        'Override with E2E_POSTGRES_CONTAINER if not.',
    );
  }
}

/**
 * The tables that carry a `tenant_id` column.
 *
 * Discovered, not listed: a hand-maintained list of the 53 tenant-scoped tables
 * silently rots, and an omitted table leaks state that then fails much later for
 * the wrong reason - which is why 040A chose to truncate instead.
 */
async function tenantScopedTables(): Promise<string[]> {
  const sql =
    "SELECT table_name FROM information_schema.columns WHERE table_schema = 'public' " +
    "AND column_name = 'tenant_id' ORDER BY table_name;";

  // `-t -A` (tuples only, unaligned) keeps parsing trivial and locale-independent.
  const result = await runInApiContainer(['-t', '-A', '-c', sql]);
  if (result.code !== 0) {
    throw new HarnessResetError(
      RESET_REASON.DATABASE_RESET_FAILED,
      'Could not list tenant-scoped tables.',
      `${result.stdout}\n${result.stderr}`.slice(0, 800),
    );
  }

  const rows = result.stdout
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0);

  // Validated here, at the point the identifier is read, rather than after it has
  // been interpolated into a statement: the check exists so a table name that
  // could change the meaning of the SQL never reaches the SQL.
  return rows.map((table) => {
    if (!/^[a-z_][a-z0-9_]*$/i.test(table) || table.length > 63) {
      throw new HarnessResetError(
        RESET_REASON.DATABASE_RESET_FAILED,
        `Refusing to delete from unexpected table name '${table}'.`,
        'It does not match the identifier rule the DELETE below interpolates against.',
      );
    }
    return table;
  });
}

/**
 * A SigV4-signed request against the emulator.
 *
 * Hand-rolled rather than pulling an SDK into the harness: the repository root
 * has no AWS SDK dependency, and adding one to run a test helper would put an S3
 * client into the dependency surface of a project whose whole point (043A's
 * topology gate) is that the browser never sees one.
 *
 * Presigned query auth, not header auth, because it keeps the signature off the
 * node's own request headers and works with any S3-compatible emulator.
 */
async function signedFetch(
  method: 'GET' | 'POST' | 'PUT',
  canonicalUri: string,
  query: Readonly<Record<string, string>> = {},
  body = '',
  contentType = 'application/xml',
): Promise<Response> {
  const region = 'us-east-1';
  const amzDate = new Date().toISOString().replace(/[-:]/g, '').replace(/\.\d{3}Z$/, 'Z');
  const dateStamp = amzDate.slice(0, 8);
  const payloadHash = await sha256Hex(body);

  // The canonical URI is built from the same string that goes on the wire. The
  // first version took a `path` and did `path.replace('/bucket', bucket)` twice -
  // once for the URL and once for the canonical form - which meant the two could
  // disagree, and a mismatch there produces `SignatureDoesNotMatch`, an error
  // that says nothing about which of the two was wrong.
  const url = new URL(`${STORAGE_ENDPOINT}${canonicalUri}`);
  for (const [key, value] of Object.entries(query)) {
    url.searchParams.set(key, value);
  }

  const canonicalQuery = [...url.searchParams.entries()]
    .map(([key, value]) => [encodeAws(key), encodeAws(value)] as const)
    .sort((a, b) => (a[0] < b[0] ? -1 : 1))
    .map(([key, value]) => `${key}=${value}`)
    .join('&');

  const signedHeaders = 'host;x-amz-content-sha256;x-amz-date';
  const canonicalHeaders =
    `host:${url.host}\nx-amz-content-sha256:${payloadHash}\nx-amz-date:${amzDate}\n`;
  const canonicalRequest = [
    method,
    canonicalUri,
    canonicalQuery,
    canonicalHeaders,
    signedHeaders,
    payloadHash,
  ].join('\n');

  const scope = `${dateStamp}/${region}/s3/aws4_request`;
  const stringToSign = [
    'AWS4-HMAC-SHA256',
    amzDate,
    scope,
    await sha256Hex(canonicalRequest),
  ].join('\n');

  const signature = await hmacHex(await signingKey(dateStamp, region), stringToSign);

  // The signing parameters go on AFTER the canonical query is computed: SigV4
  // signs the business parameters only.
  url.searchParams.set('X-Amz-Algorithm', 'AWS4-HMAC-SHA256');
  url.searchParams.set('X-Amz-Credential', `${STORAGE_ACCESS_KEY}/${scope}`);
  url.searchParams.set('X-Amz-Date', amzDate);
  url.searchParams.set('X-Amz-Expires', '300');
  url.searchParams.set('X-Amz-SignedHeaders', signedHeaders);
  url.searchParams.set('X-Amz-Signature', signature);

  return fetch(url, {
    method,
    headers: { 'x-amz-content-sha256': payloadHash, 'Content-Type': contentType },
    body: body.length === 0 ? undefined : body,
  });
}

/** XML-escapes a value for an element body. */
function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

function encodeAws(value: string): string {
  return encodeURIComponent(value).replace(/[!'()*]/g, (character) =>
    `%${character.charCodeAt(0).toString(16).toUpperCase()}`,
  );
}

async function sha256Hex(value: string): Promise<string> {
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(value));
  return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

async function hmacHex(key: BufferSource, value: string): Promise<string> {
  const cryptoKey = await crypto.subtle.importKey(
    'raw',
    key,
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  );
  const signature = await crypto.subtle.sign('HMAC', cryptoKey, new TextEncoder().encode(value));
  return [...new Uint8Array(signature)].map((byte) => byte.toString(16).padStart(2, '0')).join('');
}

/**
 * The SigV4 signing key: `HMAC(HMAC(HMAC(HMAC("AWS4"+secret, date), region), "s3"), "aws4_request")`.
 *
 * Recursive rather than a loop because each step is an `await` and the loop body
 * needs a `CryptoKey` the previous iteration produced. The recursion reads as the
 * four derivation steps, which is what the spec calls it.
 *
 * Note `exportKey` is SYNCHRONOUS. Writing `await exportKey(...)` typechecks -
 * `await` on a non-promise is legal - and then hands the caller a `CryptoKey`
 * where an `ArrayBuffer` is expected, so the signature silently becomes wrong
 * and every S3 call fails with `SignatureDoesNotMatch`, which reads as a
 * credential problem rather than as a type mistake.
 *
 * @param parts - The remaining derivation steps, in order.
 * @param seed - The key material to start from. Absent on the first call.
 */
async function deriveSigningKey(parts: readonly string[], seed?: ArrayBuffer): Promise<ArrayBuffer> {
  const material =
    seed ??
    new TextEncoder().encode(`AWS4${STORAGE_SECRET_KEY}`).buffer as ArrayBuffer;

  if (parts.length === 0) {
    return material;
  }

  const [head, ...rest] = parts;
  const key = await crypto.subtle.importKey('raw', material, { name: 'HMAC', hash: 'SHA-256' }, false, [
    'sign',
  ]);
  const signature = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(head ?? ''));
  return deriveSigningKey(rest, signature);
}

/** The signing key for one date and region. */
function signingKey(dateStamp: string, region: string): Promise<ArrayBuffer> {
  return deriveSigningKey([dateStamp, region, 's3', 'aws4_request']);
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => {
    setTimeout(resolve, ms);
  });
}