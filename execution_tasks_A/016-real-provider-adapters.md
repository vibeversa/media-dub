# Task 16 — Real Provider Adapters

## Goal

Implement Azure, OpenAI, Google/Gemini, and Local Inference adapters with long-running job support, idempotency, and WireMock contract coverage without requiring live paid calls in tests.

## Context

Binding: Azure (STT/diarization/translation/TTS), OpenAI (STT/translation/TTS), Google (STT/Translation/Gemini LLM context+translation/TTS where configured), Local Inference via Python gRPC/HTTP sidecar with model registry (id/version/hash/device/capability/runtime), device profile, concurrency, warmup. Long jobs: start→store external job id→poll→reconcile after lease loss. Contract fixtures: 429/timeout/malformed/async/duplicate/partial/expiry/quota. Record every execution incl. fallback; fail fast on missing creds for configured non-mock.

## Starting State

Interfaces, resolver, recorder, mocks exist. No real adapters. HttpClient + Polly registered. WireMock.Net available in tests.

## Scope

Must implement: 4 adapter families + local bridge + job poller + contract fixtures + DI. Must not implement: worker orchestration, routing changes.

## Instructions

1. Create folders `src/DubbingPlatform.Infrastructure/Providers/Azure|OpenAI|Google|LocalInference/`.
2. Azure: `AzureSttProvider : ITranscriptionProvider, IDiarizationProvider` using `Azure:SpeechKey,SpeechRegion,TranslatorKey` env; `AzureTtsProvider : ITtsProvider`; `AzureTranslationProvider : ITranslationProvider` (Translator Text API). Map Azure confidences directly; diarization via Conversation Transcription; handle 429→ProviderRateLimited (delayed retry), timeout→ProviderTimeout, 4xx→Permanent.
3. OpenAI: `OpenAiSttProvider` (whisper via `/audio/transcriptions` multipart), `OpenAiTranslationProvider` (chat `/chat/completions` with glossary system prompt), `OpenAiTtsProvider` (`/audio/speech`). Config `OpenAI:ApiKey,Model,BaseUrl` (BaseUrl overridable to WireMock in tests). Idempotency: send `Idempotency-Key` header where supported (TTS/translation), value `$"{run}:{stage}:{scope}:{attempt}"`.
4. Google: `GoogleSttProvider` (Speech-to-Text v2 `recognize`), `GoogleTranslationProvider` (Translate v3), `GeminiLlmProvider` (Gemini `generateContent` for context/translation when `Providers:Google:UseGeminiForTranslation=true`), `GoogleTtsProvider` (`text:synthesize`). Config `Google:ApiKey,ProjectId,Location`.
5. LocalInference: `LocalInferenceProvider : ILocalInferenceProvider + capability bridges` calling sidecar `LocalInference:Endpoint` (HTTP POST `/infer` with `{modelName,modelVersion,capability,payload}` or gRPC if `Protocol=grpc`); include `model artifact hash, device profile, concurrency policy, warmup state`; `WarmupAsync` called before accepting work; enforce `MaxConcurrency` semaphore (default 2, GPU 1). Record model hash + device in ProviderExecution.
6. Long-running jobs: `ProviderJobPoller.cs` generic: `Start→store ExternalJobId in ProviderExecution→poll every 10s up to 30min→on lease loss reconcile by ExternalJobId`. Implement for Azure batch STT + OpenAI batch where applicable.
7. Config validation: `ValidateConfig()` per adapter throws PROVIDER_CONFIGURATION_ERROR on missing key when that provider enabled in RoutePriority; startup fail-fast.
8. Fixtures: `tests/DubbingPlatform.ContractTests/Fixtures/` WireMock stubs for 429/timeout/malformed/async lifecycle/duplicate/partial/expiry/quota per provider; tests in `ProviderContractTests.cs` asserting outcome classification + fallback + recording.

## Requirements

- R1: All listed adapters exist with correct capabilities.
- R2: Idempotency keys sent where supported.
- R3: Long-job start/poll/reconcile works.
- R4: Missing creds fail fast when configured.
- R5: Every call recorded even on fallback.

## Edge Cases and Error Handling

- 429 → delayed retry (not immediate), respect Retry-After header.
- Timeout → ProviderTimeout, retryable within budget.
- Malformed payload → ProviderInvalidResponse, fallback not transport-retry.
- Job expiry → mark Failed + review if policy.
- Duplicate request (same idempotency key) → return existing output hash, no double charge.

## Security and Safety Requirements

- Keys from env/secret manager only; never logged/hashed; BaseUrl allowlist (https or localhost for tests); mTLS to sidecar where configured.

## Testing

Create `tests/DubbingPlatform.ContractTests/Providers/ProviderContractTests.cs` (WireMock): `Handles_429`, `Handles_Timeout`, `Handles_Malformed`, `Async_Job_Lifecycle`, `Duplicate_Request_Reconciled`, `Partial_Result`, `Job_Expiry`, `Quota_Exhausted` per provider family (parameterized).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ProviderContractTests
```

## Completion Criteria

- Adapters + poller + fixtures work; contract tests pass without live keys.

## Traceability

- Plan Section 6 actions 22–29; Assumptions 41–43; Tests checklist Azure/OpenAI/Google contract + async/429/malformed/expiry.
