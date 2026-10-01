# Auth outage

Sessions cannot be established or are being rejected. Either nobody can log in,
or logged-in users are being logged out.

## Symptoms

- `POST /api/v1/auth/login` returns 401 with `INVALID_CREDENTIALS` for correct
  passwords, or 500.
- `POST /api/v1/auth/refresh` returns 401 `TOKEN_EXPIRED` / `TOKEN_REUSED`
  in a burst, from many tenants at once.
- Users report being signed out between page loads. The frontend's session
  bootstrap is failing; a 401 from any endpoint surfaces as a redirect to
  `/logged-out`.
- `auth_failed` / `TOKEN_REUSED` rate on the auth dashboard is well above its
  baseline while login volume is flat.

## Five-minute triage

```bash
# 1. Is the API itself up? Auth failures during a database outage look identical
#    to credential failures from the user's side.
curl -fsS https://api.<env>/health | jq '{status, readiness}'

# 2. What is the error distribution, not just the rate?
kubectl -n <ns> logs deploy/dubbing-api --since=15m | grep -o '"code":"[A-Z_]*"' | sort | uniq -c | sort -rn | head

# 3. Signing key: is the SAME key in every pod and unchanged?
kubectl -n <ns> get secret dubbing-secrets -o jsonpath='{.data.auth-signing-key}' | sha256sum
kubectl -n <ns> rollout history deploy/dubbing-api
```

Step 3 is the one that matters and it is the one people skip. A token signed by
one key and verified by another fails as `INVALID_SIGNATURE`, which the client
surfaces as a generic session error. The signature is the only thing that makes
a stateless JWT work; if pods disagree about it, a login succeeds and the *next*
request fails.

## Mitigation

**If the signing key changed unintentionally** — the most common cause, and it
is a deploy problem rather than an auth problem:

```bash
# All three at once, so every pod presents the same key.
kubectl -n <ns> rollout restart deploy/dubbing-api
kubectl -n <ns> rollout status deploy/dubbing-api
```

That fixes *new* logins. **Existing tokens remain valid** if the old key is still
accepted, and are dead if the key was replaced rather than rotated. See
[`../security/secret-rotation.md`](../security/secret-rotation.md) for the
dual-support window, which exists precisely so this does not sign everyone out.

**If the key must be rotated deliberately**, restore dual support first: the
verifier must accept the old public key until every token minted with it has
expired (access tokens: minutes; refresh tokens: as long as the refresh window).
Then add the new key. Then remove the old one after the longest token lifetime
plus clock skew.

**If it is a database outage**, do not touch auth. `/health/ready` fails, pods
leave the Service, and the 401s are a symptom. Follow
[`db-failover.md`](db-failover.md).

**If it is a brute-force or credential-stuffing pattern**: the per-tenant and
per-IP rate limiter is doing its job and the 401s are the *correct* response. Do
not disable it. Confirm the limiter is the source (429s alongside 401s from the
same source) and let the rate limit stand.

## Escalation

- **L1 (0–15 min)**: keys 1–3 above. If the signing key differs across pods,
  restart them together — that is L1-resolvable.
- **L2 (1 h)**: the key is consistent, `/health` is green, and logins still fail.
  Page the identity owner. A `TOKEN_REUSED` spike with a healthy API is usually
  a client retrying a refresh it already consumed, which is a client bug that
  logs users out.
- **L3 (4 h)**: a key compromise is suspected, or `/health/ready` has been 503
  for over an hour. See [`../security/secret-rotation.md`](../security/secret-rotation.md)
  and [`../security/mtls.md`](../security/mtls.md).

## Postmortem trigger

Any of:

- an unplanned signing-key change, with or without user impact;
- a full sign-out event affecting more than one tenant;
- rate limiting disabled, or a limiter bypassed, in any incident;
- `TOKEN_REUSED` as the dominant auth error for more than 15 minutes.

A key change with no user impact still gets a postmortem: the question is how a
key was rotated in a way that could have signed everyone out, and the answer is
usually that the dual-support window did not exist yet.

## The product runbook for this

[`product/auth-outage.md`](product/auth-outage.md) — or start from
[`product/index.md`](product/index.md), which indexes the set by symptom — is
the same incident indexed
by what a **user says** — "the login page just spins", "it says my password is
wrong and it is not", "I keep getting signed out" — rather than by what the
session machinery is doing. It carries the two-step triage that separates "nobody
can log in" from "one tenant" from "one browser", the logs-first path for when
the metrics stack is down, and the access-and-audit restatement for the
privileged actions here (re-enabling a user, rotating a signing key, granting a
role). It does not restate any of the mechanism on this page; start it from the
user's report and come back here once the class of fault is known.
