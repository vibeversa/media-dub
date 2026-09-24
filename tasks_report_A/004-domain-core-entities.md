# 004 — Domain Core Entities Report

## Status
COMPLETED

## Summary
Implemented all 15 core entities in `src/DubbingPlatform.Domain/Entities/` with exact properties/types, `DomainException` in `Exceptions/`, and `PublicIdMapper` in `Identity/` covering all 17 prefixes via deterministic N-hex encoding. Added `tests/DubbingPlatform.UnitTests/Domain/CoreEntitiesTests.cs` with the 5 required tests. `dotnet build` and lint build succeed with 0 warnings 0 errors and the filtered test run passes 5/5.

## Files Created/Modified
- `src/DubbingPlatform.Domain/Exceptions/DomainException.cs` — `public class DomainException : Exception` with `(string)` and `(string, Exception)` ctors.
- `src/DubbingPlatform.Domain/Identity/PublicIdMapper.cs` — Static mapper with 17 prefix consts, `AllPrefixes`, `ToPublic(Guid,string)`, `FromPublic(string)`, `PrefixFor<T>()`/`PrefixFor(Type)`; N-hex `$"{prefix}{guid:N}"` with XML doc on ULID decision.
- `src/DubbingPlatform.Domain/Entities/Tenant.cs` — `Id/Name/Slug/CreatedAt`, no `TenantId`.
- `src/DubbingPlatform.Domain/Entities/DubbingProject.cs` — Full shape with `ProjectStatus Status`, 2-3 letter language validation, source≠target (OrdinalIgnoreCase).
- `src/DubbingPlatform.Domain/Entities/ProcessingRun.cs` — Full shape with `ProcessingRunStatus`, `Attempt>=0`, `CompletedAt>=StartedAt`.
- `src/DubbingPlatform.Domain/Entities/MediaAsset.cs` — Full shape with `MediaAssetStatus`, `SizeBytes/DurationMs>=0`, `SampleRate/Channels>0`.
- `src/DubbingPlatform.Domain/Entities/UploadSession.cs` — Full shape with `UploadStatus`, `MaxPartBytes>0`, `ExpiresAt>=CreatedAt`.
- `src/DubbingPlatform.Domain/Entities/UploadPart.cs` — Full shape with `PartNumber` 1..10000 enforcement.
- `src/DubbingPlatform.Domain/Entities/Speaker.cs` — Full shape with `FirstAppearanceMs>=0`, `Last>=First`, `Confidence` 0..1.
- `src/DubbingPlatform.Domain/Entities/VoiceProfile.cs` — Full shape with `VoiceType Type`, 2-3 letter `Language` validation.
- `src/DubbingPlatform.Domain/Entities/SpeakerVoiceAssignment.cs` — Full shape, all FK Guids non-empty.
- `src/DubbingPlatform.Domain/Entities/ConsentRecord.cs` — Full shape with `ConsentStatus`, `RevokedAt>=GrantedAt`.
- `src/DubbingPlatform.Domain/Entities/SpeechSegment.cs` — `DurationMs` computed via `checked(endMs-startMs)`, `Validate()` checks `DurationMs==EndMs-StartMs`; `StartMs>=0`, `EndMs>StartMs`, long-range duration check.
- `src/DubbingPlatform.Domain/Entities/OverlapGroup.cs` — Full shape with `StartMs>=0`, `EndMs>StartMs`, duration range check.
- `src/DubbingPlatform.Domain/Entities/SegmentOverlap.cs` — Full shape with `RelationType` restricted to Overlap/Contains/ContainedBy/Adjacent, `Order>=0`, overlap window checks.
- `src/DubbingPlatform.Domain/Entities/ContextWindow.cs` — Full shape with `Sequence/TokenCount>=0`, non-empty text/hash.
- `src/DubbingPlatform.Domain/Entities/SegmentContextAssignment.cs` — `Id/TenantId/SegmentId/ContextWindowId/CreatedAt`.
- `tests/DubbingPlatform.UnitTests/Domain/CoreEntitiesTests.cs` — 5 tests with exact required names.

