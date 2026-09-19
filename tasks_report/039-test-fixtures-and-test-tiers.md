# 039 — Test Fixtures and Test Tiers Report

## Status
COMPLETED

## Summary
Delivered the full Task 39 testing slice: rewrote `scripts/generate-fixtures.sh` to generate 10 deterministic sub-5MB/sub-15s fixtures (48kHz canonical, fixed seeds) plus the 2 legacy Task 19 controls, with `fixtures/README.md` golden tables; added `TestFixtureBase` (PG16/RabbitMQ3.13/Redis7/MinIO Testcontainers + WireMock + mock defaults + ffmpeg/fixture probes, all Docker-missing paths skip); added 8 mock-based E2E pipeline tests and the Recovery/Load/Soak/MediaBomb/BackupRestore/MigrationCompat/Signal tiers. Validation: build 0/0, E2E 9/9, new tiers 25 passed + 4 live-PG skipped (Signal 6/6 live on ffmpeg), Unit 310/310, Contract 39/39, all regression chunks green with zero failures.

## Files Created/Modified
- `scripts/generate-fixtures.sh` — rewritten: 10 fixtures (single/multi/overlap/silence/music-dialogue/noisy wavs, single/multi mp4, low-quality 8k→48k, non-english 520Hz + language=es) + legacy valid-2s/invalid-text, idempotent skip unless `FIXTURES_FORCE=1`, 5MB size gate, bash LF.
- `fixtures/` (12 generated binaries, 45B–2.3MB) + `fixtures/README.md` — durations, expected segments/speakers per fixture, golden thresholds (±100ms, ±1LU, 50/100ms + 15%/1.15x).
- `tests/DubbingPlatform.IntegrationTests/Fixtures/TestFixtureBase.cs` — abstract base: `StartPostgresAsync/RabbitMqAsync/RedisAsync/MinioAsync` (compose-pinned images, skip without Docker), `StartWireMock()`, `MockOptions/MockSuccessOptions`, `FixtureDirectory/FixturePath/RequireFixture` (ordinal name match), `FfmpegAvailableAsync/SkipUnlessFfmpegAsync`, `CreatePgOptions/MigrateAsync/SeedTenantAsync`, `IsInfrastructureUnavailable`, `DeleteDirQuietly`.
- `tests/DubbingPlatform.E2ETests/FullPipelineTests.cs` — 8 mock E2Es, 5-min `CancellationTokenSource` budget each: `E2E_Full_Pipeline_Mocks` (upload→progress→Completed, MP4 URL, tolerances, stable voices, context-aware translation, timing, background flag), `E2E_LowConfidence_Fallback`, `E2E_Separation_Fallback`, `E2E_Overlap_Fixture`, `E2E_Video_Tolerance`, `E2E_Audio_Only`, `E2E_Exports`, `E2E_Review_Resolution`.
- `tests/DubbingPlatform.E2ETests/DubbingPlatform.E2ETests.csproj` — added `Xunit.SkippableFact 1.3.12` (parity with IntegrationTests).
- `tests/DubbingPlatform.IntegrationTests/Recovery/RecoveryTests.cs` — 9 hermetic decision-semantics facts (lease expiry, token fencing, ledger dedup, cancel races, retry supersede, broker-outage buffer/redrive, partition heal, failover pick, WireMock 429→200) + 2 live PG facts (crash→`RecoverStaleAsync`=1→`RetryPending`; duplicate claim `IsNew=false` same id).
- `tests/DubbingPlatform.IntegrationTests/Load/LoadTests.cs` — 200-segment fan-out/in (`Parallel.ForEachAsync`, dop 8), 5×20 concurrent-project isolation, 50× throttled fail-closed (0 successes).
- `tests/DubbingPlatform.IntegrationTests/Load/SoakTests.cs` — `[Trait("Category","Soak")]`, skips unless `RUN_SOAK=1` (30min) or `SOAK_ITERATIONS=n`; asserts zero failures + byte/voice determinism.
- `tests/DubbingPlatform.IntegrationTests/Media/MediaBombTests.cs` — real `MediaValidator` (declared-100MB-ok vs actual-1GB-reject, 10GB sparse reject, zero-byte reject, fixture positive control) + real `DiskSpaceChecker` (`long.MaxValue`→`RESOURCE_EXHAUSTED`).
- `tests/DubbingPlatform.IntegrationTests/Media/SignalTests.cs` — hermetic goldens (`LoudnessTarget` Web −16/Broadcast −23, `TimingWindow` 50/100/15%/1.15x, canonical 48kHz-stereo validator defaults) + 3 live FFmpeg fixture probes (silence ≥8s + audible tail, sane loudness, noisy audible).
- `tests/DubbingPlatform.IntegrationTests/Persistence/BackupRestoreTests.cs` — hermetic `BackupManifest` JSON round-trip + live `pg_dump -a --inserts` → TRUNCATE → `ExecScriptAsync` restore → counts equal.
- `tests/DubbingPlatform.IntegrationTests/Persistence/MigrationCompatTests.cs` — hermetic migration-files gate + live `GetAppliedMigrationsAsync` non-empty, 6 core tables + 16 pinned `stage_executions` columns via `information_schema` (`{0}`-only params).

