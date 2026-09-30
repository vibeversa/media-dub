# Task 043A - Frontend Hosting, Env Config, CDN

## Status

**COMPLETED.** All five instructions are implemented, the Testing block exists
(`deploy/frontend/*.test.mjs`, 43 tests), and the `Validation` block's four
commands pass **verbatim** - including `kubectl apply --dry-run=client`, which
was proved against a live kind control plane rather than argued about.

**Four defects were found by running the thing, and all four are fixed.** Three
are pre-existing and were invisible to review:

1. **`/version.json` could never ship.** `deploy/config-inject.sh` wrote it into
   `frontend/dist/`, and `vite build` **empties `outDir`** - so the build deleted
   it and every release image shipped without one. The hosting gate reported the
   resulting 404 as *"as expected for an image built without
   `config-inject.sh`"*, which was indistinguishable from the local case,
   because it *was* the release case.
2. **The `connection-string` secret rule never matched a realistic connection
   string.** Its middle segment was `[^;]*`, so it required the password to be
   the *second* `;`-separated pair. Its own test passed anyway, because
   `inline-credential` also matches `;Password=` and the assertion was on the
   `SECRET_SHAPED_VALUE` *reason*, never on which rule fired.
3. **The kustomize overlays have never built.** The default load restrictor
   rejects every `..` resources entry, and independently the overlay patches do
   not match their targets (base manifests hard-code `namespace: dubbing-prod`,
   the patches declare none).
4. `maskComments` used `break` instead of `continue`, so the first line comment
   in a file abandoned the rest of it. Caught in the same commit that added it,
   by the test that has both a line comment and a block comment.

## Summary

The frontend is now a static SPA behind `deploy/k8s/frontend/` - a 2-replica
`ClusterIP` origin, a TLS-terminating ingress for the **origin** hostname, and a
build-input ConfigMap - with `scripts/check-frontend-topology.mjs` as the R4 gate
and 43 config tests asserting the cache classes, header set, SPA fallback, env
allowlist and ingress annotations over the committed artefacts. Two findings
shaped the work: the origin image (`nginxinc/nginx-unprivileged:1.27-alpine`) is
built `--with-http_gzip_static_module` and **not**
`--with-http_brotli_module`, so brotli is a CDN responsibility and a
`brotli on;` line would stop the container starting; and ingress-nginx has **no
first-class annotation for an arbitrary response header** (both `configuration-snippet`
and `more-headers` are disabled by default since 1.9), so the single copy of the
header set stays in `deploy/nginx/security-headers.conf` and the ingress
deliberately declares none. The hosting gate now asserts 30 live response
properties against a running container, up from 23.

## Files Created/Modified

### The manifests (instruction 2)

| File | What it is |
| --- | --- |
| `deploy/k8s/frontend/deployment.yaml` | **New.** The origin. 2 replicas, `maxUnavailable: 0`, port 8080, liveness+readiness on `/healthz`, `readOnlyRootFilesystem`, uid 101. Documents that it has **no** `env`/`envFrom`. |
| `deploy/k8s/frontend/service.yaml` | **New.** `ClusterIP`, `targetPort: 8080`. The property that keeps the pods unreachable from outside the cluster. |
| `deploy/k8s/frontend/ingress.yaml` | **New.** TLS for the origin hostname, `ssl-redirect` + `force-ssl-redirect`, HSTS (preload `false`), `enable-cors: false`, `server-tokens: false`, cert-manager issuer. No snippet annotations, with the reason. |
| `deploy/k8s/frontend/configmap.yaml` | **New.** The 7 `VITE_*` keys 043A names, all `CHANGE_ME`/inert. Records the **build-arg vs runtime** decision and why it is not mounted. |
| `deploy/k8s/static-deployment.yaml` | **Deleted.** Superseded; split into the four files above. |
| `deploy/k8s/networkpolicies.yaml` | **Modified.** `static-allow` gains `ingress-nginx` as the origin-side hop (two namespaceSelector peers, no `ipBlock`, no bare `podSelector`). |
| `deploy/k8s/overlays/{staging,prod}/kustomization.yaml` | **Modified.** Add the four frontend resources, the `dubbing-frontend` image pin, `replicas: frontend: 2`, and the new patches. 043 had added the origin but **not** to the overlays, so it could never be environment-specific. |
| `deploy/k8s/overlays/staging/frontend-{configmap,ingress}-patch.yaml`, `.../prod/frontend-configmap-patch.yaml` | **New.** Staging host + `letsencrypt-staging` issuer; per-environment `VITE_*`. |

### The image and the origin config (instruction 1)

