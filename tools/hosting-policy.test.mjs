// The hosting policy's decision layer (Task 043). Run by `npm run test:tools`.
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DOCUMENT_CACHE_CONTROL,
  IMMUTABLE_CACHE_CONTROL,
  REASON_INVALID_CONFIG,
  REQUIRED_CLASS_IDS,
  SHARED_SECURITY_HEADERS,
  classifyPath,
  compareSecurityHeaders,
  evaluateHosting,
  evaluateResponse,
  extensionOf,
  isCacheableMethod,
  isStorable,
  matchesClass,
  nginxBlocksWithHeaderButNoInclude,
  normalizePath,
  parseNginxHeaders,
  resolveRequest,
  validateOriginConfig,
} from './hosting-policy.mjs';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const originJson = JSON.parse(readFileSync(join(REPO_ROOT, 'deploy/cdn/origin.json'), 'utf8'));
const nginxText = readFileSync(join(REPO_ROOT, 'deploy/nginx/default.conf'), 'utf8');
const securitySnippet = readFileSync(join(REPO_ROOT, 'deploy/nginx/security-headers.conf'), 'utf8');
const classes = originJson.cacheClasses;

// The API's own header set, transcribed from
// `src/DubbingPlatform.Api/Middleware/SecurityHeadersMiddleware.cs`. The
// duplication is deliberate and is the thing under test: the static origin and
// the API must send the same policy, and a test that read both files would be
// unable to tell "they agree" from "the test agrees with itself".
const API_SECURITY_HEADERS = {
  'Content-Security-Policy':
    "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'",
  'Referrer-Policy': 'no-referrer',
  'X-Content-Type-Options': 'nosniff',
  'X-Frame-Options': 'DENY',
};

test('the committed origin.json is valid', () => {
  const result = validateOriginConfig(originJson);
  assert.deepEqual(result.problems, []);
  assert.equal(result.ok, true);
});

test('the committed origin.json declares every required class', () => {
  for (const id of REQUIRED_CLASS_IDS) {
    assert.ok(classifyPath(classes, '/') !== null, `no class matches '/' (${id} missing?)`);
    assert.ok(
      classes.some((cacheClass) => cacheClass.id === id),
      `origin.json is missing the '${id}' class`,
    );
  }
});

test('path normalisation strips query, fragment and trailing slash', () => {
  assert.equal(normalizePath('/index.html?x=1'), '/index.html');
  assert.equal(normalizePath('/index.html#top'), '/index.html');
  assert.equal(normalizePath('/projects/'), '/projects');
  assert.equal(normalizePath('projects'), '/projects');
  assert.equal(normalizePath(''), '/');
  assert.equal(normalizePath('/'), '/');
  assert.equal(normalizePath(undefined), '/');
});

test('extensions are read case-insensitively and a dotfile has none', () => {
  assert.equal(extensionOf('/assets/index-AB12.js'), 'js');
  assert.equal(extensionOf('/assets/index-AB12.JS'), 'js');
  assert.equal(extensionOf('/.well-known'), null);
  assert.equal(extensionOf('/noext'), null);
});

test('a class with a catchAll matcher matches everything', () => {
  const catchAll = { id: 'x', match: { catchAll: true }, spaFallback: true };
  for (const path of ['/', '/a', '/a/b/c', '/index.html', '/api/v1/x']) {
    assert.equal(matchesClass(catchAll, path), true, path);
  }
});

test('a class with no matcher does not match unless it is a document class', () => {
  assert.equal(matchesClass({ id: 'x', spaFallback: true }, '/anything'), true);
  assert.equal(matchesClass({ id: 'x', cacheControl: 'public, max-age=300' }, '/anything'), false);
  assert.equal(matchesClass({ id: 'x' }, '/anything'), false);
});

test('a prefix matches the prefix itself as well as children', () => {
  const prefixClass = { id: 'x', match: { pathPrefixes: ['/assets/'] } };
  assert.equal(matchesClass(prefixClass, '/assets/'), true);
  assert.equal(matchesClass(prefixClass, '/assets/a.js'), true);
  assert.equal(matchesClass(prefixClass, '/assetsX/a.js'), false);
});

