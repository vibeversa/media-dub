// The workspace read the compatibility matrix probes (Task 043B, R3).
//
// WHAT IT IS FOR
// --------------
// `deploy/rollout/compat-smoke.sh` has to prove a claim that is entirely about
// code and schema, and the cheapest honest proof is one authenticated write
// followed by one authenticated read. This file mints the bearer token the API
// requires and does both.
//
// The token is minted HERE, in the same process that issues the requests, rather
// than being passed in, for two reasons. The signing key is a configuration
// value the API is started with by the script, so minting locally keeps it out
// of the process table and out of any argument. And a token is not a secret
// worth storing: it is a short-lived assertion about a claim the harness is
// already making on purpose, in a container that exists for the length of one
// matrix run.
//
// The claims are `tid` (the tenant), `sub` (the user) and `roles`, which is what
// `AuthPolicies` maps onto `RequireProjectEditor` and `RequireProjectViewer`.
// `Service` is the role that satisfies both without a project-ownership
// resource, so the harness does not have to create a project before it can read
// one - the very thing it is trying to test.
import { createHmac, randomUUID } from 'node:crypto';

const base64url = (input) => Buffer.from(input).toString('base64url');

/** An HS256 JWT with the claims this API reads. */
export function mintToken({ signingKey, tenantId, userId, audience = 'dubbing-api', roles = ['Service'], ttlSeconds = 900 }) {
  const now = Math.floor(Date.now() / 1000);
  const header = base64url(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const payload = base64url(JSON.stringify({
    tid: tenantId,
    sub: userId,
    aud: audience,
    // `roles` is an ARRAY and `role` is a STRING, deliberately, and not because
    // either is nicer to read.
    //
    // The two images this harness compares are two different builds, and they do
    // not read roles the same way. The candidate reads repeated `roles` claims;
    // the previous release's `AuthRegistration` reads `role` through
    // `JwtBearerOptions.MapInboundClaims`, which rewrites `role` to the long
    // `ClaimTypes.Role` URI. An ARRAY under `role` is not a string a role check
    // can match, and the previous release answered **403 FORBIDDEN** on
    // `POST /api/v1/projects` while the candidate answered 201 - with the same
    // token, the same database and the same schema.
    //
    // Emitting both shapes is what makes the comparison a comparison. A harness
    // that tuned its token until one build accepted it would be measuring its own
    // token.
    roles,
    role: roles[0],
    iat: now,
    nbf: now - 5,
    exp: now + ttlSeconds,
  }));
  const signature = createHmac('sha256', signingKey).update(`${header}.${payload}`).digest('base64url');
  return `${header}.${payload}.${signature}`;
}

/** A fresh tenant and user, so no run can see another's rows. */
export function newIdentity() {
  return { tenantId: randomUUID(), userId: randomUUID() };
}

async function call(baseUrl, token, method, path, body) {
  const response = await fetch(`${baseUrl}${path}`, {
    method,
    headers: {
      authorization: `Bearer ${token}`,
      'content-type': 'application/json',
      accept: 'application/json',
    },
    body: body === undefined ? undefined : JSON.stringify(body),
    signal: AbortSignal.timeout(20_000),
  });
  const text = await response.text();
  let parsed = null;
  try {
    parsed = text.length === 0 ? null : JSON.parse(text);
  } catch {
    parsed = null;
  }
  return { status: response.status, body: parsed, raw: text.slice(0, 400) };
}

/**
 * The two probes, plus the write that makes the read meaningful.
 *
 * A NEW column that is NOT NULL with no default breaks the WRITE, not the read -
 * the read of a project the harness just created succeeds happily and the
 * compatibility window is broken anyway. So a matrix that only reads proves half
 * the thing it claims.
 */
export async function runProbes({ baseUrl, signingKey, tenantId, userId }) {
  const token = mintToken({ signingKey, tenantId, userId });
  const results = {};

  // `/health`, not `/health/ready`, and the reason is the whole point of the
  // probe. `/health/ready` answers with a bare status code over the WHOLE ready
  // set - PostgreSQL, object storage, the broker, Redis and migration currency -
  // so a matrix keyed on it is keyed on whether an object store happens to be
  // reachable, which is not what a compatibility matrix measures. `/health`
  // answers 200 or 503 on the same aggregate AND names each check, so the
  // migration-currency verdict can be read on its own.
  //
  // The first version of this probe read the readiness code and nothing else, and
  // a storage check with no MinIO behind it turned all three cells red for a
  // reason that had nothing to do with the schema.
  results.health = await call(baseUrl, token, 'GET', '/health');
  results.ready = await call(baseUrl, token, 'GET', '/health/ready');
  results.write = await call(baseUrl, token, 'POST', '/api/v1/projects', {
    sourceLanguage: 'en',
    targetLanguage: 'es',
    name: `compat-${userId.slice(0, 8)}`,
  });
  const projectId = results.write.body?.id ?? null;
  results.workspace = projectId === null
    ? { status: 0, body: null, raw: 'no project id: the create call did not return one' }
    : await call(baseUrl, token, 'GET', `/api/v1/projects/${projectId}/workspace`);

  // `absent` is a real answer, not a missing one: the previous release's build
  // predates `MigrationCurrencyCheck` and does not serve `/health` at all, so
  // there is no migration-currency verdict to read. Reporting `absent` rather
  // than `Unhealthy` is what keeps "the build does not have the check" distinct
  // from "the check says the schema is wrong" - two states that both read as a
  // non-200 and mean opposite things.
  const checks = Array.isArray(results.health.body?.checks) ? results.health.body.checks : [];
  const currency = checks.find((check) => String(check?.name).toLowerCase() === 'migration-currency');
  const migrationCurrency = currency === undefined
    ? 'absent'
    : String(currency.status ?? 'unknown');

  return {
    // The status of the health REPORT, which is the aggregate over the same set
    // as readiness. Recorded, not asserted: it is the wrong thing to gate a
    // compatibility matrix on.
    readyStatus: results.health.status,
    readyBody: (results.ready.raw || '').slice(0, 200),
    migrationCurrency,
    migrationCurrencyDescription: currency === undefined ? '' : String(currency.description ?? ''),
    unhealthyChecks: checks.filter((check) => check?.status !== 'Healthy').map((check) => `${check?.name}=${check?.status}`),
    writeStatus: results.write.status,
    writeBody: results.write.body,
    projectId,
    workspaceStatus: results.workspace.status,
    workspaceBody: results.workspace.body,
    workspaceRaw: results.workspace.raw,
  };
}

/** Prints a single JSON object on stdout, for the shell to record. */
export async function main() {
  const baseUrl = (process.env.COMPAT_BASE_URL ?? 'http://127.0.0.1:58080').replace(/\/$/, '');
  const signingKey = process.env.COMPAT_SIGNING_KEY;
  if (!signingKey) {
    process.stderr.write('COMPAT_SIGNING_KEY is required\n');
    process.exit(2);
  }
  const identity = newIdentity();
  const probes = await runProbes({ baseUrl, signingKey, ...identity });
  process.stdout.write(`${JSON.stringify({ baseUrl, ...identity, ...probes })}\n`);
}

if (process.argv[1] && process.argv[1].endsWith('workspace-probe.mjs')) {
  main().catch((error) => {
    process.stderr.write(`workspace-probe: ${error?.message ?? error}\n`);
    process.exit(2);
  });
}
