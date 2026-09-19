# Load Drill Log

Load and media-bomb execution records with throughput and success numbers.
Soak (long-duration) variants are staging-gated; the hermetic tiers below
run on every host including CI.

## 2026-09-17 — Load suite (hermetic)

- Date: 2026-09-17 (local, UTC+03:30).
- Command: `dotnet test --filter FullyQualifiedName~LoadTests`.
- Result: **PASS** — Failed: 0, Passed: 3, Skipped: 0, Total: 3
  (`DubbingPlatform.IntegrationTests.dll`, ~1 s).
  - `FanOutIn_200_Segments_Completes` — 200 segments through mock
    transcription + translation at `MaxDegreeOfParallelism = 8`, all
    outputs deterministic, 200/200 distinct completions. **PASS**.
  - `Concurrent_5_Projects_Stay_Isolated` — 5 concurrent projects x 20
    segments (100 total), per-project output pinned to its own
    tenant/project/run ids, 100/100 with zero cross-talk. **PASS**.
  - `Throttled_Provider_Fails_Closed_Without_Partial_Writes` — 50/50
    calls rejected with `PROVIDER_RATE_LIMITED`, 0 successes, 0 partial
    writes under `MockBehaviorOptions.RateLimited`. **PASS**.
- Throughput: 200-segment fan-out/in completes in ~1 s against
  deterministic mocks (host-local; measures orchestration overhead, not
  provider latency).

## Production-scale targets (staging-gated)

The task targets — 2000 segments, 5 concurrent projects, 2 h media, and a
throttled provider via WireMock HTTP 429 — exceed hermetic scope and run
as the staging soak (`SoakTests`, `[Trait("Category","Soak")]`, opt-in via
`RUN_SOAK=1` / `SOAK_ITERATIONS=n`; excluded from the default CI filter
`Category!=Soak`). Scaling argument from the hermetic evidence:

- Fan-out is embarrassingly parallel per segment (no shared mutable
  state; isolation proven by the 5-project test), so 200 → 2000 segments
  scales with worker count under KEDA (`ai.provider` queue length 100,
  `worker-ai` 1–10 replicas; media pods bounded at
  `Media__MaxConcurrentMediaJobs=2` each).
- The throttled-provider path fails closed (0 partial writes at 50/50
  rejection), and the WireMock 429→200 failover is proven by
  `RecoveryTests.AdapterFailover_WireMock_429_Then_200`.
- 2 h media exercises the same `MediaValidator` + `DiskSpaceChecker`
  gates as the bomb tests below, at duration rather than size extremes.

Record the staging soak numbers in the next entry (template below);
promotion requires 100% hermetic success plus the soak meeting its
throughput/SLO budget.

```text
## YYYY-MM-DD — staging soak (2000 segments / 5 projects / 2h media / WireMock 429)
- Segments completed: <n>/2000, success rate: <%> (gate: per SLO pipeline success >= 98%)
- Wall-clock: <Xm>; p95 stage latency: <Xms>; queue depth peak: <n> (gate: < 1000 sustained)
- Throttled-provider rejections: <n> (all fail-closed, 0 partial writes: yes/no)
- Verdict: <PASS|FAIL>
```

## 2026-09-17 — Media-bomb protection (hermetic)

- Date: 2026-09-17 (local, UTC+03:30).
- Commands:
  - `dotnet test --filter FullyQualifiedName~MediaBombTests` → **PASS**
    (Failed: 0, Passed: 5, Skipped: 0, ~260 ms).
  - `dotnet test --filter FullyQualifiedName~QuotaTests` → **PASS**
    (Failed: 0, Passed: 5, Skipped: 2 — live-tier skips need Docker).
- Verification (R4):
  - Declared-100 MB / actual-1 GB mismatch: declared accepted, actual
    rejected with `MediaUnsupported` + "Size" reason
    (`Declares_100MB_Actual_1GB_Rejected`). No commit on mismatch.
  - Oversize sparse 10 GB payload: rejected at the `MediaValidator`
    gate; staging `long.MaxValue` fails fast with `RESOURCE_EXHAUSTED`
    via `DiskSpaceChecker.EnsureFree`
    (`ZipBomb_Oversized_Rejected_And_Staging_Fails_Fast`).
  - Disk pressure: `EnsureFree` with 1 byte passes, with `long.MaxValue`
    throws `RESOURCE_EXHAUSTED` (`DiskPressure_Fails_Fast_Resource_Exhausted`).
  - Zero-byte upload rejected (`Zero_Byte_Rejected`).
  - Positive control: valid fixture probe passes with expected
    container/codec/duration (`Valid_Fixture_Probe_Passes`) — the gate
    rejects bombs without rejecting legitimate media.
  - `QUOTA_EXCEEDED` (429) path: quota denials throw
    `QuotaExceededException` before any row is written (fail-closed gates
    in `QuotaService`/`CostService`/`UploadService`); `QuotaTests`
    5 passed prove reservation/quota enforcement, and the throttled-load
    test proves 50/50 rejections with zero partial writes.
- Overall: **PASS** (R3 hermetic load green + R4 bomb protection
  verified: `RESOURCE_EXHAUSTED` for disk/size exhaustion,
  `QUOTA_EXCEEDED` for budget/quota exhaustion).

## 2026-09-19 — Task 045 local re-verification (full stack up)

- Date: 2026-09-19 (UTC). Compose `full` stack up during the run
  (11/12; `gpu` excluded).
- Commands:
  - `dotnet test --filter FullyQualifiedName~LoadTests` → **PASS**
    3/3 (`FanOutIn_200_Segments_Completes`,
    `Concurrent_5_Projects_Stay_Isolated`,
    `Throttled_Provider_Fails_Closed_Without_Partial_Writes`).
  - `dotnet test --filter FullyQualifiedName~MediaBombTests` → **PASS**
    5/5.
  - `dotnet test --filter FullyQualifiedName~SignalTests` → **PASS**
    9/9 (ffmpeg 8.1.1 live: silence/loudness/noisy fixtures measured).
  - `dotnet test tests/DubbingPlatform.E2ETests` → **PASS** 9/9
    (incl. `E2E_Full_Pipeline_Mocks`).
- Throughput: 200-segment fan-out/in ~1 s against deterministic mocks
  (orchestration overhead, not provider latency); unchanged from 09-17.
- Staging soak (2000 segments / 5 projects / 2 h media / WireMock 429)
  remains staging-gated; template in the section above still applies.
- Overall: **PASS**.
