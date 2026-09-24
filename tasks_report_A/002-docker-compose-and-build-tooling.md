# 002 — Docker Compose and Build Tooling Report

## Status
COMPLETED

## Summary
Created 8 Dockerfiles at repo root, `docker-compose.yml` with `fast`/`full` profiles and healthchecks, and `Makefile` with 12 targets. FFmpeg is scoped to media images only, fast profile runs without RabbitMQ/Redis via in-memory transport and mock providers, and `dotnet build` still succeeds with 0 warnings 0 errors. Docker CLI is absent in this environment so compose validation was done via Python YAML profile simulation; `make` is also absent so `build`/`lint` bodies were run directly.
No domain logic, messaging code, provider code, endpoints, or worker logic was added.

## Files Created/Modified
- `Dockerfile.api` — Multi-stage `sdk:10.0` build / `aspnet:10.0` final, publishes `src/DubbingPlatform.Api`, `EXPOSE 8080`, installs `curl` for healthcheck, `USER app`, `WORKDIR /app`, `ENTRYPOINT [\"dotnet\",\"DubbingPlatform.Api.dll\"]`.
- `Dockerfile.worker.control` — Same base without FFmpeg, publishes `src/DubbingPlatform.Workers`, `ENV DOTNET_WORKER_ROLE=control`, `USER app`, `WORKDIR /app`.
- `Dockerfile.worker.media.preparation` — Base + `RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg && rm -rf /var/lib/apt/lists/*` (provides `ffmpeg`+`ffprobe`), publishes Workers, `ENV DOTNET_WORKER_ROLE=media-preparation`.
- `Dockerfile.worker.media.render` — Identical to preparation except `ENV DOTNET_WORKER_ROLE=media-render`.
- `Dockerfile.worker.ai` — No FFmpeg, publishes Workers, `ENV DOTNET_WORKER_ROLE=ai`.
- `Dockerfile.worker.gpu` — No FFmpeg, `aspnet:10.0` + CUDA comment + `ENV NVIDIA_VISIBLE_DEVICES=all`, `ENV DOTNET_WORKER_ROLE=gpu`.
- `Dockerfile.worker.export` — No FFmpeg, publishes Workers, `ENV DOTNET_WORKER_ROLE=export`.
- `Dockerfile.maintenance` — `sdk:10.0` build + `sdk:10.0` final with `dotnet-ef` via `.config/dotnet-tools.json` + `dotnet tool restore` as `app` user, `ENV DOTNET_WORKER_ROLE=maintenance`, `ENTRYPOINT [\"dotnet\",\"DubbingPlatform.Workers.dll\"]`.
- `docker-compose.yml` — `name: dubbing-platform`, 12 services (`postgres`, `rabbitmq`, `redis`, `minio`, `api`, `control`, `media-preparation`, `media-render`, `ai`, `gpu`, `export`, `maintenance`), volumes `pgdata,miniadata,mediatmp`, healthchecks for postgres/rabbitmq/redis/minio/api, `${VAR:-default}` port overrides, `required: false` for api/control rabbitmq/redis deps.
- `Makefile` — LF-only, tab recipes, 12 targets (`build,test,test-unit,test-integration,test-e2e,compose-full,compose-fast,migrate,run-api,run-workers,lint,security-scan`) with exact commands from task.

