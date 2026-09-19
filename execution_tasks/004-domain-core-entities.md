# Task 4 — Domain Core Entities

## Goal

Implement core tenant/project/media/upload/speaker/segment domain entities with exact fields, types, and rules so persistence and services can build on them.

## Context

Binding: internal IDs ULID-compatible UUIDs (`Guid` in C#, `uuid` in Postgres); public IDs prefixed strings via converters/serialization (mapping: Tenant→`tenant_`, DubbingProject→`prj_`, ProcessingRun→`run_`, MediaAsset→`asset_`, UploadSession→`upl_`, SpeechSegment→`seg_`, Speaker→`spk_`, ContextWindow→`ctx_`, VoiceProfile→`voice_`, Artifact→`art_`, ContentObject→`cnt_`, StageExecution→`exe_`, ProviderExecution→`prov_`, QualityResult→`qc_`, ReviewItem→`rev_`, ExportJob→`exp_`, generic jobs→`job_`). Every tenant-scoped table carries `TenantId` (Guid). One target language per project; multi-target = separate derived projects. Source media immutable byte-for-byte. Artifacts immutable. Timeline ms ints.

## Starting State

Solution builds; `DubbingPlatform.Domain/Enums/*` and `ValueObjects/*` exist as defined in prior task (if missing, create minimal versions of ProjectStatus, ProcessingRunStatus, StageStatus, UploadStatus, MediaAssetStatus, ConsentStatus needed here). No entities yet.

## Scope

Must implement entities in `src/DubbingPlatform.Domain/Entities/`: Tenant, DubbingProject, ProcessingRun, MediaAsset, UploadSession, UploadPart, Speaker, SpeakerVoiceAssignment, VoiceProfile, ConsentRecord, SpeechSegment, OverlapGroup, SegmentOverlap, ContextWindow, SegmentContextAssignment. Must not implement: stage/artifact/provider/QC/review/export/audit/cost entities (next task), DbContext, migrations, services.

## Instructions

1. Namespace `DubbingPlatform.Domain.Entities`. All entities `sealed class` with `Guid Id`, `Guid TenantId` (except Tenant itself which has only Id), `DateTimeOffset CreatedAt/UpdatedAt` where applicable. Use enums from Task 3.
2. Exact shapes:
   - `Tenant { Guid Id; string Name; string Slug; DateTimeOffset CreatedAt; }`
   - `DubbingProject { Guid Id; Guid TenantId; string SourceLanguage; string TargetLanguage; ProjectStatus Status; string SettingsJson; string ConfigurationHash; Guid? SourceMediaAssetId; Guid? ActiveRunId; DateTimeOffset CreatedAt; DateTimeOffset UpdatedAt; }` — validate SourceLanguage/TargetLanguage are 2-3 letter codes, different from each other.
   - `ProcessingRun { Guid Id; Guid TenantId; Guid ProjectId; int Attempt; ProcessingRunStatus Status; string PipelineVersion; string ConfigurationHash; string ProviderRouteHash; string ExecutionSnapshotHash; DateTimeOffset CreatedAt; DateTimeOffset UpdatedAt; DateTimeOffset? StartedAt; DateTimeOffset? CompletedAt; }`
   - `MediaAsset { Guid Id; Guid TenantId; Guid ProjectId; Guid ContentObjectId; string FileName; string Container; string AudioCodec; string? VideoCodec; long SizeBytes; int DurationMs; int SampleRate; int Channels; string ChannelLayout; MediaAssetStatus Status; string? FailureReason; string ContentHash; DateTimeOffset CreatedAt; }`
   - `UploadSession { Guid Id; Guid TenantId; Guid ProjectId; string FileName; string DeclaredContentType; long DeclaredSizeBytes; long MaxPartBytes; string StorageKey; string? MultipartUploadId; UploadStatus Status; string? ClientSha256Hex; string? ContentHash; int PartCount; DateTimeOffset CreatedAt; DateTimeOffset ExpiresAt; }`
   - `UploadPart { Guid Id; Guid TenantId; Guid UploadSessionId; int PartNumber; string ETag; long SizeBytes; DateTimeOffset CreatedAt; }` — PartNumber 1..10000.
   - `Speaker { Guid Id; Guid TenantId; Guid ProjectId; string SpeakerKey; string DisplayName; int FirstAppearanceMs; int LastAppearanceMs; string MappingMethod; string MappingVersion; double Confidence; string? ProviderLabel; DateTimeOffset CreatedAt; }`
   - `VoiceProfile { Guid Id; Guid TenantId; string Provider; string VoiceId; string VoiceVersion; string Language; VoiceType Type; bool CloningEnabled; string? ModelRefJson; DateTimeOffset CreatedAt; }`
   - `SpeakerVoiceAssignment { Guid Id; Guid TenantId; Guid ProjectId; Guid RunId; Guid SpeakerId; Guid VoiceProfileId; string AssignmentReason; string PolicyHash; DateTimeOffset CreatedAt; }`
   - `ConsentRecord { Guid Id; Guid TenantId; string SubjectIdentity; string EvidenceReference; string Scope; string Jurisdiction; ConsentStatus Status; Guid? VoiceProfileId; DateTimeOffset GrantedAt; DateTimeOffset? RevokedAt; }`
   - `SpeechSegment { Guid Id; Guid TenantId; Guid ProjectId; Guid RunId; int Sequence; int StartMs; int EndMs; int DurationMs; string Status; Guid? SpeakerId; DateTimeOffset CreatedAt; }` — DurationMs=EndMs-StartMs.
   - `OverlapGroup { Guid Id; Guid TenantId; Guid ProjectId; Guid RunId; int StartMs; int EndMs; DateTimeOffset CreatedAt; }`
   - `SegmentOverlap { Guid Id; Guid TenantId; Guid OverlapGroupId; Guid SegmentId; string RelationType; int Order; int OverlapStartMs; int OverlapEndMs; }` — RelationType in {Overlap, Contains, ContainedBy, Adjacent}.
   - `ContextWindow { Guid Id; Guid TenantId; Guid ProjectId; Guid RunId; int Sequence; string ContextText; string ContextHash; int TokenCount; DateTimeOffset CreatedAt; }`
   - `SegmentContextAssignment { Guid Id; Guid TenantId; Guid SegmentId; Guid ContextWindowId; DateTimeOffset CreatedAt; }`
3. Add domain exception `DomainException : Exception` in `DubbingPlatform.Domain/Exceptions/DomainException.cs`.
4. Add `PublicIdMapper` static in `DubbingPlatform.Domain/Identity/PublicIdMapper.cs`: `ToPublic(Guid, prefix)` → `$"{prefix}{CrockfordBase32-guid}"` simplified as `$"{prefix}{guid:N}"` deterministic; `FromPublic(string)` parses prefix + 32 hex; `PrefixFor<T>()` mapping above. Document decision: N-hex encoding chosen for determinism without extra ULID library.
5. Validation: constructors or `Validate()` throw DomainException on invalid language, negative timeline, end<=start, duration beyond int range.

## Requirements

- R1: All 15 entities exist with exact properties/types.
- R2: TenantId on all except Tenant.
- R3: Language validation, timeline validation enforced.
- R4: PublicIdMapper covers all 17 prefixes.
- R5: No EF or infra references in Domain.

## Edge Cases and Error Handling

- Negative StartMs/EndMs, End<=Start: throw DomainException.
- Same source/target language: throw DomainException.
- Missing TenantId (Guid.Empty): throw DomainException.
- PartNumber out of 1..10000: throw.

## Security and Safety Requirements

- No secrets in entities. TenantId required on all scoped entities. No logging.

## Testing

Create `tests/DubbingPlatform.UnitTests/Domain/CoreEntitiesTests.cs`:
- `Project_Rejects_Same_Source_Target_Language`
- `Segment_Rejects_Negative_Timeline`
- `UploadPart_Rejects_PartNumber_Zero`
- `PublicIdMapper_RoundTrips_All_Prefixes` (all 17 prefixes)
- `All_Scoped_Entities_Have_TenantId` (reflection)

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~CoreEntitiesTests
```

## Completion Criteria

- Entities compile, tests pass, Domain still references nothing.

## Traceability

- Plan Section 2 action 1 (core subset), actions 4–6; Assumptions 12–13, 15–17; Functional checklist segments/speakers/context.
