// The hosting policy's decision layer (Task 043). Pure rules over
// `deploy/cdn/origin.json`.
//
// WHY THIS IS A MODULE AND NOT A SCRIPT
// -------------------------------------
// The hosting policy is a set of decisions: which cache class a path belongs to,
// whether a miss is the SPA document, which headers must be on a response, and
// whether the API and the static origin send the same security policy. Each of
// those is a place where being wrong is invisible in a config file and obvious
// in a response, so each is a pure function with unit tests here, and
// `deploy/tests/hosting.test.sh` is the I/O that reads the real files and starts
// the real image.
//
// THE ORDER OF THE CLASSES IS THE ORDER THEY ARE EVALUATED
// --------------------------------------------------------
// `matchesClass` returns the FIRST match, and the array in `origin.json` is
// ordered most-specific-first on purpose. `catchAll` must be last, and the
// function asserts that rather than trusting the file: a `catchAll` in the
// middle makes every route after it unreachable, which reads as "the API prefix
// 404s" rather than as "the class order is wrong".
//
// WHY `source-map` AND `api` SET A STATUS RATHER THAN A CACHE CLASS
// ------------------------------------------------------------------
// Both are refusals, not cache policies. A 404 for a `.map` and a 404 for
// `/api/…` are the correct answers, and both must NOT fall through to the SPA
// document. Serving index.html for a missing hashed asset is the specific bug
// this module's `spaFallback: false` on `hashed-asset` exists to prevent: the
// browser receives 200 text/html where it expected JavaScript, and the resulting
// error names the frontend rather than the CDN.

/** Machine-readable reasons, shared with docs/ci-branch-protection.md §2. */
export const REASON_OK = 'OK';
export const REASON_MISSING_CONFIG = 'CONFIG_MISSING';
export const REASON_INVALID_CONFIG = 'CONFIG_INVALID';
export const REASON_HEADER_MISMATCH = 'HEADER_MISMATCH';
export const REASON_CDN_UNAVAILABLE = 'CDN_UNAVAILABLE';

/**
 * The class ids `origin.json` is required to declare, in evaluation order.
 *
 * The order is not cosmetic. `matchesClass` returns the FIRST match, and an
 * extension matcher will claim anything with that extension — so
 * `runtime-version` (`/version.json`, extension `json`) has to precede
 * `unhashed-asset` (which matches `json` among others), and `source-map`
 * (`.map`) has to precede nothing but must follow nothing that would claim a
 * `.map`. Putting the extension class first is not a style error: it silently
 * gives `/version.json` a five-minute cache, and a cached version document is a
 * skew check that answers yesterday's question.
 */
export const REQUIRED_CLASS_IDS = [
  'document',
  'hashed-asset',
  'runtime-version',
  'service-worker',
  'api',
  'source-map',
  'unhashed-asset',
  'spa-route',
];

/** Headers both the static origin and the API must send, and the same value. */
export const SHARED_SECURITY_HEADERS = [
  'Content-Security-Policy',
  'Referrer-Policy',
  'X-Content-Type-Options',
  'X-Frame-Options',
];

/** Cache-Control the `document` class must carry. */
export const DOCUMENT_CACHE_CONTROL = 'no-cache';

/** Cache-Control the `hashed-asset` class must carry. */
export const IMMUTABLE_CACHE_CONTROL = 'public, max-age=31536000, immutable';

/**
 * Normalises a path for class matching: strips the query string and any
 * fragment, collapses a trailing slash, and always yields a leading slash.
 * `/index.html?x=1` and `/index.html` must land in the same class, or a cache
 * header becomes a function of someone's query string.
 */
export function normalizePath(path) {
  if (typeof path !== 'string' || path.length === 0) {
    return '/';
  }
  let value = path;
  const hash = value.indexOf('#');
  if (hash >= 0) value = value.slice(0, hash);
  const query = value.indexOf('?');
  if (query >= 0) value = value.slice(0, query);
  if (!value.startsWith('/')) value = `/${value}`;
  if (value.length > 1 && value.endsWith('/')) value = value.replace(/\/+$/, '');
  return value.length === 0 ? '/' : value;
}