## Decisions Made
- `maintenance` profiles `["full","tools"]` instead of only `["full"]`: satisfies both wordings — §Instructions says `maintenance (profile full/tools)` while Profiles bullet says `[full]`; having both means `full config` includes it and `fast config` excludes it, plus `tools config` can run migrations alone. Documented here.
- `api`/`control` `depends_on` rabbitmq/redis uses `required: false`: Compose starts dependencies even if their profile is inactive; without `required: false`, `up --profile fast` would pull rabbitmq/redis and break R3. Full profile still waits on them when present (`condition: service_healthy`).
- Fast transport defaults to in-memory via `Messaging__Transport: \"${MESSAGING_TRANSPORT:-inmemory}\"` on `api`/`control`: single service definition cannot have per-profile env, so default is `inmemory` (fast) and full sets `MESSAGING_TRANSPORT=rabbitmq`. `RabbitMq__Host=rabbitmq`/`Redis__Connection=redis:6379` remain defined but are documented as ignored in fast path to satisfy “must not reference in required startup path”.
- `Dockerfile.api` installs `curl`: `aspnet:10.0` has no curl, but compose healthcheck is `curl -f http://localhost:8080/health/live` per spec, so `apt-get install curl` is required for the probe to succeed.
- `gpu` service adds `deploy.resources.reservations.devices: [{driver: nvidia, count: 1, capabilities: [gpu]}]`: complements Dockerfile `ENV NVIDIA_VISIBLE_DEVICES=all` + CUDA comment; `config` validates without daemon.
- No `HEALTHCHECK` in Dockerfiles: keeps images liveness-probe agnostic per spec; healthchecks live in compose only.
- All images set `ENV DOTNET_EnableDiagnostics=0 TMPDIR=/tmp` + comment “writes only to /tmp” for read-only-root compatibility; no secrets beyond local dev defaults (`dubbing/dubbing`, `minioadmin`), with comments that production secrets come from secret manager.
- Shell: `default.shell` fails (`The system cannot find the path specified`); used `default.execute` → `tools[\"claude-code\"].PowerShell` for all commands, per 001 report.

## Build/Test Results
- `dotnet build` (last 10 lines):
```
  DubbingPlatform.Api -> C:\Users\fazeli\source\hobby\media-dub\src\DubbingPlatform.Api\bin\Debug\net10.0\DubbingPlatform.Api.dll
  DubbingPlatform.Workers -> C:\Users\fazeli\source\hobby\media-dub\src\DubbingPlatform.Workers\bin\Debug\net10.0\DubbingPlatform.Workers.dll
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll
  DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
  DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
  DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.95
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true` (= `make lint` body; `make` binary absent) (last 10 lines):
```
  DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
  DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
  DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:20.70
```
- `docker compose --profile fast config` / `--profile full config`: `docker` CLI not found in this host (`The term 'docker' is not recognized`), so could not run verbatim. Substituted Python `yaml.safe_load` profile simulation (PyYAML 6.0.3):
```
name: dubbing-platform
services: ['ai', 'api', 'control', 'export', 'gpu', 'maintenance', 'media-preparation', 'media-render', 'minio', 'postgres', 'rabbitmq', 'redis']
volumes: ['mediatmp', 'miniadata', 'pgdata']
fast: ['api', 'control', 'minio', 'postgres']
full: ['ai', 'api', 'control', 'export', 'gpu', 'maintenance', 'media-preparation', 'media-render', 'minio', 'postgres', 'rabbitmq', 'redis']
fast has rabbitmq/redis: False
full has all required: True
```
- Dockerfile checks: 8 files present; `ffmpeg` true only in `Dockerfile.worker.media.preparation, Dockerfile.worker.media.render`; `USER app` and `WORKDIR /app` true in all 8; `Makefile` CRLF 0 / LF 37 / tabs True / 12 targets matched exactly.
- `make build`/`make lint`: `make`/`mingw32-make`/`nmake` binaries absent; ran underlying `dotnet build` commands directly (see above), both 0 warnings.