## Decisions Made
- Private parameterless ctor + public validating ctor + public `Validate()` per entity: private ctor (defaults `string.Empty`) keeps EF Core materialization viable for 006 without allowing invalid state via the public API; public ctor assigns then calls `Validate()`. Properties are `{ get; private set; }`.
- `SpeechSegment.DurationMs` is computed, not a ctor parameter (`checked(endMs-startMs)` before `Validate()`): guarantees `DurationMs=EndMs-StartMs` by construction; `Validate()` additionally asserts equality so EF-materialized drift is caught. `checked` plus explicit `(long)EndMs-StartMs > int.MaxValue` check covers the "duration beyond int range" rule even though `int/int` inputs can only overflow via negative start (already rejected).
- Language validation duplicated as private `IsValidLanguageCode` in `DubbingProject` and `VoiceProfile` (2-3 ASCII letters): no shared helper file to avoid extra surface; comparison is `OrdinalIgnoreCase` so `EN/en` is rejected as same.
- `ProcessingRun.Attempt` validates `>=0` (not `>=1`): spec is silent on base; `>=0` catches negatives without assuming 0- vs 1-based attempts in future tasks.
- `Speaker.LastAppearanceMs >= FirstAppearanceMs` (equal allowed): point-appearances are valid; strict `>` would reject them.
- `UploadSession` enforces only `ExpiresAt>=CreatedAt` and `CompletedAt>=StartedAt` on runs: no clock-skew-sensitive `StartedAt>=CreatedAt` checks.
- Hash fields (`ConfigurationHash`, `ContentHash`, etc.) require only non-empty, not 64-hex: strict hex would risk breaking future seeds/fixtures; `ValueObjects/ContentHash.IsValidHex` remains the strict checker.
- `PublicIdMapper.FromPublic` returns `(string Prefix, Guid Id)` tuple and matches prefixes longest-first with `Ordinal` + `Guid.TryParseExact(hex,"N")`, rejecting `Guid.Empty`: supports variable-length prefixes (`prov_` vs `prj_`) and avoids exceptions-as-control-flow except for the final `DomainException`.
- `PrefixFor(Type)` switches on `type.Name` string literals including the 9 future names (`Artifact`,`ContentObject`,`StageExecution`,`ProviderExecution`,`QualityResult`,`ReviewItem`,`ExportJob`,`Job`) so all 17 prefixes are covered without referencing types that do not exist until 005; unmapped names (e.g. `UploadPart`) throw `DomainException`. Generic `job_` maps from type name `"Job"`.
- `DomainException` is non-sealed with only 2 ctors (no serialization ctor): matches spec literally and avoids SYSLIB0051 obsolete warnings under `AnalysisLevel latest` + `TreatWarningsAsErrors`.
- Shell: `default.shell` is broken; all commands ran via `default.execute` → `tools["claude-code"].PowerShell`, per 003 report.

## Build/Test Results
- `dotnet build` (last 10 lines):
```
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll
  DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
  DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
  DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:12.20
```
- `dotnet test --filter FullyQualifiedName~CoreEntitiesTests` (last 10 lines):
```
A total of 1 test files matched the specified pattern.
A total of 1 test files matched the specified pattern.
No test matches the given testcase filter `FullyQualifiedName~CoreEntitiesTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll

No test matches the given testcase filter `FullyQualifiedName~CoreEntitiesTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll


Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total:     5, Duration: 385 ms - DubbingPlatform.UnitTests.dll (net10.0)
No test matches the given testcase filter `FullyQualifiedName~CoreEntitiesTests` in C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
```
- `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true` (last 10 lines):
```
  DubbingPlatform.ContractTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.ContractTests\bin\Debug\net10.0\DubbingPlatform.ContractTests.dll
  DubbingPlatform.IntegrationTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.IntegrationTests\bin\Debug\net10.0\DubbingPlatform.IntegrationTests.dll
  DubbingPlatform.E2ETests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.E2ETests\bin\Debug\net10.0\DubbingPlatform.E2ETests.dll
  DubbingPlatform.UnitTests -> C:\Users\fazeli\source\hobby\media-dub\tests\DubbingPlatform.UnitTests\bin\Debug\net10.0\DubbingPlatform.UnitTests.dll

Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:25.92
```

