# Task 17 — API Foundation Auth and Idempotency

## Goal

Implement JWT auth, role matrix, tenant/project ownership enforcement, race-safe idempotency, pagination, OpenAPI, signed-URL policy, and audit hooks so all endpoints share secure foundations.

## Context

Binding: roles TenantAdmin/ProjectOwner/ProjectEditor/Reviewer/ProjectViewer/Service. IdempotencyRecord (tenant/endpoint/key/request-hash/state Started|Succeeded|Failed/response status+body or resource ref/created/expires), atomic insert, canonical response replay, 409 on same key different hash, retention project-create 7d/upload-create 7d/upload-complete 7d/processing-start 7d/cancel 24h/retry 24h/export 24h. Pagination {items,page,pageSize,total,hasMore}. OpenAPI with schemas+error envelope+JWT+Idempotency-Key+pagination. Downloads via signed URLs 15min default. Audit privileged ops. Correlation IDs on all endpoints.

## Starting State

Middleware/options/errors, health, bus, storage interfaces, provider model exist. No controllers, no auth, no idempotency service, no authorization policies.

## Scope

Must implement: JWT bearer, 6 roles + matrix, ownership checks, IdempotencyService + filter, pagination envelope, OpenAPI, audit helper, controller shells. Must not implement: concrete endpoint business logic (next task), workers.

## Instructions

1. JWT: `services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o=>{o.Authority=Auth:Authority;o.Audience=Auth:Audience;o.RequireHttpsMetadata=Auth:RequireHttps;})` + `AddAuthorization` policies `RequireTenantAdmin, RequireProjectOwner, RequireProjectEditor, RequireReviewer, RequireProjectViewer, RequireService`. Claims: `tid` (tenant), `sub` (user), `roles[]`. Create `src/DubbingPlatform.Application/Authorization/RoleMatrix.cs`: dictionary endpoint→allowed roles (e.g., POST projects: TenantAdmin/ProjectOwner/ProjectEditor; GET: +Reviewer/Viewer; DELETE: TenantAdmin/Owner; reviews approve: Reviewer+above; admin: Service/TenantAdmin). Enforce via `[Authorize(Policy=...)]` + resource check `IAuthorizationHandler ProjectOwnershipHandler` verifying `route project.TenantId == claim tid` else 403.
2. Idempotency: `src/DubbingPlatform.Application/Services/IdempotencyService.cs` `Task<(bool isReplay, string? response)> TryClaimAsync(tenant,endpoint,key,requestHash,expiry)` doing atomic `INSERT ... ON CONFLICT (tenant,endpoint,key) DO NOTHING RETURNING`; if conflict: load row; if RequestHash!=incoming → throw ConflictException (409 CONFLICT); if State Succeeded → replay stored response; if Started (age<5min) → return 409 or 425? Decision: return 409 CONFLICT with code CONFLICT + Retry-After (document). `CompleteAsync` stores status+body/resource. Filter `IdempotencyFilter : IAsyncActionFilter` reading `Idempotency-Key` header on POST/DELETE mutations, computing requestHash via ConfigurationHashCalculator over canonical body.
3. Pagination: `PaginatedResult<T> { Items, Page, PageSize, Total, HasMore }` + `PaginationParams { Page=1, PageSize=20 max 100 }`.
4. OpenAPI: `AddOpenApi` + doc with JWT bearer scheme, global `Idempotency-Key` header param on mutations, error envelope schema, pagination schema.
5. Signed URLs: helper `SignedUrlPolicy { TimeSpan Expiry=15min }`; issuance only after ownership check (enforced in later tasks, helper here).
6. Audit: `AuditService.LogAsync(tenant,project,actor,action,resourceType,resourceId,details)` append-only insert; call sites in later tasks but service here.
7. Create controller shells `src/DubbingPlatform.Api/Controllers/` with `[ApiController][Route("api/v1/...")]` + `[Authorize]` + correlation passthrough, no business logic yet (return 501): ProjectsController, UploadsController, ProcessingController, SegmentsController, ReviewsController, ExportsController, OutputController, AdminController. Include `GET /health/live`, `/health/ready`, `/metrics` already mapped.

## Requirements

- R1: JWT + 6 roles + matrix enforced.
- R2: Idempotency atomic, replay same, mismatch 409, concurrent safe.
- R3: Retention per endpoint-class as listed.
- R4: Pagination envelope exact.
- R5: OpenAPI complete; signed URLs 15min; audit append-only.

## Edge Cases and Error Handling

- Missing/invalid JWT → 401 UNAUTHORIZED.
- Cross-tenant project id → 403 FORBIDDEN.
- Concurrent duplicate POST → one succeeds, other replays (no duplicate rows).
- Same key different body → 409 CONFLICT.
- Expired idempotency row → treat as new claim.

## Security and Safety Requirements

- Tenant scoping at repo level (add `ITenantScoped` filter helper `ApplyTenant(query,tenant)`); ownership before signed URL; no secrets in audit/logs.

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Api/IdempotencyTests.cs` (WebApplicationFactory + PG): `Duplicate_Returns_Same_Response`, `Mismatch_Returns_409`, `Concurrent_Duplicates_Single_Row` (10 parallel POSTs, assert 1 resource), `Unauthorized_401`, `Forbidden_Tenant_403`, `OpenApi_Complete`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~IdempotencyTests
```

## Completion Criteria

- Auth/idempotency/pagination/OpenAPI/audit shells work; tests pass.

## Traceability

- Plan Section 7 actions 1–13,15–20; Assumptions 62–63; Tests checklist idempotency/API endpoints.