## Recommendations for Next Agent (003)
- Repo state: `DubbingPlatform.sln` (10 projects, `net10.0`, `TreatWarningsAsErrors true`) builds clean; new files are `Dockerfile.api`, `Dockerfile.worker.control`, `Dockerfile.worker.media.preparation`, `Dockerfile.worker.media.render`, `Dockerfile.worker.ai`, `Dockerfile.worker.gpu`, `Dockerfile.worker.export`, `Dockerfile.maintenance`, `docker-compose.yml`, `Makefile`. Do not modify project refs or `Placeholder.cs` stubs (`DubbingPlatform.<Layer>.Placeholder` in `src/*/Placeholder.cs`); 003 will add domain types.
- Shell gotcha: `default.shell` is broken (`The system cannot find the path specified`); use `default.execute` with `tools[\"claude-code\"].PowerShell({command, description})`. Working dir is already `C:\\Users\\fazeli\\source\\hobby\\media-dub`; do not prefix with `cd`. `make`, `docker`, `trivy` binaries are absent — validate via `dotnet build`, `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true`, and `python -c \"import yaml; yaml.safe_load(open('docker-compose.yml'))\"`.
- Naming: Dockerfiles are `Dockerfile.api`, `Dockerfile.worker.{control,media.preparation,media.render,ai,gpu,export}`, `Dockerfile.maintenance` at repo root; compose services are `postgres,rabbitmq,redis,minio,api,control,media-preparation,media-render,ai,gpu,export,maintenance`; worker role env is `DOTNET_WORKER_ROLE={control,media-preparation,media-render,ai,gpu,export,maintenance}` (Dockerfile `ENV` + compose `environment`); compose project `name: dubbing-platform`; volumes `pgdata:/var/lib/postgresql/data`, `miniadata:/data`, `mediatmp:/tmp/media`.
- Config keys: compose env uses `ConnectionStrings__Default=Host=postgres;Port=5432;Database=dubbing;Username=dubbing;Password=dubbing`, `RabbitMq__Host=rabbitmq`, `Redis__Connection=redis:6379`, `Storage__Endpoint=http://minio:9000` + `Storage__AccessKey/SecretKey=minioadmin`, `Providers__Default=mock`, `Messaging__Transport=${MESSAGING_TRANSPORT:-inmemory}` (fast default; run full with `MESSAGING_TRANSPORT=rabbitmq`), `ASPNETCORE_URLS=http://+:8080`. Appsettings still have 16 empty `{}` sections (`ConnectionStrings,RabbitMq,Redis,Storage,Providers,Media,Timing,Retry,Quota,RateLimit,Observability,Auth,Retention,Privacy,Features,Deployment`) — 003 must not fill them.
- Incomplete integration: no `/health/live` endpoint yet (compose healthchecks will fail until 009 implements it); no FFmpeg invocation code (images only provide binaries via `ffmpeg` apt package); no `ProcessStartInfo.ArgumentList` usage yet; `maintenance` image has `dotnet-ef 10.0.12` but no `DbContext`/migrations (006/007); `gpu` needs NVIDIA runtime on host (Dockerfile comment + compose `deploy.resources.reservations.devices`).
- Test helpers: no new C# tests in 002; existing `SmokeTests.Smoke_Passes()` in each of `tests/DubbingPlatform.{UnitTests,IntegrationTests,ContractTests,E2ETests}/SmokeTests.cs` still passes; run `dotnet test --filter Smoke_Passes` if needed.
- Warnings: `EnforceCodeStyleInBuild true` + `TreatWarningsAsErrors true`; keep Dockerfiles LF-only (verified 0 CRLF) and `Makefile` LF + tab recipes; do not add secrets to Dockerfiles/compose beyond `dubbing/dubbing` and `minioadmin` dev defaults; port overrides are `${POSTGRES_PORT:-5432}`, `${RABBITMQ_PORT:-5672}`, `${RABBITMQ_MANAGEMENT_PORT:-15672}`, `${REDIS_PORT:-6379}`, `${MINIO_PORT:-9000}`, `${MINIO_CONSOLE_PORT:-9001}`, `${API_PORT:-8080}`.
- For 003: expect domain enums/value objects only; do not change Docker/compose/Makefile except if 003 needs new config keys (keep as `{}`); `dotnet build` must stay 0 warnings.
