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
