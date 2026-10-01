# Product incident runbooks

Task 043C. The **product** half of the runbook set: what a user reports, where
the responder looks first, and what to do in the first fifteen minutes.

There are two sets and the split is deliberate, so it is stated once here rather
than repeated as a caveat in eight files:

| | This directory | [`../`](../index.md) |
| --- | --- | --- |
| Owns | the **symptom a user reports**, the first discriminating check, the product-level mitigation | the **mechanism** behind it: sessions, the CDN, the stream, the deploy, the queue, the contract, the pipeline |
| Audience | on-call, support, and whoever the user calls | whoever is going to change a component |
| Rule | never restate a mechanism; link it | never restate a product symptom; link it |

A runbook that exists in both places is a runbook that will be forked. The
mechanism pages predate this directory and are not edited here; each product page
links the one page that owns its mechanism and stops there.

## The eight, by what a user says

| What a user says | Runbook | Mechanism page (Plan A) |
| --- | --- | --- |
| "Nobody can log in." / "I keep getting signed out." | [`auth-outage.md`](auth-outage.md) | [`../auth-outage.md`](../auth-outage.md) |
| "The site is blank, or it is serving yesterday's build." | [`cdn-outage.md`](cdn-outage.md) | [`../cdn-cache-poison.md`](../cdn-cache-poison.md) |
| "The progress bar is frozen." | [`sse-outage.md`](sse-outage.md) | [`../sse-degraded.md`](../sse-degraded.md) |
| "The site is broken after the deploy." | [`frontend-deploy-failure.md`](frontend-deploy-failure.md) | [`../deploy-failed.md`](../deploy-failed.md) |
| "I never got the notification." | [`notification-backlog.md`](notification-backlog.md) | [`../notification-backlog.md`](../notification-backlog.md) |
| "The page says the field doesn't exist." | [`contract-drift.md`](contract-drift.md) | [`../contract-drift.md`](../contract-drift.md) |
| "My upload keeps failing at 90%." | [`upload-surge-failure.md`](upload-surge-failure.md) | [`../upload-surge.md`](../upload-surge.md) |
| "My project has been waiting for review for days." | [`review-backlog-surge.md`](review-backlog-surge.md) | [`../review-surge.md`](../review-surge.md) |

Three names appear on both sides. That is intended: the product page answers
"what does the user say and what do I check first", the mechanism page answers
"why is the component doing that". Neither is a copy of the other, and the
product page says so where the two are most easily confused.

## The shape every page here has

Fixed order, same as the mechanism pages, plus two sections they do not have:

```text
# Title
What the user reports, and which page owns the mechanism.
## Signals          monitor/dashboard (038) · diagnostics view (036) ·
                    mechanism page · rollback (043B) · the gap, if there is one
## Symptoms         what the user and the responder both see
## Five-minute triage   the check that DISCRIMINATES between causes
## Degraded-mode triage   what to do when the monitor/dashboard is down too
## Mitigation        the product-level action, and the thing never to do
## Escalation        L1/L2/L3 with the SLA and the owner
## Postmortem trigger    a list of conditions, not a judgement call
## Access and audit  the role restriction and the audit trail, restated
```

**`## Degraded-mode triage` is not optional.** A monitor that fires is the
monitoring system working; a monitor that is *down* during the incident it should
be watching is the ordinary case, not the exotic one, and a responder with nothing
but a runbook and no dashboard still has to be able to make progress. Every page
here therefore has a logs-first path that needs no metrics pipeline.

**`## Access and audit` is on every page** because the mitigations these pages
describe are privileged: a token revocation, a DLQ redrive, a feature-flag flip, a
rollback. `docs/operations/support-access.md` is the policy; the page names the
role and the audit event so the responder does not have to go looking.

## Access, once, for all eight

Admin diagnostics and every privileged mitigation in this directory are
`Service` or `TenantAdmin` only, enforced by the `RequireTenantAdmin` policy
(`docs/operations/support-access.md`). A `ProjectViewer` gets 403 and an
anonymous caller gets 401 — those are the expected answers, not a bug to work
around.

- Do not mint a long-lived admin token to "get in" during an incident. Escalate
  for a scoped grant.
- Every privileged action taken from these pages — DLQ redrive or discard, flag
  apply, token revocation, `kubectl rollout undo`, a forced DLQ drain — must be
  attributable in `audit_events`. If you cannot name the actor that will be
  recorded, do not perform the action under a shared or borrowed session.
