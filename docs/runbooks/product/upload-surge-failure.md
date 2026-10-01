# "My upload keeps failing at 90%"

**What a user says:** "it fails every time at the end", "it uploaded for an hour
and then said something went wrong", or the quiet one — "it says the upload
finished but the project has no media".

The last variant is the dangerous one. A `complete` that returns success while
the media is not in the catalogue produces an empty workspace, and the user
discovers it after they have walked away.

The mechanism page is [`../upload-surge.md`](../upload-surge.md) — it owns the
resumable-upload protocol, the media-preparation worker and the queue. This page
owns the symptom and the triage order, because a "surge" and a "single failing
upload" are different incidents with the same message.

## Signals

| | |
| --- | --- |
| Monitor / dashboard | `PipelineSuccessLow`, `OrphanRateHigh` and `DLQDepthNonZero` in `deploy/observability/alerts.yml` are the adjacent rules; `queues.json` carries the media queue depths. `BackendMetrics.UploadFunnel` (`upload_funnel_total`, labelled by `stage` in `initiated` / `chunk_received` / `completed` / `failed`) **is** emitted and is on no dashboard. **GAP — no upload-funnel panel and no upload alert.** Live: `curl -fsS http://localhost:8080/metrics \| grep '^upload_'`. **Owner: 038.** |
| Diagnostics view (036) | `/admin` → ops → queues (media queue depth) and errors (dead-letter reasons); `GET /api/v1/admin/diagnostics/orphans` for the "uploaded but never committed" case; `GET /api/v1/admin/diagnostics/leases` for a wedged media-prep worker. |
| Mechanism page | [`../upload-surge.md`](../upload-surge.md) |
| Rollback (043B) | [`../../rollout.md`](../../rollout.md) only if a release changed the media path. An upload failure at the end is a storage or worker fault far more often than a code fault, and a rollback that changes the code does not change either. |

## Symptoms

- `POST /api/v1/projects/{id}/uploads/{uploadId}/parts` returns
  `UPLOAD_INCOMPLETE`, or `STORAGE_UNAVAILABLE`, or times out.
- `POST …/complete` returns 500 after every part succeeded. This is the
  "fails at 90%" report, and the parts are almost certainly fine.
- The project has no media after a "successful" upload. `MEDIA_UNAVAILABLE` on
  the workspace, or an empty segment list.
- `DUPLICATE_MEDIA` — a second upload of the same bytes. The content-hash
  dedupe on `media_assets` is working; the user sees it as a rejection.
- `storage_orphans_total` rising: bytes in the bucket that no metadata row
  describes. `OrphanRateHigh` fires at `increase(...) > 10` over 1 h.
- One user, or everyone. One user is their network or their file; everyone is the
  storage or the worker.

## Five-minute triage

**Step 1: which phase failed?** The upload protocol is three phases with three
different owners, and the error code names the phase. Getting this right first
saves the entire investigation.

```bash
# 1a. The session's own state. uploadId is synthetic; take the real one from the
#     user's network tab, not from the database.
curl -sS -H "Authorization: Bearer $PROJECT_JWT" \
  "https://api.<env>/api/v1/projects/prj_01HZYABCDEFG/uploads/up_01HZYABCDEFG"

# 1b. Storage reachability, from the API's point of view - the same view that
#     decides whether a part is accepted.
curl -sS -o /dev/null -w '%{http_code}\n' \
  -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/status"
```

| Symptom | Phase | Owner | Go to |
| --- | --- | --- | --- |
| `UPLOAD_INCOMPLETE` on `parts` | bytes in flight | client or network | step 2 |
| `STORAGE_UNAVAILABLE` on `parts` | storage | object store | [`../storage-outage.md`](../storage-outage.md) |
| 500 on `complete`, parts all 200 | **the commit** | media-prep worker | step 3 |
| `complete` returns 200, project has no media | **the commit, silently** | projection or orphan | step 4 |
| `DUPLICATE_MEDIA` | dedupe | working as designed | close it with an explanation |
| `MEDIA_CORRUPT` / `ARTIFACT_CHECKSUM_MISMATCH` | verification | the bytes | step 4 |

The "500 on `complete` with every part accepted" row is the one that accounts for
most "fails at 90%" reports. Every byte is already in the bucket; what failed is
the step that turns bytes into a `media_assets` row. Rolling back code does not
help and re-uploading does not either, because the bytes are already there.

**Step 2: is it this upload or all of them?** The funnel counters separate the
two in one query, and the shape of the funnel is the diagnosis.

