# Task 023 — Resumable Upload UX

## Goal
Implement multipart resumable upload with pause/resume/recovery and clear validation-state distinction.

## Context
Media ingest for new projects (embedded in Task 022 wizard, reusable standalone); uploads use presigned part URLs against Plan A storage. Must survive network drops and page refreshes without re-uploading completed parts, and must distinguish client upload states from server validation states.

## Starting State
Task 018 (shell, primitives, telemetry) done. No `features/uploads`. Depends on Task 018.

## Scope
Included: `MediaUploader`/`UploadProgress`, presigned-part orchestration, pause/resume/cancel/retry-part, network/refresh recovery, local metadata persistence, uploading→ready/rejected state display with reason-specific guidance.
Excluded: server-side media validation/analysis (Plan A), processing start (Task 024).

## Instructions
1. Create `frontend/src/features/uploads/`: `MediaUploader.tsx` (drag-drop + file picker), `UploadProgress.tsx` (per-part progress bar/ring), `uploadStore.ts` (zustand + `localStorage` metadata persistence), `useResumableUpload.ts` (presigned-part PUTs, concurrency cap 3, per-part checksums where backend requires), `fingerprint.ts` (client content hash for duplicate detection).
2. Controls: pause (stops all in-flight parts), resume, cancel, retry-part; presigned URL per part; expired presigned URL mid-upload → refresh URLs and continue without losing completed parts.
3. Recovery: on mount, resume from persisted metadata (`projectId, uploadId, name, size, fingerprint, language, status`) by re-listing uploaded parts server-side; network drop → auto-pause with resume prompt; page refresh → resume prompt with completed-parts intact.
4. No media bytes in state: store/`localStorage` hold only metadata + progress numbers; `File`/`Blob` references kept in a module-scoped ref, never in zustand/`localStorage` (test-asserted).
5. Server states distinguished in UI: `uploading → uploaded → server-validation → analysis → ready | rejected`; `rejected` maps reason → guidance: `duplicate` (+ "use existing" link), `unsupported` (+ accepted formats), `corrupt` (+ re-export/retry advice).

## Requirements
- R1: Refresh resumes without re-uploading completed parts (part-list reconciliation).
- R2: Pause stops all in-flight part requests; resume continues from next pending part.
- R3: `localStorage` holds metadata only — no `Blob`/base64 (assertion test).
- R4: Every `rejected` reason maps to specific user guidance + next action.
- R5: Part-upload concurrency capped at 3.

## Edge Cases and Error Handling
- File changed on disk between sessions (fingerprint mismatch) → restart upload with notice.
- Zero-byte file → client-side rejection before any request.
- Tab closed during server validation → status re-polled on return (no stuck spinner).
- Cancel → server abort call + local metadata cleanup + confirm dialog.

## Security and Safety Requirements
- Presigned URLs used as-is, never logged or sent to telemetry.
- File-picker `accept` hints are UX-only; server validation authoritative.
- Uploads only to tenant-scoped URLs returned for the current project.

## Testing
- Create `frontend/src/features/uploads/__tests__/`: recovery-from-metadata resume, pause/resume sequencing, no-bytes-in-state assertion, state-machine transitions, reason→guidance mapping (MSW for part URLs + status polling).
- Playwright `@upload`: picker upload, pause/resume, refresh recovery, rejected-reason display.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/uploads`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/uploads
npx playwright test --grep="@upload"
```

## Completion Criteria
- Resumable upload with pause/resume/cancel/retry-part and refresh recovery works; metadata-only persistence verified; validation states with reason guidance render; uploads tests + `@upload` E2E pass.

## Traceability
- Plan B §10.6, §12.5. Depends on Task 018.
