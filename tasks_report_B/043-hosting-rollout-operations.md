# Task 043 - Frontend Hosting, Env Config, Rollout, Operations

## Status

**COMPLETED.** All eight instructions are implemented, and the `Validation`
block's four commands pass verbatim. The hosting gate builds the frontend image
for real, runs it, and asserts 23 checks against the responses it actually
returns; the topology, the migration-first ordering and the migration-refusal
case were proved on a live ephemeral cluster (kind), not asserted on paper.

**Two pre-existing defects in `Dockerfile.frontend` and `deploy/nginx/default.conf`
were found by running the thing, and both are fixed.** The image **had never been
built** - 042's report recorded "the first CI run is the first build" - and when
it was built it failed with `MODULE_NOT_FOUND`. The nginx config served **no
security headers on any cached asset**, because nginx's `add_header` replaces
the inherited set rather than adding to it. Both are the class of defect this task
exists to prevent, and neither was visible in review.

## Summary

The frontend is now a static SPA behind a CDN with a static origin
(`deploy/k8s/static-deployment.yaml`, deliberately not behind an Ingress), an
allowlisted config injection path that fails the build on anything secret-shaped,
and a hosting gate that reads the **live response headers** rather than the
config that is supposed to produce them. `/health` and `/version` were added, and
readiness now asserts **migration currency** - a pod whose build is ahead of the
schema never takes traffic, which is what turns a botched rollout from "a 500 on
the first request that touches a new column" into "the pod is not in the
Service". The rollout is migration-job-first with a compatibility window and a
two-green-release contract gate; rollback is one command for the API and a CDN
version pin for the frontend, and the database is never rolled back. Nine
runbooks, a topology document and an ops index exist, and `npm run test:tools`
asserts all of it structurally.

## Files Created/Modified

### The hosting gate and its decision layer

| File | What it is |
| --- | --- |
| `tools/hosting-policy.mjs` | **New.** The pure decision layer: path classification, cache classes, `resolveRequest`, `validateOriginConfig`, `compareSecurityHeaders`, `parseNginxHeaders`, `nginxBlocksWithHeaderButNoInclude`, `evaluateHosting`. Unit-tested; the shell is only I/O. |
| `tools/hosting-policy.test.mjs` | **New.** 61 tests over those rules, including the committed `origin.json`, `default.conf` and `security-headers.conf`. |
| `deploy/tests/hosting.test.sh` | **New.** The gate. Static tier (policy shape + topology) and docker tier (build the image, run it, curl the header matrix). Ends with `HOSTING_GATE_RESULT`. |
| `deploy/tests/hosting-topology.py` | **New.** The NetworkPolicy analyser, as a separate module rather than a heredoc. |

### The CDN, the origin, and the config

| File | What it is |
| --- | --- |
| `deploy/cdn/origin.json` | **New.** The serving rules as data: eight cache classes with their order documented as load-bearing, the header set, compression, TLS, the version pin. |
| `deploy/cdn/viewer-request.security-headers.js` | **New.** CloudFront Functions `viewer-request`: HTTPS redirect (using `request.uri`, not `request.url`) + the security headers. |
| `deploy/cdn/README.md` | **New.** The layout, why the origin is not behind an Ingress, and the header-assertion table. |
| `deploy/nginx/security-headers.conf` | **New.** The header set as an `include`, because `add_header` replaces rather than adds. |
| `deploy/nginx/default.conf` | **Modified.** `/api/` → 404, `^~ /assets/`, per-block includes, `/version.json` uncached, CSP moved to the snippet. |
| `Dockerfile.frontend` | **Modified.** Repository-shaped build context, `tools/` + `OpenApi/` copied for the drift gate, the security snippet copied, the env file logged. |
| `deploy/config-inject.sh` | **New.** Per-environment `frontend/.env.production` + `dist/version.json`, reading `info.version` from the committed bundle. |
| `scripts/vite-env-audit.sh` | **New.** Allowlist + secret-name + secret-value rules, and a bundle cross-check. Ends with `VITE_ENV_AUDIT_RESULT`. |
| `frontend/src/config/env.ts` | **New.** `DEPLOY_CONFIG_ALLOWLIST` (the single source of truth), `auditDeployConfig`, `parseRuntimeVersion`, `compareVersions`, `getDeployConfig`. |
| `frontend/src/config/__tests__/env.test.ts` | **New.** 29 tests. |
| `frontend/src/lib/env.ts` | **Modified.** `VITE_CDN_ORIGIN` and `VITE_VERSION_TAG` added, both defaulting to empty. |
| `frontend/.env.example` | **New to git** (see Decisions §1). Regenerated from the allowlist, byte-compared against it by a test. |

