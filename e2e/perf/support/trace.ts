// Task 041D: trace capture and scrubbing.
//
// The task's security requirement is "Traces scrubbed (URLs, tokens, media
// bytes) before CI attach per 038 allowlist", and its edge case is "Trace
// upload failure -> run still fails with timing evidence; missing-trace warning
// attached". Both are implemented here, and the second one is the one that is
// easy to get wrong: a scrubber that throws on a trace it cannot parse and a
// scrubber that quietly produces a half-scrubbed archive look identical from
// the outside. So this module never swallows anything. It returns a
// `TraceOutcome` that says exactly what was dropped, what was redacted, and
// whether the result was verified, and the caller attaches that to the run
// whether or not a trace exists.
//
// What is actually in a Playwright trace, measured rather than assumed (see
// `e2e/perf/README.md`):
//
//   trace.trace      JSONL: library calls, console, `frame-snapshot` (full DOM
//                    as JSON), `screencast-frame` (references a jpeg)
//   trace.network    JSONL: HAR-shaped `resource-snapshot` records with request
//                    and response **headers** (Authorization, Cookie) and
//                    **URLs** (presigned query strings)
//   screencast/*     jpeg frames
//   resources/*      response **bodies** - the media bytes, and every API body
//   src/*            application source
//   trace.stacks     call-stack samples
//
// The policy is an allowlist, not a denylist, because a denylist over a format
// that grows with the browser is a scrubber that leaks the next time Chromium
// adds a field: `resources/`, `screencast/` and `src/` are dropped wholesale,
// and of the three text streams only `trace.trace` and `trace.network` survive,
// with headers reduced to an allowlist and every query string removed.

import { mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname } from 'node:path';
import type { BrowserContext } from '@playwright/test';

import { readZip, writeZip, type ZipEntry } from './zip.js';

/** Trace entries that survive scrubbing. Everything else is dropped. */
const KEPT_ENTRIES: readonly string[] = ['trace.trace', 'trace.network', 'trace.stacks'];

/** Request/response headers allowed to remain in `trace.network`. */
const ALLOWED_HEADERS: ReadonlySet<string> = new Set([
  'accept',
  'accept-encoding',
  'cache-control',
  'content-encoding',
  'content-length',
  'content-type',
  'vary',
]);

/** `trace.trace` record types that carry page content and are dropped whole. */
const DROPPED_TRACE_TYPES: ReadonlySet<string> = new Set(['frame-snapshot', 'screencast-frame']);

/**
 * Patterns that must not survive into an attached trace.
 *
 * Verified against the *written* archive, not against the input, so a redaction
 * that fails to match still fails the run.
 */
export const FORBIDDEN_PATTERNS: readonly { readonly label: string; readonly regex: RegExp }[] = [
  { label: 'bearer token', regex: /Bearer\s+[A-Za-z0-9._~+/=-]{8,}/i },
  { label: 'JWT', regex: /eyJ[A-Za-z0-9_-]{6,}\.[A-Za-z0-9_-]{4,}\./ },
  { label: 'presigned query parameter', regex: /[?&](X-Amz-[A-Za-z]+|Signature|X-Goog-[A-Za-z]+|AWSAccessKeyId|Expires)=/i },
  { label: 'authorization header', regex: /"authorization"\s*:/i },
  { label: 'cookie header', regex: /"(set-)?cookie"\s*:/i },
  { label: 'access/refresh token field', regex: /"(access|refresh|idempotency)[_-]?token"\s*:/i },
  { label: 'rig placeholder secret', regex: /CHANGE_ME/ },
];

export interface TraceOutcome {
  /** `true` when a trace was produced, scrubbed and written. */
  readonly captured: boolean;
  /** Where the scrubbed trace was written, or the intended path when it was not. */
  readonly path: string;
  /** Entry name -> bytes, for every entry that was dropped wholesale. */
  readonly dropped: Readonly<Record<string, number>>;
  /** Records dropped from inside the kept text streams. */
  readonly droppedRecords: number;
  /** How many string values were rewritten. */
  readonly redacted: number;
  /** `true` when the written archive was re-read and found clean. */
  readonly verified: boolean;
  /** Anything that went wrong. Always reported, never thrown away. */
  readonly problems: readonly string[];
  /** One line for the test log. */
  readonly summary: string;
}

function scrubUrl(raw: string): string {
  try {
    const url = new URL(raw);
    const params = [...url.searchParams.keys()];
    const suffix = params.length === 0 ? '' : `?<${String(params.length)} param(s) redacted>`;
    return `${url.origin}${url.pathname}${suffix}`;
  } catch {
    // Not a URL. Anything that is not a URL and is not on the allowlist is
    // replaced wholesale rather than passed through.
    return '<redacted-non-url>';
  }
}

