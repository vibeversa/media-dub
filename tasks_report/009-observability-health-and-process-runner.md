# 009 — Observability Health and Process Runner Report

## Status
COMPLETED

## Summary
Implemented Serilog JSON console logging with secret redaction, OpenTelemetry tracing/metrics with conditional OTLP plus Prometheus `/metrics`, separated `/health/live` (process-local) and `/health/ready` (dependency) endpoints with correlation headers, argument-safe `ProcessRunner` (ArgumentList, no shell, timeout/cancel, 1 MB truncation flags, `dubbing-*` temp isolation), `TransportOptions` (`InMemory` fast / `RabbitMq` full) profile wiring, and `LOCAL_PROFILES.md`. Live returns 200 without dependencies, ready 503 when down, metrics Prometheus text, and request logs carry `CorrelationId`; 15 unit + 5 integration tests added.
## Files Created/Modified
- `src/DubbingPlatform.Application/Options/TransportOptions.cs` — `Transport` section (`Provider=InMemory|RabbitMq`, default `InMemory`) + `TransportOptionsValidator` naming the type on failure.
- `src/DubbingPlatform.Infrastructure/Observability/SecretDestructuringPolicy.cs` — `SecretDestructuringPolicy:IDestructuringPolicy` (redacts destructured members/dictionaries with secret/password/token/key/credential names), `SecretRedactingEnricher:ILogEventEnricher` (top-level props), `RedactingJsonFormatter:ITextFormatter` (JsonFormatter + `SecretRedactor.Redact` on rendered JSON).
- `src/DubbingPlatform.Infrastructure/Observability/ObservabilitySetup.cs` — `ConfigureLogger`/`BuildLoggerConfiguration` (Information, EFCore Warning, `FromLogContext`, secret policy/enricher, `WriteTo.Console(RedactingJsonFormatter)`); `AddDubbingOpenTelemetry(builder,includeAspNetCore)` (AspNetCore opt, HttpClient, EFCore, `MassTransit` + `DubbingPlatform.*` sources; metrics AspNetCore/Http/Runtime/Process; OTLP only when `Observability:OtlpEndpoint` set; Prometheus exporter for API).
- `src/DubbingPlatform.Infrastructure/Processes/ProcessResult.cs` — `ProcessResult{ExitCode,StdOut,StdErr,TimedOut,Duration,Exe,Args,StdOutTruncated,StdErrTruncated}` + `ResourceLimits{CpuThreads,MemoryBytes}`.
- `src/DubbingPlatform.Infrastructure/Processes/ProcessRunner.cs` — `RunAsync(exe,args,workingDir,timeout,ct)` with `UseShellExecute=false` + `ArgumentList.Add` per arg, 1 MB caps with `[TRUNCATED]` + flags, kill on timeout/cancel, `dubbing-*` temp enforcement (`CreateTempWorkingDir`, restrictive chmod on Unix), redacted logging with resource metadata; `RejectIfShellChars(string)` validates exe/workingDir (args are safe literals, never passed here); disk-full throws `IOException` containing `RESOURCE_EXHAUSTED`.
- `src/DubbingPlatform.Infrastructure/Health/LiveCheck.cs` — always Healthy, tag `live`, no dependencies.
- `src/DubbingPlatform.Infrastructure/Health/NpgSqlHealthCheck.cs` — `NpgsqlConnection` open + `SELECT 1`, 5s timeouts, tag `ready`; unhealthy when connection string missing/unreachable.
- `src/DubbingPlatform.Infrastructure/Health/RabbitMqHealthCheck.cs` — TCP connect 5s, tag `ready`, full-profile only.
- `src/DubbingPlatform.Infrastructure/Health/RedisHealthCheck.cs` — `ConnectionMultiplexer` + `Ping`, 5s timeouts, tag `ready`, full-profile only.
- `src/DubbingPlatform.Infrastructure/Health/S3HealthCheck.cs` — `AmazonS3Client.ListBuckets` 5s, tag `ready`, `NormalizeEndpoint` adds scheme.
- `src/DubbingPlatform.Infrastructure/Health/FfmpegVersionCheck.cs` — `ffmpeg -version` via `ProcessRunner` 15s in temp dir, unhealthy message when missing/non-zero/timeout, tag `ready`, media roles only.
- `src/DubbingPlatform.Infrastructure/Health/ProviderConfigCheck.cs` — default provider present/enabled, tag `ready`, AI/GPU roles only.
- `src/DubbingPlatform.Infrastructure/Health/HealthRegistration.cs` — `AddApiHealthChecks`/`AddWorkerHealthChecks` (live + postgres + S3 always; Rabbit/Redis only when `Transport:Provider=RabbitMq` with `Messaging:Transport` fallback; FFmpeg for `media-*`, provider-config for `ai|gpu` via `DOTNET_WORKER_ROLE`); public `IsFullProfile/IsMediaRole/IsAiRole` for tests.
- `src/DubbingPlatform.Api/Program.cs` — `UseSerilog(ConfigureLogger)`, 14 options incl. `TransportOptions` + validators, FV, `providers` resilience, `AddDubbingOpenTelemetry(true)`, `AddApiHealthChecks`, `CorrelationId→SerilogRequestLogging→Exception` order, `MapHealthChecks(/health/live|/health/ready)` with `Tags.Contains(live|ready,Ordinal)`, `MapPrometheusScrapingEndpoint(/metrics)`; keeps `public partial class Program`.
- `src/DubbingPlatform.Workers/Program.cs` — `Services.AddSerilog(ConfigureLogger)`, same 14 options + validators, `AddDubbingOpenTelemetry(false)`, `AddWorkerHealthChecks`; no explicit `Program` partial (avoids CS0433 with Api).
- `src/DubbingPlatform.Api/appsettings.json`, `appsettings.Development.json`, `src/DubbingPlatform.Workers/appsettings.json`, `appsettings.Development.json` — added `"Transport":{"Provider":"InMemory"}`.
- `docker-compose.yml` — `Providers__Default→Providers__DefaultProvider` fix (correct `ProviderOptions.DefaultProvider` key); `Transport__Provider:${TRANSPORT_PROVIDER:-InMemory}` for api/control (fast) + legacy `Messaging__Transport` alias; `Transport__Provider:${TRANSPORT_PROVIDER:-RabbitMq}` for media/ai/gpu/export (full).
- `LOCAL_PROFILES.md` — fast (`--profile fast`, InMemory+mock, services) vs full (`--profile full`, RabbitMq) commands, live vs ready expectations, probe curls.
- `src/DubbingPlatform.Api/DubbingPlatform.Api.csproj` — added `OpenTelemetry.Instrumentation.AspNetCore/Http/EntityFrameworkCore/Runtime/Process 1.18.0(-beta/rc)`, `Exporter.OpenTelemetryProtocol 1.18.0`, `Serilog.Sinks.Console 6.1.1`.
- `src/DubbingPlatform.Workers/DubbingPlatform.Workers.csproj` — added `OpenTelemetry.Extensions.Hosting`, `Instrumentation.Http/EFCore/Runtime/Process`, `Exporter.OpenTelemetryProtocol`, `Serilog.Extensions.Hosting 10.0.0`, `Serilog.Sinks.Console`, `Diagnostics.HealthChecks 10.0.12`.
- `src/DubbingPlatform.Infrastructure/DubbingPlatform.Infrastructure.csproj` — added `Diagnostics.HealthChecks 10.0.12`, `OpenTelemetry.Extensions.Hosting`, `Instrumentation.AspNetCore/Http/EFCore/Runtime/Process`, `Exporter.OpenTelemetryProtocol`.
- `tests/DubbingPlatform.UnitTests/Processes/ProcessRunnerTests.cs` — `Rejects_Shell_Concatenation`, `Shell_Metachars_In_Args_Stay_Literal`, `Captures_Stdout_Stderr` (cmd/sh), `Captures_Stdout_From_Dotnet`, `Timeout_Kills_Process` (ping/sleep).
- `tests/DubbingPlatform.UnitTests/Observability/SecretRedactionTests.cs` — policy redacts/ignores, enricher top-level, formatter output (no `hunter2`, has `[REDACTED]` + correlation).
- `tests/DubbingPlatform.UnitTests/Health/HealthRegistrationTests.cs` — live always healthy, NpgSql unhealthy empty/down, `IsFullProfile` case-insensitive, role classification.
- `tests/DubbingPlatform.UnitTests/Options/OptionsValidationTests.cs` — added Transport defaults-pass + unknown/empty fails, InMemory/RabbitMq pass.
- `tests/DubbingPlatform.IntegrationTests/Api/HealthCheckTests.cs` — live 200 DB-down + correlation, ready 503 DB-down + correlation, fast-profile liveness, metrics Prometheus text, ready 200 when up (`[SkippableFact]`, PG16 + Minio containers).