### The version-skew mechanism

| File | What it is |
| --- | --- |
| `frontend/src/hooks/useVersionWatch.ts` | **New.** Compares the baked tag against `/version.json` on mount and on focus (60s window), records one telemetry event, clears the query cache once on a detected mismatch. |
| `frontend/src/components/VersionMismatchBanner.tsx` | **New.** Renders **nothing** unless the mismatch is proven. `role="status"`, not `alert`. |
| `frontend/src/hooks/__tests__/useVersionWatch.test.tsx` | **New.** 17 tests. |
| `frontend/src/telemetry/telemetry.ts` | **Modified.** `version_mismatch` event, carrying two release tags and nothing else. |
| `frontend/src/app/layouts/AppShell.tsx`, `frontend/src/i18n/locales/en/common.json` | **Modified.** The banner in the shell, and its three keys. |
| `frontend/eslint.config.js` | **Modified.** `src/config/env.ts` added to the `import.meta.env` exemption, with the reason. |

### The backend surface

| File | What it is |
| --- | --- |
| `src/DubbingPlatform.Api/Endpoints/HealthEndpoints.cs` | **New.** `GET /health` and `GET /version`, with the per-check detail and the no-store metadata. |
| `src/DubbingPlatform.Api/Endpoints/BuildInformation.cs` | **New.** The build stamp, read from assembly metadata, with a `CHANGE_ME` → `unknown` sentinel and control-character sanitising. |
| `src/DubbingPlatform.Infrastructure/Health/MigrationCurrencyCheck.cs` | **New.** `MigrationState` (pure), `MigrationCurrencyCheck` (pure rule over it), `EfCoreMigrationStateReader` (the only I/O). |
| `HealthRegistration.cs` | **Modified.** Registers the currency check for the API and the workers, fail-closed in both directions. |
| `AnonymousRoutes.cs` | **Modified.** `GET /health` and `GET /version` added, with the operational reason. |
| `Program.cs`, `DubbingPlatform.Api.csproj` | **Modified.** `MapHostingEndpoints()`; the four `AssemblyMetadata` stamps. |
| `tests/…/Api/HostingEndpointsTests.cs`, `tests/…/Health/MigrationCurrencyTests.cs` | **New.** 39 tests. |

### Topology, rollout, runbooks

| File | What it is |
| --- | --- |
| `deploy/k8s/static-deployment.yaml` | **New.** The static origin. `ClusterIP`, **no Ingress**, 2 replicas, port 8080. |
| `deploy/k8s/networkpolicies.yaml` | **Modified.** `static-allow` and `migration-allow` added; `workers-allow` no longer sweeps in the migration; `api-allow` gains the `cdn-edge` peer and a documented "namespaceSelector only" rule. |
| `deploy/k8s/migration-job.yaml`, `api-deployment.yaml` | **Modified.** Comments stating what enforces the ordering and why liveness stays process-only. |
| `deploy/verify.sh` | **Modified.** Requires the snippet and the static manifest; asserts the static Service type, the port, the default-deny, the three policies, and the migration's 5432-only egress. |
| `docs/topology.md`, `docs/rollout.md` | **New.** |
| `docs/runbooks/index.md` + 8 incident + `rollback.md` + `backup-restore.md` | **New.** 11 files. |
| `tools/runbooks-index.test.mjs` | **New.** 12 tests: every required runbook exists, is linked, carries its five sections in order, is non-empty, and has a trigger **list**. |
| `docs/ci-branch-protection.md`, `deploy/README.md`, `.github/CODEOWNERS`, `package.json` | **Modified.** The new reason vocabularies, the launch gate, the ownership map, three npm scripts. |

## Decisions Made

1. **`frontend/.env.example` was never tracked; it is now.** The blanket
   `.env.*` rule in `.gitignore` swallowed it, so a **fresh clone had no
   `.env.example`** - and `docs/ci.md` step 7 and `Dockerfile.frontend`'s
   fallback both copy it. On every developer's machine it existed and was
   untracked, so nothing noticed. Found while making a test that byte-compares
   the committed file against the allowlist render. The negation is narrow;
   `.env.production` (the generated one) stays ignored.