## Recommendations for Next Agent (005)
- Repo state: `DubbingPlatform.sln` (10 projects, `net10.0`, `TreatWarningsAsErrors true`, `EnforceCodeStyleInBuild true`) builds clean; Domain now has `Placeholder.cs` (untouched), `Enums/` (25 enums), `ValueObjects/` (10 records), `Entities/` (15 sealed classes), `Exceptions/DomainException.cs`, `Identity/PublicIdMapper.cs`. `src/DubbingPlatform.Domain/DubbingPlatform.Domain.csproj` has zero `PackageReference`/`ProjectReference` — keep it that way (R5); do not add EF references to Domain in 005.
- Shell gotcha: `default.shell` fails (`The system cannot find the path specified`); use `default.execute` with `tools["claude-code"].PowerShell({command, description})`. CWD is already `C:\Users\fazeli\source\hobby\media-dub`. `make`/`docker`/`trivy` absent — validate with `dotnet build`, `dotnet build --no-incremental /p:EnforceCodeStyleInBuild=true`, `dotnet test --filter FullyQualifiedName~<Name>`.
- Naming/conventions: entities are `namespace DubbingPlatform.Domain.Entities`, `public sealed class <Name>` with `{ get; private set; }`, private parameterless ctor defaulting strings to `string.Empty`, public full-arg ctor in task property order calling `public void Validate()` which throws `DubbingPlatform.Domain.Exceptions.DomainException`. Usings are only `DubbingPlatform.Domain.Enums`/`Exceptions` (System comes from ImplicitUsings); file-scoped namespaces, 4-space indent, LF, `insert_final_newline true`.
- Key APIs: `new SpeechSegment(id,tenantId,projectId,runId,sequence,startMs,endMs,status,speakerId,createdAt)` computes `DurationMs`; `new DubbingProject(...,sourceLanguage,targetLanguage,...)` validates 2-3 ASCII letters + inequality; `new UploadPart(...,partNumber,...)` enforces 1..10000; `PublicIdMapper.ToPublic(Guid,string)`, `FromPublic(string)->(string Prefix, Guid Id)`, `PrefixFor<T>()`/`PrefixFor(Type)`, `IReadOnlyList<string> AllPrefixes` (count 17, order tenant_,prj_,run_,asset_,upl_,seg_,spk_,ctx_,voice_,art_,cnt_,exe_,prov_,qc_,rev_,exp_,job_), prefix consts e.g. `PublicIdMapper.TenantPrefix`. `ContentHash.IsValidHex(string?)` is `internal static` — visible only inside Domain.
- Incomplete integration: stage/artifact/provider/QC/review/export/audit/cost entities are 005 scope — do not add `DbContext`/migrations/services; `Artifact`/`ContentObject`/`StageExecution`/`ProviderExecution`/`QualityResult`/`ReviewItem`/`ExportJob`/`Job` types do not exist yet but their prefix strings already exist in `PublicIdMapper` (name-switch); when creating those classes, type names must match exactly (`Artifact`, `ContentObject`, `StageExecution`, `ProviderExecution`, `QualityResult`, `ReviewItem`, `ExportJob`) or `PrefixFor<T>()` breaks. `UploadPart`/`SpeakerVoiceAssignment`/`ConsentRecord`/`OverlapGroup`/`SegmentOverlap`/`SegmentContextAssignment` intentionally have no prefix — `PrefixFor` throws for them.
- Test helpers: `tests/DubbingPlatform.UnitTests/Domain/CoreEntitiesTests.cs` (`DubbingPlatform.UnitTests.Domain.CoreEntitiesTests`, `private static readonly DateTimeOffset Now`) has the 5 tests; keep exact names and keep `All_Scoped_Entities_Have_TenantId` asserting exactly 15 entity types (update count only when 005 adds types, and move/rename that assertion deliberately). Prior `EnumAndValueObjectTests` (6 tests) still passes; total UnitTests is now 1 smoke + 6 + 5 = 12.
- Warnings: `AnalysisLevel latest`; no serialization ctor on new exceptions; no `DateTime.Now` (tests use fixed `2026-01-01`); validate `double.IsNaN` for confidence; use `StringComparison.Ordinal/OrdinalIgnoreCase` explicitly; no secrets/logging in Domain.
- Config: appsettings still 16 empty `{}` sections; Docker/compose/Makefile untouched; no new packages added.