test('path classification lands each shape in the right class', () => {
  const cases = [
    ['/index.html', 'document'],
    ['/', 'spa-route'],
    ['/projects/prj_1/workspace', 'spa-route'],
    ['/assets/index-Ab12Cd.js', 'hashed-asset'],
    ['/favicon.svg', 'unhashed-asset'],
    ['/version.json', 'runtime-version'],
    ['/service-worker.js', 'service-worker'],
    ['/assets/index-Ab12.js.map', 'source-map'],
    ['/api/v1/projects', 'api'],
  ];
  for (const [path, expected] of cases) {
    assert.equal(classifyPath(classes, path)?.id, expected, path);
  }
});

test('classifyPath returns null rather than a default for an unrouted path', () => {
  // A config with no catchAll must leave a path unrouted, not hand it to the
  // last class. That difference is the difference between a 404 and a 200 of
  // the wrong content type.
  const partial = classes.filter((cacheClass) => cacheClass.id !== 'spa-route');
  assert.equal(classifyPath(partial, '/some/deep/route'), null);
});

test('the document is never storable and a hashed asset always is', () => {
  assert.equal(resolveRequest(classes, '/index.html').cacheControl.includes(DOCUMENT_CACHE_CONTROL), true);
  assert.equal(isStorable(resolveRequest(classes, '/index.html').cacheControl), false);
  assert.equal(resolveRequest(classes, '/assets/a-Bc12.js').cacheControl, IMMUTABLE_CACHE_CONTROL);
  assert.equal(isStorable(resolveRequest(classes, '/assets/a-Bc12.js').cacheControl), true);
});

test('a missing hashed asset is a 404, never the document', () => {
  // The specific bug this policy exists to prevent: 200 text/html where the
  // browser expected JavaScript, producing an error that names the frontend.
  const request = resolveRequest(classes, '/assets/index-DOESNOTEXIST.js', { exists: false });
  assert.equal(request.status, 404);
  assert.equal(request.servesDocument, false);
  assert.equal(request.cacheControl, null);
  assert.equal(request.reason, 'MISS');
});

test('a present hashed asset is served immutably', () => {
  const request = resolveRequest(classes, '/assets/index-Ab12Cd.js', { exists: true });
  assert.equal(request.status, 200);
  assert.equal(request.servesDocument, false);
  assert.equal(request.cacheControl, IMMUTABLE_CACHE_CONTROL);
});

test('a miss in a document class is still the document', () => {
  // `/` and every client-router path are the same request from the server's
  // point of view: no such file, serve index.html. The miss is the normal case
  // for the largest class in the policy, so a blanket "miss means 404" would
  // 404 every deep link.
  const request = resolveRequest(classes, '/projects/prj_1/workspace', { exists: false });
  assert.equal(request.status, 200);
  assert.equal(request.servesDocument, true);
});

test('a miss in the runtime-version class is a 404, not the document', () => {
  // /version.json is not a document class, so a missing one is a genuine 404. A
  // build that predates the file must not be answered with HTML the client will
  // try to parse as JSON.
  const request = resolveRequest(classes, '/version.json', { exists: false });
  assert.equal(request.status, 404);
  assert.equal(request.servesDocument, false);
});

test('the API prefix is a 404 from the static origin, not the document', () => {
  const request = resolveRequest(classes, '/api/v1/projects');
  assert.equal(request.status, 404);
  assert.equal(request.servesDocument, false);
});

test('a source map is a 404', () => {
  const request = resolveRequest(classes, '/assets/index-Ab12.js.map');
  assert.equal(request.status, 404);
  assert.equal(request.servesDocument, false);
});

test('/version.json is served and never storable', () => {
  const request = resolveRequest(classes, '/version.json');
  assert.equal(request.status, 200);
  assert.equal(isStorable(request.cacheControl), false);
  // The skew check reports what the CDN is CURRENTLY serving. A cached
  // /version.json answers yesterday's question.
  assert.equal(request.cacheControl.toLowerCase().includes('no-cache'), true);
});

test('a client-router deep link is served the document', () => {
  const request = resolveRequest(classes, '/projects/prj_01HZY/workspace');
  assert.equal(request.status, 200);
  assert.equal(request.servesDocument, true);
});

test('only GET and HEAD are cacheable methods', () => {
  assert.equal(isCacheableMethod('GET'), true);
  assert.equal(isCacheableMethod('head'), true);
  assert.equal(isCacheableMethod('POST'), false);
  assert.equal(isCacheableMethod('DELETE'), false);
  assert.equal(isCacheableMethod(undefined), true);
});