```bash
# The four funnel stages, in order. A `chunk_received` count far above
# `completed` is uploads that never committed; a `failed` count close to
# `initiated` is the storage refusing the bytes.
curl -fsS http://localhost:8080/metrics | grep '^upload_funnel'

# Session state per tenant, from an admin session.
psql "$DB" -c "
  SELECT status, count(*), min(created_at) AS oldest
  FROM upload_sessions
  WHERE created_at > now() - interval '6 hours'
  GROUP BY 1 ORDER BY 2 DESC;"
```

A pile of `Aborted` or `Expired` sessions is a *client* fault — the client gave
up — and the fix is in the client, not the server. A pile of `Completing` that
never reaches `Completed` is a **wedged worker**, and that is step 3.

**Step 3: the media-preparation worker.** This is the phase that fails at 90%,
so it gets the most attention.

```bash
kubectl -n <ns> get pods -l app.kubernetes.io/component=worker-media-prep
kubectl -n <ns> logs deploy/worker-media-prep --since=15m \
  | grep -oE 'MEDIA_[A-Z_]+|ARTIFACT_[A-Z_]+|STORAGE_[A-Z_]+|LEASING_LEASE_LOST' \
  | sort | uniq -c | sort -rn

# The concurrency limit is deliberately 2 per pod and the scratch PVC is 50Gi.
# Both are asserted by deploy/verify.sh, so a change here is a change somebody
# made on purpose.
kubectl -n <ns> get deploy worker-media-prep \
  -o jsonpath='{.spec.template.spec.containers[0].env[?(@.name=="Media__MaxConcurrentMediaJobs")].value}{"\n"}'
kubectl -n <ns> top pods -l app.kubernetes.io/component=worker-media-prep
```

`Media__MaxConcurrentMediaJobs = 2` and a 50 Gi scratch PVC are the two numbers
that produce a "surge" that is really a **saturation**: at two concurrent media
jobs per pod, three replicas is six, and a burst of large files queues behind
them. A surge runbook that scales pods when the constraint is the scratch PVC gets
a bigger bill and the same user-visible delay.

**Step 4: the "succeeded but empty" case.** Bytes in the bucket, no row.

```bash
# The orphans view, and the raw comparison behind it. OrphanRateHigh fires on
# this number, which is the only reason it is worth reading.
curl -sS -H "Authorization: Bearer $SERVICE_JWT" \
  "https://api.<env>/api/v1/admin/diagnostics/orphans"

# By content hash, for one suspect object. `media_assets` is unique per
# (tenant_id, content_hash), so a hash that resolves to no row is an orphan.
psql "$DB" -c "
  SELECT m.id, m.content_hash, m.status
  FROM media_assets m
  WHERE m.tenant_id = 'de710000-0000-4000-8000-000000000001'
  ORDER BY m.created_at DESC LIMIT 10;"
```

`OrphanObjectReconciler` exists and is run by the maintenance worker. If the
orphans view is non-empty and the maintenance worker is running, the reconciler
will adopt or clean the objects on its own; the incident is the *rate*, not the
count.

## Degraded-mode triage (no dashboard, no metrics)

`upload_funnel_total` is emitted and ungraphed, so the counter is the substitute
for the panel. If `/metrics` is not reachable at all, everything below works with
a log stream and a database session.

```bash
# 1. The four phases, straight from the API's log. The stage names are the
#    funnel's own labels, so this is the funnel without the metrics pipeline.
kubectl -n <ns> logs deploy/dubbing-api --since=15m \
  | grep -oE 'upload\.(initiated|chunk_received|completed|failed)' \
  | sort | uniq -c

# 2. Storage, from inside the API pod. A storage fault that the API's health
#    check does not cover is a real failure mode: readiness covers the database
#    and the broker, and the bucket is neither.
kubectl -n <ns> exec deploy/dubbing-api -- sh -c 'nc -z -w3 <storage-endpoint> 9000 && echo "storage reachable"'

# 3. The wedged-worker signature: sessions stuck in a non-terminal state, with
#    an age that exceeds any plausible media job.
psql "$DB" -c "
  SELECT status, count(*), min(created_at) AS oldest
  FROM upload_sessions
  WHERE status NOT IN ('Completed','Aborted','Expired')
    AND created_at < now() - interval '30 minutes'
  GROUP BY 1;"

# 4. Scratch space. A full PVC fails the *commit*, which is exactly the
#    "fails at 90%" signature, and it does not look like a storage outage in any
#    dashboard.
kubectl -n <ns> exec deploy/worker-media-prep -- df -h /scratch 2>/dev/null \
  || kubectl -n <ns> get pvc
```

Step 4 is the one that catches the incident nobody expected. The media-prep
worker writes transcode scratch to a 50 Gi PVC. When it fills, the job fails at
the **last** step — after the bytes, after the probe — and every symptom above the
worker looks healthy. The dashboard that would show it is the PVC's, and the PVC's
dashboard is `kubectl top`.