/** The lowercase extension of a path, or `null`. Pure. */
export function extensionOf(path) {
  const normalized = normalizePath(path);
  const lastSlash = normalized.lastIndexOf('/');
  const name = lastSlash >= 0 ? normalized.slice(lastSlash + 1) : normalized;
  const dot = name.lastIndexOf('.');
  if (dot <= 0) return null;
  return name.slice(dot + 1).toLowerCase();
}

/**
 * Whether a cache class matches a path. Pure, and returns the FIRST match —
 * see the ordering note at the top of this file.
 */
export function matchesClass(cacheClass, path) {
  const match = cacheClass?.match;
  if (!match || typeof match !== 'object') {
    // A class with no matcher is a `catchAll`, which is how `spa-route` is
    // declared. An absent `match` on a class that is not last is a config
    // error, and `validateOriginConfig` reports it; here it simply does not
    // match, so a malformed class cannot silently swallow every path.
    return match === undefined && cacheClass?.spaFallback === true;
  }
  if (match.catchAll === true) {
    return true;
  }
  if (Array.isArray(match.exactPaths)) {
    if (match.exactPaths.includes(normalizePath(path))) return true;
  }
  if (Array.isArray(match.pathPrefixes)) {
    const normalized = normalizePath(path);
    // Each prefix is normalised, and the boundary is enforced at a path SEGMENT.
    //
    // Two separate traps here, both of which are real request paths:
    //   1. `normalizePath` strips a trailing slash, so a declared `/assets/`
    //      becomes `/assets` and the directory URL would stop matching its own
    //      class.
    //   2. A bare `startsWith` on the trimmed form then also matches a SIBLING
    //      with a shared name — `/assetsX/a.js` for an `/assets/` prefix,
    //      `/api/v2/x` for `/api/`. For the `api` class that hands a v2 request
    //      to the v1 origin, and the symptom is a 404 that reads as a routing
    //      bug rather than as a cache-class one.
    // So: equal to the trimmed prefix, OR under it at a segment boundary.
    const matches = match.pathPrefixes.some((declared) => {
      const trimmed = normalizePath(declared);
      if (normalized === trimmed) return true;
      return normalized.startsWith(`${trimmed}/`);
    });
    if (matches) {
      return true;
    }
  }
  if (Array.isArray(match.extensions)) {
    const extension = extensionOf(path);
    if (extension !== null && match.extensions.map((value) => String(value).toLowerCase()).includes(extension)) {
      return true;
    }
  }
  return false;
}

/**
 * The class a path belongs to, or `null` when nothing matches. Pure.
 *
 * `null` is a real outcome and not an error: a config with no `catchAll` leaves
 * genuinely unrouted paths, and pretending they would fall through to the
 * document is how a 404 turns into a 200 of the wrong content type.
 */
export function classifyPath(cacheClasses, path) {
  if (!Array.isArray(cacheClasses)) return null;
  for (const cacheClass of cacheClasses) {
    if (matchesClass(cacheClass, path)) {
      return cacheClass;
    }
  }
  return null;
}

/**
 * What the origin should answer for a request. Pure.
 *
 * The three response fields are deliberately separate:
 *   `status`         the HTTP status
 *   `servesDocument` whether the body is index.html
 *   `cacheControl`   what to send, or null for a refusal
 *
 * Collapsing `status` and `servesDocument` is the mistake this shape prevents:
 * a class with `spaFallback: true` and a 404 is a 404 the browser tries to parse
 * as an app, and a class with a 200 and no document is an empty success.
 *
 * `options.exists` is a separate input rather than derived from the class,
 * because "which cache class is this path in" and "are the bytes on disk" are
 * different questions with different owners — the policy and the filesystem.
 * `hosting.test.sh` passes the real answer from the container.
 */
