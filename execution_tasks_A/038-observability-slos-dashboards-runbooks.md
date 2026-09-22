# Task 38 — Observability SLOs Dashboards Runbooks

## Goal

Provide SLOs, alerts, dashboards, enriched traces/metrics, protected diagnostics endpoints, and operational runbooks with escalation.

## Context

Binding: SLOs API availability/p95 latency/pipeline success/stage failure/provider error/queue+DLQ depth/lease recovery/export success/orphan rate. Dashboards pipeline/provider/cost/queue/concurrency/DLQ/tenant/storage-orphans/review-backlog. Metrics project started/completed/failed/cancelled, stage started/completed/failed/retry, segment duration, provider latency/error/cost, quota/rate rejections, review open/resolved, export generated/failed, orphans, lease recoveries. Trace enrichment tenant/project/run/stage/provider/model/attempt. Diagnostics (stage/provider detail, DLQ summary, lease status, review backlog) for operators only. Runbooks DLQ/lease/provider-outage/storage-outage/db-failover/orphan/quota-cost/review-backlog + escalation. Correlation end-to-end.

## Starting State

Serilog/OTel/metrics/health exist with basic enrichment. No SLO/alert/dashboard configs, no diagnostics endpoints, no runbooks. Metrics names partially defined (schema_mismatch, dlq.depth, orphans, lease, cost/quota/rate, security).

## Scope

Must implement: SLO definitions, alert rules, dashboard configs, metric emission wiring, trace enrichment completion, diagnostics controllers, runbooks + escalation. Must not implement: CI/CD, HA/DR chaos (next tasks).

## Instructions

1. Metrics: create `src/DubbingPlatform.Infrastructure/Observability/PlatformMetrics.cs` using System.Diagnostics.Metrics Meter `dubbing-platform`: counters `projects.started|completed|failed|cancelled`, `stages.started|completed|failed|retry`, `provider.calls|errors|cost`, `quota.rejections`, `ratelimit.rejections`, `reviews.opened|resolved`, `exports.generated|failed`, `storage.orphans`, `leases.recovered`, `messaging.schema_mismatch`, `dlq.depth` (gauge), histograms `api.latency`, `provider.latency`, `segment.duration`. Wire calls in services/saga/consumers (add one-line increments at each site).
2. SLOs: create `docs/observability/slos.md` with targets: API availability 99.9%/30d, p95 latency <500ms, pipeline success ≥98%, stage failure <2%, provider error <5%, queue depth <1000 sustained, DLQ depth 0 sustained, lease recovery <1% stages, export success ≥99%, orphan rate <0.1%. Alert thresholds in `deploy/observability/alerts.yml` (PrometheusRule): e.g., `DLQDepth>0 for 5m → page`, `LeaseRecoveryRate>1% → ticket`, `ProviderError>5% → ticket`, `ApiP95>500ms 10m → ticket`, `OrphanRate high → ticket`.
3. Dashboards: `deploy/observability/dashboards/` JSON for Grafana: `pipeline-health.json`, `provider-health.json`, `cost.json`, `queues.json`, `reviews.json` (panels per dashboard list in plan; use Prometheus datasource, metric names above).
4. Trace enrichment: ensure every Activity sets tags tenant/project/run/stage/provider/model/attempt (add helper `TraceEnricher.Set(...)` called in BaseConsumer + provider recorder + FFmpeg wrapper).
5. Diagnostics: `src/DubbingPlatform.Api/Controllers/AdminController.cs` (already shelled) implement `GET /api/v1/admin/stages/{execId}`, `/provider-executions/{id}`, `/dlq/summary`, `/leases/status`, `/reviews/backlog` — all `[Authorize(Policy=RequireService|TenantAdmin)]`, read-only, paginated where lists, never expose secrets.
6. Runbooks: `docs/runbooks/` one md per incident (dlq, lease, provider-outage, storage-outage, db-failover, orphan, quota-cost, review-backlog) each with symptoms → checks (queries/curls) → remediation → escalation (`docs/runbooks/escalation.md`: L1 on-call → L2 platform → L3 provider/DBA with SLAs 15m/1h/4h).
7. Verify correlation header flows API→messages→providers→logs/traces (add integration assertion in tests).

## Requirements

- R1: Metrics scraped at /metrics with all names.
- R2: Alerts fire on simulated failures (document test procedure).
- R3: Dashboards display core metrics.
- R4: Runbooks reviewed/tested.
- R5: Diagnostics protected (401/403 without role).

## Edge Cases and Error Handling

- Metrics cardinality: tenant label only on aggregated counters, never segment-id (prevent explosion — document).
- Diagnostics on large DLQ: paginate, cap 100 rows.
- Missing OTLP endpoint → local console exporter, no crash.

## Security and Safety Requirements

- Diagnostics require Service/TenantAdmin; no secrets in metrics/traces/logs; tenant label safe (no PII).

## Testing

Create `tests/DubbingPlatform.IntegrationTests/Observability/ObservabilityTests.cs`: `Metrics_Scraped` (/metrics contains dubbing counters), `Diagnostics_Protected` (401 anon, 403 viewer, 200 admin), `Correlation_EndToEnd` (POST project → check logs contain same correlation; simplified via response header echo + message header assert).

## Validation

```bash
dotnet build
dotnet test --filter FullyQualifiedName~ObservabilityTests
curl -f http://localhost:8080/metrics | head -n 50
```

## Completion Criteria

- SLOs/alerts/dashboards/diagnostics/runbooks complete; tests pass.

## Traceability

- Plan Section 27; Assumption 75; Observability checklist.
