# Task 8 — API Middleware Options and Errors

## Goal

Implement configuration options, validation, correlation-ID and exception-handling middleware with the full public error-code catalog so all APIs return consistent structured errors.

## Context

Binding: correlation header `X-Correlation-Id` generated if missing, propagated via HttpContext/logs/traces/message headers/provider executions. Public error codes (29): VALIDATION_FAILED, UNAUTHORIZED, FORBIDDEN, NOT_FOUND, CONFLICT, MEDIA_UNSUPPORTED, MEDIA_CORRUPT, UPLOAD_INCOMPLETE, DUPLICATE_MEDIA, PROVIDER_CONFIGURATION_ERROR, PROVIDER_RATE_LIMITED, PROVIDER_TIMEOUT, PROVIDER_INVALID_RESPONSE, PROVIDER_QUOTA_EXHAUSTED, PROVIDER_FAILED, QC_BLOCKED, QUOTA_EXCEEDED, RATE_LIMITED, RESOURCE_EXHAUSTED, ARTIFACT_UNAVAILABLE, ARTIFACT_CHECKSUM_MISMATCH, LEASE_LOST, PIPELINE_INVARIANT_VIOLATION, MANUAL_REVIEW_REQUIRED, EXPORT_NOT_READY, STORAGE_UNAVAILABLE, CONSENT_REQUIRED, POLICY_DENIED, INTERNAL_ERROR. Richer internal FailureCategory separate. Secrets marked sensitive, excluded from logs/traces/hashes/errors. FluentValidation + Polly for outbound HTTP (retry exponential backoff+jitter, circuit breaker, timeout).

## Starting State

Solution builds; DbContext + migrations exist; Api project has placeholder Program + empty appsettings sections; no middleware, no options, no error envelope.

## Scope

Must implement: 12 options classes + validation, CorrelationIdMiddleware, ExceptionHandlingMiddleware + error envelope, FluentValidation registration, Polly policies. Must not implement: health checks/metrics/tracing (next task), endpoints, workers.

## Instructions

1. Create `src/DubbingPlatform.Application/Options/` namespace `DubbingPlatform.Application.Options`, one file per class with DataAnnotations + `IValidateOptions<T>`:
   - `ObservabilityOptions { string ServiceName="dubbing-platform"; string OtlpEndpoint=""; bool EnableTracing=true; bool EnableMetrics=true; }`
   - `MediaOptions { int MaxUploadBytes=5368709120; int MaxDurationMs=7200000; string[] AllowedContainers=["mp4","mov","mkv","wav","mp3","flac"]; long MinDiskFreeBytes=1073741824; int FfmpegTimeoutSec=600; int MaxConcurrentMediaJobs=2; int CpuThreads=4; }`
   - `RetryOptions { int TransportMaxAttempts=5; int ProviderRequestMaxAttempts=3; int FallbackMaxAttempts=2; int LogicalStageMaxAttempts=3; int ManualRetryMaxAttempts=3; int RateLimitDelaySec=30; }` per-stage override `Dictionary<string,int> PerStageMaxAttempts`.
   - `TimingOptions { int PreferredToleranceMs=50; int MaxToleranceMs=100; double MaxRateChangePercent=15.0; double MaxStretchFactor=1.15; int MaxCandidates=3; int MaxTtsPreviewAttempts=3; int MaxRewrites=2; }`
   - `StorageOptions { string Endpoint="localhost:9000"; string Bucket="dubbing"; bool UseSsl=false; string AccessKey=""; string SecretKey=""; string KeyPrefix=""; }` — keys marked `[Secret]`.
   - `ProviderOptions { string DefaultProvider="mock"; Dictionary<string,string> RoutePriority; Dictionary<string,bool> Enabled; }` — no hardcoded Azure/OpenAI precedence; default route config `{"transcription":["mock"],"translation":["mock"],"tts":["mock"]}`.
   - `QuotaOptions { int MaxActiveProjects=10; int MaxProjectsPerDay=50; double MaxCostPerProject=50.0; double MaxCostPerSegment=2.0; int MaxSegmentCount=2000; long MaxStorageBytes=107374182400; int MaxConcurrentStagesPerTenant=20; }`
   - `RateLimitOptions { int RequestsPerMin=60; int TokensPerMin=100000; int CharsPerMin=500000; int AudioSecondsPerMin=3600; int Concurrency=10; }`
   - `PrivacyOptions { bool ExternalProvidersAllowed=true; string[] AllowedProviders=["mock"]; string? ResidencyConstraint=null; }`
   - `FeatureOptions { bool VideoIntelligenceEnabled=false; bool LipSyncEnabled=false; bool LocalInferenceEnabled=false; }` (disabled defaults for optional).
   - `DeploymentOptions { string Environment="Development"; string Region="local"; }`
   - Plus `AuthOptions { string Authority=""; string Audience="dubbing-api"; bool RequireHttps=false; }` and `RetentionOptions { int IntermediateDays=30; int FinalDays=90; int AuditDays=365; }` under same folder.
   Validate at startup via `ValidateOnStart()`; missing/invalid → fail fast with options name.