2. **`/version` does not fail when the database is unreachable.** It reports
   `migrationHead: "unknown"`, `migrationsPending: -1`. A version answer that
   500s during a database incident removes the one endpoint that could have said
   which build is running. `/health` is the authoritative signal and *is* allowed
   to fail. A test pins the distinction, including that an `OperationCanceled`
   propagates rather than being absorbed into a 200.

3. **Readiness includes migration currency; liveness does not.** Reachable-DB and
   matching-schema are different facts. Only the second one stops a pod ahead of
   the database from serving. A liveness probe that depended on PostgreSQL would
   restart every pod during a blip and turn a degradation into an outage.

4. **`/health` is `Unhealthy` when *either* half is `Unhealthy`, and `Degraded`
   only when both are.** My first implementation used `liveness > readiness` to
   pick the worst - and `HealthStatus` is declared `Unhealthy=0`, so `>` picks the
   **healthiest**. A pod that could not reach its database would have reported
   `Degraded`, and a monitor paging on `Unhealthy` would not have paged. The
   severity order is now spelled out with a comment saying why, because it is the
   one comparison in the file whose direction is not obvious.

5. **The cache-class ORDER in `origin.json` is asserted, and it mirrors nginx's
   resolution rather than a first-match scan.** `matchesClass` returns the first
   match, but nginx evaluates a regex ahead of a plain prefix - so `source-map`
   must precede `hashed-asset` and `runtime-version` must precede the extension
   class, or `/version.json` gets a five-minute cache. The `catchAll`-must-be-last
   invariant is asserted. Two ordering bugs were found this way.

6. **The security headers are an `include`, and the include count is
   brace-matched per block.** nginx's `add_header` **replaces** the inherited set
   in any block declaring one, so a block without the include serves with no CSP.
   I first wrote it as a total-count comparison ("6 headers, 7 includes, fine"),
   which is wrong twice: it cannot say *which* block, and it breaks on
   `location = /index.html` legitimately carrying two headers and one include -
   i.e. it passed for a reason unrelated to correctness. Now per block, with the
   reported line.

7. **The topology analyser is a Python file, not a heredoc.** A `SyntaxError`
   inside `$( )` writes a traceback to stderr, the caller captures it,
   `grep -c '^FAIL'` returns zero, and the tier reports a **PASS having asserted
   nothing**. A Python file also cannot be pointed at by anything else. Its exit
   code carries no verdict (0 = it ran to completion); a non-zero is a failure.

8. **`/version.json`'s 404 is a pass, not a skip, in the hosting gate - and the
   test distinguishes 404-with-document from 404-clean.** A missing version
   document must be a real 404, not the SPA document, which the client would
   parse as JSON. An image built without `config-inject.sh` legitimately has
   none, and the gate says so.

9. **The version-mismatch banner renders nothing on `UNKNOWN`.** Offline, a proxy
   that strips `/version.json`, or a deploy predating the file: there is no
   evidence of skew, and a banner that appears on an inconclusive check trains
   people to dismiss banners. `role="status"` not `role="alert"` - the skew is
   real but not urgent, and an alert interrupts the work the user would have to
   redo. The reload is **offered, not forced**: a forced reload mid-edit loses
   the user's work.

10. **The migration Job's ordering is enforced three ways, and all three are
    asserted**: the ArgoCD `PreSync` and Helm `pre-upgrade` hooks (checked by
    `deploy/verify.sh`), the `kubectl wait` in the runbook, and the API's
    `wait-for-migrations` initContainer. The **wait** is the gate; the apply is
    not. The refusal case was run on a live cluster.

11. **`workers-allow` now excludes `migration` and `static`.** `NotIn [api]` swept
    the migration Job in with the workers, giving a database migration broker and
    Redis access. A migration that can publish is a migration that can deliver a
    message against a schema mid-change. `migration-allow` grants 5432 only.

12. **The static origin is not behind an Ingress.** A second public route to the
    document bypasses every header the CDN's viewer-request function adds,
    including the HTTPS redirect the origin cannot perform for a hostname it does
    not terminate. `deploy/verify.sh` asserts the Service is `ClusterIP` and
    port 8080.

13. **HSTS is set at the CDN, not the origin.** It is a per-origin,
    browser-cached, irreversible-for-max-age commitment, and this origin
    terminates no TLS. Setting it on an origin whose certificate the CDN holds
    would commit browsers to a policy about a hostname the operator does not
    control.

