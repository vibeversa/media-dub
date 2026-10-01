// The rehearsal rollout target (Task 043B).
//
// WHAT THIS IS, AND WHAT IT IS NOT
// --------------------------------
// A deterministic stand-in for the API, the workers and the static origin, used
// by `deploy/rollout/rehearse-rollback.sh` to exercise ROLLBACK MECHANICS in a
// real cluster: revision history, `kubectl rollout undo`, probe-gated readiness,
// `maxUnavailable: 0` and the PodDisruptionBudget.
//
// It is NOT the application, and the rehearsal record says so in `notCovered`.
// Nothing here touches PostgreSQL, the broker, object storage or a provider,
// and a run of this image proves that the Kubernetes rollback path works - not
// that a production rollback is safe. Those are different claims and collapsing
// them is the failure the record's `notCovered` list exists to prevent.
//
// WHY IT EXISTS AT ALL
// --------------------
// A rollback path that has never been run is a claim, and R5 is "rehearsed and
// recorded, not just documented". The alternative to a stand-in is a real
// application deploy, which needs PostgreSQL, RabbitMQ, Redis, object storage
// and credentials - a whole environment - and which would then prove the
// application rolled back rather than proving the OPERATIONS work. Both matter;
// only the second is reproducible by one person on one machine, so it is the one
// a quarterly rehearsal runs, and the first is what the post-deploy gate in
// `deploy/verify.sh` is for.
//
// THE ONE ENDPOINT THAT MATTERS MOST
// ----------------------------------
// `/version` reports the release baked into THIS image. `kubectl rollout undo`
// moving a Deployment's template proves the API accepted a command; it does not
// prove the pods are the previous build - a re-pushed tag makes the two differ,
// and the runbook already says to confirm with `/version` rather than with the
// rollout status. This server exists so the rehearsal can make exactly that
// check, against two genuinely different builds.
import { createServer } from 'node:http';

const PORT = Number.parseInt(process.env.PORT ?? '8080', 10);
// Baked at BUILD time, so the two rehearsal images are distinguishable by what
// they serve rather than by what tag they were given. A tag can be re-pushed;
// this string cannot.
const RELEASE = process.env.REHEARSAL_RELEASE ?? 'rehearsal-unknown';

function send(response, status, body, type = 'application/json') {
  const payload = typeof body === 'string' ? body : JSON.stringify(body);
  response.writeHead(status, {
    'content-type': type,
    'content-length': Buffer.byteLength(payload),
    'cache-control': 'no-store',
  });
  response.end(payload);
}

const server = createServer((request, response) => {
  const path = (request.url ?? '/').split('?')[0];

  if (path === '/health/live') {
    send(response, 200, { status: 'Healthy', release: RELEASE, checks: {} });
    return;
  }
  if (path === '/health/ready') {
    // The shape the real `/health/ready` has, including the migration-currency
    // check the real readiness asserts. A stand-in that answered 200 with an
    // empty body would let a rehearsal pass while proving nothing about the
    // check that actually keeps an un-migrated pod out of the Service.
    send(response, 200, {
      status: 'Healthy',
      release: RELEASE,
      checks: { 'migration-currency': { status: 'Healthy', description: 'rehearsal stand-in: no schema' } },
    });
    return;
  }
  if (path === '/healthz') {
    send(response, 200, 'ok', 'text/plain');
    return;
  }
  if (path === '/version') {
    send(response, 200, { release: RELEASE, commit: RELEASE, openapiVersion: RELEASE, build: 'rehearsal' });
    return;
  }
  // The workspace read the compatibility matrix probes.
  const workspace = /^\/api\/v1\/projects\/([^/]+)\/workspace$/.exec(path);
  if (workspace !== null) {
    send(response, 200, { projectId: workspace[1], release: RELEASE, segments: [], artifacts: [] });
    return;
  }
  send(response, 404, { error: 'not_found', path, release: RELEASE });
});

server.listen(PORT, '0.0.0.0', () => {
  process.stdout.write(`rehearsal target listening on ${PORT}, release=${RELEASE}\n`);
});

// A worker has no probe, so nothing restarts it on a crash. Exiting on a signal
// is still what makes a rollout terminate instead of hanging, and the runner
// waits for `rollout status` rather than for a log line.
for (const signal of ['SIGTERM', 'SIGINT']) {
  process.on(signal, () => {
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 2000).unref();
  });
}