## Decisions Made
- `TransportOptions` added as 14th options class (009 explicitly requires `Transport:{Provider}`; 008 had 13 — no conflict, validator accepts `InMemory|RabbitMq` case-insensitive, default `InMemory` so missing section passes fast by design).
- Compose keeps `Messaging__Transport` as legacy alias alongside `Transport__Provider` because 011 spec already expects `Transport:Provider`; `HealthRegistration.IsFullProfile` reads `Transport:Provider` then `Messaging:Transport` fallback. `Providers__Default` fixed to `Providers__DefaultProvider` (old key never bound to `DefaultProvider`).
- Full-profile gating is config-driven (`Transport:Provider=RabbitMq`), not profile-name sniffing: Rabbit/Redis checks registered only when full, so fast/`InMemory` readiness never requires broker/Redis (R5). S3 + postgres always registered (both profiles have MinIO + PG).
- `RedactingJsonFormatter` wraps `JsonFormatter` (satisfies “WriteTo.Console(new JsonFormatter())” + “filter out secret values” simultaneously); destructuring policy handles complex objects/dictionaries, enricher handles top-level props, formatter is final backstop on rendered JSON. All use the same 5 fragments as `SecretRedactor`/`ConfigurationHashCalculator`.
- `ProcessResult` adds `StdOutTruncated/StdErrTruncated` beyond the 7 listed fields to satisfy “truncate + flag” edge case; marker `[TRUNCATED]` appended. `RejectIfShellChars(string)` validates exe/workingDir only — args via `ArgumentList` are safe literals and must NOT be rejected (proven by `; rm -rf /` arg staying literal). “AND caller attempted shell” satisfied by hard `UseShellExecute=false` + `InvalidOperationException` if ever true.
- Temp enforcement: null/empty workingDir is caller error (`ArgumentException`); provided dirs under `GetTempPath()` must start `dubbing-`, outside-temp dirs (e.g. `/tmp/media`) allowed; `CreateTempWorkingDir()` creates `dubbing-{N}` with Unix 700 best-effort. Disk-full maps to `IOException` containing `RESOURCE_EXHAUSTED` (compatible with `ErrorCodes.ResourceExhausted` without referencing Api from Infrastructure).
- Workers OTel omits AspNetCore instrumentation (`includeAspNetCore:false`); only Api calls `MapPrometheusScrapingEndpoint` (workers have no HTTP). Shared `ObservabilitySetup` lives in Infrastructure so both hosts share Serilog/OTel; Infrastructure therefore references OTel instrumentation packages (compile-only need).
- `WebApplicationFactory<CorrelationIdMiddleware>` retained (not `<Program>`) due to Api+Workers global `Program` collision (CS0433). HTTP envelope tests self-host Kestrel; health tests use real factory (middleware + health + metrics) with `ConfigureAppConfiguration` overrides.
- `MinioBuilder("minio/minio:latest")` (parameterless obsolete-as-error); `S3HealthCheck` needs only `ListBuckets` (no bucket pre-creation for ready-200).
- `UseSerilogRequestLogging` placed after `CorrelationIdMiddleware` so request logs (`Serilog.AspNetCore.RequestLoggingMiddleware`) carry `CorrelationId` (proven in live logs).

