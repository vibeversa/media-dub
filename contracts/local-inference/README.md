# Local Inference Sidecar Contract (Task 043, OPTIONAL post-MVP)

Disabled by default (`Features:LocalInferenceEnabled=false`). When disabled the
core pipeline is unaffected and no sidecar is required.

## Transports

Two transports expose the same JSON shape. Default is HTTP for simplicity;
gRPC is optional via `LocalInference:Protocol=http|grpc` (default `http`).

### HTTP (default)

- `GET /health` → `{"status":"ok"}` (liveness; used as warmup fallback).
- `POST /warmup` with `{"modelName","modelVersion","deviceProfile"}`
  → `{"status":"warmed","modelName","modelVersion","deviceProfile"}`.
  `WarmupAsync` tries `POST /warmup` first and falls back to `GET /health`
  so existing `GET /health`-only stubs keep working.
- `POST /infer` with
  `{"modelName","modelVersion","artifactHash","capability","payload"}`
  (plus `X-Sidecar-Protocol: grpc` marker when `Protocol=grpc`)
  → `{"output","confidence","modelHash","device"}`.
  On GPU exhaustion the sidecar returns `429` or `503` with a body containing
  `"exhausted":true`; the adapter maps this to `PROVIDER_RATE_LIMITED`
  (delayed retry + scale signal) instead of plain `PROVIDER_FAILED`.

### gRPC (optional)

See `proto/local_inference.proto`. Service `LocalInference`
(`Infer`, `Health`, `Warmup`) carries the same fields:
`model_name`, `model_version`, `artifact_hash`, `capability`, `payload_json`,
`device_profile`. The C# adapter currently uses the HTTP mapping for both
settings and sends the `X-Sidecar-Protocol: grpc` marker when
`Protocol=grpc`; true gRPC transport is a drop-in swap that needs no C#
model changes.

## Model registry

`LocalInference:Models[]` entries:
`{id, version, artifactHash, deviceProfile (cpu|cuda:0), capability, runtimeRequirements}`.
Validation: `artifactHash` must be 64 hex chars; `capability` must parse to a
known `ProviderCapability`; `deviceProfile` must be `cpu`, `cuda`, or
`cuda:N`. Invalid entries fail fast with `PROVIDER_CONFIGURATION_ERROR`
(no retry). `ModelRegistry.ResolveModel(capability)` returns the first entry
matching the capability (case-insensitive); when `Models` is empty it
synthesizes an entry from the legacy single-model fields
(`ModelName/ModelVersion/ModelHash/Device`).

## Privacy / local-only routing

No new policy field. Local-only routing reuses the existing tenant policy:
`ExternalProvidersAllowed=false` + `LocalInferenceAllowed=true` (+
`AllowedProviders` containing `local`/`LocalInference`). `PolicyChecker`
already blocks every external provider in that state, so `ProviderResolver`
restricts to local-only. Documented deviation from the task-text sketch
(`LocalInferenceOnly=true` was not added as a new column).

## Recording

Every local execution records a `ProviderExecution` with model hash +
device profile + warmup state in `RawMetadata`:
`model.hash`, `device`, `warmed=true`. Hashes are verified before load
(registry hash must match the sidecar `modelHash` echo where present).

## Failures

| Signal | Mapping |
|---|---|
| Model-load fail (body `model_load*`, `model_not_found`, `invalid_model`) | `PROVIDER_FAILED` (permanent, fallback per budget, no transport retry) |
| GPU exhaustion (`429`/`503` with `exhausted:true`) | `PROVIDER_RATE_LIMITED` (delayed retry honoring `Retry-After` + `localinference.gpu_exhausted` scale signal) |
| Timeout (`408`/`504` or `HttpClient` timeout) | `PROVIDER_TIMEOUT` |
| Invalid version (`invalid_version`, `version_mismatch`, empty `ModelVersion`) | `PROVIDER_CONFIGURATION_ERROR` (fail-fast, no retry) |
| Sidecar down (`HttpRequestException`) | `PROVIDER_FAILED` transient (fallback if allowed else review) |
| Warmup fail | Worker readiness `Unhealthy`, no work taken |

## Security

- mTLS to the sidecar where configured (`LocalInference:RequireMtls=true`
  requires `https` + `ClientCertificatePath`; handler in
  `ProviderRegistration.cs`).
- Model hashes verified before load; GPU isolation via `nodeSelector` +
  `NoSchedule` toleration + `nvidia.com/gpu:1` limit.
- No secrets in the registry or hashes (`ConfigurationHashCalculator`
  strips secret-like keys).
- In-cluster plain HTTP to `http://local-inference:8000` is allowed
  (cluster-local service); public internet still requires `https`.

## Concurrency

Per-worker semaphore `LocalInference:MaxConcurrency` (GPU `1`, CPU `2`)
enforced in the adapter. GPU manifests set `LocalInference__MaxConcurrency=1`.