test('isStorable treats no-store and private as never', () => {
  // A `private` response stored by a shared cache is one user's response served
  // to another, which for this application is a signed-URL leak.
  assert.equal(isStorable('private, max-age=31536000'), false);
  assert.equal(isStorable('no-store'), false);
  assert.equal(isStorable('no-cache'), false);
  assert.equal(isStorable('max-age=0'), false);
  assert.equal(isStorable('public, max-age=300'), true);
  assert.equal(isStorable(undefined), false);
  assert.equal(isStorable('nonsense'), false);
});

test('a 200 document with a storable Cache-Control is a finding', () => {
  const bad = evaluateResponse({ path: '/index.html', status: 200, cacheControl: 'public, max-age=3600' });
  assert.equal(bad.ok, false);
  assert.match(bad.problems[0], /index\.html/);
});

test('a 200 document with no-cache is not a finding', () => {
  assert.equal(evaluateResponse({ path: '/index.html', status: 200, cacheControl: 'no-cache, no-store, must-revalidate' }).ok, true);
});

test('a 404 is never a finding about caching', () => {
  assert.equal(evaluateResponse({ path: '/assets/gone-Bc12.js', status: 404, cacheControl: undefined }).ok, true);
});

test('a non-cacheable method is not judged on Cache-Control', () => {
  const post = evaluateResponse({ path: '/index.html', status: 200, cacheControl: 'public, max-age=3600', method: 'POST' });
  assert.equal(post.ok, true);
});

test('validation rejects a catchAll that is not last', () => {
  // A catchAll in the middle makes every later class dead, and the symptom is a
  // path that 404s when it should have been routed.
  const broken = structuredClone(originJson);
  broken.cacheClasses = [broken.cacheClasses.at(-1), ...broken.cacheClasses.slice(0, -1)];
  const result = validateOriginConfig(broken);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('catchAll must be last')));
});

test('validation rejects a hashed-asset class that falls back to the document', () => {
  const broken = structuredClone(originJson);
  broken.cacheClasses.find((cacheClass) => cacheId(cacheClass) === 'hashed-asset').spaFallback = true;
  const result = validateOriginConfig(broken);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes("hashed-asset' class must set spaFallback: false")));
});

test('validation rejects a cacheable document', () => {
  const broken = structuredClone(originJson);
  broken.cacheClasses.find((cacheClass) => cacheClass.id === 'document').cacheControl = 'public, max-age=300';
  const result = validateOriginConfig(broken);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes("'document' class must carry")));
});

test('validation rejects a cacheable /version.json', () => {
  const broken = structuredClone(originJson);
  broken.cacheClasses.find((cacheClass) => cacheClass.id === 'runtime-version').cacheControl = 'public, max-age=31536000';
  const result = validateOriginConfig(broken);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes("'runtime-version' class must not be cached")));
});

test('validation rejects unsafe-inline and unsafe-eval in the CSP', () => {
  for (const directive of ['unsafe-inline', 'unsafe-eval']) {
    const broken = structuredClone(originJson);
    broken.securityHeaders['Content-Security-Policy'] += `; script-src 'self' '${directive}'`;
    const result = validateOriginConfig(broken);
    assert.equal(result.ok, false, directive);
    assert.ok(result.problems.some((problem) => problem.includes(directive)), directive);
  }
});

test('validation rejects a missing required class', () => {
  const broken = structuredClone(originJson);
  broken.cacheClasses = broken.cacheClasses.filter((cacheClass) => cacheClass.id !== 'runtime-version');
  const result = validateOriginConfig(broken);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes("'runtime-version'")));
});

test('validation rejects a missing security header', () => {
  const broken = structuredClone(originJson);
  delete broken.securityHeaders['X-Frame-Options'];
  const result = validateOriginConfig(broken);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('X-Frame-Options')));
});

test('validation rejects a config with no tls redirect', () => {
  const broken = structuredClone(originJson);
  broken.tls.redirectPlainHttpToHttps = false;
  const result = validateOriginConfig(broken);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('redirectPlainHttpToHttps')));
});

