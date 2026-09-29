// Ephemeral stub of a deployed API, used only to exercise deploy/verify.sh's
// post-deploy gate on a machine with no cluster. NOT part of any suite.
//
// It serves exactly what the gate reads - /health/live, /health/ready and the
// OpenAPI document - and the document's version and a forced HTTP status are
// controlled by the environment, so each branch of the gate can be reached
// without a deployment.
//
//   STUB_VERSION=v1        info.version in the served document
//   STUB_ROUTES=health    which routes answer 200 (comma list)
//   STUB_DOC=missing       serve no OpenAPI document at all
//   STUB_DOC=garbage       serve a non-JSON body
//
// Usage: node scripts/verify-stub.mjs [port]
import { createServer } from 'node:http';

const port = Number(process.argv[2] ?? process.env.STUB_PORT ?? 58200);
const version = process.env.STUB_VERSION ?? 'v1';
const routes = new Set((process.env.STUB_ROUTES ?? 'health,doc').split(','));
const doc = process.env.STUB_DOC ?? 'ok';

const document = JSON.stringify({
  openapi: '3.0.3',
  info: { title: 'Dubbing Platform API', version },
  paths: {},
});

const server = createServer((request, response) => {
  const path = (request.url ?? '/').split('?')[0];
  const isHealth = path === '/health/live' || path === '/health/ready';
  const isDoc = path === '/openapi.json' || path === '/openapi/v1.json';
  if (isHealth && routes.has('health')) {
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end('{"status":"Healthy"}');
    return;
  }
  if (isDoc && routes.has('doc') && doc !== 'missing') {
    if (doc === 'garbage') {
      response.writeHead(200, { 'content-type': 'application/json' });
      response.end('<html>not a document</html>');
      return;
    }
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end(document);
    return;
  }
  response.writeHead(404, { 'content-type': 'application/json' });
  response.end('{"error":{"code":"NOT_FOUND"}}');
});

server.listen(port, '127.0.0.1', () => {
  console.log(`verify-stub listening on http://127.0.0.1:${port} (version=${version} routes=${[...routes].join(',')} doc=${doc})`);
});