| File | What it is |
| --- | --- |
| `Dockerfile.frontend` | **Modified.** Precompress pass (`.gz` + `.br`, `node:zlib`, assets > 4 KiB) in a `<<'PRECOMPRESS'` heredoc; `version.json` copied into `dist/` **after** the build with an `if` that logs which case it was; `HEALTHCHECK` on `/healthz`. |
| `deploy/nginx/default.conf` | **Modified.** `location = /healthz` (static 200, `no-store`, `access_log off`); `gzip_static on;`; the brotli-absence comment with the `nginx -V` evidence. |
| `deploy/cdn/origin.json` | **Modified.** New `healthz` cache class; `edge` section (the four hops, the single-public-route claim and what enforces it); `compression.precompressedSiblings` and `compression.brotli`. |
| `tools/hosting-policy.mjs` | **Modified.** `NEVER_STORED_CLASS_IDS`; `healthz` in `REQUIRED_CLASS_IDS`; validation of the `healthz` class; three new nginx assertions (`location = /healthz`, `gzip_static on`, **no `brotli` directive**). |
| `deploy/cdn/viewer-request.security-headers.js` | Unchanged - the HTTPS redirect is asserted to live here, and the ingress redirect is the origin-side hedge. |

### The env configuration (instruction 2, R3)

| File | What it is |
| --- | --- |
| `frontend/src/config/env.ts` | **Modified.** Allowlist 6 → 11 keys; `SENTRY_DISABLED`; `readBooleanFlag`, `isSentryDsnConfigured`; `RuntimeVersion` gains `version`/`builtAt` (**required**); `DeployConfig` gains `environment`, three flags, `sentryDsn`. **Bug fix:** the `connection-string` regex. |
| `frontend/src/config/__tests__/env.test.ts` | **Modified.** `ALLOWLIST_VALUES` for the 5 new keys; a new describe for the flag/DSN helpers; a test that asserts the **rule id** per secret shape; a test that a passwordless connection string is not a secret. |
| `frontend/src/hooks/__tests__/useVersionWatch.test.tsx` | **Modified.** The `SERVED` fixture gains `version`/`builtAt` - without them `parseRuntimeVersion` throws and 5 tests assert a banner that is correctly not shown. |
| `frontend/.env.example` | **Modified.** The 5 new keys. Still `CHANGE_ME`-free (asserted). |
| `deploy/config-inject.sh` | **Modified.** Allowlist + the 5 keys; `--env` is the authority for `VITE_ENVIRONMENT` and disagreement is an error; flags constrained to `true`/`false`; DSN constrained; **stages `frontend/version.json` outside `dist/`**; `--out-dir` removed (a flag that no longer does anything is worse than none). |
| `scripts/vite-env-audit.sh` | **Modified.** The `connection-string` regex, with the reason. |
| `deploy/frontend/hosting-config.test.mjs` | **New.** 25 tests. |
| `scripts/check-frontend-topology.mjs` | **New.** 13 rules + the two URL rules; pure module + thin CLI. |
| `deploy/frontend/topology.test.mjs` | **New.** 18 tests: one fixture per rule (so no rule is unproven), the shapes that must stay legal, and the real tree. |
| `deploy/frontend/README.md` | **New.** Diagram, cache table, env table, SPA fallback, compression, health/version, gates, rollback pointer. |
| `scripts/check-frontend-topology.mjs` wiring | `package.json`: `test:tools` now also globs `deploy/frontend/*.test.mjs`; new `check:frontend` and `check:frontend-topology`. |
| `.github/workflows/basic-ci.yml` | **Modified.** Two steps in `frontend-basic`: the topology gate and the hosting config tests. Both need only node, so they belong in the early gate. |
| `docs/ci-branch-protection.md` | **Modified.** §2 gains the `FRONTEND_TOPOLOGY_RESULT` vocabulary. |
| `docs/topology.md`, `deploy/README.md` | **Modified.** The new hop, and the deploy step. |
| `deploy/verify.sh` | **Modified.** Manifest map → the new paths; the static-origin contract now asserts ClusterIP + exactly one base Ingress + `/healthz` probes + no runtime env; `os.walk`; a new **overlay-integrity** check; the kustomize load-restrictor flag. |
| `deploy/tests/hosting-topology.py` | **Modified.** `os.walk`; `static-allow` peers are now a named allowlist rather than "only the CDN". |
| `deploy/tests/hosting.test.sh` | **Modified.** `/healthz` rows; the `.gz`/`.br` sibling checks; the sibling's cache class; the `{version,commit,builtAt}` field check; `probe` gains `want-body=no`. |
| `.gitignore` | **Modified.** `frontend/version.json` ignored, with the reason. |

