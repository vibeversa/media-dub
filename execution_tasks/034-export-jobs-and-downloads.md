# Task 34 — Export Jobs and Downloads

## Goal

Implement on-demand durable export jobs with completeness metadata and secure downloads, independent of the core DAG.

## Context

Binding: export removed from core DAG. Formats SRT/WebVTT/JSON-timeline/speaker-metadata/transcript/translation/quality-report. ExportService generates from selected immutable run data. Supports completed + partial (failed/cancelled where data exists). Never export rendered media unless render completed. Completeness {projectId,runId,sourceHash,completed,failed,skipped,reviewCount,isComplete,generatedAt}. Artifacts immutable. Audit. Signed URLs 15min. Ownership enforced. Endpoints already routed (Task 18) — implement service + worker + list/download logic here.

## Starting State

Render/OutputAsset + segments/translations/QC results exist. Export endpoints routed but service stubbed. No ExportService/ExportWorker, no format generators.

## Scope

Must implement: ExportService, 7 format generators, ExportWorker, list/get/download endpoints logic, partial handling. Must not implement: core pipeline changes.

## Instructions

1. Create `src/DubbingPlatform.Application/Exports/` generators: `SrtGenerator` (timestamps `HH:MM:SS,mmm`, selected translations, ordered seq), `WebVttGenerator` (`WEBVTT` header, `HH:MM:SS.mmm`), `TimelineJsonGenerator` (mirror timeline artifact + completeness), `SpeakerMetadataGenerator` (speakers + voice assignments), `TranscriptJsonGenerator`, `TranslationJsonGenerator`, `QualityReportGenerator` (QC results). Each `string Generate(runData)` deterministic, UTF-8, LF.
2. Create `ExportService.cs`: `RequestAsync(tenant,project,format)` validates render-completed if format requires media? Decision: subtitle/JSON exports allowed partial; `rendered-media` never exported (throw EXPORT_NOT_READY if render incomplete and format==rendered — but no such format; enforce: if run not Completed and format==JsonTimeline include IsPartial=true). Creates ExportJob Pending→Running, generates content from immutable snapshots (selected versions at request time), uploads artifact type Export, sets CompletenessJson, marks Completed, audit event, returns job id. `ListAsync`, `GetAsync`, `GetDownloadUrlAsync` (ownership + 15min presigned).
3. Create `ExportWorker : BaseConsumer<ExportJobRequested>` (queue export): claim job, call service generation, Complete/Fail/Cancel per ExportStateMachine.
4. Wire controllers: `POST /projects/{id}/exports` body `{format: srt|webvtt|json-timeline|speaker-metadata|transcript|translation|quality-report}` 202 `{exportId: exp_...}` idempotency 24h; `GET .../exports` paginated; `GET .../exports/{exportId}` 200 with status+completeness; `GET .../exports/{exportId}/download` 302/200 with presigned URL JSON `{url, expiresAt}`.
5. Partial rule: if run Failed/Cancelled, include available segments + `isComplete:false` + counts; machine-readable JSON always valid.

## Requirements

- R1: SRT/WebVTT valid timestamps.
- R2: JSON timeline matches segments.
- R3: Speaker metadata stable.
- R4: QC report includes entries.
- R5: Partial includes completeness; signed URL expires.

## Edge Cases and Error Handling

- No data (zero segments) → 400 EXPORT_NOT_READY.
- Render incomplete + request timeline → partial with flag, not fail.
- Concurrent duplicate export request (same idempotency) → replay same job.
- Cancelled job → status Cancelled, no artifact.

## Security and Safety Requirements

- Ownership enforced; 15min URLs; audit; tenant-scoped artifacts.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Api/ExportTests.cs`: `Srt_Valid`, `WebVtt_Valid`, `Timeline_Matches`, `Speaker_Stable`, `Qc_Included`, `Partial_Has_Completeness`, `Url_Expires`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ExportTests
```

## Completion Criteria

- Exports on-demand/complete-or-partial + downloads work; tests pass.

## Traceability

- Plan Section 23; Functional checklist exports on-demand/partial; Error checklist partial machine-readable.
