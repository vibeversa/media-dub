# 044 — Stabilize Contract Tests and CI Gates Report

## Status
COMPLETED

## Summary
Fixed the flaky `ProviderContractTests.Handles_Timeout(local)` by adding an explicit fast `POST /warmup` stub (`StubLocalWarmup`) so warmup no longer depends on WireMock's unmatched-404 → `GET /health` fallback under parallel load, and by giving the local case dedicated timing (1 s client timeout vs 5 s `/infer` delay instead of the shared 2 s/10 s). Achieved 3 consecutive green `DubbingPlatform.ContractTests` runs (39/39 each) plus green Unit (310), E2E (9), and Integration (91 passed / 84 Docker-gated skipped) tiers. Appended 2026-09-19 entries to the chaos log (C1–C6 matrix), DR drill log (PITR), and rotation drill log; live destructive kills remain staging-gated (no Docker/kubectl on this host) per repo convention. No production code touched.

## Files Created/Modified
- `tests/DubbingPlatform.ContractTests/Providers/Fixtures/ProviderWireMockFixtures.cs` — modified: added `StubLocalWarmup` (POST `/warmup` → fast 200 `{"status":"warmed"}`) with XML doc explaining why timeout test uses it and others keep health-fallback coverage.
- `tests/DubbingPlatform.ContractTests/Providers/ProviderContractTests.cs` — modified: `Handles_Timeout(local)` now stubs `/warmup` + `/health`, uses dedicated 1 s timeout / 5 s infer delay with comment citing 029/033 flakes.
- `docs/operations/chaos-log.md` — modified: appended `2026-09-19 Task 044` entry (flake fix, all CI tier results, C1–C6 staging-gated table).
- `docs/dr/drill-log.md` — modified: appended `2026-09-19 Task 044 PITR` entry (BackupRestoreTests 1/1 hermetic, live PITR staging-gated with N/A fields).
- `docs/operations/rotation-drill-log.md` — modified: appended `2026-09-19` rotation re-verification entry (SecretRedaction 4/4, TenantIsolation 7+1; live rotation staging-gated).

## Decisions Made
- **Explicit `/warmup` stub instead of relying on 404 fallback**: `WarmupAsync` tries POST `/warmup` then falls back to GET `/health` on 404. Leaving `/warmup` unstubbed made warmup depend on WireMock's default unmatched handling, the timing-sensitive part under parallel load (029 reported 38/39 then 39/39 on re-run; 033 same). Stubbing it gives sub-100 ms deterministic warmup so only `/infer` exercises the timeout path.
- **Dedicated 1 s/5 s timing for local only**: shared 2 s/10 s left ~8 s of blocked server delay per case, amplifying thread-pool contention when the local case runs last (after azure/openai/google delays). 1 s vs 5 s keeps a 4 s margin (robust) while halving blocked time. Other families untouched to avoid scope creep; both warmup paths stay covered (timeout via `/warmup`, all other local cases via `/health` fallback).
- **No `HttpClient` disposal refactor**: all `Create*` helpers leak `HttpClient` equally (prod DI owns lifetime); the leak is not the local-only differentiator, so changing only local would be inconsistent. Left as-is.
- **Honest staging-gated entries, not fake live runs**: this host has no Docker daemon and no `kubectl` (verified both missing). Live C1–C6 kills and managed PITR cannot be executed here, so entries record hermetic/CI evidence + explicit STAGING-GATED deferral with staging commands, matching the existing log convention. No production safeguards removed (only test + doc files changed).
- **Rotation log updated although not in Files to Modify**: scope item 3 explicitly includes the rotation drill, so appended to the canonical `rotation-drill-log.md` and documented here.