## Decisions Made

1. **The ingress is the ORIGIN-side hop, not a second public route.** 043A
   requires `ingress.yaml` (TLS, frontend host) and 043 deliberately had none.
   Both are satisfied by putting the ingress *behind* the CDN: the Service is
   still `ClusterIP`, `static-allow` admits `cdn-edge` + `ingress-nginx` and
   nothing else, and the ingress binds the **origin** hostname rather than the
   public one. Three assertions enforce it (`deploy/verify.sh`,
   `hosting-config.test.mjs`, `hosting-topology.py`).
2. **No header set on the ingress.** ingress-nginx has no first-class
   arbitrary-header annotation and both workarounds are disabled by default since
   1.9. A manifest that depends on a disabled feature silently stops applying its
   security policy on a controller upgrade. The single copy stays in
   `deploy/nginx/security-headers.conf`.
3. **brotli at the edge, and the absence is asserted in code.** `nginx -V`
   against the image is the evidence. `tools/hosting-policy.mjs` fails on any
   `brotli` directive in `default.conf` with that explanation, because the
   instinct on reading "the CDN should serve brotli" is to add the line and the
   result is an origin that will not boot.
4. **Precompression is real, not declared.** The build emits `.gz` and `.br`
   siblings so `gzip_static on;` has something to serve and a Brotli-configured
   edge serves files instead of compressing on the miss path. Only assets: a
   `.gz` of `index.html` would be matched by `location /`, which declares no
   `Cache-Control` at all.
5. **`VITE_ENVIRONMENT` is a token that must equal `--env`.** My first
   implementation read it into the same variable `--env` writes, so an unset
   `VITE_ENVIRONMENT` overwrote `--env staging` with `local` and wrote "local"
   into the public bundle and `/version.json`. Found by running the script, not
   by reading it. Two authorities for one value is not a default, it is a way for
   them to disagree, so the disagreement is an error.
6. **The 5 new keys are allowlisted, emitted and **read** by the app.** An
   allowlisted key nothing reads is not inlined by Vite, and
   `vite-env-audit.sh --bundle` would then report every audited value as
   "missing from the artefact". `getDeployConfig()` references them and resolves
   each to a documented default, so an absent optional variable degrades rather
   than crashing. The feature-flag *consumer* is 048; this task owns the name
   and the validated value.
7. **`version`/`builtAt` are added alongside `release`/`builtAtUtc`,** written
   from the same two values. A renaming would break older clients; adding is
   backward-compatible because the zod schema strips unknown keys. The client
   **requires** all six, so a CDN serving the old four-field document reports
   `UNKNOWN` rather than a false `MATCH`.
8. **The static origin moved rather than being duplicated.** Two Deployments and
   two Services for one component in one cluster is not an option; `git mv`
   semantics via `git rm` + four new files, with `verify.sh` and the analyser
   repointed.
9. **The topology rules are narrow, and the narrowness is asserted.** A gate that
   fires on `features/exports/types.ts`'s defensive `lowered.includes('s3://')`
   gets disabled. Endpoint rules require a host after `://`; the port rule
   requires a colon before the digits; `//` preceded by `:` is a URL, not a
   comment.
10. **`config/env.ts` is excluded from the topology scan, with the reason
    stated and asserted.** It is the secret-shape catalogue; every rule it breaks
    it breaks in order to detect that rule. An unexplained exclusion is
    indistinguishable from one added to make a failure go away, so the test
    asserts the list is exactly that one entry.
11. **`kubectl --dry-run=client` was validated against a real API server.** With
    no kubeconfig it fails with `dial tcp [::1]:8080` on **every** manifest -
    a validator absence, not a manifest error. kind v0.30.0 /
    `kindest/node:v1.34.0` plus the KEDA and External Secrets CRDs; removed
    afterwards.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ npm run build --prefix frontend
