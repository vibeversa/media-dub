# Task 15 — Mock Providers

## Goal

Implement deterministic fixture-script-driven mock adapters for all 8 capabilities so local development and CI run without paid services.

## Context

Binding: mocks deterministic (stable text/timestamps/speakers/durations, configurable failures); default for local/CI; fallback path explicit; provider vs quality distinct; long-running job support pattern (start/poll/reconcile) must be mockable; contract fixtures needed for 429/timeout/malformed/async/duplicate/partial/expiry/quota.

## Starting State

Provider interfaces, DTOs, resolver, recorder, health exist. No concrete adapters. `Providers:DefaultProvider=mock` config exists.

## Scope

Must implement: 8 mock adapters + fixture scripts + failure injection + DI registration. Must not implement: real Azure/OpenAI/Google/Local adapters.

## Instructions

1. Create `src/DubbingPlatform.Infrastructure/Providers/Mock/` namespace `DubbingPlatform.Infrastructure.Providers.Mock`, one file per adapter: `MockVadProvider, MockDiarizationProvider, MockTranscriptionProvider, MockTranslationProvider, MockTtsProvider, MockSourceSeparationProvider, MockVideoIntelligenceProvider, MockLocalInferenceProvider`, each implementing its interface.
2. Determinism: seed `new Random(42)` or hash of (TenantId+SegmentId) for stable outputs; e.g., MockTranscription returns `Text=$"mock transcript seg {ScopeId} [{Language}]"`, Confidence=0.95, Words split every 300ms; MockTranslation returns Primary=`$"mock-{target}::{sourceText}"` + 2 alternatives; MockTts returns 1s sine WAV bytes (generate via code, 16kHz mono) with DurationMs=wordCount*400ms clamped 500..5000ms, stored via IArtifactStorage in later tasks but here return in-memory stream + ContentHash; MockDiarization returns round-robin `spk_0/spk_1` stable per SegmentId hash; MockVad returns single region full duration; MockSeparation returns same bytes + Confidence=0.85; MockVideo returns 1 face + active speaker; MockLocal returns echo payload.
3. Fixture scripts: `MockBehaviorOptions { string Scenario="success"; double FailRate=0; string? FailWith=null; }` bound from `Providers:Mock:Scenario`; support scenarios `success, low-confidence, rate-limited, timeout, malformed, async-job, duplicate, partial, expired, quota-exhausted` mapping to OutcomeClass (RateLimited→ProviderRateLimited, timeout→ProviderTimeout, malformed→ProviderInvalidResponse, quota→ProviderQuotaExhausted). Configurable per capability via `Providers:Mock:Behaviors:Transcription:Scenario`.
4. Async-job mock: `StartJob` returns `job_<hash>`, `Poll` returns Running twice then Succeeded (counter in memory keyed by job id) to exercise poll/reconcile.
5. Register in DI: `services.AddSingleton<ITranscriptionProvider, MockTranscriptionProvider>()` etc. when `Providers:DefaultProvider==mock` (default); allow per-capability override `Providers:RoutePriority`.
6. Document in `MOCK_PROVIDERS.md` (create at `src/DubbingPlatform.Infrastructure/Providers/Mock/README.md`): determinism guarantee + scenario table.

## Requirements

- R1: All 8 mocks implement interfaces.
- R2: Same input → byte-identical output (deterministic).
- R3: All failure scenarios configurable.
- R4: Mocks default; DI switchable.

## Edge Cases and Error Handling

- Unknown scenario string → fail fast configuration error.
- FailRate between 0..1; out-of-range → validation error.
- Timeout scenario must respect CancellationToken (throw OperationCanceledException, mapped to PROVIDER_TIMEOUT).

## Security and Safety Requirements

- No network calls, no secrets. Deterministic, no randomness without seed.

## Testing

Create `tests/DubbingPlatform.UnitTests/Providers/MockDeterminismTests.cs`: `Same_Input_Same_Output` for each mock (call twice, assert equal), `Stable_Speaker_Labels`, `Configurable_Failure_Emits_Correct_Outcome` (each scenario), `Async_Job_Lifecycle` (start→running→succeeded).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~MockDeterminismTests
```

## Completion Criteria

- 8 mocks + scenarios work; determinism tests pass.

## Traceability

- Plan Section 6 actions 20–21; Assumptions 39–40; Tests checklist mock providers.