## Build/Test Results
- `dotnet build --nologo -v q` (last 4):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.39
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true` (last 4):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:22.55
```
- `dotnet test --filter FullyQualifiedName~ProcessRunnerTests` (last 2):
```
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 2 s - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~HealthCheckTests` (last 2):
```
Passed!  - Failed:     0, Passed:     4, Skipped:     1, Total:     5, Duration: 18 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- Full suites: UnitTests `Passed: 196, Skipped: 0` (181 prior + 15 new); IntegrationTests `Passed: 11, Skipped: 7` (6 prior Docker skips + ready-up skip).
- Live curl (`Invoke-WebRequest http://127.0.0.1:8080/health/live`): `200 Healthy` with `X-Correlation-Id: 074d8fbabef34ab5af1beea99e15efbd`; request log shows `Serilog.AspNetCore.RequestLoggingMiddleware ... CorrelationId:074d... StatusCode:200`.
- Metrics curl (`/metrics`): `200 text/plain; version=0.0.4; charset=utf-8` with correlation header, body starts `# TYPE target_info gauge ... target_info{service_name="dubbing-platform"...} 1`.
- Ready with no deps: `503` (postgres “not configured”, storage “unreachable”), liveness stays `200`.

## Recommendations for Next Agent (010)
- Repo state: builds 0/0 incl. lint (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest`). `Program.cs` (Api) wires 14 options + Serilog + OTel + health + `/metrics` + FV + `providers` resilience; Workers wires same options + OTel (no AspNetCore) + role health. No endpoints, no `AddDbContext`, no messaging runtime/storage adapters/providers — per 009 scope. `appsettings.*.json` have `Transport:InMemory` defaults; compose drives `Transport__Provider` per profile.
- Key files: `Infrastructure/Observability/ObservabilitySetup.cs` (`ConfigureLogger`, `BuildLoggerConfiguration`, `AddDubbingOpenTelemetry(builder,bool)`); `Infrastructure/Observability/SecretDestructuringPolicy.cs` (3 classes); `Infrastructure/Processes/ProcessRunner.cs` (`RunAsync`, `RejectIfShellChars`, `CreateTempWorkingDir`, `MaxOutputBytes=1MB`) + `ProcessResult.cs` (`ProcessResult`, `ResourceLimits`); `Infrastructure/Health/*.cs` (`LiveCheck/NpgSql/RabbitMq/Redis/S3/FfmpegVersion/ProviderConfig` + `HealthRegistration.AddApiHealthChecks/AddWorkerHealthChecks`, public `IsFullProfile/IsMediaRole/IsAiRole`, `WorkerRoleEnvironmentVariable="DOTNET_WORKER_ROLE"`); `Application/Options/TransportOptions.cs` (`SectionName="Transport"`, `InMemory/RabbitMq` consts); `LOCAL_PROFILES.md`; `Api/Program.cs` health mapping predicates use `Tags.Contains(x,Ordinal)`.
- Conventions: file-scoped namespaces, 4-space/LF, `StringComparison.Ordinal(IgnoreCase)` everywhere, `ConfigureAwait(false)` in library code (hosts/tests use `true`/none per existing style), `[GeneratedRegex]` not needed here; `#pragma CA1031` only for Docker-availability probes with justification; test `Options` namespace collides with `Microsoft.Extensions.Options` — fully qualify `Options.DefaultName`.
- Gotchas: shell is PowerShell (`Select-Object`, no `||`); no Docker on this host — Docker tests must be `[SkippableFact]` + `Skip.If` (`Xunit.SkippableFact 1.3.12` in IntegrationTests only); `WebApplicationFactory<Program>` ambiguous — use `CorrelationIdMiddleware` marker; `WithWebHostBuilder.Configure` endpoints bypass `Program` middleware — self-host Kestrel for envelope tests, use real factory + `ConfigureAppConfiguration` for health; FV mixed 12.1.1/11.x works, don’t downgrade without need; `MinioBuilder` requires image arg; `MapPrometheusScrapingEndpoint` is in `Microsoft.AspNetCore.Builder` (no extra using with ImplicitUsings); `AddOpenTelemetry` needs `using Microsoft.Extensions.DependencyInjection` + `OpenTelemetry` + `Extensions.Hosting` package; `IDestructuringPolicy.TryDestructure` needs `[NotNullWhen(true)] out LogEventPropertyValue?`.
- Incomplete integration points for 010+: messaging contracts (15 messages, version policy, queue names) will use `TransportOptions.Provider` (`InMemory` fast vs `RabbitMq` full) — read via `IOptions<TransportOptions>` or `IConfiguration["Transport:Provider"]` with `Messaging:Transport` fallback as in `IsFullProfile`; `ProcessRunner` is ready for FFmpeg/FFprobe tasks (019/020/031/033) — always pass `ffmpeg` + args via `ArgumentList`, workingDir under `CreateTempWorkingDir()` or `/tmp/media` (outside-temp allowed); health `FfmpegVersionCheck`/`ProviderConfigCheck` already role-gated, reuse for worker readiness; OTel sources `MassTransit` + `DubbingPlatform.Orchestration/Media/Providers/FFmpeg` already registered — providers/workers should use `ActivitySource("DubbingPlatform.Providers")` etc.; secrets via env (`Storage__SecretKey` etc.), never logged (enricher + formatter + `SecretRedactor`).
- Config keys: `Transport:Provider`, `Observability:ServiceName/OtlpEndpoint/EnableTracing/EnableMetrics`, `Storage:Endpoint/Bucket/UseSsl/AccessKey/SecretKey`, `Providers:DefaultProvider`, `RabbitMq:Host/Port`, `Redis:Connection`, `ConnectionStrings:Default`; compose env `TRANSPORT_PROVIDER` (fast default `InMemory`, full default `RabbitMq`), `MESSAGING_TRANSPORT` legacy alias.
