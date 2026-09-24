# 043 — Optional Local Inference GPU Report

## Status
COMPLETED

## Summary
Implemented the gated local/GPU inference boundary: sidecar contract (`contracts/local-inference/README.md` + `proto/local_inference.proto`), `ModelRegistry` with 64-hex/capability/device validation, extended `LocalInferenceProvider` (registry-aware payloads, POST `/warmup` with GET `/health` fallback, GPU-exhaustion/model-load/invalid-version classification, hash+device+warmed recording, GPU=1/CPU=2 semaphore), `LocalInferenceWarmupCheck` readiness gating, completed GPU manifests (`workers-gpu.yaml` + task-text `gpu-worker.yaml` with sidecar Service), and Python FastAPI stub. Added hermetic `LocalInferenceTests` (2 always-run + 3 flag-gated); full suites green with `dotnet build` 0/0.
Disabled by default (`Features:LocalInferenceEnabled=false`); core unaffected when disabled.

## Files Created/Modified
- `contracts/local-inference/README.md` — new: HTTP (`/health|/warmup|/infer`) + gRPC docs, registry shape, local-only policy, recording, failure table, mTLS/concurrency.
- `proto/local_inference.proto` — new: `service LocalInference { Infer|Health|Warmup }` with `model_name,model_version,artifact_hash,capability,payload_json,device_profile`.
- `src/DubbingPlatform.Infrastructure/Providers/LocalInference/ModelRegistry.cs` — new: loads `LocalInference:Models[]`, validates hash/capability/device, `ResolveModel(string|ProviderCapability)`, legacy synthesis when empty, fail-fast on invalid version.
- `src/DubbingPlatform.Infrastructure/Observability/LocalInferenceMetrics.cs` — new: `localinference.gpu_exhausted{device}` scale signal on `dubbing-platform` meter.
- `src/DubbingPlatform.Infrastructure/Health/LocalInferenceWarmupCheck.cs` — new: `ready`-only GPU probe; Healthy when flag false, else GET `/health` 5s timeout.
- `deploy/k8s/gpu-worker.yaml` — new: task-text path; worker-gpu Deployment (0 replicas, nodeSelector, toleration, gpu:1, `DOTNET_WORKER_ROLE=gpu`, `LocalInference__Endpoint=http://local-inference:8000`, `MaxConcurrency=1`) + sidecar Deployment + ClusterIP Service.
- `services/local-inference/app.py` — new: FastAPI stub `/health|/warmup|/infer` echo + `exhausted` → 503 `exhausted:true` drill.
- `services/local-inference/Dockerfile` — new: `python:3.12-slim`, non-root 1654, uvicorn :8000.
- `services/local-inference/requirements.txt` — new: pinned `fastapi/uvicorn/pydantic`.
- `tests/DubbingPlatform.IntegrationTests/Providers/LocalInferenceTests.cs` — new: `Mock_Passes`, `Gpu_Schedules_Only_Gpu_Nodes`, `LocalOnly_Policy_Routes_Local`, `ModelHash_Recorded`, `Gpu_Exhaustion_Safe` (last 3 `[SkippableFact]` gated on `Features__LocalInferenceEnabled=true`).
- `src/DubbingPlatform.Application/Options/LocalInferenceOptions.cs` — modified: added `LocalInferenceModelOptions` + `Models[]`, `IsSidecarEndpointAllowed` (https|http-loopback|http `local-inference|*.svc*|*.cluster.local`), `RequireSidecarEndpoint`, `IsValidArtifactHash/DeviceProfile`; validator covers Models + sidecar allowance.
- `src/DubbingPlatform.Infrastructure/Providers/LocalInference/LocalInferenceProvider.cs` — modified: registry-aware (optional `ModelRegistry?` ctor overload keeps `new(Http,Options)` compat), warmup fallback, `IsGpuExhaustionSignal/IsModelLoadFailure/IsInvalidVersionSignal`, `ThrowIfInferErrorAsync/ThrowIfWarmupErrorAsync` (exhausted 429/503→RateLimited+metric, model-load→Failed, version→ConfigError), `Metadata` now `model.hash|device|warmed`, `ValidateConfig` fail-fast on empty version, `EndpointBase` via sidecar allowance.
- `src/DubbingPlatform.Infrastructure/Health/HealthRegistration.cs` — modified: added `IsGpuRole(role)` + `LocalInferenceWarmupCheck` for `gpu` role only; doc updated.
- `src/DubbingPlatform.Infrastructure/Providers/ProviderRegistration.cs` — modified: `AddSingleton<ModelRegistry>()`; `CreateLocal` passes registry through.
- `src/DubbingPlatform.Application/Providers/PolicyChecker.cs` — modified: docs only — local-only = `ExternalProvidersAllowed=false + LocalInferenceAllowed=true` + `AllowedProviders∋local` (no new column).
- `deploy/k8s/workers-gpu.yaml` — modified: header Task 43 notes + env `LocalInference__Endpoint=http://local-inference:8000`, `LocalInference__Protocol=http`, `LocalInference__MaxConcurrency=1`.

