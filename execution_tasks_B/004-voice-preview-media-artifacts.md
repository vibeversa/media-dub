# Task 004 — Voice Preview Jobs and Media Preview Artifacts

## Goal
Add VoicePreviewJob lifecycle and preview artifact types for fast UI playback without touching final audio.

## Context
Voice assignment needs instant audible preview and the media workspace needs cheap peaks/thumbnails; generating these from final pipeline artifacts is too slow and couples preview failure to pipeline failure. Plan B requires a separate preview lane produced during analysis/audio-prep and served via lightweight ContentObject-backed artifacts.

## Starting State
Task 001 done (TenantUser, ProjectMembership exist). Plan A ContentObject, provider execution records, and audio-prep stage exist. No VoicePreviewJob table, no MediaPreviewAudio/WaveformPeaks/VideoPreview/VoicePreviewAudio/QcEvidenceArtifact types.

## Scope
Included: VoicePreviewJob entity + state machine, preview artifact type registrations, generation hooks in analysis/audio-prep, quota/consent gates, EF configs + migration + RLS, provider execution record for previews.
Excluded: HTTP endpoints (Task 010), signed-URL serving (Task 012/037), frontend player/waveform/timeline (Task 030), final dub audio path (Plan A, untouched).

## Instructions
1. Create `src/DubbingPlatform.Domain/Entities/VoicePreviewJob.cs`: `Id` (`vpv_` prefix mapped in API), `TenantId`, `ProjectId`, `SpeakerId`, `VoiceId`, `Text` (max 500 chars, server-truncated), `Status` (Pending/Running/Completed/Failed/Cancelled), `RequestedByUserId`, `IdempotencyKey`, `QuotaCheck` (Allowed/Denied + reason), `ConsentState` (Verified/Blocked), `ProviderExecutionId?`, `ArtifactId?`, `ErrorCode?`, `ErrorMessage?`, `CreatedAt`, `StartedAt?`, `CompletedAt?`. Unique `(TenantId, IdempotencyKey)` where key not null.
2. Add `VoicePreviewJobConfiguration.cs` in `src/DubbingPlatform.Infrastructure/Persistence/Configurations/` + migration `AddVoicePreviewJobs`; enable RLS on tenant; indexes `(TenantId, ProjectId, Status)`, `(TenantId, SpeakerId, CreatedAt)`.
3. Register preview artifact types on existing ContentObject (no new blob table): `MediaPreviewAudio` (short proxy mp3), `WaveformPeaks` (multi-resolution JSON: 64/256/1024 peaks per media), `VideoPreview` (optional low-res proxy), `VoicePreviewAudio` (per preview job), `QcEvidenceArtifact` (QC clip/snapshot reference). Store as `ContentObject.Purpose` enum extension + `MetadataJson` (durationMs, sampleRate, resolutions, sourceMediaId).
4. Create `src/DubbingPlatform.Application/Previews/VoicePreviewService.cs`: `RequestPreviewAsync(...)` enforces consent gate (cloning voice without consent → `VOICE_CONSENT_REQUIRED`), quota/rate-limit check (per-tenant daily cap + per-minute throttle), then enqueues provider call with execution record; `CancelAsync(...)` only from Pending/Running.
5. Create `src/DubbingPlatform.Application/Previews/MediaPreviewGenerator.cs`: hooked into analysis/audio-prep completion — generates `MediaPreviewAudio` + `WaveformPeaks` (all three resolutions) per accepted media; `VideoPreview` only when source video exists and flag `preview.video.enabled` is true. Failures logged with `correlationId`, never fail the parent stage.
6. Create `src/DubbingPlatform.Application/Previews/QcEvidenceLinker.cs`: attaches `QcEvidenceArtifact` references to QC issues (clip range + peak slice), tenant-scoped, no duplication on re-run (dedupe on `(TenantId, QcIssueId, ArtifactKind)`).
7. Wire MassTransit consumers / domain-event handlers idempotently: preview request → Running → Completed/Failed; preview completion publishes `VoicePreviewCompleted` (consumed by Task 010 endpoint layer later).

## Requirements
- R1: Preview status machine only allows Pending→Running→Completed/Failed and Pending/Running→Cancelled; illegal transitions return 409 `PREVIEW_STATE_CONFLICT`.
- R2: Quota-exceeded request fails fast with 429 `PREVIEW_QUOTA_EXCEEDED` and no provider call is made.
- R3: Cloning-voice preview without recorded consent is blocked with `VOICE_CONSENT_REQUIRED` (no bypass flag).
- R4: Preview artifacts are tenant-scoped ContentObjects, never reuse final-audio artifact IDs.
- R5: Preview generation failure never fails analysis/audio-prep stage (parent stage completes with `previewDegraded: true`).
- R6: `WaveformPeaks` always contains 64/256/1024 resolutions or the media is flagged `peaksMissing`.
- R7: RLS enabled on `VoicePreviewJob`; all reads tenant-scoped.

## Edge Cases and Error Handling
- Empty/overlong preview text → 400 `PREVIEW_TEXT_INVALID` (trim, max 500).
- Duplicate idempotency key → return existing job (200), do not enqueue second provider call.
- Provider timeout → job Failed with `PREVIEW_PROVIDER_TIMEOUT`, retryable via new idempotency key.
- Cancel on terminal job → 409, no state change.
- Media without audio track → `MediaPreviewAudio` skipped, `WaveformPeaks` empty-flagged, stage still completes.

## Security and Safety Requirements
- All preview reads/writes tenant-scoped; cross-tenant job ID returns 404.
- Preview text sanitized (max length, no SSRF via voice-sample URLs — voice IDs server-resolved only).
- No provider secrets in job rows or logs; execution record stores provider name + latency, never keys.
- Consent decisions audited via existing AuditEvent.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Previews/PreviewArtifactTests.cs`: lifecycle Pending→Running→Completed, cancel path, quota-denied no-provider-call, consent-blocked cloning voice, preview-failure-does-not-fail-stage, multi-resolution peaks present, cross-tenant isolation, idempotent duplicate key.
- Type: integration (Testcontainers PostgreSQL); provider client mocked.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~PreviewArtifactTests
```

## Completion Criteria
- VoicePreviewJob + preview artifact generation exist with migration + RLS; `PreviewArtifactTests` pass; parent stage completes when preview generation throws (verified by test).

## Traceability
- Plan B §8.6, §8.7, §8.9, §9.6.
