# Notification channels

Task 049. How a notification reaches a recipient, what is frozen about that, and
what a second channel has to do. The seam is
`src/DubbingPlatform.Application/Notifications/`; the contract it enforces is
pinned by `tests/DubbingPlatform.UnitTests/Notifications/NotificationChannelTests.cs`.

**In-app is the only channel that exists.** Plan B §8.3.1 requires in-app
delivery and reserves email and webhook as future-only, so this page documents
how to add one and does not add any. Nothing in the product opens an SMTP
connection or posts to a webhook, and `NotificationChannelTests` asserts that by
reflection, by a comment-stripped scan of `src/**`, and by reading both host
registrations.

## The one rule

```
   source event ──▶ NotificationProjector ──▶ commit ──┬──▶ NotificationChannelDispatcher ──▶ in-app
                    (Task 002: dedup,                   │    (Task 049: fan-out)
                     recipients, sanitize)              │         ▲
                                                        │         └── INotificationChannelPublisher[]
                                                        └── the row IS the in-app delivery
```

**Persist, then publish.** The projector calls `SaveChangesAsync` and only then
hands the committed rows to `NotificationChannelDispatcher`. A publisher can
never observe an uncommitted row, and a publisher that throws can never unwrite
one. Everything before the commit is Task 002's projection semantics, which this
task did not change.

Everything else follows from that ordering plus one closed list:

| | |
| --- | --- |
| `NotificationChannels.All` | the closed set of channel keys, in publish order. `in-app` is index 0 |
| `INotificationChannelPublisher` | one delivery mechanism: `Channel`, `IdempotencyKey(notification)`, `PublishAsync(notification, ct)` |
| `NotificationChannelDispatcher` | validates the registered set once, then fans out in `All` order |
| `InAppChannelPublisher` | the sole active channel; validates the row and adds no transport |

## Dedup and idempotency

Three different things, and confusing them is how a notification gets sent twice.

| | Rule | Enforced by |
| --- | --- | --- |
| **Row dedup** | one row per `(TenantId, RecipientUserId, SourceEventId)`, including the concurrent race via the filtered unique index (CONFLICT → re-read) | the projector's filtered query + the DB index (Task 002) |
| **Publish dedup** | a publisher is called only for rows *this call created*. Every deduplicated path — the pre-check, the CONFLICT race, and the no-recipient skip — publishes nothing | the projector's single `DispatchAsync` call site, after `SaveChangesAsync` |
| **Channel idempotency** | a publisher deduplicates on `IdempotencyKey(notification)`, which is `"{channel}:{SourceEventId}"`, or `"{channel}:notification:{Id}"` when there is no source event | `NotificationChannels.IdempotencyKey`, exposed on the interface as a default member so a new channel inherits it |

The key is scoped by channel on purpose: two channels delivering the same source
event keep **independent** delivery state, so a webhook that fails cannot be
fixed by re-sending the email.

**Deliveries are at-least-once.** A publisher may be handed the same
notification again after a restart, after a redelivered source event, or after a
retried dispatch. It must treat `IdempotencyKey` as a deduplication key, not as
an assertion that the notification is new. The in-app channel can afford this
because the row is already there.

## When a channel fails

`DispatchAsync` records the failure (`notifications.channel_failed_total`, tagged
`channel`) and rethrows **the publisher's own exception, unwrapped**. The row
stays: it is committed, and the retry reuses the same `SourceEventId` so it
deduplicates to the same row instead of appending a second one.

A cancelled caller is not a channel failure: `OperationCanceledException` on a
cancelled token is rethrown without touching the counter or the log, so the
failure rate is a number a channel can actually move.

Unwrapping is load-bearing. `MessageDisposition.IsTransient` classifies a
handler failure by exception type — `HttpRequestException`, `TimeoutException`,
`SocketException`, `IOException` retry; everything else is poison and is parked
in `_skipped`. A dispatcher that wrapped failures in its own exception type would
turn every future channel's network timeout into poison and silently stop the
retry. Channel identity goes in the log line (id and type only, never the body)
and the meter tag, not in the exception type.

