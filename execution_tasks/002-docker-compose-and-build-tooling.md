# Task 2 — Docker Compose and Build Tooling

## Goal

Define all container images, local compose profiles (`fast` and `full`), healthchecks, and Makefile targets so local development and validation commands work reproducibly.

## Context

Binding decisions: FFmpeg + FFprobe present only in media worker images. `full` profile runs PostgreSQL 16, RabbitMQ 3.13, Redis 7, MinIO, API, all workers. `fast` profile runs PostgreSQL, MinIO, API, control worker with in-memory transport and mock providers (no RabbitMQ/Redis required). No shell interpolation; `ProcessStartInfo.ArgumentList` mandatory later — images must provide FFmpeg binaries. Managed services in production; compose is local-only.

## Starting State

Repository contains `DubbingPlatform.sln`, `src/*` (Domain, Contracts, Application, Infrastructure, Api, Workers), `tests/*`, `Directory.Build.props`, `global.json`, `.editorconfig`, `.gitignore`, `.config/dotnet-tools.json`, placeholder `appsettings*.json` with 15 empty sections. Solution builds. No Dockerfiles, no `docker-compose.yml`, no `Makefile` yet.

## Scope

Must implement: 8 Dockerfiles, `docker-compose.yml` with fast/full profiles, healthchecks, `Makefile` with 12 targets. Must not implement: domain logic, messaging code, provider code, API endpoints, worker logic.

## Instructions

1. Create Dockerfiles at repo root:
   - `Dockerfile.api` — base `mcr.microsoft.com/dotnet/aspnet:10.0`, build stage `mcr.microsoft.com/dotnet/sdk:10.0`, publish `src/DubbingPlatform.Api`, expose 8080, non-root `app` user, `ENTRYPOINT ["dotnet","DubbingPlatform.Api.dll"]`, liveness probe agnostic (no dependency check in image).
   - `Dockerfile.worker.control` — same base without FFmpeg, publish `src/DubbingPlatform.Workers`, `DOTNET_WORKER_ROLE=control`.
   - `Dockerfile.worker.media.preparation` — FROM base + `ffmpeg` + `ffprobe` via apt (`ffmpeg` package), publish Workers, `DOTNET_WORKER_ROLE=media-preparation`.
   - `Dockerfile.worker.media.render` — same as preparation, `DOTNET_WORKER_ROLE=media-render`.
   - `Dockerfile.worker.ai` — no FFmpeg, `DOTNET_WORKER_ROLE=ai`.
   - `Dockerfile.worker.gpu` — base `mcr.microsoft.com/dotnet/aspnet:10.0` + CUDA runtime note (comment + `ENV NVIDIA_VISIBLE_DEVICES=all`), `DOTNET_WORKER_ROLE=gpu`.
   - `Dockerfile.worker.export` — no FFmpeg, `DOTNET_WORKER_ROLE=export`.
   - `Dockerfile.maintenance` — SDK image with dotnet-ef for migrations/reconcilers, `DOTNET_WORKER_ROLE=maintenance`.
   All: `USER app`, `WORKDIR /app`, read-only-root compatible (write only to `/tmp`).
2. Create `docker-compose.yml` (Compose Spec, `name: dubbing-platform`):
   - Services: `postgres` (image `postgres:16`, env `POSTGRES_DB=dubbing POSTGRES_USER=dubbing POSTGRES_PASSWORD=dubbing`, volume `pgdata`, healthcheck `pg_isready -U dubbing`, port 5432), `rabbitmq` (`rabbitmq:3.13-management`, ports 5672/15672, healthcheck `rabbitmq-diagnostics ping`, profiles `[full]`), `redis` (`redis:7`, port 6379, healthcheck `redis-cli ping`, profiles `[full]`), `minio` (`minio/minio`, command `server /data --console-address ":9001"`, env root user/password `minioadmin`, ports 9000/9001, healthcheck `curl -f http://localhost:9000/minio/health/live`, volume `miniadata`), `api` (build Dockerfile.api, ports 8080:8080, depends_on postgres+minio always, rabbitmq/redis only in full, env ConnectionStrings__Default, Storage__Endpoint=minio:9000, Providers__Default=mock), workers `control`, `media-preparation`, `media-render`, `ai`, `gpu` (profiles full only except control also in fast), `export`, `maintenance` (profile full/tools).
   - Profiles: services postgres/minio/api/control have profiles `[fast, full]`; rabbitmq/redis/media/ai/gpu/export/maintenance have profiles `[full]`.
   - Volumes: pgdata, miniadata, mediatmp.
3. Add healthchecks for all dependency services as above; api healthcheck `curl -f http://localhost:8080/health/live`.
4. Create `Makefile` (LF) with targets:
   - `build: dotnet build`
   - `test: dotnet test`
   - `test-unit: dotnet test tests/DubbingPlatform.UnitTests`
   - `test-integration: dotnet test tests/DubbingPlatform.IntegrationTests`
   - `test-e2e: dotnet test tests/DubbingPlatform.E2ETests`
   - `compose-full: docker compose --profile full config && docker compose --profile full up -d --build`
   - `compose-fast: docker compose --profile fast config && docker compose --profile fast up -d --build`
   - `migrate: dotnet ef database update --project src/DubbingPlatform.Infrastructure --startup-project src/DubbingPlatform.Api`
   - `run-api: dotnet run --project src/DubbingPlatform.Api`
   - `run-workers: dotnet run --project src/DubbingPlatform.Workers`
   - `lint: dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true`
   - `security-scan: trivy config . || echo "trivy not installed - install to scan"`
5. Validate `docker compose --profile fast config` and `--profile full config` succeed.

## Requirements

- R1: All 8 Dockerfiles exist and build args reference correct projects.
- R2: FFmpeg only in media.preparation and media.render Dockerfiles.
- R3: Compose fast profile starts without rabbitmq/redis; full includes postgres, rabbitmq, redis, minio, api, workers.
- R4: Healthchecks defined for postgres, rabbitmq, redis, minio, api.
- R5: Makefile contains all 12 targets exactly named above.

## Edge Cases and Error Handling

- Docker unavailable: `docker compose config` must still validate (no daemon needed for config); document that `up` needs daemon.
- Port conflicts: document override via `POSTGRES_PORT`, `MINIO_PORT` env in compose (use `${POSTGRES_PORT:-5432}` pattern).
- Missing ffmpeg package: Dockerfile must `RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*`; fail build if install fails, do not continue without ffprobe.
- Fast profile must not reference rabbitmq/redis hostnames in required startup path.

## Security and Safety Requirements

- No secrets in Dockerfiles/compose beyond local dev defaults; document that production secrets come from secret manager.
- Images run as non-root `app` user.
- No secrets logged in Makefile.

## Testing

- No new C# tests. Manual validation only.

## Validation

```bash
docker compose --profile fast config
docker compose --profile full config
make lint
dotnet build
```

Expected: both config commands exit 0; build 0 warnings.

## Completion Criteria

- 8 Dockerfiles present, FFmpeg scoping correct.
- `docker-compose.yml` validates for both profiles.
- `Makefile` has 12 targets and `make build` works.

## Traceability

- Plan Section 1 actions 10–16; files Dockerfile.*, docker-compose.yml, Makefile; Assumptions 24–27, 79, 82; Integration checklist compose profiles, FFmpeg images.
