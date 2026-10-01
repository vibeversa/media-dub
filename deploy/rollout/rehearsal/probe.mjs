// The availability probe (Task 043B rehearsal).
//
// A separate, long-lived pod that polls the API Service's ClusterIP and logs one
// line per FAILED sample. The count of those lines is the downtime measurement,
// which is why the log is sparse: a probe that logs every successful sample
// produces a log nobody reads, and a log nobody reads is not a measurement.
//
// The Service ClusterIP, not a pod IP, is the target on purpose. During a rolling
// update the pods are being replaced; the thing that must not stop answering is
// the Service, and a probe pinned to one pod IP proves only that one pod stayed
// up.
//
// Why a pod and not `kubectl exec` into the API: the API pod is itself being
// replaced by the rollout being measured, so a probe running inside it dies
// mid-measurement and reports the interruption as a gap rather than as downtime.
import { setTimeout as sleep } from 'node:timers/promises';

const url = process.env.AVAILABILITY_URL;
const intervalMs = Number.parseInt(process.env.AVAILABILITY_INTERVAL_MS ?? '300', 10);
const budgetMs = Number.parseInt(process.env.AVAILABILITY_BUDGET_MS ?? '600000', 10);

if (!url) {
  process.stderr.write('AVAILABILITY_URL is required\n');
  process.exit(2);
}

// One banner line, before the first sample. The runner reads the head of this
// pod's log to decide whether the probe is the process that is running, and it
// used to decide by ABSENCE - "no 'listening' line and phase Running". This
// process prints nothing at all for its first 50 samples (25 seconds at the
// rehearsal's 500ms interval), so that check was satisfied by the probe being
// silent rather than by the probe being alive, which is the same defect as the
// `|| echo 0` default in the revision reader: a check that passes because it
// found nothing to complain about. With a banner, "the probe is measuring" is a
// positive assertion, and "the server answered instead" is a fact rather than an
// inference from silence.
process.stdout.write(`START url=${url} intervalMs=${intervalMs} budgetMs=${budgetMs}\n`);

const deadline = Date.now() + budgetMs;
let samples = 0;
let failures = 0;

while (Date.now() < deadline) {
  samples += 1;
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(2000) });
    if (!response.ok) {
      failures += 1;
      process.stdout.write(`FAIL ${new Date().toISOString()} status=${response.status}\n`);
    }
  } catch (error) {
    failures += 1;
    process.stdout.write(`FAIL ${new Date().toISOString()} ${error?.name ?? 'error'}: ${error?.message ?? ''}\n`);
  }
  // A progress line every 50 samples, so the sample COUNT is readable while the
  // probe is still running. The runner reads the log before the pod is deleted,
  // and a log that only ever contains failures cannot say how many opportunities
  // to fail there were - "zero failures" out of zero samples is not a result.
  if (samples % 50 === 0) process.stdout.write(`PROGRESS samples=${samples} failures=${failures}\n`);
  await sleep(intervalMs);
}

process.stdout.write(`SUMMARY samples=${samples} failures=${failures}\n`);