14. **The allowlist is duplicated in bash and in TypeScript, and
    `--allowlist-sync` fails on divergence.** `deploy/config-inject.sh` must run
    in an image build before a node layer exists. Duplication needs a check, not a
    comment. `tools/vite-env-audit.sh` reads the literal form of
    `DEPLOY_CONFIG_ALLOWLIST`; changing that TypeScript shape is a change to the
    audit, and the script says so.

15. **The bundle cross-check reads files, not a shell variable.** The first
    version did `BUNDLE_TEXT="$(cat ...)"` and `printf '%s' "$BUNDLE_TEXT"` -
    megabytes of minified JavaScript in an environment variable, which made
    `bash -x` produce a multi-megabyte trace and the run take minutes. It also
    cross-checks **one** file (`.env.production`, else `.env`), because a bundle
    is built in ONE mode. Asserting every scanned file's values reports
    `.env.cross-layer`'s URL as "missing from a production build", which is
    correct and useless - a gate that is structurally guaranteed to fail gets
    disabled.

16. **Entropy is deliberately not a secret rule.** A hashed asset filename is
    high-entropy and a URL is not; a rule that flags entropy fires on every build
    artefact. The rules are shapes only credential material has: PEM, JWT,
    connection string, inline credential, AWS key id, bearer credential. Pinned
    by a test.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ cd frontend && npm run build
dist/assets/ReviewPage-C4kVTpUQ.js            26.55 kB │ gzip:   7.64 kB
dist/assets/ExportsPage-DilBHyP1.js           31.81 kB │ gzip:   7.61 kB
dist/assets/AdminPage-DVL4L27_.js             59.94 kB │ gzip:  13.78 kB
dist/assets/index-CAJDr4mJ.js                 465.92 kB │ gzip: 140.90 kB
✓ built in 11.95s
EXIT=0
```

```
$ docker build -f Dockerfile.frontend .
#16 1.026 frontend env file in use:
#16 1.026   .env.production
#18 0.959 > dubbing-frontend@0.1.0 prebuild
#18 1.026 check-api-drift: generated client matches the committed bundle.
#18 1.102 ✓ built in 13.4s
#18 1.103 DONE
EXIT=0
```

```
$ kubectl apply --dry-run=client -f deploy/k8s/
deployment.apps/api created (dry run)
service/api created (dry run)
configmap/dubbing-config created (dry run)
deployment.apps/worker-gpu created (dry run)
...
deployment.apps/static created (dry run)
service/static created (dry run)
persistentvolumeclaim/media-scratch created (dry run)
EXIT=0
```
(17 files, 26 objects. KEDA and External Secrets CRDs were installed on the
ephemeral cluster first; without them this command fails on `no matches for kind`,
which is a **CRD absence**, not a manifest error - and `--validate=false` in
`deploy/verify.sh` treats it the same way.)

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
  ok    a hashed asset is served immutable for a year (ActivityPage-Cnv157d7.js)
  ok    a hashed asset is served gzip-encoded with Accept-Encoding: gzip
  ok    the CDN viewer-request function redirects http to https using request.uri (query string preserved)
  docker tier: PASS (headers verified against the running image)

== hosting gate result ==
  passed: 23, failed: 0
  static: PASS, docker: PASS
HOSTING_GATE_RESULT reason=OK status=OK exit=0 static=PASS docker=PASS
```

### The gate proved red, on the real artefacts

| Class | Injected fault | Result | After |
| --- | --- | --- | --- |
| image build | `WORKDIR /app` + only `frontend/` copied | `docker build` **failed**, `MODULE_NOT_FOUND ../tools/check-api-drift.mjs` | repository-shaped layout → builds |
| `^~` removed | `location ^~ /assets/` → `location /assets/` | a hashed asset served `public, max-age=300` instead of `immutable` (8 header failures) | restored → `immutable` |
| include removed from one block | deleted `include …security-headers.conf` from `location /api/` | **all five security headers absent from a 200** (5 failures) | restored → all five present |
| `/api/` block removed | SPA fallback answers `/api/...` | `GET /api/v1/projects` returned the HTML document | restored → 404 |
| `VITE_` key not allowlisted | `VITE_EXPERIMENTAL_CRED=hunter2` | `VITE_ENV_AUDIT_RESULT reason=VITE_KEY_NOT_ALLOWLISTED status=FAIL` **exit 1** | — |
| connection string in `VITE_*` | `VITE_API_BASE_URL=Host=db…;Password=hunter2` | `reason=SECRET_IN_VITE_ENV status=FAIL` **exit 1** | — |
| analyser crashes | Python `SyntaxError` in the heredoc | **silently PASSED** with zero findings | separate module, exit code honoured |
| `read` consumes one line | `IFS=$'\n' read -r status value body < <(probe …)` | every `Cache-Control` assertion failed with `''` against a **correct** origin | globals, not stdout lines |