## Decisions Made
- **E2E stays hermetic, fixtures referenced not required**: `AssertFixtureSize` checks the 5MB gate only when the file exists; durations come from `fixtures/README.md` constants — R2 passes with or without generated binaries, no ffmpeg needed.
- **No `Fact(Timeout=)`**: xUnit 2.9 has no Timeout; the 5-min budget is a real `CancellationTokenSource(TimeSpan.FromMinutes(5))` threaded through every mock call.
- **Backup uses `--data-only --inserts`**: `COPY FROM stdin` is not replayable over `ExecScriptAsync`; plain INSERTs restore deterministically after TRUNCATE…CASCADE.
- **Compat pins names, not full schemas**: 6 core tables + 16 `stage_executions` columns (lease/state-machine SQL surface) + applied-migrations non-empty; avoids brittleness against future additive columns.
- **Live lease-expiry via SQL backdate**: `ClaimAsync` with negative TTL was unverified, so the crash test claims normally then backdates `lease_expires_at` with raw SQL — deterministic, no sleeps.
- **MinIO image explicit**: parameterless `MinioBuilder()` is obsolete (error under warnings-as-errors) → `new MinioBuilder("minio/minio")`.

## Build/Test Results
- `dotnet build --nologo -v q` (last 5):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)

Time Elapsed 00:00:07.01
```
- `dotnet test E2ETests --filter FullyQualifiedName~FullPipelineTests` (last 2):
```
Passed!  - Failed:     0, Passed:     8, Skipped:     0, Total:     8, Duration: 115 ms - DubbingPlatform.E2ETests.dll (net10.0)
```
- `dotnet test E2ETests --filter FullyQualifiedName~FullPipelineTests.E2E_Full_Pipeline_Mocks` (last 2):
```
Passed!  - Failed:     0, Passed:     1, Skipped:     0, Total:     1, Duration: 90 ms - DubbingPlatform.E2ETests.dll (net10.0)
```
- New tiers `--filter RecoveryTests|LoadTests|MediaBombTests|BackupRestoreTests|MigrationCompatTests|Media.SignalTests` (last 2):
```
Passed!  - Failed:     0, Passed:    25, Skipped:     4, Total:    29, Duration: 11 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
(4 skips = live-PG only: 2 Recovery + 1 BackupRestore + 1 MigrationCompat; Signal 6/6 live on ffmpeg.)
- Soak gate: `FullyQualifiedName~SoakTests` → `Skipped! - Failed: 0, Passed: 0, Skipped: 1` (excluded by `Category!=Soak`).
- Regressions: Unit `Passed: 310`; Contract `Passed: 39`; E2E full `Passed: 9`; Integration chunks — Persistence|Orchestration 5+14skip, Media|Pipeline 26+36skip, Observability|Smoke 5+3skip, Cost|Security 12+3skip, Messaging 0+5skip, Storage 1+7skip, ErrorEnvelope|Export 13, Health 2+2pass/1skip, Idempotency 2+4skip, ProgressRetryReview 7, ProjectsUploads|TenantIsolation 7+7skip. Zero failures everywhere.
- `bash scripts/generate-fixtures.sh` re-run: all `exists, skipping` (idempotent); `file` = Bourne-Again, 0 CR bytes (LF).
- Fixed during work: RunId GUID typo, stage counter 8→12, 10GB-disk assertion → `long.MaxValue` (C: has >10GB free), `Assert.Equal(1, ledger.Count)` → `Assert.Single` (xUnit2013), `tests/Fixtures` shadowing `fixtures` (ordinal match).

