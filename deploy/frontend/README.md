# Frontend hosting

The static SPA behind the CDN. This is the document that answers "where does the
browser's request go, and what is allowed to be cached" — the rest of the
hosting arrangement is code, and code does not explain itself:

| File | What it is |
| --- | --- |
| `../k8s/frontend/deployment.yaml` | The origin pods. 2 replicas, port 8080, probes on `/healthz`. |
| `../k8s/frontend/service.yaml` | `ClusterIP`. The property that keeps the pods unreachable from outside the cluster. |
| `../k8s/frontend/ingress.yaml` | TLS for the **origin** hostname + the HTTP→HTTPS redirect. Not a second public route. |
| `../k8s/frontend/configmap.yaml` | The per-environment **build inputs**. Deliberately *not* mounted into the pods. |
| `../../Dockerfile.frontend` | The image: `npm run build` → precompress → `scratch` → nginx-unprivileged. |
| `../nginx/default.conf` | The cache classes and the routing, as the origin actually serves them. |
| `../nginx/security-headers.conf` | The one copy of the response security headers. |
| `../cdn/origin.json` | The same rules as **data**, plus the edge topology. Read by `tools/hosting-policy.mjs`. |
| `../cdn/viewer-request.security-headers.js` | The CDN function: HTTPS redirect + headers, for the public hostname. |
| `../config-inject.sh` | Renders `frontend/.env.production` and `dist/version.json` from the values above. |
| `hosting-config.test.mjs` | The config half of the contract. Run by `npm run test:tools`. |
| `topology.test.mjs` | The topology rules, and the real tree. Run by `npm run test:tools`. |

## Hosting diagram

```
                    ┌───────────────────────────────────────────────┐
   Browser ────────►│ CDN            public hostname, CHANGE_ME     │
   (https only)     │  • caches one representation per Accept-Encoding│
                    │  • holds the certificate for the PUBLIC name   │
                    │  • adds the security headers                   │
                    │  • redirects plain HTTP (301)                  │
                    │  • sets HSTS                                  │
                    └───────────────────────┬───────────────────────┘
                                            │ origin fetch, over the
                                            │ provider's private path
                    ┌───────────────────────▼───────────────────────┐
                    │ ingress-nginx     ORIGIN hostname, CHANGE_ME   │
                    │  • terminates TLS for the ORIGIN name          │
                    │  • force-ssl-redirect                         │
                    │  • HSTS as an origin-side hedge                │
                    │  • declares NO header set - see below         │
                    └───────────────────────┬───────────────────────┘
                                            │ ClusterIP:8080, and the
                                            │ `static-allow` policy
                    ┌───────────────────────▼───────────────────────┐
                    │ frontend pods × 2   nginx-unprivileged:8080   │
                    │  • SPA fallback to index.html                 │
                    │  • /assets/ immutable for a year              │
                    │  • index.html, /version.json, /healthz  no-store│
                    │  • security headers on every response          │
                    └───────────────────────┬───────────────────────┘
                                            │ one origin, from
                                            │ VITE_API_BASE_URL only
                    ┌───────────────────────▼───────────────────────┐
                    │ API  api.<env>  a different host, its own       │
                    │      ingress, CORS allowlist, auth             │
                    └───────────────────────────────────────────────┘

   Browser ──✗──► PostgreSQL / RabbitMQ / Redis / MinIO / workers
                   (no route, no NetworkPolicy, and no reference in
                    frontend/src - scripts/check-frontend-topology.mjs)
```

**One public route to the document, and it is the CDN's.** Three things make
that true, and all three are asserted rather than assumed:

1. `service.yaml` declares `type: ClusterIP`, so the pods have no address
   outside the cluster. (`deploy/verify.sh`, and `hosting-config.test.mjs`.)
2. `static-allow` in `../k8s/networkpolicies.yaml` admits the `cdn-edge` and
   `ingress-nginx` namespaces and nothing else — no `ipBlock`, no bare
   `podSelector`. (`deploy/tests/hosting-topology.py`.)
3. `ingress.yaml` binds the **origin** hostname, not the public one, so it is a
   hop *behind* the CDN rather than a way around it. (`deploy/verify.sh`.)

A second public route to the document serves identical bytes over plain HTTP
with no edge policy: no redirect, no HSTS, no CSP. That is the whole failure
this arrangement is arranged against.

### Why the Ingress declares no response headers

