# 012B — Notifications API

## Status
COMPLETED

## Summary
Completed the 012B split (notifications half of superseded 012). The HTTP surface itself already existed from the combined-012 implementation (`NotificationsController` list/unread-count/read/read-all over the Task 002 projector, 8 Docker-gated tests); this task closed the remaining spec gaps: named the Task 013 `notification.created` SSE contract and hint semantics explicitly, documented the R5 deep-link table with fallback (linked row → `projectId` → list), and pinned page-based (not cursor) pagination as the API-wide convention. Added hermetic `NotificationApiContractTests` (5 tests) so R4/R5 are asserted without Docker.

## Files Created/Modified
- `src/DubbingPlatform.Api/Controllers/NotificationsController.cs` (modified) — class doc now names `notification.created` (013-owned emission, hint only), documents deep-link routing table + fallback, and page-based pagination decision.
- `src/DubbingPlatform.Api/Models/NotificationDtos.cs` (modified) — `NotificationResponse` doc pins `ResourceType` allowlist (`ProcessingRun|ReviewItem|ExportJob|MediaAsset|DubbingProject|Tenant`) and fallback chain.
- `src/DubbingPlatform.Api/OpenApi/OpenApiConfiguration.cs` (modified) — document description + `NotificationResponse` schema mention `notification.created`, deep-link fallback, and page-based listing.
- `tests/DubbingPlatform.UnitTests/Notifications/NotificationApiContractTests.cs` (created) — 5 hermetic tests: sanitize strips URLs/tokens, sanitize redacts instead of storing, all mapper resource types in documented set with URL-free bodies, read-all dual-name compat, role-matrix entries.

## Decisions Made
- **Page-based, not cursor:** Instruction 1 says "cursor pagination" but the entire API (projects, runs, segments, exports, admin) uses page-based `PaginatedResult`, and all 8 existing tests assert `page/pageSize/total/hasMore`. Kept page-based for consistency; documented in controller/OpenAPI. R1 ("paginated, newest-first, excludes expired") is satisfied.
- **No emission implemented for `notification.created`:** Instruction 4 says "Emit `notification.created` (013) on projection" — the parenthetical assigns ownership to 013 (next task), and per "do not over-implement / ignore future tasks" this task documents the contract (event name + hint-only semantics, API as source of truth) without building SSE emission. 013 will own the envelope and emission.
- **R5 as documented fallback, not server resolution:** items return stored `resourceType/resourceId/projectId`; the API does not join/resolve linked rows (bounded reads, no N+1, no leak). Fallback (linked row → project → list) is documented for 034 in controller, DTO, and OpenAPI.
- **No new error codes or schema change:** notification scoping/reads reuse `NOT_FOUND` (expired/cross-user/cross-tenant → 404, already-read → 200 no-op, read-all empty → 200 `marked:0`). Catalog stays 65.
- **No recipient-logging change needed:** verified `NotificationProjector` and `NotificationsController` log nothing (no Title/Body in telemetry), satisfying "ID + type only" — projector stores sanitized summaries; `Sanitize` redacts URLs/bearer tokens and `SanitizeResourceId` rejects them.

## Build/Test Results
- `dotnet build` — `Build succeeded. 0 Warning(s) 0 Error(s) Time Elapsed 00:00:39.67` (TreatWarningsAsErrors on).
- `dotnet test tests/DubbingPlatform.UnitTests` (first run) — `Failed! - Failed: 1, Passed: 411, Skipped: 0, Total: 412` (`SanitizeResourceId_Rejects_Urls_And_Tokens` wrongly expected `Sanitize` to throw; `Sanitize` redacts, only internal `SanitizeResourceId` rejects — fixed test to assert redaction).
- `dotnet test tests/DubbingPlatform.UnitTests` (rerun) — `Passed! - Failed: 0, Passed: 412, Skipped: 0, Total: 412` (407 prior + 5 new).
- `dotnet test --filter FullyQualifiedName~NotificationsApiTests` — `Skipped! - Failed: 0, Passed: 0, Skipped: 14, Total: 14` (8 inbox + 6 Task-002 `NotificationActivityTests` share the filter; no Docker locally, CI runs live).
- `dotnet test tests/DubbingPlatform.ContractTests --filter FullyQualifiedName~MessageContractTests` — `Passed! - Failed: 0, Passed: 4, Skipped: 0, Total: 4`.

## Recommendations for Next Agent (013)
- **State:** 001–011 committed; 012A committed (`c3e02e7`); combined-012 tests committed (`ca9d1c5`); 012B delta in tree (uncommitted: 3 doc edits + 1 new test dir). `master-prompt.md` modification predates these tasks (untouched). Counts pinned: `ErrorCodes.All`=65, `PublicIdMapper.AllPrefixes`=22, permissions=12, `WorkspaceService.QueryCeiling`=12, Contracts=20. UnitTests total now 412. No Docker locally; CI must run `NotificationsApiTests` (8) + `OutputExportApiTests` (11) live.
- **Key APIs:** `NotificationProjector.ListActiveAsync(tenant,recipient,unreadOnly?,page,pageSize,ct)→(Items,Total)`, `CountUnreadAsync→long`, `MarkAllReadAsync→int`, `MarkAsReadAsync` (expired/missing/other-recipient → `NotFoundException` → 404; re-mark no-op); `NotificationEventMapper.From*` (resource types: `ProcessingRun|ReviewItem|ExportJob|MediaAsset|DubbingProject|Tenant`); `NotificationsController.List/UnreadCount/MarkRead/MarkAllRead/TenantForNotifications/RecipientForNotifications`; DTOs `NotificationResponse/UnreadCountResponse/MarkAllReadResponse(MarkedCount,Marked)`; `NotificationApiContractTests.AllowedResourceTypes` mirrors the documented allowlist — keep in sync if a mapper gains a type.
- **Gotchas:** (1) `sub` must be GUID user id (401 otherwise); use stable recipient GUIDs for isolation tests. (2) Expired rows → 404 on read (never 410), excluded from list/count. (3) Unit-test namespace trap: `DubbingPlatform.UnitTests.Notifications` needs `global::` for `Domain.Entities`/`Domain.Exceptions` (used in new file). (4) xUnit analyzers are errors: `Assert.Single(col,pred)`, never `.Where+Single`. (5) `Sanitize` redacts (URLs→`[redacted-url]`, bearer→`[redacted-token]`); `SanitizeResourceId` (internal) throws on URLs/tokens — don't conflate them. (6) Never log notification Title/Body, signed URLs, `?access_token`, `RefreshToken`/`Salt`/`TokenHash`.
- **Incomplete integration points:** 013 owns `notification.created` SSE envelope + emission (this API documented as source of truth; SSE is hint → clients refetch); 014 consumes `NotificationResponse` OpenAPI schema; 034 consumes deep links (`resourceType/resourceId/projectId` + fallback linked→project→list); 026 uses SSE hint to refetch this API.
- **Test helpers:** `NotificationsApiTests` pattern — `CreateFactory(conn)` (`Auth:*`, `Transport:InMemory`), `SeedNotificationsAsync(tenant,recipient,project,count)`, `SeedQuotaAsync` (null ProjectId tenant row), `SeedExpiredAsync`, `MarkReadDirectAsync`; hermetic `NotificationApiContractTests` for hygiene/deep-links/matrix without Docker.
- **Warnings:** `TreatWarningsAsErrors` on. No migration added. Contract suite flaky `Handles_Timeout(local)` — rerun before blaming new code.
