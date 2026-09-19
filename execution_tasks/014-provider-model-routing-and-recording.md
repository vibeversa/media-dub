# Task 14 — Provider Model Routing and Recording

## Goal

Implement provider capability interfaces, DTOs, descriptors, privacy-aware routing, health tracking, outcome classification, and execution recording so all AI calls are routable, classified, and traceable.

## Context

Binding: capabilities Vad/Diarization/Transcription/Translation/Tts/SourceSeparation/VideoIntelligence/LocalInference; no provider hardcoded in domain; families Azure/OpenAI/Google-Gemini/Mock/Local; provider vs quality failure distinct; external work at-least-once + reconciled; routing by capability→tenant policy→route priority→health→cost; descriptors versioned + compatibility-checked; outcomes Success/ProviderUnavailable/ProviderRateLimited/ProviderTransientFailure/ProviderPermanentFailure/ProviderInvalidResponse/ProviderTimeout/QualityBelowThreshold/PolicyRejected/UnsupportedCapability/Cancelled with retry/fallback/review/fail-fast mapping; route snapshot hashes; tenant ProcessingPolicy (external allowed, allowed list, residency, sensitive/voice policies, local allowed, retention overrides); idempotency keys where supported; reconcile duplicates by stage-exec + idempotency key + request hash + output hash; fail fast on missing creds for configured non-mock.

## Starting State

Domain has ProviderExecution/ProviderCapabilityDescriptor/ProviderRouteSnapshot/Prompt entities + enums OutcomeClass/ProviderType/ProviderCapability. No provider interfaces, no resolver, no recorder, no health.

## Scope

Must implement: 8 provider interfaces + DTOs, descriptor model + store, outcome policy, route snapshot, ProcessingPolicy enforcement, ProviderResolver, health tracker, ProviderExecutionRecorder. Must not implement: concrete adapters (next two tasks), worker logic.

## Instructions

1. Create `src/DubbingPlatform.Application/Abstractions/Providers/` interfaces (one file each, methods accept CancellationToken, return typed responses):
   - `IVadProvider { Task<VadResponse> DetectAsync(VadRequest, CancellationToken); }`
   - `IDiarizationProvider`, `ITranscriptionProvider`, `ITranslationProvider`, `ITtsProvider`, `ISourceSeparationProvider`, `IVideoIntelligenceProvider`, `ILocalInferenceProvider` similarly.
   DTOs in `Dtos/`: `VadRequest { Guid TenantId,ProjectId,RunId; string ArtifactId; string Language; }`, `VadResponse { List<VadRegion> Regions; double Confidence; string Model; }`, `VadRegion { int StartMs; int EndMs; double Confidence; }`; diarization `{ Segments, SpeakerLabels }`; transcription `{ string Text; double Confidence; List<WordTimestamp> Words; string Model; }` with `WordTimestamp { string Word; int StartMs; int EndMs; double Confidence; }`; translation `{ string PrimaryText; List<string> Alternatives; double SemanticScore; double NaturalnessScore; double TimingScore; }`; TTS `{ string ContentObjectId; int DurationMs; string VoiceId; }`; separation `{ string DialogueArtifactId; string? BackgroundArtifactId; double Confidence; }`; video `{ Faces, ActiveSpeaker }`; local `{ string ModelId; string PayloadJson; }`. All include language/duration/timestamps/speaker/confidence/model/version/deployment/usage/raw-metadata + capability optionals.
2. Create `ProviderCapabilityDescriptor` service: `src/DubbingPlatform.Application/Providers/DescriptorStore.cs` loading from config `Providers:Descriptors[]` + DB table, versioned; `IsCompatible(descriptor, request)` checks languages/formats/maxBytes/maxDuration/wordTimestamps/diarization/cloning etc.
3. Outcome policy `OutcomePolicy.cs`: map outcome→{allowRetry,allowFallback,allowReview,failFast}: e.g., RateLimited→retry+fallback, InvalidResponse→fallback+review, QualityBelow→review only, PolicyRejected/Unsupported→fail-fast, Cancelled→no-retry.
4. `ProviderRouteSnapshot` builder: hashes routeConfig+descriptors+privacy via ConfigurationHashCalculator.
5. `ProcessingPolicy` enforcement: `PolicyChecker.CanUseProvider(tenantPolicy, provider, residency)`; privacy blocks disallowed before routing.
6. Implement `ProviderResolver.cs`: `Task<(ProviderType,string model)> ResolveAsync(ProviderCapability cap, Guid tenant, string language, long bytes, int durationMs, CancellationToken)`: capability filter → policy filter → route priority (config `Providers:RoutePriority:Capability=[ordered providers]`, no hardcoded Azure-first) → health filter → cost guard (ICostGate). Throw PolicyDenied/UnsupportedCapability with PUBLIC codes POLICY_DENIED/PROVIDER_CONFIGURATION_ERROR.
7. Health `ProviderHealthTracker.cs` (Redis + memory): tracks configValid, runtimeAvailable, circuitState, errorRate, rateLimitPressure; `IsHealthy(provider)` false if circuit open or errorRate>20% last 100 calls or rate-limited. Separate config validation (`ValidateConfig`) from runtime.
8. `ProviderExecutionRecorder.cs`: `RecordAsync(ProviderExecution entity)` persists every call including fallback attempts with all 25 fields listed in plan (provider/capability/model/version/deployment/region/apiVersion/attempt/request+response hashes/latency/tokens/audioSecs/usage/est+actual cost/price version/outcome/fallback reason/prompt id+hash+system+safety hashes/voice version/external job id/idempotency key). Use provider idempotency key ` $"{run}:{stage}:{scope}:{attempt}"` where supported.
9. DI: `services.AddSingleton<ProviderResolver, ...>` etc.; missing creds for enabled non-mock → startup fail fast.

## Requirements

- R1: 8 interfaces + DTOs exact.
- R2: Resolver order capability→policy→priority→health→cost, no hardcoded precedence.
- R3: Compatibility checked before routing.
- R4: Every call recorded with all fields.
- R5: Privacy/policy blocks with correct codes.

## Edge Cases and Error Handling

- Capability mismatch → UnsupportedCapability fail-fast.
- Privacy block → POLICY_DENIED + metric.
- Unhealthy provider skipped, next route tried within fallback budget.
- Duplicate provider exec (same idempotency key) → reconcile by request+output hash, no double billing.

## Security and Safety Requirements

- No secrets in descriptors/logs/hashes; credentials via env/secret manager only; residency enforced.

## Testing

Create `tests/DubbingPlatform.UnitTests/Providers/ResolverTests.cs`: `Capability_Mismatch_Blocks`, `Privacy_Blocks_Disallowed`, `Unhealthy_Route_Skipped`, `No_Hardcoded_Precedence` (config order respected), `Fallback_Budget_Respected`, `Quality_Not_As_Transport` (QualityBelow doesn't trigger transport retry).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ResolverTests
```

## Completion Criteria

- Provider model + routing + recording compile; tests pass.

## Traceability

- Plan Section 6 actions 1–19,28–30; Assumptions 35–38,41; Tests checklist provider failover.