ingress-nginx has **no first-class annotation for an arbitrary response
header**. The two ways to add one are the `configuration-snippet` annotation and
the global `more-headers` ConfigMap, and *both are disabled by default since
ingress-nginx 1.9* (`allow-snippet-annotations: false`). A manifest that depends
on a disabled feature is a manifest that silently stops applying its security
policy the day someone upgrades the controller — with no error anywhere.

So the single copy of the header set is `../nginx/security-headers.conf`, which
the origin includes in every `location` block that declares an `add_header` (see
below for why that matters), and the CDN function adds the same set for the
public hostname. If the CDN is ever removed and this Ingress becomes the public
edge directly, the set has to be added to `more-headers` — and
`hosting-config.test.mjs` asserts the annotations that *do* work without
snippets, so the two paths cannot be confused.

## Cache policy

Declared once in `../cdn/origin.json` and implemented in
`../nginx/default.conf`; `tools/hosting-policy.mjs` is the single implementation
of the rules and `deploy/tests/hosting.test.sh` curls a real container to prove
the implementation agrees.

| Class | Paths | `Cache-Control` | Miss is | Why |
| --- | --- | --- | --- | --- |
| `document` | `/index.html` | `no-cache, no-store, must-revalidate` | the document | A cached document names asset hashes the next deploy has already replaced. The symptom is a white screen on every hard refresh after a release — the most common way a hashed-asset setup fails. |
| `hashed-asset` | `/assets/**` | `public, max-age=31536000, immutable` | **404** | The bytes behind a content hash never change, so a stale entry cannot exist. A miss is 404 and *never* the document: 200 `text/html` where the browser expected JavaScript produces a `SyntaxError` that names the frontend instead of the CDN. |
| `runtime-version` | `/version.json` | `no-cache, no-store, must-revalidate` | 404 | Its job is to report what the edge is serving *now*. Caching it makes the skew check answer yesterday's question. |
| `healthz` | `/healthz` | `no-store` | 404 | A stored health response is a 200 that outlives the thing it reported. |
| `service-worker` | `/service-worker.js` | `no-cache, no-store, must-revalidate` | 404 | A cached service worker pins a client to an old asset manifest — the same failure as a cached document, one layer deeper. |
| `api` | `/api/**` | — (404) | — | The API is a different host. Answering `/api/…` with the SPA document gives the client `text/html` where it expected JSON. |
| `source-map` | `**.map` | — (404) | — | `build.sourcemap` is `false`; the 404 is the second line of defence, so a future build-flag change cannot leak a copy of the source. |
| `unhashed-asset` | the other extensions | `public, max-age=300` | 404 | These names do **not** change with content, so a year would be wrong; a year-less policy would be too. |
| `spa-route` | everything else | inherits the document's | the document | A deep link like `/projects/01HZY…/workspace` is handled by the client router, and the server has never heard of it. Without a fallback it is a 404 the user sees, and no amount of client-side routing fixes it. |

**The order of `cacheClasses` is load-bearing** and mirrors nginx's resolution
rather than a first-match scan: an `=` exact match beats a plain prefix, and a
regex beats a plain prefix too. `runtime-version` therefore precedes
`unhashed-asset` (which also matches `json`) and `source-map` precedes
`hashed-asset` (or `/assets/x.js.map` would be served as an immutable asset —
exactly the leak the class exists to prevent). `spa-route` is the catchAll and
must be last. `validateOriginConfig` asserts the catchAll position;
`deploy/frontend/hosting-config.test.mjs` asserts the overlapping pair.

### Three nginx details that are not cosmetic

* **`location ^~ /assets/`, not `location /assets/`.** nginx evaluates a regex
  location ahead of a plain prefix one, so without `^~` a request for
  `/assets/index-Ab12.js` is handled by the `\.(js|css|…)$` block below and gets
  `max-age=300` — a mutable cache header on a content-hashed name. Both blocks
  look correct in review.
* **The security headers are an `include`, repeated in every block.** nginx's
  `add_header` **replaces** the inherited set in any block that declares one, so
  a `location` with a `Cache-Control` of its own silently serves that path with
  no CSP, no `nosniff` and no `X-Frame-Options` — while the `server` block reads
  correctly. The check is per-block and brace-matched, not a count of headers
  against a count of includes; a count is right twice for the wrong reason.
* **A `.map` request is a 404, not a document.** Covered above.

### Compression

* **gzip, at the origin.** `gzip on` with `gzip_min_length 1024` (gzipping a
  300-byte file makes it larger), plus `gzip_static on`, which serves the `.gz`
  sibling the build wrote instead of compressing on every request.
