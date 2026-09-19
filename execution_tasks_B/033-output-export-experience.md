# Task 033 — Output and Export Experience

## Goal
Implement the output readiness page and export request flow with honest partial states and signed-URL downloads.

## Context
Delivery surface for finished dubs; output/export endpoints from Task 012 (per-item status, export generation, short-lived download URLs), progress events from Task 026, mounted in the Task 018 project tabs. Partial availability (e.g. 96/100) is a first-class state, not an error.

## Starting State
Task 012 (outputs/exports/notifications API) done. No `features/exports`. Depends on Task 012.

## Scope
Included: output page per-item states, `ExportCard` request dialog (type/scope/run/format), export generation state display, short-lived signed-URL downloads, no-internal-paths rule.
Excluded: export pipeline execution (backend), QC scoring (Task 032), notification center (Task 034).

## Instructions
1. Create `frontend/src/features/exports/OutputsPage.tsx`: per-output-item states — ready / generating / failed / partial / unavailable — fed by `useOutputs(projectId)` on `queryKeys.outputs`; partial items show explicit explanation (e.g. "96/100 segments — 4 failed QC, see Quality") with deep link to Task 032; unavailable items show the reason, never a bare disabled button.
2. Create `frontend/src/features/exports/ExportCard.tsx`: request dialog with type (audio/video/subtitles/manifest), scope (full/segment-range/per-speaker where advertised), run selector, format selector (options from backend allowlist only); submit → Task 012 export endpoint; success invalidates `queryKeys.exports` (+ Task 026 completion events).
3. Implement generation-state display (`frontend/src/features/exports/ExportRow.tsx`): queued/generating/ready/failed per export with approximate progress where backend provides it (no client ETA computation); failed shows backend reason + retry action only where `actions[]` advertises it.
4. Implement downloads: ready exports download via short-lived signed URL in a plain `<a download>`; URL fetched at click time (never pre-fetched/cached), expiry → single refetch; display file size/format where provided.
5. Enforce no-internal-paths rule: no server paths, bucket names, or internal URIs rendered anywhere in outputs/exports UI (grep-gate test for `s3://|/mnt/|/var/|C:\\|bucket` in rendered output); errors show backend `message` only.

## Requirements
- R1: All five item states (ready/generating/failed/partial/unavailable) render distinctly with reasons.
- R2: Partial states quantify availability (e.g. 96/100) with an explanation + QC deep link.
- R3: Export dialog offers only backend-allowlisted types/scopes/formats (test asserts no hardcoded options).
- R4: Downloads use click-time signed URLs; expired URLs refetched once, never cached.
- R5: No internal paths leak into UI or errors (grep-gate test).

## Edge Cases and Error Handling
- Export 409 (already generating) → show existing generation row + toast, no duplicate request.
- Download URL expired before click completes → single refetch + resume; double-expiry → error with retry.
- Format allowlist empty (backend misconfig) → dialog shows `EmptyState` with support hint, submit hidden.
- Output deleted server-side → row removed on refetch + toast.

## Security and Safety Requirements
- Signed download URLs never logged, never stored in state beyond the click handler, never shared into filter URLs.
- Export parameters validated against the backend allowlist before submit; server remains authoritative.

## Testing
- Create `frontend/src/features/exports/__tests__/exports.test.tsx`: five-state rendering, partial explanation content, allowlist-only dialog options, click-time URL fetch + expiry refetch, no-internal-paths scan.
- Playwright `@exports`: outputs page states render, export dialog submits, ready export downloads via signed URL.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/exports`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/exports
npx playwright test --grep="@exports"
```

## Completion Criteria
- Outputs show honest per-item states with partial explanations and allowlisted export requests downloading via short-lived URLs; exports tests + `@exports` E2E pass.

## Traceability
- Plan B §12.15. Depends on Task 012.
