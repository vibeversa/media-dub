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