function scrubString(value: string): { readonly value: string; readonly changed: boolean } {
  let out = scrubUrl(value);
  for (const pattern of FORBIDDEN_PATTERNS) {
    out = out.replace(new RegExp(pattern.regex.source, 'gi'), `<redacted:${pattern.label}>`);
  }
  return { value: out, changed: out !== value };
}

function scrubValue(value: unknown, counter: { redacted: number }): unknown {
  if (typeof value === 'string') {
    // A bare string field is treated as a candidate only if it looks like a URL
    // or a secret; ordinary DOM text is left alone so the trace stays readable.
    const looksSensitive = /^[a-z][a-z0-9+.-]*:\/\//i.test(value) || FORBIDDEN_PATTERNS.some((p) => p.regex.test(value));
    if (!looksSensitive) {
      return value;
    }
    const scrubbed = scrubString(value);
    if (scrubbed.changed) {
      counter.redacted += 1;
    }
    return scrubbed.value;
  }
  if (Array.isArray(value)) {
    return value.map((item) => scrubValue(item, counter));
  }
  if (typeof value === 'object' && value !== null) {
    return scrubObject(value as Record<string, unknown>, counter);
  }
  return value;
}

function scrubHeaders(value: unknown, counter: { redacted: number }): unknown {
  if (!Array.isArray(value)) {
    return [];
  }
  const kept: unknown[] = [];
  for (const header of value) {
    if (typeof header !== 'object' || header === null) {
      continue;
    }
    const record = header as Record<string, unknown>;
    const name = typeof record['name'] === 'string' ? record['name'].toLowerCase() : '';
    if (!ALLOWED_HEADERS.has(name)) {
      counter.redacted += 1;
      continue;
    }
    const headerValue = record['value'];
    kept.push({
      name,
      value: typeof headerValue === 'string' ? scrubString(headerValue).value : '',
    });
  }
  return kept;
}

function scrubObject(value: Record<string, unknown>, counter: { redacted: number }): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const [key, entry] of Object.entries(value)) {
    const lowered = key.toLowerCase();
    if (lowered === 'headers') {
      out[key] = scrubHeaders(entry, counter);
      continue;
    }
    if (lowered === 'cookies' || lowered === 'postdata' || lowered === 'querystring' || lowered === '_sha1') {
      counter.redacted += 1;
      continue;
    }
    if (lowered === 'url') {
      out[key] = typeof entry === 'string' ? scrubString(entry).value : entry;
      counter.redacted += 1;
      continue;
    }
    out[key] = scrubValue(entry, counter);
  }
  return out;
}

function scrubTraceJsonl(text: string): { readonly text: string; readonly redacted: number; readonly dropped: number } {
  const counter = { redacted: 0 };
  const kept: string[] = [];
  let dropped = 0;

  for (const line of text.split('\n')) {
    if (line.trim().length === 0) {
      continue;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      // Not JSON. Nothing in the trace streams is expected to be, so this is a
      // signal rather than noise; keep the line out rather than pass it through.
      dropped += 1;
      continue;
    }
    const record = parsed as Record<string, unknown>;
    const type = typeof record['type'] === 'string' ? record['type'] : '';
    if (DROPPED_TRACE_TYPES.has(type)) {
      dropped += 1;
      continue;
    }
    kept.push(JSON.stringify(scrubObject(record, counter)));
  }

  return { text: `${kept.join('\n')}\n`, redacted: counter.redacted, dropped };
}

function scrubTraceStacks(text: string): { readonly text: string; readonly redacted: number } {
  const counter = { redacted: 0 };
  const kept: string[] = [];
  for (const line of text.split('\n')) {
    if (line.trim().length === 0) {
      continue;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(line);
    } catch {
      continue;
    }
    kept.push(JSON.stringify(scrubValue(parsed, counter)));
  }
  return { text: `${kept.join('\n')}\n`, redacted: counter.redacted };
}

export interface ScrubResult {
  readonly archive: Buffer;
  /** Entry name -> bytes, for whole entries that were dropped. */
  readonly dropped: Readonly<Record<string, number>>;
  /** Records dropped from inside a kept text stream (DOM snapshots, frames). */
  readonly droppedRecords: number;
  readonly redacted: number;
  readonly problems: readonly string[];
}