### The ordering and topology, on a live ephemeral cluster

kind v0.30.0, node `kindest/node:v1.34.0`, KEDA v2.17.2 and External Secrets
v0.19.1 CRDs installed. The cluster and the downloaded tooling were removed
afterwards.

```
$ kubectl wait -n dubbing-prod --for=condition=complete --timeout=25s job/dubbing-migration
error: timed out waiting for the condition on jobs/dubbing-migration
-> the wait refuses; the API rollout must not proceed

$ kubectl logs -n dubbing-prod api-7c46865c69-4lptv -c wait-for-migrations
stub efbundle: refusing to migrate (ordering test)
-> the initContainer gate refuses independently

$ kubectl get pod -n dubbing-prod api-7c46865c69-4lptv -o jsonpath='{…}'
PodReadyToStartContainers=True Initialized=False Ready=False ContainersReady=False
-> the pod never starts

$ kubectl get endpoints -n dubbing-prod api
NAME      ENDPOINTS   AGE
api               101s
-> no ready endpoint: an unready API pod receives no traffic
```

Network policy, against the same static Service IP (`10.96.22.252`):

```
=== OUTSIDER (default ns)        -> BLOCKED   (wget timed out)
=== NETCHECK (same ns, not cdn-edge) -> BLOCKED   (wget timed out)
=== EDGE (cdn-edge ns)           -> REACHED
```

And the headers the CDN edge actually receives:

```
HTTP/1.1 200 OK
Cache-Control: no-cache, no-store, must-revalidate
Content-Security-Policy: default-src 'self'; script-src 'self'; … object-src 'none'; frame-ancestors 'none'
Referrer-Policy: no-referrer
X-Content-Type-Options: nosniff
X-Frame-Options: DENY

GET /api/v1/projects  ->  HTTP/1.1 404 Not Found   (not the document)
```

### No regression elsewhere

```
$ dotnet build DubbingPlatform.sln
Build succeeded.  0 Warning(s)  0 Error(s)      (51s)

$ dotnet test tests/DubbingPlatform.UnitTests
Passed!  - Failed: 0, Passed: 3040, Skipped: 0, Total: 3040   (39 tests added by this task)

$ npm run typecheck --prefix frontend          -> (no output)   EXIT=0
$ npm run lint --prefix frontend              -> (no output)   EXIT=0   (--max-warnings=0)
$ npm run test --prefix frontend
Test Files  146 passed (146)
     Tests  1643 passed (1643)          (1587 before; 56 added)
$ npm run build --prefix frontend          -> ✓ built in 11.95s
$ npm run check:no-hex --prefix frontend
check-no-hex: no hardcoded hex outside tokens.css.
$ npm run typecheck:e2e                   -> (no output)   EXIT=0
$ node tools/generate-client.mjs && git diff --exit-code -- frontend/src/api/generated/
EXIT=0

$ npm run test:tools
tests 173   pass 173   fail 0        (105 before; 68 added)
$ npm run check:unit-containers
CI_GATE_RESULT reason=OK status=PASS files=77 tests=1440
$ bash scripts/workflow-lint.sh
workflow-lint: 5 workflow file(s), 0 finding(s)
$ bash scripts/quarantine-check.sh
CI_GATE_RESULT reason=OK status=PASS
$ bash scripts/migration-compat.sh
MIGRATION_COMPAT_RESULT reason=OK status=PASS previous=20260921115016_AddVoicePreviewJobs current=20260922082522_AddRefreshSessions
$ bash deploy/verify.sh
structural OK: 17 files, kinds as specified
frontend delivery contract OK: Dockerfile.frontend present, index.html uncached, SPA fallback present
static origin contract OK: ClusterIP, no Ingress, port 8080
topology contract OK: default-deny (networkpolicies.yaml), api/migration/static policies present
== result: 1 passed, 0 failed ==
VERIFY_RESULT reason=OK status=PASS exit=0

$ bash scripts/vite-env-audit.sh --allowlist-sync --bundle frontend/dist
vite-env-audit: allowlist in sync across the shell and TypeScript copies (6 keys)
vite-env-audit: bundle cross-check against frontend/.env.production
vite-env-audit: 4 file(s), 22 VITE_* key(s), 6 allowlisted; 0 violation(s).
VITE_ENV_AUDIT_RESULT reason=OK status=PASS files=4 keys=22

$ VITE_API_BASE_URL=https://api.staging.example.com VITE_CDN_ORIGIN=https://cdn.staging.example.com \
    bash deploy/config-inject.sh --env staging --release v1.4.2 --commit 9e107d9d…
config-inject: wrote frontend/.env.production and frontend/dist/version.json
CONFIG_INJECT_RESULT reason=OK status=PASS env=staging release=v1.4.2 openapi=v1
```

