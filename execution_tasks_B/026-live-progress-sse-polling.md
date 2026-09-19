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
