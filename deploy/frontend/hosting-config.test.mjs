// The hosting config tests (Task 043A, Testing).
//
// WHAT IS ASSERTED, AND WHY IT IS NOT THE SAME ASSERTION AS THE HOSTING GATE
// ---------------------------------------------------------------------------
// `deploy/tests/hosting.test.sh` builds the image, runs it, and reads the headers
// that actually come back - which is the only form in which nginx's
// `add_header` inheritance can be believed, because it depends on which
// `location` block matched. This file is the cheap half: it reads the committed
// artefacts and the decision rules in `tools/hosting-policy.mjs`, so a mistake
// is caught in a second instead of in a three-minute image build.
//
// The division is deliberate and is the same one `tools/hosting-policy.mjs`
// draws. If these tests were the only ones, a config that says the right thing
// and serves the wrong thing would be green. If the gate were the only one, a
// broken policy would be discovered by a Docker build.
//
// Reading YAML WITHOUT A YAML PARSER
// ----------------------------------
// There is no YAML dependency in this repository's root `package.json`, and
// adding one to assert five fields is a worse trade than a line-based reader
// that reads exactly the fields it asserts and fails loudly on anything it does
// not understand. Schema-level validation belongs to `kubectl apply --dry-run`
// and `kubeconform`, which `deploy/verify.sh` already runs; this file asserts
// the CONTRACT, not the schema.
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  DOCUMENT_CACHE_CONTROL,
  IMMUTABLE_CACHE_CONTROL,
  REQUIRED_CLASS_IDS,
  SHARED_SECURITY_HEADERS,
  classifyPath,
  nginxBlocksWithHeaderButNoInclude,
  parseNginxHeaders,
  resolveRequest,
  validateOriginConfig,
} from '../../tools/hosting-policy.mjs';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (relativePath) => readFileSync(join(REPO_ROOT, relativePath), 'utf8');

const originJson = JSON.parse(read('deploy/cdn/origin.json'));
const nginxText = read('deploy/nginx/default.conf');
const securitySnippet = read('deploy/nginx/security-headers.conf');
const dockerfile = read('Dockerfile.frontend');
const envTs = read('frontend/src/config/env.ts');
const injector = read('deploy/config-inject.sh');
const classes = originJson.cacheClasses;
const byId = (id) => classes.find((cacheClass) => cacheClass?.id === id);

/** The `data:` block of a ConfigMap, as key/value pairs. */
function readConfigMapData(text) {
  const lines = text.split('\n');
  const start = lines.findIndex((line) => line === 'data:');
  assert.notEqual(start, -1, 'the ConfigMap has no top-level `data:` block');
  const data = {};
  for (let i = start + 1; i < lines.length; i += 1) {
    const line = lines[i];
    if (line.length === 0) continue;
    // A line that is not indented ends the block; this file is a single document,
    // so that is also the end of the loop.
    if (!line.startsWith(' ')) break;
    const match = /^ {2}([A-Za-z0-9_.-]+):\s*(.*)$/.exec(line);
    if (match === null) continue;
    data[match[1]] = match[2].replace(/^"|"$/g, '');
  }
  return data;
}

/** Every `annotations:` key of the first document in a manifest. */
function readAnnotations(text) {
  const lines = text.split('\n');
  const start = lines.findIndex((line) => line === '  annotations:');
  assert.notEqual(start, -1, 'the manifest has no `annotations:` block');
  const annotations = {};
  for (let i = start + 1; i < lines.length; i += 1) {
    const line = lines[i];
    if (!line.startsWith('    ')) break;
    const match = /^ {4}([A-Za-z0-9._/-]+):\s*(.*)$/.exec(line);
    if (match === null) continue;
    annotations[match[1]] = match[2].replace(/^"|"$/g, '');
  }
  return annotations;
}

// ---------------------------------------------------------------------------
// R1 / R2 - the cache classes and the fallback
// ---------------------------------------------------------------------------

test('the committed origin.json is valid and declares every required class', () => {
  const result = validateOriginConfig(originJson);
  assert.deepEqual(result.problems, []);
  for (const id of REQUIRED_CLASS_IDS) {
    assert.ok(byId(id) !== undefined, `origin.json is missing the '${id}' class`);
  }
});

