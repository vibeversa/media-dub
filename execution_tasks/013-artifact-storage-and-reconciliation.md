# Task 13 — Artifact Storage and Reconciliation

## Goal

Implement immutable tenant-scoped content storage, logical artifacts, atomic publication, relational lineage, deduplication, presigned URLs, integrity checks, and orphan reconcilers.

## Context

Binding: ContentObject immutable tenant-scoped SHA-256, size, format, status, storage key, created/last-referenced. Artifact logical with project/run/stage, type, content ref, provider/model, config+snapshot hashes, status, schema version. Lineage relational (ArtifactParent/StageInput/Output), JSONB only for non-relational metadata. Key `{tenantId}/{projectId}/{processingRunId}/{stageType}/{artifactType}/{contentHash}{extension}`. Streaming upload/download, streaming SHA-256, use storage checksum when authoritative (do not re-download to hash). Publication: reserve ContentObject Pending + Artifact Pending → upload blob → verify checksum → mark both Committed + complete stage + outbox publish in same transaction. Tenant dedup unique (TenantId,ContentHash) reuse within tenant + new logical row; cross-tenant disabled. Presigned URLs require auth + ownership. Optional SHA revalidate on QC/render. Retention: logical first, physical only when refcount zero + retention satisfied + no hold. Schema version recorded.

## Starting State

DbContext + migrations with content/artifact tables exist. S3 packages referenced. No IArtifactStorage, no S3 adapter, no ArtifactService, no reconcilers. MinIO available via compose/Testcontainers.

## Scope

Must implement: IArtifactStorage, S3 adapter, ArtifactService/ContentObjectService, publication workflow, dedup, presigned URLs, integrity option, retention hooks, orphan/dangling reconcilers, MinIO tests. Must not implement: business workers, retention sweeper scheduling (Task 37), export logic.

## Instructions

1. Create `src/DubbingPlatform.Application/Abstractions/IArtifactStorage.cs`:
   ```csharp
   public interface IArtifactStorage {
     Task UploadAsync(Stream content, string storageKey, string contentType, CancellationToken ct);
     Task<Stream> DownloadAsync(string storageKey, CancellationToken ct);
     Task<bool> ExistsAsync(string storageKey, CancellationToken ct);
     Task DeleteAsync(string storageKey, CancellationToken ct);
     Task<string> GetPresignedDownloadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct);
     Task<string> GetPresignedUploadUrlAsync(string storageKey, TimeSpan expiry, CancellationToken ct);
     Task<string?> GetStorageChecksumAsync(string storageKey, CancellationToken ct);
   }
   ```
2. Implement `src/DubbingPlatform.Infrastructure/Storage/S3ArtifactStorage.cs` with AWSSDK.S3 (TransferUtility streaming, 8MB part size); compute SHA-256 streaming on upload path (wrap stream in hashing stream); compare to storage ETag/checksum when available; expiry default 15min for presigned.
3. Implement `src/DubbingPlatform.Application/Services/ArtifactService.cs` + `ContentObjectService.cs`: `PublishAsync(tenant,project,run,stageType,artifactType,Stream,extension,contentType,provider,model,configHash,snapshotHash,parentIds[],ct)`: check dedup `SELECT ... WHERE tenant+hash` → if exists reuse ContentObject (update LastReferencedAt) + create new Artifact Committed; else reserve Pending rows, upload, verify, commit both + link parents + StageOutput row in same EF transaction with outbox publish. `GetDownloadUrlAsync` validates ownership (`artifact.TenantId==caller && project match`) else throw ForbiddenException.
4. Storage key builder `BuildKey(tenant,project,run,stage,artifactType,hash,ext)` exactly per convention (all lowercase Guids N-format).
5. Implement `src/DubbingPlatform.Workers/Services/OrphanObjectReconciler.cs` (BackgroundService daily + manual trigger): find storage objects without committed metadata older than 24h → delete/quarantine (quarantine prefix `quarantine/`); find committed ContentObjects with zero artifact refs older than grace (7d) → mark Orphaned. Log + metric `storage.orphans_detected`.
6. Retention hooks: `CanDeleteContentObject` checks refcount==0 + retention days (from RetentionOptions via parameter) + no active RetentionHold; called by Task 37 but defined here.
7. Config: `StorageOptions` already defined; wire S3 client with endpoint/bucket/ssl/keys; bucket auto-create on startup in Development only.

## Requirements

- R1: Interface exact; S3 streaming; presigned 15min default.
- R2: Publication atomic (DB commit + outbox same tx; no committed artifact on DB fail).
- R3: Tenant dedup reuse; cross-tenant never reuse.
- R4: Presigned requires ownership.
- R5: Reconcilers detect orphans/dangling; holds block delete.

## Edge Cases and Error Handling

- Checksum mismatch → ARTIFACT_CHECKSUM_MISMATCH, blob deleted, stage fails.
- Storage unavailable → STORAGE_UNAVAILABLE, retryable.
- Failed DB commit → blob remains orphan for reconciler, no committed rows.
- Duplicate bytes concurrent → unique constraint wins, loser reuses winner.
- Missing artifact → ARTIFACT_UNAVAILABLE.

## Security and Safety Requirements

- Tenant-prefixed keys enforced; ownership check before URL; URLs 15min expiry; no secrets in keys/logs; streaming avoids disk exhaustion (cap memory 16MB, spill to /tmp).

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Storage/ArtifactStorageTests.cs` (Testcontainers MinIO + PG): `Upload_Produces_Committed_Rows`, `Duplicate_Within_Tenant_Reuses_ContentObject`, `Duplicate_Across_Tenants_No_Reuse`, `Presigned_Requires_Ownership` (403 on wrong tenant), `Failed_Commit_Leaves_No_Committed_Artifact`, `Orphan_Reconciler_Detects_Blob`, `Lineage_Query_Returns_Parents`.

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ArtifactStorageTests
```

Env: `Storage__Endpoint=localhost:9000 Storage__Bucket=dubbing-test`.

## Completion Criteria

- Storage + publication + lineage + reconcilers work; tests pass.

## Traceability

- Plan Section 5 all actions; Assumptions 21–26; Functional checklist traceability; Error checklist checksum/orphan/holds.
