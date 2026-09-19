# Task 18 — Project and Upload Endpoints

## Goal

Implement project CRUD, multipart upload session endpoints, and processing/review/export/output route surface with exact routes, schemas, status codes, and side effects.

## Context

Binding: endpoints list (32 routes under /api/v1): POST+GET /projects, GET+DELETE /projects/{projectId}, POST /projects/{projectId}/uploads, GET .../uploads/{uploadId}, POST .../parts, POST .../complete, POST .../abort, POST+GET .../processing, POST .../cancel, POST .../retry, GET .../progress, GET .../segments, GET .../segments/{segmentId}, POST .../segments/{segmentId}/retry, GET .../reviews, GET /reviews/{reviewId}, POST /reviews/{reviewId}/approve|reject|requeue|resolve, POST+GET /projects/{projectId}/exports, GET .../exports/{exportId}, GET .../exports/{exportId}/download, GET .../output/download. Project creation validates languages/settings, computes config hash, persists, publishes event. Upload creation validates filename/type/size, enforces max, creates S3 multipart. Part URLs 15min. Idempotency per Task 17. Auth matrix per Task 17. Pagination. Signed downloads 15min. Audit privileged. Correlation IDs.

## Starting State

Controller shells + auth + idempotency + pagination + OpenAPI exist. UploadService/MediaIngestion not yet implemented (stub). DbContext + storage interfaces exist.

## Scope

Must implement: ProjectsService + UploadService session logic + all controller actions with validation/status codes (business pipeline triggers publish minimal MediaUploaded/RunStarted messages where DB state allows; full worker logic later). Must not implement: FFprobe validation, ingestion worker, processing saga execution, mixing/render.

## Instructions

1. Implement `src/DubbingPlatform.Application/Services/ProjectService.cs`: `CreateAsync(tenant, sourceLang, targetLang, settingsJson, idempotencyKey)` validates ISO codes (2-3 letters, different), settings JSON valid (if empty use `{}`), computes ConfigurationHash, inserts DubbingProject Status=Created + ProcessingPolicy default + AuditEvent; `ListAsync(tenant,page)`, `GetAsync(tenant,projectId)` (404 NOT_FOUND if missing, 403 if tenant mismatch), `DeleteAsync` (logical delete flag via DeletedAt? Decision: add `bool IsDeleted` shadow — document; sets artifact refs removed later by Task 37).
2. Implement `src/DubbingPlatform.Application/Services/UploadService.cs` session part: `CreateSessionAsync(tenant,project,fileName,contentType,declaredSize)` validates filename (no path traversal, max 256 chars), contentType allowlist (video/mp4,video/quicktime,video/x-matroska,audio/wav,audio/mpeg,audio/flac,audio/x-wav), declaredSize 1..MediaOptions.MaxUploadBytes else 400; creates UploadSession Status Created→InProgress, StorageKey `{tenant}/{project}/{uplId}/{fileName}`, S3 CreateMultipartUpload, returns `{uploadId: upl_..., multipartUploadId, partSize: 8MB, expiresAt}`. `GetPartUrlsAsync(uploadId, partNumbers[])` returns presigned upload URLs 15min each. `GetStatusAsync` queries S3 ListParts (authoritative) + DB reconcile, returns `{completedParts[], missingParts[], status}`. `CompleteAsync` validates required parts present else 400 UPLOAD_INCOMPLETE, CompleteMultipart, mark Completed, publish MediaUploaded. `AbortAsync` aborts multipart + mark Aborted.
3. Controllers: implement exact routes with methods:
   - `POST /api/v1/projects` 201 `{id: prj_..., status: Created}`; errors 400 VALIDATION_FAILED,401,403. Idempotency-Key required, 7d retention.
   - `GET /api/v1/projects?page=&pageSize=` 200 paginated.
   - `GET /api/v1/projects/{projectId}` 200 full DTO; 404 NOT_FOUND.
   - `DELETE /api/v1/projects/{projectId}` 202 + audit.
   - Upload routes as listed: `POST .../uploads` 201 `{uploadId: upl_..., partSize, multipartUploadId}`; `GET .../uploads/{uploadId}` 200 status; `POST .../uploads/{uploadId}/parts` body `{partNumbers:[1,2]}` 200 `{urls:{1:url}}`; `POST .../complete` 200; `POST .../abort` 200.
   - Processing/segments/reviews/exports/output routes: implement thin pass-through returning 501 only if downstream service missing? Decision: implement full routing + auth + DTO validation now, with service calls stubbed to return 409/404 appropriately (e.g., processing start validates media-ready else 409). Do not leave 501; return structured errors.
   Request/response examples: POST /projects `{sourceLanguage:en,targetLanguage:es,settings:{}}` → 201 `{id:prj_abc,status:Created}`.
4. Validation via FluentValidation validators per DTO; auth attributes per RoleMatrix; idempotency filter on POST/DELETE; audit on create/delete/processing-start/cancel/retry/export; correlation header echoed.
5. Public ID serialization: expose `prj_/upl_` strings via PublicIdMapper; accept both raw Guid and prefixed (parse both).

## Requirements

- R1: All 32 routes exist with exact methods/paths.
- R2: Status codes + error codes correct per route.
- R3: Upload part URLs 15min; S3 authoritative for parts.
- R4: Idempotency on mutations; pagination on lists; signed URLs for downloads.
- R5: Audit on privileged ops.

## Edge Cases and Error Handling

- Invalid language → 400 VALIDATION_FAILED.
- Oversize upload → 400 + QUOTA_EXCEEDED if storage quota (check via QuotaService stub allow + size check).
- Incomplete complete → 400 UPLOAD_INCOMPLETE.
- Second processing start with active run → 409 CONFLICT.
- Path traversal filename → 400.

## Security and Safety Requirements

- JWT + ownership on every route; filename sanitized; content-type not trusted (sniffing later); no secrets in responses.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Api/ProjectsUploadsApiTests.cs`: `Create_Project_201`, `Create_Invalid_Language_400`, `Upload_Create_Part_Complete_Flow`, `Incomplete_Complete_400`, `Cross_Tenant_403`, `Idempotent_Create_Same_Response`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ProjectsUploadsApiTests
```

## Completion Criteria

- All routes implemented with correct contracts; tests pass.

## Traceability

- Plan Section 7 actions 13–20 + Section 8 actions 1–8; Assumptions 16; Functional checklist uploads/query/resume.
