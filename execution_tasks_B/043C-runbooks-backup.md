# Task 043C — Incident Runbooks and Backup Drills

**Required/Optional:** Required
**Complexity:** S

## Goal
Provide product incident runbooks and backup/restore drills for the new durable entities.

## Context
Split from Task 043. Plan A owns backend runbooks; this task adds the product surfaces (auth/CDN/SSE/deploy/notification-backlog/contract-drift/upload-surge/review-surge) plus backup coverage for new tables (tenant users, preferences, notifications, activity, memberships, voice previews, extended project metadata). Hosting is 043A, rollout 043B.

## Starting State
Depends on Tasks 038 (monitors that trigger runbooks), 043A (hosting topology runbooks reference), 043B (rollback procedure runbooks link to). Task 043 (combined) is superseded.

## Scope
Included: eight product runbooks, backup inclusion + restore drill, support-diagnostics pointers (role-restricted + audited).
Excluded: hosting/env (043A), rollout/rollback mechanics (043B), backend Plan A runbooks (referenced, not rewritten).

## Instructions
1. Add `docs/runbooks/` entries (one page each, same template: symptoms → triage queries → mitigation → escalation → postmortem link): `auth-outage.md`, `cdn-outage.md`, `sse-outage.md` (fallback-polling verification), `frontend-deploy-failure.md`, `notification-backlog.md`, `contract-drift.md`, `upload-surge-failure.md`, `review-backlog-surge.md`. Each names the dashboard/monitor (038/043A), the diagnostics view (036), and the rollback link (043B) where applicable.
2. Extend backup docs/job to include new durable entities: tenant users, preferences, notifications, activity events, memberships, voice preview jobs, extended project metadata; record retention + restore priority in `docs/backup.md`.
3. Run a restore drill on staging (new tables only OK): restore → verify counts + spot-read one project/user/notification; record date + result + gaps in `docs/backup.md`.
4. Point support diagnostics at 036 views (workspace/run state, review/notification backlog, queue/DLQ/leases/orphans, provider health, cost anomalies); restate role restriction + audit requirement in each runbook.

## Requirements
- R1: Eight runbooks exist with uniform template and monitor/diagnostic/rollback links.
- R2: Backup covers all seven new-entity groups.
- R3: Restore drill recorded (date + result), not just documented.
- R4: Every runbook states role restriction + audit for privileged actions.
- R5: No backend runbook duplication (link Plan A, do not fork).

## Edge Cases and Error Handling
- Runbook query fails during incident (monitor down) → each runbook includes degraded-mode triage (logs-first path).
- Backup restore partially fails → drill records scope of loss + forward-fix, never silent partial success.
- Review-surge runbook conflicts with 031 UX copy → runbook links the UI path instead of duplicating instructions.

## Security and Safety Requirements
- Runbooks contain example IDs only (synthetic); no real tenant/user IDs, tokens, or signed URLs.
- Restore drill on staging only unless explicitly approved for prod-like env.

## Testing
- Drill-based: restore drill log + runbook tabletop (walk one runbook against staging, record gaps).
- Type: operational verification.

## Validation
```bash
ls docs/runbooks/
grep -l "Escalation" docs/runbooks/*.md
grep -E "tenant users|preferences|notifications|activity|memberships|voice preview|project metadata" docs/backup.md
```

## Completion Criteria
- Eight runbooks + backup inclusion + recorded drill exist; supersedes the ops third of Task 043.

## Traceability
- Plan B §18.1–§18.4. Split from 043; monitors in 038; diagnostics UI in 036; rollback in 043B.