## Mitigation

**Storage unreachable:** [`../storage-outage.md`](../storage-outage.md). Do not
retry harder; `STORAGE_UNAVAILABLE` is a dependency fault and a client retry loop
against a down bucket makes the recovery slower for everyone.

**The commit step is failing and the worker is wedged:** restart the
media-preparation pods. The sessions are in the database and the bytes are in the
bucket, so a restart resumes the commit rather than losing anything.

```bash
kubectl -n <ns> rollout restart deploy/worker-media-prep
kubectl -n <ns> rollout status deploy/worker-media-prep --timeout=180s
```

**The scratch PVC is full:** that is a capacity fix, not a restart. Raise the PVC
size or clear scratch. Do not delete a PVC with live jobs on it — the jobs hold
leases and the deletion is exactly how an orphan run is created
([`../orphan.md`](../orphan.md)).

**A genuine surge (many tenants, many concurrent large files):** scale
media-preparation. The limit is `Media__MaxConcurrentMediaJobs = 2` per pod and
it is deliberately low; three replicas is six concurrent jobs, and a burst of
large files queues behind them. Scale the pods *or* raise the limit, and record
the request. Note the trade: each media-prep pod is CPU-heavy and a GPU node is
not involved, so the cost is ordinary compute — but the scratch PVC per pod is
50 Gi, and scaling pods without capacity behind the PVC moves the failure from
the queue to step 4.

**"Completed but the project has no media":** check the orphans view before
anything else. If the bytes are there and the row is not, the reconciler adopts
them. If the bytes are gone, the upload never committed and the user must re-upload
— say so plainly rather than leaving them to discover it.

**Never:** delete an `upload_sessions` row, or mark a session `Completed` by hand,
to clear a stuck upload. The session is the resumable state; a session that
disagrees with reality is how a resume resumes nothing and the user re-uploads
four hours of audio a second time. If a session must be abandoned, use
`POST …/uploads/{uploadId}/abort` so the lifecycle stays honest.

## Escalation

- **L1 (0–15 min):** step 1. `DUPLICATE_MEDIA` and a single failed `parts` call
  are not incidents; close them with the code and the phase.
- **L2 (1 h):** the commit step is failing, or `OrphanRateHigh` is firing. Page
  platform. The orphans view and the DLQ are the two things to bring to the call.
- **L3 (4 h):** storage is down, or a release changed the media path. The first is
  [`../storage-outage.md`](../storage-outage.md); the second is
  [`frontend-deploy-failure.md`](frontend-deploy-failure.md) with the API's
  ReplicaSet instead of the frontend's.

## Postmortem trigger

Any of:

- an upload incident lasting longer than 30 minutes, or a surge in which the
  media queue exceeded its SLO target with the peak recorded;
- a `Media__MaxConcurrentMediaJobs` change, or a PVC resize, made during an
  incident rather than through the normal change path;
- a session marked terminal outside the protocol, or a session row deleted;
- orphan growth above the `OrphanRateHigh` threshold not explained by a known
  cause;
- an upload reported as completed with no `media_assets` row, however briefly —
  it means the protocol has a path that reports success without committing, and
  that is a correctness defect in the product's most destructive operation.

## Access and audit

- `/admin` ops panels — queues, errors, leases, orphans — require **`Service` or
  `TenantAdmin`** (`RequireTenantAdmin`; anonymous 401, `ProjectViewer` 403 —
  `docs/operations/support-access.md`). Step 1 uses a **project Viewer** token
  for the session read, because that is the caller's own data; the admin panels
  are a different privilege and a different token.
- `POST …/uploads/{uploadId}/abort` deletes a resumable session. It is a
  privileged, user-affecting action and it must be attributable in `audit_events`
  with the upload id and the reason. Aborting to "unstick" an upload is a
  decision with a cost: the user re-uploads.
- Scaling media-preparation, resizing the scratch PVC and restarting the worker
  are all privileged production changes. Record the actor and the reason for
  each; a scaling change made at 3 a.m. with no record is indistinguishable from
  one made deliberately.
- Media bytes are user content, and an object key carries the tenant id. Do not
  paste object keys, presigned URLs, session tokens, cookies, or bucket names with
  tenant prefixes into a ticket: a presigned URL **is** the credential, and it
  works for whoever holds it until it expires. Use the content hash, the state,
  the code and the timestamp instead. The `prj_01HZYABCDEFG` and `up_01HZYABCDEFG`
  ids above are synthetic and the endpoints are `CHANGE_ME` placeholders.