## Findings

Every one of these was found by running the thing, and every one would have
produced a green gate that meant nothing.

### 1. `Dockerfile.frontend` had never been built, and could not build

`npm run build` has a `prebuild` → `check-drift` → `node ../tools/
check-api-drift.mjs`. That script resolves the repo root as `dirname/..` and
looks for `frontend/src/api/generated/` and the OpenAPI bundle beneath it. The
Dockerfile had `WORKDIR /app` and copied only `frontend/`, so the build died with
`MODULE_NOT_FOUND`. Nothing noticed: 042's `deploy/verify.sh` asserted the
Dockerfile **existed** and that the nginx config said the right things, and the
report recorded "the first CI run is the first build".

The first fix was wrong in an instructive way: `WORKDIR /app` with `COPY tools/
/tools/` — the script then resolves the root as `/` and reports *"committed
output missing: frontend/src/api/generated"* for a directory that is right there.
The build context has to be the **repository shape**, not a flattened one.

### 2. nginx served no security headers on any cached asset

`add_header` in a `location` block **discards every `add_header` it inherited**
and uses only its own. Every cache class in `default.conf` declares a
`Cache-Control`, so every asset response shipped with no CSP, no
`X-Frame-Options`, no `nosniff` and no `Referrer-Policy` — while the `server`
block, in review, had all four. Found by curling the built image; **not** by
reading the config, which is why the gate reads responses.

Fixed with an `include` in every block that declares a header, and with a
brace-matched per-block assertion so a future block cannot silently opt out.

### 3. A plain `location /assets/` loses to a regex location

nginx evaluates a regex location ahead of a plain prefix one, so
`/assets/index-Ab12.js` was handled by `\.(js|css|…)$` and served
`public, max-age=300` instead of `immutable`. A mutable cache header on a
content-hashed name means a browser revalidates bytes that never change and a CDN
that evicts it re-fetches it for every user. Fixed with `^~`, and asserted.

### 4. `HealthStatus.Unhealthy` is `0`, so `>` picks the healthiest

`OverallStatus(liveness, readiness)` was `liveness > readiness ? liveness :
readiness`. With the enum ordered `Unhealthy=0, Degraded=1, Healthy=2`, that
returns the **healthiest** of the two, so a pod that could not reach its
database reported `Degraded` - and a monitor paging on `Unhealthy` would not have
paged. A unit test over all seven combinations caught it before the endpoint was
ever served.

### 5. A Python `SyntaxError` in a heredoc is a silent PASS

The topology analyser was a heredoc inside `$( )`. A `SyntaxError` writes a
traceback to stderr, the command substitution captures it, `grep -c '^FAIL'`
finds **zero**, and the tier reports a pass having asserted nothing. The literal
`{}` inside an f-string is the trigger, and it is a `SyntaxError` before Python
3.12. Two fixes: string concatenation (portable), and a separate `.py` file
whose exit code the shell honours.

### 6. `read` consumes one line, and every header assertion read empty

`probe` printed three lines and callers did `IFS=$'\n' read -r status value body
< <(probe …)`. `read` reads **one** line, so `value` and `body` were always
empty and every `Cache-Control` assertion failed with `header is ''` against an
origin serving the header perfectly. The obvious conclusion is that the origin is
broken. Fixed with globals. This is the fourth instance of the pattern 042's
report records (npm spawn, `$TMPDIR`, `mktemp -d`, WSL bash without node).

### 7. A hard-coded `/favicon.svg` row asserted a 404 where the policy says 200