dist/assets/ExportsPage-BS6FP6-7.js           31.81 kB │ gzip:   8.61 kB
dist/assets/AdminPage-CFRZcz1v.js             59.94 kB │ gzip:  13.78 kB
dist/assets/index-DbvzFmUZ.js                 466.98 kB │ gzip: 141.22 kB
✓ built in 11.46s
EXIT=0
```

```
$ docker build -f Dockerfile.frontend -t dubbing-frontend:check .
#10 frontend env file in use:
#21 RUN if [ -f version.json ]; then cp version.json dist/version.json && echo "version document in use: …"
#24 exporting layers done
#24 naming to docker.io/library/dubbing-frontend:check 0.1s done
#24 DONE 0.4s
EXIT=0
```

```
$ node scripts/check-frontend-topology.mjs
check-frontend-topology: 271 file(s) scanned under …\frontend\src, 0 finding(s).
FRONTEND_TOPOLOGY_RESULT reason=OK status=PASS files=271 findings=0
EXIT=0
```

```
$ kubectl apply --dry-run=client -f deploy/k8s/frontend/
configmap/frontend-build-config created (dry run)
deployment.apps/frontend created (dry run)
ingress.networking.k8s.io/frontend created (dry run)
service/frontend created (dry run)
EXIT=0
```

### The hosting gate: 30 live assertions, both tiers PASS

```
$ bash deploy/tests/hosting.test.sh
== static tier: policy shape ==
  ok    all seven required policy files are present
  ok    the hosting policy over the committed files is valid (0 problem(s))
  ok    default-deny present: networkpolicies.yaml (empty podSelector / Ingress+Egress)
  ok    6 NetworkPolicy/policies checked
  ok    the network topology enforces Browser->CDN->Static->API with no path to a datastore
  static tier: PASS (policy valid; topology ok)

== docker tier: the actual response headers ==
  ok    docker build -f Dockerfile.frontend .
  ok    the origin is serving
  ok    GET / -> 200, Cache-Control contains 'no-cache'
  ok    GET /index.html -> 200, Cache-Control contains 'no-cache'
  ok    GET /projects/prj_01HZYABCDEFG/workspace -> 200, Cache-Control contains 'no-cache'
  ok    GET /api/v1/projects -> 404, Cache-Control contains 'no-store'
  ok    GET /healthz -> 200, Cache-Control contains 'no-store'
  ok    GET /healthz -> 200, X-Content-Type-Options contains 'nosniff'
  ok    a missing hashed asset is 404, not the HTML document
  ok    a source map request is 404
  ok    GET /api/v1/projects is 404 on the static origin
  ok    Content-Security-Policy contains 'script-src' on a 200
  ok    Content-Security-Policy contains 'object-src' on a 200
  ok    Referrer-Policy contains 'no-referrer' on a 200
  ok    X-Content-Type-Options contains 'nosniff' on a 200
  ok    X-Frame-Options contains 'DENY' on a 200
  ok    Content-Security-Policy does not contain 'unsafe-inline'
  ok    Content-Security-Policy does not contain 'unsafe-eval'
  ok    Server does not contain 'nginx/'
  ok    a hashed asset is served immutable for a year (ActivityPage-Cg0gstRA.js)
  ok    a hashed asset is served gzip-encoded with Accept-Encoding: gzip
  ok    the image ships a precompressed .gz sibling for ActivityPage-Cg0gstRA.js
  ok    the image ships a precompressed .br sibling for ActivityPage-Cg0gstRA.js
  ok    a precompressed sibling inherits the immutable class, not the catch-all's
  ok    /version.json is served and uncached
  ok    /version.json carries the {version, commit, builtAt} names the hosting contract declares
  ok    the CDN viewer-request function redirects http to https using request.uri (query string preserved)
  docker tier: PASS (headers verified against the running image)

== hosting gate result ==
  passed: 30, failed: 0
  static: PASS, docker: PASS
HOSTING_GATE_RESULT reason=OK status=PASS exit=0 static=PASS docker=PASS
```
(23 assertions before this task; 7 added: two `/healthz` rows, two sibling
checks, the sibling's cache class, the version-document field check, and
`/version.json` served.)

### The gates proved red, on the real artefacts

| Class | Injected fault | Result | After |
| --- | --- | --- | --- |
| image build | multi-line `RUN node -e '` without a heredoc | `dockerfile parse error on line 115: unknown instruction: import` - the script never ran | `<<'PRECOMPRESS'` → builds |
| image build | `version.json` written into `dist/` before the build | Vite's `emptyOutDir` deletes it; `/version.json` 404 on every release image | staged beside `.env`, copied after the build → served |
| env injection | `VITE_ENVIRONMENT=prod --env staging` | `::error::VITE_ENVIRONMENT='prod' disagrees with --env 'staging'`, `CONFIG_INJECT_RESULT reason=INVALID status=FAIL` | — |
| secret rule | `Host=db;Database=d;Username=u;Password=hunter2` | `connection-string` did **not** fire; only `inline-credential` did | `[^"']*` middle segment → the named rule fires |
| nginx | any `brotli <x>;` in `default.conf` | `HOSTING_GATE_RESULT … status=FAIL` (unknown directive, container would not start) | asserted absent |
| nginx | `gzip_static on;` removed | the config test and the gate both fail: the precompressed siblings are dead weight | asserted present |
| topology | a file with `postgresql://…:5432`, `amqp://…:5672`, `http://10.42.0.7:8080/…` | 7 findings with exact `file:line`, `FRONTEND_TOPOLOGY_VIOLATION status=FAIL files=273 findings=7` | — |
| topology | the same three lines, with the scan's narrowness intact | `features/exports/types.ts`'s `includes('s3://')` produces **no** finding; `90000` is not a port | — |
| mask | `break` after a line comment | the rest of the file was never masked, so findings after it were missed | `continue`; asserted with a fixture holding both comment kinds |
| verify.sh | the overlay's `frontend-ingress-patch.yaml` counted as a second Ingress | the new assertion failed: *"a second is a second public route"* | overlay files excluded from the object count |
| verify.sh | the overlay pins an image no manifest declares | failed: a kustomize `images:` entry matching nothing is a **silent no-op** | tag-stripped name comparison |

