# Real Provider Adapters

Azure, OpenAI, Google/Gemini, and Local sidecar adapters with long-running job
support, idempotency, and WireMock contract coverage. No live paid calls in CI.

## Families and capabilities

| Family | Adapters | Capabilities |
|---|---|---|
| Azure | `AzureSttProvider` | Transcription + Diarization (conversation) + batch |
| Azure | `AzureTranslationProvider` | Translation (Translator Text) |
| Azure | `AzureTtsProvider` | TTS |
| OpenAI | `OpenAiSttProvider` | Transcription (whisper multipart) + batch |
| OpenAI | `OpenAiTranslationProvider` | Translation (chat with glossary prompt) |
| OpenAI | `OpenAiTtsProvider` | TTS |
| Google | `GoogleSttProvider` | Transcription (STT v2 recognize) |
| Google | `GoogleTranslationProvider` | Translation (Translate v3) |
| Google | `GeminiLlmProvider` | Translation via `generateContent` (when `Google:UseGeminiForTranslation=true`) |
| Google | `GoogleTtsProvider` | TTS (`text:synthesize`) |
| Local | `LocalInferenceProvider` | LocalInference + bridges (transcription/translation/TTS via `/infer`) |

Mocks remain the default (`Providers:DefaultProvider=mock`). Real adapters
register as concrete singletons plus keyed mappings
(`azure/openai/google/gemini/local`) for resolver-driven selection; routing
itself is unchanged (014).

## Configuration

```json
{
  "Azure": { "SpeechKey": "", "SpeechRegion": "", "TranslatorKey": "", "SpeechBaseUrl": null, "TranslatorBaseUrl": null },
  "OpenAI": { "ApiKey": "", "Model": "whisper-1", "ChatModel": "gpt-4o-mini", "TtsModel": "tts-1", "BaseUrl": "https://api.openai.com/v1" },
  "Google": { "ApiKey": "", "ProjectId": "", "Location": "global", "UseGeminiForTranslation": false, "SpeechBaseUrl": null, "TranslateBaseUrl": null, "GeminiBaseUrl": null, "TtsBaseUrl": null },
  "LocalInference": { "Endpoint": "http://localhost:8081", "Protocol": "http", "ModelName": "local-small", "ModelVersion": "1", "ModelHash": "", "Device": "cpu", "MaxConcurrency": 2, "RequireMtls": false }
}
```

- Keys from env/secret manager only (`Azure__SpeechKey`,
  `OpenAI__ApiKey`, `Google__ApiKey`); never logged/hashed (see
  `ConfigurationHashCalculator` secret stripping + `[Secret]`).
- `BaseUrl`/`Endpoint` allowlist: `https` anywhere, or `http` only for
  loopback (`localhost/127.0.0.1/::1`) for WireMock tests.
- Empty keys pass startup validation for mock-only boots; per-adapter
  `ValidateConfig()` + `ProviderStartupValidator` throw
  `PROVIDER_CONFIGURATION_ERROR` when a family is enabled/routed without keys.
- Local `MaxConcurrency` 1..16 (default 2); `WarmupAsync()` (GET `/health`)
  must succeed before `InferAsync`; `RequireMtls=true` requires https +
  client cert (`ClientCertificatePath`, X509 via `X509CertificateLoader`).

## Wire contracts (hermetic)

- Azure STT: `POST {SpeechBase}/speech/recognition/transcribe?language=` +
  `{"artifactId","language"}` → `{"text","confidence","words":[]}`;
  diarization: `.../conversation` → `{"segments":[]}`;
  batch: `POST .../speech/batch` → `202 {"jobId"}`,
  `GET .../speech/batch/{id}` → `{"status","reason"}`.
- Azure Translator: `POST {TranslatorBase}/translate?api-version&from&to` +
  `[{"Text"}]` → `[{"translations":[{"text"}],"alternatives":[]}]`.
- Azure/OpenAI/Google TTS: `POST ...` → `{"contentId","durationMs","voice","confidence"}`.
- OpenAI STT: `POST {BaseUrl}/audio/transcriptions` multipart
  (`file` stub + `model`/`language`); chat: `POST /chat/completions` with
  glossary system prompt; batch: `POST /audio/batch` → `202 {"jobId"}`.
- Google: `POST .../v2/...:recognize` → `{"results":[{"transcript","confidence","words":[]}]}`;
  `.../v3/...:translateText` → `{"translations":[{"translatedText"}]}`;
  Gemini `.../v1/models/{m}:generateContent` → `{"candidates":[{"content":{"parts":[{"text":"{\"primary\":...}"}]}}]}`.
- Local: `POST {Endpoint}/infer` +
  `{"modelName","modelVersion","capability","payload"}` →
  `{"output","confidence","modelHash","device"}`; `GET /health` for warmup.

## Failure mapping

429 → `PROVIDER_RATE_LIMITED` (quota body/header → `PROVIDER_QUOTA_EXHAUSTED`);
408/504 → `PROVIDER_TIMEOUT`; other 4xx → `PROVIDER_FAILED` (permanent);
5xx → `PROVIDER_FAILED` (transient); 200 invalid JSON → `PROVIDER_INVALID_RESPONSE`;
job `Failed/Expired` → `PROVIDER_FAILED` (review if policy).
`Retry-After` is surfaced in the message; `IProviderHealthTracker` applies the
delay. Timeouts respect cancellation (external cancel → `OperationCanceledException`).

## Idempotency and jobs

- `Idempotency-Key: {run:N}:{stage}:{scope}:{attempt}` sent on all
  TTS/translation calls (Azure/OpenAI/Google); duplicates reconcile via
  `ProviderExecutionRecorder` (same hashes → existing id, different → 409).
- `ProviderJobPoller`: `PollAsync(fetch, externalJobId, 10s, 30min)` +
  `ReconcileAsync` (single fetch for lease-loss). Azure/OpenAI batch adapters
  expose `StartBatchAsync` → `ExternalJobId` (store on `ProviderExecution`) +
  `GetBatchStatusAsync` for the poller.
- Every call records model/version/device/job/idempotency in
  `ProviderExecution` (see `RawMetadata`: `provider`, `model.hash`, `device`);
  fallback attempts record separately (R5).