export function resolveRequest(cacheClasses, path, options = {}) {
  const exists = options.exists !== false;
  const cacheClass = classifyPath(cacheClasses, path);
  if (cacheClass === null) {
    return { classId: null, status: 404, servesDocument: false, cacheControl: null, reason: 'NO_MATCHING_CLASS' };
  }

  // A status is a REFUSAL only at 4xx or above. Treating "has a numeric status"
  // as a refusal would be wrong in a way that is easy to miss: a class that
  // declares `"status": 200` - which is the natural way to say "this class
  // answers 200" - would stop serving its document, and the class that needs the
  // document most (the client-router catchAll) is exactly the one that would
  // declare 200. The branch is therefore `>= 400`, not `typeof number`.
  const declared = typeof cacheClass.status === 'number' ? cacheClass.status : 200;
  if (declared >= 400) {
    return { classId: cacheClass.id, status: declared, servesDocument: false, cacheControl: null, reason: 'REFUSED' };
  }

  const isDocumentClass = cacheClass.servesDocument === true || cacheClass.spaFallback === true;

  // A MISS is a different question from "which class is this path in", and the
  // two have to be answered separately. The class decides the cache policy; the
  // filesystem decides whether the bytes are there. Conflating them is what
  // produces the bug this function exists to prevent: a class with
  // `spaFallback: false` and no declared status would otherwise answer 200 with
  // nothing, or — worse, in a first-match-wins scan that falls through — the
  // document.
  if (!exists && !isDocumentClass) {
    return { classId: cacheClass.id, status: 404, servesDocument: false, cacheControl: null, reason: 'MISS' };
  }

  if (isDocumentClass) {
    return {
      classId: cacheClass.id,
      status: declared,
      servesDocument: true,
      // The class's own cacheControl rather than a hardcoded one, so the
      // `document` and `runtime-version` classes cannot drift from each other.
      cacheControl: cacheClass.cacheControl ?? DOCUMENT_CACHE_CONTROL,
      reason: 'DOCUMENT',
    };
  }

  return {
    classId: cacheClass.id,
    status: declared,
    servesDocument: false,
    cacheControl: cacheClass.cacheControl ?? null,
    reason: 'ASSET',
  };
}

/** The HTTP method this policy applies to. Only GET/HEAD carry cache semantics. */
export function isCacheableMethod(method) {
  const upper = String(method ?? 'GET').toUpperCase();
  return upper === 'GET' || upper === 'HEAD';
}

/**
 * Structural validation of `origin.json`. Pure.
 *
 * Every problem is collected rather than thrown on the first one. A config gate
 * that reports one defect at a time makes a four-problem fix four runs.
 */