The header matrix contained `/favicon.svg|200|…`. **No Vite build in this
repository emits one**, so the row was asserting something false and the gate was
red for a reason unrelated to the policy. Hashed assets, unhashed assets and
`/version.json` are now **discovered from the running container**, and only
always-present paths are in the matrix.

### 8. The migration Job was a worker, and had the broker

`workers-allow`'s selector was `NotIn [api]`, which matched the migration Job -
so a database migration had RabbitMQ, Redis and object-storage egress. A
migration that can publish is a migration that can deliver a message against a
schema mid-change. `NotIn [api, migration, static]` plus a 5432-only
`migration-allow`, asserted both in `deploy/verify.sh` and by the topology
analyser.

### 9. `/version.json` was being caught by the unhashed-asset class

The `unhashed-asset` class matches `json`, and it was declared **before**
`runtime-version`. First-match-wins gave the version document a five-minute
cache - and a cached `/version.json` answers *yesterday's* question, so the skew
check silently stops working. The class order is now documented as load-bearing
and asserted (`source-map` before `hashed-asset` for the same reason).

### 10. `frontend/.env.example` was never in git

The blanket `.env.*` ignore swallowed it, so a fresh clone had no
`.env.example` and both `docs/ci.md` and `Dockerfile.frontend` copy a file that
does not exist. Present and untracked on every developer's machine, so invisible.

## Recommendations for Next Agent (044)

### Repo state

- `main` carries this task. `master-prompt.md` is modified and uncommitted - a
  pre-existing scratch file, deliberately not committed (042 and 042A did the
  same).
- Green: `dotnet build` 0 warnings, **3040** backend unit tests (3031 + 9), **1643**
  frontend tests (1587 + 56), typecheck, lint, `vite build`, `check:no-hex`,
  `typecheck:e2e`, `test:tools` **173/173** (105 + 68), `check:unit-containers`,
  `workflow-lint` 0 findings across 5 workflows, `quarantine-check`,
  `migration-compat`, `require-docker`, `deploy/verify.sh`, `deploy/tests/hosting.test.sh`
  (`reason=OK static=PASS docker=PASS`), `vite-env-audit.sh`, `config-inject.sh`.
- Still red **by design and pre-existing**: `scripts/contract-snapshot.sh`
  (quarantined as `API_CONTRACT_DIVERGENCE`, issue `#422` still a placeholder)
  and the 042 blocking audit (8 HIGH/CRITICAL). Do not "fix" either by
  suppressing it.
- **Branch protection is not configured.** It is a repository setting.
  `docs/ci-branch-protection.md` §1.1 is the checklist and it lists **five**
  required checks.

### New in this task - read these before touching hosting

1. **Run the repo's bash with `C:\Program Files\Git\bin\bash.exe -lc`.** WSL bash
   has no `node` on its PATH, so `deploy/tests/hosting.test.sh` and
   `scripts/vite-env-audit.sh` (which call node) fail there. `verify.sh` and
   `quarantine-check.sh` are fine in WSL - they only need `python3`.
2. **`deploy/tests/hosting.test.sh` needs Docker and takes ~3 min** (the image
   build dominates). Without a daemon it reports
   `HOSTING_GATE_RESULT reason=HOSTING_IMAGE_UNVERIFIED status=SKIP exit=0` -
   **a SKIP, not a PASS**, and a promotion gate does not accept it.
3. **`kubeconform`, `kustomize` and `helm` are still not installed here**, and
   `kustomize` cannot be used on a Windows host at all (parent-path
   `resources:`). All three are named SKIPs in `deploy/verify.sh`.
4. **The ephemeral-cluster setup, if you need it again:** `kind` was downloaded
   to a temp dir and deleted. The dry-run needs **KEDA and External Secrets
   CRDs** installed or it fails on `no matches for kind` - which is a CRD
   absence, not a manifest error. URLs that worked:
   `https://github.com/kedacore/keda/releases/download/v2.17.2/keda-2.17.2-crds.yaml`
   and `https://github.com/external-secrets/external-secrets/releases/download/v0.19.1/external-secrets.yaml`
   (the `crds/bundle.yaml` path 404s for that version).
5. **Image loads into kind need `--image-pull-policy=IfNotPresent`**
   (`Dockerfile.frontend` and the manifests set `Always`, which is correct in
   production and makes a locally-loaded image hang on a pull). The
   `ghcr.io/CHANGE_ME/...` name is also not a loadable reference because of the
   uppercase `CHANGE_ME`; retag to something lowercase for a local test.

