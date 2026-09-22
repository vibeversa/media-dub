# Task 9 — Observability Health and Process Runner

## Goal

Implement structured logging, tracing, metrics, separated health checks, and argument-safe process execution with fast/full local profiles.

## Context

Binding: Serilog structured props (CorrelationId,TenantId,ProjectId,ProcessingRunId,StageType,ScopeType,ScopeId,SegmentId,Attempt,Provider,Model,LeaseToken). OTel traces ASP.NET Core+HttpClients+EFCore+MassTransit+provider calls+FFmpeg. Prometheus /metrics. Health: liveness process-local only; readiness requires dependencies; worker-specific readiness (storage/queue/provider/FFmpeg where relevant); FFmpeg version check only in media workers; liveness never depends on PG/Rabbit/Redis/storage. ProcessRunner: ArgumentList only, no shell, cancellation/stdout-stderr/timeout/temp-dir/resource metadata. Local fast: PG+MinIO+mock+in-memory transport; full: PG+Rabbit+Redis+MinIO+workers.

## Starting State

Middleware/options/error envelope exist. No Serilog/OTel/metrics/health/ProcessRunner. Compose profiles defined but appsettings not wired.

## Scope

Must implement: Serilog config, OTel, /metrics, /health/live + /health/ready, FFmpeg health, ProcessRunner, profile wiring. Must not implement: messaging runtime, storage adapters, providers.

## Instructions

1. Configure Serilog in `src/DubbingPlatform.Api/Program.cs` and `Workers/Program.cs`: `WriteTo.Console(new JsonFormatter())`, enrich with `LogContext` + props above; filter out secret values via destructuring policy that redacts keys matching secret/password/token/key. MinimumLevel Information, override Microsoft.EntityFrameworkCore Warning.
2. Configure OTel in both Programs: `AddOpenTelemetry().WithTracing(t=>t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddEntityFrameworkCoreInstrumentation().AddSource("MassTransit").AddSource("DubbingPlatform.*")).WithMetrics(m=>m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation().AddRuntimeInstrumentation().AddProcessInstrumentation())`, OTLP exporter if `Observability:OtlpEndpoint` set; Prometheus exporter via `MapPrometheusScrapingEndpoint("/metrics")` (package OpenTelemetry.Exporter.Prometheus.AspNetCore).
3. Health: `AddHealthChecks()`; `MapHealthChecks("/health/live", predicate: r=>r.Tags.Contains("live"))` with self-check `LiveCheck : IHealthCheck` always Healthy; `MapHealthChecks("/health/ready", predicate: r=>r.Tags.Contains("ready"))` with `NpgSqlHealthCheck` (tag ready), RabbitMQ TCP check (full only), Redis check (full only), S3 check (MinIO list-bucket, tag ready). Worker-specific: media workers add `FfmpegVersionCheck` (runs `ffmpeg -version` via ProcessRunner, tag ready); ai workers add provider-config check. Liveness must not include any dependency check.
4. Implement `src/DubbingPlatform.Infrastructure/Processes/ProcessRunner.cs` namespace `DubbingPlatform.Infrastructure.Processes`: `Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, string workingDir, TimeSpan timeout, CancellationToken ct)` using `ProcessStartInfo { FileName=exe, UseShellExecute=false, RedirectStandardOutput=true, RedirectStandardError=true }` + `ArgumentList.Add(arg)` per arg (never string concat); capture stdout/stderr (max 1MB each, truncate), exit code, timed out flag; kill on timeout/cancel; enforce temp dir under `Path.GetTempPath()/dubbing-*`; record `record ResourceLimits { int CpuThreads; long MemoryBytes; }` metadata in logs. Provide `static void RejectIfShellChars(string)` — unit test hook that throws if arg contains shell metachars AND caller attempted shell (document: args passed via ArgumentList so metachars safe as literals; method asserts UseShellExecute==false).
5. `ProcessResult { int ExitCode; string StdOut; string StdErr; bool TimedOut; TimeSpan Duration; string Exe; IReadOnlyList<string> Args; }`.
6. Wire profiles: `appsettings.json` `Transport: { Provider: "RabbitMq|InMemory" }`; fast sets InMemory + mock providers; full sets RabbitMq. Document in `LOCAL_PROFILES.md` (create at root): fast command `docker compose --profile fast up`, full command, /health/live vs /ready expectations.
7. Register FluentValidation + options validation already done; ensure health endpoints emit correlation ID header.

## Requirements

- R1: /health/live always 200 without dependencies; /health/ready reflects deps.
- R2: /metrics returns Prometheus text.
- R3: Logs include CorrelationId + listed props when present.
- R4: ProcessRunner uses ArgumentList, no shell, supports timeout/cancel/capture.
- R5: Fast profile runs without RabbitMQ/Redis.

## Edge Cases and Error Handling

- FFmpeg missing: readiness Unhealthy with message, liveness still Healthy.
- Process timeout: kill, return TimedOut=true, log exe+args+duration (no secrets).
- Stdout overflow: truncate + flag.
- Disk full on temp: throw IOException with RESOURCE_EXHAUSTED code mapping.

## Security and Safety Requirements

- No shell interpolation anywhere; UseShellExecute=false enforced; no secrets in logs/traces; temp dirs isolated per execution with restrictive perms.

## Testing

Create `tests/DubbingPlatform.UnitTests/Processes/ProcessRunnerTests.cs`: `Rejects_Shell_Concatenation` (assert UseShellExecute false via reflection/behavior; `echo` with `; rm` stays literal), `Timeout_Kills_Process`, `Captures_Stdout_Stderr`. Integration `HealthCheckTests.cs` (WebApplicationFactory): /health/live 200 with DB down; /health/ready 503 when DB down, 200 when up.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ProcessRunnerTests
dotnet test --filter FullyQualifiedName~HealthCheckTests
curl -f http://localhost:8080/health/live
curl -f http://localhost:8080/metrics
```

## Completion Criteria

- Observability + health separation + ProcessRunner + profiles work; tests pass.

## Traceability

- Plan Section 3 actions 8–15, 22–24; Assumptions 27–29,75,82; Integration checklist Serilog/OTel/Prometheus/health/fast+full.
