# Task 28 — Voice Generation TTS

## Goal

Generate target-language speech artifacts with traceability, pre-call duration estimation, preview/final separation, and budget enforcement.

## Context

Binding: segment-scoped; uses selected translation + assigned voice. Deterministic estimator before paid TTS (phoneme/rate model + window + prosody). Initial SSML/prosody adjustment before first call when possible. Preview vs final artifacts distinct. GeneratedAudioArtifact fields provider/model/voice/duration/content/attempt/preview-flag. Record executions. TTS budget max 3 attempts (`Tts: { MaxAttempts=3, EstimatorEnabled=true }`). Cost reservation before call. Barrier per segment.

## Starting State

Selected translations + voice assignments exist. No TtsService/VoiceGenerationWorker, no estimator.

## Scope

Must implement: duration estimator, TtsService, VoiceGenerationWorker, preview/final handling, budgets. Must not implement: timing optimization (next task).

## Instructions

1. Create `src/DubbingPlatform.Application/Services/DurationEstimator.cs`: `int EstimateMs(string text, string targetLang, double rate=1.0)` deterministic: `chars * perCharMs[lang] / rate` where perCharMs table `{ en:70, es:75, de:80, fr:75, default:75 }` (document); clamp 300..30000ms; no randomness; used to pre-adjust prosody (`rate = clamp(targetMs/estimate, 0.85, 1.15)`) and choose SSML `<prosody rate>` before first paid call.
2. Create `TtsService.GenerateAsync(tenant,project,run,segment,isPreview,ct)`: load translation + voice; run estimator; apply prosody; reserve cost via ICostGate (fail QUOTA_EXCEEDED without call); resolve ITtsProvider; call; validate output decodable via FFprobeService (readable audio, duration>0); persist GeneratedAudioArtifact (IsPreview flag; preview stored separately, never selected as final); record ProviderExecution; enforce MaxAttempts (loop only for retryable outcomes; invalid input fail-fast).
3. Preview vs final: `isPreview=true` for timing-optimization probes (Task 29 calls with preview), `false` for final; both immutable; final selected after timing passes.
4. Create `VoiceGenerationWorker : BaseConsumer<StageWorkRequested>` (VoiceGeneration, scope Segment, queue ai.provider): claim, call GenerateAsync(isPreview=false initial), Complete/barrier.
5. Config: `Tts: { MaxAttempts=3, PreviewEnabled=true }`.

## Requirements

- R1: Output FFprobe-readable.
- R2: Preview/final distinct rows.
- R3: Estimator reduces attempts (prosody pre-adjust).
- R4: Reservation enforced before call.
- R5: Executions recorded.

## Edge Cases and Error Handling

- Reservation fail → QUOTA_EXCEEDED, no call.
- Provider rate-limit → delayed retry within budget.
- Corrupt TTS bytes (undecodable) → PROVIDER_INVALID_RESPONSE + fallback provider once.
- Budget exhausted → review (TTS_QUALITY) not silent fail.
- Lease lost → discard bytes, orphan reconciler cleans.

## Security and Safety Requirements

- Tenant-scoped audio; temp files cleaned; no secrets in SSML/logs.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Pipeline/TtsTests.cs` (mocks): `Readable_By_Ffprobe`, `Preview_Final_Distinct`, `Estimator_Reduces_Attempts` (assert prosody rate applied), `Reservation_Enforced` (mock gate deny → no provider call), `Execution_Recorded`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~TtsTests
```

## Completion Criteria

- TTS traceable/budgeted/preview-aware; tests pass.

## Traceability

- Plan Section 17; Assumptions 74 estimator; Functional checklist TTS versioned/traceable.