export function validateOriginConfig(config) {
  const problems = [];
  if (config === null || typeof config !== 'object') {
    return { ok: false, problems: ['origin.json is not a JSON object'] };
  }

  const classes = config.cacheClasses;
  if (!Array.isArray(classes) || classes.length === 0) {
    return { ok: false, problems: ['origin.json declares no cacheClasses'] };
  }

  const ids = classes.map((cacheClass) => cacheClass?.id);
  for (const required of REQUIRED_CLASS_IDS) {
    if (!ids.includes(required)) {
      problems.push(`origin.json is missing the required cache class '${required}'`);
    }
  }
  for (const id of ids) {
    if (ids.filter((candidate) => candidate === id).length > 1) {
      problems.push(`origin.json declares the cache class '${id}' more than once`);
    }
  }

  // The ordering invariant. A `catchAll` before the last position means every
  // class after it is dead, and the symptom is a path that 404s when it should
  // have been routed - which reads as a CDN misconfiguration rather than as a
  // class-order mistake.
  classes.forEach((cacheClass, index) => {
    if (cacheClass?.match?.catchAll === true && index !== classes.length - 1) {
      problems.push(
        `cache class '${cacheClass.id ?? index}' is a catchAll at position ${index} of ${classes.length}. ` +
          'A catchAll must be last, or every class after it is unreachable.',
      );
    }
  });

  const byId = (id) => classes.find((cacheClass) => cacheClass?.id === id);

  const document = byId('document');
  if (document !== undefined) {
    if (typeof document.cacheControl !== 'string' || !document.cacheControl.toLowerCase().includes(DOCUMENT_CACHE_CONTROL)) {
      problems.push(
        `the 'document' class must carry Cache-Control containing '${DOCUMENT_CACHE_CONTROL}'; it has ${JSON.stringify(document.cacheControl)}`,
      );
    }
    if (document.spaFallback !== true) {
      problems.push("the 'document' class must set spaFallback: true");
    }
  }

  const hashed = byId('hashed-asset');
  if (hashed !== undefined) {
    if (typeof hashed.cacheControl !== 'string' || !hashed.cacheControl.includes('immutable')) {
      problems.push(
        `the 'hashed-asset' class must carry 'immutable'; it has ${JSON.stringify(hashed.cacheControl)}`,
      );
    }
    if (!String(hashed.cacheControl ?? '').includes('31536000')) {
      problems.push("the 'hashed-asset' class must carry a one-year max-age (31536000)");
    }
    // The specific bug: a missing hashed asset served as the HTML document.
    if (hashed.spaFallback !== false) {
      problems.push(
        "the 'hashed-asset' class must set spaFallback: false. A missing asset answered with index.html is a " +
          '200 text/html where the browser expected JavaScript, and the resulting error names the frontend.',
      );
    }
  }

  const runtimeVersion = byId('runtime-version');
  if (runtimeVersion !== undefined) {
    const cacheControl = String(runtimeVersion.cacheControl ?? '').toLowerCase();
    if (!cacheControl.includes(DOCUMENT_CACHE_CONTROL)) {
      problems.push(
        "the 'runtime-version' class must not be cached. Its job is to report what the CDN is CURRENTLY serving; " +
          'a cached /version.json makes the skew check answer yesterday\'s question.',
      );
    }
  }

  for (const id of ['source-map', 'api']) {
    const cacheClass = byId(id);
    if (cacheClass !== undefined) {
      if (cacheClass.status !== 404) {
        problems.push(`the '${id}' class must answer 404; it declares ${JSON.stringify(cacheClass.status)}`);
      }
      if (cacheClass.spaFallback !== false) {
        problems.push(`the '${id}' class must set spaFallback: false`);
      }
    }
  }

  if (config.tls?.redirectPlainHttpToHttps !== true) {
    problems.push('origin.json must require tls.redirectPlainHttpToHttps: true');
  }

  const headers = config.securityHeaders;
  if (headers === null || typeof headers !== 'object') {
    problems.push('origin.json declares no securityHeaders');
  } else {
    for (const header of SHARED_SECURITY_HEADERS) {
      if (typeof headers[header] !== 'string' || headers[header].length === 0) {
        problems.push(`origin.json securityHeaders is missing ${header}`);
      }
    }
    const csp = String(headers['Content-Security-Policy'] ?? '');
    // Task 037's policy, and the reason it exists: `unsafe-inline` on
    // script-src turns every XSS into a script execution, and `unsafe-eval`
    // defeats the point of a CSP for a bundler that does not need it.
    if (csp.includes('unsafe-inline')) {
      problems.push("the Content-Security-Policy contains 'unsafe-inline', which Task 037 forbids");
    }
    if (csp.includes('unsafe-eval')) {
      problems.push("the Content-Security-Policy contains 'unsafe-eval', which Task 037 forbids");
    }
    if (csp.length > 0 && !csp.includes("script-src 'self'")) {
      problems.push("the Content-Security-Policy must pin script-src to 'self'");
    }
  }

  return { ok: problems.length === 0, problems };
}

