# Nobody can log in

**What a user says:** "the login page just spins", "it says my password is wrong
and it is not", or — the one that pages you at 3 a.m. — "I keep getting signed
out". A third variant is quieter and worse: the login *succeeds* and the next
page load bounces to `/logged-out`.

The mechanism page is [`../auth-outage.md`](../auth-outage.md). It owns why
sessions fail. This page owns the first ten minutes, and the one question that
decides everything else: **is anyone able to log in at all, or is this one
tenant, one user, or one browser?**

## Signals

| | |
| --- | --- |
| Monitor / dashboard | **GAP — no auth alert and no auth dashboard.** `deploy/observability/alerts.yml` has seven rules (DLQ depth, lease recovery, provider error, API p95, orphan rate, export success, pipeline success) and none of them is about authentication; `docs/observability/slos.md` has no auth SLO. The nearest live signals are the `ApiP95LatencyHigh` rule and the HTTP 401/5xx rates on the pipeline dashboard. **Owner: 038.** Until it exists, the `curl` in triage is the check, not the dashboard. |
| Diagnostics view (036) | [`../../operations/support-access.md`](../../operations/support-access.md) for the access policy. There is **no** auth section in `/admin` — the seven sections are tenants, users, health, usage, retention-audit, flags, ops. A suspended or disabled user is visible on the tenants/users panels; a *token* problem is not visible anywhere. |
| Mechanism page | [`../auth-outage.md`](../auth-outage.md) |
| Rollback (043B) | [`../../rollout.md`](../../rollout.md) §1–§2 for a release that broke login; [`../rollback.md`](../rollback.md) for the decision table. Neither applies to a credential or dependency fault. |

## Symptoms

- `POST /api/v1/auth/login` returns **401 `INVALID_CREDENTIALS`** for a password
  that is correct, or **500**.
- `POST /api/v1/auth/refresh` returns **401 `TOKEN_EXPIRED`** or **401
  `TOKEN_REUSED`** in a burst, across many tenants at once.
- Users are signed out between page loads. The frontend's session bootstrap is
  failing and any 401 surfaces as a redirect to `/logged-out` — so a *single*
  failing endpoint anywhere can look like a total auth outage to the user.
- A burst of `TOKEN_REUSED`. That code fires when a refresh token is presented
  twice, which is what a **replay** looks like and what a **retry storm** also
  looks like. Those two have opposite responses, so do not skip the check.
- One tenant only, or one user only. Both are common and neither is an outage.

## Five-minute triage

**Step 1 decides the incident, and it is one call.** Do not read logs first.

```bash
# Synthetic ids only. $ADMIN_JWT must be a Service or TenantAdmin token; a
# ProjectViewer gets 403 here and that is the expected answer, not a fault.
curl -sS -o /dev/null -w '%{http_code}\n' \
  -X POST "https://api.<env>/api/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"email":"probe@drill.invalid","password":"CHANGE_ME"}'
```

Read the answer as a class, not a code:

| Answer | Class | Go to |
| --- | --- | --- |
| **401** `INVALID_CREDENTIALS` | auth is *working* and rejecting a bad password | step 2 — the users' real logins are the question, not this call |
| **401** anything else (`TOKEN_*`, `UNAUTHORIZED`) on a *login* | the token path is broken on requests that should not use it | step 3 |
| **500** | the server or a dependency is failing | step 3 |
| connection refused / TLS error / timeout | **not an auth incident at all** | [`frontend-deploy-failure.md`](frontend-deploy-failure.md) or [`cdn-outage.md`](cdn-outage.md) |
| **401 with an `INVALID_CREDENTIALS` you did not send** | the request never reached the auth handler | step 3, and check the ingress |

**A 401 on a deliberately wrong password is the healthy answer.** It is on the
page because the failure mode people actually call about — "it says my password
is wrong and it is not" — produces exactly this response, and the only way to
tell the two apart is to make a request whose correct answer you already know.

**Step 2: is it everyone or one tenant?**

```bash
# Unread-count is the cheapest authenticated call in the product: no body, no
# database work beyond a count, and it 401s if the session path is broken.
curl -sS -o /dev/null -w '%{http_code}\n' \
  -H "Authorization: Bearer $USER_JWT" \
  "https://api.<env>/api/v1/notifications/unread-count"
```

`200` for your token and `500` for a user's is the split that matters. One
tenant is usually a **suspended or disabled user** (`USER_DISABLED`) or a bad
`external_subject` mapping; check `/admin` → users for that tenant before
anything else.

**Step 3: `TOKEN_REUSED` specifically.** This is the one place where the
distinguishing check is cheap and the wrong answer is a security incident.

```bash
# The replay-vs-retry discriminator: one client failing to reuse a token, or many.
kubectl -n <ns> logs deploy/api --since=15m \
  | grep -c 'TOKEN_REUSED'
```

One or two, from one user, right after a deploy: a **stale client** holding a
token the server has rotated. Many, from many clients, at once: a **retry storm**
in the frontend, and the fix is in the client. Many, from a *single* source: stop
and treat it as a replay until proven otherwise — see the mechanism page.