- Nothing in this directory may be pasted into a ticket: no tokens, no
  connection strings, no signed URLs. Example ids are synthetic and start with
  `prj_01`, `usr_01`, `ntf_01`, `ten_01`.

## Tabletop record

Per release, walk one page end-to-end as a tabletop: read every command, predict
its output, check the prediction against staging. `tools/product-runbooks.test.mjs`
covers the structural half (the template, the links, the role-and-audit
restatement, the absence of literal credentials). The *semantic* half — a command
that no longer does what the page says — can only be found by running it, which
is why the walk is on the calendar rather than in the gate.

| Date | Page walked | Verdict | Gaps found |
| --- | --- | --- | --- |
| 2026-10-01 | [`sse-outage.md`](sse-outage.md) | **PASS with 3 gaps** | See the row-by-row result below. |

### 2026-10-01 — `sse-outage.md` tabletop

Walked against a local stack (compose `fast` profile + a running API), not
against a shared staging environment; see the scope note at the end of this
section.

| Step | Predicted | Observed | Verdict |
| --- | --- | --- | --- |
| `GET /health/ready` on the API | 200 while SSE is degraded | 200 | as documented — readiness does not cover the stream, by design |
| `curl -N .../progress/stream?access_token=` with a valid token | opens, then one envelope every 2 s | opens; envelopes on the 2 s cadence | as documented |
| stop the API container, re-run the same `curl` | connection refused, exit 7 | exit 7 | as documented |
| `GET .../progress` while the stream is refused | 200 with the same counts the stream was carrying | 200, counts identical | **the fallback is real** — this is the assertion that matters |
| `sse.payload_dropped_total` in `/metrics` | present after a drop | **absent** | gap 1 |
| `sse_reconnects` on the pipeline dashboard | a panel | **no such panel exists** | gap 2 |
| `replayTruncated` response header on a resume past the 100-event window | set | set, and the client falls back to polling | as documented |

**Gap 1 — the drop counter is named in the code and in no dashboard.**
`ProcessingController` documents `sse.payload_dropped_total` (the XML doc on
`StreamProgress`) and nothing in `deploy/observability/dashboards/` reads it. A
dropped payload is silent by construction: the stream stays open, the event
arrives, and the frame is incomplete. Owner: 038. Until it is graphed, the only
evidence of a drop is the event itself, so treat a client report of "the
percentage went backwards" as a drop until proven otherwise.

**Gap 2 — `sse_connections` and `sse_reconnects` do not exist.** The mechanism
page [`../sse-degraded.md`](../sse-degraded.md) names both as the discriminating
check ("`sse_connections` on the API dashboard is well below its baseline while
`sse_reconnects` is above it"), and the metrics are not emitted. The check as
written cannot be run. The substitute that *was* verified is the direct `curl` in
step 2 plus the pod-level log count, and both are in the product page's triage
section. Owner: 038, and the mechanism page's triage should be amended in the
same change so the two do not disagree.

**Gap 3 — no alert fires for a dead stream.** `deploy/observability/alerts.yml`
has seven rules and none of them is about SSE. The stream degrading to polling
is an SLO violation (progress is no longer live) with no page attached, which is
exactly the failure mode that survives for weeks because the product keeps
working. Owner: 038.

**Scope note.** Staging was not reachable from this environment, so the walk used
a local compose stack. That covers the HTTP behaviour, the headers, the metrics
endpoint and the client's fallback — which is what this page is about — and does
**not** cover ingress annotation effects (a 60 s `proxy-read-timeout` closing an
idle stream is the mechanism page's most likely cause and it cannot be reproduced
without a real ingress controller). That half stays unverified and is recorded
as unverified rather than assumed.

## Related

- [`../index.md`](../index.md) — the mechanism pages, and the symptom index
- [`../../operations/support-access.md`](../../operations/support-access.md) — the
  diagnostics access policy this directory's mitigations assume
- [`../../rollout.md`](../../rollout.md) — the release procedure, and the
  `--post-deploy` verification a deploy failure has already run
- [`../../backup.md`](../../backup.md) — backup coverage for the new tables, and
  the recorded restore drill
- [`../escalation.md`](../escalation.md) — who to page, and when
