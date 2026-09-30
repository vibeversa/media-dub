# Topology

Task 043, instruction 4. This is the data-flow diagram and the policy that
enforces each hop. Everything here is asserted mechanically by
`deploy/tests/hosting.test.sh` (the static tier) and
`deploy/verify.sh` (the manifest tier); a change that breaks the picture below
breaks a gate rather than a review comment.

## The flow

```
                    ┌──────────────────────────────────────────────┐
                    │                  BROWSER                    │
                    │  no credentials for the data stores, ever    │
                    └───────────────┬──────────────────────────────┘
                                    │ HTTPS (TLS terminated at the edge)
                                    ▼
                    ┌──────────────────────────────────────────────┐
                    │                   CDN EDGE                    │
                    │  viewer-request function:                     │
                    │    http -> https 301                         │
                    │    security headers + HSTS                   │
                    │  cache classes: document revalidate,          │
                    │    /assets/* immutable                        │
                    └───────┬──────────────────────┬───────────────┘
                            │ origin fetch        │ CORS + XHR
                            │ (private link)      │ (Bearer JWT)
                            ▼                     ▼
          ┌─────────────────────────────┐   ┌──────────────────────────┐
          │      STATIC ORIGIN         │   │      API INGRESS         │
          │  nginx, Dockerfile.frontend│   │  ingress-nginx, TLS      │
          │  ClusterIP; fronted by an ingress for the ORIGIN hostname (043A)│   └────────────┬─────────────┘
          │  2 replicas, no egress     │                │
          └─────────────────────────────┘                ▼
                                            ┌──────────────────────────┐
                                            │        API PODS          │
                                            │  /health/live   process  │
                                            │  /health/ready  DB+stor+ │
                                            │               broker+   │
                                            │               migration  │
                                            │  /health  /version       │
                                            └───┬────────┬────────┬───┘
                                                │        │        │
                                                ▼        ▼        ▼
                                    ┌────────────┐┌────────┐┌────────┐
                                    │ PostgreSQL ││RabbitMQ││ Redis  │
                                    │   5432     ││  5672  ││  6379  │
                                    └────────────┘└────────┘└────────┘
                                                │
                                                ▼
                                    ┌────────────────────────┐
                                    │     object storage     │
                                    │        9000            │
                                    │  + provider APIs :443  │
                                    └────────────────────────┘
```

## The three properties, and what enforces each

### 1. No browser→data-store path exists (R4)

The browser has exactly two routes out, and neither reaches a data store:

- **To the static origin**, through the CDN and then the TLS ingress. The static
  origin has `policyTypes: [Ingress, Egress]` in `static-allow`
  (`deploy/k8s/networkpolicies.yaml`) and its only ingress peers are the
  `cdn-edge` and `ingress-nginx` namespaces. It has no egress at all — a file
  server has nothing to call. It is a `ClusterIP` Service, and the only Ingress
  selecting it (`deploy/k8s/frontend/ingress.yaml`, added in Task 043A) binds the
  **origin** hostname rather than the public one — so the CDN stays the only
  browser-reachable name, and the ingress is a hop *behind* it rather than a way
  around it. A second public route to the document would bypass the CDN's HTTPS
  redirect, its HSTS and its security headers, which is the failure the
  `ClusterIP` type and the two-namespace peer list exist to prevent.
- **To the API**, through the ingress. `api-allow` grants ingress only from the
  `ingress-nginx` and `cdn-edge` namespaces, and the topology analyser rejects
  any API ingress peer that is not a `namespaceSelector` — a bare `podSelector`
  matches pods in *this* namespace, which would admit every workload deployed
  alongside the API.

The data stores are covered by `default-deny-all` (`podSelector: {}`,
`policyTypes: [Ingress, Egress]`), which is what makes the specific allows
meaningful. Without it, every other policy in the file is an addition to
"everything is permitted".

Asserted by `deploy/tests/hosting-topology.py`, checks 1, 2, 3 and 5.