test('a deep link lands in the SPA class and is answered with the document', () => {
  // R1. The single most valuable assertion in this file: it is the one that fails
  // if the SPA fallback is removed, and a removed fallback is invisible until
  // somebody shares a link.
  for (const path of ['/projects/prj_01HZYABCDEFG/workspace', '/admin/ops', '/exports/exp_1/download']) {
    assert.equal(classifyPath(classes, path)?.id, 'spa-route', path);
    const request = resolveRequest(classes, path, { exists: false });
    assert.equal(request.status, 200, path);
    assert.equal(request.servesDocument, true, path);
    assert.ok(
      String(request.cacheControl).includes(DOCUMENT_CACHE_CONTROL),
      `${path} is served with Cache-Control '${request.cacheControl}'`,
    );
  }
});

test('a hashed asset is immutable for a year, and a miss is a 404 not the document', () => {
  const request = resolveRequest(classes, '/assets/index-Ab12Cd.js', { exists: true });
  assert.equal(request.cacheControl, IMMUTABLE_CACHE_CONTROL);
  const miss = resolveRequest(classes, '/assets/index-Ab12Cd.js', { exists: false });
  assert.equal(miss.status, 404);
  assert.equal(miss.servesDocument, false);
});

test('/healthz is never stored and is never answered with the document', () => {
  // The liveness probe, the readiness probe and the image HEALTHCHECK all use it.
  // `exists: false` is the interesting case: a probe handed the HTML document is
  // a probe that cannot tell a working origin from a broken one.
  assert.equal(classifyPath(classes, '/healthz')?.id, 'healthz');
  const found = resolveRequest(classes, '/healthz', { exists: true });
  assert.equal(found.status, 200);
  assert.equal(found.servesDocument, false);
  assert.ok(String(found.cacheControl).includes('no-store'));
  const missing = resolveRequest(classes, '/healthz', { exists: false });
  assert.equal(missing.status, 404);
  assert.equal(missing.servesDocument, false);
});

test('/version.json is never stored', () => {
  const request = resolveRequest(classes, '/version.json', { exists: true });
  assert.ok(
    String(request.cacheControl).includes('no-store'),
    `a cached /version.json answers yesterday's question, got '${request.cacheControl}'`,
  );
});

test('the cache-class order is nginx resolution order, not a first-match scan', () => {
  // A `catchAll` before the last position makes every later class dead, and the
  // symptom is a path that 404s when it should have been routed.
  const catchAllIndex = classes.findIndex((cacheClass) => cacheClass?.match?.catchAll === true);
  assert.equal(catchAllIndex, classes.length - 1);
  // `/version.json` matches the `json` extension class too, so the exact-path
  // class has to come first. This is the ordering bug 043 found and it is
  // invisible in the array unless you know which classes overlap.
  assert.ok(
    classes.findIndex((cacheClass) => cacheClass.id === 'runtime-version') <
      classes.findIndex((cacheClass) => cacheClass.id === 'unhashed-asset'),
  );
});

// ---------------------------------------------------------------------------
// R2 - headers, compression, and the two traps in default.conf
// ---------------------------------------------------------------------------

test('the security snippet is the single copy of the header set', () => {
  const headers = parseNginxHeaders(securitySnippet);
  for (const header of SHARED_SECURITY_HEADERS) {
    assert.ok(typeof headers[header] === 'string' && headers[header].length > 0, `${header} is not set`);
  }
  assert.equal(headers['X-Content-Type-Options'], 'nosniff');
  assert.ok(headers['Content-Security-Policy'].includes("script-src 'self'"));
  assert.ok(!headers['Content-Security-Policy'].includes('unsafe-inline'));
  assert.ok(!headers['Content-Security-Policy'].includes('unsafe-eval'));
});

test('every nginx block that declares a header also includes the snippet', () => {
  // nginx REPLACES the inherited add_header set in any block that declares one,
  // so a block without the include serves its path with no CSP and no nosniff
  // while the file reads correctly. Checked per block by brace matching.
  assert.deepEqual(nginxBlocksWithHeaderButNoInclude(nginxText), []);
});

