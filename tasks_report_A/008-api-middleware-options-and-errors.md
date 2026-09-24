# 008 — API Middleware Options and Errors Report

## Status
COMPLETED

## Summary
Implemented 13 options classes with exact defaults plus per-class `IValidateOptions<T>` validators (all bound with `ValidateDataAnnotations().ValidateOnStart()`), `CorrelationIdMiddleware` (`X-Correlation-Id` generate/propagate/return), `ExceptionHandlingMiddleware` with the exact `{error:{code,message,correlationId,details}}` envelope covering all 29 public error codes, 7 application exceptions, `[Secret]` marking with `SecretRedactor`, and the `providers` HttpClient with `AddStandardResilienceHandler` (exponential+jitter retry on 429/5xx only, 5-throughput/30s-break circuit breaker, 30s total timeout). Added 54 unit tests and 6 integration tests; verified invalid options fail startup naming the options type.

## Files Created/Modified
- `src/DubbingPlatform.Application/Options/SecretAttribute.cs` — `[Secret]` marker attribute for secret-bearing options properties.
- `src/DubbingPlatform.Application/Options/ObservabilityOptions.cs`, `MediaOptions.cs`, `RetryOptions.cs`, `TimingOptions.cs`, `StorageOptions.cs`, `ProviderOptions.cs`, `QuotaOptions.cs`, `RateLimitOptions.cs`, `PrivacyOptions.cs`, `FeatureOptions.cs`, `DeploymentOptions.cs`, `AuthOptions.cs`, `RetentionOptions.cs` — 13 options classes with `SectionName` consts, exact task defaults, DataAnnotations, and co-located `IValidateOptions<T>` validators whose `Fail()` messages name the options type.
- `src/DubbingPlatform.Application/Errors/ErrorCodes.cs` — 29 `const` codes (PascalCase identifiers, underscore values), `All` (29 entries), `IsKnown`, `StatusFor` default-status table.
- `src/DubbingPlatform.Application/Exceptions/AppException.cs` — abstract base (`ErrorCode`, abstract `StatusCode`); `ErrorCodeException.cs` (any catalog code, rejects unknown with `ArgumentException`); `NotFoundException.cs`, `ConflictException.cs`, `ForbiddenException.cs`, `QuotaExceededException.cs`, `RateLimitedException.cs` (fixed code + status via `StatusFor`).
- `src/DubbingPlatform.Application/Security/SecretRedactor.cs` — `[GeneratedRegex]` redaction of `secret|password|token|key|credential` assignments and bearer tokens to `[REDACTED]`; `IsSensitiveKey` (same 5 fragments as `ConfigurationHashCalculator`); `RedactDetails` for envelopes.
- `src/DubbingPlatform.Api/Middleware/ErrorResponse.cs` — `ErrorResponse(ErrorBody)` / `ErrorBody(Code,Message,CorrelationId,Details)` records serialized camelCase.
- `src/DubbingPlatform.Api/Middleware/CorrelationIdMiddleware.cs` — reuses/propagates `X-Correlation-Id` (accepts only `[A-Za-z0-9-_]{1,128}`, else generates `Guid:N`), sets Items/TraceIdentifier/response header, Serilog `LogContext`, `Activity` tag `correlation.id`; static `GetCorrelationId` fallback.
- `src/DubbingPlatform.Api/Middleware/ExceptionHandlingMiddleware.cs` — maps ValidationException/DomainException→400, UnauthorizedAccess→401, Forbidden→403, NotFound→404, Conflict→409, AppException→own code/status, else 500 with generic message; redacts messages, field details, never stack traces; justified single CA1031 boundary pragma.
- `src/DubbingPlatform.Api/Resilience/ProviderResilience.cs` — `HttpClientName="providers"`; `Configure(HttpStandardResilienceOptions)`: retry 3× exponential+jitter with `HttpClientResiliencePredicates.IsTransient` (429/5xx only, 4xx fail fast), CB sampling 30s/min-throughput 5/break 30s, total timeout 30s.
- `src/DubbingPlatform.Api/Program.cs` — 13× `AddOptions.BindConfiguration.ValidateDataAnnotations.ValidateOnStart` + 13× `AddSingleton<IValidateOptions<T>>`, `AddFluentValidationAutoValidation`, `AddValidatorsFromAssembly(Application)`, `AddHttpClient("providers").AddStandardResilienceHandler(Configure)`, middleware order Correlation→Exception; keeps `public partial class Program`.
- `src/DubbingPlatform.Api/DubbingPlatform.Api.csproj` — added `Microsoft.Extensions.Http.Resilience 10.0.0`.
- `src/DubbingPlatform.Application/DubbingPlatform.Application.csproj` — added `Microsoft.Extensions.Options 10.0.12` (matches 10.0.x pins).
- `tests/DubbingPlatform.UnitTests/Middleware/ErrorEnvelopeTests.cs` — 38-case mapping theory (explicit exceptions + all 29 codes via `ErrorCodeException`), 29-code count test, envelope-shape, 500-hides-detail, secret-redaction, validation-details, 3 correlation facts (46 tests).
- `tests/DubbingPlatform.UnitTests/Options/OptionsValidationTests.cs` — all-13-defaults-pass plus invalid Media/Retry/Timing/Provider/Retention/Quota and DataAnnotations facts (8 tests).
- `tests/DubbingPlatform.IntegrationTests/Api/ErrorEnvelopeApiTests.cs` — 3 HTTP facts over Kestrel with production middleware pair + test endpoint (400 envelope + header echo, generated ID, 200 path) and 3 factory facts (options defaults, `providers` client, FV descriptors).

