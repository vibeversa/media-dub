# Task 43 — [OPTIONAL] Local Inference GPU Integration

## Goal

[OPTIONAL POST-MVP] Expose local/GPU inference as a first-class provider route via a Python sidecar boundary without forcing remote-HTTP assumptions, disabled by default.

## Context

Binding (optional): local adapter via Python gRPC/HTTP sidecar contract {modelName,modelVersion,artifactHash,capability,request,response,health,warmup}; model registry {id,version,hash,device,capability,runtime}; GPU workers separate queue ai.gpu, KEDA/GPU-metric scaling, per-worker concurrency, warmup before work; local executions recorded as ProviderExecutions with model hash + device; route option subject to privacy (local-only routing possible); mock local adapter for tests; failures model-load/GPU-exhaustion/timeout/invalid-version handled safely. Flags `Features:LocalInferenceEnabled=false` default false. Must not alter core mandatory behavior when disabled.

## Starting State

Core + real adapters + mocks (incl. MockLocalInferenceProvider) + K8s GPU deployment skeleton + provider routing exist. No Python sidecar contract, no model registry, no GPU warmup/concurrency enforcement, no local-only routing test.

## Scope

Must implement (gated by flag): sidecar contract, registry, GPU worker config, warmup/concurrency, recording, privacy routing, failure tests. Must not change core completion criteria.

## Instructions

1. Contract: create `contracts/local-inference/README.md` + `proto/local_inference.proto` (create files; allowed scaffolding): service `LocalInference { rpc Infer(InferRequest) returns (InferResponse); rpc Health(HealthRequest) returns (HealthResponse); rpc Warmup(WarmupRequest) returns (WarmupResponse); }` messages with `model_name, model_version, artifact_hash, capability, payload_json, device_profile`. HTTP fallback `POST /infer` same JSON (document both; default HTTP for simplicity, gRPC optional via `LocalInference:Protocol=http|grpc` default http).
2. Registry: `src/DubbingPlatform.Infrastructure/Providers/LocalInference/ModelRegistry.cs` loading `LocalInference:Models[]` config `{id, version, artifactHash, deviceProfile (cpu|cuda:0), capability, runtimeRequirements}` + validation (hash 64 hex, capability known); `ResolveModel(capability)` returns registry entry.
3. GPU worker: `deploy/k8s/gpu-worker.yaml` already sketched in Task 40 — complete here: `replicas:0-4`, `nodeSelector`, `tolerations`, `resources.limits nvidia.com/gpu:1`, env `DOTNET_WORKER_ROLE=gpu`, `LocalInference__Endpoint=http://local-inference:8000`, KEDA scaler on `ai.gpu` queue + GPU util; concurrency semaphore `LocalInference:MaxConcurrency=1` for GPU (2 for CPU) enforced in adapter; `WarmupAsync` calls sidecar /warmup or Warmup RPC before accepting work (worker startup blocks readiness until warmup ok when flag true).
4. Adapter: complete `LocalInferenceProvider` (partial in Task 16; extend here): route via ProviderResolver as `LocalInference` family option; privacy: if tenant policy `LocalInferenceOnly=true` (new ProcessingPolicy field? Decision: reuse `ExternalProvidersAllowed=false + LocalInferenceAllowed=true` → resolver restricts to local only — document) → only local routes; record ProviderExecution with model hash + device profile + warmup state.
5. Python sidecar stub: `services/local-inference/app.py` (create minimal FastAPI stub with /infer /health /warmup echoing mock responses + Dockerfile `services/local-inference/Dockerfile`) for local testing; production swaps real models without C# changes.
6. Failures: model-load fail → ProviderPermanentFailure + fallback per budget; GPU exhaustion (429/503 with `exhausted:true`) → delayed retry + scale signal; timeout → ProviderTimeout; invalid version → fail-fast config error.
7. Tests: `LocalInferenceTests.cs`: `Mock_Passes`, `Gpu_Schedules_Only_Gpu_Nodes` (manifest assert), `LocalOnly_Policy_Routes_Local`, `ModelHash_Recorded`, `Gpu_Exhaustion_Safe` (mock 503 → delayed retry, no crash). All skipped unless flag true except mock + manifest asserts.

## Requirements

- R1: Disabled → core unaffected, no sidecar required.
- R2: Mock local passes; GPU manifest correct.
- R3: Local-only policy routes local.
- R4: Hash + device recorded.
- R5: Exhaustion handled safely.

## Edge Cases and Error Handling

- Sidecar down → ProviderUnavailable, fallback if allowed else review.
- Warmup fail → worker readiness Unhealthy, no work taken.
- Version mismatch → fail-fast, no retry.

## Security and Safety Requirements

- mTLS to sidecar where configured; model hashes verified before load; GPU isolation via node selector; no secrets in registry.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Providers/LocalInferenceTests.cs` as above.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~LocalInferenceTests
kubectl apply --dry-run=client -f deploy/k8s/gpu-worker.yaml
```

## Completion Criteria

- Local/GPU boundary complete, gated, recorded; tests + manifest valid.

## Traceability

- Plan Section 32; Assumptions 42–43; Completion criteria extensibility (local inference part).