test('the origin config has no brotli directive, because the image has no brotli module', () => {
  // `nginx -V` against nginxinc/nginx-unprivileged:1.27-alpine reports
  // --with-http_gzip_static_module and no --with-http_brotli_module. A `brotli
  // on;` line is an unknown directive and nginx refuses to start on it, so this
  // assertion exists to stop the instinct on reading "the task asks for brotli".
  assert.ok(!/^\s*brotli\s+[a-z_]+\s*;/m.test(nginxText));
  assert.equal(originJson.compression.brotli.atOrigin, false);
  assert.equal(originJson.compression.brotli.atEdge, true);
});

test('compression is configured on both the origin and the edge, from real files', () => {
  assert.match(nginxText, /gzip\s+on\s*;/);
  assert.match(nginxText, /gzip_static\s+on\s*;/);
  // `gzip_static` is a no-op unless the build wrote the siblings, so the pair is
  // asserted together: a Dockerfile that stopped precompressing would leave a
  // directive in the config that does nothing, which reads as "compression is on".
  assert.match(dockerfile, /\.gz/);
  assert.match(dockerfile, /\.br/);
  assert.ok(originJson.compression.precompressedSiblings.enabled);
  for (const suffix of [originJson.compression.precompressedSiblings.gzipSuffix, originJson.compression.precompressedSiblings.brotliSuffix]) {
    assert.ok(
      originJson.compression.precompressedSiblings.types.some((type) => type.endsWith(suffix.replace('.gz', '.js').replace('.br', '.js'))),
      `no precompressed type declared for ${suffix}`,
    );
  }
});