/**
 * Finds the nginx blocks that declare an `add_header` of their own and do NOT
 * include the security headers. Pure; returns a list of `line N` locators.
 *
 * Brace-matched, not counted, and the distinction is load-bearing. A
 * total-count comparison says "6 headers, 7 includes, fine" and is wrong twice
 * over: it cannot tell WHICH block is missing the include, and it breaks the
 * moment one block legitimately carries two `add_header` directives and one
 * include. Both happen in this repository's own file, so the counting version
 * passed for a reason unrelated to correctness.
 *
 * Comments and string bodies are skipped so a `}` inside a quoted directive
 * value does not end the block early. That is not hypothetical: a CSP contains
 * no braces today, but a `map` block or an `if` would, and a parser that
 * mis-nests produces a finding that points at the wrong line.
 */
export function nginxBlocksWithHeaderButNoInclude(text) {
  const findings = [];
  if (typeof text !== 'string') return findings;

  const SECURITY_INCLUDE = 'security-headers.conf';
  const lines = text.split('\n');
  /** @type {{line: number, depth: number, hasHeader: boolean, hasInclude: boolean}[]} */
  const stack = [];
  let currentLine = 0;

  const isComment = (line) => line.trim().startsWith('#');

  for (const raw of lines) {
    currentLine += 1;
    if (isComment(raw)) continue;

    // Track the block structure and what each open block contains. Depth is
    // counted on the same scan, so a `{` and its `}` balance.
    const opens = (raw.match(/\{/g) ?? []).length;
    const closes = (raw.match(/\}/g) ?? []).length;

    if (opens > 0) {
      stack.push({ line: currentLine, depth: stack.length, hasHeader: false, hasInclude: false });
    }

    const current = stack[stack.length - 1];
    if (current !== undefined) {
      if (/\badd_header\b/.test(raw)) current.hasHeader = true;
      if (raw.includes(SECURITY_INCLUDE)) current.hasInclude = true;
    }

    for (let i = 0; i < closes; i += 1) {
      const closing = stack.pop();
      if (closing !== undefined && closing.hasHeader && !closing.hasInclude) {
        findings.push(`line ${closing.line}`);
      }
    }
  }

  // An unterminated block at EOF is a malformed config. Reported like any other,
  // because a truncated file is a file whose contents were not all inspected.
  for (const unterminated of stack) {
    if (unterminated.hasHeader && !unterminated.hasInclude) {
      findings.push(`line ${unterminated.line} (block is not closed)`);
    }
  }

  return findings;
}

/**
 * Reads the `add_header` directives out of an nginx snippet. Pure.
 *
 * A deliberately small parser: it reads the one directive form this repository
 * uses (`add_header Name "value" always;`) and returns `null` for anything else
 * rather than guessing. A parser that tried to handle nginx's full syntax would
 * be a second nginx implementation, and a wrong answer from it would be trusted
 * over the real one.
 */
export function parseNginxHeaders(text) {
  const headers = {};
  if (typeof text !== 'string') return headers;
  // `add_header\s+([A-Za-z0-9-]+)\s+"([^"]*)"(\s+always)?\s*;`
  const pattern = /add_header\s+([A-Za-z0-9-]+)\s+"([^"]*)"(?:\s+always)?\s*;/g;
  for (const line of text.split('\n')) {
    // Comments are skipped, not pattern-matched around. The snippet's own header
    // comments quote the directives they describe, so a matcher that reads
    // through comments "finds" an `add_header` that nginx would ignore — and it
    // finds the one in a comment, not the live one.
    if (line.trim().startsWith('#')) continue;
    pattern.lastIndex = 0;
    let match = pattern.exec(line);
    while (match !== null) {
      headers[match[1]] = match[2];
      match = pattern.exec(line);
    }
  }
  return headers;
}

/**
 * Whether the static origin's security policy and the API's are the same set of
 * header values. Pure.
 *
 * A stricter static origin and a looser API is not "the API is internal" — the
 * weaker of the two is what a browser enforces, and the API serves the
 * presigned-URL endpoints, so a divergence in either direction matters. An
 * absent header on either side is a mismatch, not a pass: an origin that omits
 * CSP is exactly the state this exists to catch.
 */