### Naming and configuration conventions

- `tools/*.test.mjs` is picked up by `npm run test:tools`. `hosting-policy.mjs`
  is the **pure** layer; `deploy/tests/hosting.test.sh` is its I/O. Do not put
  logic in the shell.
- `DEPLOY_CONFIG_ALLOWLIST` in `frontend/src/config/env.ts` is the single source
  of truth, duplicated in `readonly ALLOWLIST=(...)` in
  `deploy/config-inject.sh`. **`bash scripts/vite-env-audit.sh --allowlist-sync`
  fails when they diverge** - do not edit one alone.
- The script reads the allowlist with the regex
  `DEPLOY_CONFIG_ALLOWLIST\s*=\s*\[([\s\S]*?)\]\s*as const`. **Changing that
  TypeScript shape breaks the audit**, and the failure says so.
- `deploy/cdn/origin.json`'s `cacheClasses` order is asserted and is nginx's
  resolution order. `source-map` before `hashed-asset`; every `=` match before
  the extension class; `spa-route` (the `catchAll`) last.
- New nginx `location` blocks that declare any `add_header` **must**
  `include /etc/nginx/snippets/security-headers.conf`. The gate finds the
  omission per block and reports the line.
- Every gate ends with one machine-readable line:
  - `HOSTING_GATE_RESULT reason=… status=<PASS|FAIL|SKIP> exit=… static=… docker=…`
  - `VITE_ENV_AUDIT_RESULT reason=… status=… files=… keys=…`
  - `CONFIG_INJECT_RESULT reason=OK|INVALID status=… env=… release=… openapi=…`
  All three vocabularies are in `docs/ci-branch-protection.md` §2. **Add a reason
  there in the same commit.**

### Incomplete integration points (mine, explicitly)

- **No release job calls `deploy/verify.sh --post-deploy --require-post-deploy`.**
  042 and 042A both assigned this to 043; I documented the exact command in
  `docs/rollout.md` §5 and in the launch gate, but **no workflow invokes it**.
  It is not wired, and the `@smoke` spec still does not exist, so the post-deploy
  gate correctly fails with `SMOKE_SPECS_MISSING`.
- **`deploy/cdn/viewer-request.security-headers.js` has never been executed by a
  CDN.** The gate asserts its two load-bearing properties textually (it
  redirects, and it rebuilds the URL from `request.uri` not `request.url`).
  Deploying it is a CloudFront change nobody has made.
- **`connect-src 'self'` on the static origin** assumes the CDN and the API share
  an origin in production, which the ingress layout provides. If they are split,
  the origin's CSP must name the API origin explicitly, and
  `compareSecurityHeaders` will then flag the divergence against the API's own
  `SecurityHeadersMiddleware` until both are widened together. **This is the one
  decision in the task most likely to need revisiting after a real deploy.**
- **HSTS is asserted nowhere at runtime**, only in the CDN function's source. It
  cannot be asserted from the origin because the origin does not set it.
- **Migration currency is in readiness, but nothing asserts it end-to-end
  against a real database.** The unit tests drive `MigrationState` directly with
  no container, and the ephemeral-cluster test proved the *ordering*, not a
  pending-migration 503. That is the one R3 case left unproven.
- **The contract phase has never run.** Nothing has been dropped, so the
  "two green releases" rule and the `schema is ahead` refusal row in
  `docs/runbooks/rollback.md` are documented and asserted as *text* only.

### Open items 043 inherits, that this task did not fix

- **The five HIGH/CRITICAL advisories.** Upgrade `vitest`/`@vitest-coverage-v8`,
  `postcss`, `vite` and `react-router-dom`. As a dependency PR, not a gate change.
- **The contract divergence (`#422` placeholder).** Regenerate the bundle from the
  server document, `make generate-api`, update `OpenApiCoverageTests`, delete the
  registry row and remove the `continue-on-error` in the same commit.
- **The `CHANGE_ME` handles** in `.github/CODEOWNERS` (now 45+ rows, 16 added by
  this task) and `deploy/helm/dubbing/values-prod.yaml`.
- **Admission control for unsigned images** - cluster-side, not CI-side.
- **The `cd /app` shape assumption in `deploy/tests/hosting-topology.py`** assumes
  every `NetworkPolicy` lives flat in `deploy/k8s/`. A policy in an overlay
  directory is not read. `deploy/verify.sh` has the same assumption.