Consequence, stated plainly: **a channel that fails is not retried by the
projector.** The durable in-app record is correct either way, and the failure is
visible in the metric and the log, but a future external channel that needs
retry-on-failure has to own that retry itself (or add a delivery record). That is
the cost of keeping the seam free of transport, and it is the right default for a
seam nobody has built a channel on yet.

## Adding a channel

Two files and two registrations. The projector, the dedup query, the entity, and
the migrations are not touched.

1. **Add the key** to `NotificationChannels.All`, in the position that describes
   when it should be attempted. It is a closed list, so the key and the
   implementation land in the same change — there is no window where a registered
   channel is unrecognised.
2. **Implement the interface.** A new class in
   `src/DubbingPlatform.Application/Notifications/` (or in `Infrastructure` if it
   needs MassTransit — the interface lives in `Application` so `Application` never
   references a transport, the same split
   `ISpeakerVoiceEventPublisher` uses). Do not reimplement `IdempotencyKey`; the
   default member is the documented rule.
3. **Register it in both hosts** — `src/DubbingPlatform.Api/Program.cs` *and*
   `src/DubbingPlatform.Workers/Program.cs`. They mirror each other because
   `NotificationProjector` is registered in both; a registration that exists in
   only one host fails at resolution in the other.
4. **Extend `NotificationChannelTests`.** Three assertions will fail until you
   do, and each one is meant to: the reflection test that exactly one publisher
   implementation exists, the source/package scans that no external delivery
   client exists, and `BothHosts_RegisterTheInAppChannel_AndOnlyIt`, which
   asserts one publisher registration per host.
5. **Say what it does** here: what it sends, what its failure mode is, and
   whether it retries.

### What a channel must not do

- Re-persist the row. The projector owns persistence; a second write is a second
  source of truth.
- Log the title, body, or resource id. Ids and type only — 002's rule, inherited
  at this boundary and asserted by `Dispatch_LogsIdsAndTypeOnly_WhenAChannelFails`.
- Send transcript bodies, storage keys, signed URLs, or secrets. The row was
  sanitized by the projector, and `InAppChannelPublisher` re-runs the entity
  guards on the way out so the channel boundary itself rejects unsafe content.
- Hold credentials in a channel that the in-app row does not need. There are no
  channel credentials in this product today; a channel that needs one is a
  configuration and secret-rotation decision, not just a class.

## What is deliberately not here

**`notification.created` is not emitted.** Task 013 froze the SSE event type and
the envelope; no code constructs one. Wiring it here would be a cross-host
transport decision, not a no-op: `SseEnvelope` and `SseEventBuffer` live in
`DubbingPlatform.Api`, while notifications are projected in
`DubbingPlatform.Workers`, and `SseEventBuffer` is a static in-process
dictionary, so a Workers-side append would not reach an Api-side subscriber
without a broker hop. `SseEventBuffer.KeyFor` also has no user dimension, and a
notification is per recipient — a tenant/project stream key would broadcast one
user's notification to another user's stream. It needs its own task, its own
stream, and its own authorization story.

**There is no per-recipient preference gate at this seam.** Notification
preferences are Task 001/035's business and are applied where a notification is
read, not where it is delivered. A channel that fans out to devices has to answer
the preference question for itself.

## Verifying a change

```bash
dotnet build
dotnet test --filter FullyQualifiedName~NotificationChannelTests   # 27 tests, no Docker
dotnet test tests/DubbingPlatform.UnitTests                        # the projection matrix
dotnet test tests/DubbingPlatform.IntegrationTests \
  --filter FullyQualifiedName~NotificationActivityTests             # real PostgreSQL, Docker-gated
```

`NotificationActivityTests` constructs the projector through the real in-app
channel, so `Dedup_Same_Source_Event_Projects_Once` also covers persist-then-publish
against a live database and its filtered unique index.