## Decisions Made
- **Sidecar allowance tight, not global**: `ProviderEndpointValidator` stays https-or-loopback; new `LocalInferenceOptions.IsSidecarEndpointAllowed` additionally permits plain HTTP only to `local-inference` and `*.svc*|*.cluster.local`. Allows in-cluster `http://local-inference:8000` without opening cleartext to the internet.
- **No new `LocalInferenceOnly` policy column**: reused `ExternalProvidersAllowed=false + LocalInferenceAllowed=true` per task Decision note; `PolicyChecker` already restricts to local-only in that state. Documented in `PolicyChecker.cs` + sidecar README.
- **Warmup fallback, not replacement**: `WarmupAsync` tries POST `/warmup` then falls back to GET `/health` on 404. Keeps all 34 existing `ProviderContractTests` (health-only stubs) green while satisfying task `/warmup or Warmup RPC`.
- **Registry fallback chain**: `Models` empty → synthesize from legacy fields (zero core change when disabled); `Models` non-empty but capability missing → `PROVIDER_CONFIGURATION_ERROR` fail-fast; capability bridges (`Transcription|Translation|Tts`) try specific then fall back to generic `LocalInference` entry.
- **Exhaustion → `PROVIDER_RATE_LIMITED`**: 429/503 with `exhausted:true` maps to RateLimited (delayed retry honoring `Retry-After` + `LocalInferenceMetrics.Exhausted` scale signal); `OutcomePolicy` already gives retry+fallback for RateLimited. Model-load substrings → `PROVIDER_FAILED` permanent (fallback, no transport retry); version substrings/empty version → `PROVIDER_CONFIGURATION_ERROR` fail-fast.
- **`gpu-worker.yaml` vs `workers-gpu.yaml`**: kept canonical `workers-gpu.yaml` (checked by `deploy/verify.sh`) and created task-text `gpu-worker.yaml` in sync (worker + sidecar Deployment + Service) so `kubectl apply --dry-run=client -f deploy/k8s/gpu-worker.yaml` validates. Directory apply is idempotent (same worker spec).
- **Gated tests use `[SkippableFact]`**: xUnit v2 ignores `SkipException` from `[Fact]` (reports FAIL); `[SkippableFact]` + `Skip.If` reports SKIP per repo convention (Tasks 006/039).
- **New meter, no renames**: added `localinference.gpu_exhausted` on `dubbing-platform`; frozen `provider.*` and `enrichment.failed` untouched.

## Build/Test Results
- `dotnet build --nologo -v q` (last 4):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:03.42
```
- `dotnet test --filter FullyQualifiedName~LocalInferenceTests --nologo -v q` flag false (last 1):
```
Passed!  - Failed:     0, Passed:     2, Skipped:     3, Total:     5, Duration: 69 ms - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- With flag true (`$env:Features__LocalInferenceEnabled="true"; dotnet test --filter FullyQualifiedName~LocalInferenceTests`):
```
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 4 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `dotnet test tests/DubbingPlatform.ContractTests --filter FullyQualifiedName~ProviderContractTests`:
```
Passed!  - Failed:     0, Passed:    34, Skipped:     0, Total:    34, Duration: 24 s - DubbingPlatform.ContractTests.dll (net10.0)
```
- `dotnet test tests/DubbingPlatform.UnitTests`:
```
Passed!  - Failed:     0, Passed:   310, Skipped:     0, Total:    310, Duration: 3 s - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test tests/DubbingPlatform.ContractTests`:
```
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 23 s - DubbingPlatform.ContractTests.dll (net10.0)
```
- `dotnet test --filter FullyQualifiedName~EnrichmentTests`:
```
Passed!  - Failed:     0, Passed:     3, Skipped:     0, Total:     3, Duration: 263 ms - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `kubectl apply --dry-run=client -f deploy/k8s/gpu-worker.yaml`: SKIP — `kubectl` not installed on this host (runs in CI); replaced with `python -c yaml.safe_load` structural asserts: `['Deployment','Deployment','Service']`, replicas 0, nodeSelector `nvidia.com/gpu.present=true`, limits `nvidia.com/gpu:1`, env `DOTNET_WORKER_ROLE|LocalInference__Endpoint|LocalInference__MaxConcurrency`; `workers-gpu.yaml` gpu contract OK; KEDA `worker-gpu` min 0 max 4 triggers `[rabbitmq,prometheus]`.
- `python -m py_compile services/local-inference/app.py`: `COMPILE_OK`.

