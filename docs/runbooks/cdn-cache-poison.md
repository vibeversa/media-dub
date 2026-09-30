# CDN cache poisoning

The CDN is serving a response that does not belong to the requester: another
tenant's data, another user's HTML, or a stale document that points at assets
that no longer exist.

**Treat every confirmed instance as a data-exposure incident until proven
otherwise.** The classification question — is it a security incident or an
availability one — is answered by the first triage step, and answering it later
means the wrong people were paged.

## Symptoms

- A user sees another tenant's project name, project id, or user content in the
  SPA shell. The document is public HTML, so this is only possible through a
  poisoned *response body*, not a cache-key collision on the document itself.
- A user loads the app and it is visibly the wrong build: `VITE_APP_VERSION` in
  the footer does not match `/version.json`.
- The SPA fails to load with a chunk-load error after a deploy, and a hard
  refresh fixes it for one user but not another.
- `Vary` or `Cache-Control` appears on a response where it should not, or a
  `Set-Cookie` appears on a cached response.
- Cache hit ratio spikes at deploy time and asset 404s spike immediately after.

## Five-minute triage

```bash
# 1. Is anyone actually seeing another tenant's data? This decides the severity
#    and the paging, and it is one question to one person.
#    Ask for the page they saw and the page they requested.

# 2. What is actually cached, and with which headers?
curl -sS -D - -o /dev/null "https://<cdn-host>/" 
curl -sS -D - -o /dev/null "https://<cdn-host>/version.json"

# 3. Is the origin itself serving it, or only the CDN? This is the decisive
#    split: origin-clean means the CDN is at fault; origin-dirty means the origin
#    is and the CDN is faithfully caching it.
curl -sS -D - -o /dev/null -H "Cache-Control: no-cache" "https://<static-origin>/"
```

Step 3 is the one that decides everything. A poisoned cache with a clean origin
is a CDN configuration problem. A poisoned cache with a dirty origin is a
response that was already wrong before anything cached it, and the CDN is the
only reason the damage is shared.

Then check the two headers that make shared caching of this origin wrong:

- **`Set-Cookie` on a cached response.** The static origin sets none
  (`deploy/nginx/default.conf`). If one appears, something upstream is adding it
  and the response must not be stored.
- **`Vary` on anything that is not `Accept-Encoding`.** The origin sets
  `Vary: Accept-Encoding` from gzip. A `Vary: Cookie` or `Vary: Authorization`
  means the edge is keying on a credential it should not be storing.

## Mitigation

**One request with a credential is enough to poison a shared cache.** So:

1. **Purge the affected paths**, not the whole distribution. `/index.html`,
   `/version.json`, and any path a user reported. A full purge under load turns a
   caching bug into an origin overload.
2. **Add the missing `Vary` or strip the `Set-Cookie`** at the origin before
   serving traffic again. Purging without fixing the cause means the next
   request re-poisons it.
3. **Do not roll back the application** for a CDN cache problem. The origin is
   the artefact that is wrong; a redeploy replaces one cache entry with another.
4. If the origin is dirty, fix the origin first and then purge. Purging a dirty
   origin buys minutes and re-poisons immediately.

**The structural check that should have prevented this** is in the gate:
`/api/` returns 404 from the static origin, and no response from it carries a
`Set-Cookie`. Both are asserted against the running image in
`deploy/tests/hosting.test.sh`. If this incident happens with both green, the
poisoning is happening at the CDN and the origin's configuration is not the
cause.

## Escalation

- **L1 (0–15 min)**: confirm or deny cross-tenant exposure with step 1. Purge
  the reported paths. That contains a single-user report.
- **L2 (1 h)**: more than one report, or exposure confirmed. Page the platform
  team. Do **not** purge the whole distribution without L2 — a full purge is an
  origin DoS.
- **L3 (immediately, in parallel)**: confirmed cross-tenant data exposure. This
  is a security incident, not an availability one. Page security. Preserve the
  CDN access logs before they rotate — they are the only record of who received
  the poisoned response.

## Postmortem trigger

Any of:

- cross-tenant data observed by any user, confirmed or not;
- any response with a `Set-Cookie` served from cache;
- a purge of the full distribution, or of more than 20 paths;
- a change to the cache-key policy, the `Vary` set, or the cache classes.

The postmortem must answer one question: **what in the response was
distinguishable between users?** If the answer is "nothing", then the exposure
was of public content and is an availability incident. If the answer is anything
else — a URL, a header, a `Set-Cookie` — it is a security incident, and the
finding is that the key did not include that dimension.
