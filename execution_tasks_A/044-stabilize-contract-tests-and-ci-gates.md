# 044 - Stabilize Contract Tests and Bank CI/Staging Gates

## Objective
Eliminate the flaky `Handles_Timeout(local)` contract test, achieve 3 consecutive green full-suite runs, and execute Docker-gated CI tiers and staging live-drills.

## Scope
1. Fix timing/isolation in `ProviderContractTests.Handles_Timeout(family: "local")`.
2. Execute and record full CI test tiers (Integration, E2E, Backup/Restore).
3. Execute staging live-kill matrix (C1-C6), PITR restore, and rotation drill.

## Files to Modify
- `tests/DubbingPlatform.ContractTests/Providers/ProviderContractTests.cs`
- `tests/DubbingPlatform.ContractTests/Providers/ProviderWireMockFixtures.cs`
- `docs/operations/chaos-log.md`
- `docs/dr/drill-log.md`

## Acceptance Criteria
- [ ] 3 consecutive `dotnet test tests/DubbingPlatform.ContractTests` runs: 39/39 pass.
- [ ] CI pipeline fully green.
- [ ] Staging drill entries appended for C1-C6 and PITR.
- [ ] No production safeguards removed.

## Estimated Effort
Test Fix: Small (<2h) | CI/Drills: Large (1-2 days)