### 2. The migration job is the only thing that may migrate, and it reaches only PostgreSQL

`migration-allow` selects `app.kubernetes.io/component: migration` and grants
egress to port 5432 only. DNS comes from `allow-dns-egress`.

Before Task 043 the Job was swept in by `workers-allow`, whose
`NotIn [api]` selector matched it — so a migration had broker and Redis access.
The selector is now `NotIn [api, migration, static]`. A migration that can
publish to the queue is a migration that can deliver a message against a schema
that is mid-change.

Asserted by check 4, which also fails if the migration policy names no
PostgreSQL target — a rule that opens 5432 to something else is not that.

### 3. The static origin is not the API, and does not pretend to be

`location /api/ { return 404; }` in `deploy/nginx/default.conf`. Without it the
`location /` SPA fallback answers every `/api/...` path with `index.html`, and
the client gets `200 text/html` where it expected JSON. The resulting parse
error names the frontend, so the investigation starts in the wrong place.

The same rule applies to a missing hashed asset: `try_files $uri =404` in the
assets block, not the document. `200 text/html` where the browser expected
JavaScript produces a `SyntaxError` that again names the frontend.

Both are asserted against the **running image**, not the config, in
`deploy/tests/hosting.test.sh` — because `add_header` and `try_files`
inheritance depend on which `location` block matched, and a config that reads
correctly can serve the wrong thing.

## Health, version, and what each is for

| Endpoint | Answers | Auth | Used by |
| --- | --- | --- | --- |
| `GET /health/live` | is the process up | anonymous | kubelet liveness |
| `GET /health/ready` | may this pod take traffic | anonymous | kubelet readiness, the Service |
| `GET /health` | both, as JSON, with per-dependency detail | anonymous | humans, the hosting gate |
| `GET /version` | which build, which migration head | anonymous | the release gate, support |

All four are anonymous, and the reason is operational rather than a convenience:
a kubelet, a CDN, and a rollback script are the callers most likely to need them
during an incident, and they are the ones least likely to hold a valid session. A
monitoring endpoint that 401s is a monitoring endpoint that reports nothing.

`/health/ready` includes **migration currency** (`MigrationCurrencyCheck`).
Reachable-database and matching-schema are different facts, and only the second
one stops a pod whose build is ahead of the database from receiving traffic.

`/version` deliberately does NOT fail when the database is unreachable. It
reports `migrationHead: "unknown"` and `migrationsPending: -1` instead, because
a version answer that 500s during a database incident removes the one endpoint
that could have said which build is running. The authoritative signal is
`/health`, which is allowed to fail and does.

## The `CHANGE_ME` inventory

Every one of these is a placeholder that must be substituted before a deploy.
`deploy/verify.sh` and `deploy/tests/hosting.test.sh` both fail on a `CHANGE_ME`
reaching a deployed artefact.

| Where | What |
| --- | --- |
| `deploy/k8s/*-deployment.yaml` | `ghcr.io/CHANGE_ME/…` image org |
| `deploy/k8s/networkpolicies.yaml` | managed PG / broker / storage CIDRs |
| `deploy/k8s/ingress.yaml` | API hostname, cert-manager issuer |
| `deploy/cdn/origin.json` | static origin host, version-pin path |
| `deploy/helm/dubbing/values-prod.yaml` | registry, OTLP endpoint |
| `.github/CODEOWNERS` | every owner handle |

## What is deliberately not in this picture

- **No direct browser→API path that bypasses the ingress.** The API's Service is
  `ClusterIP`; nothing else in the namespace can reach it except what
  `api-allow` names.
- **No self-hosted data stores in prod.** The manifests do not deploy PostgreSQL,
  RabbitMQ, Redis or MinIO statefulsets; prod uses managed services reached over
  the documented CIDRs. The policies allow both topologies because staging uses
  in-cluster statefulsets.
- **No `NodePort` or `LoadBalancer` on the static Service.** A second public
  address for the document is the route `static-allow` exists to prevent.
