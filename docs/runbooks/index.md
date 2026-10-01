# Operations index

Every runbook, and the topology and rollout documents they depend on. Start
here rather than from a search: the symptom-to-runbook mapping is the reason this
page exists, and a search for "upload" finds six documents.

## Start with these two

| | |
| --- | --- |
| [`../topology.md`](../topology.md) | What may talk to what, and which policy enforces each hop. Read before diagnosing anything involving a network path, because most "the API is down" reports are a policy denying what someone assumed was allowed. |
| [`../rollout.md`](../rollout.md) | The release procedure: migration-first, backward-compatible, flag-gated, verify, then contract. |

## By symptom

| What you see | Runbook |
| --- | --- |
| Nobody can log in; users signed out | [`auth-outage.md`](auth-outage.md) |
| Someone sees another tenant's data, or the wrong build | [`cdn-cache-poison.md`](cdn-cache-poison.md) — **data exposure: page security first** |
| Progress bars stutter; notifications late via SSE | [`sse-degraded.md`](sse-degraded.md) |
| A rollout did not complete, or verification failed | [`deploy-failed.md`](deploy-failed.md) |
| Notifications not arriving; backlog growing | [`notification-backlog.md`](notification-backlog.md) |
| Client and server disagree about the API | [`contract-drift.md`](contract-drift.md) |
| Uploads failing, or media queues deep | [`upload-surge.md`](upload-surge.md) |
| Finished runs waiting on people | [`review-surge.md`](review-surge.md) |
| You need to undo a release | [`rollback.md`](rollback.md) — start with the decision table |
| A database is unavailable or being failed over | [`db-failover.md`](db-failover.md) |
| Data loss, or "what do we restore from" | [`backup-restore.md`](backup-restore.md) |
| Object storage is unreachable | [`storage-outage.md`](storage-outage.md) |
| A message provider is failing | [`provider-outage.md`](provider-outage.md) |
| Messages are stuck in the DLQ | [`dlq.md`](dlq.md) |
| A worker's lease is stuck | [`lease.md`](lease.md) |
| An orphaned run or pod | [`orphan.md`](orphan.md) |
| Cost is unexpected, or a quota is exhausted | [`quota-cost.md`](quota-cost.md) |
| Too many reviews pending | [`review-backlog.md`](review-backlog.md) |
| **Who do I page, and when** | [`escalation.md`](escalation.md) — read this one first on a new rotation |

## The product runbooks

[`product/index.md`](product/index.md) is a second set, and the split is worth
knowing before you open either. **These pages are indexed by what a *user says*;
the pages above are indexed by what a *component* is doing.** Same incidents, two
entry points, and neither is a copy of the other.

Use the product page first if a user has called. Use the mechanism page first if
you already know the component. Each links the other, and a page that restates
the other is a bug — `tools/product-runbooks.test.mjs` fails on one.

| What a user says | Start here | Then |
| --- | --- | --- |
| "Nobody can log in." | [`product/auth-outage.md`](product/auth-outage.md) | [`auth-outage.md`](auth-outage.md) |
| "The site is blank / serving yesterday's build." | [`product/cdn-outage.md`](product/cdn-outage.md) | [`cdn-cache-poison.md`](cdn-cache-poison.md) |
| "The progress bar is frozen." | [`product/sse-outage.md`](product/sse-outage.md) | [`sse-degraded.md`](sse-degraded.md) |
| "The site is broken after the deploy." | [`product/frontend-deploy-failure.md`](product/frontend-deploy-failure.md) | [`deploy-failed.md`](deploy-failed.md) |
| "I never got the notification." | [`product/notification-backlog.md`](product/notification-backlog.md) | [`notification-backlog.md`](notification-backlog.md) |
| "The page says the field doesn't exist." | [`product/contract-drift.md`](product/contract-drift.md) | [`contract-drift.md`](contract-drift.md) |
| "My upload keeps failing at 90%." | [`product/upload-surge-failure.md`](product/upload-surge-failure.md) | [`upload-surge.md`](upload-surge.md) |
| "My project has been waiting for review for days." | [`product/review-backlog-surge.md`](product/review-backlog-surge.md) | [`review-surge.md`](review-surge.md) |

The product pages carry two sections the pages above do not: a
**degraded-mode triage** path that works with no dashboard and no metrics
pipeline, and an explicit **access-and-audit** restatement, because their
mitigations are privileged. Several name a monitoring gap that does not exist yet
— that is deliberate, and the owner is on the page.

## The shape every runbook shares

Each has the same five sections, and the order is deliberate: **symptoms →
five-minute triage → mitigation → escalation → postmortem trigger.**

The triage section is capped at five minutes on purpose. A runbook that takes
twenty minutes to diagnose is a runbook that gets skipped, and the person who
skips it improvises. Each triage section starts with the check that *discriminates*
between causes rather than the check that is easiest to run, and each one says what
the answer means.

The **postmortem trigger** is a list of conditions, not a judgement call. "Was
this bad enough for a postmortem?" is a question decided at 2 a.m. by the person
with the least context, and it is answered "no" too often. A trigger is met → a
postmortem happens.

## The three properties most runbooks assume

Stated once here rather than in each file:

1. **Liveness is process-only; readiness is everything else.** A liveness probe
   that depends on a database restarts every pod during a blip and turns a
   degradation into an outage. Readiness includes **migration currency**: a pod
   whose build is ahead of the schema does not take traffic.
2. **Migrations are additive only.** A rollback never reverses a migration. Old
   code tolerates new nullable columns for at least one release window, so
   rolling back code never requires rolling back the schema. After a contract
   phase it does — which is the one case where a rollback is *refused*.
3. **`GET /version` answers "which build is this?"** during an incident. It is
   anonymous precisely so a kubelet, a CDN, or a rollback script can ask it when
   nobody has a session.

## Verifying the runbooks themselves

A runbook is documentation until somebody has used it. Per release, walk one
runbook end-to-end as a **tabletop** — read each command, predict its output, and
check the prediction against the real thing. Record it in
[`../operations/chaos-log.md`](../operations/chaos-log.md) and file the
discrepancies as issues.

The purpose is to find the commands that do not work, and there is always at
least one: a dashboard that was renamed, a query that assumed a column that was
dropped in a contract migration, a `kubectl` flag from a version nobody runs any
more. A runbook with a wrong command is worse than no runbook, because it
consumes the responder's time and produces a confident wrong answer.

## Related

- [`../operations/ha-topology.md`](../operations/ha-topology.md) — failover and
  the RTOs
- [`../operations/chaos-log.md`](../operations/chaos-log.md) — chaos results
- [`../operations/migration-compat.md`](../operations/migration-compat.md) — the
  expand/contract check
- [`../operations/rotation-drill-log.md`](../operations/rotation-drill-log.md) —
  secret rotation drills
- [`../observability/slos.md`](../observability/slos.md) — the targets
- [`../security/secret-rotation.md`](../security/secret-rotation.md) — the
  dual-support window
- [`../dr/backup-restore.md`](../dr/backup-restore.md) — the restore mechanisms
- [`../../deploy/README.md`](../../deploy/README.md) — deploy order and the six
  launch gates
- [`../ci-branch-protection.md`](../ci-branch-protection.md) — which gates block a
  merge, and what each can emit
