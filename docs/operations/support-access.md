# Support Access and On-Call

## Diagnostics access control

Admin diagnostics endpoints (DLQ summary, provider executions, queue
depths, lease recoveries) require the `RequireTenantAdmin` policy, which
allows only the `Service` and `TenantAdmin` roles:

- Policy shape pinned by `ObservabilityTests.Diagnostics_PolicyShape`
  (allows `TenantAdmin` + `Service`, denies `ProjectViewer`).
- Anonymous callers get 401 (`Diagnostics_Anonymous_401`).
- `ProjectViewer` gets 403 on the live path (`Diagnostics_Viewer_403`,
  Docker-gated, live in CI).
- `TenantAdmin` gets 200 with the DLQ payload (`Diagnostics_Admin_200`,
  Docker-gated, live in CI).

Verification on 2026-09-17 (`SecretRedactionTests` + `TenantIsolationTests`
+ `ObservabilityTests` filter): Unit 4/4 passed; Integration 11 passed,
3 skipped (live-DB 200/403 cases need Docker, live in CI). Verdict:
**PASS** — support access is Service/TenantAdmin-only, and secret
material never reaches logs (see rotation drill log).

Support engineers authenticate as `Service` (machineJWT for on-call
tooling) or as the tenant's `TenantAdmin`. Never mint long-lived admin
tokens for a drill; never paste secrets into tickets (runbook
`docs/runbooks/escalation.md`).

## On-call dashboards

Grafana JSON (Prometheus datasource) in `deploy/observability/dashboards/`:

| Dashboard | File | Used for |
|---|---|---|
| Pipeline health | `pipeline-health.json` | Pipeline success ≥ 98%, stage failure < 2%, API p95 < 500 ms |
| Provider health | `provider-health.json` | Provider error < 5%, route selections, policy denials |
| Cost | `cost.json` | Spend vs reservations, quota/rate-limit rejections |
| Queues | `queues.json` | Queue depth < 1000, DLQ depth 0, lease recoveries, retries |
| Reviews | `reviews.json` | Review backlog, export success ≥ 99%, orphans |

Raw metrics: `curl -f http://localhost:8080/metrics` (must expose
`projects_started`, `stages_*`, `provider_*`, `dlq_depth`,
`leases_recovered`). Alert thresholds live in
`deploy/observability/alerts.yml` and are restated in
`docs/observability/slos.md`.

## Escalation

- L1 on-call (15 min SLA): DLQ / pipeline-success pages.
- L2 platform (1 h SLA): provider error/latency, lease recovery, orphans,
  exports; owns failover coordination.
- L3 provider/DBA (4 h SLA): vendor-side outages, DB failover, storage.
- Full matrix: `docs/runbooks/escalation.md`.
- Incident runbooks: `docs/runbooks/db-failover.md`,
  `storage-outage.md`, `provider-outage.md`, `dlq.md`, `lease.md`,
  `orphan.md`, `quota-cost.md`, `review-backlog.md`.