test('validation rejects a non-object and an empty class list', () => {
  assert.equal(validateOriginConfig(null).ok, false);
  assert.equal(validateOriginConfig('nope').ok, false);
  assert.equal(validateOriginConfig({ cacheClasses: [] }).ok, false);
});

test('the static origin and the API send the same security headers', () => {
  const result = compareSecurityHeaders(originJson.securityHeaders, API_SECURITY_HEADERS);
  assert.deepEqual(result.problems, []);
  assert.equal(result.ok, true);
});

test('an absent header on the API side is a mismatch, not a pass', () => {
  // An origin that sets CSP while the API does not is exactly the divergence
  // this is for: the weaker of the two is what a browser enforces.
  const partial = { ...API_SECURITY_HEADERS };
  delete partial['X-Content-Type-Options'];
  const result = compareSecurityHeaders(originJson.securityHeaders, partial);
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('the API does not set X-Content-Type-Options')));
});

test('a differing value is reported with both sides', () => {
  const looser = { ...API_SECURITY_HEADERS, 'Referrer-Policy': 'strict-origin-when-cross-origin' };
  const result = compareSecurityHeaders(originJson.securityHeaders, looser);
  assert.equal(result.ok, false);
  const problem = result.problems.find((entry) => entry.includes('Referrer-Policy'));
  assert.match(problem, /no-referrer/);
  assert.match(problem, /strict-origin-when-cross-origin/);
});

test('every shared header is checked', () => {
  assert.ok(SHARED_SECURITY_HEADERS.includes('Content-Security-Policy'));
  assert.ok(SHARED_SECURITY_HEADERS.includes('Referrer-Policy'));
  assert.ok(SHARED_SECURITY_HEADERS.includes('X-Content-Type-Options'));
  assert.ok(SHARED_SECURITY_HEADERS.includes('X-Frame-Options'));
});

test('the committed nginx config implements the policy', () => {
  // The static half of the gate. The live half is deploy/tests/hosting.test.sh,
  // which curls a real container: `add_header` inheritance depends on which
  // `location` block matched, so a file that says the right thing can serve the
  // wrong thing and only a response proves it.
  const result = evaluateHosting({
    originConfig: originJson,
    nginxText,
    securitySnippet,
    apiSecurityHeaders: API_SECURITY_HEADERS,
    responses: [],
  });
  assert.deepEqual(result.problems, []);
  assert.equal(result.ok, true);
});

test('the committed security snippet parses to the shared header set', () => {
  const parsed = parseNginxHeaders(securitySnippet);
  for (const header of SHARED_SECURITY_HEADERS) {
    assert.equal(parsed[header], API_SECURITY_HEADERS[header], header);
  }
});

test('the nginx snippet, origin.json and the API all agree on every header', () => {
  // Three sources, and the interesting case is two agreeing while the third
  // drifts. Comparing only origin.json against a transcribed copy of the API's
  // headers would pass with all three disagreeing.
  const parsed = parseNginxHeaders(securitySnippet);
  for (const header of SHARED_SECURITY_HEADERS) {
    assert.equal(parsed[header], originJson.securityHeaders[header], `origin.json vs nginx: ${header}`);
    assert.equal(parsed[header], API_SECURITY_HEADERS[header], `nginx vs api: ${header}`);
  }
});

test('a snippet missing a shared header is a finding', () => {
  // The LAST occurrence, not the first. The snippet's own comment block quotes
  // the directive it is explaining, so a `replace` on the first match deletes the
  // line inside a comment and leaves the live directive in place — the fixture
  // edit "succeeds", the parsed headers are unchanged, and the assertion would
  // pass for a reason that has nothing to do with the rule. A slice from the
  // final index is the edit that actually removes the live directive.
  const marker = 'add_header X-Frame-Options "DENY" always;\n';
  const at = securitySnippet.lastIndexOf(marker);
  assert.ok(at > 0, 'the fixture edit found nothing to remove');
  const trimmed = securitySnippet.slice(0, at) + securitySnippet.slice(at + marker.length);
  assert.equal(parseNginxHeaders(trimmed)['X-Frame-Options'], undefined, 'the live directive was not removed');

  const result = evaluateHosting({
    originConfig: originJson,
    nginxText,
    securitySnippet: trimmed,
    apiSecurityHeaders: API_SECURITY_HEADERS,
    responses: [],
  });
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('security-headers.conf does not set X-Frame-Options')));
});