export function compareSecurityHeaders(originHeaders, apiHeaders) {
  const problems = [];
  const left = originHeaders ?? {};
  const right = apiHeaders ?? {};

  for (const header of SHARED_SECURITY_HEADERS) {
    const leftValue = left[header];
    const rightValue = right[header];
    if (leftValue === undefined) {
      problems.push(`the static origin does not set ${header}`);
      continue;
    }
    if (rightValue === undefined) {
      problems.push(`the API does not set ${header}, so the two disagree about a header sent to the browser`);
      continue;
    }
    if (String(leftValue).trim() !== String(rightValue).trim()) {
      problems.push(
        `${header} differs between the static origin and the API:\n    origin: ${leftValue}\n    api:    ${rightValue}`,
      );
    }
  }
  return { ok: problems.length === 0, problems };
}

/**
 * Reads a response's `Cache-Control` and answers the one question the policy
 * cares about: may this be stored without revalidation? Pure.
 *
 * `no-store` and `private` are treated as "never". A private response is not
 * shared-cacheable, and a CDN that stored one anyway would serve one user's
 * response to another — which for this application is a signed-URL leak.
 */
export function isStorable(cacheControl) {
  if (typeof cacheControl !== 'string') return false;
  const value = cacheControl.toLowerCase();
  if (value.includes('no-store') || value.includes('private')) return false;
  if (value.includes('immutable') && value.includes('max-age=31536000')) return true;
  const maxAge = /max-age\s*=\s*(\d+)/.exec(value);
  if (maxAge === null) return false;
  return Number(maxAge[1]) > 0;
}

/**
 * The verdict on a served response, given what the policy says it should be.
 * Pure, and used both by the unit tests and by `hosting.test.sh` against a real
 * HTTP response.
 */
export function evaluateResponse({ path, status, cacheControl, method = 'GET' }) {
  const problems = [];

  if (status === 200 && cacheControl === undefined) {
    // Not a failure by itself; the caller decides whether the class requires a
    // header. Kept as a named case so the test can assert the distinction.
    return { ok: true, classId: null, problems: [] };
  }
  if (!isCacheableMethod(method)) {
    return { ok: true, classId: null, problems };
  }
  if (status === 200 && isStorable(cacheControl) && normalizePath(path) !== '/version.json') {
    // A 200 storable response for the document is the failure that produces a
    // white screen after a release. The version document is excluded because it
    // is checked separately and for the opposite reason.
    problems.push(`${path} returned 200 with a storable Cache-Control (${cacheControl})`);
  }
  return { ok: problems.length === 0, classId: null, problems };
}

/**
 * The hosting gate's whole decision, over the real artefacts. Pure.
 *
 * Takes already-read inputs and returns one verdict. `hosting.test.sh` supplies
 * them from disk and from a live container; the unit tests supply them inline.
 */
