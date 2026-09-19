


<!-- ===== FILE: TASK_MAP.md ===== -->


# Task Map — Plan B (Frontend, Product UX, Read Models, Backend Extensions, Hardening)

> Source: `implementation_plan-B.md`. Plan A is assumed implemented; no Plan A work is re-done. All tasks preserve Plan B architecture decisions (§6) and binding principles (§4).

## Phase 1 — Backend Extension Foundation

### Task 001 — Identity, preferences, project metadata, membership
Goal: Add TenantUser, UserPreference, DubbingProject extensions, and ProjectMembership with migrations and RLS.
Dependencies: None (requires Plan A schema).
Plan sections: §8.1, §8.2, §8.9, §20.
Deliverables: Entities `TenantUser (usr_)`, `UserPreference`, `DubbingProject` extensions, `ProjectMembership (mbr_)` + roles, EF configs, expand/contract migration, RLS, indexes.
Validation: `dotnet build`; `dotnet ef migrations script`; `dotnet test --filter FullyQualifiedName~IdentityPreferencesTests`.
Complexity: M
Required/Optional: Required

### Task 002 — Notifications and activity projection
Goal: Add durable Notification and ActivityEvent entities with outbox-driven projection.
Dependencies: 001.
Plan sections: §8.3, §8.4, §8.9, §9.9, §20.
Deliverables: `Notification (ntf_)` + 9 types + dedup constraint, `ActivityEvent (act_)` append-only, projector, indexes, RLS, expiry.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~NotificationActivityTests`.
Complexity: M
Required/Optional: Required

### Task 003 — Segment selection concurrency and version-aware editing backend
Goal: Add explicit selection state and version-aware transcript/translation mutation semantics.
Dependencies: 001.
Plan sections: §8.5, §8.9, §9.5.
Deliverables: Selection projection + version counter, expected-version checks, 409 path, invalidation + audit hooks.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~SelectionConcurrencyTests`.
Complexity: M
Required/Optional: Required

### Task 004 — Voice preview jobs and media preview artifacts
Goal: Add VoicePreviewJob lifecycle and preview artifact types for UI performance.
Dependencies: 001.
Plan sections: §8.6, §8.7, §8.9, §9.6.
Deliverables: `VoicePreviewJob (vpv_)` state machine, quota/consent gates, `MediaPreviewAudio`, `WaveformPeaks`, `VideoPreview`, `VoicePreviewAudio`, `QcEvidenceArtifact` hooks.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~PreviewArtifactTests`.
Complexity: M
Required/Optional: Required

### Task 005 — Provider health and diagnostics read support
Goal: Add read-only diagnostics aggregations for admin/operator UI.
Dependencies: 001.
Plan sections: §8.8, §8.9, §9.10.
Deliverables: Query services for provider health/routes/queue/DLQ/leases/orphans/backlog/worker health; elevated-authz hooks; secret-free DTOs.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~DiagnosticsReadTests`.
Complexity: S
Required/Optional: Required

## Phase 2 — API Read Models and Contracts

### Task 006 — Auth/session/me/preferences API
Goal: Expose login/refresh/logout, `/me`, and preferences endpoints.
Dependencies: 001.
Plan sections: §9.1, §12.1, §13.1–§13.3.
Deliverables: `POST /api/v1/auth/login|refresh|logout`, `GET /me`, `GET|PUT /me/preferences`; permission strings; OpenAPI updates.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~MePreferencesTests`.
Complexity: S
Required/Optional: Required

### Task 007 — Dashboard and projects API
Goal: Expose dashboard summary and project CRUD/archive endpoints.
Dependencies: 001.
Plan sections: §9.2, §12.2–§12.4.
Deliverables: `GET /dashboard/summary`, `GET|POST /projects`, `GET|PATCH /projects/{id}`, `POST .../archive|unarchive`, `DELETE`.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~ProjectsApiTests`.
Complexity: M
Required/Optional: Required

### Task 008 — Processing, workspace, progress, SSE endpoint shell
Goal: Expose processing lifecycle, workspace aggregate, progress, and SSE stream endpoints.
Dependencies: 001.
Plan sections: §9.4, §9.11, §12.6–§12.8.
Deliverables: `POST|GET .../processing`, `POST .../cancel|retry`, `GET .../progress|stream|workspace|activity|output|quality`; workspace DTO; idempotency + authz.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~WorkspaceProgressTests`.
Complexity: M
Required/Optional: Required

### Task 009 — Segments, transcript, translation editing API
Goal: Expose segment list/detail, selection, manual-edit, and retry endpoints.
Dependencies: 003.
Plan sections: §9.5, §12.9–§12.10.
Deliverables: `GET .../segments[/{id}]`, `POST .../retry|transcript-selection|translation-selection|transcript-edits|translation-edits`.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~SegmentEditingApiTests`.
Complexity: M
Required/Optional: Required

### Task 010 — Speakers, voice assignment, voice preview API
Goal: Expose speaker list/detail, compatible voices, assignment, and preview endpoints.
Dependencies: 004.
Plan sections: §9.6, §12.11.
Deliverables: `GET .../speakers[/{id}|/{id}/available-voices]`, `PUT .../voice-assignment`, `POST|GET .../voice-previews[/{id}]`.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~VoiceApiTests`.
Complexity: M
Required/Optional: Required

### Task 011 — Reviews and review-context API
Goal: Expose review-context read model and harden review mutations.
Dependencies: 003.
Plan sections: §9.7, §12.13.
Deliverables: `GET /api/v1/reviews/{id}/context`; idempotency + expected-version + reason + audit; `ResolvedWithEdit`.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~ReviewContextTests`.
Complexity: M
Required/Optional: Required

