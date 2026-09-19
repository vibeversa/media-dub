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
