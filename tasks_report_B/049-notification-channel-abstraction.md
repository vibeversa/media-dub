# Task 049 — Outbox Channel Abstraction for Future Notification Channels

## Status

**COMPLETED.** R1–R4 are satisfied, the `Validation` block passes verbatim, and the
seam is real rather than nominal: `NotificationProjector` now commits first and
publishes second, `NotificationChannelDispatcher` fans out to every registered
`INotificationChannelPublisher` in a closed-vocabulary order, and
`InAppChannelPublisher` is the only implementation in the product — asserted by
reflection, by a comment-stripped scan of `src/**`, and by reading both host
registrations. 27 new tests in `NotificationChannelTests`, all hermetic.

The task's own starting assumption was wrong in a way that mattered. Instruction 2
says the in-app publisher is a "no-op beyond persisted row + **existing**
`notification.created` emission path in 013". There is no such emission path.
`SseEventTypes.NotificationCreated` is a frozen constant with zero producers —
013's own report records this (`tasks_report_B/013-admin-sse-error-contracts.md:43`:
"`notification.created` emission still owns no producer"). This task therefore
builds the seam without inventing the emission, and `docs/notifications-channels.md`
records in "What is deliberately not here" exactly why wiring it is a cross-host
transport decision rather than a no-op. Building it would also have violated the
task's own exclusion of HTTP changes.

## Summary

Task 049 adds a publisher/channel abstraction between the durable notification
projection and its delivery mechanisms, without touching Task 002's projection
semantics. `INotificationChannelPublisher` is one delivery mechanism
(`Channel`, `IdempotencyKey(notification)`, `PublishAsync(Notification, ct)`);
`NotificationChannelDispatcher` owns the entire channel policy — validating the
registered set once at construction (unknown key, duplicate key, or a missing
in-app channel all fail there, not at delivery), publishing in
`NotificationChannels.All` order so in-app is always attempted first, and
metering per channel. The projector gained one call site, after
`SaveChangesAsync`, on the single code path that returns rows *this call created*
— so every deduplicated path (the pre-check, the concurrent CONFLICT race, the
no-recipient skip) publishes nothing, and a redelivered source event is exactly
one row and one publish. `InAppChannelPublisher` re-runs the entity's content
guards on the way out and adds no transport: the persisted row *is* the delivery,
served by 012B and rendered by 034. Nothing external was added: no SMTP client,
no webhook client, no channel credential, no config key.

## Files Created/Modified

### Created