* **Precompressed siblings, at build time.** `Dockerfile.frontend` writes a `.gz`
  **and a `.br`** next to every asset over 4 KiB, using `node:zlib` — no extra
  dependency and no extra layer. Only assets: a `.gz` of `index.html` would sit
  at the top level where `location /` matches it, and `location /` declares no
  `Cache-Control` at all.
* **brotli, at the edge — and there is deliberately no `brotli on;` in
  `default.conf`.** `nginxinc/nginx-unprivileged:1.27-alpine` is built
  `--with-http_gzip_static_module` and is **not** built
  `--with-http_brotli_module`; `nginx -V` against the image is where that was
  established. A `brotli on;` line is an unknown directive and nginx refuses to
  start on it, so the instinct on reading "the CDN should serve brotli" has to
  be stopped in code. `tools/hosting-policy.mjs` and `hosting-config.test.mjs`
  both assert the absence, with the reason in the message.
* Brotli is the right thing at the edge independently of the module: the edge
  caches one representation per `Accept-Encoding` and revalidates against the
  origin far less often than a browser does. An edge configured for automatic
  compression will ignore the `.br` files and compress once, which is also
  correct — the siblings exist so an edge configured for Brotli serves real
  files rather than compressing on the miss path.

## Env injection: build-arg, not runtime

**Decision: build-time, and it is forced rather than preferred.** Vite inlines
`import.meta.env.VITE_*` into the JavaScript at build time. There is no runtime
indirection to exploit, so a "runtime config" for these keys could only be built
by fetching a JSON document at boot — which makes the first paint depend on a
second origin and is a different architecture with a different CSP, not a
configuration option.

The consequences, accepted deliberately:

* **One image per environment.** That is why the image is versioned and signed
  like the backend ones. An `index.html` baked with staging's API base URL is a
  different product, not a different build of the same one.
* **`configmap.yaml` is a build INPUT record, not runtime configuration.** It is
  read by the release pipeline *before* the image is built and rendered into
  `frontend/.env.production` by `../config-inject.sh`, which is the file Vite
  actually consumes. It is deliberately **not** mounted: a ConfigMap presented as
  runtime configuration is the most expensive kind of no-op — an operator changes
  it, observes no effect, and concludes the deployment is broken.
  `deployment.yaml` says so, and `deploy/verify.sh` and
  `hosting-config.test.mjs` both assert the deployment has no `env` and no
  `envFrom`.
* **The alternative this repository does not take:** one image, with the edge
  rewriting a placeholder in `index.html`. It is popular, and it is a
  cache-invalidation problem invented on purpose — the rewritten document is a
  different body under the same URL, which every cache in the path stores by URL.

### The variables

`DEPLOY_CONFIG_ALLOWLIST` in `frontend/src/config/env.ts` is the single source
of truth. `../config-inject.sh` carries a **second copy** in bash, because it has
to run in an image build before a node layer exists; `bash
scripts/vite-env-audit.sh --allowlist-sync` **fails** when the two diverge, and
`hosting-config.test.mjs` compares them as well. A comment saying "keep in step"
is not a check.

| Variable | Kind | Default | Notes |
| --- | --- | --- | --- |
| `VITE_API_BASE_URL` | URL | *required* | The only origin the browser may reach. Missing ⇒ the build fails closed rather than shipping `undefined`-origin calls. |
| `VITE_CDN_ORIGIN` | URL | falls back to `VITE_API_BASE_URL` | Pin-able in `connect-src` without a wildcard. |
| `VITE_VERSION_TAG` | label | falls back to `VITE_APP_VERSION` | Compared against `/version.json` to detect a stale tab. |
| `VITE_APP_VERSION` | label | `0.1.0-dev` | Shown in the footer; derived from the release tag so the two cannot disagree. |
| `VITE_SSE_ENABLED` | `true`/`false` | `true` | |
| `VITE_TELEMETRY_ENABLED` | `true`/`false` | `false` | |
| `VITE_ENVIRONMENT` | token | `--env` | Which deployment. A label, so an operator reading the bundle off the edge can tell environments apart. Constrained to `[A-Za-z0-9._-]`; if it is set it must **equal** `--env`, because two authorities for one value is a way for them to disagree. |
| `VITE_ENABLE_ANALYTICS` | `true`/`false` | `false` | Presentation only. The consumer is Task 048. |
| `VITE_ENABLE_DIAGNOSTICS` | `true`/`false` | `false` | Presentation only — the API's authorization is the access control, and no client-side flag is one. |
| `VITE_ENABLE_EXPERIMENTAL_FEATURES` | `true`/`false` | `false` | Presentation only. |
| `VITE_SENTRY_DSN` | URL or sentinel | `sentry-disabled` | A Sentry DSN is a **public, write-only client key**, designed to be readable by anyone who can load the page — different in kind from a signing key, which is why it is allowlisted and a token is not. `sentry-disabled` is non-empty on purpose: an empty injected value passes an "is it configured?" check and then initialises nothing. |

