// Task 040A: static file server for the cross-layer frontend container.
//
// Serves the prebuilt frontend/dist with SPA fallback (any unknown path returns
// index.html so client-side routes deep-link correctly), on port 4173 which the
// compose file maps to the host's 54173. No dependencies, no framework: the rig
// must boot from a stock node image.
//
// Security: read-only bind mounts, no directory listing, and no header that could
// weaken the API's own security policy - the API's SecurityHeadersMiddleware is
// the single owner of response headers for API traffic; this server only serves
// static assets and explicitly does not duplicate CSP.

import { createReadStream, existsSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { extname, join, normalize, resolve, sep } from 'node:path';

const ROOT = resolve('/app/dist');
const PORT = 4173;

// Dual-stack, deliberately. Docker publishes the port on both 0.0.0.0 and [::],
// but a listener bound to 0.0.0.0 only has no IPv6 socket to answer on, so a
// client that resolves `localhost` to ::1 first - which both Node on Windows and
// Chromium do - gets ERR_CONNECTION_RESET against a perfectly healthy server.
// Binding `::` accepts IPv4-mapped connections as well (Node's default
// ipv6Only is false), so one listener serves both families.
const HOST = '::';

const CONTENT_TYPES = new Map([
  ['.html', 'text/html; charset=utf-8'],
  ['.js', 'text/javascript; charset=utf-8'],
  ['.mjs', 'text/javascript; charset=utf-8'],
  ['.css', 'text/css; charset=utf-8'],
  ['.json', 'application/json; charset=utf-8'],
  ['.svg', 'image/svg+xml'],
  ['.png', 'image/png'],
  ['.jpg', 'image/jpeg'],
  ['.jpeg', 'image/jpeg'],
  ['.webp', 'image/webp'],
  ['.ico', 'image/x-icon'],
  ['.woff', 'font/woff'],
  ['.woff2', 'font/woff2'],
  ['.map', 'application/json; charset=utf-8'],
  ['.txt', 'text/plain; charset=utf-8'],
]);

/**
 * Maps a request path to a file inside ROOT, or null when it escapes the root.
 * Traversal is rejected rather than normalised into a sibling directory.
 */
function resolveWithinRoot(urlPath) {
  const decoded = decodeURIComponent(urlPath.split('?')[0] ?? '/');
  const candidate = resolve(join(ROOT, normalize(decoded)));
  if (candidate !== ROOT && !candidate.startsWith(ROOT + sep)) {
    return null;
  }
  return candidate;
}

function send(response, status, body, contentType) {
  response.writeHead(status, {
    'Content-Type': contentType,
    'Content-Length': Buffer.byteLength(body),
    'X-Content-Type-Options': 'nosniff',
  });
  response.end(body);
}

function serveFile(response, filePath) {
  const stats = statSync(filePath);
  const contentType = CONTENT_TYPES.get(extname(filePath).toLowerCase()) ?? 'application/octet-stream';
  response.writeHead(200, {
    'Content-Type': contentType,
    'Content-Length': stats.size,
    'X-Content-Type-Options': 'nosniff',
  });
  createReadStream(filePath).pipe(response);
}

const server = createServer((request, response) => {
  // Every filesystem touch is guarded. The rig rebuilds `dist` in place while
  // this container is serving it, and Vite empties the output directory before
  // writing, so a request can legitimately arrive for a file that no longer
  // exists for a few hundred milliseconds. An unguarded `statSync` throws, an
  // unhandled throw kills the process, and the container then refuses every
  // connection - turning a normal rebuild into a rig-wide failure.
  try {
    handle(request, response);
  } catch (error) {
    if (!response.headersSent) {
      send(
        response,
        503,
        `frontend/dist is being rebuilt or is incomplete: ${error.message}`,
        'text/plain; charset=utf-8',
      );
      return;
    }
    response.destroy();
  }
});

function handle(request, response) {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    send(response, 405, 'Method Not Allowed', 'text/plain; charset=utf-8');
    return;
  }

  const indexPath = join(ROOT, 'index.html');
  if (!existsSync(indexPath)) {
    // Fail loudly: a missing bundle means the harness was not built, and a silent
    // blank page would make every browser assertion lie.
    send(
      response,
      503,
      'frontend/dist is missing. Build it with: npm run build --prefix frontend (see tests/cross-layer/README.md).',
      'text/plain; charset=utf-8',
    );
    return;
  }

  const candidate = resolveWithinRoot(request.url ?? '/');
  if (candidate === null) {
    send(response, 400, 'Bad Request', 'text/plain; charset=utf-8');
    return;
  }

  if (existsSync(candidate) && statSync(candidate).isFile()) {
    serveFile(response, candidate);
    return;
  }

  // SPA fallback so /projects/... deep links resolve client-side.
  serveFile(response, indexPath);
}

server.listen(PORT, HOST, () => {
  process.stdout.write(`cross-layer frontend listening on ${HOST}:${PORT} serving ${ROOT}\n`);
});