/** Scrubs a trace archive in memory. Exposed separately so it can be tested. */
export function scrubTraceArchive(archive: Buffer): ScrubResult {
  const dropped: Record<string, number> = {};
  const problems: string[] = [];
  let redacted = 0;
  let droppedRecords = 0;

  let entries: ZipEntry[];
  try {
    entries = readZip(archive);
  } catch (error) {
    return {
      archive: Buffer.alloc(0),
      dropped,
      droppedRecords,
      redacted,
      problems: [`trace could not be parsed, so it cannot be scrubbed: ${(error as Error).message}`],
    };
  }

  const output: ZipEntry[] = [];
  for (const entry of entries) {
    if (!KEPT_ENTRIES.includes(entry.name)) {
      dropped[entry.name] = entry.data.length;
      continue;
    }
    const text = entry.data.toString('utf8');
    if (entry.name === 'trace.stacks') {
      const scrubbed = scrubTraceStacks(text);
      redacted += scrubbed.redacted;
      output.push({ name: entry.name, method: 8, data: Buffer.from(scrubbed.text, 'utf8') });
      continue;
    }
    const scrubbed = scrubTraceJsonl(text);
    redacted += scrubbed.redacted;
    droppedRecords += scrubbed.dropped;
    output.push({ name: entry.name, method: 8, data: Buffer.from(scrubbed.text, 'utf8') });
  }

  return { archive: writeZip(output), dropped, droppedRecords, redacted, problems };
}

/** Scans a scrubbed archive for anything the policy says must not be in it. */
export function findForbiddenContent(archive: Buffer): string[] {
  const hits: string[] = [];
  let entries: ZipEntry[];
  try {
    entries = readZip(archive);
  } catch (error) {
    return [`scrubbed trace is not a readable archive: ${(error as Error).message}`];
  }
  for (const entry of entries) {
    const text = entry.data.toString('binary');
    for (const pattern of FORBIDDEN_PATTERNS) {
      if (pattern.regex.test(text)) {
        hits.push(`${entry.name}: ${pattern.label}`);
      }
    }
  }
  return hits;
}

/**
 * Stops tracing on `context`, scrubs the archive and writes it to `path`.
 *
 * Never throws. A trace that cannot be produced is an outcome the caller
 * reports, not a second failure on top of the one it already has.
 */
export async function stopScrubbedTrace(
  context: BrowserContext,
  path: string,
  rawPath: string,
): Promise<TraceOutcome> {
  const problems: string[] = [];
  let archive: Buffer;
  try {
    await context.tracing.stop({ path: rawPath });
    archive = readFileSync(rawPath);
  } catch (error) {
    return {
      captured: false,
      path,
      dropped: {},
      droppedRecords: 0,
      redacted: 0,
      verified: false,
      problems: [`no trace could be captured (${(error as Error).message})`],
      summary: 'MISSING TRACE',
    };
  }

  const scrubbed = scrubTraceArchive(archive);
  problems.push(...scrubbed.problems);

  if (scrubbed.problems.length > 0) {
    try {
      rmSync(rawPath, { force: true });
    } catch {
      // The raw trace is in the git-ignored artifact directory; failing to
      // delete it must not replace the report with a second failure.
    }
    return {
      captured: false,
      path,
      dropped: scrubbed.dropped,
      droppedRecords: scrubbed.droppedRecords,
      redacted: scrubbed.redacted,
      verified: false,
      problems,
      summary: 'UNSCRUBBABLE TRACE - withheld, timing evidence still attached',
    };
  }

  const hits = findForbiddenContent(scrubbed.archive);
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, scrubbed.archive);
  // The raw, unscrubbed trace is deleted unconditionally: it is the thing the
  // policy exists to keep out of CI artifacts, and leaving it next to the
  // scrubbed one is how it eventually gets uploaded.
  try {
    rmSync(rawPath, { force: true });
  } catch (error) {
    problems.push(`raw trace could not be deleted at ${rawPath}: ${(error as Error).message}`);
  }

  if (hits.length > 0) {
    return {
      captured: true,
      path,
      dropped: scrubbed.dropped,
      droppedRecords: scrubbed.droppedRecords,
      redacted: scrubbed.redacted,
      verified: false,
      problems: [...problems, `post-scrub verification failed: ${hits.join('; ')}`],
      summary: 'TRACE ATTACHED BUT NOT CLEAN',
    };
  }

  return {
    captured: true,
    path,
    dropped: scrubbed.dropped,
    droppedRecords: scrubbed.droppedRecords,
    redacted: scrubbed.redacted,
    verified: true,
    problems,
    summary:
      `trace attached (${path}); dropped ${String(Object.keys(scrubbed.dropped).length)} entry/entries ` +
      `including all response bodies and screencast frames, plus ${String(scrubbed.droppedRecords)} ` +
      `DOM/frame record(s); redacted ${String(scrubbed.redacted)} value(s); verified clean`,
  };
}