## Recommendations for Next Agent (044)
- Repo state: builds 0/0 (`TreatWarningsAsErrors`). Task 043 is the last optional (042+043 done). Not a git repo. No Docker/kubectl on host; hermetic tests pass, Docker-gated tests skip per convention. `deploy/k8s/gpu-worker.yaml` (new, task-text path) mirrors `deploy/k8s/workers-gpu.yaml` (canonical, checked by `deploy/verify.sh`) — keep them in sync if worker env changes.
- Key files for any follow-up: `src/DubbingPlatform.Application/Options/LocalInferenceOptions.cs` (`IsSidecarEndpointAllowed|RequireSidecarEndpoint|IsValidArtifactHash|IsValidDeviceProfile`, `Models[]`), `src/DubbingPlatform.Infrastructure/Providers/LocalInference/ModelRegistry.cs` (`ResolveModel(string|ProviderCapability)`, `ValidateEntry`), `src/DubbingPlatform.Infrastructure/Providers/LocalInference/LocalInferenceProvider.cs` (`WarmupAsync` POST /warmup→GET /health, `IsGpuExhaustionSignal|IsModelLoadFailure|IsInvalidVersionSignal`, `ThrowIfInferErrorAsync`, `Metadata` keys `model.hash|device|warmed`, `HttpClientName="local-inference"`), `src/DubbingPlatform.Infrastructure/Health/LocalInferenceWarmupCheck.cs` (`Name="local-inference-warmup"`, flag-gated), `src/DubbingPlatform.Infrastructure/Observability/LocalInferenceMetrics.cs` (`localinference.gpu_exhausted{device=cpu|cuda}`), `contracts/local-inference/README.md`, `proto/local_inference.proto`, `services/local-inference/app.py`, `tests/DubbingPlatform.IntegrationTests/Providers/LocalInferenceTests.cs` (`IsEnabled` reads `Features__LocalInferenceEnabled`, manifest `ReadManifest` walks to `DubbingPlatform.sln`).
- Gotchas: NEVER run two `dotnet test` concurrently (MSBuild/testhost contention). Keep `Features__LocalInferenceEnabled=false` default; do not rename frozen queues (`QueueNames.cs`) or instruments (`PlatformMetrics.cs|EnrichmentMetrics.cs|LocalInferenceMetrics.cs`). Gated tests must stay `[SkippableFact]` (v2 `[Fact]`+`Skip.If` reports FAIL). `LocalInferenceProvider` ctor overload with `ModelRegistry?` preserves `new(Http,Options)` call sites (contract tests). `ProviderEndpointValidator` is still strict globally — sidecar allowance lives only in `LocalInferenceOptions`. `ArtifactHash` registry validation is 64 hex (case-insensitive); legacy `ModelHash` empty → metadata `unspecified` (do not fail when `Models` empty).
- Conventions: error codes via `ErrorCodes.cs` + `ErrorCodeException` (never raw HTTP in services); options with `SectionName` + `IValidateOptions<>`; `ArgumentList` for FFmpeg (not used here); pod security `runAsUser:1654` + readOnlyRootFilesystem; secrets via config/`CHANGE_ME`, never hardcoded.
- Config keys: `Features__LocalInferenceEnabled` (false), `LocalInference__Endpoint` (`http://localhost:8081` local, `http://local-inference:8000` cluster), `LocalInference__Protocol` (`http|grpc`), `LocalInference__MaxConcurrency` (1 gpu, 2 cpu), `LocalInference__Models__{i}__{Id,Version,ArtifactHash,DeviceProfile,Capability,RuntimeRequirements}`, `LocalInference__Device`, `LocalInference__ClientCertificatePath` (mTLS), `Media__MaxConcurrentMediaJobs=2` (sidecar must not bypass).
- Test helpers: `MockBehaviorOptions.Success|RateLimited|Timeout`, `TestFixtureBase.MockSuccessOptions()`; `ConfigurationHashCalculator.Compute`; `ProviderExecutionRecorder.BuildIdempotencyKey(run:N,stage,scope,attempt)`; WireMock `StubLocalHealth|StubLocalInfer` plus inline POST `/warmup` stub for warmup tests.
