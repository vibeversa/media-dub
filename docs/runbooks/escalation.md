# Escalation

- L1 on-call (15m SLA): acknowledge pages (DLQ, pipeline success), triage via
  `docs/observability/slos.md` dashboards, run incident runbook checks.
- L2 platform (1h SLA): provider error/latency, lease recovery, orphan growth,
  export success. Owns config changes, manual retries, queue purges (with
  approval), and failover coordination.
- L3 provider/DBA (4h SLA): provider outage beyond fallback, database failover,
  storage outage requiring vendor action.

Escalate when: SLO breach exceeds 30m, remediation needs prod data changes,
or two L1 rotations cannot resolve. Always audit `admin.access` calls; never
paste secrets into tickets. After resolution, file a postmortem with metric
links and dashboard screenshots.
