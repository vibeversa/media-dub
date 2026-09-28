/**
 * Telemetry scrubber (Task 038).
 *
 * Recursively strips sensitive content from arbitrary payloads before emission:
 * tokens/secret-bearing keys, URLs (incl. signed URLs + query strings), media
 * references (data: URIs, long base64 blobs), transcript/translation text,
 * emails, bearer/JWT credentials, and stack internals. Output preserves shape
 * (same keys, redacted scalar values) so taxonomy conformance tests can assert
 * structure while adversarial fixtures prove zero leakage.
 */

export const SCRUB_REDACTED = '[REDACTED]';

/** Exact key names (case-insensitive) whose values are always redacted. */
const SENSITIVE_KEYS: ReadonlySet<string> = new Set([
  'token',
  'accesstoken',
  'refreshtoken',
  'authorization',
  'bearer',
  'email',
  'password',
  'secret',
  'query',
  'search',
  'transcript',
  'translation',
  'edittext',
  'text',
  'media',
  'audio',
  'video',
  'image',
  'file',
  'blob',
  'url',
  'downloadurl',
  'signedurl',
  'body',
  'payload',
  'details',
  'stack',
  'stacktrace',
  'trace',
]);

/** Substring fragments (case-insensitive) marking a key as sensitive. */
const SENSITIVE_KEY_FRAGMENTS: readonly string[] = [
  'token',
  'secret',
  'passwd',
  'credential',
  'apikey',
  'api_key',
  'privatekey',
  'private_key',
  'signedurl',
  'transcript',
  'bearer',
];

const URL_PATTERN = /https?:\/\/[^\s"'<>]+/gi;
const DATA_URI_PATTERN = /data:[a-zA-Z0-9/+=.-]+;[a-zA-Z0-9;=+-]+,[^\s"'<>]*/g;
const BEARER_PATTERN = /\bearer\s+[A-Za-z0-9\-._~+/=]+/gi;
const JWT_PATTERN = /\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/g;
const EMAIL_PATTERN = /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b/g;
const LONG_BASE64_PATTERN = /\b[A-Za-z0-9+/]{64,}={0,2}\b/g;
const STACK_FRAME_PATTERN = /^\s*at\s+\S+.*\(.*:\d+:\d+\)\s*$/gm;

/** True when a key name denotes sensitive material (case-insensitive). */
export function isSensitiveKey(key: string): boolean {
  const lower = key.toLowerCase();
  if (SENSITIVE_KEYS.has(lower)) {
    return true;
  }
  return SENSITIVE_KEY_FRAGMENTS.some((fragment) => lower.includes(fragment));
}

/** Redacts sensitive patterns inside a free-text string. */
export function scrubString(value: string): string {
  let scrubbed = value;
  scrubbed = scrubbed.replace(URL_PATTERN, SCRUB_REDACTED);
  scrubbed = scrubbed.replace(DATA_URI_PATTERN, SCRUB_REDACTED);
  scrubbed = scrubbed.replace(BEARER_PATTERN, `Bearer ${SCRUB_REDACTED}`);
  scrubbed = scrubbed.replace(JWT_PATTERN, SCRUB_REDACTED);
  scrubbed = scrubbed.replace(EMAIL_PATTERN, SCRUB_REDACTED);
  scrubbed = scrubbed.replace(STACK_FRAME_PATTERN, SCRUB_REDACTED);
  // Long base64 blobs (media bytes) last: JWTs/URLs already handled above.
  scrubbed = scrubbed.replace(LONG_BASE64_PATTERN, SCRUB_REDACTED);
  return scrubbed;
}

/**
 * Deep-clones `input`, redacting sensitive keys and scrubbing every string.
 * Non-plain values (functions, class instances beyond plain objects/arrays)
 * collapse to `[REDACTED]` rather than leaking internals.
 */
export function scrubPayload<T>(input: T): T {
  if (typeof input === 'string') {
    return scrubString(input) as unknown as T;
  }
  if (typeof input !== 'object' || input === null) {
    return input;
  }
  if (Array.isArray(input)) {
    return input.map((item) => scrubPayload(item)) as unknown as T;
  }
  const proto = Object.getPrototypeOf(input);
  if (proto !== Object.prototype && proto !== null) {
    return SCRUB_REDACTED as unknown as T;
  }
  const output: Record<string, unknown> = {};
  for (const key of Object.keys(input as Record<string, unknown>)) {
    const value = (input as Record<string, unknown>)[key];
    if (isSensitiveKey(key)) {
      output[key] = SCRUB_REDACTED;
    } else {
      output[key] = scrubPayload(value);
    }
  }
  return output as unknown as T;
}

/**
 * True when a (possibly already scrubbed) value still carries sensitive
 * material: a sensitive key anywhere in its JSON shape, or a live URL,
 * credential, email, data: URI, stack frame, or long base64 blob in its text.
 * Test seam for adversarial "fully redacted" assertions.
 */
export function containsSensitive(value: unknown): boolean {
  if (typeof value === 'string') {
    const patterns = [
      /https?:\/\//i,
      /\bdata:[a-zA-Z0-9/+=.-]+;/i,
      /\bearer\s+[A-Za-z0-9\-._~+/=]+/i,
      /\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+/,
      /\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b/,
      /^\s*at\s+\S+.*\(.*:\d+:\d+\)\s*$/m,
      /\b[A-Za-z0-9+/]{64,}={0,2}\b/,
    ];
    return patterns.some((pattern) => pattern.test(value));
  }
  if (typeof value !== 'object' || value === null) {
    return false;
  }
  if (Array.isArray(value)) {
    return value.some((item) => containsSensitive(item));
  }
  return Object.entries(value as Record<string, unknown>).some(([key, nested]) => {
    if (isSensitiveKey(key) && nested !== SCRUB_REDACTED) {
      return true;
    }
    return containsSensitive(nested);
  });
}

/**
 * Throws when `value` still carries sensitive material. Emit paths call this
 * in dev/tests (mirroring `assertAllowlisted`) so leaks fail fast.
 */
export function assertNoSensitive(value: unknown): void {
  if (containsSensitive(value)) {
    throw new Error('Telemetry payload still contains sensitive material after scrubbing.');
  }
}