## Build/Test Results
- `dotnet build --nologo -v q` (last 4):
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
Time Elapsed 00:00:09.86
```
- `dotnet test tests/DubbingPlatform.ContractTests --nologo -v q` run 1 (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 54 s - DubbingPlatform.ContractTests.dll (net10.0)
```
- Run 2 (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 45 s - DubbingPlatform.ContractTests.dll (net10.0)
```
- Run 3 (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 38 s - DubbingPlatform.ContractTests.dll (net10.0)
```
- `dotnet test tests/DubbingPlatform.UnitTests --nologo -v q` (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:   310, Skipped:     0, Total:   310, Duration: 17 s - DubbingPlatform.UnitTests.dll (net10.0)
```
- `dotnet test tests/DubbingPlatform.E2ETests --nologo -v q` (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     9, Skipped:     0, Total:     9, Duration: 179 ms - DubbingPlatform.E2ETests.dll (net10.0)
```
- `dotnet test tests/DubbingPlatform.IntegrationTests --nologo -v q` (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:    91, Skipped:    84, Total:   175, Duration: 1 m 32 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `--filter FullyQualifiedName~RecoveryTests` (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     9, Skipped:     2, Total:    11, Duration: 30 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `--filter FullyQualifiedName~BackupRestoreTests` (last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     1, Skipped:     1, Total:     2, Duration: 16 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```
- `--filter FullyQualifiedName~SecretRedactionTests` (Unit, last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     4, Skipped:     0, Total:     4, Duration: 102 ms - DubbingPlatform.UnitTests.dll (net10.0)
```
- `--filter FullyQualifiedName~TenantIsolationTests` (Integration, last 3):
```
A total of 1 test files matched the specified pattern.
Passed!  - Failed:     0, Passed:     7, Skipped:     1, Total:     8, Duration: 15 s - DubbingPlatform.IntegrationTests.dll (net10.0)
```

## Recommendations for Next Agent (045)
- Repo state: builds 0/0 (`TreatWarningsAsErrors`). Not a git repo. No Docker/kubectl on this host; hermetic suites pass, Docker-gated tests skip via `[SkippableFact]` + `Skip.If` (never `[Fact]`+skip — v2 reports FAIL) and run live in CI. Task 044 touched only test + doc files; `src/` untouched since 043.
- Key files for follow-ups: `tests/DubbingPlatform.ContractTests/Providers/ProviderContractTests.cs` (`Handles_Timeout` local branch now uses `localTimeout=1s`/`localDelay=5s` + `StubLocalWarmup`; other families share `clientTimeout=2s`/`mockDelay=10s`; `CreateWarmedLocalAsync(TimeSpan?)` helper unchanged), `tests/DubbingPlatform.ContractTests/Providers/Fixtures/ProviderWireMockFixtures.cs` (`StubLocalWarmup` POST `/warmup` fast-200; `StubLocalHealth` GET `/health`; `StubLocalInfer(server,status,body,delay?)`), `docs/operations/chaos-log.md` (2026-09-19 C1–C6 table), `docs/dr/drill-log.md` (2026-09-19 PITR entry + template), `docs/operations/rotation-drill-log.md` (2026-09-19 entry).
- Gotchas: NEVER run two `dotnet test` concurrently (MSBuild/testhost contention; one run timed out at the default 120 s limit — use `timeout` 300000–600000 for contract/integration runs). Contract full suite takes ~38–54 s (variance is host load, not flake). `WireMockServer` is per-test-instance (`_server` fresh each case); do not add `Reset()` — it is a no-op on fresh servers. `LocalInferenceProvider.WarmupAsync` POSTs `/warmup` first (explicit stub in timeout test) then falls back to `/health` (all other local tests) — keep both paths covered. `ProviderHttpHelper.SendAsync` maps timeout to `PROVIDER_TIMEOUT` only when `!cancellationToken.IsCancellationRequested`; outer CTS is 30 s, inner `HttpClient.Timeout` 1 s (local) / 2 s (others).
- Conventions: error codes via `ErrorCodes.cs` + `ErrorCodeException`; secrets as `"CHANGE_ME"` placeholders, never real material in logs (drill logs record counts/durations/verdicts only); `ArgumentList` for FFmpeg (not used here).
- Config keys (unchanged): `Features__LocalInferenceEnabled` (false default), `LocalInference__Endpoint` (`http://localhost:8081` local, `http://local-inference:8000` cluster), `LocalInference__Protocol`, `LocalInference__MaxConcurrency`, `Media__MaxConcurrentMediaJobs=2`.
- Test helpers: `MockBehaviorOptions.Success|RateLimited|Timeout`; `ProviderExecutionRecorder.BuildIdempotencyKey(run:N,stage,scope,attempt)`; WireMock `StubLocalHealth|StubLocalWarmup|StubLocalInfer`, `StubAzureTranscribeDelay`, `StubOpenAiTranscriptionsDelay`, `StubGoogleRecognizeDelay`.
- Warnings: `deploy/verify.sh` (bash + `kubectl`/`kubeconform`/`helm` gates) cannot run on this Windows host without those tools — structural/k8s gates run in CI. `deploy/k8s/gpu-worker.yaml` (task-text path) must stay in sync with canonical `deploy/k8s/workers-gpu.yaml` (checked by `verify.sh`) if worker env changes.