## Recommendations for Next Agent (040)
- Repo state: builds 0/0 incl. `EnforceCodeStyleInBuild` + `AnalysisLevel latest` + `TreatWarningsAsErrors`. Unit 310, Contract 39, E2E 9/9, new tiers 25+4skip, Soak 1 skip-by-default. PG/Redis/Rabbit/MinIO suites skip without Docker (absent locally, live in CI); ffmpeg/ffprobe live at `C:\ffmpeg\bin` on PATH on this host. MassTransit 8.5.10. No new migration in 039. Not a git repo (`git status` → fatal); file list above is authoritative.
- Key APIs for 040 (CI/K8s — reuse, do not rename): `scripts/generate-fixtures.sh` (`[out-dir]` arg, `FIXTURES_FORCE=1`, 5MB gate, exit 1 without ffmpeg — CI images need ffmpeg), `fixtures/README.md` golden table, `IntegrationTests/Fixtures/TestFixtureBase.cs` (`StartPostgresAsync/RabbitMqAsync/RedisAsync/MinioAsync(ITestOutputHelper?)`, `StartWireMock()`, `MockOptions(scenario)/MockSuccessOptions()`, `FixtureDirectory/FixturePath/RequireFixture`, `FfmpegAvailableAsync/SkipUnlessFfmpegAsync`, `CreatePgOptions/MigrateAsync/SeedTenantAsync`), `E2ETests/FullPipelineTests.cs` (8 E2Es, 5-min CTS budget each), `IntegrationTests/{Recovery/RecoveryTests,Load/LoadTests,Load/SoakTests [Trait Category=Soak, RUN_SOAK/SOAK_ITERATIONS],Media/MediaBombTests,Media/SignalTests,Persistence/BackupRestoreTests,Persistence/MigrationCompatTests}`.
- Gotchas: NEVER run two `dotnet test` concurrently on this host — orphaned runs contend (MSBuild/testhost) and look like hangs; check `Win32_Process dotnet.exe/testhost.exe` and let one finish (a stale PID may already have exited — re-check before killing). `FixtureDirectory` matches `fixtures` with `StringComparison.Ordinal` (capital-`Fixtures` source dirs shadow it on Windows). `MinioBuilder` requires an image string. xUnit2013 forbids `Assert.Equal(1, coll.Count)` → `Assert.Single`. `MinioBuilder("minio/minio:latest")` in `HealthCheckTests.Ready_Returns_200_When_Up` skips in ~10s alone. Npgsql-down health tests take ~27s (connection timeouts — normal). `dotnet test --filter Category!=Soak` needs quoting in PowerShell; full unfiltered suite exceeds the tool timeout — run per-project/per-namespace chunks. E2E `ResolveFixturePath` is file-existence based (no shadowing bug). `container.ExecAsync(["sh","-c","PGPASSWORD=$POSTGRES_PASSWORD pg_dump -a --inserts -h localhost -U $POSTGRES_USER -d $POSTGRES_DB"])` + `ExecScriptAsync` is the proven backup pattern.
- Conventions: file-scoped namespaces, 4-space/LF, `StringComparison.Ordinal(IgnoreCase)`, `CultureInfo.InvariantCulture`, `ConfigureAwait(true)` in IntegrationTests async, raw SQL `ExecuteSqlRawAsync/SqlQueryRaw` with `{0}` only (`"Value"` alias for scalar mapping), `TenantContext.BeginScope` (tenant) / `BeginMaintenanceScope` (cross-tenant + sweeps/migrations), `await using` containers, `#pragma warning disable CA1031` only with probe justification, Moq `Returns(Task.FromResult(...))`, `WebApplicationFactory<CorrelationIdMiddleware>` marker (never `Program`), no secrets (fixtures synthetic tones only).
- Config keys: no new sections in 039. Env-only: `RUN_SOAK=1`/`SOAK_ITERATIONS=n`, `FIXTURES_FORCE=1`. Existing: `Media:MaxUploadBytes`(5GB)/`MaxDurationMs`/`AllowedContainers`/`MinDiskFreeBytes`(1GB)/`SeparationThreshold`(0.70), `Retry:LogicalStageMaxAttempts`(3), `ConnectionStrings:Default` (PG16), `Storage:*` (MinIO), `Transport:Provider` (`InMemory` fast), `Auth:SigningKey/Audience` (tests: `test-signing-key-0123456789abcdef-test-signing-key-01`/`dubbing-api`).