### No regression elsewhere

```
$ dotnet build DubbingPlatform.sln
Build succeeded.  0 Warning(s)  0 Error(s)      (26.98s)

$ dotnet test tests/DubbingPlatform.UnitTests
Passed!  - Failed: 0, Passed: 3040, Skipped: 0, Total: 3040   (46 s)

$ npm run typecheck --prefix frontend          -> (no output)   EXIT=0
$ npm run lint --prefix frontend              -> (no output)   EXIT=0   (--max-warnings=0)
$ npm run test --prefix frontend
  Test Files  146 passed (146)
       Tests  1651 passed (1651)      (1643 before; 8 added)
$ npm run check:no-hex --prefix frontend
  check-no-hex: no hardcoded hex outside tokens.css.
$ npm run typecheck:e2e                   -> (no output)   EXIT=0
$ node tools/generate-client.mjs && git diff --exit-code -- frontend/src/api/generated/
  generate-api: wrote 4 files to frontend\src\api\generated
  EXIT=0

$ npm run test:tools
  tests 216   pass 216   fail 0        (173 before; 43 added, 25 + 18 in deploy/frontend/)
$ node --test deploy/frontend/*.test.mjs
  tests 43    pass 43    fail 0
$ npm run check:unit-containers
  CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440
$ bash scripts/workflow-lint.sh
  workflow-lint: 5 workflow file(s), 0 finding(s)
$ bash scripts/quarantine-check.sh
  CI_GATE_RESULT reason=OK status=PASS
$ bash scripts/migration-compat.sh
  MIGRATION_COMPAT_RESULT reason=OK status=PASS previous=20260921115016_AddVoicePreviewJobs current=20260922082522_AddRefreshSessions
$ bash deploy/verify.sh
  structural OK: 20 files, kinds as specified
  frontend delivery contract OK: Dockerfile.frontend present, index.html uncached, SPA fallback present
  static origin contract OK: ClusterIP, one TLS ingress, 8080, /healthz probes, no runtime env
  overlay contract OK: 2 overlays, every resource/patch path exists, every pinned image and replica target resolves to a base manifest
  topology contract OK: default-deny (networkpolicies.yaml), api/migration/static policies present
  == result: 2 passed, 0 failed ==
  VERIFY_RESULT reason=OK status=PASS exit=0
$ bash scripts/vite-env-audit.sh --allowlist-sync
  vite-env-audit: allowlist in sync across the shell and TypeScript copies (11 keys)
  VITE_ENV_AUDIT_RESULT reason=OK status=PASS files=4 keys=32
```

## Findings

Every one of these was found by running the thing.

### 1. `/version.json` could never ship, and the gate called it expected

`deploy/config-inject.sh` wrote `frontend/dist/version.json`. `vite build` sets
`emptyOutDir: true` by default, so **the build deleted it** - and
`Dockerfile.frontend`'s `dist` stage copied the emptied directory. Every release
image shipped without the document.

