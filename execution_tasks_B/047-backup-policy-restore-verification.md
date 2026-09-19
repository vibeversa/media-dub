# Task 047 — Backup Policy Execution and Restore Verification

**Required/Optional:** Required
**Complexity:** S

## Goal
Prove backup/restore covers the seven new durable entity groups from Plan B §18.4.

## Context
043C documents backup inclusion and runs a staging drill. This task is the execution/policy layer on top: jobs/config actually include the tables, restore is verified against recorded steps, RPO/RTO and escalation are explicit, and gaps block release. Does not rewrite Plan A backend runbooks or 043C product runbooks.

## Starting State
Depends on 001 (TenantUser/UserPreference/DubbingProject-extensions/Membership tables), 002 (Notification/ActivityEvent tables), 004 (VoicePreviewJob + preview artifact backing rows), 043C (backup docs baseline + prior drill log in `docs/backup.md`). Plan A backup jobs exist but do not yet list new tables.

## Scope
Included: backup job/config update for seven groups, restore verification steps + recorded drill, RPO/RTO statement, failure escalation, release-gate rule.
Excluded: runbook content (stays in 043C), hosting/env (043A), rollout/rollback mechanics (043B), backend Plan A runbooks (referenced only).

## Instructions
1. Update backup jobs/config (location per repo convention, referenced from `docs/backup.md`) to include: tenant users, preferences, notifications, activity events, memberships, voice preview jobs, extended project metadata. One checklist line per group; no group implied.
2. Define restore verification steps in `docs/backup.md`: restore target (staging), count checks per group, one spot-read per group (project/user/notification), success/fail recording fields (date, scope, result, gaps, owner).
3. Record RPO/RTO expectations + failure escalation path in `docs/backup.md`. **Assumption:** Plan §18.4 specifies no RPO/RTO values, job paths, or escalation contacts; values/contacts are team-recorded, not plan-derived.
4. Execute (or re-execute on top of 043C) one restore drill covering all seven groups; record date + result + gaps. Partial success is recorded as failure scope, never silent.
5. Enforce release gate: open backup/restore gaps block release (checklist item in release verification).

## Requirements
- R1: Backup config lists all seven entity groups explicitly.
- R2: Restore drill is recorded (date + result + gaps), not just documented.
- R3: RPO/RTO + escalation are written down. **Assumption:** numeric targets are team-chosen; plan only requires that expectations exist.
- R4: Gaps block release (explicit gate statement).
- R5: No duplication of 043C runbooks or Plan A backend runbooks (link, do not fork).

## Edge Cases and Error Handling
- Restore partially fails → record loss scope + forward-fix; gate stays red.
- New table added later without backup entry → gap item, blocks release until config updated.
- Drill env unavailable → drill is overdue (recorded), not waived.

## Security and Safety Requirements
- Drill on staging only unless explicitly approved; synthetic/example IDs only in docs; no tokens, signed URLs, or real tenant/user IDs.

## Testing
- Operational verification: config grep + drill log review. No unit tests.

## Validation
```bash
grep -E "tenant users|preferences|notifications|activity|memberships|voice preview|project metadata" docs/backup.md
grep -E "RPO|RTO|Escalation" docs/backup.md
ls docs/backup.md
```

## Completion Criteria
- Backup config exists, all seven entity groups included, restore drill recorded, gaps block release.

## Traceability
- Plan B §18.4. Depends on 001, 002, 004, 043C. Extends (does not replace) 043C.
