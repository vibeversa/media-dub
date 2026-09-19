# Database Failover

## Symptoms

- `/health/ready` 503 with PostgreSQL check failing.
- API 500s on writes; workers stall on claim/commit.

## Checks

```bash
curl -f http://localhost:8080/health/ready
pg_isready -h $PGHOST -U dubbing
```

Check PITR lag, replication status, and RLS policy presence
(`SELECT * FROM pg_policies WHERE schemaname='public';`).

## Remediation

1. Promote standby per managed-service runbook (RPO 5m, RTO 1h).
2. Verify `app_role` grants and `REVOKE UPDATE,DELETE ON audit_events`
   (see `Sql/audit_appendonly.sql`) on the new primary.
3. Replay outbox (`outbox_message`) — MassTransit outbox redrives on boot.
4. Run `dotnet ef database update` migration job before API rollout.

## Escalation

L2 (1h) → L3 DBA (4h). See `escalation.md`.