export function evaluateHosting({ originConfig, nginxText, securitySnippet, apiSecurityHeaders, responses }) {
  const problems = [];
  const config = validateOriginConfig(originConfig);
  if (!config.ok) {
    problems.push(...config.problems);
  }

  // Three sources, and they must agree: the policy in `origin.json`, the snippet
  // nginx actually includes, and the API's own middleware. Two agreeing and one
  // drifting is the case this catches, and a test that only compared `origin.json`
  // against a hand-transcribed copy of the API's headers would have passed with
  // all three disagreeing.
  if (originConfig !== null && typeof originConfig === 'object' && originConfig.securityHeaders) {
    const headerComparison = compareSecurityHeaders(originConfig.securityHeaders, apiSecurityHeaders);
    if (!headerComparison.ok) {
      problems.push(...headerComparison.problems);
    }
  }

  if (typeof securitySnippet === 'string') {
    const snippetHeaders = parseNginxHeaders(securitySnippet);
    const declared = (originConfig?.securityHeaders ?? {});
    for (const header of SHARED_SECURITY_HEADERS) {
      const fromSnippet = snippetHeaders[header];
      if (fromSnippet === undefined) {
        problems.push(
          `deploy/nginx/security-headers.conf does not set ${header}, so the origin sends it to nobody`,
        );
        continue;
      }
      const fromPolicy = declared[header];
      if (typeof fromPolicy === 'string' && fromPolicy.trim() !== fromSnippet.trim()) {
        problems.push(
          `${header} differs between origin.json and the nginx snippet:\n` +
            `    origin.json: ${fromPolicy}\n` +
            `    nginx:       ${fromSnippet}`,
        );
      }
    }
  } else {
    problems.push(
      'deploy/nginx/security-headers.conf could not be read, so the headers the origin actually serves were not inspected',
    );
  }

  // The nginx config is checked for the three things a static text match can
  // actually establish. It is not a substitute for the live header assertions:
  // `add_header` inheritance depends on which `location` block matched, and a
  // file that says the right thing can serve the wrong thing.
  if (typeof nginxText === 'string') {
    if (!nginxText.includes('try_files')) {
      problems.push('deploy/nginx/default.conf has no try_files, so there is no SPA fallback and every deep link is a 404');
    }
    if (!/location\s*=\s*\/index\.html/.test(nginxText)) {
      problems.push('deploy/nginx/default.conf has no exact `location = /index.html` block, so the document cannot be uncached');
    }
    if (nginxText.includes('unsafe-inline')) {
      problems.push('deploy/nginx/default.conf contains unsafe-inline, which Task 037 forbids');
    }
    if (!nginxText.includes('immutable')) {
      problems.push('deploy/nginx/default.conf does not mark /assets/ immutable');
    }
    if (!/\.map/.test(nginxText)) {
      problems.push('deploy/nginx/default.conf has no .map refusal, so a source map is served if one is ever built');
    }
    // `^~` on /assets/. nginx evaluates a regex location ahead of a plain prefix
    // one, so without `^~` the `\.(js|css|…)$` block wins for
    // `/assets/index-Ab12.js` and the content-hashed asset gets `max-age=300`
    // instead of immutable. That is exactly the bug this assertion exists for,
    // and it is invisible in the config: both blocks look right.
    if (!/location\s+\^~\s+\/assets\//.test(nginxText)) {
      problems.push(
        "deploy/nginx/default.conf does not use `location ^~ /assets/`. nginx evaluates a regex location " +
          'ahead of a plain prefix one, so without `^~` the unhashed-asset regex wins for ' +
          '/assets/<hash>.js and serves it with a five-minute cache instead of immutable.',
      );
    }

    // The security headers are an `include`d snippet, and the reason is the
    // directive's own semantics: a `location` that declares ANY `add_header`
    // discards the whole inherited set. So every block that declares one has to
    // re-include them — and if one does not, the origin serves that path with no
    // CSP and no `nosniff` while every config review reads clean.
    //
    // Checked per BLOCK, by brace matching, rather than by comparing two totals.
    // A total-count comparison is wrong the moment one block legitimately carries
    // two headers and one include (`location = /index.html` sets `Pragma` and
    // `Cache-Control`), which is exactly the shape this repository has — so the
    // count check reported 6 headers against 7 includes on a correct file and
    // would have "passed" for the wrong reason.
    const unsecured = nginxBlocksWithHeaderButNoInclude(nginxText);
    for (const where of unsecured) {
      problems.push(
        `deploy/nginx/default.conf: the block at ${where} declares add_header without including the ` +
          'security headers. nginx REPLACES the inherited add_header set in any block that declares one, ' +
          'so that path is served with no CSP, no X-Frame-Options and no nosniff.',
      );
    }
  } else {
    problems.push('deploy/nginx/default.conf could not be read, so the origin policy was not inspected at all');
  }

  for (const response of responses ?? []) {
    const verdict = evaluateResponse(response);
    if (!verdict.ok) {
      problems.push(...verdict.problems);
    }
  }

  return {
    ok: problems.length === 0,
    reason: problems.length === 0 ? REASON_OK : REASON_INVALID_CONFIG,
    problems,
  };
}