### Task 012 — Output, exports, notifications API
Goal: Expose output summary, export lifecycle passthrough, and notification endpoints.
Dependencies: 002, 004.
Plan sections: §9.8, §9.9, §12.15–§12.16.
Deliverables: `GET .../output` + partial/completeness, export wiring (signed URLs), `GET /notifications|unread-count`, `POST .../read|read-all`.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~OutputNotificationsApiTests`.
Complexity: S
Required/Optional: Required

### Task 013 — Admin/diagnostics API, SSE contract freeze, error mapping
Goal: Freeze SSE envelope/event types, admin read endpoints, and backend error envelope.
Dependencies: 005, 008.
Plan sections: §9.10, §9.11, §9.12, §12.19.
Deliverables: `GET /admin/...|diagnostics/...`; SSE envelope + 14 event types + payload allowlist; error `{code,message,correlationId,details}`.
Validation: `dotnet build`; `dotnet test --filter FullyQualifiedName~AdminSseErrorContractTests`.
Complexity: M
Required/Optional: Required

### Task 014 — OpenAPI authority and TypeScript client generation
Goal: Make OpenAPI authoritative with generated TS client and CI drift check.
Dependencies: 006–013.
Plan sections: §10.4, §16.3.
Deliverables: Versioned OpenAPI, `make generate-api`, generated `frontend/src/api/generated/`, CI drift-fail.
Validation: `make generate-api`; `npm run build`; `dotnet build`.
Complexity: S
Required/Optional: Required

## Phase 3 — Frontend Foundation

### Task 015 — Frontend scaffolding, routing, providers
Goal: Create standalone `frontend/` app with strict TS, Vite, Router, Query, store, styling baseline.
Dependencies: 014.
Plan sections: §10.1, §10.2, §22 (B-2).
Deliverables: `frontend/` structure, `App.tsx`, `router.tsx`, providers/layouts, code-splitting, env config.
Validation: `npm run typecheck`; `npm run lint`; `npm run build`.
Complexity: M
Required/Optional: Required

### Task 016 — Design tokens and core UI primitives
Goal: Implement token system and domain-agnostic component library.
Dependencies: 015.
Plan sections: §11.2, §11.3.
Deliverables: `tokens.css`/`globals.css`, primitives (Button/Input/Modal/Table/Badge/Progress/Skeleton/Toast/Empty/ErrorState/Dialog/Card, etc.).
Validation: `npm run typecheck`; `npm run test -- src/components`; Storybook build.
Complexity: M
Required/Optional: Required

### Task 017 — API client, query keys, error normalization
Goal: Wire generated client, query-key factory, auth headers, correlation IDs, error mapping.
Dependencies: 014, 015.
Plan sections: §10.3, §10.4, §9.12.
Deliverables: `api/client/`, `api/queryKeys/`, `api/errors/`, idempotency-key support, GET-only retry.
Validation: `npm run typecheck`; `npm run test -- src/api`; `make generate-api` drift check.
Complexity: S
Required/Optional: Required

### Task 018 — App shell, navigation, i18n, telemetry baseline
Goal: Implement shell, IA navigation, locale readiness, and safe telemetry harness.
Dependencies: 015–017.
Plan sections: §10.9, §11.1, §11.4–§11.5, §14.2–§14.4.
Deliverables: Layouts + project tabs, i18n keys/plural/RTL, telemetry + correlation propagation.
Validation: `npm run typecheck`; `npm run test`; `npx playwright test --grep="@shell"`.
Complexity: M
Required/Optional: Required

## Phase 4 — Core Project Workflow

### Task 019 — Authentication and session UX
Goal: Implement login/logout/restore/expiry with pre-shell `/me` resolution.
Dependencies: 006, 018.
Plan sections: §12.1, §13.3.
Deliverables: Login, silent refresh, expiry → login, 401/403 states; in-memory token.
Validation: `npm run test -- src/features/auth`; `npx playwright test --grep="@auth"`.
Complexity: S
Required/Optional: Required

### Task 020 — Dashboard
Goal: Implement dashboard summary with drill-down.
Dependencies: 007, 018.
Plan sections: §12.2.
Deliverables: Counts, recent outputs, storage/cost, quota, warnings, backlog; loading/empty/partial/error states.
Validation: `npm run test -- src/features/dashboard`; `npx playwright test --grep="@dashboard"`.
Complexity: S
Required/Optional: Required

### Task 021 — Project list
Goal: Implement filterable, paginated project list with valid-actions-only UX.
Dependencies: 007, 018.
Plan sections: §12.3.
Deliverables: Columns, filters, sort/pagination, open/cancel/retry/export/delete/archive actions.
Validation: `npm run test -- src/features/projects`; `npx playwright test --grep="@projects"`.
Complexity: S
Required/Optional: Required

### Task 022 — Project creation wizard
Goal: Implement creation wizard with progressive disclosure and immutable-language rule.
Dependencies: 007, 018.
Plan sections: §12.4.
Deliverables: Steps Create→Language→Settings→Upload→Review→Start; validation; config-hash transparency.
Validation: `npm run test -- src/features/projects`; `npx playwright test --grep="@project-create"`.
Complexity: M
Required/Optional: Required

## Phase 5 — Upload and Media Validation UX

### Task 023 — Resumable upload client and validation UX
Goal: Implement multipart resumable upload with validation-state distinction.
Dependencies: 018.
Plan sections: §10.6, §12.5.
Deliverables: `MediaUploader`/`UploadProgress`; pause/resume/cancel/retry-part/recovery; states uploading→ready/rejected; duplicate feedback.
Validation: `npm run test -- src/features/uploads`; `npx playwright test --grep="@upload"`.
Complexity: M
Required/Optional: Required

## Phase 6 — Processing Progress Workspace

### Task 024 — Processing start and preflight
Goal: Implement cost/quota/consent preflight with explicit confirmation.
Dependencies: 008, 018.
Plan sections: §12.6, §12.18.
Deliverables: Preflight dialog, costly-op confirmation, conflict disabling, 202 → workspace nav.
Validation: `npm run test -- src/features/processing`; `npx playwright test --grep="@processing-start"`.
Complexity: S
Required/Optional: Required

### Task 025 — Project workspace shell
Goal: Implement aggregated workspace using workspace read model (no N+1).
Dependencies: 008, 018.
Plan sections: §12.7.
Deliverables: Header, PipelineStepper/StageProgress, secondary media/config/run/cost/activity panels.
Validation: `npm run test -- src/features/processing`; `npx playwright test --grep="@workspace"`.
Complexity: M
Required/Optional: Required

### Task 026 — Live progress via SSE with polling fallback
Goal: Implement authenticated streaming progress client as invalidation hints.
Dependencies: 008, 013, 025.
Plan sections: §10.5, §12.8, §9.11.
Deliverables: `useProgressStream` (backoff/cursor/dup-tolerance/invalidation), adaptive polling, approximate-% only.
Validation: `npm run test -- src/hooks/useProgressStream`; `npx playwright test --grep="@progress"`.
Complexity: M
Required/Optional: Required

## Phase 7 — Transcript and Translation Editing

### Task 027 — Transcript workspace
Goal: Implement player+waveform/list/inspector transcript review with version-safe edits.
Dependencies: 009, 026.
Plan sections: §12.9.
Deliverables: `TranscriptEditor`, `SegmentRow`, seek-on-select, highlight+autoscroll, virtualized list.
Validation: `npm run test -- src/features/transcript`; `npx playwright test --grep="@transcript"`.
Complexity: M
Required/Optional: Required

### Task 028 — Translation workspace
Goal: Implement side-by-side translation review with immutable candidates.
Dependencies: 009, 027.
Plan sections: §12.10.
Deliverables: `TranslationEditor`, select-or-edit-as-manual, navigate-away guard, conflict → refresh.
Validation: `npm run test -- src/features/translation`; `npx playwright test --grep="@translation"`.
Complexity: M
Required/Optional: Required

## Phase 8 — Voice Assignment and Preview

### Task 029 — Voice assignment and preview
Goal: Implement speaker list, compatible-voice selection, and durable preview playback.
Dependencies: 010, 018.
Plan sections: §12.11.
Deliverables: Speaker list, `VoiceSelector`, compatible-only display, impact pre-confirm, consent states.
Validation: `npm run test -- src/features/voices`; `npx playwright test --grep="@voices"`.
Complexity: M
Required/Optional: Required

## Phase 9 — Timeline and Media Inspection

### Task 030 — Media player, waveform, timeline inspection
Goal: Implement signed-URL playback, peak-based waveform, and read-only timeline lanes.
Dependencies: 004, 012, 018.
Plan sections: §10.7, §12.12.
Deliverables: `MediaPlayer`, canvas `Waveform`, `Timeline` lanes + zoom/pan/select/seek/issue-jump.
Validation: `npm run test -- src/features/timeline`; `npx playwright test --grep="@timeline"`.
Complexity: L
Required/Optional: Required

## Phase 10 — Review Studio

### Task 031 — Manual review studio
Goal: Implement review queue plus single-screen resolution context.
Dependencies: 011, 027–029.
Plan sections: §12.13.
Deliverables: `ReviewQueue` + filters, `ReviewCard` detail, idempotent resolve with version guard.
Validation: `npm run test -- src/features/review`; `npx playwright test --grep="@review"`.
Complexity: M
Required/Optional: Required

## Phase 11 — QC, Output, Exports

### Task 032 — Quality control interface
Goal: Implement QC summary and evidence-linked issue list.
Dependencies: 008, 018.
Plan sections: §12.14.
Deliverables: `QualitySummary`, `QualityIssue` + evidence links, blocking prominence.
Validation: `npm run test -- src/features/quality`; `npx playwright test --grep="@quality"`.
Complexity: S
Required/Optional: Required

### Task 033 — Output and export experience
Goal: Implement output readiness, partial-state explanation, and export creation/download.
Dependencies: 012, 018.
Plan sections: §12.15.
Deliverables: Output page (ready/generating/failed/partial/unavailable), `ExportCard` dialog, signed-URL downloads.
Validation: `npm run test -- src/features/exports`; `npx playwright test --grep="@exports"`.
Complexity: M
Required/Optional: Required

## Phase 12 — Notifications, Activity, Preferences

### Task 034 — Notifications center
Goal: Implement durable notification list, unread count, and deep links.
Dependencies: 012, 018, 026.
Plan sections: §12.16.
Deliverables: Header icon + Notification Center, mark-read/read-all, SSE invalidation, prefs hook.
Validation: `npm run test -- src/features/notifications`; `npx playwright test --grep="@notifications"`.
Complexity: S
Required/Optional: Required

### Task 035 — Activity feed, cost/quota visibility, user preferences
Goal: Implement activity timeline, cost summary, and settings/preferences screens.
Dependencies: 006–008, 018.
Plan sections: §12.17, §12.18, §8.1.
Deliverables: `AuditTimeline`, `CostSummary`, Settings (locale/timezone/theme/filters/zoom/notif prefs).
Validation: `npm run test -- src/features/settings`; `npx playwright test --grep="@activity"`.
Complexity: S
Required/Optional: Required

## Phase 13 — Admin and Diagnostics

### Task 036 — Admin, usage, provider health, diagnostics UI
Goal: Implement role-gated admin and operator diagnostics.
Dependencies: 005, 013, 018.
Plan sections: §12.19, §19.3.
Deliverables: Admin + ops dashboard, elevated gating, no secrets, confirm+reason+audit on destructive.
Validation: `npm run test -- src/features/admin`; `dotnet test --filter FullyQualifiedName~AdminAuthzTests`; `npx playwright test --grep="@admin"`.
Complexity: M
Required/Optional: Required

## Phase 14 — Security and Privacy Hardening

### Task 037 — Tenant isolation, signed URLs, CORS/CSP, consent, secrets hygiene
Goal: Harden authz, isolation, transport, and consent enforcement end-to-end.
Dependencies: 006–013, 019, 029, 030, 033.
Plan sections: §13.1–§13.8, §23.4.
Deliverables: Per-endpoint checks, tenant-scoped keys/URLs/telemetry, 15-min URLs, CORS/CSP, consent gate, negative tests.
Validation: `dotnet test --filter FullyQualifiedName~TenantIsolationTests`; `dotnet test --filter FullyQualifiedName~ConsentTests`.
Complexity: M
Required/Optional: Required

## Phase 15 — Observability

### Task 038 — Backend metrics, frontend telemetry, analytics, correlation
Goal: Wire product-grade observability without leaking sensitive content.
Dependencies: 018, 026.
Plan sections: §14.1–§14.4, §18.1–§18.2.
Deliverables: SSE/notification/read-model/upload/review/export latencies; safe FE telemetry; allowlisted analytics + opt-out; E2E correlation.
Validation: `dotnet test --filter FullyQualifiedName~ObservabilityTests`; `npm run test -- src/telemetry`.
Complexity: S
Required/Optional: Required

## Phase 16 — Testing and E2E

### Task 039 — Unit, component, API-mock tests
Goal: Cover pure logic, components, and error/edge API states.
Dependencies: 015–036.
Plan sections: §15.1–§15.3.
Deliverables: Unit + component + MSW (success/401/403/404/409/429/500/validation/provider/partial/stale-conflict).
Validation: `dotnet test --filter FullyQualifiedName~UnitTests`; `npm run test`; `npm run test -- --coverage`.
Complexity: M
Required/Optional: Required

### Task 040 — Backend integration and cross-layer tests
Goal: Verify new endpoints and frontend→backend→infra flows.
Dependencies: 006–013, 039.
Plan sections: §15.4–§15.5.
Deliverables: Integration + cross-layer (API→PG, upload→storage, start→SSE, review→mutation, export→download, voice→invalidation, stale→refresh).
Validation: `dotnet test --filter FullyQualifiedName~IntegrationTests`; `npx playwright test --grep="@cross-layer"`.
Complexity: M
Required/Optional: Required

### Task 041 — Playwright E2E, full smoke, visual, a11y, performance
Goal: Prove the §24 end-to-end journey plus non-functional gates.
Dependencies: 019–036.
Plan sections: §15.6–§15.10, §24.
Deliverables: Playwright scenarios; full smoke (real FE+API+PG+storage+transport, mock AI); visual × breakpoints/themes/LTR-RTL; WCAG 2.2 AA; perf budgets.
Validation: `npx playwright test`; `npm run test:a11y`; `npm run test:visual`.
Complexity: L
Required/Optional: Required

## Phase 17 — CI/CD and Deployment

### Task 042 — CI pipelines and contract management
Goal: Gate backend extensions and frontend on build/test/scan/contract checks.
Dependencies: 014, 039–040.
Plan sections: §16.1–§16.3.
Deliverables: Backend CI + frontend CI + breaking-change detection.
Validation: CI green on PR; `bash deploy/verify.sh`.
Complexity: M
Required/Optional: Required

### Task 043 — Frontend hosting, env config, rollout, operations
Goal: Ship static frontend + expand/contract backend rollout with ops readiness.
Dependencies: 042.
Plan sections: §17.1–§17.4, §18.1–§18.4, §23.5.
Deliverables: CDN/static host, `VITE_*` injection, migration-job rollout + compat + flags, rollback, runbooks, backup coverage.
Validation: `npm run build`; `docker build -f Dockerfile.frontend .`; `kubectl apply --dry-run=client -f deploy/k8s/`.
Complexity: M
Required/Optional: Required

## Phase 18 — Optional Enrichment / Local AI UX

### Task 044 — Optional video intelligence, lip-sync, local GPU UX
Goal: Expose optional enrichment surfaces without blocking core completion.
Dependencies: 036, 043.
Plan sections: §19.1–§19.3.
Deliverables: Flag-gated UI (video-intel, lip-sync score + separate asset, local provider health for operators).
Validation: `npm run test -- src/features/admin`; `npx playwright test --grep="@optional-enrichment"`.
Complexity: S
Required/Optional: Optional

---

## Addendum — Review Fixes (E2E Ownership, New Prerequisites, Splits, Dependency Corrections)

### A. E2E ownership fix
- Feature-level E2E specs (`e2e/features/<name>.spec.ts`, tags `@auth/@dashboard/@projects/@project-create/@upload/@processing-start/@workspace/@progress/@transcript/@translation/@voices/@timeline/@review/@quality/@exports/@notifications/@activity/@settings/@admin`) belong to Tasks 019–036. Task 041 (combined, superseded) no longer owns them; 041A owns journeys/smoke only.

### B. New prerequisite tasks (Required)
- **Task 045 — i18n/RTL foundation** (`045-i18n-rtl-foundation.md`, S, Required): translation keys, locale/timezone/plural helpers, RTL + pseudo-locale test, extraction gate, default-locale-from-preference. Deps 015/016. Unblocks 018/019–036/041B. Plan §10.9/§11.5/§15.8.
- **Task 046 — test harness and fixtures** (`046-test-harness-fixtures.md`, M, Required): Vitest/MSW/Playwright config + tags, handler taxonomy (11 states), synthetic tenants/users/projects/runs/segments, reset/SSE/quarantine, PII scrubber, `docs/test-ownership.md`. Deps 014/015. Unblocks all feature verification; converts 039–041 to gap closure.
- **Task 042A — basic CI early gate** (`042A-basic-ci.md`, S, Required): per-PR typecheck/lint/unit/build, fail-closed, Testcontainers excluded by tag, advisory audit. Deps 015/046. Existing 042 keeps full gates as 042B scope.

### C. Splits (originals marked SUPERSEDED, new files carry the work)
- **012 → 012A** Output/Export API (M, Req; deps 002/004/008; §9.8/§12.15) **+ 012B** Notifications API (S, Req; dep 002; §8.3/§9.9/§12.16).
- **035 → 035A** Activity/Cost UI (S, Req; deps 002/007–008/018; §12.17–12.18) **+ 035B** Settings/Preferences UI (S, Req; deps 006/018/045; §8.1/§9.1).
- **039 → 039A** Infra/coverage + conformance + gap report (S, Req; dep 046) **+ 039B** Frontend state-matrix closure (M, Req; deps 046/039A) **+ 039C** Backend unit closure (M, Req; deps 046/039A). Feature specs stay in 006–036.
- **040 → 040A** Cross-layer harness/rig smoke (S, Req; deps 046/006–013/014) **+ 040B** Seven named seam specs (M, Req; dep 040A; §15.4–15.5). Endpoint integration stays in 006–013.
- **041 → 041A** Journeys/smoke (M, Req; deps 019–036/040A/046; §15.6–15.7/§24) **+ 041B** Visual matrix (M, Req; deps 019–036/045/046; §11.5/§15.8) **+ 041C** A11y audit (M, Req; deps 016/019–036/046; §10.10/§15.9) **+ 041D** Perf budgets (M, Req; deps 015/017/025/027/030/046; §10.11/§15.10).
- **043 → 043A** Hosting/env/CDN (M, Req; deps 015/042A/045; §17.1–17.2) **+ 043B** Rollout/rollback (M, Req; deps 001–005/042/043A; §2.3/§6.9/§17.3) **+ 043C** Runbooks/backup drills (S, Req; deps 038/043A/043B; §18.1–18.4).

### D. Dependency/ordering corrections (appended to files)
- **016:** after 015 only; 014 NOT required. **028:** parallel with 027 (dep 009 only). **030:** deps 004/008/009; 012A optional. **037:** completion gate (negative tests/headers/secret scan/revocation proofs; baseline in feature tasks). **038:** completion gate (validates taxonomy/scrubbing/allowlist; bootstrap in 013/017 + backend tasks). **042:** now 042B full-gates scope.
- Corrected chains: `001 → 003 → 009 → 027`, `001 → 003 → 009 → 028` (parallel), `001 → 004 → 010 → 029`, `006–013 → 014 → 017 → 018 → 019–036`, `015 → 016 → 018`, `046 → 019–036 → 039–041 gap closure`, `042A basic CI → feature work → 042B full gates`.

### E. Revised execution order
- 0: 001. 1: 002/003/004/005/006 (parallel after 001). 2: 007/008/009/010/011/012A/012B/013. 3: 014. 4: 015/016/017/045/046/042A (016 beside 017 after 015). 5: 018. 6: 019–026 (with own feature E2E). 7: 027 + 028 (parallel) / 029 / 030. 8: 031/032/033/034/035A/035B/036 (with own feature E2E). 9: 037/038 (gates). 10: 039A → 039B/039C, 040A → 040B. 11: 041A/B/C/D. 12: 042B/043A/043B/043C. 13: 044 (Optional).

### F. Missing-scope closure — Tasks 047/048/049 (Required, no unrelated scope)
- **Task 047 — backup policy execution and restore verification** (`047-backup-policy-restore-verification.md`, S, Required): backup jobs/config for all seven §18.4 groups, restore verification steps, recorded drill, RPO/RTO + escalation, release gate (gaps block release). Deps 001/002/004/043C. Extends 043C (runbooks + baseline drill stay in 043C); Plan §18.4.
- **Task 048 — frontend feature-flag evaluation hook** (`048-frontend-feature-flag-hook.md`, S, Required): `useFeatureFlag` (or equivalent) from `/me` or bootstrap config, fail-closed (missing → `false`), rollout-only (never authorization), wired into admin-gated/enrichment-gated UI. Deps 006/018/036. Plan §6.9, §12.19, §19 (flag source §9.1/§12.1).
- **Task 049 — outbox channel abstraction** (`049-notification-channel-abstraction.md`, S, Required): channel-publisher seam with in-app as sole active channel; future email/webhook addable without changing projection semantics; no external channel implemented. Dep 002. Plan §8.3.1.
- Dependency/ordering delta (existing files untouched; consumer-only notes): `002 → 049 → 012B/034 (semantics unchanged)`; `006 + 018 + 036 → 048 → 044 (044 consumes hook, scope otherwise unchanged)`; `001/002/004 + 043C → 047 (terminal ops gate)`.
- Execution order delta: 049 runs with the Phase-1 backend batch (after 002, alongside 012B work); 048 runs after 036 and before 044; 047 runs after 043C as the final ops item before release. All other ordering per §E unchanged.
- Assumptions (not plan-derived): RPO/RTO values, backup job paths, escalation contacts (047); hook path, flag-key names, per-flag defaults — all `false` (048); publisher interface name/signature — invariant is persist-then-publish + `SourceEventId` dedup (049).



<!-- ===== FILE: 001-identity-preferences-membership.md ===== -->


# Task 001 — Identity, Preferences, Project Metadata, Membership

## Goal
Add TenantUser, UserPreference, DubbingProject extensions, and ProjectMembership with migrations and RLS.

## Context
Plan B needs user resolution for `/me`, persisted UI preferences, named/archivable projects with versioned processing settings, and project-level roles for authorization, notification recipients, and activity attribution. Plan A remains authoritative; this task only adds the smallest required product entities.

## Starting State
Plan A backend implemented: modular monolith API, EF Core + PostgreSQL 16, `DubbingProject` without product metadata, no `TenantUser`/`UserPreference`/`ProjectMembership`. Assumes `src/DubbingPlatform.Domain`, `Infrastructure`, `Api` exist.

## Scope
Included: entities, EF configurations, expand/contract migration, RLS policies, indexes, domain validation for settings JSON schemaVersion.
Excluded: endpoints (Tasks 006–007), notifications/activity (Task 002), selection concurrency (Task 003), frontend.

## Instructions
1. Create `src/DubbingPlatform.Domain/Entities/TenantUser.cs`: `Id` (Guid, public `usr_` prefix mapped in API), `TenantId`, `ExternalSubject`, `Email`, `DisplayName`, `Status` (Active/Disabled), `CreatedAt`, `UpdatedAt`. Unique `(TenantId, ExternalSubject)`.
2. Create `src/DubbingPlatform.Domain/Entities/UserPreference.cs`: `TenantId`, `UserId`, `Key`, `ValueJson`, `UpdatedAt`; PK `(TenantId, UserId, Key)`. Whitelist keys: `locale, timezone, theme, defaultProjectFilters, timelineZoom, notificationPreferences`; reject others with validation error; never store secrets.
3. Extend `src/DubbingPlatform.Domain/Entities/DubbingProject.cs` with `Name` (required, max 200), `Description` (max 2000, nullable), `OwnerUserId`, `CreatedByUserId`, `UpdatedByUserId`, `IsArchived`, `ArchivedAt`, `SettingsVersion`, `ProcessingSettingsJson` (versioned: `schemaVersion, sourceSeparationPolicy, outputProfile, timingStrictness, voicePolicy, reviewThreshold, glossary[{sourceTerm,targetTerm,notes}], styleInstructions`). Target language stays immutable (enforce in Task 007).
4. Create `src/DubbingPlatform.Domain/Entities/ProjectMembership.cs`: `Id` (`mbr_`), `TenantId`, `ProjectId`, `UserId`, `Role` (ProjectOwner/ProjectEditor/Reviewer/ProjectViewer), `GrantedByUserId`, `CreatedAt`. Unique `(ProjectId, UserId)`.
5. Add EF configs in `src/DubbingPlatform.Infrastructure/Persistence/Configurations/`: `TenantUserConfiguration.cs`, `UserPreferenceConfiguration.cs`, `ProjectMembershipConfiguration.cs`, extend `DubbingProjectConfiguration.cs`. snake_case naming, tenant indexes, RLS enabled (`ALTER TABLE ... ENABLE ROW LEVEL SECURITY`, tenant policy on `tenant_id`).
6. Add migration `dotnet ef migrations add AddProductIdentityExtensions --project src/DubbingPlatform.Infrastructure`; verify expand/contract: only additive nullable columns / new tables.
7. Add `ProjectProcessingSettingsValidator.cs` (FluentValidation) for settings JSON schemaVersion=1.

## Requirements
- R1: Unique `(TenantId, ExternalSubject)` enforced at DB.
- R2: Preference PK `(TenantId, UserId, Key)`; unknown keys rejected.
- R3: `IsArchived` does not alter processing semantics.
- R4: Settings changes allowed only with no conflicting active run (guard lives in Task 007; entity carries `SettingsVersion` + hash input).
- R5: RLS enabled on all three new tables; tenant indexes exist.
- R6: Migration applies cleanly on Plan A database (expand/contract compatible).

## Edge Cases and Error Handling
- Duplicate membership → 409 unique violation mapped to structured error.
- Unknown preference key → 400 validation error.
- Overlong name/description → 400, not truncation.
- Settings JSON with unknown schemaVersion → 400 with code `SETTINGS_VERSION_UNSUPPORTED`.

## Security and Safety Requirements
- All queries tenant-scoped; no cross-tenant user enumeration.
- No secrets in preferences; `ValueJson` size-capped (4KB) and logged only as key names.
- Archival/ownership changes audited (hook into existing AuditEvent).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Identity/IdentityPreferencesTests.cs`: unique subject, preference round-trip + unknown-key rejection, membership uniqueness, RLS blocks cross-tenant read, archival flag persistence.
- Type: integration (Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet ef migrations script --project src/DubbingPlatform.Infrastructure --no-build | Select-Object -First 50
dotnet test --filter FullyQualifiedName~IdentityPreferencesTests
```

## Completion Criteria
- Entities, configs, migration, RLS, and tests exist; `IdentityPreferencesTests` pass; migration script shows additive-only changes.

## Traceability
- Plan B §8.1, §8.2, §8.9, §20; API consumers §9.1–§9.2.



<!-- ===== FILE: 002-notifications-activity.md ===== -->


# Task 002 — Notifications and Activity Projection

## Goal
Add durable Notification and ActivityEvent entities with outbox-driven projection.

## Context
Notifications must survive reload and be delivered as a durable projection, not transient events; activity feed is the user-visible history while AuditEvent stays the security record. Both are required by the notification center, workspace, and admin surfaces.

## Starting State
Task 001 done (TenantUser exists). Plan A outbox/inbox and AuditEvent exist. No Notification/ActivityEvent tables or projectors.

## Scope
Included: entities, EF configs, migration + RLS, projector from backend events/outbox, deduplication, expiry.
Excluded: HTTP endpoints (Task 012), SSE payload (Task 013), frontend center (Task 034).

## Instructions
1. Create `src/DubbingPlatform.Domain/Entities/Notification.cs`: `Id` (`ntf_`), `TenantId`, `RecipientUserId`, `ProjectId?`, `Type` (ProcessingCompleted/Failed, ManualReviewRequired, ReviewResolved, ExportCompleted/Failed, UploadRejected, QuotaWarning, ProviderPolicyWarning), `Severity`, `Title` (max 200), `Body` (max 1000, summary only), `ResourceType`, `ResourceId`, `SourceEventId`, `ReadAt?`, `CreatedAt`, `ExpiresAt?`. Unique `(TenantId, RecipientUserId, SourceEventId)` where SourceEventId not null; index `(TenantId, RecipientUserId, ReadAt, CreatedAt)`.
2. Create `src/DubbingPlatform.Domain/Entities/ActivityEvent.cs`: `Id` (`act_`), `TenantId`, `ProjectId?`, `ProcessingRunId?`, `Type`, `ActorType`, `ActorUserId?`, `Summary`, `Severity`, `CorrelationId`, `OccurredAt`, `SchemaVersion=1`, `MetadataJson`. Index `(TenantId, ProjectId, OccurredAt)`; append-only (no update/delete repository methods).
3. Add configs + migration `AddNotificationsActivity`; enable RLS on both.
4. Create `src/DubbingPlatform.Application/Notifications/NotificationProjector.cs`: maps run/review/export/upload/quota events → notifications (recipient resolution via ProjectMembership + Owner); deduplicates on SourceEventId; never includes transcript bodies, signed URLs, or secrets.
5. Create `src/DubbingPlatform.Application/Activity/ActivityProjector.cs`: maps upload/start/translation/review/edit/export/complete actions → ActivityEvent; security-relevant actions also write AuditEvent.
6. Wire projectors to existing MassTransit consumers / domain-event handlers idempotently.

## Requirements
- R1: Duplicate source event does not create second notification.
- R2: Notifications survive API restart (persisted, not in-memory).
- R3: Activity is append-only and paginated by `(ProjectId, OccurredAt)`.
- R4: Bodies contain short human-readable summaries only.
- R5: RLS + tenant isolation on both tables.

## Edge Cases and Error Handling
- Missing recipient (no membership) → fall back to project owner; if none, skip + metric `notifications.skipped_total`.
- Null ProjectId allowed only for quota/policy types; others require ProjectId.
- Expired notifications excluded from list but retained until retention job.

## Security and Safety Requirements
- Tenant/user scoping on every read; cross-tenant access returns 404 (not 403 with existence leak) — endpoint behavior verified in Task 012.
- No sensitive content in Title/Body/MetadataJson (test asserts absence of tokens/URLs).

## Testing
- `tests/DubbingPlatform.IntegrationTests/Notifications/NotificationActivityTests.cs`: dedup, survival across restart (re-project same event), pagination order, cross-tenant isolation, no-secret assertion.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~NotificationActivityTests
```

## Completion Criteria
- Tables + projectors + tests exist; dedup, pagination, isolation verified.

## Traceability
- Plan B §8.3, §8.4, §8.9, §9.9, §20.



<!-- ===== FILE: 003-selection-concurrency-editing.md ===== -->


# Task 003 — Segment Selection Concurrency and Version-Aware Editing Backend

## Goal
Add explicit segment selection state with expected-version concurrency control for transcript/translation edits.

## Context
Transcript/translation versions are immutable; user edits create new manual versions and selection changes are explicit auditable operations. Without a selection version counter, concurrent editors silently lose updates. This task provides the backend invariant Tasks 009/027/028 depend on.

## Starting State
Plan A SpeechSegment + TranscriptVersion/TranslationVersion exist (immutable). Task 001 done. No selection counter or conflict path.

## Scope
Included: selection projection/counter, domain service, EF changes + migration, conflict errors, invalidation hook, audit.
Excluded: HTTP endpoints (Task 009), review-resolve-with-edit wiring (Task 011), frontend editors (Tasks 027–028).

## Instructions
1. Create or extend `src/DubbingPlatform.Domain/Entities/SegmentSelection.cs` (or extend `SpeechSegment`): `TenantId`, `ProjectId`, `SegmentId`, `SelectedTranscriptVersionId?`, `SelectedTranslationVersionId?`, `SelectedAudioArtifactId?`, `SelectionVersion` (int, starts 0), `UpdatedAt`, `UpdatedByUserId`. Unique `(TenantId, SegmentId)`.
2. Add `SegmentSelectionConfiguration.cs` + migration `AddSegmentSelection`; RLS on tenant.
3. Create `src/DubbingPlatform.Application/Segments/SegmentSelectionService.cs` with `SelectTranscriptAsync(...)`, `SelectTranslationAsync(...)`, `CreateManualTranscriptVersionAsync(...)`, `CreateManualTranslationVersionAsync(...)` — all require `expectedSelectionVersion`; on mismatch throw `ConflictException(code: SELECTION_CONFLICT)`; record actor + reason; bump counter atomically (row-version / `UPDATE ... WHERE SelectionVersion=@expected`).
4. Emit domain event `SegmentSelectionChanged` → invalidates dependent voice/timing work (publish existing invalidation message; do not execute recompute here).
5. If final output exists for the run, mark output stale via existing output-state flag (no recompute).

## Requirements
- R1: Stale `expectedSelectionVersion` returns conflict, never overwrites.
- R2: Content version rows never updated in place (new row per manual edit).
- R3: Every selection change records actor, reason (nullable), timestamp.
- R4: Concurrent selects serialize (exactly one winner).
- R5: Dependent-stage invalidation event always published on change.

## Edge Cases and Error Handling
- Missing version id → 404 `VERSION_NOT_FOUND`.
- Selecting version from another segment → 400 `VERSION_SEGMENT_MISMATCH`.
- Edit after output finalized → succeeds but output flagged stale + warning returned.

## Security and Safety Requirements
- Tenant + project-membership authorization at service layer (defense in depth; endpoints re-check).
- Reason text sanitized (max 500 chars, no HTML passthrough).

## Testing
- `tests/DubbingPlatform.IntegrationTests/Segments/SelectionConcurrencyTests.cs`: happy-path select, stale-write 409, concurrent race (one winner), manual edit creates new version, invalidation published, cross-tenant blocked.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~SelectionConcurrencyTests
```

## Completion Criteria
- Selection entity + service + migration + tests exist; stale writes rejected; invalidation emitted.

## Traceability
- Plan B §8.5, §8.9, §9.5, §4.6.



<!-- ===== FILE: 004-voice-preview-media-artifacts.md ===== -->


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



<!-- ===== FILE: 005-diagnostics-read-support.md ===== -->


# Task 005 — Provider Health and Diagnostics Read Support

## Goal
Add read-only diagnostics query services for provider health, queues, leases, orphans, and review backlog.

## Context
Admin/operator UI needs a secret-free, elevated-authz read layer over provider routing, queue depth, DLQ, stale leases, orphan artifacts, review backlog, and worker health. These are aggregations over Plan A runtime state — no new orchestration, no mutations.

## Starting State
Task 001 done. Plan A provider router, outbox/inbox, lease manager, worker host, and review store exist. No diagnostics query services or DTOs.

## Scope
Included: read-only query services + secret-free DTOs, elevated-authz hooks (service-layer role check), correlation IDs on every DTO.
Excluded: HTTP endpoints (Task 013), mutations/retry/DLQ-replay actions, frontend admin UI (Task 036), metrics pipeline (Task 038).

## Instructions
1. Create `src/DubbingPlatform.Application/Diagnostics/ProviderHealthQueryService.cs`: aggregates per-provider `Status` (Healthy/Degraded/Down/Unknown), `LatencyMsP95`, `ErrorRate`, `LastSuccessAt`, `ActiveRoutes`, `CircuitBreakerState` from existing router health probes. Never include keys, endpoints with credentials, or raw payloads.
2. Create `src/DubbingPlatform.Application/Diagnostics/QueueDiagnosticsService.cs`: returns queue depth per queue, DLQ depth + oldest-entry age, dead-letter reason breakdown (top 10 codes), all from existing MassTransit/outbox stores.
3. Create `src/DubbingPlatform.Application/Diagnostics/LeaseOrphanService.cs`: `GetStaleLeasesAsync` (lease older than configured TTL + heartbeat missed), `GetOrphanArtifactsAsync` (ContentObject with no owning run/segment reference), each with `correlationId`, `lastHeartbeatAt`, `ownerHint` (run/project id only, no secrets).
4. Create `src/DubbingPlatform.Application/Diagnostics/ReviewBacklogService.cs`: counts by severity/status/oldest-waiting, per-project breakdown, from existing review store.
5. Create `src/DubbingPlatform.Application/Diagnostics/WorkerHealthService.cs`: per-worker `Status`, `LastHeartbeatAt`, `ActiveJobs`, `Version` from existing worker heartbeat table.
6. Add DTOs in `src/DubbingPlatform.Application/Diagnostics/Dto/`: `ProviderHealthDto`, `ProviderRouteDto`, `QueueDepthDto`, `DlqSummaryDto`, `StaleLeaseDto`, `OrphanArtifactDto`, `ReviewBacklogDto`, `WorkerHealthDto` — every DTO carries `correlationId`; no `Secret`, `ApiKey`, `ConnectionString`, `InternalPath`, or `RawPayload` fields exist.
7. Add service-layer guard `RequireDiagnosticsViewerAsync(TenantId, UserId)` (role: TenantAdmin/Operator or `diagnostics.view` permission) called at the top of every query method; throws `ForbiddenException(code: DIAGNOSTICS_FORBIDDEN)` on failure.

## Requirements
- R1: Every query method enforces elevated authz before touching data (covered by unit test with viewer vs non-viewer).
- R2: No DTO contains secrets, connection strings, signed URLs, internal paths, or raw provider payloads (assertion test scans serialized JSON).
- R3: Every DTO/response carries a `correlationId` (generated per call if caller omits one).
- R4: Stale-lease cutoff uses configured TTL (not hardcoded); test pins TTL via options override.
- R5: All queries are read-only (no INSERT/UPDATE/DELETE; verified by EF read-only/no-tracking + test asserting zero change-tracker entries).

## Edge Cases and Error Handling
- Provider never probed → status `Unknown`, not `Down`.
- Empty DLQ → return zero depth + null oldest age (not 404).
- Orphan scan on huge table → paginated (default 50, max 200) with `hasMore` cursor.
- Missing worker heartbeat → worker listed as `Unknown` with `lastHeartbeatAt: null`.

## Security and Safety Requirements
- Tenant isolation: operators see only their tenant; cross-tenant query returns empty/404, never neighbor data.
- Elevated authz defense-in-depth (service layer now, endpoints re-check in Task 013).
- Logs contain correlation IDs + counts only, never payload bodies or lease tokens.

## Testing
- Create `tests/DubbingPlatform.UnitTests/Diagnostics/DiagnosticsReadTests.cs`: authz denial for non-viewer, secret-free serialization scan, correlationId presence, stale-lease TTL boundary, empty-DLQ shape, pagination cap, read-only (no tracked changes).
- Type: unit (mocked stores/options).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~DiagnosticsReadTests
```

## Completion Criteria
- All five query services + DTOs + authz guard exist; `DiagnosticsReadTests` pass; serialized DTOs contain no secret-bearing fields.

## Traceability
- Plan B §8.8, §8.9, §9.10.



<!-- ===== FILE: 006-auth-me-preferences-api.md ===== -->


# Task 006 — Auth, Session, Me, Preferences API

## Goal
Expose login/refresh/logout, GET /me, and GET|PUT /me/preferences with permission-string UX hints.

## Context
Frontend shell cannot render navigation, guards, or locale before resolving who the user is, which tenant/roles they hold, and their persisted preferences. Auth tokens stay in memory (Task 019); the backend owns session issuance, permission strings (UX hints only — every endpoint re-authorizes), and preference validation against the Task 001 whitelist.

## Starting State
Task 001 done (TenantUser, UserPreference, ProjectMembership exist). Plan A auth issuance (external subject) exists. No `/api/v1/auth/*`, `/me`, or preferences endpoints.

## Scope
Included: `POST /api/v1/auth/login|refresh|logout`, `GET /api/v1/me`, `GET|PUT /api/v1/me/preferences`, permission-string contract, OpenAPI updates for these routes, audit on login/logout.
Excluded: project membership management UI logic, frontend auth UX (Task 019), settings screens (Task 035), admin role assignment.

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/AuthController.cs`: `POST /api/v1/auth/login` (validates external credential → issues access+refresh pair, writes AuditEvent), `POST /api/v1/auth/refresh` (rotates refresh token, rejects reuse with 401 `TOKEN_REUSED`), `POST /api/v1/auth/logout` (revokes refresh token, idempotent — unknown token still 200).
2. Create `src/DubbingPlatform.Api/Controllers/MeController.cs`: `GET /api/v1/me` returns `{ user, tenant, roles, permissions[], locale, featureFlags, session: { issuedAt, expiresAt } }`; `GET|PUT /api/v1/me/preferences` reads/writes Task 001 whitelist keys only (unknown key → 400 `PREFERENCE_KEY_UNKNOWN`; oversize ValueJson → 413).
3. Define permission strings in `src/DubbingPlatform.Application/Authorization/Permissions.cs`: `project.view`, `project.edit`, `project.delete`, `processing.start`, `processing.cancel`, `processing.retry`, `review.view`, `review.resolve`, `export.create`, `export.download`, `admin.manage`, `diagnostics.view`. Document in XML comment: UX hints only, never a security boundary.
4. Implement `IPermissionResolver` in `src/DubbingPlatform.Application/Authorization/PermissionResolver.cs`: resolves permissions from TenantUser status + ProjectMembership roles (Disabled user → zero permissions); cache per-request only (no cross-request cache).
5. Update OpenAPI (`src/DubbingPlatform.Api/OpenApi/` or Swashbuckle annotations): schemas for login/refresh/me/preferences, 401/403/validation error examples, idempotency not required on these routes (document why).
6. Wire rate limiting on login (5/min/IP) + refresh (30/min/user) via existing ASP.NET Core rate-limiter config in `src/DubbingPlatform.Api/Program.cs`.

## Requirements
- R1: `GET /me` returns tenant, roles, full permission list, locale, flags, and session expiry in one call (no second round-trip).
- R2: Disabled user gets 403 `USER_DISABLED` on `/me` and zero permissions.
- R3: `PUT /me/preferences` rejects unknown keys and oversize values; valid keys round-trip exactly.
- R4: Refresh-token reuse is rejected and the whole token family revoked (theft detection).
- R5: Logout with unknown/expired token still returns 200 (idempotent).
- R6: Permission strings match exactly the 12 defined names (contract test asserts set equality).

## Edge Cases and Error Handling
- Bad credentials → 401 `INVALID_CREDENTIALS` (no user-enumeration detail).
- Expired refresh → 401 `TOKEN_EXPIRED`; frontend redirects to login (Task 019 consumes this code).
- Concurrent preference writes → last-writer-wins per key (documented; no 409 here — 409 lives in Tasks 003/009).
- Missing tenant claim → 401 `TENANT_REQUIRED`.

## Security and Safety Requirements
- Tenant isolation: `/me` resolves only the caller's tenant; no `?tenantId` override accepted.
- Refresh tokens hashed at rest (SHA-256 + salt), never logged; access tokens short-lived.
- Permission strings are hints: every controller still calls authorization handlers (test asserts 403 when hint present but server policy denies).
- Login/logout audited with correlationId; no passwords/secrets in logs.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Auth/MePreferencesTests.cs`: login→me→preferences round-trip, unknown-key 400, disabled-user 403, refresh rotation + reuse rejection, logout idempotency, permission-set contract, cross-tenant isolation, rate-limit 429 on login burst (relaxed in CI via options).
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~MePreferencesTests
```

## Completion Criteria
- Auth/me/preferences endpoints + permission contract + OpenAPI exist; `MePreferencesTests` pass; refresh-reuse revokes token family.

## Traceability
- Plan B §9.1, §12.1, §13.1–§13.3.



<!-- ===== FILE: 007-dashboard-projects-api.md ===== -->


# Task 007 — Dashboard and Projects API

## Goal
Expose GET /dashboard/summary and full project CRUD with archive semantics and settings-change guards.

## Context
Dashboard and project list are the entry surfaces: counts, recent outputs, storage/cost, quota, warnings, and backlog in one summary call; projects need filterable CRUD where target language is immutable after creation and settings changes are blocked while a run is active (with config-hash transparency + audit).

## Starting State
Task 001 done (DubbingProject extensions, membership, settings validator). No dashboard/projects controllers.

## Scope
Included: `GET /api/v1/dashboard/summary`, `GET|POST /api/v1/projects`, `GET|PATCH /api/v1/projects/{id}`, `POST .../archive|unarchive`, `DELETE /api/v1/projects/{id}`, filters/pagination/sort, settings-change guard + config hash + audit.
Excluded: processing start (Task 008), workspace aggregate (Task 008), segments/voices/reviews/output (Tasks 009–012), frontend list/wizard (Tasks 021–022).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/DashboardController.cs`: `GET /api/v1/dashboard/summary` returns `{ projectCounts {active, archived, total}, recentOutputs[] (max 5), storage {usedBytes, quotaBytes}, cost {monthToDate, currency}, quota {remaining, resetsAt}, warnings[] {code, message, projectId?}, backlog {pendingReviews, runningJobs} }` — single aggregated query service, tenant-scoped.
2. Create `src/DubbingPlatform.Api/Controllers/ProjectsController.cs`: `GET /api/v1/projects` (filters: `status, ownerId, search, archived`; pagination `page, pageSize` default 20/max 100; sort `createdAt|updatedAt|name` + `sortDir`), `POST /api/v1/projects` (Name required ≤200, targetLanguage required + immutable thereafter), `GET /api/v1/projects/{id}`, `PATCH /api/v1/projects/{id}` (name/description/settings only), `POST /api/v1/projects/{id}/archive|unarchive`, `DELETE /api/v1/projects/{id}` (soft-delete → archived + `DeletedAt`; hard delete only via admin path, not here).
3. Implement settings-change guard in `src/DubbingPlatform.Application/Projects/ProjectSettingsGuard.cs`: `PATCH` with `processingSettings` rejected with 409 `SETTINGS_LOCKED_ACTIVE_RUN` when any active run exists for the project; on success bump `SettingsVersion`, recompute config hash (SHA-256 over canonical settings JSON in `src/DubbingPlatform.Application/Projects/ProjectConfigHash.cs`), persist + AuditEvent with old/new hash.
4. Enforce target-language immutability server-side: any `PATCH` containing `targetLanguage` → 400 `LANGUAGE_IMMUTABLE` (frontend also hides the field — Task 022 — but backend is authoritative).
5. Add `GET /api/v1/projects` response envelope `{ items[], page, pageSize, total, sort, sortDir }`; archived projects excluded by default (`archived=false`), included only with explicit `archived=true|all`.
6. Update OpenAPI for dashboard + projects schemas, error codes (`LANGUAGE_IMMUTABLE`, `SETTINGS_LOCKED_ACTIVE_RUN`, `PROJECT_NOT_FOUND`), and pagination envelope examples.

## Requirements
- R1: Dashboard summary returns all seven sections in one 200 response; warnings carry machine-readable codes.
- R2: Project list supports filters + pagination + sort; default excludes archived; max pageSize 100 enforced.
- R3: `PATCH` changing targetLanguage always fails with 400 `LANGUAGE_IMMUTABLE`.
- R4: Settings change with active run fails 409; without active run succeeds, bumps version, changes hash, writes audit.
- R5: Archive/unarchive are idempotent (already-archived → 200, no error); DELETE on running project → 409 `PROJECT_HAS_ACTIVE_RUN`.
- R6: Cross-tenant project id returns 404 (not 403).

## Edge Cases and Error Handling
- Duplicate project name in tenant → allowed (names not unique), but empty/overlong name → 400.
- `pageSize > 100` → clamped to 100 with `clamped: true` hint, not an error.
- PATCH with unknown fields → 400 validation, no partial apply.
- Concurrent PATCH (lost update) → ETag/`If-Match` on `SettingsVersion`; mismatch → 409 `SETTINGS_VERSION_CONFLICT`.

## Security and Safety Requirements
- Tenant isolation on every query/command; project access additionally requires membership or ownership (403/404 per policy).
- `project.delete` permission required for DELETE; `project.edit` for PATCH/archive.
- Audit every mutation (create/patch/archive/unarchive/delete) with actor + correlationId.
- No cost/storage internals beyond the summary DTO (no per-invoice detail here).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Projects/ProjectsApiTests.cs`: dashboard shape, list filters/pagination/sort, create + get, language-immutable 400, settings guard 409 with active run vs success + hash change + audit without, archive idempotency, delete-with-active-run 409, cross-tenant 404, ETag conflict.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~ProjectsApiTests
```

## Completion Criteria
- Dashboard + projects endpoints, guard, hash, audit, and OpenAPI exist; `ProjectsApiTests` pass; language change and guarded settings change provably rejected.

## Traceability
- Plan B §9.2, §12.2–§12.4.



<!-- ===== FILE: 008-processing-workspace-progress-sse.md ===== -->


# Task 008 — Processing, Workspace, Progress, SSE Shell

## Goal
Expose processing lifecycle, workspace aggregate, progress, and SSE stream endpoints with idempotency and authz.

## Context
The workspace is the live project screen: starting/cancelling/retrying runs, one-call workspace aggregate (no N+1), approximate progress, and an SSE stream that acts only as invalidation hints (Task 026 polls on hint). This task builds the endpoint shell; the SSE event-type freeze happens in Task 013.

## Starting State
Task 001 done. Plan A processing-run orchestration, stage machine, and progress tracking exist. No workspace aggregate DTO or processing HTTP shell.

## Scope
Included: `POST|GET .../processing`, `POST .../cancel|retry`, `GET .../progress|stream|workspace|activity|output|quality`, workspace aggregate DTO, idempotency-key support, per-endpoint authz.
Excluded: SSE envelope/event-type freeze + payload allowlist (Task 013), segments/voices/reviews/output bodies (Tasks 009–012), frontend workspace/SSE client (Tasks 025–026).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/ProcessingController.cs` under `/api/v1/projects/{projectId}`: `POST .../processing` (start run; requires `Idempotency-Key` header, returns 202 + `runId`), `GET .../processing` (run list, paginated), `POST .../processing/{runId}/cancel` (202, idempotent), `POST .../processing/{runId}/retry` (new run linked via `RetryOfRunId`, requires fresh idempotency key).
2. Create `src/DubbingPlatform.Api/Controllers/WorkspaceController.cs`: `GET .../workspace` (aggregate DTO below, single handler call), `GET .../progress` (approximate percent + current stage only), `GET .../stream` (SSE transport shell — headers `text/event-stream`, auth via query `access_token` mapped to same policy; event serialization deferred to Task 013), `GET .../activity|output|quality` (thin projections delegating to Task 002/012/008-quality services, paginated where lists).
3. Define `WorkspaceDto` in `src/DubbingPlatform.Application/Workspace/WorkspaceDto.cs`: `{ project, media, run {id, status, configHash}, phase, stage, progress {percentApproximate, currentStage, updatedAt}, review {pendingCount, oldestWaitingAt}, warnings[], output {state, completeness}, cost {runCost, monthToDate}, activity {recent[] max 10}, permissions {allowedActions[]} }`. One handler, one DB round-trip batch (no N+1 — test asserts query count ceiling).
4. Implement idempotency in `src/DubbingPlatform.Application/Processing/ProcessingIdempotency.cs`: `(TenantId, Idempotency-Key)` → existing run returned with 200 + `Idempotent-Replayed: true`; keys expire after 24h; retry requires a NEW key (reuse of a completed-start key for different payload → 422 `IDEMPOTENCY_KEY_REUSED`).
5. Enforce authz per endpoint: `processing.start` (start/retry), `processing.cancel` (cancel), `project.view` (all GETs); archived project blocks start/retry with 409 `PROJECT_ARCHIVED`.
6. Update OpenAPI for processing/workspace/progress/stream schemas with 202 + 409 examples.

## Requirements
- R1: Start with idempotency key twice → same `runId`, second response flagged replayed, only one run created.
- R2: `GET workspace` returns all ten sections in one call with bounded queries (≤ N+1 ceiling asserted in test).
- R3: Cancel is idempotent (cancel twice → 202 both, single terminal transition); cancel on terminal run → 409 `RUN_ALREADY_TERMINAL`.
- R4: `GET progress` percent is labeled approximate (field name `percentApproximate`) and never drives billing.
- R5: Archived project start/retry rejected; active-run conflict on start returns 409 `RUN_ALREADY_ACTIVE` unless `force` with `processing.retry` permission.
- R6: SSE `stream` requires auth and tenant-scoped run; unauthenticated → 401, cross-tenant → 404.

## Edge Cases and Error Handling
- Missing `Idempotency-Key` on start → 400 `IDEMPOTENCY_KEY_REQUIRED`.
- Retry of failed run copies config hash; settings changed since → 409 `CONFIG_CHANGED_SINCE_RUN` with current hash.
- Progress on run with no stages yet → `{ percentApproximate: 0, currentStage: null }`, not 404.
- Stream disconnect → client resumes with `Last-Event-ID` (shell accepts header; replay semantics frozen in Task 013).

## Security and Safety Requirements
- Tenant + membership checks on every route; run IDs unguessable (GUID) and never enumerated cross-tenant.
- SSE token via query param is single-use-scoped (short TTL) and never logged.
- Cost fields are display approximations; billing source of truth stays in Plan A ledger.
- All mutations audited with correlationId + idempotency key.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Workspace/WorkspaceProgressTests.cs`: start idempotency + replay flag, missing-key 400, cancel idempotency + terminal 409, retry config-changed 409, workspace single-call shape + query-count ceiling, progress approximate field, archived-block, authz matrix (viewer cannot start), stream auth/tenant checks.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~WorkspaceProgressTests
```

## Completion Criteria
- Processing/workspace/progress/stream endpoints + aggregate DTO + idempotency exist; `WorkspaceProgressTests` pass; duplicate start provably creates one run.

## Traceability
- Plan B §9.4, §9.11, §12.6–§12.8.



<!-- ===== FILE: 009-segment-editing-api.md ===== -->


# Task 009 — Segments, Transcript, Translation Editing API

## Goal
Expose segment list/detail, selection, manual-edit, and retry endpoints with version-safe concurrency.

## Context
Transcript/translation review is the highest-concurrency surface: multiple editors, immutable version history, explicit selection, and auditable invalidation of downstream voice/timing work. The backend invariant (expected-version counter, 409 path) was built in Task 003; this task puts the HTTP contract on it.

## Starting State
Task 003 done (SegmentSelection + SelectionVersion counter, ConflictException, invalidation hook). Plan A SpeechSegment + immutable TranscriptVersion/TranslationVersion exist. No segment HTTP endpoints.

## Scope
Included: `GET .../segments[/{id}]`, `POST .../retry|transcript-selection|translation-selection|transcript-edits|translation-edits`, filters + pagination, expected-version enforcement, invalidation + stale-output marking wiring, audit.
Excluded: selection concurrency internals (Task 003, reuse as-is), review-resolve-with-edit (Task 011), frontend editors (Tasks 027–028).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/SegmentsController.cs` under `/api/v1/projects/{projectId}`: `GET .../segments` (filters: `speakerId, reviewStatus, qualityFlag, syncIssue, text` substring, `startMs,endMs` time window; pagination default 50/max 200; sort `startMs`), `GET .../segments/{segmentId}` (detail with current versions + selection version), `POST .../segments/{segmentId}/retry` (requeue ASR/translation for segment, 202), `POST .../transcript-selection|translation-selection` (body `{ versionId, expectedSelectionVersion, reason? }`), `POST .../transcript-edits|translation-edits` (body `{ text, expectedSelectionVersion, reason? }` → creates manual version + selects it).
2. Delegate all mutations to `src/DubbingPlatform.Application/Segments/SegmentSelectionService.cs` (Task 003); map `ConflictException` → 409 `SELECTION_CONFLICT` with `{ currentSelectionVersion, currentVersionIds }` so the client can refresh (Task 027/028 consumes this shape).
3. On every successful selection/edit, publish the Task 003 invalidation event and, when final output exists for the run, flag output stale (reuse existing output-state flag; no recompute here). Return `outputStale: true` + warning code in the mutation response.
4. Record AuditEvent per mutation with actor, reason (nullable, max 500, sanitized), old/new version IDs, and correlationId.
5. Enforce `review.view` for GETs; transcript/translation mutation requires `project.edit`; retry additionally requires `processing.retry`.
6. Update OpenAPI: segment schemas, filter params, 409 conflict shape with refresh payload, edit-request examples.

## Requirements
- R1: List supports all six filters + time window + pagination; combined filters AND together.
- R2: Stale `expectedSelectionVersion` → 409 `SELECTION_CONFLICT` with current versions (never silent overwrite).
- R3: Content versions immutable: edit creates a new manual version row; old rows unchanged (test reads old row post-edit).
- R4: Successful mutation publishes invalidation and marks stale output when final output exists.
- R5: Every mutation writes an audit record with actor + reason + version delta.
- R6: Selecting a version from another segment → 400 `VERSION_SEGMENT_MISMATCH`.

## Edge Cases and Error Handling
- Missing version id → 404 `VERSION_NOT_FOUND`.
- Edit with empty/whitespace text → 400 `SEGMENT_TEXT_EMPTY`.
- Retry on segment with active retry → 409 `SEGMENT_RETRY_ACTIVE` (idempotent poll returns existing job).
- Edit after output finalized → succeeds with `outputStale: true` warning (not an error).

## Security and Safety Requirements
- Tenant + project-membership check on every route; cross-tenant segment id → 404.
- Reason/text sanitized (max lengths, no HTML passthrough; stored as plain text).
- No transcript bodies in logs; log version IDs + correlationId only.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Segments/SegmentEditingApiTests.cs`: filter matrix, pagination, stale-write 409 with refresh payload, immutable-version assertion, invalidation published, stale-output flag, audit written, version-segment mismatch 400, authz matrix, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~SegmentEditingApiTests
```

## Completion Criteria
- Segment endpoints + 409 refresh contract + audit + OpenAPI exist; `SegmentEditingApiTests` pass; concurrent stale writes provably rejected.

## Traceability
- Plan B §9.5, §12.9–§12.10. Depends on Task 003.



<!-- ===== FILE: 010-speakers-voice-api.md ===== -->


# Task 010 — Speakers, Voice Assignment, Voice Preview API

## Goal
Expose speaker list/detail, compatible voices, stable voice assignment, and durable preview endpoints.

## Context
Each speaker keeps one stable voice; changing it invalidates dependent dubbing work, cloning voices requires recorded consent, and incompatible voices must be blocked server-side (the frontend compatible-only display is not a security boundary). Previews are the durable Task 004 jobs surfaced here.

## Starting State
Task 004 done (VoicePreviewJob lifecycle, consent/quota gates, VoicePreviewAudio artifacts). Plan A speaker diarization + voice catalog exist. No speaker/voice HTTP endpoints.

## Scope
Included: `GET .../speakers[/{id}|/{id}/available-voices]`, `PUT .../voice-assignment`, `POST|GET .../voice-previews[/{id}]`, consent + compatibility enforcement, change-invalidation, audit.
Excluded: preview job internals (Task 004, reuse as-is), frontend voice UX (Task 029), final TTS render (Plan A).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/SpeakersController.cs` under `/api/v1/projects/{projectId}`: `GET .../speakers` (list with `speakerId, voiceId?, segmentCount, assignedVoice`), `GET .../speakers/{speakerId}`, `GET .../speakers/{speakerId}/available-voices` (compatible-only: language match + gender/style constraints from `src/DubbingPlatform.Application/Voices/VoiceCompatibility.cs`; incompatible voices excluded with `excludedCount` + reasons, never returned as selectable).
2. Add `PUT .../speakers/{speakerId}/voice-assignment` (body `{ voiceId, reason? }`): enforces compatibility server-side (incompatible → 422 `VOICE_INCOMPATIBLE`), consent gate for cloning voices (no consent → 403 `VOICE_CONSENT_REQUIRED`), keeps exactly one voice per speaker, publishes dependent-invalidation event, marks stale output when final output exists; audit with actor + old/new voice.
3. Add `POST .../voice-previews` (body `{ speakerId, voiceId, text?, Idempotency-Key }` → delegates to Task 004 `VoicePreviewService`, returns 202 + `previewId` `vpv_`) and `GET .../voice-previews[/{previewId}]` (status + artifact reference when Completed; artifact served as signed URL only, never internal path).
4. Implement `VoiceCompatibility.cs` rule set: target-language match required; sample-rate/channel floor; cloning flag requires consent record; style-tag allowlist per project settings.
5. Enforce `project.view` for GETs, `project.edit` for assignment/preview creation.
6. Update OpenAPI for speaker/voice/preview schemas and error codes (`VOICE_INCOMPATIBLE`, `VOICE_CONSENT_REQUIRED`, `PREVIEW_QUOTA_EXCEEDED`).

## Requirements
- R1: Available-voices lists only compatible voices; incompatible ones never selectable (test attempts direct assignment of excluded voice → 422).
- R2: Exactly one assigned voice per speaker; reassignment replaces, never duplicates.
- R3: Cloning-voice assignment/preview without consent blocked server-side (403), even if frontend allowed it.
- R4: Voice change publishes invalidation + marks stale output when final output exists (response carries `outputStale: true`).
- R5: Preview creation honors Task 004 idempotency/quota/consent (duplicate key → same job; quota → 429).
- R6: Every assignment writes an audit record with actor + old/new voice + reason.

## Edge Cases and Error Handling
- Unknown voice id → 404 `VOICE_NOT_FOUND`.
- Preview text overlong → 400 `PREVIEW_TEXT_INVALID` (inherited from Task 004).
- Assign same voice id → 200 no-op with `changed: false`, no invalidation emitted.
- Speaker with zero segments → assignment allowed, flagged `unusedSpeaker: true`.

## Security and Safety Requirements
- Tenant + membership checks per route; cross-tenant speaker/voice id → 404.
- Compatibility/consent enforced server-side regardless of client filtering.
- No voice-model binaries or provider keys in responses/logs; preview audio via signed URL only.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Voices/VoiceApiTests.cs`: compatible-only listing, incompatible-assign 422, consent-block 403, stable single-voice invariant, change invalidation + stale flag, preview 202/status/idempotency/quota, no-op same-voice, audit written, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL; provider client mocked).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~VoiceApiTests
```

## Completion Criteria
- Speaker/voice/preview endpoints + compatibility + consent + audit exist; `VoiceApiTests` pass; incompatible/consent-less assignments provably blocked.

## Traceability
- Plan B §9.6, §12.11. Depends on Task 004.



<!-- ===== FILE: 011-reviews-context-api.md ===== -->


# Task 011 — Review Context and Mutations

## Goal
Expose GET /api/v1/reviews/{id}/context single-screen read model and harden review mutations.

## Context
Reviewers resolve queue items on one screen: the item plus project/run/segment/version history/voice/audio/sync/QC evidence, allowed actions, and full history. Mutations must be idempotent, version-guarded, reasoned, and audited; ResolvedWithEdit creates a real manual content version (not a silent text patch).

## Starting State
Task 003 done (selection versioning, manual-version semantics). Plan A review queue + statuses exist. No context aggregate endpoint; existing mutations lack idempotency/version/reason hardening.

## Scope
Included: `GET /api/v1/reviews/{id}/context`, mutation hardening (`resolve|dismiss|reopen|resolve-with-edit`) with idempotency key + expected version + reason + audit, `ResolvedWithEdit` → manual version creation.
Excluded: queue listing filters UI, segment endpoints (Task 009), frontend studio (Task 031).

## Instructions
1. Create `src/DubbingPlatform.Application/Reviews/ReviewContextDto.cs`: `{ item {id, type, severity, status, version}, project {id, name}, run {id, status, configHash}, segment {id, startMs, endMs, speakerId}, versions {transcript[], translation[], selectedIds, selectionVersion}, voice {speakerId, voiceId, consentState}, audio {previewArtifactId?, signedUrl?: null — resolved by Task 012 at serve time}, sync {offsetMs, driftFlag}, qc {issues[], evidenceArtifactIds[]}, actions {allowed[]}, permissions {canResolve, canEdit}, history[] }`. Single handler, batched queries (no N+1).
2. Create `src/DubbingPlatform.Api/Controllers/ReviewsController.cs`: `GET /api/v1/reviews/{id}/context` (requires `review.view`); mutations `POST /api/v1/reviews/{id}/resolve|dismiss|reopen|resolve-with-edit` requiring `Idempotency-Key` header + body `{ expectedVersion, reason (required, max 500), editText? (resolve-with-edit only) }`.
3. Implement `src/DubbingPlatform.Application/Reviews/ReviewMutationService.cs`: expected-version mismatch → 409 `REVIEW_VERSION_CONFLICT` with current version; duplicate idempotency key → replay original result with `Idempotent-Replayed: true`; missing/blank reason → 400 `REVIEW_REASON_REQUIRED`; `resolve-with-edit` delegates to Task 003 manual-version creation then resolves with `ResolvedWithEdit` status linking the new version id.
4. Write AuditEvent per mutation (actor, action, reason, old/new status, version delta, correlationId, idempotency key).
5. Enforce `review.resolve` permission for all mutations; cross-tenant review id → 404.
6. Update OpenAPI with context schema, mutation bodies, 409 refresh shape, and `ResolvedWithEdit` examples.

## Requirements
- R1: Context returns all eleven sections in one 200 (item, project, run, segment, versions, voice, audio, sync, QC, actions/permissions, history).
- R2: Stale `expectedVersion` → 409 with current version (never silent resolve).
- R3: Duplicate idempotency key replays original result; no second state transition (test asserts history length unchanged).
- R4: Mutations without reason rejected (400); reason persisted + audited.
- R5: `resolve-with-edit` creates a new immutable manual version and links it on the review (old versions untouched).
- R6: Allowed-actions reflect actual server policy (test asserts disallowed action attempt → 403 even when client forges it).

## Edge Cases and Error Handling
- Resolve on already-resolved → 409 `REVIEW_ALREADY_RESOLVED` (with current status; idempotent key replay still 200).
- Reopen on open item → 409 `REVIEW_NOT_RESOLVED`.
- Edit text empty on resolve-with-edit → 400 `REVIEW_EDIT_EMPTY`.
- Version history capped at 50 entries in context (with `truncated: true` flag).

## Security and Safety Requirements
- Tenant + membership + `review.view|resolve` checks per route; existence never leaked cross-tenant (404).
- Reason/edit text sanitized (plain text, max lengths); no transcript dumps in logs (IDs only).
- Audio/QC evidence exposed as IDs here; signed URLs minted only by Task 012 output path.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Reviews/ReviewContextTests.cs`: context shape single-call, version-conflict 409 + refresh, idempotent replay, reason-required 400, resolve-with-edit creates version + links, already-resolved 409, allowed-actions honesty, audit written, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~ReviewContextTests
```

## Completion Criteria
- Context endpoint + hardened mutations + audit exist; `ReviewContextTests` pass; replayed mutations provably cause no second transition.

## Traceability
- Plan B §9.7, §12.13. Depends on Task 003.



<!-- ===== FILE: 012-output-exports-notifications-api.md ===== -->


> **REVIEW FIX — SUPERSEDED (split):** this combined file is superseded by `012A-output-export-api.md` (output/export API) + `012B-notifications-api.md` (notifications API). New work goes to 012A/012B. Ownership: workspace projection stays in 008; output/export API in 012A; notifications API in 012B.

# Task 012 — Output, Exports, Notifications API

## Goal
Expose output summary with partial states, export lifecycle wiring, and notification read endpoints.

## Context
Users need one output call (video/audio/subs/transcript/translation/timeline/speakers/QC with completeness like 96/100 and generation state), export creation/download via signed URLs only, and durable notifications (list, unread count, mark-read/read-all) backed by the Task 002 projection.

## Starting State
Tasks 002 (Notification/ActivityEvent + projectors) and 004 (preview + QC evidence artifacts) done. Plan A export pipeline + blob storage exist. No output/exports/notifications HTTP endpoints.

## Scope
Included: `GET .../output`, export create/get/download wiring, `GET /api/v1/notifications|unread-count`, `POST .../read|read-all`, signed-URL-only delivery, OpenAPI updates.
Excluded: export pipeline internals (Plan A), notification projection (Task 002, reuse), SSE delivery (Task 013), frontend output/center (Tasks 033–034).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/OutputController.cs` under `/api/v1/projects/{projectId}`: `GET .../output` returns `{ state (Ready|Generating|Failed|Partial|Unavailable), completeness { ready, total } e.g. 96/100, items { video?, audio?, subtitles[], transcript?, translation?, timeline?, speakers?, qc { summary, issuesUrl? } }, warnings[], updatedAt }`. Every servable file exposed as time-boxed signed URL only — never internal paths, never `ContentObject` storage keys.
2. Wire exports in `src/DubbingPlatform.Api/Controllers/ExportsController.cs`: `POST .../exports` (body `{ format, profile }`, requires `Idempotency-Key`, `export.create`), `GET .../exports` (list, paginated), `GET .../exports/{exportId}` (status), `GET .../exports/{exportId}/download` (302 to signed URL, requires `export.download`; URL TTL ≤ 15 min). Export completion/failure publishes existing domain events (consumed by Task 002 projector + Task 013 SSE).
3. Create `src/DubbingPlatform.Api/Controllers/NotificationsController.cs`: `GET /api/v1/notifications` (paginated, filter `unreadOnly`, sort `createdAt desc`), `GET /api/v1/notifications/unread-count`, `POST /api/v1/notifications/{id}/read` (idempotent), `POST /api/v1/notifications/read-all` (idempotent, returns `markedCount`). Null-ProjectId quota/policy notifications visible at tenant level.
4. Partial-state rule: when `ready < total`, state is `Partial` with per-item `missing[]` reasons (e.g. `SEGMENT_PENDING`, `QC_BLOCKED`); `Generating` carries `progressApproximate`; `Failed` carries `errorCode` (no stack traces).
5. Enforce `project.view` (output/exports list), `export.create|download` (exports), and recipient-or-admin scoping on notifications (users see only their own + tenant-broadcast; cross-user id → 404).
6. Update OpenAPI for output/exports/notifications schemas, signed-URL shape, and error codes.

