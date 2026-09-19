# Local Profiles

Fast and full local profiles share PostgreSQL 16 + MinIO. Fast runs without
RabbitMQ/Redis (in-memory transport + mock providers); full adds RabbitMQ,
Redis, and all workers.

## Fast (default local)

```bash
docker compose --profile fast up --build
```

Services: `postgres`, `minio`, `api`, `control`.
Transport: `Transport__Provider=InMemory` (default in `appsettings.json`;
compose default `TRANSPORT_PROVIDER=InMemory`). Providers:
`Providers__DefaultProvider=mock`.

## Full

```bash
docker compose --profile full up --build
# Or explicitly:
TRANSPORT_PROVIDER=RabbitMq docker compose --profile full up --build
```

Services: fast plus `rabbitmq`, `redis`, `media-preparation`, `media-render`,
`ai`, `gpu`, `export`, `maintenance`.
Transport: `TRANSPORT_PROVIDER=RabbitMq` (`Transport__Provider=RabbitMq`).
`media-*`/`ai`/`gpu`/`export` default to `RabbitMq` when the variable is unset.

## Health expectations

- `GET /health/live` — liveness, process-local only. Always `200 Healthy`
  when the process runs; never checks PostgreSQL, RabbitMQ, Redis, storage,
  providers, or FFmpeg.
- `GET /health/ready` — readiness, reflects dependencies. `200` only when all
  registered readiness checks pass:
  - `postgres` (always): `SELECT 1` succeeds.
  - `storage` (always): S3 `ListBuckets` succeeds.
  - `rabbitmq`, `redis` (full profile only, `Transport:Provider=RabbitMq`):
    TCP/connect + ping succeed. Not registered in fast/`InMemory`, so fast
    readiness does not require RabbitMQ/Redis (R5).
  - Media workers (`DOTNET_WORKER_ROLE=media-*`): `ffmpeg -version` via
    `ProcessRunner` succeeds; missing FFmpeg makes readiness `503 Unhealthy`
    while liveness stays `200`.
  - AI workers (`DOTNET_WORKER_ROLE=ai|gpu`): default provider enabled.
- `GET /metrics` — Prometheus text exposition (API only). Requires
  `Observability:EnableMetrics=true` (default).

## Probes

```bash
curl -f http://localhost:8080/health/live
curl -f http://localhost:8080/health/ready
curl -f http://localhost:8080/metrics
```

Compose `api` healthcheck probes `/health/live` only, so an unhealthy
dependency never restarts a live process via liveness.
