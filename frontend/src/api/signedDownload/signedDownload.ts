import { normalizeError } from '../errors/index.js';
import type { AppError } from '../errors/index.js';

/**
 * Shared click-time signed-URL resolution (Task 044 reusing Task 033/037).
 *
 * The export download already established the mechanism this module holds:
 * a click handler asks for a URL, the server answers `302` with a
 * `Location`, the URL lives in the handler's local variable for the duration
 * of one click and is then discarded, and an expired link (`410 URL_EXPIRED`)
 * is refetched exactly once. Signed URLs are 15-minute, tenant-bound and
 * never logged, never cached, never put in a query key and never put in the
 * DOM as an `href` before the click.
 *
 * WHY IT IS EXTRACTED RATHER THAN REWRITTEN
 * -----------------------------------------
 * Task 044 needs the same contract for the lip-sync transformed asset, and R3
 * is explicit that it must reuse the mechanism rather than introduce a second
 * one. Two copies of a 410-retry ladder is how the two copies drift — the
 * export path gets a `Location`-header fix and the enrichment path does not,
 * and only the first is covered by tests. So the ladder is written once here
 * and both callers pass their own fetcher.
 *
 * The caller supplies `once` rather than a path so this module never has to
 * know about auth headers, base URLs or the transport.
 */

/** The transient result of one click. Never stored beyond the handler. */
export interface SignedDownloadUrl {
  readonly url: string;
}

/** Lets a caller word the two terminal messages for its own subject. */
export interface SignedDownloadMessages {
  /** 404: the asset or job no longer exists. */
  readonly missing: string;
  /** Anything else: the link could not be prepared. */
  readonly unavailable: string;
}

function normalized(value: unknown): AppError {
  return normalizeError(value, { method: 'GET' });
}

function isExpired(value: unknown): boolean {
  const error = normalized(value);
  return error.status === 410 || error.code === 'URL_EXPIRED';
}

function isMissing(value: unknown): boolean {
  const error = normalized(value);
  return error.status === 404 || error.code.includes('NOT_FOUND');
}

/**
 * Reads the redirect target off a response.
 *
 * Prefers the `Location` header and falls back to the response URL. The
 * `https://` check on the fallback is a guard, not decoration: a
 * `requestRaw` with `redirect: 'manual'` can surface an `opaqueredirect`,
 * whose `url` is the request URL rather than the target, and following that
 * would re-issue the API call with the bearer token attached to a media URL.
 */
function locationOf(response: Response): string | undefined {
  const header = response.headers.get('Location') ?? response.headers.get('location');
  if (header !== null && header !== '') {
    return header;
  }
  const url = typeof response.url === 'string' ? response.url : '';
  if (url !== '' && url.toLowerCase().startsWith('https://')) {
    return url;
  }
  return undefined;
}

async function errorEnvelopeOf(response: Response): Promise<{ code: string; message: string }> {
  let code = 'INTERNAL_ERROR';
  let message = '';
  try {
    const body = (await response.clone().json()) as { error?: { code?: string; message?: string } };
    if (typeof body.error?.code === 'string') {
      code = body.error.code;
    }
    if (typeof body.error?.message === 'string') {
      message = body.error.message;
    }
  } catch {
    // Non-JSON failure: keep the generic envelope.
  }
  return { code, message };
}

/**
 * Fetches one signed download URL at click time.
 *
 * `once` is called once, and at most one extra time when the answer is an
 * expiry. Double expiry rejects with `URL_EXPIRED` so the row can offer a
 * retry; a `404` rejects with `NOT_FOUND` so the row can drop itself. Nothing
 * here retries a `5xx`: a failing download is the caller's to retry
 * explicitly, and an automatic retry of a mutation-shaped endpoint is exactly
 * what the idempotency rules exist to prevent.
 */
export async function fetchSignedDownloadUrl(
  once: () => Promise<Response>,
  messages: SignedDownloadMessages,
): Promise<SignedDownloadUrl> {
  const missing = (): AppError =>
    normalizeError({ status: 404, code: 'NOT_FOUND', message: messages.missing, correlationId: '', details: {} }, { method: 'GET' });
  const unavailable = (status: number, code: string, message: string): AppError =>
    normalizeError({ status, code, message: message === '' ? messages.unavailable : message, correlationId: '', details: {} }, { method: 'GET' });
  const expired = (): AppError => unavailable(410, 'URL_EXPIRED', messages.unavailable);

  /** One attempt: returns the URL, or throws the normalized terminal error. */
  async function attempt(): Promise<SignedDownloadUrl> {
    let response: Response;
    try {
      response = await once();
    } catch (error) {
      if (isMissing(error)) {
        throw missing();
      }
      if (isExpired(error)) {
        throw expired();
      }
      throw normalized(error);
    }

    if (response.status === 404) {
      throw missing();
    }
    if (response.status === 410) {
      throw expired();
    }
    // `requestRaw` throws for other non-2xx, but a hand-rolled fetcher may not;
    // read the uniform error envelope so the caller shows the server's message.
    if (!response.ok && response.status !== 302 && response.type !== 'opaqueredirect') {
      const envelope = await errorEnvelopeOf(response);
      throw unavailable(response.status, envelope.code, envelope.message);
    }
    const url = locationOf(response);
    if (url === undefined) {
      throw unavailable(500, 'INTERNAL_ERROR', messages.unavailable);
    }
    return { url };
  }

  try {
    return await attempt();
  } catch (first) {
    if (!isExpired(first)) {
      throw first;
    }
    // Exactly one refetch on expiry. A second expiry is terminal: a link that
    // expires twice in a row is not a link, it is a misconfiguration, and
    // looping here would turn one click into an unbounded request storm.
    try {
      return await attempt();
    } catch (second) {
      if (isExpired(second) || isMissing(second)) {
        throw isMissing(second) ? missing() : expired();
      }
      throw second;
    }
  }
}

/**
 * Triggers a browser download for a URL the caller already holds. Mirrors the
 * Task 033 export row: build the anchor, click it, remove it. The URL is
 * never written into React state and never survives this call.
 */
export function triggerBrowserDownload(url: string, filename: string): void {
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  anchor.rel = 'noreferrer';
  document.body.appendChild(anchor);
  anchor.click();
  anchor.remove();
}