2. Create `src/DubbingPlatform.Api/Middleware/CorrelationIdMiddleware.cs`: read `X-Correlation-Id`, else `Guid.NewGuid():N`; set `HttpContext.Items["CorrelationId"]`, response header, `Serilog.LogContext` property, `Activity.Current?.SetTag("correlation.id",...)`. Create `ExceptionHandlingMiddleware.cs`: catch DomainException→400 VALIDATION_FAILED, UnauthorizedAccessException→401, ForbiddenException (create `ForbiddenException` in Application/Exceptions)→403, NotFoundException→404, ConflictException→409, else 500 INTERNAL_ERROR; envelope `{ "error": { "code": "...", "message": "...", "correlationId": "...", "details": {} } }`; never include secrets/stack traces.
3. Create `src/DubbingPlatform.Application/Exceptions/` with `NotFoundException, ConflictException, ForbiddenException, QuotaExceededException, RateLimitedException` mapping to codes.
4. Register in `Program.cs`: `builder.Services.AddOptions...BindConfiguration(...).ValidateDataAnnotations().ValidateOnStart()`, `AddFluentValidationAutoValidation`, `AddValidatorsFromAssembly(Application)`, Polly: `AddHttpClient("providers").AddStandardResilienceHandler()` + explicit retry (exponential+jitter), circuit breaker (5 failures/30s break), timeout 30s.
5. Mark secrets: custom `[Secret]` attribute; ensure exception middleware strips values for keys containing secret/password/token/key.

## Requirements

- R1: All 14 options classes exist with exact keys/defaults above.
- R2: Correlation ID generated/propagated/returned.
- R3: All 29 error codes mappable; envelope shape exact.
- R4: Options invalid → startup failure.
- R5: FluentValidation + Polly registered.

## Edge Cases and Error Handling

- Invalid request body: 400 VALIDATION_FAILED with field details.
- Missing correlation header: generate, do not fail.
- Options missing section: fail startup naming section.
- Provider HTTP 429/5xx: Polly retry per policy; 4xx (except 429): fail fast no retry.
- Secrets in exception message: redacted to `[REDACTED]`.

## Security and Safety Requirements

- No secrets in logs/traces/hashes/errors. Error responses never include stack traces or secret values. Correlation IDs are random, not sequential.

## Testing

Create `tests/DubbingPlatform.UnitTests/Middleware/ErrorEnvelopeTests.cs` (assert mapping for each exception→code/status + envelope shape + secret redaction) and `OptionsValidationTests.cs` (invalid MediaOptions fails, defaults pass). Integration: `tests/DubbingPlatform.IntegrationTests/Api/ErrorEnvelopeApiTests.cs` — invalid POST returns 400 VALIDATION_FAILED with correlation header echoed.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ErrorEnvelopeTests
dotnet test --filter FullyQualifiedName~OptionsValidationTests
```

## Completion Criteria

- Middleware + options + envelope work; tests pass; startup validation active.

## Traceability

- Plan Section 3 actions 1–7, 16–21; Assumptions 62–63,76; Error checklist invalid-input fail fast, config fail fast.
