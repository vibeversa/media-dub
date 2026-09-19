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