**No secrets, and that is enforced rather than promised.** Every value above is
inlined into a public bundle. Anything that grants authority to read or write
server-side data belongs in `../k8s/secrets.yaml` (an `ExternalSecret`), never
here. `hosting-config.test.mjs` asserts every key is on the allowlist, that no
name is secret-shaped, and that no value matches any of the shapes in
`SECRET_VALUE_PATTERNS`.

The rules are **shapes only credential material has** — PEM block, JWT,
connection string with a password, broker/object-storage URI with inline
credentials, inline credential parameter, AWS access key id, bearer credential.
**Entropy is deliberately not one of them**: a hashed asset filename is
high-entropy and a rule that flags entropy fires on every build artefact it is
pointed at, which is how a rule gets disabled.

> One consequence worth knowing: each rule is asserted by **id**, not by "some
> secret reason". The `connection-string` rule required the password to be the
> *second* `;`-separated pair, so it never matched a realistic four-pair
> connection string — and its test passed anyway, because `inline-credential`
> also matches `;Password=` and the assertion was on the reason rather than on
> which rule fired. Fixed in Task 043A in all three copies.

## SPA fallback behaviour

`location /` does `try_files $uri $uri/ /index.html`. Three consequences:

* **A deep link works.** `/projects/prj_01HZYABCDEFG/workspace` has never been
  seen by the server, so without the fallback it is a 404 the user sees.
* **A miss under `/assets/` is a 404, not the document.** `try_files $uri =404`
  in that block, which is the difference between "the CDN is missing a chunk"
  and "the frontend is serving HTML as JavaScript".
* **A miss under `/api/` is a 404.** Without that block, `location /` answers
  every `/api/…` path with `index.html`.

Both refusals carry `always` on their headers, so the 404 a user sees when
something has gone wrong is as well-labelled as the 200 that worked.

## Health and version

| Endpoint | Served by | Contract |
| --- | --- | --- |
| `/healthz` | `location = /healthz`, `return 200` from the config | A **static** 200. No filesystem access and no dependency on the build output, because the probe's job is "can this process serve an HTTP response" and asking for the document conflates that with "does the build contain an index.html" — a build that produced no document then becomes a failing readiness probe on every replica, which reads as a broken cluster rather than a broken build. Used by the pod liveness probe, the pod readiness probe and the image `HEALTHCHECK`. `no-store`, and `access_log off` (two replicas polling every 5–10s is log noise). |
| `/version.json` | `deploy/config-inject.sh` stages it; `Dockerfile.frontend` copies it into `dist/` | `{release, version, commit, openapiVersion, builtAtUtc, builtAt, environment}`. `version` and `builtAt` are the names the hosting contract declares; they are **added alongside** `release`/`builtAtUtc`, written from the same two values, so an older client strips what it does not know and there is no second source to drift. The client's `parseRuntimeVersion` **requires** all six, so a CDN still serving a four-field document reports `UNKNOWN` rather than a false `MATCH` — the correct direction, because a skew that cannot be evaluated must not present as agreement. |
| `/version` | the API | `GET /version`, which must agree with the document above. |

**Stale HTML at the edge → a banner, not silence.** `useVersionWatch` compares
the baked tag against `/version.json` on mount and on focus, and
`VersionMismatchBanner` renders **nothing** unless the mismatch is proven
(`UNKNOWN` renders nothing — offline, a proxy that strips the file, a deploy
predating it). It is `role="status"`, not `role="alert"`: the skew is real but
not urgent, and an alert interrupts the work the user would have to redo. The
reload is **offered, not forced** — a forced reload mid-edit loses the user's
work. A banner that appears on an inconclusive check trains people to dismiss
banners, which is worse than having none.

CDN availability and frontend error rate are the two signals that belong in the
038/043C monitors. Neither is wired to a dashboard here: this task owns hosting,
and the dashboards are 038's.

### Where `/version.json` is written, and why not in `dist/`

`deploy/config-inject.sh` stages the document at **`frontend/version.json`** — a
sibling of `.env.production`, not a bundler output — and `Dockerfile.frontend`
copies it into `dist/` **after** `npm run build`.

