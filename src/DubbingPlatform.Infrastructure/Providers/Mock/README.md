# Mock Providers

Deterministic fixture-script-driven adapters for all 9 capabilities.
Default for local development and CI (`Providers:DefaultProvider=mock`);
no network calls, no secrets, no paid services.

## Determinism guarantee

Same input yields byte-identical output across runs, replicas, and restarts:

- All randomness derives from SHA-256 of the input parts
  (`TenantId|ProjectId|RunId|ArtifactId|Language|...`), never
  `Random.Shared`, time, or GUIDs. The task's `new Random(42)` seed is the
  degenerate case; per-request seeds are `SHA-256(parts)[0..4]` so different
  inputs diverge deterministically while identical inputs repeat exactly.
- Transcription text is `mock transcript seg {ArtifactId} [{Language}]`
  with word timestamps every 300ms; translation primary is
  `mock-{target}::{sourceText}` plus two `alt1/alt2` alternatives;
  diarization splits into 2-3 segments (seeded) with round-robin
  `spk_0/spk_1`; VAD returns one region covering the full duration;
  separation ids are `mock-dialogue-{hash}` / `mock-bg-{hash}`;
  video returns one `track_0` face plus one `spk_0` span;
  local inference echoes `PayloadJson`; TTS duration is
  `wordCount*400ms` clamped `500..5000ms` with a generated 16kHz mono
  sine WAV (44-byte header + PCM16, 440Hz) whose SHA-256 is returned as
  `mock.contentHash` alongside `mock-tts-{hash}` content id.
- `FailRate` sampling uses the same stable seed (`seedKey|failrate`), so
  probabilistic injection is also repeatable per input.
- Lip-sync returns score 0.85 (`lipSyncScore`) covering the full duration
  (0.35 on low-confidence); video returns one `track_0` face plus one
  `spk_0` span.

## Configuration

```json
{
  "Providers": {
    "DefaultProvider": "mock",
    "Mock": {
      "Scenario": "success",
      "FailRate": 0,
      "FailWith": null,
      "Behaviors": {
        "Transcription": { "Scenario": "rate-limited" },
        "Tts": { "Scenario": "success", "FailRate": 0.1, "FailWith": "timeout" }
      }
    }
  }
}
```

- Global defaults bind from `Providers:Mock`; per-capability overrides via
  `Providers:Mock:Behaviors:{Capability}:Scenario` (capability names
  `Vad|Diarization|Transcription|Translation|Tts|SourceSeparation|VideoIntelligence|LipSync|LocalInference`,
  case-insensitive). `FailRate` must be in `0..1`; out-of-range fails
  validation. Unknown `Scenario`/`FailWith` strings fail fast with
  `PROVIDER_CONFIGURATION_ERROR` at startup and at call time.
- `FailRate` semantics: `Scenario=success` plus `FailRate>0` injects
  `FailWith ?? rate-limited` with probability `FailRate` per input;
  a non-success `Scenario` with `FailRate=0` always applies (deterministic
  fixtures); with `FailRate>0` it applies with probability `FailRate`
  else succeeds. Routing still selects via `Providers:RoutePriority`.

## Scenario table

| Scenario | Observable | Error code / outcome |
|---|---|---|
| `success` | Deterministic success payload (`Confidence` 0.85-0.99) | `Success` |
| `low-confidence` | Same shape, `Confidence` 0.35 (sub-scores 0.35) | `QualityBelowThreshold` (quality path, never transport retry) |
| `rate-limited` | Throws | `PROVIDER_RATE_LIMITED` (429) / `ProviderRateLimited` |
| `timeout` | Cancellable 50ms delay then throws; cancelled token throws `OperationCanceledException` | `PROVIDER_TIMEOUT` (504) / `ProviderTimeout` |
| `malformed` | Throws | `PROVIDER_INVALID_RESPONSE` (502) / `ProviderInvalidResponse` |
| `async-job` | `StartJob` returns `job_{hash}`; `PollJob` returns `Running` twice then `Succeeded`; interface call polls to completion and returns success with `mock.job_id` + `mock.polls=3` | `Success` after poll/reconcile |
| `duplicate` | Success payload plus `mock.duplicate=true` (byte-identical to `success`) | `Success` (idempotent replay) |
| `partial` | Truncated payload plus `mock.partial=true` (half words/regions, no alternatives/background/speakers, half TTS duration, truncated local echo) | `Success` (downstream QC decides) |
| `expired` | Throws | `PROVIDER_FAILED` (502) / `ProviderPermanentFailure` |
| `quota-exhausted` | Throws | `PROVIDER_QUOTA_EXHAUSTED` (429) |

## Async-job pattern

Each mock exposes `StartJob(string key) -> job_<16hex>` and
`PollJob(string jobId) -> (Status, PollCount)` backed by an in-memory
counter keyed by job id. `Status` is `Running` for the first two polls,
`Succeeded` from the third on. Interface methods in `async-job` mode call
`StartJob` then poll up to three times (respecting cancellation) before
returning the success payload, exercising the start/poll/reconcile path
without external systems.