| File | What it is |
| --- | --- |
| `src/DubbingPlatform.Application/Notifications/NotificationChannels.cs` | The closed channel vocabulary: `NotificationChannels.InApp` (`"in-app"`), `All` (ordered), `IsKnown`, `Normalize` (throws `DomainException` on blank/unknown), `IdempotencyKey(channel, notification)` (the per-channel dedup rule, as code), and internal `OrderOf`. |
| `src/DubbingPlatform.Application/Notifications/INotificationChannelPublisher.cs` | The seam interface. `Channel`, a **default interface member** `IdempotencyKey(Notification)` so a new channel inherits the rule rather than reinventing it, and `PublishAsync(Notification, CancellationToken)`. XML docs carry the ordering/idempotency/content invariants and the "extend the set" recipe. |
| `src/DubbingPlatform.Application/Notifications/NotificationChannelDispatcher.cs` | Owns the policy: validates the registered set once (empty / null / unknown key / duplicate key / missing in-app all rejected), orders by vocabulary, and `DispatchAsync` fans out, metering success and failure and rethrowing **unwrapped**. |
| `src/DubbingPlatform.Application/Notifications/InAppChannelPublisher.cs` | The sole active channel. Calls `notification.Validate()` (re-runs 002's URL/token/length/expiry guards at the channel boundary) and logs at debug with ids + type + channel only. No transport, no write. |
| `tests/DubbingPlatform.UnitTests/Notifications/NotificationChannelTests.cs` | **27 tests**, hermetic (InMemory `AppDbContext`, `IStageExecutionContextFactory` fake, recording channel, recording logger, no containers/network). Selected by the task's own validation filter. |
| `docs/notifications-channels.md` | The extension page the task requires: the one rule (persist then publish), the three dedup notions, at-least-once tolerance, the failure contract, the five-step "add a channel" checklist with what it must not do, and what is deliberately absent. |

### Modified

| File | What changed |
| --- | --- |
| `src/DubbingPlatform.Application/Notifications/NotificationProjector.cs` | `NotificationMeters` gains `notifications.channel_published_total` / `notifications.channel_failed_total` (tag `channel`) and `ChannelTagName`. Constructor takes `NotificationChannelDispatcher` as a second required dependency. `ProjectAsync` gains exactly one publish loop, after `SaveChangesAsync`, on the created-rows path only; the two dedup returns and the no-recipient skip gained comments explaining *why* they publish nothing. Dedup query, sanitizers, recipient resolution, listing and mark-as-read are untouched. |
| `src/DubbingPlatform.Api/Program.cs` | Three registrations beside the existing `NotificationProjector` line: `InAppChannelPublisher`, the `INotificationChannelPublisher` → `InAppChannelPublisher` alias (the `IQuotaGate` idiom already used at line 274), and `NotificationChannelDispatcher`. |
| `src/DubbingPlatform.Workers/Program.cs` | The identical three registrations. Required: `NotificationProjector` is registered in **both** hosts (012A's report flags this), so a new ctor dependency must exist in both or the Workers host fails to resolve it. |
| `tests/DubbingPlatform.UnitTests/Notifications/NotificationLogicTests.cs` | 16 construction sites moved to a `CreateProjector(factory)` helper that builds the **real** dispatcher over the **real** in-app channel, so 002's matrix runs through the seam rather than around it. The ctor guard test now covers both new `ArgumentNullException` paths. `MetricNames_AreFrozen` extended with the two new counters (it owns the frozen-metric contract — deliberately not restated in the new file). |
| `tests/DubbingPlatform.IntegrationTests/Notifications/NotificationActivityTests.cs` | The one `CreateNotificationProjector` helper now wires the real in-app channel, so the Docker-gated suite exercises persist-then-publish against live PostgreSQL and its filtered unique index. |
| `README.md` | Two lines in the documentation index pointing at `docs/notifications-channels.md`. |

## Decisions Made

1. **The projector's dependency is a dispatcher, not `IEnumerable<…>` directly.**
   The task says "projector calls *it*", which is only true while there is one
   channel. R3 requires adding a channel **without** touching the projector, so the
   projector needs a stable collaborator that resolves channels at runtime — and a
   dispatcher is where the two hard rules (in-app always present, publish in
   vocabulary order) can live as object state rather than as review comments.
   `NotificationProjector`'s constructor signature will not change again when a
   second channel lands.

2. **A missing in-app channel is a construction failure, not a delivery skip.**
   This is the "never silent drop of in-app" edge case. Because the dispatcher is
   resolved by DI, a registration set without in-app fails at startup rather than
   at 3am on the first notification. `MissingInAppChannel_IsRejected_NotSilentlyDropped`
   pins it.

3. **An unknown channel key is a `DomainException`, thrown from the dispatcher
   constructor.** A string key with a validated closed set (rather than an enum) is
   what makes the task's "unknown future channel key → validation error" edge case
   reachable at all; an enum would make it a compile error and the rule untestable.
   The cost — a string that can be wrong — is paid by construction-time validation
   plus `NotificationChannelsTests.ChannelVocabulary_IsClosed_AndInAppIsFirst`.

4. **The dispatcher rethrows the publisher's exception UNWRAPPED.** This is the
   highest-value decision in the task and it was found by reading the consumer,
   not the seam. `MessageDisposition.IsTransient` (src/DubbingPlatform.Infrastructure/Messaging/MessageDisposition.cs:91)
   classifies a handler failure **by exception type**: `HttpRequestException`,
   `TimeoutException`, `SocketException`, `IOException` retry, everything else is
   poison and is parked in `_skipped`. A dispatcher that wrapped failures in its
   own exception type would convert **every future channel's network timeout into
   poison** and silently stop the retry — a bug that could not be written today
   because no channel exists. `Dispatch_RethrowsTheOriginalException_Unwrapped`
   asserts `Assert.Same` plus `MessageDisposition.IsTransient(thrown)` so the
   classification stays intact.

5. **Publish happens only on the created-rows path.** The task requires
   "duplicate `SourceEventId` → single row + single in-app publish", and
   `ProjectAsync` has four returns: no-recipient skip, dedup pre-check, CONFLICT
   race, and the created path. Only the last one reaches the dispatcher. This is
   what makes publish-dedup a property of the call site rather than of every
   return path — the tempting version (dispatch on all four) would publish N times
   per redelivery for N recipients.

6. **A publish failure propagates; the row is not rolled back.** The edge case
   says "row retained, retry reuses `SourceEventId` (no duplicate row)", which is
   only observable if the failure surfaces. `SaveChangesAsync` has already
   committed, so propagation costs nothing and buys visibility; swallowing it
   would be the silent drop this task exists to prevent. Both existing call sites
   handle it: `NotificationActivityProjectionConsumer` parks poison / lets
   transient failures retry, and `ExportWorker.ProjectExportAsync` is explicitly
   best-effort ("export outcome already committed"). Documented as a stated
   consequence in the doc page: a failing channel is **not** retried by the
   projector, so a future channel that needs retry-on-failure must own it.

7. **A cancelled caller is not a channel failure.** `OperationCanceledException`
   on a cancelled token is rethrown without touching the counter or the log.
   Without this, shutting down mid-dispatch would move a failure rate that no
   channel can fix. `Dispatch_Cancellation_IsNotRecordedAsAChannelFailure`.

8. **`InAppChannelPublisher` calls `notification.Validate()`.** The row was
   sanitized at write time, so this re-check looks redundant — it is the point.
   It makes the channel boundary itself reject unsafe content even if a future
   writer bypasses the projector's sanitizers, which is 002's security rule
   enforced at the place a future channel would leak from. Verified no legitimate
   row is rejected (every guard the projector satisfies is satisfied by every
   `NotificationEventMapper` shape).

9. **The idempotency key is a default interface member, not just a helper.**
   `INotificationChannelPublisher.IdempotencyKey` has a body, so a channel author
   gets `"{channel}:{SourceEventId}"` — and the channel scoping — for free. The
   alternative (a static helper every publisher must remember to call) is exactly
   the "definition without a reader" defect 048 found in `config/env.ts`. When
   `SourceEventId` is null the projector disables dedup by design, so the key
   degrades to `"{channel}:notification:{Id}"` — the only stable identity such a
   row has. Both branches asserted.

10. **`InAppChannelPublisher` does not observe its cancellation token, on purpose.**
    There is no I/O to cancel and the row is already durable; throwing on an
    already-cancelled token would manufacture a failure for work that is done. The
    XML comment says so, because a reviewer will otherwise "fix" it.

11. **R2 is asserted three independent ways, because any one of them alone could
    pass while the others were lying.** (a) Reflection over **every** product
    assembly — loaded by reference, which is the only way to cover the Workers
    host whose entry point is internal — asserts exactly one concrete
    `INotificationChannelPublisher`. (b) A comment-stripped scan of `src/**/*.cs`
    for eleven code-shaped external-delivery tokens, plus a scan of `src/**.csproj`
    for the same, asserts zero. (c) A regex over each host's `Program.cs` asserts
    exactly one `INotificationChannelPublisher` registration per host and that it
    is the in-app one. (b) strips comments precisely because this task's own XML
    docs say "email/webhook are reserved for later" — a scanner that cannot tell a
    comment from code forbids a word, and the first response to a forbidden word is
    a vaguer explanation. Both scans assert their own coverage (file count > 100;
    the six expected csproj files) so a pass cannot mean "scanned nothing".

12. **A hermetic DI-resolution test was added because the HTTP tier cannot prove
    the graph here.** `TheRegisteredGraph_Resolves` builds a real
    `ServiceCollection` with the same four registrations and resolves the
    dispatcher and projector. The duplication is deliberate and labelled: the
    host side is pinned by `BothHosts_RegisterTheInAppChannel_AndOnlyIt`, which
    reads `Program.cs`, and the HTTP tier proves the same graph against a live
    host when it is runnable.

13. **The frozen-metric contract stays in one place.** The two new counters are
    declared on the existing `NotificationMeters` and asserted in the existing
    `NotificationLogicTests.MetricNames_AreFrozen`. A first draft asserted them in
    the new file too; two places claiming to freeze the same names is the
    "four sources that all derive from one another" shape `docs/backup.md` calls
    out. Consolidated.

14. **The new tests run through the real in-app channel wherever they are not
    testing the channel.** `NotificationLogicTests` and
    `NotificationActivityTests` build the production dispatcher, not a no-op one,
    so the existing matrices cannot drift past the seam unnoticed.

## Build/Test Results

### The task's `Validation` block, verbatim

```
$ dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:03.76
EXIT=0

$ dotnet test --filter FullyQualifiedName~NotificationChannelTests
Passed!  - Failed:     0, Passed:    27, Skipped:     0, Total:    27, Duration: 4 s - DubbingPlatform.UnitTests.dll (net10.0)
EXIT=0
```

(`dotnet test` from the repo root matches all four test projects; the other three
report "No test matches the given testcase filter" and the command still exits 0.)

### The rest of the .NET tiers

```
$ dotnet test tests/DubbingPlatform.UnitTests
  Failed DubbingPlatform.UnitTests.Options.OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority(authority: "/relative/path") [< 1 ms]
  Failed DubbingPlatform.UnitTests.Media.MediaValidationTests.Probe_Real_Files_Via_Ffprobe [6 ms]
Failed!  - Failed:     2, Passed:  3065, Skipped:     0, Total:  3067, Duration: 35 s
EXIT=1

$ dotnet test tests/DubbingPlatform.ContractTests
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 32 s
EXIT=0

$ dotnet test tests/DubbingPlatform.TestFixtures.Tests
Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 1 s
EXIT=0

$ dotnet test tests/DubbingPlatform.IntegrationTests --filter FullyQualifiedName~NotificationActivityTests
Failed!  - Failed:     1, Passed:    15, Skipped:     0, Total:    16, Duration: 43 s
# CrossTenant_Isolation_Enforced_With_Rls — pg_policies count (Expected 2, Actual 1) — PRE-EXISTING
# Dedup_Same_Source_Event_Projects_Once and the other 7 Docker-gated tests PASS against live PostgreSQL
```

Unit tier was **3038 passing before this task; 3065 now (+27)**, with the same two
pre-existing failures. `NotificationActivityTests` was **15 passing / 1 failing
before** and is unchanged: the one failure is a PostgreSQL RLS policy-count
assertion with no relation to channels.

### Pre-existing, verified by `git stash push -u` on each

| Failure | Proof |
| --- | --- |
| `OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority` | Fails identically with the change stashed (tree back to the previous commit). |
| `MediaValidationTests.Probe_Real_Files_Via_Ffprobe` | Same. `ffprobe` is not installed on this host. |
| `NotificationActivityTests.CrossTenant_Isolation_Enforced_With_Rls` | Same — `Expected 2, Actual 1` with the change stashed. |
| `NotificationsApiTests` / `OutputNotificationsApiTests` (14 tests) | Same — every one returns `401 UNAUTHORIZED` with the change stashed, so the JWT signing key is not accepted in this environment. **Note:** this also means the Api host's DI graph could not be verified through the HTTP tier here — hence Decision #12. |

### The repository gates

```
$ npm run check:unit-containers
CI_GATE_RESULT reason=OK status=PASS files=78 tests=1465        EXIT=0   # was files=77 tests=1440
$ npm run check:rollout
ROLLOUT_GATE_RESULT reason=OK status=PASS migrations=7 findings=0   EXIT=0
$ npm run check:i18n
COPY_GATE_RESULT reason=OK status=PASS files=218 findings=939 new=0  EXIT=0
$ npm run check:frontend-topology
FRONTEND_TOPOLOGY_RESULT reason=OK status=PASS files=297 findings=0  EXIT=0
$ npm run test:presence --prefix frontend
PRESENCE_OK:27 areas with specs                                 EXIT=0
$ npm run check:runbooks                    # fail 0            EXIT=0
$ npm run check:openapi-diff:self-test       # fail 0            EXIT=0
$ npm run lint:workflows                    # 0 finding(s)      EXIT=0
$ npm run test:tools
# pass 408  # fail 1
#   tools/npm-audit-gate.test.mjs:29 "a shell-free candidate must exist
#   before the shell fallback is reached" — byte-identical to the 045/046/047/048 reports
```

### Not run, and why

- `npm run check:api-contract` needs a live API on `127.0.0.1:58080`; this task
  changed no endpoint, no DTO and no OpenAPI file.
- `npm run check:backup-policy` remains `GAP_BLOCKS_RELEASE` (047's D1a/F1). No
  table, migration or retention policy changed.
- The frontend suite, the Playwright tiers and `tests/cross-layer` were not run:
  this task touched no `.ts`/`.tsx` file. `git status` shows changes only under
  `src/DubbingPlatform.{Application,Api,Workers}`, `tests/DubbingPlatform.*`,
  `docs/` and `README.md`.
- `DubbingPlatform.E2ETests` has no notification tests (its `SmokeTests` is
  `Assert.True(true)`).

## Recommendations for Next Agent (050)

### Repo state

- `main` carries … → 046 → 047 → 048 → **this task**.
- **Five new files**, six modified in place:
  - `src/DubbingPlatform.Application/Notifications/NotificationChannels.cs`
  - `src/DubbingPlatform.Application/Notifications/INotificationChannelPublisher.cs`
  - `src/DubbingPlatform.Application/Notifications/NotificationChannelDispatcher.cs`
  - `src/DubbingPlatform.Application/Notifications/InAppChannelPublisher.cs`
  - `tests/DubbingPlatform.UnitTests/Notifications/NotificationChannelTests.cs`
  - `docs/notifications-channels.md`
- **Green:** `dotnet build` (0 warnings), `NotificationChannelTests` 27/27,
  `ContractTests` 39/39, `TestFixtures.Tests` 16/16,
  `NotificationActivityTests` 15/16, `check:unit-containers`,
  `check:rollout`, `check:i18n`, `check:frontend-topology`, `test:presence`,
  `check:runbooks`, `check:openapi-diff:self-test`, `lint:workflows`.
- **Red, pre-existing (each verified by stashing):**
  `OptionsValidationMatrixTests.AuthOptions_Rejects_NonAbsolute_Authority`;
  `MediaValidationTests.Probe_Real_Files_Via_Ffprobe` (no ffprobe);
  `NotificationActivityTests.CrossTenant_Isolation_Enforced_With_Rls` (pg_policies);
  all 14 `NotificationsApiTests` / `OutputNotificationsApiTests` (401);
  `tools/npm-audit-gate.test.mjs:29`; `check:backup-policy`
  (`GAP_BLOCKS_RELEASE`, 047's D1a/F1).

### The API you will call

```csharp
// src/DubbingPlatform.Application/Notifications/INotificationChannelPublisher.cs
public interface INotificationChannelPublisher
{
    string Channel { get; }                                       // must be in NotificationChannels.All
    string IdempotencyKey(Notification n) => NotificationChannels.IdempotencyKey(Channel, n);
    Task PublishAsync(Notification notification, CancellationToken cancellationToken = default);
}

// NotificationChannelDispatcher.cs
public sealed class NotificationChannelDispatcher
{
    public NotificationChannelDispatcher(IEnumerable<INotificationChannelPublisher> channels,
                                        ILogger<NotificationChannelDispatcher> logger);
    public IReadOnlyList<string> ActiveChannels { get; }
    public Task DispatchAsync(Notification notification, CancellationToken cancellationToken = default);
}

// NotificationChannels.cs
public static class NotificationChannels
{
    public const string InApp = "in-app";
    public static readonly string[] All = [InApp];   // closed set, publish order
    public static bool IsKnown(string? channel);
    public static string Normalize(string? channel);  // throws DomainException on blank/unknown
    public static string IdempotencyKey(string channel, Notification notification);
    internal static int OrderOf(string channel);
}
```

`NotificationProjector`'s constructor is now
`NotificationProjector(IStageExecutionContextFactory, NotificationChannelDispatcher)`.
**If you change that signature, you must change all three of:**
`src/DubbingPlatform.Api/Program.cs` and `src/DubbingPlatform.Workers/Program.cs`
(both register the projector) **and** the three helpers
`NotificationLogicTests.CreateProjector`,
`NotificationActivityTests.CreateNotificationProjector`,
`NotificationChannelTests.CreateProjector`. 012A's report flags the dual-host
registration as a standing hazard.

### Adding a channel (the checklist the doc page and the tests both enforce)

1. Add the key to `NotificationChannels.All`, in the position that says when it
   should be attempted. Index 0 must stay `InApp`.
2. Implement `INotificationChannelPublisher`. Do **not** reimplement
   `IdempotencyKey`. Put the class in
   `src/DubbingPlatform.Application/Notifications/`, or in `Infrastructure` if it
   needs MassTransit.
3. Register **both** in `src/DubbingPlatform.Api/Program.cs` **and**
   `src/DubbingPlatform.Workers/Program.cs`, following the existing pattern:
   `AddScoped<Impl>()`, then `AddScoped<INotificationChannelPublisher>(p => p.GetRequiredService<Impl>())`.
4. Expect `NotificationChannelTests` to fail on
   `InApp_Is_TheOnlyImplementedChannel_InEveryProductAssembly`,
   `NoMailOrWebhookClient_IsReachable_FromProductCode`,
   `NoMailOrWebhookPackage_IsDeclared_ByAnyProductProject`, and
   `BothHosts_RegisterTheInAppChannel_AndOnlyIt` — all four are meant to fail.
5. Update `docs/notifications-channels.md`.

### Gotchas that cost time

1. **`.NET` SDK was missing and is now installed at `10.0.401` under
   `$HOME/.dotnet`, which is NOT on `PATH`.** Every `dotnet` command needs
   `export PATH="$HOME/.dotnet:$PATH"` first; without it you get
   "A compatible .NET SDK was not found" (only 8.0.408 is on the system path).
   Reinstall with
   `bash /tmp/opencode/dotnet-install.sh --channel 10.0 --install-dir "$HOME/.dotnet" --no-path`.
2. **`Assert.Equal([x], someIReadOnlyList)` does not compile** —
   CS8631, `string?` vs `IEquatable<string?>`. Use an explicit
   `Assert.Equal(new[] { x }, list)`. The same applies to a collection expression
   of `Type.FullName` (which is `string?`).
3. **A default interface member is only callable through the interface.** A test
   calling `publisher.IdempotencyKey(n)` on the concrete class fails with CS1061;
   cast: `((INotificationChannelPublisher)publisher).IdempotencyKey(n)`.
4. **`Assembly.Load` by reference is how to scan the Workers host.**
   `typeof(Workers.Program)` is unreachable — top-level statements produce an
   internal class. `Assembly.GetExecutingAssembly().GetReferencedAssemblies()` +
   `Assembly.Load` covers all six product assemblies.
5. **The unit tier's InMemory pattern** (`TestContextFactory` /
   `TestAppDbContext` with the `SealDispose` trick,
   `ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>()`,
   per-test-unique database name) is **duplicated** in each notifications test
   file as private nested classes. That is the existing convention, not an
   oversight; copying it beats reaching across test classes.
6. **Repository-file reads from a .NET test**: walk up from
   `AppContext.BaseDirectory` looking for `DubbingPlatform.sln`. Precedent:
   `tests/DubbingPlatform.IntegrationTests/Persistence/MigrationCompatTests.cs:41`.
7. **New UI copy and new metrics have different blast radii.** Metric names are
   only frozen by a test (`NotificationLogicTests.MetricNames_AreFrozen`) —
   nothing scans for them. UI strings are gated by `scripts/check-no-hardcoded-copy.mjs`
   (939 baselined, 0 new allowed).
8. **`NotificationChannels.OrderOf` is `internal` and the Application project has
   no `InternalsVisibleTo`.** It is deliberately untestable from the unit tier;
   the ordering rule is asserted through `dispatcher.ActiveChannels` and
   `NotificationChannels.All[0] == NotificationChannels.InApp` instead. Adding an
   `InternalsVisibleTo` just for this was judged not worth it.

### Incomplete integration points (mine, explicitly)

- **G1 — `notification.created` still has no producer.** Documented in
  `docs/notifications-channels.md` "What is deliberately not here". Building it
  needs: the envelope out of `DubbingPlatform.Api` (where `SseEnvelope` and
  `SseEventTypes` live) into a project both hosts reference; a broker hop or a
  shared store, because `SseEventBuffer` is a **static in-process dictionary**
  and notifications are projected in `DubbingPlatform.Workers` while subscribers
  connect to the Api; a stream key with a **user** dimension, because
  `SseEventBuffer.KeyFor(tenantId, projectId)` would broadcast one user's
  notification to another user's stream; and a per-recipient authorization story.
  **Owner: the first task that emits a notification event.** The natural home is
  a new publisher inside `InAppChannelPublisher`'s seam, so no projector change is
  needed — the work is transport, not the seam.
- **G2 — a channel failure is not retried by the projector.** Stated in the doc
  page's "When a channel fails". Inherent to the design (retrying would re-publish
  after a dedup, which R4 forbids). A future external channel that needs
  retry-on-failure owns it. **Owner: whoever adds that channel.**
- **G3 — there is no per-recipient channel preference gate at the seam.**
  Notification preferences (001/035) are applied where a notification is read, not
  where it is delivered. A fan-out-to-devices channel must answer it itself.
  **Owner: whoever adds that channel.**
- **G4 — the CONFLICT (concurrent-redelivery) publish path is asserted only
  indirectly.** `NotificationActivityTests.Dedup_Same_Source_Event_Projects_Once`
  covers the DB-level dedup on live PostgreSQL, but no test forces two writers
  into the race and asserts the loser published nothing. Forcing it needs a
  barrier between the projector's pre-check and its `SaveChangesAsync`, which
  would mean a test seam in production code. **Owner: whoever cares about
  concurrent redelivery at scale.**
- **G5 — `docs/topology.md` was not touched.** The channel seam has no network
  topology yet (no external send), so there was nothing true to add to the
  topology tables. **Owner: whoever adds the first external channel.**
- **G6 — the Api host's DI graph for the seam is only verified hermetically
  here.** `TheRegisteredGraph_Resolves` mirrors the registrations rather than
  executing `Program.cs`, because every HTTP integration test 401s in this
  environment. It will be genuinely verified the first time the HTTP tier runs
  green. **Owner: whoever fixes the test-JWT environment.**

### Environment

- Docker **is** available on this host, so the Docker-gated
  `NotificationActivityTests` do run (15/16) against real PostgreSQL 16-alpine.
  `tests/cross-layer` was not exercised.
- `ffprobe` is **not** installed; `dotnet` is **not** on `PATH`.