## Decisions Made
- Count discrepancy (scope says 12, R1 says 14, spec lists 13 incl. Auth+Retention): implemented exactly the 13 specified classes; no invented 14th (would be over-implementation).
- `MediaOptions.MaxUploadBytes` is `long`, not `int`: 5368709120 overflows `int`; DataAnnotations use `[Range(typeof(long),...)]`.
- `ProviderOptions.RoutePriority` is `Dictionary<string,string[]>`, not `Dictionary<string,string>`: the mandated default (`{"transcription":["mock"],...}`) is array-valued and priority ordering needs lists; `Enabled` defaults to `{"mock":true}` (unspecified, consistent with `DefaultProvider="mock"`).
- `ErrorCodes` identifiers are PascalCase with underscore string values (avoids CA1707-style naming friction while keeping exact wire codes).
- Missing-section edge case: since every member has a mandated valid default, a missing section binds defaults and passes by design; fail-fast (naming the type) applies to invalid values — proven live (`Media__MaxUploadBytes=0` → `OptionsValidationException` naming `MediaOptions` from both DataAnnotations and `IValidateOptions`).
- Incoming correlation IDs are allow-listed (`[A-Za-z0-9-_]{1,128}`); anything else (spaces, injection chars) is replaced, never echoed raw.
- `Microsoft.Extensions.Http.Resilience 10.0.0` (only 10.0.x line available; 10.10.0 latest avoided to stay with repo's 10.0.x wave); v10 renamed the options type to `HttpStandardResilienceOptions` — used that.
- Integration HTTP tests self-host Kestrel with the production middleware pair instead of `WithWebHostBuilder`-appended endpoints: appended endpoints run outside `Program`'s middleware (proven: raw `DomainException` escaped), so they cannot test the envelope; service wiring is still asserted against the real factory.
- `WebApplicationFactory<CorrelationIdMiddleware>` (Api-assembly marker) instead of `<Program>`: Workers' top-level `Program` collides in the global namespace (CS0433).
- FV assertion checks for any `FluentValidation*`-assembly service descriptor rather than `FluentValidationAutoValidationConfiguration` resolution: 11.3.1 does not register that object as a service (probed: `GetService` returns null despite the call executing).
- No `ILogger` in middleware (avoids secret-leak surface and logging-analyzer friction); request logging stays with task 009. No `FailureCategory` (provider-task scope, not 008).

## Build/Test Results
- `dotnet build --nologo -v q` (last 4 lines):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.47
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true --nologo -v q` (last 4 lines):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:25.17
```
- `dotnet test --filter FullyQualifiedName~ErrorEnvelopeTests` (last 2 lines):
```
Passed!  - Failed:     0, Passed:    46, Skipped:     0, Total:    46, Duration: 221 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~OptionsValidationTests` (last 2 lines):
```
Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 68 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- Full suites: UnitTests `Passed: 181, Skipped: 0` (127 prior + 54 new); IntegrationTests `Passed: 7, Skipped: 6` (6 Docker-dependent 007 tests skip, 6 new + smoke pass).
- R4 live proof (`Media__MaxUploadBytes=0 dotnet run --no-build`, first 4 lines):
```
fail: Microsoft.Extensions.Hosting.Internal.Host[11]
      Hosting failed to start
      Microsoft.Extensions.Options.OptionsValidationException: DataAnnotation validation failed for 'MediaOptions' members: 'MaxUploadBytes' with the error: 'The field MaxUploadBytes must be between 1 and 9223372036854775807.'.; MediaOptions.MaxUploadBytes must be at least 1.
```

## Recommendations for Next Agent (009)
- Repo state: builds 0/0 including lint (`TreatWarningsAsErrors`, `EnforceCodeStyleInBuild`, `AnalysisLevel latest`). `Program.cs` wires options/middleware/FV/resilience but has no endpoints, no `AddDbContext`, no Serilog/OTel/health — all 009 scope. appsettings.json still has 16 empty `{}` sections (binding yields code defaults; populate real values in 009/deploy tasks, never secrets).
- Key files: options+validators `src/DubbingPlatform.Application/Options/*.cs` (each exposes `SectionName`; validators are `XOptionsValidator : IValidateOptions<XOptions>`); catalog `src/DubbingPlatform.Application/Errors/ErrorCodes.cs` (`All`, `IsKnown`, `StatusFor`); exceptions `src/DubbingPlatform.Application/Exceptions/*.cs` (`AppException.ErrorCode/StatusCode`); redactor `src/DubbingPlatform.Application/Security/SecretRedactor.cs` (`Redact`, `IsSensitiveKey`, `RedactDetails`, `RedactedValue`); middleware `src/DubbingPlatform.Api/Middleware/` (`CorrelationIdMiddleware.HeaderName/ItemKey/GetCorrelationId`, `ExceptionHandlingMiddleware.Map` internal for tests, `ErrorResponse/ErrorBody`); resilience `src/DubbingPlatform.Api/Resilience/ProviderResilience.cs` (`HttpClientName="providers"`, `Configure`).
- Conventions: file-scoped namespaces, 4-space indent, LF; `StringComparison.Ordinal` everywhere; `[GeneratedRegex]` partials for regex (SYSLIB); single justified `#pragma warning disable CA1031` only at middleware boundary; test namespace `DubbingPlatform.UnitTests.Options` collides with `Microsoft.Extensions.Options` — fully qualify `Options.DefaultName`.
- Gotchas: `default.shell` is PowerShell (no `tail`/`grep`; use `Select-Object`/`Select-String`). No `docker` on this host — 007 tests skip via `[SkippableFact]`; 008 tests need no Docker. `WebApplicationFactory<Program>` is ambiguous (Workers also has top-level `Program`) — use an Api-assembly marker type. `WithWebHostBuilder.Configure` endpoints run outside `Program` middleware — test HTTP envelopes via self-hosted pipeline mirroring `Program` order. Mixed FV versions (core 12.1.1 via Application, AspNetCore/DI 11.x) work for registration; if 009+ validators hit runtime mismatch, align Application to 11.11.0 (in local NuGet cache).
- Incomplete integration points for 009: wire Serilog request logging (middleware already pushes `CorrelationId` to `LogContext`), OTel tracing (middleware already tags `correlation.id` on `Activity.Current`), health endpoints (separate liveness/readiness per plan), `ProcessRunner`, and `AddDbContext<AppDbContext>` with `UseNpgsql/UseSnakeCaseNamingConvention/TenantSessionInterceptor` (+ `TenantModelCacheKeyFactory` when pooling). Resilience pipeline builds on first `providers` send — covered by provider tasks (014+).
- Config keys: options sections `Observability/Media/Retry/Timing/Storage/Providers/Quota/RateLimit/Privacy/Features/Deployment/Auth/Retention`; secrets only via env (`Storage__SecretKey` etc.), `[Secret]`-marked, stripped by `SecretRedactor` (same 5 fragments as `ConfigurationHashCalculator`: secret/password/token/key/credential).
