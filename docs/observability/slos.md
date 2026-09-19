# Service-Level Objectives

All SLOs use a 30-day rolling window unless noted. Metric names are frozen;
renaming breaks dashboards/alerts. Cardinality policy: `tenant` label only on
aggregated counters, never segment/execution/artifact ids.

## Targets

| SLO | Target | Metrics / Queries |
|-----|--------|-------------------|
| API availability | 99.9% successful responses (non-5xx) / 30d | `http.server.request.duration` + `api.latency` |
| API p95 latency | &lt; 500 ms | histogram `api.latency` |
| Pipeline success | ≥ 98% runs Completed | `projects.completed / (projects.completed + projects.failed)` |
| Stage failure | &lt; 2% stage executions Failed | `stages.failed / (stages.completed + stages.failed)` |
| Provider error | &lt; 5% provider calls error | `provider.errors / provider.calls` |
| Queue depth | &lt; 1000 sustained | broker queue depth + `WorkDispatcher` reasons |
| DLQ depth | 0 sustained | `dlq.depth` (`_skipped` + `_error` parks) |
| Lease recovery | &lt; 1% stages recovered | `leases.recovered / stages.started` |
| Export success | ≥ 99% exports generated | `exports.generated / (exports.generated + exports.failed)` |
| Orphan rate | &lt; 0.1% content objects orphaned | `storage.orphans` + `storage.orphans_detected` |

## Alert thresholds

Defined in `deploy/observability/alerts.yml` (PrometheusRule):

- `DLQDepth > 0 for 5m` → page (L1, 15m SLA).
- `LeaseRecoveryRate > 1%` → ticket (L1, 1h).
- `ProviderErrorRate > 5%` → ticket (L2, 1h).
- `ApiP95 > 500ms for 10m` → ticket (L2, 1h).
- `OrphanRate high` (`storage.orphans` growth) → ticket (L2, 4h).
- `ExportSuccess < 99%` → ticket.
- `PipelineSuccess < 98%` → page.

## Test procedure (alerts fire on simulated failures)

1. Scrape: `curl -f http://localhost:8080/metrics | head -n 50` must contain
   `projects_started`, `stages_*`, `provider_*`, `dlq_depth`, `leases_recovered`.
2. Simulate DLQ: publish a message with `SchemaVersion: 999` to any workload
   queue; assert `dlq.depth` increments and `DLQDepth` alert fires within 5m.
3. Simulate provider errors: set mock behavior `failing` for Transcription;
   run one segment; assert `provider.errors / provider.calls > 5%` fires.
4. Simulate latency: inject 600 ms delay in a test provider; assert `ApiP95`
   alert fires after 10m.
5. Simulate orphans: upload bytes without committing metadata, wait 24h (or
   invoke `OrphanObjectReconciler.ReconcileAsync` with a backdated entry);
   assert `storage.orphans` increments.

## Dashboards

Grafana JSON in `deploy/observability/dashboards/` (Prometheus datasource):

- `pipeline-health.json` — pipeline success, stage failure, api.latency p95,
  segment.duration.
- `provider-health.json` — provider.calls/errors/cost, provider.latency,
  policy denials, route selections.
- `cost.json` — provider.cost, cost.reserved/reconciled, quota/ratelimit
  rejections.
- `queues.json` — queue depth, worker concurrency, DLQ depth, lease
  recoveries, stage retries.
- `reviews.json` — reviews.opened/resolved, review backlog, export
  generated/failed, storage orphans.