test('an unreadable snippet is a finding', () => {
  const result = evaluateHosting({
    originConfig: originJson,
    nginxText,
    securitySnippet: null,
    apiSecurityHeaders: API_SECURITY_HEADERS,
    responses: [],
  });
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('security-headers.conf could not be read')));
});

test('parseNginxHeaders reads the one directive form and ignores the rest', () => {
  const parsed = parseNginxHeaders('add_header X-A "1" always;\nadd_header X-B "2";\n# add_header X-C "3" always;\nserver_name x;');
  assert.equal(parsed['X-A'], '1');
  assert.equal(parsed['X-B'], '2');
  // A commented-out directive is not a directive. Matching inside comments is
  // what makes a linter that greps for header names report a header that is
  // present only in prose.
  assert.equal(parsed['X-C'], undefined);
});

test('no block in the committed config declares a header without the include', () => {
  assert.deepEqual(nginxBlocksWithHeaderButNoInclude(nginxText), []);
});

test('a block with its own add_header and no include is found, and located', () => {
  // nginx REPLACES the inherited add_header set in any block that declares one,
  // so a block without the include serves with no CSP at all - and every config
  // review reads clean. This is the assertion that catches it structurally.
  const broken = nginxText.replace(
    /location \/api\/ \{\n(\s*)add_header Cache-Control "no-store" always;\n\s*include [^\n]*\n/,
    'location /api/ {\n        add_header Cache-Control "no-store" always;\n',
  );
  assert.notEqual(broken, nginxText, 'the fixture edit did not apply; the assertion below would pass vacuously');

  const found = nginxBlocksWithHeaderButNoInclude(broken);
  assert.equal(found.length, 1);
  assert.match(found[0], /^line \d+$/);

  const result = evaluateHosting({
    originConfig: originJson,
    nginxText: broken,
    securitySnippet,
    apiSecurityHeaders: API_SECURITY_HEADERS,
    responses: [],
  });
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('declares add_header without including')));
  assert.ok(result.problems.some((problem) => problem.includes(found[0])));
});

test('a block with two headers and one include is not a finding', () => {
  // The count-based version of this check reported "6 headers, 7 includes, fine"
  // on the real file and would have failed the moment the counts lined up
  // without the structure being right. `location = /index.html` is exactly this
  // shape: `Cache-Control` AND `Pragma`, and one include.
  //
  // The `server` block below has a header and NO include, so it is a finding in
  // its own right — which is why the assertion is that the LOCATION is absent
  // from the findings, not that the list is empty. In the real file the server
  // block does include, and `no block in the committed config…` covers it.
  const shaped = [
    'server {',
    '    add_header X-Server "1" always;',
    '    location = /a {',
    '        add_header Cache-Control "no-cache" always;',
    '        add_header Pragma "no-cache" always;',
    '        include /etc/nginx/snippets/security-headers.conf;',
    '        try_files $uri =404;',
    '    }',
    '}',
  ].join('\n');
  assert.deepEqual(nginxBlocksWithHeaderButNoInclude(shaped), ['line 1']);
});

test('a server block with a header and no include IS a finding', () => {
  // Asserted explicitly because the fixture above depends on it, and because it
  // is the same bug one level up: the server block is what every block without
  // its own header inherits from.
  const shaped = ['server {', '    add_header X-Server "1" always;', '    location / {', '        try_files $uri /index.html;', '    }', '}'].join('\n');
  assert.deepEqual(nginxBlocksWithHeaderButNoInclude(shaped), ['line 1']);
});

test('a commented-out header or include is not a directive', () => {
  const shaped = [
    'server {',
    '    # add_header X-Commented "1" always;',
    '    location = /a {',
    '        add_header Cache-Control "no-cache" always;',
    '        # include /etc/nginx/snippets/security-headers.conf;',
    '    }',
    '}',
  ].join('\n');
  // The commented include does not count, so the block IS a finding. A parser
  // that matched inside comments would call this clean and report a pass.
  const found = nginxBlocksWithHeaderButNoInclude(shaped);
  assert.equal(found.length, 1);
});

test('an unterminated block is reported rather than silently dropped', () => {
  const truncated = 'server {\n    add_header Cache-Control "no-cache" always;\n';
  const found = nginxBlocksWithHeaderButNoInclude(truncated);
  assert.equal(found.length, 1);
  assert.match(found[0], /not closed/);
});