What makes this worth writing down is the *reporting*. The hosting gate's
`/version.json` branch had two outcomes: 200-with-`no-cache` (pass) and 404 (a
`note`, described as "expected for an image built without
`deploy/config-inject.sh`"). The release path *had* run the injector, so a broken
release path and an intentionally-uninjected local build produced **byte-identical
output**. A gate whose two failure modes are indistinguishable cannot detect
either. The fix is on both sides: the document is staged outside `dist/` and
copied in afterwards, and the gate now asserts the `{version, commit, builtAt}`
fields when the document *is* present - because a document missing them degrades
the client's skew check to `UNKNOWN` on every deploy, which is also silent.

### 2. A secret rule that only ever fired as a side effect of another rule

`SECRET_VALUE_PATTERNS`' `connection-string` used `[^;]*` for the middle
segment, which requires the password to be the second `;`-separated pair:

```js
/\b(?:Host|Server|Data Source)\s*=\s*[^;\s]+;[^;]*\b(?:Password|Pwd)\s*=/i
// 'Host=db;Password=x'          -> true
// 'Host=db;Database=d;Password=x' -> FALSE
```

Its own test used the four-pair form and **passed**, because
`inline-credential`'s `[?&;](?:password|…)\s*=` also matches `;Password=` and
the assertion was `expect(rejections[0].reason).toBe('SECRET_SHAPED_VALUE')` -
a *reason*, not a rule. The test asserted that the value was refused, which it
was, for a different reason. Fixed in all three copies (`env.ts`,
`vite-env-audit.sh`, `config-inject.sh`) and pinned by a test that asserts the
**rule id** per shape.

The generalisable lesson, and the one the test now encodes: for a rule that is
one of several which can fire on the same input, assert *which* one fired.

### 3. `nginx -V` is the answer, and it is not the answer people expect

`nginxinc/nginx-unprivileged:1.27-alpine` is configured with
`--with-http_gzip_static_module --with-http_v2_module --with-http_v3_module` and
**not** `--with-http_brotli_module`. 043A's instruction 1 asks for "gzip/brotli".
The natural response is a `brotli on;` block, and it would stop the container
from starting with `unknown directive "brotli"` - a deployment-time outage
discovered by a page load failing.

So brotli is served at the **edge**, which is the right place independently (the
edge caches per `Accept-Encoding` and revalidates far less than a browser), the
build emits `.br` siblings so a Brotli-configured edge serves files rather than
compressing on the miss path, and the absence of a `brotli` directive in
`default.conf` is now a **failing assertion with that explanation in the
message**, not a comment.

### 4. ingress-nginx cannot set an arbitrary response header, by default

The obvious way to make the new ingress enforce the header set is
`configuration-snippet` (or the global `more-headers` ConfigMap). Both are
**disabled by default since ingress-nginx 1.9** (`allow-snippet-annotations:
false`). A manifest that depends on a disabled feature is a manifest that
silently stops applying its security policy on a controller upgrade, with no
error anywhere - the worst possible failure shape for a security header.

The ingress therefore sets only what a TLS terminator can: the redirect and
HSTS. The header set stays in `deploy/nginx/security-headers.conf` (the origin
sends it) and `deploy/cdn/viewer-request.security-headers.js` (the edge adds it
for the public hostname). `hosting-config.test.mjs` asserts the ingress uses no
`*-snippet` annotation, with the reason in the assertion message.

### 5. `maskComments` used `break`, so it stopped masking at the first comment

Found in the same commit that introduced it, by the test that has both a line
comment and a block comment: `break` abandoned **the rest of the file** after the
first `//`, so a block comment on a later line was never blanked and every real
finding after it was missed. A masking bug that *removes* findings is
indistinguishable from a clean tree, which is the whole reason to assert the
masker's behaviour rather than trust it.

### 6. The kustomize overlays have never built (pre-existing, NOT fixed)

Two independent blockers, both from Task 40 and both silent because
`deploy/verify.sh` reports the tier as a SKIP:

* kustomize's default `LoadRestrictionsRootOnly` refuses a `resources:` entry
  that leaves the kustomization's directory with `..` - and every entry is one.
* With `--load-restrictor=LoadRestrictionsNone` the build then fails with
  `no resource matches strategic merge patch
  "ConfigMap.v1.[noGrp]/dubbing-config.[noNs]"`: the base manifests hard-code
  `namespace: dubbing-prod`, the kustomizations set `namespace:` to the
  environment, and the patches declare none - so the target id has no namespace
  and the resource still has `dubbing-prod` at the point patches are applied.

I fixed the load-restrictor flag (it is required and it is a one-line change
with the security trade-off documented) and added an **overlay-integrity** check
to `verify.sh` that does not need kustomize: every `resources:`/`patches:` path
exists, every `images:` name resolves to a base manifest (tag-stripped, because
the transformer matches on the name), and every `replicas:` entry names a real
Deployment. That check caught two of my own mistakes while I was writing it.

I did **not** fix the namespace mismatch: it is a Task 40 defect in six
base manifests and the fix is a design decision (drop the hard-coded namespace
from the base and let the kustomization own it, which changes the flat
`kubectl apply -f deploy/k8s/` path). That belongs to 043B, which owns rollout.

## Recommendations for Next Agent (043B)

### Repo state

- `main` carries 043A. `master-prompt.md` is modified and uncommitted - a
  pre-existing scratch file, deliberately left alone (042, 042A and 043 did the
  same).
- Green: `dotnet build` 0 warnings; **3040** backend unit tests (unchanged);
  **1651** frontend tests (1643 + 8); typecheck, lint, `vite build`,
  `check:no-hex`, `typecheck:e2e`, client drift; `test:tools` **216/216**
  (173 + 43); `check:unit-containers`; `workflow-lint` 0 findings across 5
  workflows; `quarantine-check`; `migration-compat`; `deploy/verify.sh`;
  `deploy/tests/hosting.test.sh` (30 assertions, `static=PASS docker=PASS`);
  `vite-env-audit.sh`; `config-inject.sh`;
  `node scripts/check-frontend-topology.mjs`.
- Still red **by design and pre-existing**: `scripts/contract-snapshot.sh`
  (quarantined as `API_CONTRACT_DIVERGENCE`, issue `#422` still a placeholder)
  and the 042 blocking audit. Do not "fix" either by suppressing it.
- **Branch protection is not configured** - it is a repository setting.
  `docs/ci-branch-protection.md` §1.1 is the checklist, and it lists **five**
  required checks.

### Local tooling on a Windows host - read this before running a gate

1. **Run the repo's bash with `"C:\Program Files\Git\bin\bash.exe" -lc`.** WSL
   bash cannot start (no `node` on its PATH, and `wsl` errors with *"Failed to
   start the systemd user session"* here). Git bash works, and its `python3` is
   3.14 **with PyYAML**, so `verify.sh` and `quarantine-check.sh` run fine.
2. **`deploy/tests/hosting.test.sh` needs Docker and takes ~3 min** (the image
   build dominates). Docker Desktop 29.8.0 with a working daemon is available.
   Without a daemon: `HOSTING_GATE_RESULT reason=HOSTING_IMAGE_UNVERIFIED
   status=SKIP` - a **SKIP, not a PASS**, and a promotion gate does not accept it.
3. **`kubectl --dry-run=client` needs a reachable API server** for schema
   validation. With no context it fails with `dial tcp [::1]:8080` on *every*
   file - a validator absence, not a manifest error. To run it for real:
   * `kind.exe` from `https://kind.sigs.k8s.io/dl/v0.30.0/kind-windows-amd64`,
     `kind create cluster --image kindest/node:v1.34.0`;
   * KEDA CRDs: `https://github.com/kedacore/keda/releases/download/v2.17.2/keda-2.17.2-crds.yaml`;
   * External Secrets: `https://github.com/external-secrets/external-secrets/releases/download/v0.19.1/external-secrets.yaml`
     (the `crds/bundle.yaml` path 404s for that version). Some of its CRDs fail
     with `metadata.annotations: Too long` against etcd's 256 KiB limit; that is
     fine, `externalsecrets`, `scaledobjects` and `triggerauthentications` all
     land, which is what `deploy/k8s/` needs.
   * Image loads into kind need `--image-pull-policy=IfNotPresent` (the manifests
     set `Always`, correct in production), and the `ghcr.io/CHANGE_ME/…` name is
     not loadable because of the uppercase placeholder - retag to lowercase.
4. **`kustomize`/`kubeconform`/`helm` are still not installed**, and
   `kubectl kustomize` cannot resolve `..` resources on Windows at all. All
   three are named SKIPs in `deploy/verify.sh`.
5. **`npm run test --prefix frontend` takes ~2.5 min** and `dotnet test` ~1 min.

### Naming and configuration conventions

- `tools/hosting-policy.mjs` is the **pure** decision layer; the shell is I/O.
  `deploy/frontend/hosting-config.test.mjs` is the config tier and
  `deploy/tests/hosting.test.sh` is the live tier. Do not put logic in the shell.
- `scripts/check-frontend-topology.mjs` is the **pure** layer and exports
  `scanSource`, `maskComments`, `hostOf`, `isAllowedHost`, `isScannableFile`,
  `lineOf`, `listScannableFiles`, `EXEMPT_PATHS`, `ALLOWED_HOSTS`,
  `ALLOWED_HOST_SUFFIXES`, `FORBIDDEN_PATTERNS`, `REASON_*`. The CLI runs only
  when `process.argv[1]` is the script itself, so importing it from a test does
  not exit.
- `DEPLOY_CONFIG_ALLOWLIST` in `frontend/src/config/env.ts` is the single source
  of truth, duplicated as `readonly ALLOWLIST=(...)` in
  `deploy/config-inject.sh`. **11 keys.** `bash scripts/vite-env-audit.sh
  --allowlist-sync` fails when they diverge, and
  `hosting-config.test.mjs` compares them too. Do not edit one alone.
- The audit reads the allowlist with
  `DEPLOY_CONFIG_ALLOWLIST\s*=\s*\[([\s\S]*?)\]\s*as const`. **Changing that
  TypeScript shape breaks the audit**, and the failure says so.
- `deploy/cdn/origin.json`'s `cacheClasses` order is asserted and is nginx's
  resolution order. `healthz` sits after `document` (both `=` matches) and
  before `spa-route`; `runtime-version` before `unhashed-asset`; `source-map`
  before `hashed-asset`; `catchAll` last.
- New nginx `location` blocks that declare any `add_header` **must**
  `include /etc/nginx/snippets/security-headers.conf`. The gate finds the
  omission per block, brace-matched, and reports the line.
- Gate result lines, and their reason vocabularies in
  `docs/ci-branch-protection.md` §2: `HOSTING_GATE_RESULT`,
  `VITE_ENV_AUDIT_RESULT`, `CONFIG_INJECT_RESULT`, `FRONTEND_TOPOLOGY_RESULT`,
  `VERIFY_RESULT`. **Add a reason there in the same commit.**

### Incomplete integration points (mine, explicitly)

- **No release job calls `deploy/config-inject.sh`.** 043 documented the
  injection and this task implemented the artefact that was being destroyed, but
  **nothing invokes the injector in CI**. Until a job does, every image built by
  a pipeline has no `/version.json` and the skew check reports `UNKNOWN`. The
  Dockerfile's `if` logs which case it was, so the answer is in the build log.
- **No release job reads `deploy/k8s/frontend/configmap.yaml`.** The ConfigMap is
  the declarative record of the build inputs and the two new overlay patches are
  the per-environment values; a pipeline that renders them into
  `frontend/.env.production` does not exist yet. This is the single highest-value
  wiring left in this task's scope, and it belongs to 043B.
- **The frontend was never added to the kustomize overlays** until this task, so
  it has never been environment-specific. That is now fixed for the two
  environments that exist, and 043B should decide what happens to a third.
- **`deploy/cdn/viewer-request.security-headers.js` has still never been executed
  by a CDN.** The gate asserts its two load-bearing properties textually.
- **HSTS is asserted nowhere at runtime** - only in the ingress annotations and
  the CDN function's source. It cannot be asserted from the origin, which does
  not set it.
- **Brotli is unproven end-to-end.** The `.br` siblings are in the image (asserted
  by the gate) and `origin.json` records that the edge serves them, but no CDN is
  configured to.
- **The ingress has never been applied to a cluster.** `kubectl apply
  --dry-run=client` validates the schema; the annotations were chosen from the
  ingress-nginx documentation and asserted textually, not observed.
- **`connect-src 'self'` on the static origin** still assumes the CDN and the API
  share an origin, which the ingress layout provides. If they are split, the
  origin's CSP must name the API origin, and `compareSecurityHeaders` will flag
  the divergence until both are widened together. **Still the decision most likely
  to need revisiting after a real deploy.**
- **CDN availability and frontend error rate are not on any dashboard.** 043A owns
  hosting; 038 owns the dashboards. `/healthz` and `/version.json` are the signals
  they need and they exist.

### Open items 043A inherited, that this task did not fix

- **The kustomize overlay patch-target failure** (Finding 6). The fix is a design
  decision: drop the hard-coded `namespace: dubbing-prod` from the six base
  manifests in `deploy/k8s/` and let the kustomization own it, which changes the
  flat `kubectl apply -f deploy/k8s/` path. **This is 043B's**, because 043B owns
  the rollout procedure that uses the overlays.
- **The five HIGH/CRITICAL advisories.** Upgrade `vitest`/`@vitest-coverage-v8`,
  `postcss`, `vite`, `react-router-dom`. A dependency PR, not a gate change.
- **The contract divergence (`#422` placeholder).** Regenerate the bundle from the
  server document, `make generate-api`, update `OpenApiCoverageTests`, delete the
  registry row and remove the `continue-on-error` in the same commit.
- **The `CHANGE_ME` handles** in `.github/CODEOWNERS` and
  `deploy/helm/dubbing/values-prod.yaml` - and now also the 4 new
  `CHANGE_ME-frontend-origin…` occurrences in `deploy/k8s/frontend/ingress.yaml`
  and the 2 overlay patches, which must become real hostnames and a real TLS
  `secretName` before a deploy.
- **Admission control for unsigned images** - cluster-side, not CI-side.