## Degraded-mode triage (no dashboard, no metrics)

The auth dashboard does not exist, so this is not a degraded path — it is the
only path. It needs nothing but pod logs and the API's own error envelope.

```bash
# 1. Does the login handler run at all? A 500 with no auth log line means the
#    request died in middleware, which is a different incident.
kubectl -n <ns> logs deploy/api --since=10m | grep -c 'auth.login'

# 2. Classify every auth error the API emitted, by code. The codes are the
#    public ErrorCodes catalog, so this needs no dashboard vocabulary.
kubectl -n <ns> logs deploy/api --since=10m \
  | grep -oE 'INVALID_CREDENTIALS|TOKEN_EXPIRED|TOKEN_REUSED|USER_DISABLED|UNAUTHORIZED' \
  | sort | uniq -c | sort -rn

# 3. Did the API restart recently? A restart between issue and validation
#    invalidates every session, which is an outage with a cause and an ETA.
kubectl -n <ns> get pods -l app.kubernetes.io/component=api \
  -o jsonpath='{range .items[*]}{.metadata.name}{"\t"}{.status.containerStatuses[0].restartCount}{"\t"}{.status.containerStatuses[0].lastState.terminated.reason}{"\n"}{end}'
```

Step 3 has caught more "sudden mass logout" reports than the metric ever has. A
rolling restart with no configuration change signs out every user in the cluster,
and the dashboard shows a flat 401 rate with no explanation.

## Mitigation

**Users signed out, no `TOKEN_REUSED`:** a restart, a signing-key rotation, or
an expired refresh window. Check step 3 first, then
[`../../security/secret-rotation.md`](../../security/secret-rotation.md) — a key
rotation is a legitimate cause and the mitigation is to finish the dual-support
window, not to reissue tokens.

**`TOKEN_REUSED` burst from many clients:** the frontend is retrying a refresh
that already succeeded. Rolling the API back does not help, because the client is
the thing looping. Do **not** disable refresh to stop it — that signs everyone
out and converts a partial fault into a total one.

**`INVALID_CREDENTIALS` for correct passwords, one tenant:** look at that tenant's
`tenant_users.status`. `Disabled` is a deliberate state, not a fault, and the fix
is to ask the tenant's admin rather than to re-enable it yourself.

**`500` on login:** this is a dependency failure wearing an auth costume. Go to
[`../db-failover.md`](../db-failover.md) if PostgreSQL readiness is failing, or
[`../provider-outage.md`](../provider-outage.md) if the identity provider is. The
login handler writes to `refresh_sessions`, so a database problem surfaces here
first and gets diagnosed as auth.

**Never:** widen the token lifetime to stop sign-outs, disable the refresh-token
reuse check to stop `TOKEN_REUSED`, or grant yourself an admin role to get past a
403. All three turn a recoverable incident into a security one.

## Escalation

- **L1 (0–15 min):** step 1 only. One user with a healthy step-1 answer is a
  client or credential problem — close it with an explanation, not an escalation.
- **L2 (1 h):** `500` on login, or a `TOKEN_REUSED` burst, or the whole tenant
  affected. Page platform. If `GET /health/ready` is failing, hand over to
  [`../db-failover.md`](../db-failover.md) — the auth surface is a symptom, not
  the cause.
- **L3 (4 h):** a suspected token replay from a single source, or any sign of
  cross-tenant authentication. This is a security incident, not an availability
  one: follow [`../escalation.md`](../escalation.md) and preserve the audit trail
  before rotating anything — a rotation destroys the evidence.

## Postmortem trigger

Any of:

- an auth outage longer than 30 minutes;
- a signing-key rotation or an API restart that signed out users without being
  declared as an auth-affecting change;
- `TOKEN_REUSED` resolved as a client retry storm, with the client fix filed;
- a `500` on login traced to a dependency and misdiagnosed as auth for more than
  one escalation window;
- any privileged action taken from this page whose `audit_events` row is missing
  or unattributable.

## Access and audit

- The admin panels that answer "is this user disabled" — `/admin` → tenants and
  users — require **`Service` or `TenantAdmin`**, enforced by the
  `RequireTenantAdmin` policy: an anonymous caller gets **401** and a
  `ProjectViewer` gets **403** (see
  [`../../operations/support-access.md`](../../operations/support-access.md)). A
  `ProjectViewer` 403 on `/admin` during an incident is the policy working, not a
  second fault to work around.
- Do **not** mint a long-lived admin token to get in. Escalate for a scoped
  grant; a shared admin session is an unattributable session and every action
  taken under it is unattributable in `audit_events`.
- Any re-enablement of a user, any role grant, any signing-key rotation is
  privileged and must land in `audit_events` with a named actor. If you cannot
  name the actor it will be recorded against, do not perform the action.
- No tokens, cookies, connection strings or signed URLs from this page go into a
  ticket. The ids above are synthetic (`probe@drill.invalid`, `CHANGE_ME`).
