# Task 19 — Upload Ingestion and Media Validation

## Goal

Implement resumable-upload completion handling, content hashing, duplicate detection, FFprobe-based media validation, and media-readiness transitions.

## Context

Binding: object-storage multipart authoritative, DB metadata reconciled at completion. Completion validates parts, completes multipart, marks Completed, publishes MediaUploaded. Ingestion validates completion, checks client SHA-256 if supplied, computes/verifies SHA-256 streaming, creates tenant ContentObject + source Artifact, duplicate handling (same-project → link existing asset + mark Duplicate, no new pipeline; cross-project same-tenant → reuse ContentObject + new logical asset), content sniffing (never trust MIME/extension), FFprobe staged/streamed JSON (container/duration/streams/codecs/resolution/fps/sampleRate/channels), validates size/duration/container allowlist/decodable audio/video/corruption; reject MEDIA_UNSUPPORTED/MEDIA_CORRUPT; persist metadata + FFprobe artifact + MediaValidation stage execution; valid→asset Valid + project MediaReady + publish MediaValidated; invalid→asset Invalid + MediaRejected + reason. Enforce storage + duration quotas. Use existing IArtifactStorage with key `{tenant}/{project}/{run-or-norun}/{stage}/{type}/{hash}{ext}` (for source use run=uploadId), StageExecutionService, ArtifactService.

## Starting State

Upload session endpoints exist (create/part/complete/abort/status). No ingestion worker, no FFprobeService, no validation logic, no duplicate handling. MediaOptions, error codes, storage, messaging exist.

## Scope

Must implement: FFprobeService, MediaIngestionWorker (MediaUploaded consumer), validation rules, duplicate logic, quota checks. Must not implement: downstream analysis/prep workers.

## Instructions

1. Create `src/DubbingPlatform.Infrastructure/Media/FFprobeService.cs`: `Task<FfprobeResult> ProbeAsync(string storageKeyOrLocalPath, CancellationToken)` using ProcessRunner `ffprobe -v quiet -print_format json -show_format -show_streams <file>` (download to /tmp if S3 key, streaming to temp file, delete after). Parse to `FfprobeResult { string Container; long DurationMs; List<StreamInfo> Streams; } StreamInfo { string CodecType; string Codec; int? Width,Height; double? Fps; int? SampleRate; int? Channels; string? ChannelLayout; }`. Never trust client MIME.
2. Create `src/DubbingPlatform.Workers/Consumers/MediaIngestionWorker.cs` : BaseConsumer<MediaUploaded> (queue media.preparation): validate upload Completed + S3 parts present; if ClientSha256 supplied verify else compute streaming SHA-256 during download (do not double-download if S3 checksum authoritative and matches); check storage quota (QuotaService interface — use stub `IQuotaGate.CheckStorageAsync` returning allow unless over MaxStorageBytes) + duration quota; dedup: lookup ContentObject by (Tenant,Hash): same-project existing MediaAsset with same hash → mark UploadSession Duplicate, link asset, publish nothing further; cross-project same-tenant → reuse ContentObject, create new MediaAsset+Artifact; else create ContentObject+Artifact(source original, Pending→Committed atomically with stage completion + outbox).
3. Validation: size 1..MaxUploadBytes, duration 1s..MaxDurationMs, container in AllowedContainers, ≥1 audio stream decodable (codec in {aac,mp3,pcm,flac,opus,vorbis}), video if present codec in {h264,hevc,vp9,av1} else MEDIA_UNSUPPORTED; ffprobe exit≠0 or unparseable → MEDIA_CORRUPT. Persist MediaAsset (Valid/Invalid + FailureReason) + FFprobe analysis Artifact (type FfprobeAnalysis, jsonb metadata) + StageExecution MediaValidation Completed/Failed.
4. Transitions: valid → DubbingProject Status Uploading→MediaReady (via ProjectStateMachine), publish MediaValidated(IsValid=true); invalid → MediaRejected + MediaValidated(false) + record failure.
5. Config: `MediaOptions` thresholds; part size 8MB; presigned 15min.

## Requirements

- R1: Valid MP4 → MediaReady + FFprobe artifact.
- R2: Text-renamed-MP4 → MEDIA_CORRUPT/MediaRejected.
- R3: Incomplete complete → UPLOAD_INCOMPLETE.
- R4: Duplicate same-project links, cross-project reuses object + new asset.
- R5: Quotas enforced.

## Edge Cases and Error Handling

- Interrupted upload resume via status endpoint (missing parts listed).
- Corrupt/unsupported → structured codes, no retry (fail fast).
- Checksum mismatch client vs computed → ARTIFACT_CHECKSUM_MISMATCH.
- Concurrent duplicate uploads → unique (Tenant,Hash) wins, loser reuses.
- FFprobe timeout → MEDIA_CORRUPT retryable once then fail.

## Security and Safety Requirements

- FFprobe via ArgumentList only; temp files deleted always (finally); sniffing prevents MIME spoof; tenant-scoped keys; no secrets logged.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Media/IngestionTests.cs` (MinIO+PG+FFmpeg required; skip if ffmpeg missing with explicit message): `Valid_Mp4_Becomes_MediaReady`, `Invalid_Text_Renamed_Rejected`, `Incomplete_Completion_Fails`, `Duplicate_SameProject_Links`, `CrossProject_Reuses_Object`, `Quota_Rejection`, `Ffprobe_Artifact_Exists`. Provide `scripts/generate-fixtures.sh` stub generating 2s sine MP4 via ffmpeg (full fixtures Task 39, minimal here).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~IngestionTests
```

## Completion Criteria

- Ingestion + validation + duplicates + quotas work; tests pass.

## Traceability

- Plan Section 8 actions 9–25; Assumptions 15,18–20,24; Functional checklist uploads/duplicates/validation/source-immutable; Error checklist interrupted/incomplete/unsupported/corrupt.
