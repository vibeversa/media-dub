# Secret Rotation Runbook

Secrets arrive only via environment variables or the secret manager. No secret
is hardcoded, committed, logged, hashed, or returned in error responses
(see `SecretPolicy` / `SecretRedactor`; properties carrying secrets are marked
`[Secret]` and redacted at the Serilog and audit boundaries).

## Scope

- `ConnectionStrings__Default` (PostgreSQL, role `app_role`)
- `Maintenance__Connection` (PostgreSQL, role `maintenance`, `BYPASSRLS`)
- `RabbitMq__Password`, `Storage__SecretKey` (MinIO/S3)
- Provider keys: `Azure__*`, `OpenAI__*`, `Google__*` credentials
- `Auth__SigningKey` (JWT HMAC)

## Rotation without downtime (dual-support window)

1. Provision the new credential alongside the old one (database: create the
   replacement role password / second valid secret; broker/object store: add
   the new access key; JWT: publish the new signing key as primary while still
   accepting the previous key for one token lifetime).
2. Roll the secret-manager value (or environment) to serve both values during
   the window: new connections authenticate with the new credential, existing
   pooled connections drain on the old one.
3. Redeploy/restart hosts one role at a time (api, then workers) and verify
   `/health/ready` on each before moving on.
4. After one full connection-pool lifetime plus one JWT lifetime, revoke the
   old credential and verify no `STORAGE_UNAVAILABLE` / auth failures in logs.

## Drill command

```powershell
# Verify no secret material reaches logs or hashes after rotation.
dotnet test --filter FullyQualifiedName~SecretRedactionTests
dotnet test --filter FullyQualifiedName~TenantIsolationTests
```

Record each rotation (what, when, by whom) as an `admin.access`-adjacent audit
note; the audit trail itself is append-only (`audit_appendonly.sql`).