test('a plain /assets/ prefix without ^~ is a finding', () => {
  // nginx evaluates a regex location ahead of a plain prefix one, so
  // `/assets/<hash>.js` would be handled by the unhashed-asset regex and served
  // with a five-minute cache instead of immutable. Both blocks look right.
  const broken = nginxText.replace('location ^~ /assets/ {', 'location /assets/ {');
  assert.notEqual(broken, nginxText, 'the fixture edit did not apply');
  const result = evaluateHosting({
    originConfig: originJson,
    nginxText: broken,
    securitySnippet,
    apiSecurityHeaders: API_SECURITY_HEADERS,
    responses: [],
  });
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('^~')));
});

test('evaluateHosting reports an unreadable nginx config as a failure', () => {
  const result = evaluateHosting({ originConfig: originJson, nginxText: null, securitySnippet, apiSecurityHeaders: API_SECURITY_HEADERS, responses: [] });
  assert.equal(result.ok, false);
  assert.equal(result.reason, REASON_INVALID_CONFIG);
  assert.ok(result.problems.some((problem) => problem.includes('could not be read')));
});

test('evaluateHosting reports a missing SPA fallback', () => {
  const result = evaluateHosting({ originConfig: originJson, nginxText: 'server { listen 8080; }', securitySnippet, apiSecurityHeaders: API_SECURITY_HEADERS, responses: [] });
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('try_files')));
});

test('evaluateHosting reports unsafe-inline in the served headers', () => {
  // Injected into the SNIPPET, not the site config: since the headers moved into
  // an include, `default.conf` no longer contains the policy at all, and a test
  // that injected there would report PASS having changed nothing.
  const withInline = securitySnippet.replace("script-src 'self'", "script-src 'self' 'unsafe-inline'");
  assert.notEqual(withInline, securitySnippet, 'the fixture edit did not apply; the assertion would pass vacuously');
  const result = evaluateHosting({ originConfig: originJson, nginxText, securitySnippet: withInline, apiSecurityHeaders: API_SECURITY_HEADERS, responses: [] });
  assert.equal(result.ok, false);
  assert.ok(
    result.problems.some((problem) => problem.includes('unsafe-inline')),
    'the snippet/origin.json comparison must notice a widened script-src',
  );
});

test('evaluateHosting still flags unsafe-inline written into the site config', () => {
  // The site config is also read for this, so a header pasted there instead of
  // into the snippet is caught even though the snippet itself is clean.
  const withInline = nginxText.replace('server_tokens off;', "add_header Content-Security-Policy \"default-src 'self' 'unsafe-inline'\" always;");
  const result = evaluateHosting({ originConfig: originJson, nginxText: withInline, securitySnippet, apiSecurityHeaders: API_SECURITY_HEADERS, responses: [] });
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('unsafe-inline')));
});

test('evaluateHosting surfaces a live-response finding', () => {
  const result = evaluateHosting({
    originConfig: originJson,
    nginxText,
    securitySnippet,
    apiSecurityHeaders: API_SECURITY_HEADERS,
    responses: [{ path: '/index.html', status: 200, cacheControl: 'public, max-age=600' }],
  });
  assert.equal(result.ok, false);
  assert.ok(result.problems.some((problem) => problem.includes('index.html')));
});

test('the origin config is not a CHANGE_ME carrier for a deployed field', () => {
  // CHANGE_ME is correct in the repository and wrong in a deployment. The
  // deploy-time substitution is asserted by deploy/verify.sh; here the check is
  // that the placeholder is confined to the host and the version-pin path, and
  // is not sitting in a cache class or a header.
  const serialized = JSON.stringify(originJson);
  assert.ok(serialized.includes('CHANGE_ME'), 'the origin host is expected to be a placeholder in the repository');
  for (const cacheClass of classes) {
    assert.ok(!JSON.stringify(cacheClass).includes('CHANGE_ME'), `cache class ${cacheClass.id} carries a CHANGE_ME`);
  }
  for (const header of SHARED_SECURITY_HEADERS) {
    assert.ok(!String(originJson.securityHeaders[header]).includes('CHANGE_ME'), `${header} carries a CHANGE_ME`);
  }
});

function cacheId(cacheClass) {
  return cacheClass?.id;
}