## Requirements
- R1: Output returns state + completeness + per-item availability in one call; partial (e.g. 96/100) explicitly labeled `Partial` with missing reasons.
- R2: No response contains internal storage paths or bucket keys (assertion test scans JSON).
- R3: Export download is a short-lived signed redirect (302); direct object URLs never exposed.
- R4: Notifications list/unread-count/read/read-all round-trip; read twice is idempotent; read-all returns count.
- R5: Export creation with duplicate idempotency key replays (single export row).
- R6: Cross-tenant output/export/notification id → 404.

## Edge Cases and Error Handling
- Output before any run → `Unavailable` (not 404) with `reason: NO_RUNS_YET`.
- Export requested while output Partial → 409 `OUTPUT_INCOMPLETE` unless `allowPartial: true` explicitly set.
- Download on expired/failed export → 409 `EXPORT_NOT_READY`.
- Read-all with zero unread → 200 `{ markedCount: 0 }`.

## Security and Safety Requirements
- Tenant isolation everywhere; notification recipient scoping (no reading another user's notifications).
- Signed URLs tenant-scoped, ≤ 15 min TTL, single-resource; logged as issuance events (URL value never logged).
- Export formats allowlisted server-side; path traversal in format/profile rejected with 400.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Output/OutputNotificationsApiTests.cs`: output full/partial/generating/failed/unavailable shapes, no-internal-path scan, export create/idempotency/list/download-redirect TTL, partial-export guard, notification list/unread/read/read-all idempotency, recipient isolation, cross-tenant 404.
- Type: integration (WebApplicationFactory + Testcontainers PostgreSQL; storage emulator or fake URL signer).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~OutputNotificationsApiTests
```

## Completion Criteria
- Output/exports/notifications endpoints with signed-URL-only delivery exist; `OutputNotificationsApiTests` pass; partial output and idempotent exports provably correct.

## Traceability
- Plan B §9.8, §9.9, §12.15–§12.16. Depends on Tasks 002, 004.




<!-- ===== FILE: 012A-output-export-api.md ===== -->


# Task 012A — Output and Export API

**Required/Optional:** Required
**Complexity:** M

## Goal
Expose the output summary and export lifecycle endpoints with signed-URL-only downloads.

## Context
Split from oversized Task 012 (output/export + notifications). Output/export is the final-artifact surface (final video/audio, subtitles, transcript/translation, timeline JSON, speakers, QC report) with partial-state semantics; it pairs with the exports UI (033) and workspace output panel (025). Task 008 owns only the workspace projection of output readiness — this task owns the API.

## Starting State
Depends on Tasks 002 (activity for export events), 004 (preview/QC artifacts referenced by output), 008 (processing lifecycle owns run state this API projects). Task 012 (combined) is superseded by 012A + 012B.

## Scope
Included: `GET .../output` aggregate, export create/status/download wiring, partial/completeness metadata, signed-URL issuance, idempotency on export creation.
Excluded: notifications API (012B), workspace aggregate shape (008), exports UI (033), review/activity surfaces.

## Instructions
1. Implement `GET /api/v1/projects/{projectId}/output` in `src/DubbingPlatform.Api/Controllers/OutputController.cs` (or existing export/output controller): returns per-asset entries (final video, audio-only, subtitles, transcript, translation, timeline JSON, speaker metadata, QC report) each `ready|generating|failed|partial|unavailable` with `completeness` (e.g. `96/100` segments) and `generationState`; never expose internal storage paths or credentials.
2. Wire existing Plan A export jobs: `POST /api/v1/projects/{projectId}/exports` (idempotency key, 7d), `GET .../exports[/{exportId}]`, download via `GET .../exports/{exportId}/download` returning short-lived signed URL (15-min, post-auth issuance, single-use where supported). All downloads are signed URLs only.
3. Partial-export rule: any export over incomplete segments includes `completeness` + `isPartial:true` + missing-segment summary; full-export request on incomplete run returns `409 EXPORT_INCOMPLETE` with the partial offer, never silent truncation.
4. Publish export events (`export.created/completed/failed`) to the notification/activity projectors (002); record actor + idempotency key for audit.
5. Update OpenAPI (feeds 014): output + export schemas, `EXPORT_INCOMPLETE` code, idempotency header, signed-URL response shape.

## Requirements
- R1: Every asset entry carries explicit readiness state; no asset is implied ready.
- R2: Partial exports always carry completeness metadata.
- R3: Downloads are short-lived signed URLs issued post-auth only.
- R4: Export creation is idempotent (same key → same job, no duplicate).
- R5: No internal storage paths, bucket names, or credentials in any response.

## Edge Cases and Error Handling
- Export requested with no final output → `409 EXPORT_NOT_READY` with actionable next step.
- Signed URL expired → `410 URL_EXPIRED` with re-issue action, never the raw storage error.
- Duplicate export (same key) → return original job, never a second job.

## Security and Safety Requirements
- Tenant + project-membership authorization on every route; cross-tenant returns 404 without existence leak.
- Signed URLs tenant-scoped, short-lived, never logged/cached long-term; no URL in telemetry (038).

## Testing
- Extend `tests/DubbingPlatform.IntegrationTests/Exports/OutputExportApiTests.cs`: readiness matrix, partial completeness, idempotent create, signed-URL-only download, cross-tenant 404, `EXPORT_INCOMPLETE` path.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~OutputExportApiTests
```

## Completion Criteria
- Output aggregate + export lifecycle + signed-URL downloads + tests exist; partial states explicit; supersedes the output half of Task 012.

## Traceability
- Plan B §9.8, §12.15. Split from 012; pairs with 033; workspace projection stays in 008.



<!-- ===== FILE: 012B-notifications-api.md ===== -->


# Task 012B — Notifications API

**Required/Optional:** Required
**Complexity:** S

## Goal
Expose the durable notifications list, unread count, and read-state endpoints.

## Context
Split from oversized Task 012 (output/export + notifications). Notifications are the durable user-facing projection built by Task 002; this task is its HTTP surface for the notification center (034) and SSE invalidation (026). No export/output logic lives here.

## Starting State
Depends on Task 002 (Notification entity, projector, dedup). Task 012 (combined) is superseded by 012A + 012B.

## Scope
Included: `GET /notifications`, `GET /notifications/unread-count`, `POST /{id}/read`, `POST /read-all`; tenant/user scoping; deep-link fields; OpenAPI updates.
Excluded: output/export API (012A), notification projection logic (002), notification center UI (034), SSE envelope (013).

## Instructions
1. Implement in `src/DubbingPlatform.Api/Controllers/NotificationsController.cs`: `GET /api/v1/notifications` (cursor pagination, `unreadOnly` filter, newest-first; excludes expired), `GET /api/v1/notifications/unread-count` (lightweight count query, same scope), `POST /api/v1/notifications/{notificationId}/read` (idempotent), `POST /api/v1/notifications/read-all` (idempotent, scoped to caller).
2. Every item returns deep-link fields (`resourceType`, `resourceId`, `projectId`) sufficient for 034 routing; payloads carry short human-readable summaries only — never transcript bodies, signed URLs, secrets, or raw provider data.
3. Enforce tenant + recipient scoping in the repository (`TenantId + RecipientUserId == caller`); cross-user/cross-tenant reads return 404 without existence leak.
4. Emit `notification.created` (013) on projection; document that SSE is an invalidation hint — clients refetch this API as source of truth.
5. Update OpenAPI (feeds 014): notification schemas, pagination, idempotent-read semantics.

## Requirements
- R1: List is tenant/recipient-scoped, paginated, newest-first, excludes expired.
- R2: Unread count is lightweight and consistent with the list scope.
- R3: Mark-read/read-all are idempotent (repeat calls are no-ops, never errors).
- R4: Payloads contain no sensitive content (automated assertion).
- R5: Deep links resolve to an existing project/review/export or documented fallback.

## Edge Cases and Error Handling
- Already-read notification re-marked read → 200 no-op, not 409.
- `read-all` with zero unread → 200 with `marked:0`.
- Expired notification read → 404 (treated as gone), not 410.

## Security and Safety Requirements
- Strict recipient scoping; no admin bypass in this endpoint (admin views use 013 diagnostics aggregates, never other users inboxes).
- No notification body in logs/telemetry beyond ID + type.

## Testing
- Extend `tests/DubbingPlatform.IntegrationTests/Notifications/NotificationsApiTests.cs`: scoping, pagination order, unread-count consistency, idempotent reads, no-sensitive-payload assertion, cross-tenant/cross-user 404.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~NotificationsApiTests
```

## Completion Criteria
- Notifications API + scoping + idempotent reads + tests exist; supersedes the notifications half of Task 012; 034 can build on it.

## Traceability
- Plan B §8.3, §9.9, §12.16. Split from 012; projector in 002; UI in 034; SSE in 013/026.



<!-- ===== FILE: 013-admin-sse-error-contracts.md ===== -->


# Task 013 — Admin/Diagnostics API, SSE Contract Freeze, Error Mapping

## Goal
Freeze the SSE envelope with 14 event types and payload allowlist plus admin/diagnostics reads and backend error envelope.

## Context
SSE is the live-update transport for the whole frontend (Task 026 consumes it as invalidation hints) and admin/operator surfaces need elevated read endpoints. Both must be frozen now: exact envelope fields, the closed set of 14 event types, a payload allowlist that can never carry secrets/URLs/raw payloads/lease data, and a uniform error envelope with a documented code→HTTP mapping.

## Starting State
Task 005 done (diagnostics query services + secret-free DTOs). Task 008 done (SSE `stream` transport shell). No admin controllers, no frozen SSE contract, no uniform error envelope.

## Scope
Included: `GET /api/v1/admin/*` + diagnostics reads, SSE envelope + 14 types + allowlist + serialization tests, error envelope `{code,message,correlationId,details}` + mapping table + middleware.
Excluded: diagnostics mutations/replay, frontend SSE client (Task 026), frontend admin UI (Task 036), metrics (Task 038).

## Instructions
1. Create `src/DubbingPlatform.Api/Controllers/AdminController.cs`: `GET /api/v1/admin/usage|quotas|provider-health|provider-routes` and `GET /api/v1/admin/diagnostics/queues|dlq|leases|orphans|review-backlog` — thin wrappers over Task 005 services; every route requires `admin.manage` or `diagnostics.view` (service guard re-checks); paginate leases/orphans/DLQ (default 50/max 200).
2. Freeze SSE contract in `src/DubbingPlatform.Api/Sse/SseEnvelope.cs`: `{ eventId, schemaVersion: 1, eventType, tenantId, projectId?, processingRunId?, occurredAt, payload }`. Closed event-type set (exactly these 14): `project.status_changed`, `run.status_changed`, `stage.started`, `stage.progress`, `stage.completed`, `stage.failed`, `stage.review_required`, `review.created`, `review.resolved`, `export.created`, `export.completed`, `export.failed`, `notification.created`, `output.ready`. Unknown types fail serialization tests.
3. Enforce payload allowlist in `src/DubbingPlatform.Api/Sse/SsePayloadPolicy.cs`: allowed = IDs, status enums, approximate percents, counts, machine-readable codes, timestamps; NEVER = secrets, tokens, signed URLs, internal paths, raw provider payloads, lease tokens/heartbeats, transcript/translation bodies. Add unit scan asserting serialized frames contain none of the forbidden keys (`signedUrl, token, secret, apiKey, connectionString, internalPath, rawPayload, leaseToken`).
4. Implement error envelope in `src/DubbingPlatform.Api/Errors/ApiError.cs` + `ErrorMappingMiddleware`: `{ code, message (human, no internals), correlationId, details? (field errors only) }`. Mapping table: validation→400, auth→401 (`TOKEN_EXPIRED, TOKEN_REUSED, INVALID_CREDENTIALS`), forbidden→403, not-found→404 (cross-tenant included), conflict→409 (`SELECTION_CONFLICT, REVIEW_VERSION_CONFLICT, SETTINGS_LOCKED_ACTIVE_RUN, RUN_ALREADY_ACTIVE, PREVIEW_STATE_CONFLICT`), quota→429, downstream/provider→502 (`PREVIEW_PROVIDER_TIMEOUT`), unknown→500 `INTERNAL` (message generic, detail in logs only).
5. Wire `Last-Event-ID` resume on `GET .../stream`: server accepts cursor, replays missed envelope headers only (no payload backfill beyond last 100 events), duplicates tolerated by client.
6. Update OpenAPI: admin schemas, SSE envelope + event-type enum + allowlist note, error envelope + code catalog.

## Requirements
- R1: Admin/diagnostics routes enforce elevated authz (viewer 200, non-viewer 403, cross-tenant 404).
- R2: Exactly 14 SSE event types; emitting any other type fails tests (closed-enum assertion).
- R3: Every SSE frame carries all eight envelope fields; `schemaVersion` is 1.
- R4: Serialized SSE payloads contain no forbidden keys (automated scan passes).
- R5: Every error response matches `{code,message,correlationId,details?}` and the mapping table (contract test per code).
- R6: 500s never leak stack traces or internals (message generic; correlationId links to server log).

## Edge Cases and Error Handling
- DLQ/leases/orphans empty → 200 zero-shape (not 404).
- Stream replay beyond 100-event window → 200 with `replayTruncated: true` header hint; client falls back to polling (Task 026).
- Unknown admin sub-path → 404 `ADMIN_ROUTE_UNKNOWN` (not generic 404 page).
- SSE payload exceeding 64KB → dropped + metric `sse.payload_dropped_total`, stream stays open.

## Security and Safety Requirements
- Tenant isolation + elevated authz on every admin route (defense in depth: controller + Task 005 service guard).
- No secrets/URLs/raw payloads/lease data in SSE or admin DTOs (scan tests).
- CorrelationId on every error + SSE frame; logs carry IDs, never bodies.
- Admin reads audited (who viewed what scope + when).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Admin/AdminSseErrorContractTests.cs`: authz matrix, envelope-field presence, 14-type closed set, payload-forbidden-key scan, error-envelope shape + mapping-table cases (400/401/403/404/409/429/502/500), Last-Event-ID resume, oversize-payload drop.
- Type: integration (WebApplicationFactory) + unit scan for payload policy.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~AdminSseErrorContractTests
```

## Completion Criteria
- Admin endpoints + frozen SSE contract + error envelope exist; `AdminSseErrorContractTests` pass; forbidden-key scan and closed-type assertion green.

## Traceability
- Plan B §9.10, §9.11, §9.12, §12.19. Depends on Tasks 005, 008.



<!-- ===== FILE: 014-openapi-client-generation.md ===== -->


# Task 014 — OpenAPI Authority and TypeScript Client Generation

## Goal
Make versioned OpenAPI the single contract authority with generated TS client and CI drift-fail.

## Context
Frontend (Tasks 015+) must build against generated types, not hand-written fetch shapes; any backend contract change in Tasks 006–013 without regenerating the client is a defect. This task freezes OpenAPI as authoritative, adds `make generate-api`, commits the generated client under `frontend/src/api/generated/`, and fails CI/build when stale.

## Starting State
Tasks 006–013 done (all backend endpoints + OpenAPI annotations). No bundled OpenAPI artifact, no generator, no `frontend/src/api/generated/` (frontend scaffolding lands in Task 015; this task creates the target dir + .gitkeep-compatible generation).

## Scope
Included: versioned OpenAPI bundle (schemas, examples, enums, errors, pagination, SSE, idempotency, concurrency), `make generate-api`, generated client output, drift check (build fails when stale), docs for regeneration.
Excluded: frontend app scaffolding (Task 015), query-key/error-normalization wiring (Task 017), backend endpoint changes (Tasks 006–013, frozen inputs).

## Instructions
1. Bundle versioned OpenAPI in `src/DubbingPlatform.Api/OpenApi/openapi.v1.json` (generated at build from Swashbuckle/NSwag + hand-checked): must include all Tasks 006–013 routes with schemas, request/response examples, enums (statuses, severities, SSE event types, error codes), error envelope, pagination envelope, `Idempotency-Key` header + `If-Match`/expected-version concurrency docs, and SSE `stream` endpoint description.
2. Add `Makefile` target `generate-api` (repo root; also `make check-api-drift`): runs `openapi-typescript-codegen` (or `orval`) with pinned version in `package.json`/`tools/manifest`, outputs to `frontend/src/api/generated/` (`index.ts, schemas.ts, client.ts` or tool equivalent + `OPENAPI_VERSION` stamp file). Command must be hermetic (no network at generate time — bundle from step 1 is the input).
3. Commit generated output: `frontend/src/api/generated/` checked in; `.gitignore` must NOT exclude it; generated files carry `/* auto-generated — do not edit; run make generate-api */` header.
4. Add drift gate: `frontend/package.json` `prebuild` (or CI step + `make check-api-drift`) regenerates to a temp dir and diffs against committed `frontend/src/api/generated/`; any diff fails the build with `API DRIFT: run make generate-api and commit`. Backend CI runs the same check (`dotnet build` target invoking the diff script via `exec`).
5. Document regeneration in `docs/api-contract.md` (one page): source of truth diagram (annotations → bundle → generated client), `make generate-api` usage, enum/error-code addition checklist, SSE-type addition rule (Task 013 closed set must be updated first).

## Requirements
- R1: `openapi.v1.json` covers every Tasks 006–013 route (contract test asserts route count ≥ expected list; missing route fails).
- R2: Generated client contains all DTOs, enums (incl. 14 SSE types + error codes), and pagination/error envelopes.
- R3: `make generate-api` is hermetic and reproducible (two consecutive runs byte-identical).
- R4: Stale generated client fails build/CI (drift test: touch bundle → check fails until regenerate).
- R5: Generated files are committed and importable by `frontend/` strict TS (Task 015+ consumes them; smoke import compiles).

## Edge Cases and Error Handling
- OpenAPI bundle invalid → `generate-api` fails with schema validation errors (not silent partial output).
- Generator version bump → `OPENAPI_VERSION` stamp + lockfile change required in same commit (drift check covers output, reviewer covers version pin).
- `frontend/` not yet scaffolded (Task 015 pending) → `generate-api` still succeeds by creating `frontend/src/api/generated/` only.

## Security and Safety Requirements
- No secrets/examples with real tokens in OpenAPI examples (scan: `Bearer eyJ` forbidden outside `***` placeholders).
- SSE/concurrency docs state auth + tenant rules; no internal URLs in `servers[]` beyond relative `/api/v1`.
- Drift gate runs in CI on every PR touching `src/DubbingPlatform.Api/**`.

## Testing
- Backend: extend OpenAPI bundle test (route-coverage assertion) — location `tests/DubbingPlatform.IntegrationTests/OpenApi/OpenApiCoverageTests.cs` (assert all expected paths + error/pagination envelopes + 14 SSE enum values present).
- Frontend: generated-client smoke import compiles under strict TS (`frontend/src/api/generated/` import in a typecheck test; full wiring in Task 017).
- Drift: `make check-api-drift` script tested by mutating bundle in CI dry-run (documented in `docs/api-contract.md`).

## Validation
```bash
make generate-api
npm run build --prefix frontend
dotnet build
dotnet test --filter FullyQualifiedName~OpenApiCoverageTests
```

## Completion Criteria
- Versioned bundle + `make generate-api` + committed generated client + drift-fail exist; `OpenApiCoverageTests` pass; `npm run build --prefix frontend` succeeds on generated code; consecutive generations byte-identical.

## Traceability
- Plan B §10.4, §16.3. Depends on Tasks 006–013.



<!-- ===== FILE: 015-frontend-scaffolding-routing.md ===== -->


# Task 015 — Frontend Scaffolding, Routing, Providers

## Goal
Create standalone `frontend/` app with strict TypeScript, Vite, React Router, TanStack Query, Zustand, and Tailwind baseline.

## Context
All product UX (Tasks 016–044) builds on this scaffold; routing, providers, and env conventions frozen here prevent per-feature drift. Backend contracts arrive via the Task 014 generated client; this task wires the shell they plug into.

## Starting State
Task 014 done (`frontend/src/api/generated/` exists with committed client). No `frontend/package.json`, no Vite config, no `App.tsx`. Depends on Task 014.

## Scope
Included: `frontend/` project setup, TS strict config, Vite, Router, Query client, Zustand store root, Tailwind, directory structure, `App.tsx`/`router.tsx`/providers/layouts, route code-splitting, `VITE_*` env.
Excluded: design tokens/primitives (Task 016), API client wiring (Task 017), shell/IA/i18n/telemetry (Task 018), any feature screens.

## Instructions
1. Scaffold `frontend/` with `frontend/package.json` (React 18+, TypeScript strict, Vite, `react-router-dom`, `@tanstack/react-query`, `zustand`, `tailwindcss`), `frontend/tsconfig.json` (`strict: true`, `noUncheckedIndexedAccess: true`, `noImplicitReturns`), `frontend/vite.config.ts`, `frontend/tailwind.config.ts`, `frontend/postcss.config.js`, `frontend/.env.example` documenting all `VITE_*` vars (`VITE_API_BASE_URL`, `VITE_APP_VERSION`, `VITE_SSE_ENABLED`, `VITE_TELEMETRY_ENABLED`).
2. Create directory structure under `frontend/src/`: `app/` (`App.tsx`, `router.tsx`, `providers/`, `layouts/`), `api/`, `features/`, `components/`, `stores/`, `hooks/`, `lib/`, `styles/`, `i18n/`, `telemetry/`, `types/` (empty index barrels only; feature content lands in later tasks).
3. Create `frontend/src/app/App.tsx`: composes `QueryClientProvider` + `RouterProvider` + theme/locale/telemetry providers (provider implementations land in Task 018; import from provider modules with stub exports here so App compiles).
4. Create `frontend/src/app/router.tsx`: route tree with lazy code-splitting (`React.lazy` + `Suspense` fallback) for `dashboard`, `projects`, `projects/:id/*`, `review`, `notifications`, `settings`, `admin`, `login`; `*` → NotFound; no route `loader`s (data via TanStack Query in Task 017+).
5. Create `frontend/src/app/providers/` (`QueryProvider.tsx` with single shared QueryClient: `retry: false` by default — GET-only retry lives in Task 017 — plus `staleTime`/`gcTime`; `StoreProvider`/`ThemeProvider`/`LocaleProvider` stubs) and `frontend/src/app/layouts/` (`RootLayout.tsx` outlet + suspense boundary, `AuthLayout.tsx` minimal).
6. Add `frontend/src/lib/env.ts`: typed `VITE_*` reader with zod validation at startup; missing/invalid required var fails fast with a config-error screen (never silent `undefined`).
7. Add scripts to `frontend/package.json`: `typecheck` (`tsc --noEmit`), `lint` (eslint flat config, zero-warning policy), `build` (typecheck → vite build), `test` (vitest), `preview`, `build-storybook` placeholder (real config in Task 016).

## Requirements
- R1: `tsc --noEmit` passes with strict flags; no `any` leaks in scaffold files (eslint `@typescript-eslint/no-explicit-any` set to error).
- R2: Every top-level route chunk is lazy-loaded; `vite build` emits separate chunks per route (assert via chunk naming in build output).
- R3: All env access goes through `lib/env.ts`; direct `import.meta.env` outside it fails lint (`no-restricted-syntax`).
- R4: QueryClient constructed once in `QueryProvider`; no per-component client construction.
- R5: Production build succeeds with `VITE_*` values from `.env.example` (CI uses example env for the build gate).

## Edge Cases and Error Handling
- Missing `VITE_API_BASE_URL` → startup guard renders config-error screen, never a blank page.
- Chunk load failure (deploy skew) → error boundary offers reload button with `VITE_APP_VERSION` stamp.
- Unknown route → NotFound with link home; no redirect loops.

## Security and Safety Requirements
- No secrets in `.env.example` (only public `VITE_*` vars; document that backend secrets never enter frontend env).
- `index.html` sets baseline CSP meta + `referrer` policy (full CSP headers land in Task 037; scaffold must not weaken them).
- Source maps disabled for production build by default.

## Testing
- Create `frontend/src/app/__tests__/router.test.tsx`: asserts route table renders expected paths, lazy fallback shows, unknown path → NotFound (vitest + MemoryRouter).
- Type: unit (vitest); run `npm run test -- src/app`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run lint
cd frontend && npm run build
```

## Completion Criteria
- `frontend/` builds, typechecks, and lints clean; route code-splitting verified in build output; env guard works; router test passes.

## Traceability
- Plan B §10.1, §10.2, §22. Depends on Task 014.



<!-- ===== FILE: 016-design-tokens-ui-primitives.md ===== -->


# Task 016 — Design Tokens and Core UI Primitives

## Goal
Implement the token system and domain-agnostic component library backing all product screens.

## Context
Every feature task (019+) consumes these primitives; no feature may invent one-off buttons, badges, or dialogs — variants come from here. Status visualization must cover every backend status with consistent tokens.

## Starting State
Task 015 done (frontend scaffold, Tailwind configured). No `tokens.css`, no `components/`. Depends on Task 015.

## Scope
Included: `tokens.css`/`globals.css`, all listed primitives + shared product components, Storybook.
Excluded: feature screens, API wiring, shell layouts, i18n string content (Task 018).

## Instructions
1. Create `frontend/src/styles/tokens.css`: CSS custom properties for color (brand/neutral/surface/text/border + status: `success, warning, error, info, neutral, processing, review, cancelled`), spacing scale, type scale (font-size/line-height/weight), radius, shadow/elevation; dark-theme overrides via `[data-theme="dark"]`.
2. Create `frontend/src/styles/globals.css`: Tailwind directives + base resets, focus-visible ring, `prefers-reduced-motion` handling, scrollbar styling, RTL-safe logical properties (`margin-inline`, `padding-inline`, `inset-inline` — never physical left/right in shared CSS).
3. Implement primitives in `frontend/src/components/` (one folder per component, each with `.tsx` + `.test.tsx` + `.stories.tsx`): `Button`, `IconButton`, `Input`, `Textarea`, `Select`, `Combobox`, `Checkbox`, `Radio`, `Switch`, `Slider`, `Modal`, `Drawer`, `Popover`, `Tooltip`, `Tabs`, `Accordion`, `Table`, `DataGrid`, `Pagination`, `Badge`, `StatusBadge` (maps every backend status → token: success/warning/error/info/neutral/processing/review/cancelled), `ProgressBar`, `Ring`, `Skeleton`, `Alert`, `Toast` (+ `ToastProvider`/`useToast`), `EmptyState`, `ErrorState`, `ConfirmDialog`, `CommandMenu`, `Breadcrumbs`, `Card`, `Panel`.
4. Implement shared product components in `frontend/src/components/product/`: `EntityId` (monospace id with copy button), `RelativeTime` (locale-aware), `CostDisplay` (currency formatting), `QuotaMeter` (usage bar with warning threshold), `ProviderBadge` (healthy/degraded/down), `CorrelationId` (debug-only display with copy).
5. Accessibility: every interactive primitive keyboard-operable; focus-trapped dialogs (`Modal`/`Drawer`/`ConfirmDialog` trap + restore focus on close); `aria-*` labels; `Tooltip`/`Popover` escape-to-close; `Toast` region `aria-live="polite"`; `DataGrid` row keyboard navigation.
6. Storybook: `frontend/.storybook/main.ts` + `preview.ts` (light/dark theme switcher, RTL toggle, locale selector); stories cover all variants/states of each primitive; `npm run build-storybook` passes.

## Requirements
- R1: All color usage references tokens; no hardcoded hex outside `tokens.css` (stylelint `color-no-hex` or grep gate in CI).
- R2: `StatusBadge` covers every backend status enum from the generated client (exhaustiveness type-check; a new enum value without mapping fails typecheck).
- R3: Dialogs trap focus and restore focus on close (test-asserted).
- R4: RTL: logical properties only in shared CSS; LTR+RTL covered in Storybook (visual tests consume in Task 041).
- R5: `Toast` queue caps at 3 visible and dedupes identical messages.

## Edge Cases and Error Handling
- Unknown status string at runtime → `StatusBadge` renders `neutral` variant + telemetry warning (never crashes).
- Content overflow: `Tooltip`/`Popover` clamp to viewport; `Table`/`DataGrid` horizontal scroll with sticky header.
- Reduced motion: animations disabled via media query; `ProgressBar`/`Ring` render statically.

## Security and Safety Requirements
- No `dangerouslySetInnerHTML` in primitives (lint-ban); untrusted strings render as plain text.
- `Toast`/`Alert` never render raw backend HTML; error `details` shown as pre-formatted text only.

## Testing
- Colocated tests `frontend/src/components/*/*.test.tsx` (vitest + Testing Library): render variants, keyboard interaction, focus trap, unknown-status fallback.
- Type: unit/component (vitest); run `npm run test -- src/components`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/components
cd frontend && npm run build-storybook
```

## Completion Criteria
- Tokens + all primitives + product components exist with stories; typecheck/tests/Storybook build pass; status exhaustiveness enforced at compile time.

## Traceability
- Plan B §11.2, §11.3. Depends on Task 015.

## Review Fix — Dependency Correction
- **Unblocked from 014:** tokens/primitives proceed after 015 styling baseline only; the generated API client is NOT required for this task. Remove any 014 dependency; 017 API wiring consumes 014 separately.



<!-- ===== FILE: 017-api-client-query-keys-errors.md ===== -->


# Task 017 — API Client, Query Keys, Error Normalization

## Goal
Wire the generated client with a query-key factory, auth headers, correlation IDs, and normalized errors.

## Context
All feature tasks fetch through this layer; no ad-hoc query keys or hand-rolled fetch shapes may exist elsewhere. Backend error envelope comes from Task 013; generated types from Task 014; Query defaults from Task 015.

## Starting State
Tasks 014 (generated client in `frontend/src/api/generated/`) and 015 (scaffold, `QueryProvider`) done. No `api/client`, `api/queryKeys`, or `api/errors`. Depends on Tasks 014, 015.

## Scope
Included: `api/client` (headers, token attach, correlation, idempotency), `api/queryKeys` factory + invalidation registry, `api/errors` normalization with recovery hints, GET-only retry, stale-generated-types build gate.
Excluded: feature hooks/screens, auth session logic (Task 019), telemetry event emission (Task 018).

## Instructions
1. Create `frontend/src/api/client/httpClient.ts`: wraps the generated client with base URL from `lib/env`; attaches `Authorization: Bearer <in-memory token>` via a token-provider callback (registered by Task 019; default throws `NOT_AUTHENTICATED`); sends `X-Correlation-ID` (uuid per request, propagated to telemetry); supports `Idempotency-Key` header on POST mutations (caller-supplied or generated; retries reuse the same key).
2. Create `frontend/src/api/queryKeys/keys.ts`: hierarchical factory with `all`/`lists`/`detail` scopes for `dashboard`, `projects`, `project(id)`, `workspace(id)`, `progress(id)`, `segments(id, params)`, `segment(id, segId)`, `speakers(id)`, `review(id)`, `reviewContext(id)`, `exports(id)`, `notifications`, `admin`; export `queryKeyRegistry` (event-type → key mapping consumed by Task 026 invalidation). String-literal `queryKey` outside this module is forbidden (no ad-hoc keys).
3. Create `frontend/src/api/errors/` (`kinds.ts`, `normalizeError.ts`): map backend `{code, message, correlationId, details}` envelope → `AppError {kind, code, message, correlationId, retryable, recoveryHint}` where kind ∈ `Validation | Auth | Conflict | Quota | Media | Review | Unknown`; per-code recovery hints (e.g. `QUOTA_EXCEEDED` → manage-usage link; `STALE_VERSION` → refresh-and-retry; `401` → re-login). `retryable` is true only for idempotent GETs and network errors.
4. GET-only retry: `httpClient` retries network failures + 502/503/504 on GET up to 2× with exponential backoff; never auto-retry POST/PUT/PATCH/DELETE (mutations rely on idempotency keys + explicit user retry).
5. Stale-generated-types fail build: `frontend/package.json` `prebuild` runs `make check-api-drift` (Task 014 script); feature code imports generated types only via the `api/client` barrel, never deep-imports `api/generated` (lint `no-restricted-imports`).
6. Add `frontend/src/api/hooks.ts` barrel: re-export generated hooks/client plus `useAppMutation` wrapper (attaches idempotency key + returns normalized `AppError`).

## Requirements
- R1: No ad-hoc query keys (test scans for inline `queryKey:` literals outside `api/queryKeys`).
- R2: Every request carries `X-Correlation-ID`, echoed into normalized errors and telemetry.
- R3: Token read from in-memory provider only, never `localStorage`/`sessionStorage`.
- R4: Error kinds exhaustive over backend codes; unknown code → `Unknown` with correlation ID and report hint.
- R5: POST mutations accept/reuse idempotency keys; double-submit sends one effective request.
- R6: Stale generated client fails the build via the drift gate.

## Edge Cases and Error Handling
- 401 on any request → emit `auth:expired` event (Task 019 listens); never retry 401.
- 409 `STALE_VERSION` → surfaces expected/actual versions in `details` for conflict UX.
- Offline/network failure → `Unknown` + `retryable: true` with offline recovery hint.
- Correlation ID shown in error UI ("report this ID") for support triage.

## Security and Safety Requirements
- Tokens in memory only, never logged or sent to telemetry.
- Correlation IDs are random opaque uuids (no tenant/user info).
- Error `details` rendered as text, never HTML; no media/transcript content in error telemetry.

## Testing
- Create `frontend/src/api/__tests__/queryKeys.test.ts` (factory shape/scope stability), `normalizeError.test.ts` (every backend code → kind + hint), `httpClient.test.ts` (headers, GET-only retry, idempotency-key reuse) with MSW.
- Type: unit (vitest); run `npm run test -- src/api`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/api
make check-api-drift
```

## Completion Criteria
- Client, key factory + registry, and error normalization exist; no ad-hoc keys; GET-only retry verified; drift gate fails build on stale types; api tests pass.

## Traceability
- Plan B §10.3, §10.4, §9.12. Depends on Tasks 014, 015.



<!-- ===== FILE: 018-app-shell-navigation-i18n-telemetry.md ===== -->


# Task 018 — App Shell, Navigation, i18n, Telemetry Baseline

## Goal
Implement the app shell, IA navigation, locale readiness, and safe telemetry harness all features mount into.

## Context
Tasks 019+ render inside this shell; navigation IA, i18n conventions, and telemetry allowlists frozen here prevent per-feature divergence. Consumes Task 015 scaffold, Task 016 primitives, Task 017 client.

## Starting State
Tasks 015 (scaffold, providers, layouts slots), 016 (primitives), 017 (client, errors, keys) done. No real shell, IA, i18n bundles, or telemetry harness. Depends on Tasks 015, 016, 017.

## Scope
Included: layouts + IA (top-level nav, conditional Admin, adaptive project tabs), route guards, i18n keys/plural/RTL/locale dates/timezone, telemetry harness + correlation.
Excluded: auth session logic (Task 019), feature screens, notification data wiring (Task 034), backend analytics (Task 038).

## Instructions
1. Implement layouts in `frontend/src/app/layouts/`: `AppShell.tsx` (header: product nav, notification bell with unread-badge slot, user menu, locale/theme switchers; sidebar/main; footer with `VITE_APP_VERSION`), `ProjectLayout.tsx` (project header + tabs outlet).
2. Implement IA: top-level `Dashboard`, `Projects`, `Review`, `Notifications`, `Settings` + conditional `Admin` (rendered only when `/me` permissions include `admin:read`; route-guarded in `router.tsx` too — never CSS-only hiding). Project tabs `Overview, Media, Transcript, Translation, Voices, Timeline, Quality, Exports, Activity` via `frontend/src/app/navigation/projectTabs.ts` tab model derived from workspace state (e.g. `Translation` disabled until transcript exists, `Exports` badged when ready) — never hardcoded per page.
3. Implement i18n in `frontend/src/i18n/`: `en` baseline `*.json` namespaced (`common, nav, auth, dashboard, projects, errors, ...`), `i18n.ts` init (react-i18next, ICU/plural support, `en` fallback), `useLocale.ts` + locale-aware `formatDate`/`formatNumber` (`Intl`, tenant timezone from preferences — Task 035 reads it — defaulting to browser tz), RTL via `document.dir` switch (shared CSS already logical-properties-only from Task 016).
4. Implement telemetry in `frontend/src/telemetry/`: `telemetry.ts` harness (`trackPageView(route)`, `trackRouteChange`, `trackApiFailure({route, code, correlationId, latencyMs})`), `correlation.ts` (request → error → telemetry correlation-ID propagation), `TelemetryProvider` with `VITE_TELEMETRY_ENABLED` kill-switch + persisted opt-out; allowlist-based payloads only — never tokens, URLs (path only, query stripped), media bytes/URLs, or transcript/translation text.
5. Add route guards in `frontend/src/app/guards/`: `RequireAuth` (waits for pre-shell `/me` resolution from Task 019; shell skeleton meanwhile), `RequireAdmin` (permission-gated, redirects to 403).

## Requirements
- R1: Admin nav + route unreachable without `admin:read` (test asserts absence, not hiding).
- R2: All shell/nav strings via i18n keys (test scans shell components for raw JSX string literals).
- R3: Telemetry payloads constrained by allowlist type + test (forbidden fields throw/strip).
- R4: Dates/numbers respect tenant timezone/locale; unknown timezone → UTC fallback.
- R5: Project tab model derives from workspace state; state-gated tabs disabled with reason tooltip.

## Edge Cases and Error Handling
- Locale bundle missing → `en` fallback + telemetry warning (never blank strings).
- Telemetry endpoint down → drop events silently (never block UI, never retry-loop).
- Permissions load failure → render non-admin shell (fail closed on Admin).

## Security and Safety Requirements
- Telemetry never contains tokens, URL queries, media, transcript/translation text, or PII (email hashed or omitted).
- Correlation IDs random; admin gating is UX defense-in-depth (server authoritative per Tasks 013/036).

## Testing
- Create `frontend/src/app/layouts/__tests__/shell.test.tsx` (nav render, admin absent without permission, tab model states), `frontend/src/i18n/__tests__/i18n.test.ts` (plural rules incl. Arabic/Russian samples, RTL switch, `en` fallback), `frontend/src/telemetry/__tests__/telemetry.test.ts` (allowlist enforcement, kill-switch, query-stripping).
- Playwright `@shell`: login → shell renders nav; admin hidden for non-admin.
- Type: unit (vitest) + Playwright.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/app src/i18n src/telemetry
npx playwright test --grep="@shell"
```

## Completion Criteria
- Shell + adaptive IA + guards, i18n with plural/RTL/timezone, and allowlisted telemetry exist; admin fail-closed; shell/i18n/telemetry tests + `@shell` smoke pass.

## Traceability
- Plan B §10.9, §11.1, §11.4, §11.5, §14.2, §14.3, §14.4. Depends on Tasks 015, 016, 017.



<!-- ===== FILE: 019-auth-session-ux.md ===== -->


# Task 019 — Authentication and Session UX

## Goal
Implement login/logout/restore/expiry with pre-shell `/me` resolution and in-memory tokens.

## Context
Backend auth endpoints (`POST /api/v1/auth/login|refresh|logout`, `GET /me`) come from Task 006; shell slots (`RequireAuth`, guards) from Task 018. This is the first feature mounted in the shell and sets the session pattern every later task relies on.

## Starting State
Task 006 (auth/me endpoints) and Task 018 (shell, `RequireAuth` slot, i18n, telemetry) done. No `features/auth`. Depends on Tasks 006, 018.

## Scope
Included: login/logout/restore/expiry pages + store + hooks, silent refresh, failed-refresh → login preserving destination, 401/403 states, in-memory access token, token-provider registration for Task 017 client.
Excluded: preferences screens (Task 035), admin gating internals (Task 036), backend auth changes.

## Instructions
1. Create `frontend/src/features/auth/`: `api.ts` (login/refresh/logout/me via Task 017 client), `authStore.ts` (zustand: `{accessToken` — in-memory only, `user, status: unknown | authenticated | expired | error}`), `useSession.ts`, `RequireAuth.tsx` wiring, pages `LoginPage.tsx`/`LoggedOutPage.tsx`, `SessionExpiredDialog.tsx`, `ForbiddenPage.tsx`.
2. Pre-shell `/me` resolution: `useSession` restores on app boot (`authStore.restore()`: refresh → me); `RequireAuth` blocks shell until `status !== 'unknown'` (skeleton meanwhile); no feature query fires before auth resolves (queries use `enabled: isAuthenticated`).
3. Silent refresh: schedule at 80% token lifetime; concurrent requests share a single refresh promise (no refresh storms); failed refresh → `status: 'expired'` → redirect `/login?next=<destination>`; post-login navigates back to destination.
4. 401/403 states: 401 on an active session → single re-resolve, then login flow; 403 → `ForbiddenPage` with request-access hint (never retry-loop); handle the `auth:expired` event emitted by the Task 017 client globally.
5. Logout: `POST /logout` + clear in-memory token + `queryClient.clear()` + reset stores, then navigate; back-button after logout never reveals cached data (cache cleared before nav).
6. Register the in-memory token provider + `auth:expired` handler with the Task 017 client at app boot (`frontend/src/app/providers/` wiring).

## Requirements
- R1: Access token in memory only (test spies `localStorage`/`sessionStorage` for token writes).
- R2: Destination preserved across expiry (`?next=`) and restored post-login.
- R3: Single in-flight refresh shared by concurrent callers.
- R4: Query cache + stores cleared on logout before navigation.
- R5: Shell never renders for unauthenticated users (pre-shell gate blocks).

## Edge Cases and Error Handling
- Refresh while offline → one retry on reconnect, else `expired`.
- Multi-tab: `storage`-event `auth:logout` broadcast logs out other tabs.
- Expired deep link → login → original destination restored.
- Login submit double-click → single request (button disabled while pending).

## Security and Safety Requirements
- No token in logs/telemetry/URLs; `httpOnly` refresh cookie never touched by JS.
- Login errors generic (no user-enumeration); session expiry audited backend-side (Task 006).
- 403 page reveals nothing about resource existence beyond the denial.

## Testing
- Create `frontend/src/features/auth/__tests__/`: store transitions, refresh coalescing, destination preserve, storage-spy no-persist assertion, 401/403 mapping.
- Playwright `@auth`: login happy path, expiry → login → return to destination, logout clears data, forbidden page.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/auth`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/auth
npx playwright test --grep="@auth"
```

## Completion Criteria
- Login/logout/restore/expiry work with destination preservation; token never persisted; single-refresh coalescing verified; auth tests + `@auth` E2E pass.

## Traceability
- Plan B §12.1, §13.3. Depends on Tasks 006, 018.



<!-- ===== FILE: 020-dashboard.md ===== -->


# Task 020 — Dashboard

## Goal
Implement the dashboard summary with drill-down into projects, outputs, quota, and warnings.

## Context
First landing screen after login; data from `GET /dashboard/summary` (Task 007) via `queryKeys.dashboard` (Task 017), mounted in the Task 018 shell. Must handle empty tenants and partial backend failures gracefully.

## Starting State
Task 007 (dashboard summary endpoint) and Task 018 (shell, IA, telemetry) done. No `features/dashboard`. Depends on Tasks 007, 018.

## Scope
Included: summary cards (counts, recent outputs, storage/cost, quota, provider warnings, backlog) + drill-down links, loading/empty-tenant/partial/quota-warning/error states.
Excluded: project list (Task 021), workspace (Task 025), admin diagnostics (Task 036).

## Instructions
1. Create `frontend/src/features/dashboard/`: `api.ts` (`useDashboardSummary` on `queryKeys.dashboard`), `DashboardPage.tsx`, components `StatCard`, `RecentOutputs`, `StorageCostCard`, `QuotaCard`, `ProviderWarnings`, `BacklogCard`.
2. Summary content: counts (active / in-review / failed / completed), recent outputs (deep-links to project/output), storage + cost totals, quota usage vs limit, provider warnings (degraded/unhealthy), review backlog count; every card drill-down navigates to the filtered list/workspace view.
3. States: loading skeletons; empty-tenant (zero projects → onboarding CTA routing to the creation wizard); partial (per-card error with retry, rest renders); quota-warning (≥80% meter + manage link); global error (`ErrorState` + retry).

## Requirements
- R1: Data only from `GET /dashboard/summary` (+ drill-down links); no extra aggregate calls.
- R2: Quota ≥80% shows warning meter; 100%/exceeded blocks costly actions with guidance.
- R3: Partial failure isolates per card; one failed section never blanks the page.
- R4: Empty-tenant CTA routes to the project creation wizard (Task 022).

## Edge Cases and Error Handling
- All-zero tenant → empty-tenant onboarding, not zero-cards grid.
- Stale cache (304) → render stale + background refresh indicator.
- Provider warnings empty → section hidden, not an empty card.
- Cost formatted in tenant currency/locale (Task 018 formatters).

## Security and Safety Requirements
- Cost/quota cards render only with permission; hidden without permission (no error flash leaking existence).
- No per-user data of other members displayed.

## Testing
- Create `frontend/src/features/dashboard/__tests__/`: summary render, drill-down links, empty-tenant CTA, partial-state isolation (MSW error injection per section), quota-warning threshold.
- Playwright `@dashboard`: counts render, drill-down navigation, empty-tenant CTA, partial state.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/dashboard`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/dashboard
npx playwright test --grep="@dashboard"
```

## Completion Criteria
- Dashboard renders all summary sections with drill-down; empty/partial/quota/error states verified; dashboard tests + `@dashboard` E2E pass.

## Traceability
- Plan B §12.2. Depends on Tasks 007, 018.



<!-- ===== FILE: 021-project-list.md ===== -->


# Task 021 — Project List

## Goal
Implement the filterable, paginated project list with valid-actions-only row operations.

## Context
Primary project navigation; data from project endpoints (Task 007) via `queryKeys.projects` (Task 017), mounted in the Task 018 shell. Row actions must reflect backend-allowed actions only — never show operations the server would reject.

## Starting State
Task 007 (project CRUD/archive endpoints with `actions[]` allowlist) and Task 018 (shell, IA) done. `features/projects` does not exist yet. Depends on Tasks 007, 018.

## Scope
Included: columns, filters, server-side sort/pagination, URL-synced state, valid-actions-only row actions (open/cancel/retry/export/delete/archive).
Excluded: creation wizard (Task 022), workspace (Task 025), export download UX (Task 033).

## Instructions
1. Create `frontend/src/features/projects/`: `ProjectsPage.tsx`, `ProjectTable.tsx` (on Task 016 `DataGrid`), `ProjectFilters.tsx`, `useProjectsQuery.ts` (server-side pagination/sort/filter via `queryKeys.projects`; state synced to URL search params for shareable links).
2. Columns: name, media (source badge + duration), target language, status (`StatusBadge`), progress (approximate %), review (open count), created, activity (relative time); row click → workspace.
3. Filters: status, target language, review state, archived toggle (excluded by default), owner, created-date range; sort by created/activity/name/progress; page-size selector; preserve scroll on page change.
4. Valid-actions-only: per-row actions derived strictly from backend `actions[]` — `open/cancel/retry/export/delete/archive(/unarchive)` rendered only when allowed, never shown-disabled; destructive actions (delete/cancel) behind `ConfirmDialog` (+ typed name confirmation for delete, reason where required).

## Requirements
- R1: Filters/sort/page synced to URL (deep-linkable, back-button safe).
- R2: Pagination and sorting server-side; no client-side slicing of server pages.
- R3: Actions rendered strictly from backend `actions[]` allowlist (test asserts disallowed action absent from DOM).
- R4: Archived projects excluded by default; included only with archived filter on.

## Edge Cases and Error Handling
- Empty filter result → `EmptyState` + clear-filters action (not a blank table).
- Project deleted mid-page → row removed + toast (no full-page error).
- Sort on progress uses approximate ordering (note in column tooltip, consistent with no-fake-precision rule).

## Security and Safety Requirements
- Client-side permission check before showing actions, but server authoritative: 403 → toast + row refresh.
- Delete requires typed name confirmation + audit reason; cancel requires confirmation.

## Testing
- Create `frontend/src/features/projects/__tests__/`: URL sync round-trip, action-allowlist rendering, server-side pagination params, archived-default exclusion.
- Playwright `@projects`: filter/paginate/navigate, valid-actions-only behavior, delete confirmation.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/projects`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/projects
npx playwright test --grep="@projects"
```

## Completion Criteria
- List renders all columns with URL-synced server-side filter/sort/pagination; actions strictly allowlisted with confirmations; projects tests + `@projects` E2E pass.

## Traceability
- Plan B §12.3. Depends on Tasks 007, 018.



<!-- ===== FILE: 022-project-creation-wizard.md ===== -->


# Task 022 — Project Creation Wizard

## Goal
Implement the creation wizard with progressive disclosure, immutable target language, and config-hash transparency.

## Context
Entry point for new dubbing work; creates projects via Task 007 endpoints, embeds the Task 023 uploader (or defers upload), and hands off to preflight (Task 024). Users must never be asked about providers, queues, or models.

## Starting State
Task 007 (project creation endpoint with server validation + config hash) and Task 018 (shell, i18n) done. `features/projects` list exists (Task 021); no wizard. Depends on Tasks 007, 018.

## Scope
Included: wizard steps Create→Language→Settings→Upload→Review→Start, progressive disclosure, client+server validation, config-hash display, draft persistence.
Excluded: upload engine internals (Task 023), processing preflight (Task 024), post-creation editing.

## Instructions
1. Create `frontend/src/features/projects/wizard/`: `CreateWizard.tsx` stepper, steps `BasicsStep` (name required, description) → `LanguageStep` (source auto-detect note + target language select) → `SettingsStep` → `UploadStep` (embeds Task 023 uploader or "upload later" skip) → `ReviewStep` → Start; `wizardStore.ts` (zustand, draft persisted to `localStorage`, cleared on submit/discard).
2. Progressive disclosure in `SettingsStep`: collapsed Advanced sections for source-separation policy, output profile, timing strictness, voice policy, glossary editor (`sourceTerm/targetTerm/notes`), style instructions, review threshold; sensible defaults preselected, each with plain-language i18n help text.
3. Target language immutable: `LanguageStep` warns it "cannot be changed after creation"; `ReviewStep` repeats the notice; no edit-after-create UI for target language may exist.
4. Validation: name required (client zod + server 400 mapped via Task 017 `Validation` kind to inline field errors); Start → `POST /projects` → navigate to workspace (or upload-first state when upload deferred).
5. Config-hash transparency: `ReviewStep` shows `settingsVersion` + config hash preview + human-readable summary of chosen settings. Never ask provider/queue/model: no such fields exist (grep-gated).

## Requirements
- R1: Name required, enforced client- and server-side with field-level errors; draft intact on failure.
- R2: Target language uneditable post-creation (no UI path to change it).
- R3: Draft survives refresh until submit/discard (file objects excluded — re-attach after refresh).
- R4: `ReviewStep` displays settings version + config hash + human summary.
- R5: No provider/model/queue inputs exist anywhere in the wizard (grep-gate test).

## Edge Cases and Error Handling
- Session expiry mid-wizard → login → draft preserved → resume at `ReviewStep`.
- Duplicate name allowed (no uniqueness constraint) — show created-date disambiguation hint.
- Server 400 → field-level errors mapped per Task 017 kinds; wizard state preserved.

## Security and Safety Requirements
- Draft in `localStorage` holds only non-sensitive settings (no tokens, no media bytes).
- Server validation authoritative; client validation is UX-only.

## Testing
- Create `frontend/src/features/projects/wizard/__tests__/`: step flow, validation errors, draft persistence round-trip, immutable-language notice, provider-field absence gate.
- Playwright `@project-create`: full wizard → workspace; validation errors; upload-later path.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/projects`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/projects
npx playwright test --grep="@project-create"
```

## Completion Criteria
- Wizard completes Create→Start with progressive disclosure, immutable-language rule, and config-hash review; draft persistence verified; wizard tests + `@project-create` E2E pass.

## Traceability
- Plan B §12.4. Depends on Tasks 007, 018.



<!-- ===== FILE: 023-resumable-upload-ux.md ===== -->


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



<!-- ===== FILE: 024-processing-start-preflight.md ===== -->


# Task 024 — Processing Start and Preflight

## Goal
Implement cost/quota/consent preflight with explicit confirmation before any costly run starts.

## Context
Gate between a ready project (Task 023 media ready) and the processing pipeline (Task 008 endpoints, Task 025 workspace). Estimates are labeled estimates — never promises — and voice-cloning consent must be acknowledged before start.

## Starting State
Task 008 (processing endpoints with estimate + idempotency + 409 conflict) and Task 018 (shell, dialogs, i18n) done. No `features/processing`. Depends on Tasks 008, 018.

## Scope
Included: preflight dialog (estimate, quota, cloning warnings), explicit costly-op confirmation, 202 → workspace navigation, conflict-action disabling.
Excluded: workspace shell (Task 025), live progress (Task 026), cost accounting display (Task 035).

## Instructions
1. Create `frontend/src/features/processing/`: `PreflightDialog.tsx`, `usePreflight.ts` (estimate fetch), `CostEstimateCard.tsx`.
2. Preflight dialog on Start processing: cost/time estimate labeled "estimate", quota impact (remaining after run), per-cloned-voice consent warnings (explicit acknowledge checkbox covering all listed voices), configuration summary hash.
3. Explicit confirmation: confirm button disabled until estimate loaded + consent acknowledged (+ override reason entered when over quota); button label states cost ("Start run — est. $X").
4. 202 → navigate to workspace (`/projects/:id?started=1`) with "Run started" toast; 409 active-run conflict → dialog switches to conflict state: start actions disabled with explanation + link to workspace to cancel first (conflict-action disabling, not hiding).
5. Send idempotency key on start (Task 017 `useAppMutation`); double-click submits once.

## Requirements
- R1: No processing start without dialog confirmation (no direct-start code path).
- R2: Estimate always labeled "estimate" adjacent to the figure (test asserts label).
- R3: Cloning warnings block start until acknowledged (button stays disabled).
- R4: 202 navigates to workspace with confirmation toast.
- R5: Conflicting actions disabled with reason in conflict state (never silently enabled).

## Edge Cases and Error Handling
- Estimate fetch fails → start blocked with "estimate unavailable" + retry (never start blind on a costly op).
- Quota exceeded → start blocked with upgrade/manage path.
- Double-click Start → single effective run via idempotency key.

## Security and Safety Requirements
- Cost figures display-only, never editable client-side.
- Consent acknowledgement ships timestamp + user to backend; server re-validates (Task 008) — no devtools bypass.

## Testing
- Create `frontend/src/features/processing/__tests__/`: confirmation gating (button enablement matrix), estimate labeling, conflict-state disabling, idempotent double-submit (MSW).
- Playwright `@processing-start`: dialog flow → workspace nav; conflict state; quota-exceeded block.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/processing`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/processing
npx playwright test --grep="@processing-start"
```

## Completion Criteria
- Preflight dialog with labeled estimate, quota, and consent gating works; 202 → workspace; conflicts disable actions with explanation; processing-start tests + `@processing-start` E2E pass.

## Traceability
- Plan B §12.6, §12.18. Depends on Tasks 008, 018.



<!-- ===== FILE: 025-project-workspace-shell.md ===== -->


# Task 025 — Project Workspace Shell

## Goal
Implement the aggregated workspace shell fed by a single workspace read model, with no N+1 fetching.

## Context
Post-start home for a project; workspace aggregate endpoint from Task 008, live updates from Task 026, mounted in Task 018 project tabs. Phase projection is UI-only presentation of aggregate stage data — no client-side pipeline simulation, no fake ETAs.

## Starting State
Task 008 (workspace aggregate endpoint) and Task 018 (shell, `ProjectLayout`, tabs) done. `features/processing` has preflight (Task 024); no workspace page. Depends on Tasks 008, 018.

## Scope
Included: workspace page (header, `PipelineStepper`/`StageProgress`, review/warnings/output main column, media/config/run/cost/activity secondary column), UI-only phase projection with parallelism note, state-adaptive panels.
Excluded: live streaming (Task 026), transcript/translation editors (Tasks 027–028), review studio (Task 031), QC/output details (Tasks 032–033).

## Instructions
1. Create `frontend/src/features/processing/WorkspacePage.tsx` (+ `workspaceStore.ts` holding UI-only state: selected tab, panel sizes) fed by a single `useWorkspace(id)` on `queryKeys.workspace`; child components receive slices via props — no per-panel fetching (MSW test asserts one workspace request per refresh: no N+1).
2. Header: project name, `StatusBadge`, approximate progress %, valid-actions-only buttons from aggregate `actions[]` (open/cancel/retry/export/delete/archive).
3. `PipelineStepper` + `StageProgress`: stages from aggregate with per-stage state; parallel stages annotated with a "runs in parallel" note (UI-only phase projection of aggregate data); show elapsed time only where backend provides it — no ETA countdowns or time predictions anywhere.
4. Main column: review-queue summary, warnings, output readiness with deep links to Review/Quality/Exports tabs; secondary column: media info, config summary (+ hash, never secrets), run history, cost, activity excerpt (full feed lives in the Activity tab).

## Requirements
- R1: One workspace request per refresh (no N+1; test asserts single network call).
- R2: No ETA text anywhere in workspace UI (grep-gate test for `ETA|estimated time|remaining`).
- R3: Parallel stages carry the parallelism note; sequential stages do not.
- R4: Panels adapt to project state (pre-run hides progress, failed shows recovery actions, terminal states stop polling hooks).

## Edge Cases and Error Handling
- Workspace 404 (deleted) → redirect to list + toast.
- Aggregate version skew → stale banner + refresh action.
- Empty run history → `EmptyState` (not an empty table).

## Security and Safety Requirements
- Config panel shows hash + summary only, never secrets or internal paths.
- Cost visible per permission; hidden otherwise without error flash.

## Testing
- Create `frontend/src/features/processing/__tests__/workspace.test.tsx`: single-request assertion, stepper states from fixture aggregates, no-ETA text scan, state-adaptive panels, 404 redirect.
- Playwright `@workspace`: stepper renders, deep links navigate, failed-state recovery visible.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/processing`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/processing
npx playwright test --grep="@workspace"
```

## Completion Criteria
- Workspace renders from one aggregate request with stepper, main/secondary columns, parallelism notes, and zero ETA text; workspace tests + `@workspace` E2E pass.

## Traceability
- Plan B §12.7. Depends on Tasks 008, 018.



<!-- ===== FILE: 026-live-progress-sse-polling.md ===== -->


# Task 026 — Live Progress via SSE with Polling Fallback

## Goal
Implement authenticated streaming progress as invalidation hints with adaptive polling fallback and no local pipeline mirror.

## Context
Live updates for the Task 025 workspace; stream contract (envelope + event types) frozen in Task 013, progress endpoints in Task 008, invalidation registry in Task 017. Events hint which queries to refetch — the client never mirrors pipeline state locally.

## Starting State
Tasks 008 (progress/stream endpoints), 013 (SSE envelope + event types + payload allowlist), 025 (workspace consuming `queryKeys.workspace`) done. No streaming hook. Depends on Tasks 008, 013, 025.

## Scope
Included: `useProgressStream` (auth fetch-streaming, backoff reconnect, cursor catch-up, dup/stale tolerance, invalidation), adaptive polling fallback, approximate-% display rule, notification-badge invalidation.
Excluded: workspace UI (Task 025), notification center (Task 034), backend SSE emission.

## Instructions
1. Create `frontend/src/hooks/useProgressStream.ts`: authenticated `fetch` streaming reader on `GET .../progress/stream` (token via Task 017 provider, header only — never query param); cursor via `Last-Event-ID` for catch-up; parse SSE envelope per Task 013 frozen contract; unknown event types logged + ignored (forward-compatible, never crash).
2. Reconnect with exponential backoff + jitter (max 30s); resume from cursor on reconnect; dedupe by event id; ignore stale/out-of-order events (older cursor).
3. Treat events as invalidation hints only: map event type → `queryClient.invalidateQueries(queryKeyRegistry.*)` (workspace/progress/notifications); no local pipeline DB/mirror — no stage state cached outside the Query cache (test asserts events produce only invalidations, no store writes). Coalesce rapid bursts (debounce 500ms).
4. Adaptive polling fallback `useProgressPoll`: 2–5s when tab visible + run active, 15–30s when hidden/background, stops at terminal states; takes over after N failed SSE retries (and yields back on stream recovery). Progress % displayed as approximate only ("~%" + "approximate" note where shown, coordinated with Task 025).
5. Completion/failure events invalidate `queryKeys.notifications` so the Task 018 shell badge updates; 401 on stream → Task 019 expiry flow.

## Requirements
- R1: No local pipeline state mirror (events → invalidations only; test asserts no store writes).
- R2: Reconnect resumes via cursor with no missed-event gaps (dedupe + catch-up tested).
- R3: Polling stops at terminal states; hidden tabs use slow cadence (fake-timer test).
- R4: Unknown event types never crash (ignore + warn).
- R5: Stream authenticated via header; 401 routes to the session-expiry flow.

## Edge Cases and Error Handling
- Tab hidden → close stream, rely on slow poll; visible again → reconnect with cursor.
- Stream 404 (run gone) → stop + invalidate workspace (no retry loop).
- Clock skew irrelevant (cursor-based, never timestamp-based).
- Event payload failing generated-type validation → ignored + telemetry warning.

## Security and Safety Requirements
- No token in stream URL; header only.
- Event payloads untrusted: validated against generated SSE types before use; transcript/media content never expected — ignored + warned if present.

## Testing
- Create `frontend/src/hooks/useProgressStream/__tests__/`: backoff sequence, cursor catch-up, dedupe/stale tolerance, event→invalidation map, unknown-type tolerance, polling cadence with fake timers, no-store-write assertion.
- Playwright `@progress`: live bar moves on streamed events, completion updates notification badge, polling fallback engages when stream blocked.
- Type: unit (vitest) + Playwright; run `npm run test -- src/hooks/useProgressStream`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/hooks/useProgressStream
npx playwright test --grep="@progress"
```

## Completion Criteria
- Streaming progress with cursor reconnect + dedupe drives Query invalidation only; adaptive polling covers stream outages; approximate-% rule honored; hook tests + `@progress` E2E pass.

## Traceability
- Plan B §10.5, §12.8, §9.11. Depends on Tasks 008, 013, 025.



<!-- ===== FILE: 027-transcript-workspace.md ===== -->


# Task 027 — Transcript Workspace

## Goal
Implement the transcript editing workspace with version-aware segment editing and player-synced navigation.

## Context
Source-of-truth transcript review for a project; segment read/edit/version endpoints from Task 009, workspace shell from Task 025, live invalidation from Task 026, mounted in the Task 018 project tabs. Candidates are immutable snapshots — edits always create manual versions, never mutate history.

## Starting State
Tasks 009 (segment editing API: select-version, manual-version with expected-version + 409), 025 (workspace shell), 026 (`useProgressStream` invalidation) done. No `features/transcript`. Depends on Tasks 009, 026.

## Scope
Included: `TranscriptEditor` + `SegmentRow`, three-pane player+waveform/list/inspector layout, seek-on-select, active highlight + autoscroll, select-version vs create-manual-version flows, original/selected/manual display, virtualized list.
Excluded: translation editing (Task 028), media player internals + waveform rendering (Task 030), review actions (Task 031).

## Instructions
1. Create `frontend/src/features/transcript/TranscriptEditor.tsx`: three-pane layout — left player+waveform slot (embeds Task 030 `MediaPlayer` in compact mode), center virtualized segment list, right inspector. Fed by `useTranscript(projectId)` on `queryKeys.transcript`; invalidated by Task 026 progress events.
2. Create `frontend/src/features/transcript/SegmentRow.tsx`: row shows timestamp, speaker label, editable text, confidence badge, review flag, inline playback button; selected/active states distinct; keyboard-navigable (up/down moves selection, Enter seeks).
3. Implement seek-on-select: selecting a row seeks the shared player to segment start (via Task 030 player ref/store); active segment highlights during playback with autoscroll (toggleable, pauses on manual scroll, resumes on re-enable).
4. Create `frontend/src/features/transcript/Inspector.tsx`: shows selected segment detail — original text, currently-selected version text, manual draft editor, advanced disclosure (provider/model/version per version); version history list with select-version action.
5. Implement select-version (`POST .../select-version` with `expectedVersion`): on 409 conflict → stale banner + refetch + discard pending selection (no silent overwrite). Implement create-manual-version (`POST .../manual-version` with `expectedVersion` + edited text): optimistic draft state, error rollback, success invalidates `queryKeys.transcript`.
6. Virtualize the list (`frontend/src/features/transcript/VirtualizedSegmentList.tsx`): windowing for 1000+ segments; memoized rows; debounced search/filter by speaker/text/review-flag; never fetch per-row (single transcript query, slices via props).
7. Display rule: every segment shows original vs selected vs manual lineage explicitly (badges: `original`, `selected vN`, `manual`); unreviewed/low-confidence segments carry a review flag that links to Task 031.

## Requirements
- R1: Edits never mutate history — manual versions are new versions only (test asserts no PUT/patch on version endpoints).
- R2: Every mutating call sends `expectedVersion`; 409 always triggers refetch + banner, never silent overwrite.
- R3: Selecting a row seeks the player to segment start (ms-accurate within one frame interval).
- R4: Active-segment highlight tracks playback with autoscroll that yields to manual scrolling.
- R5: Original/selected/manual lineage visible per segment at all times.
- R6: List renders 1000+ segments without frame drops (virtualized;Memoized rows).

## Edge Cases and Error Handling
- 409 on select/manual → stale banner + refresh action; pending edit preserved as draft, never auto-resubmitted.
- Transcript empty (pre-run) → `EmptyState` with link to processing tab, not an empty list.
- Segment deleted server-side → row removed on refetch + toast; selection moves to nearest surviving segment.
- Playback position beyond transcript range → highlight cleared, no crash.

## Security and Safety Requirements
- Version history shows provider/model metadata only — never secrets, tokens, or internal paths.
- Manual text sanitized for display (no raw HTML injection); audit attribution uses session user only.

## Testing
- Create `frontend/src/features/transcript/__tests__/transcript.test.tsx`: select-version success + 409 refresh, manual-version create + rollback, seek-on-select wiring, lineage badges, virtualization smoke (1000-row fixture), filter behavior.
- Playwright `@transcript`: editor renders three panes, row select seeks player, highlight follows playback, 409 shows stale banner.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/transcript`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/transcript
npx playwright test --grep="@transcript"
```

## Completion Criteria
- Transcript workspace edits via versioned flows only, with seek-synced player, highlight + autoscroll, lineage display, and virtualized list; transcript tests + `@transcript` E2E pass.

## Traceability
- Plan B §12.9. Depends on Tasks 009, 026.



<!-- ===== FILE: 028-translation-workspace.md ===== -->


# Task 028 — Translation Workspace

## Goal
Implement the side-by-side translation review workspace with immutable candidates and guarded manual editing.

## Context
Per-segment translation selection for the dub; translation endpoints from Task 009 (candidates immutable, select-or-edit-as-manual with expected-version + 409), transcript rows from Task 027, mounted in the Task 018 project tabs. Candidates are never edited in place — selection or manual edit only.

## Starting State
Tasks 009 (translation select/edit API), 027 (transcript workspace patterns: versioned edits, 409→refresh, lineage display) done. No `features/translation`. Depends on Tasks 009, 027.

## Scope
Included: side-by-side source/selected/alternatives view, `TranslationEditor`, candidate immutability, select-or-edit-as-manual, navigate-away save/discard/cancel guard, conflict→refresh.
Excluded: transcript text editing (Task 027), voice assignment (Task 029), review queue actions (Task 031).

## Instructions
1. Create `frontend/src/features/translation/TranslationWorkspace.tsx`: per-segment side-by-side layout — source text (read-only, from selected transcript version) | selected translation (editable draft) | alternatives list; header row per segment shows speaker, time window, duration, sync status, glossary hits, assigned voice.
2. Create `frontend/src/features/translation/TranslationEditor.tsx`: draft editor for the selected translation with glossary-term highlighting, duration/sync indicator (over/under target window, read-only display), character/reading-speed hint; save creates a manual version, never mutates a candidate.
3. Render candidates from `useTranslations(projectId)` on `queryKeys.translations` as immutable cards (provider/model/version badge, text, score where provided); each card offers Select only — no inline edit affordance on candidates (test asserts candidates render without editable inputs).
4. Implement select (`POST .../translations/select` with `expectedVersion`) and edit-as-manual (`POST .../translations/manual` with `expectedVersion` + text): 409 conflict → stale banner + refetch + preserved draft; success invalidates `queryKeys.translations` (+ Task 026 event wiring).
5. Implement navigate-away guard (`frontend/src/features/translation/useDirtyGuard.ts`): dirty draft blocks route/tab change with save/discard/cancel dialog; save → manual-version flow, discard → reset draft, cancel → stay.
6. Sync/glossary/voice strip per segment: sync badge (in-window/overflow), glossary matches with tooltip definitions, assigned-voice chip linking to Task 029; missing data renders as `—` with explanatory tooltip, never blank.

## Requirements
- R1: Candidates immutable — no edit affordance on candidate cards (test asserts read-only rendering).
- R2: Every select/manual call sends `expectedVersion`; 409 triggers refetch + banner, never silent overwrite.
- R3: Dirty draft always triggers save/discard/cancel on navigate-away (route, tab, or segment change).
- R4: Source text shown is the currently-selected transcript version, with version label.
- R5: Sync/duration display is read-only (no manual timing edits; timing lives in Task 030 as read-only).

## Edge Cases and Error Handling
- 409 on select/manual → stale banner + refresh; draft preserved, never auto-resubmitted.
- No candidates yet (translation pending) → `EmptyState` with progress link, not an empty editor.
- Glossary term missing definition → term highlighted without tooltip, no crash.
- Source transcript version changes mid-edit → dirty guard fires with source-changed notice + rebase option.

## Security and Safety Requirements
- Candidate provider/model metadata displayed only; no secrets, keys, or internal paths.
- Draft text sanitized on display; no raw HTML rendering of source or candidate content.

## Testing
- Create `frontend/src/features/translation/__tests__/translation.test.tsx`: candidate immutability (no inputs), select + 409 refresh, manual edit + rollback, dirty-guard dialog paths (save/discard/cancel), source-version label.
- Playwright `@translation`: side-by-side renders, select updates selected card, dirty guard blocks navigation, conflict shows banner.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/translation`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/translation
npx playwright test --grep="@translation"
```

## Completion Criteria
- Translation workspace selects or manually versions immutable candidates side-by-side with source, guarded by dirty-check and conflict refresh; translation tests + `@translation` E2E pass.

## Traceability
- Plan B §12.10. Depends on Tasks 009, 027.

## Review Fix — Dependency Correction
- **Parallel with 027:** this task depends on 009 + shared editor primitives only — NOT on 027 transcript UI. 027 and 028 may execute in parallel.



<!-- ===== FILE: 029-voice-assignment-preview.md ===== -->


# Task 029 — Voice Assignment and Preview

## Goal
Implement speaker-to-voice assignment with compatible-only selection, consent gating, and preview playback.

## Context
Voice casting for dub speakers; speaker/voice endpoints from Task 010 (compatible voices, assign/replace/reset, consent states, signed-URL preview), surfaced in translation (Task 028 speaker chips) and review context (Task 031). Backend compatibility is authoritative — the client never invents eligibility.

## Starting State
Task 010 (speakers/voices API: compatibility, assignment, consent, preview URLs) done. No `features/voices`. Depends on Task 010.

## Scope
Included: speaker list with voice state, `VoiceSelector` (preview/select/replace/reset), compatible-only display, impact pre-confirm, consent-state gating, signed-URL preview playback with quota/consent errors.
Excluded: voice model provisioning (backend), translation text editing (Task 028), timeline audio rendering (Task 030).

## Instructions
1. Create `frontend/src/features/voices/SpeakerList.tsx`: per-speaker row — segment count, appearance count/time, current voice + provider/type chips, consent badge; fed by `useSpeakers(projectId)` on `queryKeys.speakers`.
2. Create `frontend/src/features/voices/VoiceSelector.tsx`: compatible-voices dropdown/list from the backend compatibility response only — incompatible voices never rendered (not disabled-rendered, not rendered); select/replace/reset actions call Task 010 endpoints; reset restores backend default with confirm.
3. Implement impact pre-confirm (`frontend/src/features/voices/ImpactDialog.tsx`): before assign/replace, show affected segment count, downstream invalidation note (translations/audio re-render), and cost note where backend provides it; confirm → mutate, cancel → no-op.
4. Implement consent gating: states `unavailable` / `consent-required` / `valid` / `revoked` per voice; non-`valid` voices blocked from assignment with the tenant-policy message from the API shown verbatim; revoked mid-session → assignment button disabled + banner on refetch.
5. Implement preview playback (`frontend/src/features/voices/PreviewPlayer.tsx`): plays the signed preview URL in an `<audio>` element; handles expiry (refetch URL once), quota errors (429 → friendly retry-later message), consent errors (403 → tenant-policy message); never persists the URL.
6. Wire success/error to cache: assignment success invalidates `queryKeys.speakers` (+ translations/audio where Task 026 events indicate); all errors surface via the Task 017 structured-error path (no ad-hoc toasts).

## Requirements
- R1: Only backend-reported compatible voices displayed (test asserts incompatible voice absent from DOM).
- R2: Impact dialog (segments/invalidation/cost) precedes every assign/replace; cancel performs no mutation.
- R3: Consent states enforced client-side with the backend tenant-policy message; non-valid voices unassignable.
- R4: Preview uses short-lived signed URLs only; expired URLs refetched, never cached or logged.
- R5: Quota (429) and consent (403) preview failures show distinct, actionable messages.

## Edge Cases and Error Handling
- Preview URL expired mid-play → single refetch + resume prompt, not a silent failure.
- Voice revoked between list and assign → 403/409 → banner + refetch, selection cleared.
- Speaker with zero segments → row shows `0 segments`, assignment allowed but impact dialog notes no affected segments.
- Compatibility endpoint 404 (project without diarization) → `EmptyState` with pipeline link.

## Security and Safety Requirements
- Signed preview URLs never logged, never stored, never placed in query params beyond the provided URL; `Referrer-Policy` respected.
- Consent/policy messages rendered from backend verbatim; client adds no legal interpretation.

## Testing
- Create `frontend/src/features/voices/__tests__/voices.test.tsx`: compatible-only rendering, impact dialog confirm/cancel paths, consent-gate blocking per state, preview error mapping (429/403/expiry), reset flow.
- Playwright `@voices`: speaker list renders, preview plays, assign shows impact dialog then updates voice, revoked voice blocked.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/voices`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/voices
npx playwright test --grep="@voices"
```

## Completion Criteria
- Speakers assigned compatible, consent-valid voices behind impact confirmation, with signed-URL previews and distinct quota/consent errors; voices tests + `@voices` E2E pass.

## Traceability
- Plan B §12.11. Depends on Task 010.



<!-- ===== FILE: 030-media-player-waveform-timeline.md ===== -->


# Task 030 — Media Player, Waveform, and Timeline

## Goal
Implement the shared media player, canvas waveform, and multi-lane timeline with read-only timing.

## Context
Media review surface reused by transcript (Task 027 compact mode), translation, review, and QC; media artifact + waveform-peak endpoints from Task 004, export/output reads from Task 012. Visualization peaks never derive from archival originals. Timing is display-only — no manual timing edits anywhere.

## Starting State
Tasks 004 (media artifacts, `WaveformPeaks`, signed URLs), 012 (output/export reads) done. No shared player. Depends on Tasks 004, 012.

## Scope
Included: `MediaPlayer` controls, canvas `Waveform` from peaks, `Timeline` lanes + zoom/pan/select/seek/play-region/issue-jump, timing markers, read-only timing rule, memoization/virtualization, debounced interactions.
Excluded: transcript/translation text editing (Tasks 027–028), review actions (Task 031), upload UX (Task 023).

## Instructions
1. Create `frontend/src/features/timeline/MediaPlayer.tsx`: controls play/pause, seek (±5s/±1-frame buttons + scrubber), volume, playback speed, fullscreen, Picture-in-Picture, prev/next segment navigation; keyboard shortcuts (space/K, arrows/J-L); error state for expired signed URL (single refetch).
2. Create `frontend/src/features/timeline/Waveform.tsx`: canvas-rendered waveform from `WaveformPeaks` (Task 004) only — assert at the data layer that peak source is never the archival original (test asserts peak endpoint used, archival URL never requested for visualization); memoized canvas draw, resize-observer redraw, debounced scrub.
3. Create `frontend/src/features/timeline/Timeline.tsx`: lanes — video, source-audio, dialogue (transcript segments), generated-audio, markers; interactions zoom (wheel/ctrl+wheel + buttons), pan (drag), click-to-select segment, click/drag seek, play-region loop, issue-jump (Task 032 issue → timeline position).
4. Render markers: segment start/end, overlap, silence, warning, missing-audio; marker tooltips explain cause + link to owning surface (transcript/translation/QC); missing markers render with pattern + label, never color alone.
5. Enforce read-only timing: no drag-to-retime, no trim handles, no duration inputs anywhere in player/timeline (grep-gate test for retime/trim/duration-edit affordances); segment boundaries come from the aggregate only.
6. Performance: memoized lane components, virtualized marker rendering for long media, debounced (≥100ms) zoom/pan/seek handlers; shared player instance via `frontend/src/features/timeline/playerStore.ts` so Task 027 embeds compact mode without a second element.

## Requirements
- R1: Waveform renders from `WaveformPeaks` only; archival media never fetched for visualization.
- R2: No manual timing edits exist in player or timeline (grep-gate + interaction test).
- R3: Timeline supports zoom/pan/select/seek/play-region/issue-jump across all five lanes.
- R4: Markers cover start/end/overlap/silence/warning/missing with text+pattern encoding (no color-only signaling).
- R5: Scrub/zoom/pan handlers debounced; lanes memoized; 60fps scrub on a 2h fixture (perf smoke).

## Edge Cases and Error Handling
- Peaks missing (still processing) → skeleton waveform + progress link, player still functional.
- Signed media URL expired → single refetch + resume position; double-expiry → error state with retry.
- Zero-duration/gap regions → rendered as explicit gap blocks, never collapsed silently.
- PiP/fullscreen unsupported → controls hidden gracefully with tooltip, no crash.

## Security and Safety Requirements
- Signed media URLs used in `<video>`/`<audio>` only; never logged, never embedded in error reports.
- Peak data treated as untrusted floats: clamped/validated before canvas draw (no NaN propagation).

## Testing
- Create `frontend/src/features/timeline/__tests__/timeline.test.tsx`: player controls + shortcuts, peaks-only assertion, marker rendering per type, read-only timing grep-gate, debounce/zoom/pan behavior, issue-jump wiring.
- Playwright `@timeline`: player plays/seeks, waveform renders, zoom/pan/select/seek work, issue click jumps playhead.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/timeline`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/timeline
npx playwright test --grep="@timeline"
```

## Completion Criteria
- Shared player + peaks waveform + five-lane timeline navigate media with full marker coverage and strictly read-only timing; timeline tests + `@timeline` E2E pass.

## Traceability
- Plan B §10.7, §12.12. Depends on Tasks 004, 012.

## Review Fix — Dependency Correction
- **Re-timed earlier:** depends on 004 (preview artifacts), 008 (run/progress contracts), 009 (segments/timing metadata). The final output API (012A) is OPTIONAL for artifact references only — do not wait for 012A to build player/waveform/timeline.



<!-- ===== FILE: 031-manual-review-studio.md ===== -->


# Task 031 — Manual Review Studio

## Goal
Implement the review queue with single-screen item context and idempotent, audited dispositions.

## Context
Human gate before release; review endpoints from Task 011 (queue, approve/reject/requeue/resolve-with-edit with idempotency + expected-version + reason + audit), segment context from Tasks 027–029, mounted in the Task 018 project tabs. Dispositions are anti-duplicate: retries never double-apply.

## Starting State
Tasks 011 (reviews API), 027 (transcript context), 028 (translation context), 029 (voice context) done. No `features/review`. Depends on Tasks 011, 027, 028, 029.

## Scope
Included: `ReviewQueue` + filters, `ReviewCard` single-screen context, approve/reject/requeue/resolve-with-edit with idempotency + expected-version + reason + audit display, conflict→refresh anti-duplicate.
Excluded: transcript/translation/voice editing outside review context (Tasks 027–029 own those), QC scoring (Task 032).

## Instructions
1. Create `frontend/src/features/review/ReviewQueue.tsx`: queue fed by `useReviewQueue(projectId, filters)` on `queryKeys.reviews`; filters — project, severity, status, type, speaker, language, age; filter state in URL search params (shareable); virtualized list; empty-filter result shows `EmptyState` with clear-filters action.
2. Create `frontend/src/features/review/ReviewCard.tsx`: single-screen context per item — media excerpt (Task 030 compact player at issue timestamp), transcript segment, translation + candidates, voice assignment, audio excerpt, sync/QC summary, provider/model/versions, available actions gated by permission, history/audit trail.
3. Implement dispositions (`frontend/src/features/review/useDisposition.ts`): approve / reject / requeue / resolve-with-edit; every call sends `Idempotency-Key` (client-generated per intent, reused on retry) + `expectedVersion` + reason (required for reject/requeue, optional note otherwise); resolve-with-edit embeds the Task 027/028 manual-version payload inline.
4. Implement conflict→refresh: 409/412 → stale banner + refetch + preserved reason text; retry reuses the same idempotency key (test asserts key stability across retries, no duplicate audit entries).
5. Gate actions by the aggregate `actions[]`/permissions: disallowed actions hidden (not disabled) with the reason available in history; optimistic status update with rollback on failure.
6. History panel per card: chronological audit entries (actor, action, reason, version) from the Task 011 history endpoint; newest first; pending-disposition spinner row while mutate in flight.

## Requirements
- R1: All filters (project/severity/status/type/speaker/lang/age) narrow the queue server-side (test asserts query params).
- R2: Every disposition carries idempotency key + expectedVersion + reason per contract; retries reuse the key.
- R3: 409/412 always refetches + banners; no duplicate dispositions from retries (audit-count assertion).
- R4: Review card shows full context single-screen without navigating away (media/transcript/translation/voice/audio/sync/QC/provider/versions/history).
- R5: Disallowed actions hidden by permission, with rationale discoverable in history.

## Edge Cases and Error Handling
- Double-click approve → single mutation (button disabled in flight + idempotency key reuse).
- Item resolved by another reviewer → 409 → banner + card marked resolved by `<actor>` + removed from active filter on refresh.
- Reason exceeding max length → 400 with inline field error, disposition not sent.
- Queue empty → `EmptyState` distinguishing "no items" from "filters exclude everything".

## Security and Safety Requirements
- Reasons rendered as plain text (no HTML); audit actor shown from backend only, never client-injected.
- Permission gating enforced from backend `actions[]`; client hiding is UX only, never a security boundary claim.

## Testing
- Create `frontend/src/features/review/__tests__/review.test.tsx`: filter param mapping, per-action payload (key/version/reason), idempotent retry (stable key, single audit), 409 refresh, permission hiding, resolve-with-edit payload.
- Playwright `@review`: queue filters, card context renders, approve/reject/requeue flows, conflict banner on stale item.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/review`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/review
npx playwright test --grep="@review"
```

## Completion Criteria
- Review queue filters and disposes items with idempotent, versioned, reasoned actions and full single-screen context; review tests + `@review` E2E pass.

## Traceability
- Plan B §12.13. Depends on Tasks 011, 027, 028, 029.



<!-- ===== FILE: 032-quality-control-interface.md ===== -->


# Task 032 — Quality Control Interface

## Goal
Implement the QC summary and issue list with evidence-backed, accessibility-safe status signaling.

## Context
Quality gate for dubs; QC read endpoints from Task 008 (processing workspace progress/QC), issue deep-links into the Task 030 timeline and Task 031 review queue. Blocking issues must be unmissable without relying on color alone.

## Starting State
Task 008 (workspace aggregate with QC summary/issues) done. No `features/quality`. Depends on Task 008.

## Scope
Included: `QualitySummary` (passed/warning/retry/review/blocked), `QualityIssue` cards (code/severity/scope/segment/description/evidence/action/status) with evidence rendering, blocking prominence, no-color-only signaling.
Excluded: timeline rendering (Task 030), review dispositions (Task 031), export gating (Task 033).

## Instructions
1. Create `frontend/src/features/quality/QualitySummary.tsx`: aggregate counts by state — passed, warning, retry, review, blocked — fed by `useQuality(projectId)` on `queryKeys.quality` (or the Task 025 workspace aggregate slice); each count links to the filtered issue list; blocked state announced with icon + text + pattern, never color alone.
2. Create `frontend/src/features/quality/QualityIssue.tsx`: card shows code, severity (with icon + label), scope (segment/project/run), linked segment id, description, suggested action, status; evidence block per issue type — waveform excerpt (Task 030 mini canvas), timestamp (click → Task 030 issue-jump), audio excerpt player, QC metric readout, artifact link (signed URL, Task 004/012).
3. Implement status signaling rule: every status/severity conveyed by icon + text label + shape/pattern in addition to color (test asserts each badge contains non-color text); blocked issues pinned to top with a prominent banner + count.
4. Implement issue actions: jump-to-timeline (Task 030 `issue-jump`), open-in-review (deep link to Task 031 card where applicable), retry-link where backend advertises the action in `actions[]`; unavailable actions omitted with reason tooltip.
5. Filters + grouping: filter by severity/status/scope; group toggle (by segment / by code); filter state in URL search params; empty states distinguish "no issues" (pass celebration) from "filters exclude everything".

## Requirements
- R1: Summary covers all five states (passed/warning/retry/review/blocked) with counts that match the issue list.
- R2: Every issue shows code/severity/scope/segment/description/evidence/action/status — none omitted (fixture completeness test).
- R3: No color-only signaling anywhere (icon + text + pattern assertion per badge).
- R4: Blocking issues pinned + bannered above all other content.
- R5: Evidence appropriate to issue type renders inline (waveform/timestamp/audio/metric/artifact).

## Edge Cases and Error Handling
- QC pending (run active) → summary shows in-progress skeleton + live updates via Task 026 invalidation, not zeros.
- Evidence artifact expired → single signed-URL refetch, then graceful "evidence unavailable" note.
- Unknown issue code → generic card with raw code + description, never dropped or crashed.
- Zero issues → explicit passed state, not an empty list.

## Security and Safety Requirements
- Artifact/evidence URLs are signed and short-lived; never logged or copied into shareable filter URLs.
- QC metric values displayed as provided; client computes no scores or thresholds.

## Testing
- Create `frontend/src/features/quality/__tests__/quality.test.tsx`: summary counts vs fixture, issue field completeness, non-color signaling assertion, blocking prominence order, filter/group behavior, unknown-code tolerance.
- Playwright `@quality`: summary renders, issue evidence plays/jumps, blocked banner visible, filters narrow list.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/quality`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/quality
npx playwright test --grep="@quality"
```

## Completion Criteria
- QC summary and evidence-backed issues render with blocking prominence and redundant (non-color) signaling; quality tests + `@quality` E2E pass.

## Traceability
- Plan B §12.14. Depends on Task 008.



<!-- ===== FILE: 033-output-export-experience.md ===== -->


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



<!-- ===== FILE: 034-notifications-center.md ===== -->


# Task 034 — Notifications Center

**Required/Optional:** Required
**Complexity:** S

## Goal
Implement the header notification bell and Notification Center with durable list, unread count, mark-read/read-all, and deep links to project/review/export.

## Context
Notification endpoints come from Task 012 (`GET /notifications`, `GET /notifications/unread-count`, `POST .../read|read-all`), the live stream from Task 026 (`notification.created` SSE event), the shell header slot from Task 018, and notification preferences from Task 006. Notifications are durable (Task 002 projector), so the Center reads from the API and treats SSE only as an invalidation hint.

## Starting State
Task 012 (notifications API) and Task 026 (SSE client) done. No `features/notifications`. Depends on Tasks 012 and 026.

## Scope
Included: header bell icon + unread badge, Notification Center page, durable list, unread count, mark-read/read-all mutations, deep links per notification type, SSE `notification.created` → invalidation, prefs hook, no-sensitive-payloads rule.
Excluded: notification projection backend (Task 002), export flow (Task 033), full settings screen (Task 035).

## Instructions
1. Create `frontend/src/features/notifications/useNotifications.ts`: list query on `queryKeys.notifications` (paginated, newest-first) + `useUnreadCount.ts` on `queryKeys.notificationsUnread`; both tenant-scoped via Task 017 client; stale-while-revalidate on SSE invalidation only, no ad-hoc polling.
2. Create `frontend/src/features/notifications/NotificationBell.tsx`: header icon (Task 018 shell slot) with unread-count badge; badge hidden at zero; click navigates to the Center; badge announces via `aria-live="polite"`; overflow (99+) renders as `99+`.
3. Create `frontend/src/features/notifications/NotificationCenter.tsx` + `NotificationList.tsx` + `NotificationItem.tsx`: durable list with type icon, title, relative timestamp, read/unread state; mark-read on open (single) and `read-all` button with optimistic update + rollback on failure.
4. Implement deep links in `frontend/src/features/notifications/notificationLinks.ts`: map each notification type to its target — project events → project workspace, review events → review studio item, export events → outputs page; unknown types fall back to the dashboard, never a dead link; deleted targets show a `GoneState` with explanation.
5. Wire SSE in `frontend/src/features/notifications/useNotificationStream.ts`: subscribe to `notification.created` from the Task 026 stream as an invalidation hint → invalidate `queryKeys.notifications` + `queryKeys.notificationsUnread`; tolerate duplicates/out-of-order events (dedupe by notification id).
6. Create `frontend/src/features/notifications/useNotificationPrefs.ts`: hook binding the Center's per-type toggles to the `notificationPreferences` key from Task 006 (`PUT /me/preferences`); toggles disable delivery only, never delete history.
7. Enforce the no-sensitive-payloads rule: render only `title/body/link` display fields; never render raw payloads, tokens, URLs internals, or transcript/media content (grep-gate test for `token|secret|signedUrl|transcript` in rendered output).

## Requirements
- R1: Bell badge reflects `unread-count` within one SSE round-trip; zero hides the badge.
- R2: List is newest-first, paginated, and survives reload (durable read from API, not SSE memory).
- R3: Mark-read (single) and read-all update the badge optimistically with rollback on failure.
- R4: Every notification type deep-links to project/review/export; unknown/deleted targets degrade to dashboard/`GoneState`, never a dead link.
- R5: `notification.created` only invalidates queries; duplicates never duplicate rows (id-keyed dedupe).
- R6: No sensitive payload content is rendered (grep-gate test).

## Edge Cases and Error Handling
- SSE duplicate/out-of-order `notification.created` → dedupe by id, no duplicate rows or badge inflation.
- Deep-link target deleted server-side → `GoneState` + toast, row retained as read.
- Read-all fails mid-flight → rollback badge + error toast, list refetch.
- Unread-count 409/expired cursor → full refetch of first page.
- Prefs save rejected (unknown key) → inline field error, toggles unchanged.

## Security and Safety Requirements
- Notification queries use tenant-scoped keys; no cross-tenant ids in links or cache.
- Never log, cache, or render signed URLs, tokens, or raw event payloads from notifications.
- Prefs mutations go through the validated Task 006 preferences endpoint (whitelisted keys only).

## Testing
- Create `frontend/src/features/notifications/__tests__/notifications.test.tsx`: badge zero/hidden vs count vs 99+, list ordering, mark-read/read-all optimistic + rollback, deep-link mapping per type, SSE-invalidation dedupe, no-sensitive-payloads scan.
- Playwright `@notifications`: bell badge appears on new notification, Center lists it, deep link lands on project/review/export, read-all clears badge.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/notifications`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/notifications
npx playwright test --grep="@notifications"
```

## Completion Criteria
- Bell + Center show durable notifications with accurate unread count, working mark-read/read-all, correct deep links, SSE-driven invalidation, and no sensitive payload rendering; notifications tests + `@notifications` E2E pass.

## Traceability
- Plan B §12.16. Depends on Tasks 012 and 026.



<!-- ===== FILE: 035-activity-cost-preferences.md ===== -->


> **REVIEW FIX — SUPERSEDED (split):** this combined file is superseded by `035A-activity-cost-ui.md` (activity/cost) + `035B-settings-preferences-ui.md` (settings/preferences). New work goes to 035A/035B.

# Task 035 — Activity Feed, Cost/Quota Visibility, User Preferences

**Required/Optional:** Required
**Complexity:** S

## Goal
Implement the activity timeline, cost/quota summary, and settings/preferences screens with honest estimates and hidden-by-default advanced detail.

## Context
Activity and cost read models come from Task 008 (`GET .../activity`, workspace aggregate with cost fields), preferences endpoints from Task 006 (`GET|PUT /me/preferences`), cost/quota semantics from Plan B §8.1, mounted in the Task 018 shell. Advanced run internals exist for operators only and stay hidden by default.

## Starting State
Tasks 006, 007, 008, 018 done. No `features/activity` or `features/settings`. Depends on Tasks 006–008.

## Scope
Included: `AuditTimeline`, `CostSummary`, Settings screen (locale/timezone/theme/filters/timeline-zoom/notif prefs).
Excluded: notification center (Task 034), admin/operator diagnostics (Task 036), preflight confirmation dialog (Task 024).

## Instructions
1. Create `frontend/src/features/activity/AuditTimeline.tsx`: render `timestamp/actor/action/summary` columns from `GET .../activity` on `queryKeys.activity` (paginated); advanced fields (`run/stage/provider/cost/correlation`) hidden by default behind a per-row expander; virtualize or paginate beyond 50 rows.
2. Create `frontend/src/features/activity/ActivityFilters.tsx`: filter by actor/action/date-range reusing the Task 006 `defaultProjectFilters` shape where applicable; filters live in the URL (shareable) and reset cleanly to defaults.
3. Create `frontend/src/features/settings/CostSummary.tsx`: show estimated vs actual cost, duration/units/storage breakdown; preflight-sourced figures always labeled `Estimate`; quota states `available/near/exceeded/reserved` map to distinct visual treatments; reserved amounts shown as informational only — never render reservation IDs.
4. Implement quota-exceeded behavior in `frontend/src/features/settings/QuotaBanner.tsx`: `exceeded` blocks costly actions with explanation + link to runbook/support hint (valid-actions-only, per Task 021); `near` shows a warning banner dismissible per session.
5. Create `frontend/src/features/settings/SettingsPage.tsx`: edit locale/timezone/theme/filters/timeline-zoom/notification-prefs via `PUT /me/preferences` (Task 006 whitelisted keys); optimistic field-level save with per-field error display; theme applies instantly, locale/timezone apply on save with confirmation note.
6. Wire invalidation: preference saves invalidate `queryKeys.preferences` + locale/theme consumers; activity list invalidates on Task 026 progress/completion events for the open project only.

## Requirements
- R1: Timeline shows timestamp/actor/action/summary for every event; advanced fields hidden until expanded.
- R2: Cost figures distinguish estimated vs actual; every estimate is labeled `Estimate`.
- R3: Quota states available/near/exceeded/reserved render distinctly; reservation IDs never appear.
- R4: Settings edits persist via whitelisted preference keys; unknown keys never sent.
- R5: Filters are URL-shareable and resettable; activity pagination never loses filter state.

## Edge Cases and Error Handling
- Empty activity (new project) → `EmptyState` with "events appear as work progresses", not a blank panel.
- Cost endpoint 404 (run predates cost tracking) → show `UnavailableState`, hide don't zero-fill.
- Quota `exceeded` mid-session → banner upgrades live via refetch; in-flight costly dialogs disable submit.
- Prefs save 400 (unknown key / oversize) → per-field error, other fields retained.
- Timezone with no matching IANA entry → fall back to UTC + inline warning.

## Security and Safety Requirements
- Never render reservation IDs, provider-internal cost keys, or raw telemetry in timeline/cost UI.
- Preference values size-capped client-side (4KB) before submit; secrets rejected with guidance.
- Actor display uses display names only; no emails/subjects leaked to viewers without membership.

## Testing
- Create `frontend/src/features/settings/__tests__/settings.test.tsx` and `frontend/src/features/activity/__tests__/activity.test.tsx`: timeline columns + hidden-advanced default, estimate labeling, quota-state treatments, no-reservation-ids scan, settings round-trip + unknown-key rejection.
- Playwright `@activity`: timeline renders with filters, cost summary shows estimate/actual, settings save persists across reload.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/settings src/features/activity`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/settings src/features/activity
npx playwright test --grep="@activity"
```

## Completion Criteria
- Timeline, cost summary, and settings render honestly (estimates labeled, advanced hidden, quotas explicit) with persisted preferences; settings/activity tests + `@activity` E2E pass.

## Traceability
- Plan B §12.17, §12.18, §8.1. Depends on Tasks 006–008.




<!-- ===== FILE: 035A-activity-cost-ui.md ===== -->


# Task 035A — Activity Timeline and Cost/Quota UI

**Required/Optional:** Required
**Complexity:** S

## Goal
Implement the project activity timeline and cost/quota visibility surfaces.

## Context
Split from oversized Task 035 (activity + cost + settings). Activity is the user-visible history projection (002/008); cost/quota is the estimate/actual + quota-state surface from workspace preflight and admin aggregates. Settings/preferences move to 035B.

## Starting State
Depends on Tasks 002 (activity projection), 007–008 (project/workspace/cost inputs), 018 (shell). Task 035 (combined) is superseded by 035A + 035B.

## Scope
Included: `AuditTimeline` (user-friendly default + authorized advanced diagnostics), `CostSummary` (estimated/actual, duration/units/storage, quota states), loading/empty/error states.
Excluded: settings/preferences screens (035B), notification center (034), admin ops dashboard (036), backend projections (002/008).

## Instructions
1. Build `frontend/src/features/activity/AuditTimeline.tsx`: paginated `GET .../activity` (cursor, newest-first), rows timestamp/actor/action/summary; default view user-friendly; advanced section (run/stage/attempt/provider/latency/cost/artifact/correlation) behind `diagnostics.view` permission only.
2. Build `frontend/src/features/cost/CostSummary.tsx` + quota badges: estimated vs actual with `estimate` labeling on preflight values, duration/provider-units/storage breakdown, quota states `available|near|exceeded|reserved`; never render reservation IDs or raw cost internals.
3. Cover states: loading/skeleton, empty (no activity yet), partial (projection lag notice), error with retry, forbidden (advanced section hidden, not error).
4. Reuse query keys from 017 (`activity`, `workspace`, `quotas`); invalidate on SSE `run.status_changed` / `export.*` via 026.

## Requirements
- R1: Activity covers upload/start/translation/review/edit/export/complete actions in order.
- R2: Advanced diagnostics hidden by default; visible only with permission.
- R3: Preflight/estimate values always labeled estimates, never guarantees.
- R4: No reservation IDs, provider secrets, or internal cost keys rendered.
- R5: Empty/partial/error states implemented, not just happy path.

## Edge Cases and Error Handling
- Projection lag (activity missing just-completed action) → `partial` notice + refetch, not an error toast.
- Quota exceeded mid-flow → badge + `contact admin` recovery action (per error UX 011.6).
- Large histories → virtualized list + cursor pagination, never full fetch.

## Security and Safety Requirements
- Tenant-scoped queries only; no cross-project activity leakage via shared keys.
- Cost details visible to authorized roles only where backend gates them; frontend hides without bypassing.

## Testing
- `frontend/src/features/activity/*.spec.ts` + `frontend/src/features/cost/*.spec.ts` (ordering, permission gating, estimate labeling, quota states, empty/partial/error).
- Playwright `@activity` journey: complete an action → appears in timeline; quota states render.

## Validation
```bash
npm run typecheck --prefix frontend
npm run test --prefix frontend -- src/features/activity src/features/cost
npx playwright test --grep="@activity"
```

## Completion Criteria
- Activity timeline + cost/quota surfaces + states + tests exist; supersedes the activity/cost half of Task 035.

## Traceability
- Plan B §12.17, §12.18. Split from 035; projections in 002/008; settings in 035B.



<!-- ===== FILE: 035B-settings-preferences-ui.md ===== -->


# Task 035B — Settings and Preferences UI

**Required/Optional:** Required
**Complexity:** S

## Goal
Implement the user settings and preferences screens backed by the preferences API.

## Context
Split from oversized Task 035 (activity + cost + settings). Preferences (`locale, timezone, theme, defaultProjectFilters, timelineZoom, notificationPreferences`) persist via Task 006; this task is their UI, including locale application from new Task 045. Activity/cost move to 035A.

## Starting State
Depends on Tasks 006 (preferences API), 018 (shell), 045 (locale/timezone application). Task 035 (combined) is superseded by 035A + 035B.

## Scope
Included: settings screens, per-key editors, optimistic update + rollback, validation, dirty/unsaved-guard states.
Excluded: activity timeline/cost UI (035A), notification center (034), backend preference storage (001/006), i18n framework itself (045).

## Instructions
1. Build `frontend/src/features/settings/SettingsPage.tsx` + `PreferencesForm.tsx`: editors for locale (select from supported list in 045), timezone (select + UTC fallback), theme (light/dark/system), default project filters, timeline zoom default, notification preferences (channel toggles; email/webhook shown as future-only where applicable).
2. Wire `GET|PUT /api/v1/me/preferences` via 017 client: optimistic update with rollback on 4xx/5xx, per-field server-validation errors, dirty state + unsaved-change guard on navigation, success toast.
3. Apply locale/timezone/theme immediately through 045 helpers on save (no reload required); record preference change in activity where backend projects it (no direct activity writes here).
4. Cover states: loading/skeleton, validation error, save conflict (409 → refetch + keep draft), offline (queued-save notice, not silent loss).

## Requirements
- R1: All six preference keys editable and round-tripped through the API.
- R2: Unknown keys rejected server-side; UI never sends unlisted keys.
- R3: Locale/timezone/theme apply without reload.
- R4: Dirty/navigate-away guard offers save/discard/cancel.
- R5: No secrets stored in preferences (4KB cap respected, asserted).

## Edge Cases and Error Handling
- Concurrent edit (two tabs) → last-write-wins per key with conflict notice, never silent cross-key overwrite.
- Invalid timezone selection → 400 maps to inline field error + UTC fallback suggestion.
- Save fails offline → draft preserved + retry action, never discarded.

## Security and Safety Requirements
- Preferences scoped to caller (tenant + user); no editing other users preferences (403 + explanation).
- Preference values sanitized (max lengths); nothing rendered as raw HTML.

## Testing
- `frontend/src/features/settings/*.spec.ts` (editors, optimistic rollback, guards, conflict, offline draft).
- Playwright `@settings` journey: change locale/theme → persists across reload.

## Validation
```bash
npm run typecheck --prefix frontend
npm run test --prefix frontend -- src/features/settings
npx playwright test --grep="@settings"
```

## Completion Criteria
- Settings/preferences UI + guards + tests exist; supersedes the settings half of Task 035.

## Traceability
- Plan B §8.1, §9.1, §12.17–§12.18 (prefs slice). Split from 035; storage in 001/006; i18n application in 045.



<!-- ===== FILE: 036-admin-diagnostics-ui.md ===== -->


# Task 036 — Admin, Usage, Provider Health, Diagnostics UI

**Required/Optional:** Required
**Complexity:** M

## Goal
Implement the role-gated Admin area (tenants/users/roles/health/routes/usage/quotas/retention/audit/flags) and the operator diagnostics dashboard with safe destructive-action gates.

## Context
Read APIs come from Task 005 (diagnostics aggregations) and Task 013 (`GET /admin/...`, `GET /diagnostics/...` with elevated authz), mounted in the Task 018 shell with role-based route guards. This is the only surface where elevated operations exist, so every destructive action carries confirm + reason + permission + audit.

## Starting State
Tasks 005, 013, 018 done. No `features/admin`. Depends on Tasks 005 and 013.

## Scope
Included: Admin section (tenants/users/roles/health/routes/usage/quotas/retention/audit/flags), ops dashboard (queues/workers/errors/DLQ/leases/orphans/backlog/failures), elevated gating, secret-free display, destructive-action gates.
Excluded: diagnostics aggregation logic (Tasks 005/013), end-user settings (Task 035), optional local-GPU surfaces (Task 044).

## Instructions
1. Create `frontend/src/features/admin/AdminPage.tsx` + `frontend/src/features/admin/adminGuard.ts`: route guard requiring the elevated role from Task 013; non-elevated users get a 403 `ForbiddenState` (never a redirect loop); guard denial is logged to telemetry without user ids.
2. Create `frontend/src/features/admin/TenantsPanel.tsx` + `UsersRolesPanel.tsx`: tenant list/detail and user-role assignment on `queryKeys.adminTenants` / `queryKeys.adminUsers`; role changes require the assigner to hold a strictly higher grant and emit an audit reason (mandatory text field, min 10 chars).
3. Create `frontend/src/features/admin/OpsDashboard.tsx`: queues/workers/errors/DLQ/leases/orphans/backlog/failure panels fed by `queryKeys.diagnostics` with 30s refetch (paused when tab hidden); DLQ rows expose redrive/discard only where backend advertises `actions[]`; orphan leases show age + owner, never raw lock tokens.
4. Create `frontend/src/features/admin/HealthRoutesPanel.tsx` + `UsageQuotasPanel.tsx` + `RetentionAuditPanel.tsx`: provider health/routes matrix, usage-vs-quota bars (reuse Task 035 quota states), retention policies, and audit-event viewer (timestamp/actor/action/summary; same hidden-advanced rule as Task 035).
5. Create `frontend/src/features/admin/FlagsPanel.tsx`: feature-flag list with safe toggle (immediate, reversible) vs destructive flag-apply gated behind the destructive flow in step 6.
6. Implement `frontend/src/features/admin/DestructiveAction.tsx`: shared gate for every destructive operation — type-to-confirm + mandatory reason + permission re-check + explicit audit emission; on success invalidate affected admin queries + show receipt (action id, timestamp); on 403 show `ForbiddenState` without leaking the required role name.
7. Enforce no-secrets display: secret-free DTO assertion — scan rendered admin output for `secret|password|connectionString|apiKey|privateKey` in tests; connection strings render as masked fingerprints only.

## Requirements
- R1: Non-elevated users cannot reach any admin route (guard + 403 state, verified by negative test).
- R2: All seven admin areas (tenants/users/roles/health/routes/usage/quotas/retention/audit/flags) render from read APIs with loading/empty/failure states.
- R3: Ops dashboard shows queues/workers/errors/DLQ/leases/orphans/backlog/failures with advertised-actions-only controls.
- R4: Every destructive action requires confirm + reason + permission + audit receipt.
- R5: No secrets/connection strings/tokens appear anywhere in admin UI (scan test).
- R6: Role assignment requires higher-grant assigner + mandatory audit reason.

## Edge Cases and Error Handling
- Elevated role revoked mid-session → next admin query 403 → guard locks the section + toast, session otherwise intact.
- DLQ redrive 409 (already redriven) → row refreshes + toast, no duplicate redrive.
- Empty DLQ / zero orphans → healthy `EmptyState`, not hidden panels.
- Audit viewer pagination gap (retention expiry) → explicit "older events expired per retention policy" marker.
- Flag toggle during rollout freeze (backend 423) → dialog explains freeze, toggle reverts.

## Security and Safety Requirements
- Elevated gating enforced both in route guard and per-query 403 handling; client never caches the elevated role claim.
- No provider/DB/storage secrets reach the frontend; masked fingerprints only, never reversible.
- Destructive actions always carry confirm + reason + permission check + audit receipt; reason text sanitized before display.

## Testing
- Create `frontend/src/features/admin/__tests__/admin.test.tsx`: guard allows/denies, destructive gate (missing reason blocks), advertised-actions-only DLQ controls, no-secrets scan, quota-state reuse.
- Create `tests/DubbingPlatform.IntegrationTests/Admin/AdminAuthzTests.cs`: non-elevated callers get 403 on every `/admin/...` and `/diagnostics/...` endpoint; elevated callers succeed; destructive ops without reason/audit are rejected.
- Playwright `@admin`: elevated login reaches ops dashboard, destructive flow requires reason, non-elevated login sees 403 state.
- Type: unit (vitest) + backend integration (Testcontainers) + Playwright.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/admin
dotnet test --filter FullyQualifiedName~AdminAuthzTests
npx playwright test --grep="@admin"
```

## Completion Criteria
- Role-gated Admin + ops dashboard render all areas with safe destructive gates and zero secret leakage; admin unit tests, `AdminAuthzTests`, and `@admin` E2E pass.

## Traceability
- Plan B §12.19, §19.3. Depends on Tasks 005 and 013.



<!-- ===== FILE: 037-security-privacy-hardening.md ===== -->


# Task 037 — Tenant Isolation, Signed URLs, CORS/CSP, Consent, Secrets Hygiene

**Required/Optional:** Required
**Complexity:** M

## Goal
Harden authz, tenant isolation, transport, and consent enforcement end-to-end with negative cross-tenant tests proving the boundaries.

## Context
All product endpoints from Tasks 006–013 must carry per-endpoint auth + tenant + project + role checks; media/downloads use short-lived signed URLs (Tasks 004, 012, 030, 033); voice preview has quota/consent gates (Tasks 010, 029). This task is the cross-cutting hardening pass — no new features, only enforcement, headers, and negative tests.

## Starting State
Tasks 006–013, 019, 029, 030, 033 done. Hardening not yet applied uniformly. Depends on Tasks 006–013.

## Scope
Included: per-endpoint checks, tenant-scoped keys/storage/URLs/telemetry/query-keys, 15-min signed URLs, CORS allowlist, strict CSP, consent gate, secrets hygiene, negative cross-tenant tests.
Excluded: new endpoints or UI (all other tasks), observability wiring (Task 038), CI gates (Task 042).

## Instructions
1. Add per-endpoint authorization in `src/DubbingPlatform.Api/Endpoints/`: every handler asserts authenticated principal + `tenant_id` match + project membership (via `ProjectMembership` role from Task 001) with least-privilege role for the operation; anonymous-allowed routes enumerated in `src/DubbingPlatform.Api/Auth/AnonymousRoutes.cs` (login/refresh/health only).
2. Enforce tenant scoping in `src/DubbingPlatform.Infrastructure/Persistence/`: RLS policies on all product tables (Tasks 001–004); storage keys prefixed `tenant/{tenantId}/...` in `src/DubbingPlatform.Infrastructure/Storage/TenantKeyBuilder.cs`; signed download/preview URLs carry tenant-bound claims and expire in 15 minutes (`src/DubbingPlatform.Api/Services/SignedUrlService.cs`).
3. Apply transport hardening in `src/DubbingPlatform.Api/Program.cs` (or `SecurityHeaders.cs`): CORS allowlist from configuration (no wildcard with credentials); strict Content-Security-Policy with no `unsafe-inline` (nonces for any inline needs); `Referrer-Policy: no-referrer`; signed URLs never logged, never cached (Cache-Control: private, no-store on issuance endpoints).
4. Implement the consent gate in `src/DubbingPlatform.Domain/Voice/ConsentGate.cs` + enforcement in Task 010/029 call paths: voice features disabled-by-default; revocation blocks all new synthesis/preview use immediately while in-flight jobs drain; consent states (`unknown/granted/revoked/expired`) visible in UI from Task 029; every gate decision audited.
5. Apply secrets hygiene: no provider/DB/storage secrets in any API response DTO (allowlist-serialize in `src/DubbingPlatform.Api/Contracts/`); no secrets in frontend bundles (`VITE_*` audit — only non-sensitive keys); telemetry scrubber (shared with Task 038) strips tokens/URLs/media/transcript before emission; query keys (Task 017) include `tenantId` so caches never cross tenants.
6. Write negative tests in `tests/DubbingPlatform.IntegrationTests/Security/TenantIsolationTests.cs`: cross-tenant read/write on every product endpoint → 403/404 (never 200, never data); tampered-tenant signed URL → rejected; expired (>15-min) URL → rejected; member of project A cannot touch project B.
7. Write `tests/DubbingPlatform.IntegrationTests/Security/ConsentTests.cs`: revoked consent blocks new preview/assignment (409/403 with `CONSENT_REQUIRED`); disabled-by-default voice without grant is blocked; revocation audit event emitted; expired grant treated as revoked.

## Requirements
- R1: Every product endpoint enforces auth + tenant + project-membership + role; anonymous set is explicitly enumerated.
- R2: Storage keys, signed URLs, telemetry, and frontend query keys are all tenant-scoped.
- R3: Signed URLs expire in 15 minutes, are never logged/cached, and carry tenant-bound claims.
- R4: CORS allowlist (no credentialed wildcard); strict CSP with no `unsafe-inline`.
- R5: Consent is disabled-by-default; revocation blocks new use immediately; states are visible.
- R6: No provider/DB/storage secrets reach frontend responses, bundles, logs, or telemetry.
- R7: Negative cross-tenant and consent tests fail closed (403/404/409, never data).

## Edge Cases and Error Handling
- Token with valid signature but unknown tenant → 401, no tenant creation side-effect.
- Membership revoked mid-session → next call 403 with `MEMBERSHIP_REVOKED`, client routes to projects list.
- Signed URL used after project archival → rejected with reason, not silent 404.
- Consent revoked while preview job runs → job drains, result discarded, user notified.
- CORS preflight from unlisted origin → rejected without leaking allowlist contents.

## Security and Safety Requirements
- Fail closed everywhere: ambiguous authz → deny; ambiguous consent → block; ambiguous tenancy → 404 without existence oracle (403 vs 404 chosen to avoid id enumeration per endpoint policy).
- Structured errors carry `code/message/correlationId` only; no stack traces, paths, or provider messages to clients.
- Audit every denied cross-tenant attempt and every consent-gate decision (actor, tenant, endpoint, outcome).

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Security/TenantIsolationTests.cs`: cross-tenant matrix over all product endpoints, tampered/expired signed URLs, project-A vs project-B separation, query-key tenant scoping (frontend unit).
- Create `tests/DubbingPlatform.IntegrationTests/Security/ConsentTests.cs`: default-disabled, revocation blocks new use, in-flight drain, audit emission, expiry-as-revoked.
- Type: backend integration (Testcontainers PostgreSQL); frontend unit for query-key scoping.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~TenantIsolationTests
dotnet test --filter FullyQualifiedName~ConsentTests
```

## Completion Criteria
- Per-endpoint authz, tenant scoping, 15-min URLs, CORS/CSP, consent gate, and secrets hygiene enforced with `TenantIsolationTests` + `ConsentTests` green and no secret leakage into frontend or logs.

## Traceability
- Plan B §13.1–§13.8, §23.4. Depends on Tasks 006–013, 019, 029, 030, 033.

## Review Fix — Scope Clarification (Completion Gate)
- **Baseline stays in feature tasks:** per-endpoint auth + tenant + project + role checks, tenant-scoped queries/keys/URLs, and consent evaluation are implemented in 006–013 / 019 / 029 / 030 / 033. This task PROVES boundaries: cross-tenant/cross-user negative tests, header sweep (CSP/CORS/signed-URL expiry), secret scan (no provider/DB/storage secrets to frontend), consent-revocation proofs. It must not be the first place authz appears.



<!-- ===== FILE: 038-observability.md ===== -->


# Task 038 — Backend Metrics, Frontend Telemetry, Analytics, Correlation

**Required/Optional:** Required
**Complexity:** S

## Goal
Wire product-grade observability — backend metrics, safe frontend telemetry, allowlisted analytics with opt-out, and end-to-end correlation — without leaking sensitive content.

## Context
SSE/notification/read-model/upload/review/export latencies need backend counters; the Task 018 telemetry harness needs its event taxonomy finalized; analytics must be allowlisted + opt-out per privacy rules; correlation IDs (Task 017) must flow end-to-end. Sensitive content (tokens/URLs/media/transcript) must never enter telemetry.

## Starting State
Task 018 (telemetry baseline) and Task 026 (SSE client) done. Depends on Tasks 018 and 026.

## Scope
Included: backend latency/failure metrics, frontend telemetry taxonomy + scrubbing, allowlisted analytics + opt-out, E2E correlation IDs.
Excluded: admin ops dashboard rendering (Task 036), E2E test scenarios (Task 041), CI quality gates (Task 042).

## Instructions
1. Add backend metrics in `src/DubbingPlatform.Api/Observability/BackendMetrics.cs` (OpenTelemetry counters/histograms): SSE connections/reconnects, notification projection failures, read-model query latency, upload-funnel stages, review/export/preview latencies; every metric labeled with `tenant_id` hash (never raw id) + endpoint/operation, never user content.
2. Finalize frontend telemetry in `frontend/src/telemetry/events.ts`: taxonomy page/API/UI/upload/SSE with payload schema `app/env/browser/OS/route/correlationId`; implement `frontend/src/telemetry/scrub.ts` stripping tokens/URLs/media/transcript/stack internals before emission; scrubber unit-tested with adversarial fixtures.
3. Implement allowlisted analytics in `frontend/src/telemetry/analytics.ts`: permitted events only — `project.created`, `upload.*`, `processing.*`, `review.*`, `translation.edited`, `voice.changed`, `export.*`; any non-allowlisted event is dropped + logged in dev; user opt-out persisted in preferences (Task 006 key) disables analytics while keeping error/crash telemetry.
4. Propagate E2E correlation in `frontend/src/api/client/correlation.ts` + `src/DubbingPlatform.Api/Middleware/CorrelationMiddleware.cs`: generate `correlationId` per user action, send as header, echo in error envelopes (Task 013), join frontend + backend spans; review/version-conflict errors carry the id so support can trace both sides.
5. Add `tests/DubbingPlatform.IntegrationTests/Observability/ObservabilityTests.cs`: metric emission smoke (SSE connect → counter, failed projection → failure counter), correlation propagation (action id appears in backend span + error envelope), scrubber never emits sensitive fixtures.

## Requirements
- R1: Backend emits SSE/reconnect, notification-failure, read-model-latency, upload-funnel, review/export/preview-latency metrics.
- R2: Frontend telemetry always includes app/env/browser/OS/route/correlation and never tokens/URLs/media/transcript.
- R3: Analytics emits only allowlisted events; opt-out disables analytics, preserves crash telemetry.
- R4: Every user action carries a correlation ID visible in both frontend spans and backend errors.
- R5: Scrubber proven by adversarial unit tests (sensitive fixtures → fully redacted output).

## Edge Cases and Error Handling
- Telemetry endpoint down → buffer locally (cap 100 events), drop oldest, never block UI or retry-storm.
- Opt-out toggled mid-session → in-flight analytics batch dropped, subsequent events suppressed.
- Missing correlation on legacy call path → middleware mints one, marks `correlation_propagated=false`.
- Metric cardinality explosion (high-cardinality route params) → route-template labeling only, ids hashed.
- SSE reconnect storm → reconnect counter + backoff (Task 026) prevents metric flood.

## Security and Safety Requirements
- Scrub tokens/secret-bearing headers/signed URLs/media bytes/transcript text before any emission, frontend or backend.
- Tenant labeling uses one-way hashes; no raw tenant/user ids in metric labels.
- Analytics allowlist enforced at the emit call-site; dynamic event names rejected by type signature.

## Testing
- Create `tests/DubbingPlatform.IntegrationTests/Observability/ObservabilityTests.cs`: metric smoke, correlation propagation, failure-counter paths.
- Create `frontend/src/telemetry/__tests__/telemetry.test.ts`: taxonomy conformance, scrubber adversarial fixtures, allowlist drop-behavior, opt-out suppression.
- Type: backend integration + frontend unit; run `npm run test -- src/telemetry`.

## Validation
```bash
dotnet test --filter FullyQualifiedName~ObservabilityTests
cd frontend && npm run test -- src/telemetry
```

## Completion Criteria
- Backend metrics, scrubbed frontend telemetry, allowlisted analytics with opt-out, and E2E correlation IDs operate with zero sensitive leakage; `ObservabilityTests` + telemetry unit tests pass.

## Traceability
- Plan B §14.1–§14.4, §18.1–§18.2. Depends on Tasks 018 and 026.

## Review Fix — Scope Clarification (Completion Gate)
- **No new correlation bootstrap:** correlation IDs + error taxonomy are established in 013/017; metrics hooks are added in backend tasks (002/005/008). This task VALIDATES completeness: taxonomy conformance, scrubbing/leakage tests (no tokens/URLs/media/transcript), analytics allowlist + opt-out, SSE/notification/read-model latency coverage.



<!-- ===== FILE: 039-unit-component-mock-tests.md ===== -->


> **REVIEW FIX — SUPERSEDED (split/rewrite):** this broad file is superseded by `039A-test-infra-coverage.md` (infra/gap report) + `039B-frontend-state-matrices.md` (frontend gap closure) + `039C-backend-unit-gaps.md` (backend gap closure). Feature tasks (006–036) author their own specs against harness 046; 039A/B/C close gaps only. No initial test authorship here.

# Task 039 — Unit, Component, API-Mock Tests

**Required/Optional:** Required
**Complexity:** M

## Goal
Cover pure logic with unit tests, components with state-matrix tests, and API edge states with MSW mocks across all failure classes.

## Context
Features from Tasks 015–036 need systematic coverage per Plan B §15.1–§15.3: pure functions (status mapping, permissions, formatting, validation, error mapping, timeline calc, query keys, editor transforms, upload retry) get unit tests; interactive components get loading/empty/failure/disabled/permission-state matrices; MSW covers the full HTTP failure taxonomy so UI behavior is proven without a backend.

## Starting State
Tasks 015–036 done (units under test exist). Depends on Tasks 015–036.

## Scope
Included: unit tests for pure logic, component state-matrix tests, MSW suites for all error/edge classes.
Excluded: backend integration (Task 040), Playwright E2E/visual/a11y/perf (Task 041), CI wiring (Task 042).

## Instructions
1. Add unit tests in `frontend/src/**/__tests__/*.test.ts(x)` (colocated) for: status mapping (pipeline/output/export states), permission helpers (valid-actions-only), formatting (duration/bytes/relative-time/cost), validation (wizard/settings/prefs), error mapping (Task 017 normalizer), timeline calculations (lanes/zoom/seek), query-key factory (tenant scoping), editor transforms (segment edit payloads), upload retry/backoff computation.
2. Add component tests with Testing Library in each feature's `__tests__/`: upload (`MediaUploader` progress/pause/resume/cancel), badges, progress bars, transcript/translation editors, voice selector, review/export panels, notification center, QC list — each asserting loading/empty/failure/disabled/permission-denied states, not just the happy path.
3. Build MSW suites in `frontend/src/test/mocks/handlers.ts` + per-feature `*.mock.test.tsx`: success, 401 (→ login redirect), 403 (→ forbidden state), 404 (→ gone/empty state), 409 (version conflict → refresh offer; duplicate → existing-row toast), 429 (→ backoff notice + retry), 500 (→ error state + correlation id shown), validation-error shape (→ per-field messages), provider-error shape (→ honest degraded state), partial-availability shape (→ partial explanation), stale-version conflict (→ refresh, no data loss).
4. Add backend unit tests in `tests/DubbingPlatform.UnitTests/`: settings validators, consent gate transitions, signed-URL expiry computation, error-envelope mapping, selection version checks — pure-domain, no containers.
5. Enforce coverage in `frontend/vitest.config.ts` (`thresholds: lines/branches/functions/statements ≥ 80` on `src/features`, `src/api`, `src/telemetry`) and `tests/DubbingPlatform.UnitTests` — CI (Task 042) fails below threshold; add `npm run test -- --coverage` output artifact.

## Requirements
- R1: Every listed pure-logic area has unit tests including boundary/negative inputs.
- R2: Every listed component asserts loading/empty/failure/disabled/permission states.
- R3: MSW covers success/401/403/404/409/429/500/validation/provider/partial/stale-conflict with correct UI outcome per class.
- R4: Backend unit tests cover validators, consent transitions, URL expiry, error mapping, version checks.
- R5: Coverage thresholds enforced (≥80% on features/api/telemetry); suite runs green via `npm run test` + `dotnet UnitTests`.

## Edge Cases and Error Handling
- Flaky-timer tests → fake timers + deterministic fixtures only; no wall-clock assertions.
- MSW unhandled-request warnings → fail the test (no silent real-network fallback).
- 429 during MSW suite → assert backoff UI, not instant retry.
- Stale-conflict test must prove local edits survive the refresh offer (no data-loss path).
- Coverage of generated code (`api/generated`) excluded from thresholds.

## Security and Safety Requirements
- Mock fixtures contain zero real secrets/tokens; snapshot tests scrub dynamic URLs/ids.
- Permission-state tests assert denial rendering without naming required roles beyond what UI shows.
- No test disables the scrubber (Task 038) or downgrades auth in shared setup.

## Testing
- This task IS the test layer: colocated `__tests__` per feature + `src/test/mocks/handlers.ts` + `tests/DubbingPlatform.UnitTests/`.
- Type: unit + component (vitest/Testing Library) + MSW; backend unit (xUnit, no containers).

## Validation
```bash
dotnet test --filter FullyQualifiedName~UnitTests
cd frontend && npm run test
cd frontend && npm run test -- --coverage
```

## Completion Criteria
- Unit, component state-matrix, and MSW suites cover all listed areas and failure classes with coverage thresholds met; `UnitTests`, `npm run test`, and coverage runs green.

## Traceability
- Plan B §15.1–§15.3. Depends on Tasks 015–036.




<!-- ===== FILE: 039A-test-infra-coverage.md ===== -->


# Task 039A — Test Infrastructure and Coverage Enforcement

**Required/Optional:** Required
**Complexity:** S

## Goal
Own coverage configuration and the MSW/unit harness contract so gap-closure tasks have a measurable baseline.

## Context
Rewrite/split from oversized Task 039 (which mixed infra, frontend matrices, and backend unit gaps while overlapping feature-level tests). Feature tasks (006–036) author their own specs against harness 046; this task owns the shared config: coverage thresholds, MSW taxonomy conformance, and the gap report that 039B/039C close. No feature specs are authored here.

## Starting State
Depends on Task 046 (harness + taxonomy + factories). Task 039 (combined) is superseded by 039A + 039B + 039C.

## Scope
Included: Vitest coverage config + thresholds, MSW conformance test, gap-report script, ownership doc enforcement.
Excluded: feature specs (006–036 own them), state-matrix closure (039B), backend unit gaps (039C), integration/cross-layer (040A/B), E2E (041A–D).

## Instructions
1. Add coverage config in `frontend/vitest.config.ts` (or `vitest.coverage.ts`): thresholds per area (e.g. `api/, lib/, hooks/` minimums agreed once and recorded), `npm run test -- --coverage` output, uncovered-file report in `coverage/` (gitignored) + `scripts/coverage-gap.mjs` listing files below threshold.
2. Add MSW conformance test `frontend/src/mocks/conformance.spec.ts`: asserts every taxonomy entry from 046 returns the documented envelope (success/401/403/404/409/429/500/validation/provider-error/partial/stale-conflict) — fails on missing or envelope-incorrect handler.
3. Add backend coverage gate reference: `coverlet` (or equivalent) settings in `tests/` props; `dotnet test --collect:"XPlat Code Coverage"` documented; gap list consumed by 039C.
4. Enforce `docs/test-ownership.md` (046): this task fails CI if a feature area has zero specs (presence gate), but does not author those specs — it reports the gap for 039B/039C.
5. Document thresholds + exclusion policy in `docs/coverage.md` (what is measured, what is excluded and why, how to close a gap).

## Requirements
- R1: Coverage thresholds configured and reported for frontend + backend.
- R2: MSW taxonomy 100% conformant (every entry present + envelope-correct).
- R3: Gap script lists every below-threshold file; empty output means 039B/039C are done.
- R4: Presence gate: every feature area has at least one spec; missing area fails with the owning task ID.
- R5: No feature test logic added here — only config, conformance, and reporting.

## Edge Cases and Error Handling
- Coverage tool version drift → lockfile-pinned versions; mismatch fails with version message, not silent zero-coverage.
- Generated code (`api/generated/`) excluded from thresholds by policy, never by accident (explicit exclude list).
- Flaky conformance (port collision) → fixed MSW ports per 046, retry forbidden.

## Security and Safety Requirements
- Coverage reports contain file paths + counts only; never source excerpts with secrets.
- No coverage gate bypass flag without expiry-tracked exception (quarantine policy 046).

## Testing
- This task IS config + `conformance.spec.ts` + `coverage-gap.mjs` + presence gate.
- Type: config smoke + conformance.

## Validation
```bash
npm run test --prefix frontend -- src/mocks/conformance.spec.ts
npm run test --prefix frontend -- --coverage
dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage"
node scripts/coverage-gap.mjs
```

## Completion Criteria
- Coverage config + conformance + gap script + ownership presence gate exist and run; gap list feeds 039B/039C; supersedes the infra third of Task 039.

## Traceability
- Plan B §15.1–§15.3 (infra slice). Split from 039; harness in 046; closure in 039B/039C.



<!-- ===== FILE: 039B-frontend-state-matrices.md ===== -->


# Task 039B — Frontend State-Matrix Gap Closure

**Required/Optional:** Required
**Complexity:** M

## Goal
Close every missing frontend loading/empty/error/permission/async-state test identified by the 039A gap report.

## Context
Split from oversized Task 039. Feature tasks (015–036) author their happy-path + primary specs; this task closes the state matrix (Plan §11.4: loading, skeleton, empty, ready, refreshing, partial, submitting, success, recoverable/blocking failure, unauthorized/forbidden/not-found, offline, cancelled, processing, review) wherever 039A reports it missing. It authors no new features and no backend tests.

## Starting State
Depends on Tasks 046 (harness), 039A (gap list; this task is done when the list is empty for frontend matrices). Feature specs from 015–036 are the baseline — this task adds only what is missing. Task 039 (combined) is superseded.

## Scope
Included: missing component/state tests for upload, badges, progress, editors, voice selector, review/export panels, notification center, QC list, and every screen state matrix entry.
Excluded: test infra (039A), backend unit gaps (039C), integration/cross-layer (040A/B), E2E/visual/a11y/perf (041A–D).

## Instructions
1. Run `node scripts/coverage-gap.mjs` (039A) and close every frontend matrix gap in priority order: auth/session states (019), dashboard/project-list states (020–021), upload states incl. duplicate/rejected (023), workspace/progress states incl. SSE fallback (025–026), editor conflict/stale states (027–028), voice consent/quota states (029), timeline empty/issue states (030), review conflict states (031), QC blocking states (032), output/export partial states (033), notification/activity/cost states (034/035A), settings guards (035B), admin forbidden states (036).
2. Each added spec uses the 046 MSW taxonomy (never live API) and asserts the recovery action per error UX (§11.6: retry/resume/replace/review/contact-admin/wait) — a failure state without its recovery action fails review.
3. No color-only status assertions: every status test also asserts non-color signal (label/icon/text), supporting 041C.
4. Update the gap list to empty for these areas; record any intentional exclusion in `docs/coverage.md` with reason + expiry.

## Requirements
- R1: Every screen in 019–036 has its relevant §11.4 states tested.
- R2: Every recoverable error test asserts its recovery action.
- R3: MSW-backed only; no network, no backend boot required.
- R4: 039A gap script reports zero frontend-matrix gaps on completion.
- R5: No backend test changes in this task.

## Edge Cases and Error Handling
- Gap caused by untestable animation → test structure/presence + reduced-motion path, never pixel assertion (visual is 041B).
- Gap caused by permission-gated UI → test both allowed + forbidden renders.
- New gap introduced by a concurrent feature PR → re-run gap script before closing; close the delta too.

## Security and Safety Requirements
- State tests use synthetic fixtures only (046); no real user data in snapshots.
- Forbidden-state tests assert no data leakage (no hidden-but-rendered sensitive content).

## Testing
- Added specs live beside features: `frontend/src/features/*/*.spec.ts`, `frontend/src/components/**/*.spec.ts` (only the missing ones).
- Type: Vitest component/state.

## Validation
```bash
npm run test --prefix frontend
npm run test --prefix frontend -- --coverage
node scripts/coverage-gap.mjs
```

## Completion Criteria
- 039A reports zero frontend-matrix gaps; all added specs pass; supersedes the frontend-matrix third of Task 039.

## Traceability
- Plan B §11.4, §11.6, §15.1–§15.3 (frontend slice). Split from 039; infra in 039A; backend in 039C.



<!-- ===== FILE: 039C-backend-unit-gaps.md ===== -->


# Task 039C — Backend Unit Gap Closure

**Required/Optional:** Required
**Complexity:** M

## Goal
Close every missing backend unit test identified by the 039A gap report without duplicating endpoint integration tests.

## Context
Split from oversized Task 039. Endpoint integration already belongs to 006–013; this task owns pure backend unit gaps: status mapping, permissions, formatting, validation, error mapping, timeline calc, duration, query construction, editor transforms, upload retry math, settings validation, selection-version logic support, notification dedup logic, signed-URL policy helpers. No frontend, integration, or E2E work here.

## Starting State
Depends on Tasks 046 (fixtures), 039A (gap list). Endpoint tests from 006–013 are the baseline — this task adds only unit gaps. Task 039 (combined) is superseded.

## Scope
Included: missing xUnit unit tests for domain/application pure logic + policy helpers (audit writer, idempotency store contract, correlation helper, signed-URL policy, consent evaluation) where Plan A does not already own them.
Excluded: test infra (039A), frontend matrices (039B), endpoint integration (006–013), cross-layer seams (040A/B), E2E (041A–D).

## Instructions
1. Run coverage gap outputs (039A) for `src/` and close gaps in: `ProjectStatus` projection mapping, permission evaluation, settings-schema validation (001/007), selection-version atomicity support (003), notification dedup + recipient resolution (002), voice-preview quota/consent evaluation (004/010), diagnostics aggregation math (005), error-code mapping (013), idempotency-key handling, correlation propagation, signed-URL expiry computation, consent revocation evaluation.
2. If Plan A already owns a helper (audit writer, idempotency store, correlation), add only the Plan B extension unit tests and reference the Plan A owner explicitly in code comments — never re-implement the helper.
3. Each test is fast and isolated (no containers, no network); any test needing PG/rabbit/redis is re-tagged `Integration` and moved out of this task into its owning endpoint task or 040.
4. Record intentional exclusions in `docs/coverage.md` with reason + expiry.

## Requirements
- R1: Every pure-logic unit listed in §15.1 has a unit test or a recorded exclusion.
- R2: No test in this task requires containers or network.
- R3: No duplication of endpoint integration already asserted in 006–013.
- R4: Shared helpers (audit/idempotency/correlation/URL/consent) have contract tests or explicit Plan A ownership references.
- R5: 039A reports zero backend-unit gaps on completion.

## Edge Cases and Error Handling
- Logic with time dependence → frozen-clock tests only, never `Thread.Sleep`-based assertions.
- Mapping with unknown enum → tested `unknown → safe default + metric` path, never throw-to-500.
- Precision logic (durations ms, completeness fractions) → boundary tests (0, 1, 99/100, overflow).

## Security and Safety Requirements
- Unit tests assert tenant-scoping predicates on every query-building helper (negative tenant test at unit level where applicable).
- No secrets in test data; scrubber (046) applies.

## Testing
- Added specs: `tests/DubbingPlatform.UnitTests/**/*Tests.cs` (only the missing ones).
- Type: xUnit unit.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~UnitTests
dotnet test --filter FullyQualifiedName~UnitTests --collect:"XPlat Code Coverage"
```

## Completion Criteria
- 039A reports zero backend-unit gaps; all added unit tests pass; supersedes the backend-unit third of Task 039.

## Traceability
- Plan B §15.1. Split from 039; infra in 039A; frontend in 039B.



<!-- ===== FILE: 040-integration-cross-layer-tests.md ===== -->


> **REVIEW FIX — SUPERSEDED (split/rewrite):** this file is superseded by `040A-cross-layer-harness.md` (rig) + `040B-cross-layer-seams.md` (seven named seams). Endpoint integration stays in 006–013 and must not be duplicated here.

# Task 040 — Backend Integration and Cross-Layer Tests

**Required/Optional:** Required
**Complexity:** M

## Goal
Verify new endpoints with backend integration tests and prove frontend→backend→infra flows with cross-layer tests.

## Context
Endpoints from Tasks 006–013 need integration coverage per Plan B §15.4; cross-layer flows per §15.5 prove the seams hold (FE→API→PG, upload→storage, start→SSE, review→versioned mutation, export→signed download, voice→invalidation, stale→refresh). Unit/MSW coverage from Task 039 is assumed; this task tests real wiring.

## Starting State
Tasks 006–013 and 039 done. Depends on Tasks 006–013 and 039.

## Scope
Included: backend integration tests (me/preferences/archive/workspace/activity/notification/review/preview/selection/admin), cross-layer Playwright flows (`@cross-layer`).
Excluded: pure unit/component/MSW (Task 039), full E2E journey + non-functional gates (Task 041), CI wiring (Task 042).

## Instructions
1. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/MePreferencesTests.cs` (Testcontainers PostgreSQL): `/me` resolution, preference round-trip + unknown-key 400, tenant isolation on prefs, disabled-user 401.
2. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/ProjectsArchiveTests.cs`: project CRUD, archive/unarchive round-trip (processing semantics unchanged), delete constraints with active run → 409, cross-tenant project access → 403/404.
3. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/WorkspaceActivityTests.cs`: workspace aggregate shape (single-query, no N+1 — assert query count), activity pagination + filters, notification list + dedup constraint + read/read-all, review-context read model with version guard on resolve.
4. Create `tests/DubbingPlatform.IntegrationTests/Endpoints/PreviewSelectionAdminTests.cs`: preview endpoints require membership (anonymous/cross-tenant → 403/404), selection expected-version conflict → 409 with current version, admin endpoints → 403 for non-elevated / 200 for elevated (links to Task 036 `AdminAuthzTests` without duplicating).
5. Create Playwright `e2e/cross-layer/*.spec.ts` tagged `@cross-layer` against real FE+API+PG (+ storage emulator, mocked AI): FE→API→PG create-project round-trip, upload→storage artifact visible, processing-start→SSE progress received, review-resolve→versioned mutation persisted, export→signed-download fetchable, voice-assign→dependent queries invalidated, stale-edit→conflict UI offers refresh preserving local text.

## Requirements
- R1: Integration tests cover /me, preferences, archive, workspace, activity pagination, notification dedup, review context, preview authz, selection conflicts, admin authz.
- R2: Workspace aggregate proven single-shot (query-count assertion, no N+1).
- R3: Cross-layer flows pass against real FE+API+PG for all seven listed seams.
- R4: Every cross-tenant/anonymous negative case fails closed with structured errors + correlation IDs.
- R5: Selection/review conflicts return current-version payloads enabling client refresh (no blind retry).

## Edge Cases and Error Handling
- Concurrent review resolves (double-submit) → idempotency key dedupes, single audit event.
- Export download URL expires mid-test → single refetch path exercised, then success.
- SSE gap during start→SSE flow → polling fallback still delivers progress (Task 026 path).
- Storage emulator reset between upload tests → no cross-test artifact leakage.
- PG container slow-start → health-gated test setup, not fixed sleeps.

## Security and Safety Requirements
- Integration fixtures use synthetic tenants/users; no production-shaped PII.
- Signed URLs and tokens in test logs redacted; failure output shows correlation IDs only.
- Cross-layer specs run against ephemeral environments; no shared/staging mutation.

## Testing
- This task IS the test layer: `tests/DubbingPlatform.IntegrationTests/Endpoints/*.cs` + `e2e/cross-layer/*.spec.ts` (`@cross-layer`).
- Type: backend integration (Testcontainers PostgreSQL + storage emulator) + Playwright cross-layer (mocked AI providers).

## Validation
```bash
dotnet test --filter FullyQualifiedName~IntegrationTests
npx playwright test --grep="@cross-layer"
```

## Completion Criteria
- All endpoint integration tests and all seven cross-layer seams pass against real FE+API+PG with negatives failing closed; `IntegrationTests` + `@cross-layer` green.

## Traceability
- Plan B §15.4–§15.5. Depends on Tasks 006–013 and 039.




<!-- ===== FILE: 040A-cross-layer-harness.md ===== -->


# Task 040A — Cross-Layer Seam Harness

**Required/Optional:** Required
**Complexity:** S

## Goal
Own the cross-layer test harness (real FE + API + PG + storage + transport, mock AI) that named seam specs run against.

## Context
Rewrite/split from oversized Task 040 (which duplicated endpoint integration already owned by 006–013). Endpoint integration stays in 006–013; this task owns the shared cross-layer rig; 040B owns the seven named seam specs. Without this rig the seams cannot run deterministically.

## Starting State
Depends on Tasks 046 (fixtures, reset, auth seeds), 006–013 (endpoints under test), 014 (contract bundle for mock-AI shape). Task 040 (combined) is superseded by 040A + 040B.

## Scope
Included: compose/up script, seeded environment, mock-AI provider, SSE-aware test client, artifact assertion helpers, runbook for local execution.
Excluded: endpoint integration tests (006–013), the seven seam specs themselves (040B), journeys/visual/a11y/perf (041A–D), CI wiring (042B).

## Instructions
1. Add `tests/cross-layer/docker-compose.cross.yml` (or reuse root compose with `cross` profile): API + PostgreSQL + object storage emulator + message transport + mock-AI provider; frontend served from `frontend/dist` (built) or vite preview on fixed port; document ports in `tests/cross-layer/README.md`.
2. Add `tests/cross-layer/harness/` : `seed.ts` (or `seed.cs`) building synthetic tenant/user/project via 046 factories, `mockAi.ts` (deterministic transcript/translation/voice outputs), `sseClient.ts` (event-driven waits from 046, no sleeps), `assertArtifacts.ts` (project row, segments, export file, notification row exist post-run).
3. Add harness smoke `tests/cross-layer/harness.spec.ts` (tagged `@cross-layer`): boots stack, seeds, runs one processing-start → SSE → workspace-read round trip, asserts artifacts, tears down; failing harness blocks 040B (fail-closed).
4. Make mock-AI failure explicit: mock down → `AI_MOCK_UNAVAILABLE` fail-fast, never timeout-hang; record in README.
5. Document local run: `docker compose -f tests/cross-layer/docker-compose.cross.yml up -d && npx playwright test --grep="@cross-layer-harness"` (or equivalent runner).

## Requirements
- R1: Harness boots real FE+API+PG+storage+transport with mock AI only.
- R2: Seed/reset/auth/SSE helpers shared by all 040B specs (no per-spec bespoke boot).
- R3: Harness smoke passes deterministically (seeded data, frozen clock where applicable).
- R4: Mock-AI outage fails fast with named error.
- R5: No endpoint-integration duplication: harness asserts seams, not per-route status codes (those live in 006–013).

## Edge Cases and Error Handling
- Port collision → fixed ports from 046 with collision error naming the holder, not silent skip.
- Partial boot (DB up, broker down) → fail-closed with service matrix, never half-run specs.
- Leftover state from prior run → `reset` runs before seed, always.

## Security and Safety Requirements
- Synthetic tenants only; seeded credentials ephemeral; no prod connection strings in compose (emulator images + env placeholders only).
- Cross-layer logs scrubbed (046 scrubber) before attaching to CI artifacts.

## Testing
- This task IS the harness + `harness.spec.ts` (`@cross-layer-harness`).
- Type: cross-layer rig smoke.

## Validation
```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npx playwright test --grep="@cross-layer-harness"
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

## Completion Criteria
- Cross-layer stack + helpers + harness smoke + README exist; 040B specs can run on this rig; supersedes the harness half of Task 040.

## Traceability
- Plan B §15.4–§15.5 (harness slice). Split from 040; endpoint integration stays in 006–013; seams in 040B.



<!-- ===== FILE: 040B-cross-layer-seams.md ===== -->


# Task 040B — Seven Named Cross-Layer Seam Specs

**Required/Optional:** Required
**Complexity:** M

## Goal
Prove the seven named frontend→backend→infrastructure seams on the 040A rig.

## Context
Split from Task 040. Endpoint integration is already owned by 006–013 and must not be re-asserted here; this task owns only the seams where layers meet (the exact list below). Each seam runs on the 040A harness with real FE+API+PG+storage+transport and mock AI.

## Starting State
Depends on Task 040A (rig + helpers). Endpoints from 006–013 and UI from 019–036 exist as the surfaces under test. Task 040 (combined) is superseded.

## Scope
Included: exactly the seven seam specs below, nothing more.
Excluded: endpoint status-code matrices (006–013), harness itself (040A), journeys/visual/a11y/perf (041A–D).

## Instructions
1. Add specs in `tests/cross-layer/seams/` (tagged `@cross-layer`), one file per seam, all using 040A helpers (no bespoke boot):
  - `seam-upload-storage.spec.ts`: frontend upload → object storage bytes + server validation → ready state (covers 023 + Plan A ingestion).
  - `seam-processing-sse.spec.ts`: processing start → SSE events → workspace refetch as source of truth (covers 024/026 + 008).
  - `seam-review-mutation.spec.ts`: review resolution → versioned mutation + audit + invalidation (covers 031 + 003/009/011).
  - `seam-export-download.spec.ts`: export creation → signed-URL download + completeness metadata (covers 033 + 012A).
  - `seam-voice-invalidation.spec.ts`: voice change → dependent invalidation/retry + consent gate (covers 029 + 010).
  - `seam-stale-conflict.spec.ts`: stale edit → 409 → refresh UX with draft preserved (covers 027/028 + 003/009).
  - `seam-notification.spec.ts`: backend event → durable notification → center render + deep link (covers 034 + 002/012B).
2. Each spec asserts both sides of the seam (client state + server rows/artifacts via `assertArtifacts`), and asserts the failure half (e.g. expired URL, revoked consent, conflicting version) — happy-path-only seams fail review.
3. Tag and quarantine per 046: `@cross-layer` required; flake → quarantine with owner + issue, never silent retry.
4. Record results mapping in `tests/cross-layer/seams/README.md` (seam → owning feature tasks → pass criteria).

## Requirements
- R1: All seven seams pass on the 040A rig.
- R2: No per-route status-code duplication (that coverage stays in 006–013).
- R3: Every seam asserts server-side rows/artifacts, not just UI text.
- R4: Every seam covers its named failure half.
- R5: Quarantine policy enforced (046).

## Edge Cases and Error Handling
- Seam passes alone but fails in full suite → tenant isolation per worker (046) is the fix; shared-tenant shortcuts forbidden.
- Mock-AI nondeterminism → deterministic mock outputs only; any randomness fails the seam.
- SSE flake → event-driven waits (040A) required; fixed-sleep waits rejected in review.

## Security and Safety Requirements
- Seams use synthetic tenants; cross-tenant assertions (where applicable) expect 404 without existence leak.
- Downloaded bytes in export seam are fixture media; scrubbed before CI attach.

## Testing
- Added specs: `tests/cross-layer/seams/*.spec.ts` (seven files).
- Type: cross-layer (Playwright or equivalent runner per 040A README).

## Validation
```bash
docker compose -f tests/cross-layer/docker-compose.cross.yml up -d
npx playwright test --grep="@cross-layer"
docker compose -f tests/cross-layer/docker-compose.cross.yml down
```

## Completion Criteria
- Seven seam specs exist and pass on the shared rig; mapping README complete; supersedes the seam half of Task 040.

## Traceability
- Plan B §15.4–§15.5 (seam slice). Split from 040; harness in 040A; endpoint tests stay in 006–013.



<!-- ===== FILE: 041-e2e-visual-a11y-perf.md ===== -->


> **REVIEW FIX — SUPERSEDED (split) + E2E OWNERSHIP FIX:** this combined file is superseded by `041A-journeys-smoke.md` + `041B-visual-regression.md` + `041C-accessibility.md` + `041D-performance.md`. Feature-level E2E specs (`e2e/features/<name>.spec.ts`, tags `@auth/@dashboard/@projects/@project-create/@upload/@processing-start/@workspace/@progress/@transcript/@translation/@voices/@timeline/@review/@quality/@exports/@notifications/@activity/@settings/@admin`) belong to Tasks 019–036, NOT to 041. 041A–D own journeys/smoke, visual, a11y, perf only.

# Task 041 — Playwright E2E, Full Smoke, Visual, A11y, Performance

**Required/Optional:** Required
**Complexity:** L

## Goal
Prove the §24 end-to-end journey plus visual, WCAG 2.2 AA, and performance-budget gates with Playwright.

## Context
All product flows from Tasks 019–036 must be exercised as user journeys per Plan B §15.6–§15.10 and §24: login through export plus cancel/retry/stale-conflict/401/403 paths; full smoke runs real FE+API+PG+storage+transport with only AI mocked; visual regression pins 12 screens across breakpoints/themes/directions; accessibility targets WCAG 2.2 AA; performance budgets guard interactive latency.

## Starting State
Tasks 019–036 done. Depends on Tasks 019–036.

## Scope
Included: Playwright journey scenarios, full smoke, visual regression (12 screens × desktop/tablet/mobile × dark/light × LTR/RTL), WCAG 2.2 AA audit, perf budgets.
Excluded: unit/component/MSW (Task 039), integration/cross-layer (Task 040), CI wiring (Task 042).

## Instructions
1. Create `e2e/journeys/*.spec.ts` covering in order: login, project create, upload, resume-after-reload, processing start (preflight confirm), live progress, workspace open, transcript edit, translation edit, voice assign, review resolve, output download, export request, cancel, retry, stale-version conflict, forced 401 (expired session → login), forced 403 (viewer attempts edit → forbidden state).
2. Create `e2e/smoke/full-smoke.spec.ts` tagged `@smoke`: the §24 happy path on real FE+API+PG+storage+transport with deterministic fixtures and mocked AI providers; asserts durable artifacts (project row, segments, export file) exist post-run; failing smoke blocks release (Task 042 gates on it).
3. Create `e2e/visual/*.spec.ts` + fixtures in `e2e/visual/fixtures/` tagged `@visual`: 12 screens (login, dashboard, project list, wizard, upload, workspace, transcript, translation, voices, review, QC, outputs/exports) × desktop/tablet/mobile × dark/light × LTR/RTL; deterministic fixtures (seeded data, frozen clock, masked timestamps); never pixel-compare dynamic progress animations — assert their presence structurally, compare static layout only.
4. Create `e2e/a11y/*.spec.ts` run via `npm run test:a11y` (axe-core + manual keyboard scripts): full keyboard traversal (upload → review → export without a mouse), visible focus on every interactive element, labels on all inputs, contrast ≥ 4.5:1 (both themes), `prefers-reduced-motion` disables waveform/progress animation, dialogs trap + restore focus, media players keyboard-operable, live-regions announce progress completion/errors (assert via `aria-live` capture, not screenshots).
5. Define perf budgets in `e2e/perf/budgets.ts` + `e2e/perf/*.spec.ts`: app-interactive ≤ 3s (desktop broadband), project-list render ≤ 1.5s at 200 rows, workspace open ≤ 2s, timeline interaction latency ≤ 100ms p95, segment search ≤ 300ms at 5k segments, media seek ≤ 500ms; budgets asserted in CI with trace artifacts on breach.
6. Stabilize the harness in `e2e/support/`: seeded auth (all roles), storage-emulator reset, SSE-aware waits (event-driven, no fixed sleeps), flake quarantine — any test failing 2/50 runs is quarantined with an owner + issue link, never silently retried.

## Requirements
- R1: All 17 journey scenarios pass in sequence on a clean environment.
- R2: Full smoke proves real FE+API+PG+storage+transport with mocked AI and post-run artifact assertions.
- R3: Visual regression covers 12 screens × 3 breakpoints × 2 themes × LTR/RTL with deterministic fixtures and no dynamic-progress pixel compares.
- R4: WCAG 2.2 AA: keyboard-complete, focus-visible, labeled, contrast-safe, motion-respecting, dialog-correct, media-operable, live-region-announced.
- R5: All six perf budgets hold in CI; breaches attach traces and fail the run.
- R6: Flake quarantine policy enforced — no silent retries, every quarantine has owner + issue.

## Edge Cases and Error Handling
- AI mock outage → journeys fail fast with `AI_MOCK_UNAVAILABLE`, never hang on timeouts.
- Visual diff from font rendering across OS → approved font-substitution list, failures beyond it investigated not blessed.
- A11y violation in third-party widget → wrapper fix or replace, never an axe skip-rule without expiry date.
- Perf breach on shared CI runners → 3-run median before verdict, breach opens a perf issue automatically.
- Stale-conflict journey must end with local text preserved post-refresh (data-loss assertion).

## Security and Safety Requirements
- E2E fixtures contain synthetic PII only; screenshots/screencasts scrubbed of any injected secrets.
- Forced-401/403 journeys assert structured errors + correlation IDs, never stack traces or internal paths.
- Smoke environment credentials ephemeral per run; never committed or logged.

## Testing
- This task IS the E2E layer: `e2e/journeys/`, `e2e/smoke/`, `e2e/visual/`, `e2e/a11y/`, `e2e/perf/` + `e2e/support/`.
- Type: Playwright (Chromium + WebKit + Firefox for journeys/smoke; Chromium for visual/a11y/perf).

## Validation
```bash
npx playwright test
npm run test:a11y
npm run test:visual
```

## Completion Criteria
- All journeys, full smoke, visual matrix, WCAG 2.2 AA audit, and perf budgets pass; quarantine log empty-or-owned; `playwright test`, `test:a11y`, `test:visual` green.

## Traceability
- Plan B §15.6–§15.10, §24. Depends on Tasks 019–036.




<!-- ===== FILE: 041A-journeys-smoke.md ===== -->


# Task 041A — Cross-Feature Journeys and Full Smoke

**Required/Optional:** Required
**Complexity:** M

## Goal
Prove cross-feature journeys and the §24 full smoke without owning feature-level E2E specs.

## Context
Split/rewrite from oversized Task 041 (which wrongly owned feature E2E while feature tasks 019–036 cited `@...` passes they could not author). Ownership fix: feature specs (`e2e/features/<name>.spec.ts`, tags `@auth/@dashboard/@projects/@project-create/@upload/@processing-start/@workspace/@progress/@transcript/@translation/@voices/@timeline/@review/@quality/@exports/@notifications/@activity/@settings/@admin`) belong to Tasks 019–036 and run on harness 046. This task owns only cross-feature journeys + the §24 full smoke; visual/a11y/perf move to 041B/C/D.

## Starting State
Depends on Tasks 019–036 (feature specs exist as the baseline), 040A (stack for smoke), 046 (tags, auth seeds, quarantine). Task 041 (combined) is superseded by 041A + 041B + 041C + 041D.

## Scope
Included: `e2e/journeys/` cross-feature specs + `e2e/smoke/full-smoke.spec.ts` (`@smoke`) on real FE+API+PG+storage+transport with mock AI.
Excluded: feature specs (019–036), visual (041B), a11y (041C), perf (041D), cross-layer seams (040B).

## Instructions
1. Add `e2e/journeys/*.spec.ts`: end-to-end user paths spanning features (login → create → upload → resume-after-reload → start → progress → workspace → transcript edit → translation edit → voice assign → review resolve → output download → export), plus cancel, retry, stale-version conflict (draft preserved post-refresh), forced 401 (expired session → login with `?next=`), forced 403 (viewer attempts edit → forbidden state). Reuse 046 auth/reset/SSE helpers; no fixed sleeps.
2. Add `e2e/smoke/full-smoke.spec.ts` (`@smoke`): the §24 happy path on the 040A-equivalent stack with deterministic fixtures + mocked AI; asserts durable artifacts (project row, segments, export file, notification row) post-run; failing smoke blocks release (042B gates on it).
3. Assert structured errors + correlation IDs on 401/403/409 paths; never stack traces or internal paths.
4. Enforce quarantine policy (046): flake (2/50) → quarantine with owner + issue, never silent retry; record journey→feature-task mapping in `e2e/journeys/README.md`.

## Requirements
- R1: All cross-feature journeys pass in sequence on a clean environment.
- R2: Full smoke proves real FE+API+PG+storage+transport with mocked AI + post-run artifact assertions.
- R3: No feature-spec authorship here (presence check: `e2e/features/` owned by 019–036).
- R4: Stale-conflict journey preserves local draft post-refresh (data-loss assertion).
- R5: Quarantine policy enforced.

## Edge Cases and Error Handling
- AI mock outage → `AI_MOCK_UNAVAILABLE` fail-fast, never hang.
- Smoke passes but a feature spec fails → release still blocked (both gates required in 042B).
- Session expiry mid-journey → re-login resumes at preserved destination, journey continues.

## Security and Safety Requirements
- Synthetic PII only; screenshots/screencasts scrubbed of injected secrets.
- Smoke credentials ephemeral per run; never committed or logged.

## Testing
- Added specs: `e2e/journeys/`, `e2e/smoke/` (+ `README.md` mapping).
- Type: Playwright (Chromium + WebKit + Firefox).

## Validation
```bash
npx playwright test --grep="@journeys"
npx playwright test --grep="@smoke"
```

## Completion Criteria
- Journeys + full smoke pass; feature-spec ownership respected; supersedes the journeys/smoke quarter of Task 041.

## Traceability
- Plan B §15.6–§15.7, §24. Split from 041; feature E2E in 019–036; visual/a11y/perf in 041B/C/D.



<!-- ===== FILE: 041B-visual-regression.md ===== -->


# Task 041B — Visual Regression

**Required/Optional:** Required
**Complexity:** M

## Goal
Pin the 12-screen visual matrix across breakpoints, themes, and directions with deterministic fixtures.

## Context
Split from Task 041. Visual regression is a non-functional gate over feature UI (019–036 + 045); it authors no feature specs and no journeys. Requires the i18n/RTL foundation (045) for the LTR/RTL axis and the harness (046) for deterministic fixtures.

## Starting State
Depends on Tasks 019–036 (screens exist), 045 (RTL + pseudo-locale), 046 (fixtures, frozen clock, masked timestamps). Task 041 (combined) is superseded.

## Scope
Included: `e2e/visual/` specs + fixtures for 12 screens × desktop/tablet/mobile × dark/light × LTR/RTL; structural assertions for dynamic progress (never pixel-compare animations).
Excluded: feature specs (019–036), journeys/smoke (041A), a11y (041C), perf (041D).

## Instructions
1. Add `e2e/visual/*.spec.ts` (tagged `@visual`) + `e2e/visual/fixtures/` (seeded data via 046, frozen clock, masked timestamps/IDs): 12 screens — login, dashboard, project list, wizard, upload, workspace, transcript, translation, voices, review, QC, outputs/exports (+ settings/admin as covered screens where changed).
2. Matrix: desktop (≥1280) / tablet (768–1279) / mobile (<768) × dark/light (where supported) × LTR/RTL (045 `?dir=`); mobile timeline degrades to list inspection per responsive rules — assert the degraded layout, not the desktop editor.
3. Dynamic progress rule: assert progress presence structurally (role/label), compare static layout only; never pixel-compare animated progress/waveform frames.
4. Font-rendering rule: approved font-substitution list for cross-OS diffs; failures beyond it are investigated, not auto-blessed; baselines versioned per theme/direction.

## Requirements
- R1: 12 screens × 3 breakpoints × themes × LTR/RTL baselines exist and pass.
- R2: Deterministic fixtures (no live data, no real clock, masked IDs).
- R3: No pixel comparison of dynamic progress/animation.
- R4: Responsive degradation (mobile list vs desktop editor) asserted, not snapshotted as failure.
- R5: Baseline updates require explicit re-baseline commit with reviewer approval.

## Edge Cases and Error Handling
- New screen added by a feature PR → visual spec required in the same PR or explicitly deferred with issue link.
- RTL-only breakage → fails with direction-labeled diff, never silently skipped.
- Theme unsupported for a screen → matrix entry marked N/A in code with reason, not deleted.

## Security and Safety Requirements
- Visual fixtures synthetic only; screenshots scrubbed (046 scrubber) before CI attach.
- Baselines contain no real user data or signed URLs.

## Testing
- Added specs: `e2e/visual/` + fixtures.
- Type: Playwright visual (Chromium) + `npm run test:visual`.

## Validation
```bash
npx playwright test --grep="@visual"
npm run test:visual
```

## Completion Criteria
- Full matrix green with versioned baselines; supersedes the visual quarter of Task 041.

## Traceability
- Plan B §11.5, §15.8. Split from 041; RTL in 045; harness in 046.



<!-- ===== FILE: 041C-accessibility.md ===== -->


# Task 041C — Accessibility Audit (WCAG 2.2 AA)

**Required/Optional:** Required
**Complexity:** M

## Goal
Prove WCAG 2.2 AA across keyboard, focus, labels, contrast, motion, dialogs, media, and live regions.

## Context
Split from Task 041. Accessibility behavior must be built into primitives (016) and feature screens (019–036); this task is the audit gate, not the implementation. It runs axe-core plus manual keyboard scripts over the same deterministic fixtures as 041B.

## Starting State
Depends on Tasks 016 (primitives carry focus/label/contrast/motion behavior), 019–036 (screens under audit), 046 (keyboard scripts harness). Task 041 (combined) is superseded.

## Scope
Included: `e2e/a11y/` axe + keyboard/focus/contrast/motion/dialog/media/live-region checks; `npm run test:a11y` gate.
Excluded: feature/a11y fixes (owned by 016/019–036), visual (041B), perf (041D), journeys (041A).

## Instructions
1. Add `e2e/a11y/*.spec.ts` run via `npm run test:a11y` (axe-core + manual scripts): full keyboard traversal (upload → review → export without a mouse), visible focus on every interactive element, labels on all inputs, contrast ≥ 4.5:1 (both themes), `prefers-reduced-motion` disables waveform/progress animation, dialogs trap + restore focus, media players keyboard-operable, live regions announce progress completion/errors (assert via `aria-live` capture).
2. Critical live events must not spam screen readers: assert announcement count caps for progress streams (026) — completion/error announced once, intermediate ticks suppressed.
3. Third-party widget violation → wrapper fix or replace in owning feature task; axe skip-rules require expiry dates and are rejected in review without one.
4. Record per-screen results in `e2e/a11y/README.md` (screen → criteria → pass/fail/waiver-with-expiry).

## Requirements
- R1: Keyboard-complete: every flow achievable without a mouse.
- R2: Focus-visible + labeled + contrast-safe in both themes.
- R3: Motion-respecting (`prefers-reduced-motion` disables non-essential animation).
- R4: Dialog-correct (trap + restore) and media-operable.
- R5: Live-region-announced without spam (capped progress announcements).

## Edge Cases and Error Handling
- Axe false positive → manual verification recorded; skip-rule only with expiry + issue link.
- Focus loss after modal close → fails (must restore to invoker).
- Dynamic content (SSE progress) → announcements asserted on completion/error only, never per-tick.

## Security and Safety Requirements
- A11y tests assert forbidden content is not exposed to assistive tech (e.g. hidden secrets never in `aria-label`s).
- Test recordings contain synthetic data only.

## Testing
- Added specs: `e2e/a11y/` + README matrix.
- Type: Playwright a11y (Chromium) + axe-core.

## Validation
```bash
npm run test:a11y
npx playwright test --grep="@a11y"
```

## Completion Criteria
- WCAG 2.2 AA audit green with per-screen matrix; supersedes the a11y quarter of Task 041.

## Traceability
- Plan B §10.10, §11.4 (live states), §15.9. Split from 041; behavior in 016/019–036.



<!-- ===== FILE: 041D-performance.md ===== -->


# Task 041D — Performance Budgets

**Required/Optional:** Required
**Complexity:** M

## Goal
Enforce frontend performance budgets on representative fixtures in CI.

## Context
Split from Task 041. Performance practices (code splitting, virtualization, preview media, memoization, debounced timeline) are built in 015/017/025/027/030; this task is the measurement gate with six budgets, traces on breach, and a median-of-three rule for shared runners.

## Starting State
Depends on Tasks 015 (splitting), 017 (query efficiency), 025/027/030 (workspace/list/timeline under test), 046 (representative fixtures). Task 041 (combined) is superseded.

## Scope
Included: `e2e/perf/budgets.ts` + `e2e/perf/*.spec.ts` for the six budgets below; CI breach handling.
Excluded: perf fixes (owning feature tasks), visual (041B), a11y (041C), journeys (041A), backend load testing (out of Plan B scope unless 042B adds it).

## Instructions
1. Define budgets in `e2e/perf/budgets.ts`: app-interactive ≤ 3s (desktop broadband), project-list render ≤ 1.5s at 200 rows, workspace open ≤ 2s, timeline interaction latency ≤ 100ms p95, segment search ≤ 300ms at 5k segments, media seek ≤ 500ms. Budgets versioned; changes require recorded reason.
2. Add `e2e/perf/*.spec.ts` using representative fixtures (046, never idealized hardware): each spec measures one budget with trace capture; breach attaches trace + fails the run and opens (or updates) a perf issue automatically per repo policy.
3. Shared-runner rule: breach verdict uses 3-run median before fail; single-run spike quarantines per 046 instead of failing release.
4. Assert performance practices structurally where cheap: route-level splitting present (015), list virtualization active at 200+ rows, timeline uses preview peaks not archival (030), search debounced — failures point at the owning task.

## Requirements
- R1: All six budgets measured on representative fixtures.
- R2: Breaches attach traces and fail (or quarantine-then-fail per median rule).
- R3: No idealized-hardware numbers; fixture sizes recorded in the spec.
- R4: Structural practice assertions present alongside timing.
- R5: Budget changes versioned with reason.

## Edge Cases and Error Handling
- CI runner slowdown → median-of-three verdict, never single-spike release block without quarantine record.
- Fixture growth (10k segments) → budget holds or explicit re-baseline with reason; silent threshold bump rejected.
- Trace upload failure → run still fails with timing evidence; missing-trace warning attached.

## Security and Safety Requirements
- Traces scrubbed (URLs, tokens, media bytes) before CI attach per 038 allowlist.
- Perf specs use synthetic fixtures only.

## Testing
- Added specs: `e2e/perf/` + `budgets.ts`.
- Type: Playwright perf (Chromium) with trace.

## Validation
```bash
npx playwright test --grep="@perf"
```

## Completion Criteria
- Six budgets green (or median-rule quarantined with owner + issue); supersedes the perf quarter of Task 041.

## Traceability
- Plan B §10.11, §15.10. Split from 041; practices in 015/017/025/027/030; fixtures in 046.



<!-- ===== FILE: 042-ci-contract-management.md ===== -->


# Task 042 — CI Pipelines and Contract Management

**Required/Optional:** Required
**Complexity:** M

## Goal
Gate backend extensions and frontend on build/test/scan/contract checks with OpenAPI breaking-change detection blocking merges.

## Context
Backend work (Tasks 001–013, 037–038) and frontend work (Tasks 014–036) need reproducible pipelines per Plan B §16.1–§16.3; the generated TS client (Task 014) must never drift from the API; `deploy/verify.sh` is the release gate. Coverage thresholds from Task 039 and smoke from Task 041 are enforced here, not just defined.

## Starting State
Tasks 014, 039–040 done. Depends on Tasks 014 and 039–040.

## Scope
Included: backend CI, frontend CI, OpenAPI breaking-change detection, `deploy/verify.sh` gate.
Excluded: hosting/rollout (Task 043), E2E scenario authorship (Task 041), test authorship (Tasks 039–040).

## Instructions
1. Create `.github/workflows/backend.yml`: restore → build (warnings-as-errors, `TreatWarningsAsErrors=true`) → `UnitTests` → `IntegrationTests` (Testcontainers PG + storage emulator services) → contract tests (OpenAPI snapshot match) → EF migration-compat check (script applies on previous-release schema) → image scan (Trivy, fail on HIGH/CRITICAL) → SBOM (Syft artifact) → sign (cosign) → publish (versioned tag only).
2. Create `.github/workflows/frontend.yml`: install (locked `npm ci`) → lint → typecheck → unit/component (`npm run test` + coverage thresholds from Task 039, fail below) → build → codegen-verify (`make generate-api` + `git diff --exit-code` on `src/api/generated/`) → E2E (`@cross-layer` + journeys + `@smoke`) → `@visual` (baseline compare) → `test:a11y` → `npm audit` (fail on high) → image build + publish (versioned tag only).
3. Implement OpenAPI breaking-change detection in `.github/workflows/contract.yml` + `scripts/openapi-diff.sh`: compare PR OpenAPI against `main` (oasdiff); removed/renamed paths, removed required fields, tightened enums, changed auth scope → fail with annotated diff; additive changes pass; version bump required on any contract change (`openapi:version` check).
4. Create `deploy/verify.sh`: post-deploy gate — health endpoint, `/openapi.json` version match vs deployed tag, smoke spec (`@smoke`) against the deployed environment, rollback trigger on any failure (hands off to Task 043 runbook); script exits non-zero with machine-readable failure reason.
5. Set branch protection expectations in `docs/ci-branch-protection.md`: required checks (both pipelines + contract), no admin bypass without recorded reason, flake-quarantine list (Task 041) reviewed weekly; document the red-build protocol (revert-first for `main`, owner-assigned within 1h).

## Requirements
- R1: Backend pipeline fails on warnings, test failures, migration incompat, image HIGH/CRITICAL, or missing SBOM/signature.
- R2: Frontend pipeline fails on lint/type/coverage/codegen-drift/E2E/visual/a11y/audit failures.
- R3: Breaking OpenAPI changes fail with annotated diff; additive changes pass; version bump enforced.
- R4: `deploy/verify.sh` gates releases and triggers rollback on failure with machine-readable reasons.
- R5: Only versioned tags publish images; `latest`/untagged pushes never publish.

## Edge Cases and Error Handling
- Testcontainers unavailable on a runner → job fails closed with `INFRA_UNAVAILABLE`, never silently skips integration tests.
- Visual baseline missing for a new screen → job marks `BASELINE_NEEDED` and blocks, requires explicit baseline approval PR.
- `npm audit` new advisory on existing lockfile → fail with advisory id + upgrade path, owner auto-assigned.
- Contract diff tool version drift → pin oasdiff version in workflow, checksum-verified.
- Verify.sh partial failure (health ok, smoke fails) → rollback + `SMOKE_FAILED` reason artifact.

## Security and Safety Requirements
- Images signed (cosign) with provenance; unsigned images never deployable (admission note for Task 043).
- SBOM attached per image; secrets-scan (gitleaks) on every PR touching `deploy/` or workflows.
- CI OIDC only — no long-lived cloud credentials in repo secrets beyond the documented bootstrap set.

## Testing
- Pipeline definitions are validated by dry-run (`act -n` or workflow lint) + a deliberate-break canary PR (remove a required field → contract job fails; break coverage → frontend job fails).
- `deploy/verify.sh` tested against ephemeral review environment before release use.
- Type: pipeline config + shell; canary-PR verification recorded in the PR checklist.

## Validation
```bash
bash deploy/verify.sh
gh workflow list --all
git diff --exit-code frontend/src/api/generated/
```

## Completion Criteria
- Both pipelines + contract detection green on PR with canary-break proof, versioned signed publishes only, and `deploy/verify.sh` gating releases; CI green + verify.sh pass.

## Traceability
- Plan B §16.1–§16.3. Depends on Tasks 014 and 039–040.

## Review Fix — Renamed Scope (042B Full Gates)
- **This file is now 042B scope:** full gates (integration/contract, migration compat, image scan/SBOM/sign/publish, OpenAPI drift via oasdiff or equivalent, visual/a11y/perf, audit-blocking). Basic per-PR gates (typecheck/lint/unit/build) live in `042A-basic-ci.md` and must land first with harness 046.



<!-- ===== FILE: 042A-basic-ci.md ===== -->


# Task 042A — Basic CI Early Gate (Typecheck, Lint, Unit, Build)

**Required/Optional:** Required
**Complexity:** S

## Goal
Gate every PR on typecheck, lint, unit tests, and build long before full contract/security/perf gates exist.

## Context
Every task lists validation commands, but full CI lived in Task 042 after nearly all work — so early feature work had no shared red/green gate. This task adds the minimal always-on pipeline; Task 042 (now 042B scope) keeps full gates (contract drift, image scan/SBOM/sign, visual/a11y/perf, audit). Basic CI must land with the harness (046) before feature work (019+).

## Starting State
Depends on Tasks 015 (frontend scaffold), 046 (harness so `npm run test` and seeded configs exist). Backend (Plan A + 001) builds with `dotnet build`.

## Scope
Included: backend restore/build/unit, frontend install/typecheck/lint/unit/component/build, PR trigger, fail-closed on warnings/errors, CI timing baseline.
Excluded: integration/contract/E2E/visual/a11y (042B), container build/scan/SBOM/sign/publish (042B), OpenAPI drift (014/042B), Testcontainers suites (042B).

## Instructions
1. Create `.github/workflows/basic-ci.yml`: triggers `pull_request` + `push` to main; jobs `backend-basic` (setup-dotnet 10, cache NuGet, `dotnet restore`, `dotnet build --warnaserror`, `dotnet test --filter FullyQualifiedName~UnitTests`), `frontend-basic` (setup-node pinned, cache npm, `npm ci`, `npm run typecheck`, `npm run lint`, `npm run test`, `npm run build`). Fail on TS errors, lint errors, test failures, build failures, or compiler warnings.
2. Pin toolchain versions (dotnet SDK from `global.json`, node version file) so local `Validation` blocks reproduce CI; document versions at top of the workflow file.
3. Handle Testcontainers absence: basic CI runs unit-only (no containers); if a unit test requires containers it must be tagged `Integration` and excluded here (fail-closed rule: untagged container dependency fails the job with `TESTCONTAINERS_REQUIRED_BUT_UNAVAILABLE` rather than silently skipping).
4. Add `npm audit --omit=dev --audit-level=high` as non-blocking annotation in basic CI (blocking audit lives in 042B); record decision in workflow comments.
5. Document in `docs/ci.md` (one page or section): what basic CI runs, how to reproduce locally (`dotnet build`, `npm run typecheck/lint/test/build`), and that 042B adds the remaining gates.

## Requirements
- R1: Every PR runs backend + frontend basic jobs; either job failing blocks merge.
- R2: Warnings-as-errors enforced (`--warnaserror` / non-zero lint/typecheck exit).
- R3: Unit-only: no Testcontainers required; container-dependent tests excluded by tag, never silently skipped.
- R4: Local reproduction commands match CI exactly (same commands as task Validation blocks).
- R5: Audit is advisory here; blocking audit is 042B.

## Edge Cases and Error Handling
- Cache poisoning (stale NuGet/npm) → cache keys include lockfiles; cache-miss falls back to clean install, never partial restore.
- Frontend build passes but typecheck fails → job fails (build alone is not sufficient).
- Flaky unit test → quarantine per 046 policy with owner + issue; no silent retry in CI.

## Security and Safety Requirements
- CI runs on `pull_request` with read-only permissions where possible; no secrets injected into basic jobs.
- No publishing or image signing in this workflow (042B owns release-adjacent permissions).

## Testing
- This task IS CI config: validated by opening a test PR (or `act -j backend-basic` / `act -j frontend-basic` where available) showing red on injected failure and green on fix.
- No C# product tests added here beyond using existing `UnitTests` filter.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~UnitTests
npm run typecheck --prefix frontend
npm run lint --prefix frontend
npm run test --prefix frontend
npm run build --prefix frontend
```

## Completion Criteria
- `basic-ci.yml` exists, runs per-PR, fails closed on warnings/errors/test failures, reproduces locally, and documents the 042B follow-up gates.

## Traceability
- Plan B §16.1–§16.2 (CI subset), §22 (early B-2/B-3 enablement). Existing Task 042 retains full gates as 042B; this task is the early prerequisite.



<!-- ===== FILE: 043-hosting-rollout-operations.md ===== -->


> **REVIEW FIX — SUPERSEDED (split):** this combined file is superseded by `043A-hosting-env-cdn.md` (hosting/env/CDN) + `043B-rollout-rollback.md` (expand/contract + rollback) + `043C-runbooks-backup.md` (runbooks/backup drills). New work goes to 043A/B/C.

# Task 043 — Frontend Hosting, Env Config, Rollout, Operations

**Required/Optional:** Required
**Complexity:** M

## Goal
Ship the static frontend behind CDN with safe config injection and roll out the backend via migration-job-first deploys with rollback and runbooks.

## Context
CI (Task 042) produces versioned signed images; the frontend is a static SPA (`frontend/dist`), the backend a modular monolith with expand/contract migrations (Tasks 001–004); topology is Browser→CDN→Static→API with no direct DB/broker/Redis access. This task makes releases boring: compatible migrations, flag-gated UI, verified rollout, one-command rollback.

## Starting State
Task 042 done. Depends on Task 042.

## Scope
Included: CDN/static host, `VITE_*` injection, health/version endpoint, topology enforcement, migration-first rollout + compat + flags, rollback, runbooks, backup coverage.
Excluded: pipeline authorship (Task 042), E2E scenarios (Task 041), optional-enrichment surfaces (Task 044).

## Instructions
1. Create `Dockerfile.frontend` + `deploy/cdn/` (or provider equivalent): serve `frontend/dist` with SPA fallback (`/index.html` for unknown non-`/api` routes), hashed-asset immutable cache (`Cache-Control: public, max-age=31536000, immutable`), short HTML cache (`no-cache`), HTTPS + compression + CSP/security headers (matching Task 037 policy, no `unsafe-inline`).
2. Implement safe config injection in `frontend/src/config/env.ts` + `deploy/config-inject.sh`: only `VITE_*` non-sensitive keys baked per environment (API base URL, CDN origin, version tag); secrets rejected at build (`scripts/vite-env-audit.sh` fails on `SECRET|KEY|TOKEN|PASSWORD` in `VITE_*`); runtime version exposed at `/version.json` (version + commit + openapi version).
3. Add `GET /health` + `GET /version` in `src/DubbingPlatform.Api/Endpoints/HealthEndpoints.cs`: liveness (process up), readiness (DB + storage + broker reachable, migration current); readiness failure → container unhealthy, never traffic-serving; version reports build tag + migration head.
4. Enforce topology in `deploy/k8s/`: `networkpolicies.yaml` allowing only CDN→static, browser/CDN→API ingress; no ingress to DB/broker/Redis except from API + migration job; document the data-flow diagram in `docs/topology.md` (Browser→CDN→Static→API→{PG, storage, broker}).
5. Implement rollout in `deploy/k8s/migration-job.yaml` + `docs/rollout.md`: migration job runs before API rollout (expand phase), API deploys with backward compat to previous frontend (contract window ≥ 1 release), new UI behind flags (Task 036 `FlagsPanel`); contract phase (drop deprecated columns/endpoints) only after two green releases.
6. Document rollback in `docs/runbooks/rollback.md`: `kubectl rollout undo deployment/api` + frontend CDN version pin; DB expand/contract note — rollback never reverses a contract migration (forward-fix only) with the decision table for expand-vs-contract states.
7. Write runbooks in `docs/runbooks/`: `auth-outage.md`, `cdn-cache-poison.md`, `sse-degraded.md`, `deploy-failed.md`, `notification-backlog.md`, `contract-drift.md`, `upload-surge.md`, `review-surge.md` — each with symptoms, 5-minute triage, mitigation, escalation, and postmortem trigger.
8. Cover backup in `docs/runbooks/backup-restore.md`: scheduled snapshots for users/prefs/notifications/activity/memberships/previews/metadata with restore drill (quarterly) and RPO/RTO targets; media artifacts covered by storage versioning policy reference.

## Requirements
- R1: SPA fallback + hashed immutable cache + short HTML cache + HTTPS/compression/CSP headers verified by header assertions.
- R2: Only safe `VITE_*` keys injected; secrets in `VITE_*` fail the build; `/version.json` + `/version` agree.
- R3: Readiness gates traffic on DB/storage/broker/migration currency; liveness is process-only.
- R4: Topology enforced by network policy — no direct browser→DB/broker/Redis path exists.
- R5: Migration-job-before-API ordering with backward compat + flag-gated UI; contract phase only after two green releases.
- R6: One-command rollback for API + CDN pin; DB rollback follows expand/contract forward-fix rule.
- R7: All eight runbooks + backup/restore drill docs exist and are linked from the ops index.

## Edge Cases and Error Handling
- Migration job fails → API rollout blocked, previous version keeps serving, alert fires with job logs link.
- CDN serves stale HTML after deploy → version-mismatch banner prompts reload (client compares `/version.json` on focus).
- Flag service unavailable at boot → flags fail closed to last-known-good snapshot, UI marks flagged areas `DegradedState`.
- Backup restore drill finds gap (e.g. previews missing) → gap logged as release-blocker until policy fixed.
- Rollback during contract window → compatibility matrix checked first; incompatible rollback refused with reason.

## Security and Safety Requirements
- No secrets in images, `VITE_*` bundles, CDN configs, or runbooks; header audit in CI (Task 042) covers CSP/CORS.
- Network policies default-deny with explicit allows; migration job uses least-privilege DB role.
- Backup media encrypted at rest; restore drill access logged and time-boxed.

## Testing
- Header/topology assertions in `deploy/tests/hosting.test.sh` (fallback, cache headers, CSP, HTTPS redirect).
- Rollout dry-run: `kubectl apply --dry-run=client -f deploy/k8s/` + migration-job ordering test on ephemeral cluster.
- Runbook drill: tabletop walkthrough recorded per release; findings filed as issues.
- Type: shell assertions + k8s dry-run + drill.

## Validation
```bash
cd frontend && npm run build
docker build -f Dockerfile.frontend .
kubectl apply --dry-run=client -f deploy/k8s/
bash deploy/tests/hosting.test.sh
```

## Completion Criteria
- CDN-hosted SPA with safe config, gated readiness, enforced topology, migration-first rollout, one-command rollback, eight runbooks, and backup drills; build + image + dry-run + hosting tests pass.

## Traceability
- Plan B §17.1–§17.4, §18.1–§18.4, §23.5. Depends on Task 042.




<!-- ===== FILE: 043A-hosting-env-cdn.md ===== -->


# Task 043A — Frontend Hosting, Env Config, CDN

**Required/Optional:** Required
**Complexity:** M

## Goal
Ship the static frontend behind CDN/ingress with safe env injection and correct caching/headers.

## Context
Split from oversized Task 043 (hosting + rollout + runbooks). This task is the hosting slice: static assets, SPA fallback, immutable hashed-asset caching, short-lived HTML, HTTPS/compression/CSP headers, `VITE_*` injection, health/version endpoint. Rollout/rollback moves to 043B; runbooks/backup to 043C. Topology constraint: Browser → CDN/Ingress → Static Host → API; frontend never talks directly to PG/RabbitMQ/Redis/workers.

## Starting State
Depends on Tasks 015 (build output), 042A (basic CI green), 045 (locale bundles ship here). Task 043 (combined) is superseded by 043A + 043B + 043C.

## Scope
Included: `Dockerfile.frontend` (or static-host config), CDN/ingress manifests, env allowlist, caching + headers, health/version endpoint.
Excluded: migration rollout/rollback (043B), runbooks/backup drills (043C), backend CI gates (042B).

## Instructions
1. Add `Dockerfile.frontend` (nginx or equivalent static host): multi-stage (`npm run build` → static root), SPA fallback (`/index.html`), immutable caching for hashed assets (`Cache-Control: public, max-age=31536000, immutable`), short-lived HTML (`no-cache`), gzip/brotli, HTTPS redirect at ingress, CSP headers (from 037 policy), `X-Content-Type-Options: nosniff`.
2. Add `deploy/k8s/frontend/` (or CDN equivalent): `deployment.yaml` (2+ replicas), `service.yaml`, `ingress.yaml` (TLS, frontend host), `configmap.yaml` for non-secret `VITE_*` (`VITE_API_BASE_URL, VITE_ENVIRONMENT, VITE_APP_VERSION, VITE_ENABLE_ANALYTICS, VITE_ENABLE_DIAGNOSTICS, VITE_ENABLE_EXPERIMENTAL_FEATURES, VITE_SENTRY_DSN`); never secrets; document injection at deploy time (build-arg vs runtime-config choice recorded).
3. Add health/version endpoint: `/healthz` (static 200) + `/version.json` (`{version, commit, builtAt}`) where practical; CDN availability + frontend error rate feed 038/043C monitors.
4. Verify topology: frontend calls API origin only (allowlist); add `scripts/check-frontend-topology.mjs` failing on direct DB/broker/redis/worker references in `frontend/src`.
5. Document in `deploy/frontend/README.md`: hosting diagram, cache policy, env table, SPA fallback behavior, rollback pointer (043B).

## Requirements
- R1: Static hosting serves SPA fallback correctly (deep links work).
- R2: Hashed assets immutable-cached; HTML short-lived; HTTPS + compression + CSP headers present.
- R3: Only documented `VITE_*` keys injected; no secrets in image or configmap.
- R4: Topology check passes (no direct infra references from frontend).
- R5: Health/version endpoints reachable through CDN/ingress.

## Edge Cases and Error Handling
- Stale HTML cached at edge → version mismatch banner (API `X-App-Version` vs `/version.json`) prompts reload, never silent breakage.
- Missing `VITE_API_BASE_URL` → build fails closed, not runtime undefined-origin calls.
- CDN outage → 043C runbook owns response; this task only ensures origin serves directly as fallback where configured.

## Security and Safety Requirements
- CSP from 037 enforced at hosting layer; CORS allowlist (backend) matches frontend origin exactly, no wildcard for authed endpoints.
- No source maps with secrets to CDN in prod (or access-restricted where kept).

## Testing
- Config tests: `deploy/frontend/*.test.mjs` (or terratest equivalent) asserting cache headers, fallback routes, header presence, env allowlist.
- Topology: `scripts/check-frontend-topology.mjs` in validation.

## Validation
```bash
npm run build --prefix frontend
docker build -f Dockerfile.frontend -t dubbing-frontend:check .
node scripts/check-frontend-topology.mjs
kubectl apply --dry-run=client -f deploy/k8s/frontend/
```

## Completion Criteria
- Hosting + CDN + env + headers + topology check exist and dry-run valid; supersedes the hosting third of Task 043.

## Traceability
- Plan B §17.1–§17.2. Split from 043; rollout in 043B; runbooks/backup in 043C.



<!-- ===== FILE: 043B-rollout-rollback.md ===== -->


# Task 043B — Expand/Contract Rollout and Rollback

**Required/Optional:** Required
**Complexity:** M

## Goal
Roll out backend extensions with expand/contract compatibility and a rehearsed rollback path.

## Context
Split from Task 043. Backend extensions (001–005, 008) must deploy over the Plan A system without downtime: additive migrations first, backward-compatible API/workers during the window, flag-gated UI exposure, then contraction. This task owns the release-engineering slice; hosting is 043A, runbooks/backup 043C.

## Starting State
Depends on Tasks 001–005 (migrations exist), 042B or 042A (CI green for the release cut), 043A (hosting target exists). Task 043 (combined) is superseded.

## Scope
Included: migration-job-before-API ordering, expand/contract window, backward-compat verification, flag-gated UI, rollback procedure + rehearsal record.
Excluded: hosting/CDN/env (043A), runbooks/backup drills (043C), full CI gates (042B).

## Instructions
1. Add `deploy/k8s/migration-job.yaml` (or reuse Plan A job): runs additive migrations before API rollout (`initContainer` wait or ArgoCD/helm pre-upgrade hook, `backoffLimit:3`); migration failure blocks rollout (API Deployment waits).
2. Enforce expand/contract window of one release: migrations additive-only (nullable columns / new tables); old API/worker code tolerates new schema; new code tolerates old schema without new columns; record the window in `deploy/rollout.md` with the contraction follow-up task.
3. Verify backward compat in CI staging: deploy new DB + old API image smoke (`/health/ready` + one workspace read), then new API; document matrix in `deploy/rollout.md`.
4. Flag-gate new UI surfaces (043A hosting serves both): feature flags are config-driven rollout only, never authorization (per §6.9); list flags + default states in `deploy/rollout.md`.
5. Document + rehearse rollback: `kubectl rollout undo` for API/workers/frontend, DB rollback = forward-fix only (no down-migration on prod data) unless explicitly approved; record last rehearsal date + result in `deploy/rollout.md`.

## Requirements
- R1: Migration job succeeds before API ready; failure blocks rollout.
- R2: Additive-only migrations during the window (CI check or reviewer gate).
- R3: Old-code/new-schema and new-code/old-schema smoke passes in staging.
- R4: Flags documented with defaults; no flag replaces authorization.
- R5: Rollback rehearsed and recorded (not just documented).

## Edge Cases and Error Handling
- Migration needs non-additive change → split into two releases (expand then contract); single-release destructive migration rejected.
- Rollback during active runs → runs continue on durable backend state; UI shows pre-existing version until re-rollout (no run cancellation by rollback).
- Flag left on permanently → expiry review date required for every flag.

## Security and Safety Requirements
- Migration job uses least-privilege DB role; secrets via external manager, never in manifests.
- Rollback does not restore deleted data (retention/deletion semantics from Plan A preserved).

## Testing
- Release tests: staging compat matrix (old-API/new-DB, new-API/old-DB where applicable) + rollback rehearsal log.
- Type: deploy verification, not unit.

## Validation
```bash
kubectl apply --dry-run=client -f deploy/k8s/
bash deploy/verify.sh
kubectl rollout history deployment/dubbing-api
```

## Completion Criteria
- Migration ordering + compat window + flag list + rehearsed rollback exist and are recorded; supersedes the rollout third of Task 043.

## Traceability
- Plan B §2.3, §6.9, §17.3, §23.5 (rollout slice). Split from 043; hosting in 043A; runbooks in 043C.



<!-- ===== FILE: 043C-runbooks-backup.md ===== -->


# Task 043C — Incident Runbooks and Backup Drills

**Required/Optional:** Required
**Complexity:** S

## Goal
Provide product incident runbooks and backup/restore drills for the new durable entities.

## Context
Split from Task 043. Plan A owns backend runbooks; this task adds the product surfaces (auth/CDN/SSE/deploy/notification-backlog/contract-drift/upload-surge/review-surge) plus backup coverage for new tables (tenant users, preferences, notifications, activity, memberships, voice previews, extended project metadata). Hosting is 043A, rollout 043B.

## Starting State
Depends on Tasks 038 (monitors that trigger runbooks), 043A (hosting topology runbooks reference), 043B (rollback procedure runbooks link to). Task 043 (combined) is superseded.

## Scope
Included: eight product runbooks, backup inclusion + restore drill, support-diagnostics pointers (role-restricted + audited).
Excluded: hosting/env (043A), rollout/rollback mechanics (043B), backend Plan A runbooks (referenced, not rewritten).

## Instructions
1. Add `docs/runbooks/` entries (one page each, same template: symptoms → triage queries → mitigation → escalation → postmortem link): `auth-outage.md`, `cdn-outage.md`, `sse-outage.md` (fallback-polling verification), `frontend-deploy-failure.md`, `notification-backlog.md`, `contract-drift.md`, `upload-surge-failure.md`, `review-backlog-surge.md`. Each names the dashboard/monitor (038/043A), the diagnostics view (036), and the rollback link (043B) where applicable.
2. Extend backup docs/job to include new durable entities: tenant users, preferences, notifications, activity events, memberships, voice preview jobs, extended project metadata; record retention + restore priority in `docs/backup.md`.
3. Run a restore drill on staging (new tables only OK): restore → verify counts + spot-read one project/user/notification; record date + result + gaps in `docs/backup.md`.
4. Point support diagnostics at 036 views (workspace/run state, review/notification backlog, queue/DLQ/leases/orphans, provider health, cost anomalies); restate role restriction + audit requirement in each runbook.

## Requirements
- R1: Eight runbooks exist with uniform template and monitor/diagnostic/rollback links.
- R2: Backup covers all seven new-entity groups.
- R3: Restore drill recorded (date + result), not just documented.
- R4: Every runbook states role restriction + audit for privileged actions.
- R5: No backend runbook duplication (link Plan A, do not fork).

## Edge Cases and Error Handling
- Runbook query fails during incident (monitor down) → each runbook includes degraded-mode triage (logs-first path).
- Backup restore partially fails → drill records scope of loss + forward-fix, never silent partial success.
- Review-surge runbook conflicts with 031 UX copy → runbook links the UI path instead of duplicating instructions.

## Security and Safety Requirements
- Runbooks contain example IDs only (synthetic); no real tenant/user IDs, tokens, or signed URLs.
- Restore drill on staging only unless explicitly approved for prod-like env.

## Testing
- Drill-based: restore drill log + runbook tabletop (walk one runbook against staging, record gaps).
- Type: operational verification.

## Validation
```bash
ls docs/runbooks/
grep -l "Escalation" docs/runbooks/*.md
grep -E "tenant users|preferences|notifications|activity|memberships|voice preview|project metadata" docs/backup.md
```

## Completion Criteria
- Eight runbooks + backup inclusion + recorded drill exist; supersedes the ops third of Task 043.

## Traceability
- Plan B §18.1–§18.4. Split from 043; monitors in 038; diagnostics UI in 036; rollback in 043B.



<!-- ===== FILE: 044-optional-enrichment-local-ai.md ===== -->


# Task 044 — Optional Enrichment / Local AI UX

**Required/Optional:** Optional
**Complexity:** S

## Goal
Expose flag-gated optional enrichment (video intelligence, lip-sync score with separate asset, local-GPU operator health) so failures never block core flows.

## Context
Core product (Tasks 001–043) completes without this task. Enrichment surfaces from Plan B §19.1–§19.3 ride behind feature flags (Task 036 `FlagsPanel`): video-intel artifacts separate from core outputs, lip-sync as score + separate transformed asset, local-GPU provider visible only as operator health with a privacy-policy routing note. Skipping this task leaves a complete product.

## Starting State
Tasks 036 and 043 done. Depends on Tasks 036 and 043. May be deferred or skipped without blocking release.

## Scope
Included: flag-gated video-intel UI, lip-sync score + separate asset download, operator-only local-GPU health panel.
Excluded: enrichment pipeline execution (backend/AI), core flows (untouched), ordinary-user GPU internals (never shown).

## Instructions
1. Create `frontend/src/features/enrichment/EnrichmentGate.tsx`: central flag check (`flags.videoIntel`, `flags.lipSync`, `flags.localGpu`) — when off, zero UI chrome for enrichment appears anywhere (no upsell banners, no disabled buttons); when on, mount the panels below without altering core layouts.
2. Create `frontend/src/features/enrichment/VideoIntelPanel.tsx`: display video-intel results as separate artifacts (scene cuts, detected overlays) linked from — never merged into — the transcript/timeline; enrichment failure renders a dismissible `UnavailableState` while transcript/translation/voice/review continue normally.
3. Create `frontend/src/features/enrichment/LipSyncPanel.tsx`: show lip-sync score per segment + separate transformed asset download (distinct from core outputs in Task 033); score shown with method note (`heuristic vX`); asset download reuses the signed-URL click-time pattern, never a new mechanism.
4. Create `frontend/src/features/admin/LocalGpuPanel.tsx` (operator-only, inside Task 036 Admin): provider health/model/version/device summary for the local GPU; ordinary users never see GPU internals; include the privacy-policy routing note (local processing path declared, data never leaves the operator boundary without explicit policy).
5. Prove non-blocking in `frontend/src/features/enrichment/__tests__/enrichment.test.tsx`: with flags off, core suites unaffected (import-gate test — enrichment modules not in core bundles); with enrichment APIs 500/timeout, workspace/review/export tests still pass (failure-isolation test).

## Requirements
- R1: All enrichment UI hidden entirely when flags are off (no chrome, no upsell, no disabled buttons).
- R2: Video-intel results are separate artifacts, never merged into core transcript/timeline data.
- R3: Lip-sync ships as score + separate asset; score carries its method note.
- R4: Local-GPU health visible to operators only with the privacy-policy routing note; no GPU internals for ordinary users.
- R5: Enrichment failure (flag on, backend 500/timeout) never blocks transcript/translation/voice/review/export flows.
- R6: Core bundles exclude enrichment modules when flags are off (import-gate test).

## Edge Cases and Error Handling
- Flag on but backend unimplemented (501) → panels show `NotAvailableState` with "operator feature in setup" copy, core unaffected.
- Lip-sync asset missing while score present → score stays, download hidden with reason.
- GPU health endpoint unreachable → operator panel shows `UnknownState`, never blocks admin login or other panels.
- Enrichment artifact references deleted segment → link degrades to `GoneState`, no crash.

## Security and Safety Requirements
- Enrichment downloads reuse Task 037 signed-URL rules (15-min, tenant-bound, never logged).
- GPU/device details restricted to elevated Admin role; API 403s for all others (extend `AdminAuthzTests` pattern).
- Privacy-policy routing note reviewed against §19.3 before display copy freezes.

## Testing
- Create `frontend/src/features/enrichment/__tests__/enrichment.test.tsx`: flag-off invisibility, separate-artifact rule, failure-isolation (500/timeout → core green), import-gate (core bundles clean).
- Playwright `@optional-enrichment`: flags on → panels render with fixtures; flags off → zero enrichment chrome; backend failure → core journey still completes.
- Type: unit (vitest) + Playwright; run `npm run test -- src/features/admin` + `@optional-enrichment`.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/features/admin
npx playwright test --grep="@optional-enrichment"
```

## Completion Criteria
- Flag-gated enrichment renders separately, fails without blocking core, hides operator GPU detail from ordinary users; enrichment tests + `@optional-enrichment` pass — or the task is explicitly deferred with no core impact.

## Traceability
- Plan B §19.1–§19.3. Depends on Tasks 036 and 043.



<!-- ===== FILE: 045-i18n-rtl-foundation.md ===== -->


# Task 045 — i18n / RTL Foundation

**Required/Optional:** Required
**Complexity:** S

## Goal
Provide the localization, pseudo-locale, and RTL foundation every feature UI builds on.

## Context
Plan B-2 explicitly requires i18n foundation (translation keys, locale-aware dates/numbers, timezone-aware timestamps, pluralization, RTL-compatible layout, default locale from user preference). The visual matrix (Task 041B) requires LTR/RTL coverage, which is impossible without this foundation. This task was missing and is added as an executable prerequisite before feature UI work.

## Starting State
Depends on Tasks 015 (scaffolding), 016 (tokens/primitives). Assumes `frontend/` exists with router/providers, `frontend/src/i18n/` directory exists or is created here.

## Scope
Included: i18n framework wiring, key extraction + no-hardcoded-copy gate, locale/timezone/plural helpers, RTL direction support + smoke, pseudo-locale test, default-locale-from-preference wiring.
Excluded: feature screen copy (owned by 019–036), visual regression matrix (041B), backend locale storage beyond Task 001/006 preferences passthrough.

## Instructions
1. Wire i18n framework in `frontend/src/i18n/` (`index.ts`, `locales/en.json` baseline, `locales/pseudo.json`): translation keys only — no hard-coded user-facing strings in `features/` after this task; add `scripts/check-no-hardcoded-copy.mjs` (or equivalent) that fails on literal UI copy outside `i18n/` + primitives, and run it in validation.
2. Add locale helpers in `frontend/src/lib/dates/` + `frontend/src/lib/formatting/`: locale-aware dates/numbers via `Intl`, timezone-aware timestamp display (user `timezone` preference from Task 006 with UTC fallback), pluralization support per locale.
3. Implement RTL support: `dir` attribute driven by locale (`ltr` default, `rtl` test locale), RTL-compatible CSS (logical properties, no hard-coded left/right in new styles), `?dir=rtl` override for manual testing, documented in `frontend/src/i18n/README.md`.
4. Add pseudo-locale test (`frontend/src/i18n/pseudo.spec.ts`): renders shell + one representative screen under pseudo-locale (expansion + brackets) and asserts no missing-key warnings, no layout crash, `dir` switching works.
5. Wire default locale from user preference: on `/me` resolution (Task 019) apply `locale` preference before first paint; document fallback chain `preference → browser → en`.

## Requirements
- R1: Zero hard-coded user-facing strings in feature code (extraction-gate passes).
- R2: Dates/numbers/timestamps render locale- and timezone-correctly (unit-tested with at least two locales/timezones).
- R3: RTL smoke passes: shell renders with `dir=rtl` without crash or overlapping chrome.
- R4: Pseudo-locale test passes with no missing-key warnings.
- R5: Default locale resolves from user preference with documented fallback.

## Edge Cases and Error Handling
- Missing key → falls back to `en` + console warning in dev, never blank screen; missing-key collector fails the pseudo test on new keys.
- Invalid `locale`/`timezone` preference → safe fallback (`en` / UTC) + warning, never crash.
- RTL + timeline canvas (Task 030) → canvas itself stays LTR-documented; chrome around it mirrors.

## Security and Safety Requirements
- No PII in locale bundles; no fetching remote translation files from untrusted origins (local bundle only unless allowlisted).
- `?dir=` override is presentation-only; never affects authz, tenant scoping, or API contracts.

## Testing
- New: `frontend/src/i18n/pseudo.spec.ts` (pseudo-locale + RTL smoke + fallback chain).
- New: `frontend/src/lib/dates/dates.spec.ts` + `frontend/src/lib/formatting/formatting.spec.ts` (multi-locale/timezone cases).
- Gate: `scripts/check-no-hardcoded-copy.mjs` run in validation.

## Validation
```bash
npm run typecheck --prefix frontend
npm run lint --prefix frontend
npm run test --prefix frontend -- src/i18n src/lib/dates src/lib/formatting
node scripts/check-no-hardcoded-copy.mjs
```

## Completion Criteria
- i18n framework + helpers + RTL + pseudo test + extraction gate exist and pass; feature tasks (019–036) can build locale-ready UI on this foundation.

## Traceability
- Plan B §10.9, §11.5 (RTL/responsive), §15.8 (visual LTR/RTL), §22 (B-2). Unblocks 018/019–036/041B.



<!-- ===== FILE: 046-test-harness-fixtures.md ===== -->


# Task 046 — Test Harness and Fixtures (Early Prerequisite)

**Required/Optional:** Required
**Complexity:** M

## Goal
Own the shared frontend/backend test harness, fixtures, and tags so feature tasks can verify locally and in CI.

## Context
Feature tasks (006–036) require Vitest, MSW, Playwright tags, synthetic tenants/users/projects/runs/segments, and PII scrubbers, but no early task owned them — the harness only appeared implicitly in late aggregate tasks 039–041. This task makes the harness an executable prerequisite: whoever implements a feature writes its specs against this harness instead of waiting for a later task.

## Starting State
Depends on Tasks 014 (OpenAPI bundle for mock generation), 015 (frontend scaffold). Must land before or alongside the first feature work (019+); later tasks 039–041 become gap closure, not initial authorship.

## Scope
Included: Vitest/MSW/Playwright config + tags, MSW handler taxonomy, synthetic fixture factories (tenants/users/projects/runs/segments), storage-emulator reset, SSE-aware waits, PII scrubbers, flake-quarantine policy.
Excluded: feature specs themselves (owned by 006–036), coverage enforcement (039A), cross-layer seams (040B), journeys/visual/a11y/perf (041A–D), full CI gates (042/042B).

## Instructions
1. Frontend harness: `frontend/vitest.config.ts` (or equivalent), `frontend/src/test/setup.ts`, `frontend/src/mocks/handlers.ts` taxonomy (`success, 401, 403, 404, 409, 429, 500, validation, provider-error, partial, stale-conflict`), `frontend/src/mocks/server.ts` (MSW), documented in `frontend/src/mocks/README.md`.
2. Playwright harness: `e2e/playwright.config.ts` (projects Chromium/WebKit/Firefox + `@smoke/@visual` tags), `e2e/support/auth.ts` (seeded auth for all roles), `e2e/support/reset.ts` (PG + storage-emulator reset), `e2e/support/sse-waits.ts` (event-driven waits, no fixed sleeps), `e2e/support/quarantine.md` (quarantine policy: 2 failures/50 runs → quarantine with owner + issue, never silent retry).
3. Backend fixtures: `tests/DubbingPlatform.TestFixtures/` (or existing fixtures dir): `SyntheticTenants.cs`, `SyntheticUsers.cs`, `SyntheticProjects.cs` (incl. runs/segments/review items), `PiiScrubber.cs` (asserts no real PII/secrets in fixtures, screenshots, or logs).
4. Add harness smoke tests: `frontend/src/mocks/handlers.spec.ts` (each taxonomy handler returns the documented envelope), `e2e/support/smoke.spec.ts` (reset + seeded login works), backend `TestFixturesTests.cs` (factories build valid graphs, scrubber passes).
5. Document ownership contract in `docs/test-ownership.md` (one page): feature tasks own their unit/component/integration/E2E slices against this harness; 039–041 own gap closure, seams, and non-functional gates only.

## Requirements
- R1: MSW taxonomy covers all 11 states with envelope-correct responses.
- R2: Playwright config supports tagged runs (`@smoke`, `@visual`, feature `@tags`) across required browsers.
- R3: Synthetic factories build tenant-isolated graphs usable by backend + E2E tests.
- R4: PII scrubber passes on all fixtures.
- R5: Ownership doc exists; no feature task is blocked waiting for 039–041 to author its specs.

## Edge Cases and Error Handling
- MSW handler missing → test fails with `MSW_HANDLER_MISSING` naming the taxonomy entry, not a generic network error.
- Storage emulator down → `e2e/support/reset.ts` fails fast with `STORAGE_EMULATOR_UNAVAILABLE`, never hangs.
- Parallel workers share tenant → factories issue isolated tenant per worker (no cross-test leakage).

## Security and Safety Requirements
- Fixtures contain synthetic PII only; scrubber test enforces it.
- Seeded credentials are ephemeral per run, never committed; no real tokens in handlers.

## Testing
- This task IS harness + its smoke tests: `handlers.spec.ts`, `e2e/support/smoke.spec.ts`, `TestFixturesTests.cs`.
- Type: unit + config smoke.

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~TestFixturesTests
npm run typecheck --prefix frontend
npm run test --prefix frontend -- src/mocks
npx playwright test --project=chromium e2e/support/smoke.spec.ts
```

## Completion Criteria
- Harness configs + taxonomy + factories + scrubbers + ownership doc exist; harness smoke tests pass; feature tasks can author specs immediately.

## Traceability
- Plan B §15.1–§15.6 (test prerequisites), §18 (support diagnostics use same seeds). Unblocks 006–041 feature verification; converts 039–041 to gap closure.



<!-- ===== FILE: 047-backup-policy-restore-verification.md ===== -->


# Task 047 — Backup Policy Execution and Restore Verification

**Required/Optional:** Required
**Complexity:** S

## Goal
Prove backup/restore covers the seven new durable entity groups from Plan B §18.4.

## Context
043C documents backup inclusion and runs a staging drill. This task is the execution/policy layer on top: jobs/config actually include the tables, restore is verified against recorded steps, RPO/RTO and escalation are explicit, and gaps block release. Does not rewrite Plan A backend runbooks or 043C product runbooks.

## Starting State
Depends on 001 (TenantUser/UserPreference/DubbingProject-extensions/Membership tables), 002 (Notification/ActivityEvent tables), 004 (VoicePreviewJob + preview artifact backing rows), 043C (backup docs baseline + prior drill log in `docs/backup.md`). Plan A backup jobs exist but do not yet list new tables.

## Scope
Included: backup job/config update for seven groups, restore verification steps + recorded drill, RPO/RTO statement, failure escalation, release-gate rule.
Excluded: runbook content (stays in 043C), hosting/env (043A), rollout/rollback mechanics (043B), backend Plan A runbooks (referenced only).

## Instructions
1. Update backup jobs/config (location per repo convention, referenced from `docs/backup.md`) to include: tenant users, preferences, notifications, activity events, memberships, voice preview jobs, extended project metadata. One checklist line per group; no group implied.
2. Define restore verification steps in `docs/backup.md`: restore target (staging), count checks per group, one spot-read per group (project/user/notification), success/fail recording fields (date, scope, result, gaps, owner).
3. Record RPO/RTO expectations + failure escalation path in `docs/backup.md`. **Assumption:** Plan §18.4 specifies no RPO/RTO values, job paths, or escalation contacts; values/contacts are team-recorded, not plan-derived.
4. Execute (or re-execute on top of 043C) one restore drill covering all seven groups; record date + result + gaps. Partial success is recorded as failure scope, never silent.
5. Enforce release gate: open backup/restore gaps block release (checklist item in release verification).

## Requirements
- R1: Backup config lists all seven entity groups explicitly.
- R2: Restore drill is recorded (date + result + gaps), not just documented.
- R3: RPO/RTO + escalation are written down. **Assumption:** numeric targets are team-chosen; plan only requires that expectations exist.
- R4: Gaps block release (explicit gate statement).
- R5: No duplication of 043C runbooks or Plan A backend runbooks (link, do not fork).

## Edge Cases and Error Handling
- Restore partially fails → record loss scope + forward-fix; gate stays red.
- New table added later without backup entry → gap item, blocks release until config updated.
- Drill env unavailable → drill is overdue (recorded), not waived.

## Security and Safety Requirements
- Drill on staging only unless explicitly approved; synthetic/example IDs only in docs; no tokens, signed URLs, or real tenant/user IDs.

## Testing
- Operational verification: config grep + drill log review. No unit tests.

## Validation
```bash
grep -E "tenant users|preferences|notifications|activity|memberships|voice preview|project metadata" docs/backup.md
grep -E "RPO|RTO|Escalation" docs/backup.md
ls docs/backup.md
```

## Completion Criteria
- Backup config exists, all seven entity groups included, restore drill recorded, gaps block release.

## Traceability
- Plan B §18.4. Depends on 001, 002, 004, 043C. Extends (does not replace) 043C.



<!-- ===== FILE: 048-frontend-feature-flag-hook.md ===== -->


# Task 048 — Frontend Feature-Flag Evaluation Hook

**Required/Optional:** Required
**Complexity:** S

## Goal
Add the frontend runtime flag-evaluation layer: `useFeatureFlag` (or equivalent) sourced from `/me` or bootstrap config, fail-closed, rollout-only.

## Context
006 owns the `/me` flags payload (`GET /me` returns `featureFlags`); 018 owns shell/guards; 036 owns the admin FlagsPanel display. None provides a shared evaluation hook. This task provides it and wires it into admin-gated and enrichment-gated UI areas without touching authorization.

## Starting State
Depends on 006 (`GET /me` returns `featureFlags`), 018 (shell + `RequireAdmin` guard slots), 036 (admin/enrichment surfaces to gate). No shared flag hook exists (only payload + panel references exist).

## Scope
Included: hook + flag source resolution + fail-closed default + rollout-only wiring into admin/enrichment-gated areas.
Excluded: `/me` contract changes (006, frozen), shell/guard redesign (018, reuse), admin panel logic (036, consume), new flags backend (plan §6.9: DB-backed flags not required).

## Instructions
1. Create `frontend/src/config/featureFlags.ts` (flag-key type + fail-closed default map) and `frontend/src/hooks/useFeatureFlag.ts`: `useFeatureFlag(key): boolean`. Sources, in order: `/me.featureFlags` (006) where present, else bootstrap config (`VITE_ENABLE_*` family per plan §17.4). **Assumption:** plan names no hook path, key names, or per-flag defaults; path/keys are conventions, every default is `false` (closed).
2. Fail closed: unknown/missing flag → `false` + safe fallback UI (hidden or disabled experimental surface, never error crash). Test asserts missing-flag → `false`.
3. Rollout-only: hook controls visibility/exposure only; every gated action still passes existing permission/authz checks (006 permission strings, 018 `RequireAdmin`, server-side authz). Add negative test: flag ON + permission OFF → still denied.
4. Wire into admin-gated areas (036 surfaces) and enrichment-gated areas (044: video-intel, lip-sync, local-GPU exposure). No other behavior change.

## Requirements
- R1: Single shared hook; no ad-hoc flag reads elsewhere (lint/grep gate).
- R2: Missing/unknown flag evaluates `false`.
- R3: Flags never grant permission (flag cannot bypass 403/authz).
- R4: Flag source is `/me` or bootstrap config only; no new flag-service scope.
- R5: Admin + enrichment surfaces consume the hook (at least one wired call-site each).

## Edge Cases and Error Handling
- `/me` flags absent (e.g. load failure) → all flags `false`, non-admin shell (consistent with 018 fail-closed).
- Flag ON but backend 403/423 on use → dialog explains denial/freeze, flag state unchanged.
- Bootstrap vs `/me` conflict → `/me` wins where both present (documented).

## Security and Safety Requirements
- Flags are UX rollout hints only (plan §6.9); server remains authorization authority. No secrets in flag values; no flag value in telemetry beyond key + boolean.

## Testing
- Create `frontend/src/hooks/__tests__/useFeatureFlag.test.tsx`: true/false/missing-flag-default-false, `/me`-over-bootstrap precedence, flag-ON-without-permission still denied (with mocked guard).
- Type: unit (vitest); consumed E2E stays in 036/044 specs.

## Validation
```bash
cd frontend && npm run typecheck
cd frontend && npm run test -- src/hooks/useFeatureFlag
```

## Completion Criteria
- UI evaluates flags via the shared hook, missing flags default safely (false), flags do not replace permission checks.

## Traceability
- Plan B §6.9, §12.19, §19 (flag source: §9.1/§12.1 `/me`). Depends on 006, 018, 036.



<!-- ===== FILE: 049-notification-channel-abstraction.md ===== -->


# Task 049 — Outbox Channel Abstraction for Future Notification Channels

**Required/Optional:** Required
**Complexity:** S

## Goal
Add a publisher/channel abstraction so future email/webhook channels can plug in later without changing notification projection semantics. In-app remains the only active channel.

## Context
002 owns the durable Notification projection + dedup. Plan §8.3.1 requires in-app delivery and reserves email/webhook as future-only. This task adds the seam; it implements no external channel.

## Starting State
Depends on 002 (Notification entity, `NotificationProjector`, unique `(TenantId, RecipientUserId, SourceEventId)` dedup). No channel abstraction exists; 012B/034 consume the in-app projection directly.

## Scope
Included: channel-publisher interface + in-app registration + extension doc + dedup/idempotency rules.
Excluded: any email/webhook implementation, projection-semantics changes (002 frozen), HTTP/UI changes (012B/034 untouched).

## Instructions
1. Define `INotificationChannelPublisher` (name/location per repo convention — **assumption:** plan specifies no interface name; e.g. `src/DubbingPlatform.Application/Notifications/INotificationChannelPublisher.cs`): `PublishAsync(Notification, CancellationToken)`; projector calls it after durable persist + dedup. **Assumption:** method signature is team convention; invariant is ordering (persist-then-publish) and idempotency.
2. Register `InAppChannelPublisher` as the sole active implementation (no-op beyond persisted row + existing `notification.created` emission path in 013). No SMTP/webhook code, config, or secrets.
3. Document extension points in code XML comments + one `docs/notifications-channels.md` page: how to add a channel without touching projector/dedup, per-channel idempotency-key rule (reuse `SourceEventId` as dedup key), at-least-once tolerance.
4. Add contract test: duplicate `SourceEventId` → single row + single in-app publish; second publish attempt is deduped.

## Requirements
- R1: In-app delivery remains durable (persist-first preserved).
- R2: Exactly one active channel (in-app); zero external sends (test asserts no SMTP/webhook client wired).
- R3: Future channel addable without projector/dedup changes (review: new class + registration only).
- R4: Dedup/idempotency rules documented and tested.

## Edge Cases and Error Handling
- Channel publish throws after persist → row retained, retry reuses `SourceEventId` (no duplicate row).
- Unknown future channel key → validation error, never silent drop of in-app.

## Security and Safety Requirements
- No notification body/secret in logs beyond ID + type (inherits 002 rule); no channel credentials introduced (none exist).

## Testing
- Extend `tests/.../Notifications/NotificationActivityTests.cs` (or new `NotificationChannelTests.cs`): persist-then-publish order, duplicate-source single-publish, single-active-channel assertion.
- Type: unit/integration (mocked publisher).

## Validation
```bash
dotnet build
dotnet test --filter FullyQualifiedName~NotificationChannelTests
```

## Completion Criteria
- In-app delivery remains durable, channel abstraction exists, no new external channel implemented.

## Traceability
- Plan B §8.3.1. Depends on 002. Consumed (unchanged) by 012B/034.