That ordering is load-bearing, and getting it wrong is a silent failure.
`vite build` empties `outDir` by default, so the document used to be written
straight into `dist/` by the injector and then **deleted by the build**: every
release image shipped without one. The hosting gate reported the resulting 404 as
*"as expected for an image built without `config-inject.sh`"* — which is exactly
what it looked like, because the release path *had* run the injector. A
version-skew check that can never read the document reports nothing and looks
healthy.

A file the bundler deletes is not a build output. `frontend/version.json` is
gitignored (a committed one would record one developer's commit sha and origin
and would be served to every user who loads the app), and the Dockerfile's `if`
prints which case it was — the build log is the only place that says so.

For a local static preview: `cp frontend/version.json frontend/dist/`.

## Topology

`scripts/check-frontend-topology.mjs` fails on any direct reference in
`frontend/src` to PostgreSQL, RabbitMQ, Redis, object storage, a worker host, a
datastore port, or an absolute URL that is neither loopback nor IANA-reserved.

```
FRONTEND_TOPOLOGY_RESULT reason=OK status=PASS files=272 findings=0
```

It is **narrow on purpose**, and the narrowness is asserted, because a gate that
fires on the defensive use of a string gets disabled:

* `features/exports/types.ts` contains `lowered.includes('s3://')` to *strip*
  internal storage URLs before rendering. Every endpoint rule therefore requires
  a host after `://`.
* The port rule requires a colon before the digits, because this codebase is
  full of `90000` and `61000`.
* `//` immediately preceded by `:` is a URL, not a comment — masking it would
  delete the very strings the URL rule reads. Comments *are* masked, so
  documenting an old endpoint is not a finding.
* Test files, stories and the Vitest harness (`src/test/setup.ts`, previously
  `src/testSetup.ts`) are out of scope: they legitimately contain hostile
  strings as fixtures.
* `frontend/src/config/env.ts` is excluded **with a stated reason**: it is the
  secret-shape catalogue, and every rule it breaks it breaks in order to detect
  that rule.

The runtime half of the topology — "no browser-reachable path to a datastore" —
is `deploy/tests/hosting-topology.py` over the NetworkPolicies, and the
live-cluster proof is in the 043 report.

## Gates

| Command | Needs | Asserts |
| --- | --- | --- |
| `node --test deploy/frontend/*.test.mjs` | node | This contract, over the committed artefacts. |
| `node scripts/check-frontend-topology.mjs` | node | R4, over `frontend/src`. |
| `bash scripts/vite-env-audit.sh --allowlist-sync [--bundle frontend/dist]` | node | The allowlist agrees across bash and TypeScript; no unlisted, secret-named or secret-shaped `VITE_*`; each audited value is present in the emitted bundle. |
| `bash deploy/config-inject.sh --env … --check` | node or python3 | A value is refused before anything is written. |
| `bash deploy/tests/hosting.test.sh` | **docker**, node, python3 | The headers a **running container** actually returns. Needs a daemon: without one it reports `HOSTING_IMAGE_UNVERIFIED status=SKIP` — a SKIP, not a PASS, and a promotion gate does not accept it. |
| `bash deploy/verify.sh` | python3 + PyYAML, kubectl | Manifests, the single-public-route claim, the ingress annotations, the NetworkPolicy topology. |

Each gate ends with one machine-readable line, and the reason vocabularies are
in `../../docs/ci-branch-protection.md` §2:
`HOSTING_GATE_RESULT`, `VITE_ENV_AUDIT_RESULT`, `CONFIG_INJECT_RESULT`,
`FRONTEND_TOPOLOGY_RESULT`, `VERIFY_RESULT`.

## Rollback

**Not this task.** Rollout and rollback are **043B**; runbooks and backup drills
are **043C**. What this task owns is the pointer:

* The frontend is rolled back by **pinning the edge to the previous image** — a
  pointer change, not a rebuild. `../cdn/origin.json` `cdnVersionPinning` and
  `../../docs/runbooks/rollback.md` carry the procedure.
* `kubectl -n dubbing-prod rollout undo deploy/frontend` reverts the pods if the
  edge is not in use.
* The **database is never rolled back.** 043B owns that rule; it is repeated
  here only because this document is what an operator reaches for first, and a
  partial statement of the rollback policy is worse than none.

One property makes the frontend rollback cheap, and it is worth preserving:
`index.html` is never cached, so a release is visible on the next document
revalidation rather than after a TTL. The cost of that is a revalidation per
navigation; the benefit is that "the CDN is serving N while the client runs N−1"
is *detectable*, via `/version.json`.