test('/healthz exists as an exact-match block, and every probe uses it', () => {
  // Without the block, `location /` answers /healthz with index.html and a 200,
  // so every probe passes while measuring nothing.
  assert.match(nginxText, /location\s*=\s*\/healthz/);
  assert.match(nginxText, /return 200 '\{"/);
  const deployment = read('deploy/k8s/frontend/deployment.yaml');
  const probes = deployment.match(/httpGet: \{path: (\S+?), port: http\}/g) ?? [];
  assert.equal(probes.length, 2, 'both the liveness and the readiness probe must use a named port path');
  for (const probe of probes) {
    assert.match(probe, /path: \/healthz/);
  }
  assert.match(dockerfile, /127\.0\.0\.1:8080\/healthz/);
});

// ---------------------------------------------------------------------------
// R2 / R5 - the manifests
// ---------------------------------------------------------------------------

const MANIFESTS = [
  'deploy/k8s/frontend/deployment.yaml',
  'deploy/k8s/frontend/service.yaml',
  'deploy/k8s/frontend/ingress.yaml',
  'deploy/k8s/frontend/configmap.yaml',
];

test('all four manifests exist and are non-empty', () => {
  for (const relativePath of MANIFESTS) {
    const full = join(REPO_ROOT, relativePath);
    assert.ok(existsSync(full), `${relativePath} is missing`);
    assert.ok(read(relativePath).trim().length > 0, `${relativePath} is empty`);
  }
});

test('the deployment is two or more replicas, pinned to the port the image serves', () => {
  const deployment = read('deploy/k8s/frontend/deployment.yaml');
  const replicas = Number(/^ {2}replicas: (\d+)$/m.exec(deployment)?.[1]);
  assert.ok(Number.isInteger(replicas) && replicas >= 2, `replicas is ${replicas}`);
  assert.match(deployment, /containerPort: 8080/);
  assert.match(deployment, /readOnlyRootFilesystem: true/);
  assert.match(deployment, /runAsUser: 101/);
  assert.match(deployment, /maxUnavailable: 0/);
});

test('the deployment does not pretend the ConfigMap is runtime configuration', () => {
  // Vite inlines VITE_* at BUILD time. A ConfigMap applied after the build cannot
  // change one byte of the bundle, and a manifest that mounts one is a ConfigMap
  // an operator changes and then observes no effect from.
  //
  // Comments are stripped first, and deliberately: this file's own deployment
  // explains the decision in a comment that necessarily NAMES `envFrom`, and a
  // check that matched the word would be asserting against the documentation of
  // the very thing it forbids.
  const manifest = read('deploy/k8s/frontend/deployment.yaml').replace(/^\s*#.*$/gm, '');
  assert.ok(!/envFrom/.test(manifest), 'the deployment must not have envFrom');
  assert.ok(!/frontend-build-config/.test(manifest), 'the deployment must not reference frontend-build-config');
  assert.ok(!/^\s+env:/m.test(manifest), 'the deployment must not declare a runtime env block');
});

test('the Service is ClusterIP, so the pods have no address outside the cluster', () => {
  // A NodePort or LoadBalancer here would be a second public route to the
  // document: same bytes, plain HTTP, no edge policy.
  const service = read('deploy/k8s/frontend/service.yaml');
  assert.match(service, /type: ClusterIP/);
  assert.ok(!/type: (?:NodePort|LoadBalancer)/.test(service));
  assert.match(service, /targetPort: 8080/);
});

test('the ingress terminates TLS and redirects plain HTTP, and names no certificate material', () => {
  const ingress = read('deploy/k8s/frontend/ingress.yaml');
  const annotations = readAnnotations(ingress);
  assert.ok(ingress.includes('kind: Ingress'));
  assert.match(ingress, /^\s+tls:/m);
  assert.match(ingress, /secretName: /);
  // A committed private key, in any form, in any file under deploy/.
  assert.ok(!/BEGIN [A-Z ]*PRIVATE KEY/.test(ingress));
  for (const annotation of [
    'nginx.ingress.kubernetes.io/ssl-redirect',
    'nginx.ingress.kubernetes.io/force-ssl-redirect',
    'nginx.ingress.kubernetes.io/hsts',
    'nginx.ingress.kubernetes.io/hsts-max-age',
  ]) {
    assert.ok(annotations[annotation] !== undefined, `${annotation} is not set on the ingress`);
  }
  assert.equal(annotations['nginx.ingress.kubernetes.io/ssl-redirect'], 'true');
  assert.equal(annotations['nginx.ingress.kubernetes.io/force-ssl-redirect'], 'true');
  assert.equal(annotations['nginx.ingress.kubernetes.io/hsts'], 'true');
  assert.equal(annotations['nginx.ingress.kubernetes.io/hsts-include-subdomains'], 'true');
  // `preload` is effectively irreversible for a domain and is not this
  // repository's decision to make.
  assert.equal(annotations['nginx.ingress.kubernetes.io/hsts-preload'], 'false');
  assert.equal(annotations['nginx.ingress.kubernetes.io/enable-cors'], 'false');
});

test('the ingress declares no snippet annotation, which a default controller would ignore', () => {
  // ingress-nginx 1.9+ ships with allow-snippet-annotations: false, so a header
  // set declared in a snippet stops being applied on a controller upgrade - with
  // no error anywhere. The single copy of the header set is the origin's
  // security-headers.conf, and it is asserted to be one copy above.
  const annotations = readAnnotations(read('deploy/k8s/frontend/ingress.yaml'));
  for (const name of Object.keys(annotations)) {
    assert.ok(
      !/-(?:configuration|server|stream)-snippet$/.test(name),
      `${name} is a snippet annotation; it is disabled by default since ingress-nginx 1.9`,
    );
  }
});

test('the ingress binds the origin hostname, so it is not a way around the CDN', () => {
  const ingress = read('deploy/k8s/frontend/ingress.yaml');
  const hosts = [...ingress.matchAll(/CHANGE_ME-frontend-origin[A-Za-z0-9.-]*/g)].map((match) => match[0]);
  assert.ok(hosts.length >= 2, 'the rule host and the TLS host must both name the origin');
  assert.equal(new Set(hosts).size, 1, 'the rule host and the tls host must be the same name');
  assert.ok(!/host: (?!CHANGE_ME-frontend-origin)/m.test(ingress.replace(/^\s*#.*$/gm, '')));
});

// ---------------------------------------------------------------------------
// R3 - the env allowlist, and the ConfigMap's relationship to it
// ---------------------------------------------------------------------------

/** `DEPLOY_CONFIG_ALLOWLIST` read from the TypeScript source of truth. */
function readAllowlist() {
  const block = /DEPLOY_CONFIG_ALLOWLIST\s*=\s*\[([\s\S]*?)\]\s*as const/.exec(envTs);
  assert.ok(block !== null, 'could not read DEPLOY_CONFIG_ALLOWLIST');
  return [...block[1].matchAll(/'([A-Z0-9_]+)'/g)].map((match) => match[1]);
}

test('the ConfigMap declares only allowlisted VITE_* keys, and declares the seven the task names', () => {
  // R3. An allowlist rather than a blacklist: a key nobody has decided is safe is
  // refused whether or not its name looks dangerous.
  const allowlist = readAllowlist();
  const data = readConfigMapData(read('deploy/k8s/frontend/configmap.yaml'));
  const keys = Object.keys(data);
  assert.ok(keys.length > 0);
  for (const key of keys) {
    assert.ok(key.startsWith('VITE_'), `${key} is not a VITE_* variable`);
    assert.ok(allowlist.includes(key), `${key} is not on DEPLOY_CONFIG_ALLOWLIST`);
  }
  for (const required of [
    'VITE_API_BASE_URL',
    'VITE_ENVIRONMENT',
    'VITE_APP_VERSION',
    'VITE_ENABLE_ANALYTICS',
    'VITE_ENABLE_DIAGNOSTICS',
    'VITE_ENABLE_EXPERIMENTAL_FEATURES',
    'VITE_SENTRY_DSN',
  ]) {
    assert.ok(keys.includes(required), `the ConfigMap does not declare ${required}`);
  }
});

test('the ConfigMap carries no secret', () => {
  const data = readConfigMapData(read('deploy/k8s/frontend/configmap.yaml'));

  // Duplicated from SECRET_VALUE_PATTERNS in frontend/src/config/env.ts on
  // purpose: this is a second reader of the same shapes, and a check that only
  // existed in the TypeScript would not run against a YAML file at all. The ids
  // are cross-checked against the TypeScript's list in the next test so the two
  // cannot drift apart silently.
  const shapes = [
    [/-----BEGIN [A-Z ]*PRIVATE KEY-----/, 'a PEM private key block'],
    [/\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}/, 'a JWT'],
    [/(?:Host|Server|Data Source)\s*=\s*[^;\s]+;[^;]*(?:Password|Pwd)\s*=/i, 'a connection string with a password'],
    [/amqps?:\/\/[^\s:@/]+:[^\s:@/]+@/i, 'a broker URI with inline credentials'],
    [/s3a?:\/\/[^\s:@/]+:[^\s:@/]+@/i, 'an object-storage URI with inline credentials'],
    [/(?:AKIA|ASIA)[0-9A-Z]{16}/, 'an AWS access key id'],
    [/\bBearer\s+[A-Za-z0-9._~+/-]{20,}=*/, 'a bearer credential'],
  ];
  const secretNames = /SECRET|KEY|TOKEN|PASSWORD|PASSWD|CREDENTIAL|PRIVATE|SIGNING|PASSPHRASE/;
  for (const [key, value] of Object.entries(data)) {
    assert.ok(!secretNames.test(key), `${key} has a secret-shaped name`);
    assert.ok(value.length > 0, `${key} is empty; an empty injected value passes a "is it configured?" check`);
    for (const [pattern, label] of shapes) {
      assert.ok(!pattern.test(value), `${key} carries ${label}`);
    }
  }
});

test('the secret-shape catalogue and this file know the same set of rules', () => {
  // The tie between the two readers. If a rule is added to
  // `SECRET_VALUE_PATTERNS` and not here, the ConfigMap check silently stops
  // covering it - which is the failure mode a duplicated list always has unless
  // something compares the two.
  const ids = [...envTs.matchAll(/id: '([a-z0-9-]+)'/g)].map((match) => match[1]);
  for (const id of ['pem-block', 'jwt', 'connection-string', 'rabbit-uri', 's3-uri', 'aws-access-key', 'bearer-credential']) {
    assert.ok(ids.includes(id), `SECRET_VALUE_PATTERNS no longer declares '${id}'`);
  }
  assert.ok(!/\bentropy\b/i.test(envTs.split('SECRET_VALUE_PATTERNS')[1]?.slice(0, 900) ?? ''), 'entropy must stay out of the rules');
});

test('the shell allowlist in config-inject.sh matches the TypeScript one', () => {
  // Duplication needs a check, not a comment. `scripts/vite-env-audit.sh
  // --allowlist-sync` is the runtime form of this; the ConfigMap is checked here.
  const shellList = injector
    .split('\n')
    .filter((line) => /^ {2}VITE_[A-Z0-9_]+$/.test(line))
    .map((line) => line.trim());
  assert.deepEqual(shellList, readAllowlist());
});

// ---------------------------------------------------------------------------
// R5 - the version document's shape
// ---------------------------------------------------------------------------

test('/version.json carries the {version, commit, builtAt} names the hosting contract declares', () => {
  // Added ALONGSIDE release/builtAtUtc rather than replacing them, so an older
  // client strips what it does not know and still parses. Both spellings are
  // written from the same two values, so there is no second source to drift.
  assert.match(injector, /\\"version\\": \\\"\$APP_VERSION\\\"/);
  assert.match(injector, /\\"builtAt\\": \\\"\$BUILT_AT\\\"/);
  assert.match(injector, /\\"release\\": \\\"\$RELEASE\\\"/);
  assert.match(injector, /\\"builtAtUtc\\": \\\"\$BUILT_AT\\\"/);
  // The client requires all six, so a CDN still serving a four-field document is
  // UNKNOWN rather than a false MATCH.
  for (const field of ['release', 'version', 'commit', 'openapiVersion', 'builtAtUtc', 'builtAt']) {
    assert.match(envTs, new RegExp(`\\n  ${field}: z\\.string\\(\\)`), `the client schema does not require ${field}`);
  }
  // And the document is served uncached, which is what makes the comparison mean
  // anything at all.
  assert.match(nginxText, /location = \/version\.json/);
});

test('the version document is staged where the build cannot delete it', () => {
  // `vite build` empties `outDir` by default. The document used to be written
  // straight into `frontend/dist/`, so the build DELETED it and every release
  // image shipped without one - while the hosting gate reported the resulting 404
  // as "expected for an image built without config-inject.sh", which is exactly
  // what it looked like. A skew check that can never read the document reports
  // nothing and looks healthy.
  //
  // So the injector stages it beside the env file and the Dockerfile copies it in
  // afterwards. Both halves are asserted: a file moved without the copy, or a
  // copy that runs before the build, is the same defect again.
  assert.match(injector, /VERSION_FILE="\$ROOT\/frontend\/version\.json"/);
  assert.ok(
    !/VERSION_FILE="\$ROOT\/frontend\/dist/.test(injector),
    'the document must not be written into dist/, which vite build empties',
  );
  const buildIndex = dockerfile.indexOf('RUN npm run build');
  const copyIndex = dockerfile.indexOf('cp version.json dist/version.json');
  assert.ok(buildIndex > 0 && copyIndex > 0, 'the Dockerfile must build and must copy the document');
  assert.ok(copyIndex > buildIndex, 'the document must be copied AFTER npm run build, not before it');
  // And it stays generated: a committed one would record one developer's commit
  // sha and origin and would be served to every user who loads the app.
  assert.match(read('.gitignore'), /^frontend\/version\.json$/m);
});

// ---------------------------------------------------------------------------
// R2 - the image
// ---------------------------------------------------------------------------

test('the frontend image is multi-stage and carries the config it is built from', () => {
  const stages = [...dockerfile.matchAll(/^FROM /gm)].length;
  assert.ok(stages >= 2, `expected a multi-stage build, found ${stages} FROM lines`);
  assert.match(dockerfile, /COPY deploy\/nginx\/default\.conf/);
  assert.match(dockerfile, /COPY deploy\/nginx\/security-headers\.conf/);
  // A runtime stage that carried node_modules would be a package tree to reason
  // about and a scan surface, for a workload that serves files.
  assert.match(dockerfile, /FROM scratch AS dist/);
});

test('the edge arrangement claims one public route, and the claim has a manifest behind it', () => {
  assert.equal(originJson.edge.singlePublicRouteToTheDocument.value, true);
  assert.equal(originJson.edge.headersAreNotDeclaredOnTheIngress.value, true);
  const enforced = originJson.edge.singlePublicRouteToTheDocument.enforcedBy.join('\n');
  for (const expected of ['ClusterIP', 'static-allow', 'ingress.yaml']) {
    assert.ok(enforced.includes(expected), `the claim does not name ${expected}`);
  }
  // The claim is only true because the NetworkPolicy says so.
  const policies = read('deploy/k8s/networkpolicies.yaml');
  assert.match(policies, /name: static-allow/);
  const staticPolicy = policies.slice(policies.indexOf('name: static-allow'));
  assert.match(staticPolicy, /matchLabels: \{name: cdn-edge\}/);
  assert.match(staticPolicy, /matchLabels: \{name: ingress-nginx\}/);
});
