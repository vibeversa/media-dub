# CDN and static-host configuration (Task 043)

The frontend is a static SPA. It is served by a CDN in front of a static origin
and it is **not** served by the API. This directory holds the two things that
make that true, and `deploy/tests/hosting.test.sh` asserts them.

## The layout

```
Browser ──HTTPS──▶ CDN edge ──origin fetch──▶ static origin (nginx, Dockerfile.frontend)
                        │
                        └── CORS + XHR ──▶ API ingress ──▶ API pods ──▶ PG / storage / broker
```

Two properties fall out of that diagram, and both are asserted rather than
assumed:

1. **The browser never talks to the data stores.** There is no route from a
   browser to PostgreSQL, RabbitMQ, Redis or object storage, because none of them
   is in the ingress and `deploy/k8s/networkpolicies.yaml` default-denies. See
   [`docs/topology.md`](../../docs/topology.md) for the full flow and the policy
   that enforces each hop.
2. **The document is never cached; the assets always are.** `index.html` names the
   current hashed asset filenames, so a cached copy points at assets the next
   deploy has already replaced. That is a white screen after every release, and
   it is the single most common way a hashed-asset setup fails. `/assets/*`
   filenames carry a content hash, so their bytes never change under a name and a
   year-long cache is safe.

## Files

| File | What it is |
| --- | --- |
| `origin.json` | The static origin's serving rules as data: cache classes, the header set, and the SPA fallback predicate. `tools/hosting-policy.mjs` reads it, and `deploy/tests/hosting.test.sh` asserts it against the nginx config that implements it. Two descriptions of one behaviour is a drift risk, so the second one is a check rather than a copy. |
| `viewer-request.security-headers.js` | A CloudFront Functions `viewer-request` handler. CloudFront Functions is the one CDN feature layer that can both redirect to HTTPS and add response headers in a single, deterministic unit — Lambda@Edge runs in a region you pay for and logs request bodies you did not ask it to. Deploy it as a **viewer-request** function on the distribution's default behaviour, before the cache behaviour, so the headers are added to the cached response and not re-added per request. |

`deploy/nginx/default.conf` is the origin's own implementation of the same
policy. It is not in this directory because it is baked into
`Dockerfile.frontend` and has to be, not configured into an origin after the
fact.

## Header assertions

`deploy/tests/hosting.test.sh` starts the built image and asserts the actual
response headers, rather than reading the config and asserting the config
mentions the right words. The distinction matters: `add_header` in an
unmatched `location` block is silently inherited-or-not depending on the block
that matched, so a config that *says* the right thing can serve the wrong thing.
The test curls a real server and reads what came back.

| Request | Asserted |
| --- | --- |
| `GET /` | 200, `Cache-Control: no-cache`, CSP without `unsafe-inline`, `X-Content-Type-Options: nosniff` |
| `GET /projects/abc/workspace` | 200 with the SPA document — the deep-link case |
| `GET /assets/<hashed>.js` | `Cache-Control: public, max-age=31536000, immutable` |
| `GET /assets/missing-<hash>.js` | 404, **not** the HTML document |
| `GET /version.json` | 200, `Cache-Control: no-cache` |
| `GET /api/v1/anything` | 404 from the origin, not the SPA document — the API is not here |
| `GET /foo.map` | 404 — source maps are not shipped |
| plain HTTP to the CDN | 301/308 to `https://` |

## What is deliberately not here

- **A TLS certificate.** Termination is the CDN's, and the origin is reached over
  the provider's private link. A certificate committed to a repository is a
  certificate with an expiry nobody is watching.
- **A secret, a key, or an origin hostname.** Every value is `CHANGE_ME` in
  `origin.json` and the deploy substitutes it. The audit in
  `scripts/vite-env-audit.sh` and the manifest checks in
  `deploy/verify.sh` both refuse a `CHANGE_ME` that reaches a deployed artefact.
- **An invalidation procedure.** The cache classes above are chosen so that a
  deploy needs no invalidation: the document is revalidated, and the assets are
  immutable under their names. A release that requires `InvalidatePath` on
  `/index.html` has chosen the wrong cache class for something.
