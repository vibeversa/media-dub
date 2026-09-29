// Task 041D: the synthetic fixtures, and the route layer that serves them.
//
// The rig seeds two projects and one segment. Every budget that is specified
// against a data size - "at 200 rows", "at 5k segments" - therefore cannot be
// measured against the seed, and the task's own security requirement settles how
// to get the data: "Perf specs use synthetic fixtures only." So the fixtures are
// generated here, in the test process, and served to the browser at the
// transport layer with `page.route`.
//
// What that does and does not preserve is the whole point, so it is stated
// rather than left to be discovered:
//
//   PRESERVED  The real bundle, the real router, the real providers, the real
//              React render, the real `DataGrid`, the real `VirtualizedSegmentList`,
//              the real debounce, the real timeline, the real `<video>` element.
//              Every millisecond measured is time the application spent.
//   REPLACED   The API responses. Server time is therefore NOT in any budget,
//              and each budget says so. A client-render regression is caught; a
//              slow endpoint is not, and is not this gate's job.
//
// The fixtures are deterministic: a fixed corpus, no randomness, no clock, so a
// run measures the same work every time and a breach is a change rather than a
// lottery.

import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import type { Page, Route } from '@playwright/test';

/** Tenant size for the project-list budget. The task's "at 200 rows". */
export const SYNTHETIC_PROJECT_COUNT = 200;

/** The page size the client can actually request: the API maximum is 100. */
export const PROJECT_PAGE_SIZE = 100;

/** Corpus size for the segment-search budget. The task's "at 5k segments". */
export const SYNTHETIC_SEGMENT_COUNT = 5000;

/**
 * Corpus size for the timeline budget.
 *
 * A long-form episode. 1,200 utterances is ~16/minute over 75 minutes, which is
 * the dense end of real dialogue. It is deliberately below the 2,000 tenant
 * quota ceiling, and `practices.spec.ts` records why: the dialogue lane renders
 * every segment and is not windowed, so the ceiling is a known gap for 030
 * rather than a number this gate quietly avoids.
 */
export const TIMELINE_SEGMENT_COUNT = 1200;

/** The token that makes a search result countable, not just "some rows". */
export const SEARCH_NEEDLE = 'perfneedle';

/** Every Nth segment carries `SEARCH_NEEDLE`, so the match count is exact. */
export const SEARCH_NEEDLE_STRIDE = 50;

/** Expected matches for `SEARCH_NEEDLE` in a 5,000-segment corpus. */
export const SEARCH_NEEDLE_EXPECTED = SYNTHETIC_SEGMENT_COUNT / SEARCH_NEEDLE_STRIDE;

/** Duration of the synthetic preview, in ms. Also the timeline's duration. */
export const PREVIEW_DURATION_MS = 60_000;

/** Same-origin path the synthetic signed descriptor points at. */
export const PREVIEW_MEDIA_PATH = '/__perf__/preview-60s.mkv';

/**
 * Route pattern for the synthetic preview.
 *
 * A RegExp, not a glob: the descriptor appends a presigned query string, and
 * Playwright's URL globs match the query as well as the path. A glob ending at
 * the file name therefore matches nothing, the request falls through to the
 * frontend's SPA fallback, and the media element ends up with an empty
 * `seekable` range - which is exactly the failure this pattern exists to avoid
 * repeating.
 */
export const PREVIEW_MEDIA_PATTERN = /\/__perf__\/preview-60s\.mkv(\?|$)/;

/**
 * A syntactically valid, never-existing project id, one per perf spec.
 *
 * The synthetic endpoints are keyed on the project id, and the specs share one
 * authenticated page (the tokens are in that page's heap), so a shared project
 * id would make one spec's 1,200-segment corpus land in another spec's
 * 5,000-segment query cache. Distinct ids keep every react-query key - and
 * therefore every fixture - separate. The id never reaches the server: the
 * routes that would carry it are all intercepted, and `ProjectLayout` fetches
 * nothing.
 */
export function perfProjectId(ordinal: number): string {
  if (!Number.isInteger(ordinal) || ordinal < 0 || ordinal > 0xff) {
    throw new Error(`perfProjectId() takes 0..255, got ${String(ordinal)}.`);
  }
  return `prj_perf${ordinal.toString(16).padStart(31, '0')}`;
}

/** Repository-relative path of the synthetic preview, and its recorded size. */
export const PREVIEW_MEDIA_FILE = 'e2e/perf/fixtures/preview-60s.mkv';

const SPEAKERS: readonly string[] = [
  'Narrator',
  'Dr. Okonkwo',
  'Ines Alvarez',
  'Ravi Menon',
  'Chancellor Vex',
  'Background',
];

const WORDS: readonly string[] = [
  'anchor', 'bridge', 'canyon', 'dial', 'ember', 'fathom', 'granite', 'harbor',
  'ivory', 'juniper', 'kettle', 'lantern', 'meridian', 'nimbus', 'obsidian',
  'parchment', 'quarry', 'rivet', 'summit', 'trellis', 'umber', 'vellum',
  'willow', 'yarrow', 'zenith', 'alcove', 'beacon', 'citadel', 'drift', 'echo',
];

/**
 * Deterministic 32-bit mixer.
 *
 * `Math.random()` would make every run measure a different corpus, which turns
 * a budget into a slot machine. This is a fixed-seed xorshift: same input, same
 * output, on every machine and every run.
 */
function mix(seed: number): () => number {
  let state = (seed | 0) === 0 ? 0x9e3779b9 : seed | 0;
  return () => {
    state ^= state << 13;
    state ^= state >>> 17;
    state ^= state << 5;
    return ((state >>> 0) % 1_000_000) / 1_000_000;
  };
}

function pad32(value: number): string {
  return value.toString(16).padStart(32, '0').slice(-32);
}

function repoFile(relative: string): string {
  let current = process.cwd();
  for (let depth = 0; depth < 10; depth += 1) {
    if (existsSync(join(current, 'playwright.config.ts'))) {
      return join(current, relative);
    }
    const parent = join(current, '..');
    if (parent === current) {
      break;
    }
    current = parent;
  }
  throw new Error(`Could not locate the repository root from ${process.cwd()} to read ${relative}.`);
}

export function readPreviewMedia(): { readonly bytes: Buffer; readonly path: string } {
  const path = repoFile(PREVIEW_MEDIA_FILE);
  if (!existsSync(path)) {
    throw new Error(
      `The synthetic preview fixture is missing at ${path}. Regenerate it with ` +
        '`node e2e/perf/fixtures/generate-media.mjs` (see e2e/perf/README.md).',
    );
  }
  return { bytes: readFileSync(path), path };
}

export interface SyntheticProject {
  readonly id: string;
  readonly name: string;
  readonly sourceLanguage: string;
  readonly targetLanguage: string;
  readonly status: string;
  readonly isArchived: boolean;
  readonly createdAt: string;
  readonly updatedAt: string;
}

export function buildProjects(count: number = SYNTHETIC_PROJECT_COUNT): SyntheticProject[] {
  const next = mix(20_260_115);
  const languages = ['es', 'fr', 'de', 'ja', 'pt-BR', 'ar'];
  return Array.from({ length: count }, (_unused, index) => ({
    id: `prj_${pad32(0x5eed_0000 + index)}`,
    name: `Perf fixture project ${String(index + 1).padStart(3, '0')}`,
    sourceLanguage: 'en',
    targetLanguage: languages[index % languages.length] ?? 'es',
    status: index % 7 === 0 ? 'Completed' : index % 3 === 0 ? 'Processing' : 'MediaReady',
    isArchived: false,
    // Fixed stamps, newest first, so the rendered order is deterministic.
    createdAt: new Date(Date.UTC(2026, 0, 15, 12, 0, 0) - index * 60_000).toISOString(),
    updatedAt: new Date(Date.UTC(2026, 0, 15, 12, 0, 0) - index * 60_000).toISOString(),
  }));
}

export interface SyntheticSegment {
  readonly id: string;
  readonly projectId: string;
  readonly sequence: number;
  readonly startMs: number;
  readonly endMs: number;
  readonly speakerId: string;
  readonly speakerLabel: string;
  readonly selectionVersion: number;
  readonly reviewStatus: string;
  readonly qualityCodes: readonly string[];
  readonly syncStatus: string | null;
  readonly confidence: number;
  readonly text: string;
  readonly originalText: string;
  readonly selectedTranscriptVersionId: string;
  readonly transcriptVersions: readonly { id: string; text: string; provider: string; model: string; isSelected: boolean; isManual: boolean; createdAt: string }[];
}

export function buildSegments(projectId: string, count: number): SyntheticSegment[] {
  const next = mix(0x0bad_c0de);
  let cursorMs = 0;
  return Array.from({ length: count }, (_unused, index) => {
    const durationMs = 1800 + Math.round(next() * 2600);
    const startMs = cursorMs;
    cursorMs += durationMs;
    const speaker = SPEAKERS[index % SPEAKERS.length] ?? 'Narrator';
    const carriesNeedle = index % SEARCH_NEEDLE_STRIDE === 0;
    const words = Array.from({ length: 6 + Math.round(next() * 8) }, () => {
      const word = WORDS[Math.floor(next() * WORDS.length)] ?? 'anchor';
      return word.charAt(0).toUpperCase() + word.slice(1);
    }).join(' ');
    // Every row carries its own zero-padded index token as well, so a search
    // term can select an exact, precomputable number of rows. Without it the
    // only predictable term is the strided needle, and a suite that searches
    // the same term three times is not testing three searches.
    const indexToken = `perfseg${String(index).padStart(5, '0')}`;
    const text = carriesNeedle
      ? `perfneedle ${indexToken} ${words} (segment ${String(index + 1)})`
      : `${indexToken} ${words} (segment ${String(index + 1)})`;
    const versionId = `tv_${pad32(0xc0de_0000 + index)}`;
    return {
      id: `seg_${pad32(0x5e60_0000 + index)}`,
      projectId,
      sequence: index + 1,
      startMs,
      endMs: startMs + durationMs,
      speakerId: `spk_${pad32(0x5a00_0000 + (index % SPEAKERS.length))}`,
      speakerLabel: speaker,
      selectionVersion: 1,
      reviewStatus: index % 17 === 0 ? 'Open' : 'Approved',
      qualityCodes: index % 23 === 0 ? ['low-confidence'] : [],
      syncStatus: null,
      confidence: 0.55 + next() * 0.45,
      text,
      originalText: text,
      selectedTranscriptVersionId: versionId,
      transcriptVersions: [
        {
          id: versionId,
          text,
          provider: 'mock-asr',
          model: 'mock-asr-v1',
          isSelected: true,
          isManual: false,
          createdAt: '2026-01-15T12:00:00.000Z',
        },
      ],
    } satisfies SyntheticSegment;
  });
}

/**
 * How many fixture rows a search term selects, counted the way the application
 * counts them.
 *
 * This mirrors `filterTranscriptSegments`: a case-insensitive substring over
 * the text, the original text and the speaker label. Computing the expectation
 * from the corpus rather than hardcoding it is the point - a hardcoded count is
 * a count that silently stops being true when the fixture changes, and the
 * symptom is a 30-second timeout rather than a wrong number.
 */
export function countMatches(segments: readonly SyntheticSegment[], term: string): number {
  const needle = term.trim().toLowerCase();
  if (needle === '') {
    return segments.length;
  }
  return segments.filter((segment) =>
    `${segment.text} ${segment.originalText ?? segment.text} ${segment.speakerLabel}`.toLowerCase().includes(needle),
  ).length;
}

export function buildPeaks(durationMs: number): Record<string, unknown> {  const next = mix(0x9ea1_0001);
  const resolutions: Record<string, number[]> = {};
  for (const resolution of [64, 256, 1024]) {
    resolutions[String(resolution)] = Array.from({ length: resolution }, () =>
      Number((0.15 + next() * 0.85).toFixed(4)),
    );
  }
  return { durationMs, sampleRate: 8000, peaksMissing: false, resolutions };
}

export interface RecordedRequest {
  readonly method: string;
  readonly url: string;
  readonly path: string;
  /** True when the request was answered by the synthetic fixture layer. */
  readonly synthetic: boolean;
}

export interface SyntheticApiOptions {
  /** Project ids the segment/peaks/output fixtures answer for. */
  readonly projectId: string;
  /** Segments served by the segment endpoints. */
  readonly segmentCount: number;
  /**
   * When true, the segment list endpoint returns the whole corpus on page 1
   * regardless of the page size the client asked for.
   *
   * This is a deliberate contract violation and it is the only way to put 5,000
   * segments in the client: `Quota:MaxSegmentCount` rejects more than 2,000 and
   * `useTranscript` fetches at most 10 pages of 200. The alternative - measuring
   * a budget the application cannot reach - would be worse. `practices.spec.ts`
   * asserts the real ceiling so this cannot be mistaken for a supported state.
   */
  readonly overshootPageSize: boolean;
}

export interface SyntheticApi {
  readonly requests: readonly RecordedRequest[];
  /** Requested paths, for the "preview peaks, never archival media" assertion. */
  readonly paths: () => readonly string[];
  close: () => Promise<void>;
  /** The corpus the segment endpoints serve, for match-count assertions. */
  readonly segments: readonly SyntheticSegment[];
}

/**
 * Installs the synthetic API layer on `page`.
 *
 * One catch-all route on the API's own path prefix that dispatches by path and
 * falls through for anything it does not own, so a budget that needs the real
 * API on the same page still gets it. Every request is recorded, which is what
 * makes the "timeline uses preview peaks, not archival media" structural
 * assertion a measurement rather than a comment.
 */
export async function installSyntheticApi(page: Page, options: SyntheticApiOptions): Promise<SyntheticApi> {
  const projects = buildProjects();
  const segments = buildSegments(options.projectId, options.segmentCount);
  const preview = readPreviewMedia();
  const requests: RecordedRequest[] = [];

  /**
   * The synthetic preview is served from a same-origin path outside the API's
   * prefix, because the app's CSP (`default-src 'self'`) forbids cross-origin
   * media and a real signed preview URL points at object storage. So it needs
   * its own route; folding it into the API route lets the frontend's SPA
   * fallback answer the media request with `index.html`, which the element
   * reports as a decode error and an empty `seekable` range.
   *
   * A RegExp rather than a glob, because the descriptor appends the presigned
   * query string and Playwright's URL globs match the query too.
   */
  async function handlePreview(route: Route): Promise<void> {
    const request = route.request();
    const url = new URL(request.url());
    requests.push({
      method: request.method(),
      url: request.url(),
      path: `${url.pathname}${url.search}`,
      synthetic: true,
    });
    await fulfillRange(route, preview.bytes, 'video/x-matroska');
  }

  async function handle(route: Route): Promise<void> {
    const request = route.request();
    const url = new URL(request.url());
    const path = url.pathname;
    const method = request.method();
    const isProjectId = (candidate: string): boolean => path.includes(candidate);
    const record = (synthetic: boolean): void => {
      requests.push({ method, url: request.url(), path: `${path}${url.search}`, synthetic });
    };

    if (method === 'GET' && /\/api\/v1\/projects$/.test(path)) {
      record(true);
      const pageNumber = Math.max(1, Number.parseInt(url.searchParams.get('page') ?? '1', 10) || 1);
      const pageSize = Math.max(1, Math.min(100, Number.parseInt(url.searchParams.get('pageSize') ?? '20', 10) || 20));
      const start = (pageNumber - 1) * pageSize;
      const items = projects.slice(start, start + pageSize);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          items,
          page: pageNumber,
          pageSize,
          total: projects.length,
          sort: 'createdAt',
          sortDir: 'desc',
          hasMore: start + items.length < projects.length,
          clamped: false,
        }),
      });
      return;
    }

    if (method === 'GET' && /\/api\/v1\/projects\/[^/]+\/segments$/.test(path) && isProjectId(options.projectId)) {
      record(true);
      const pageNumber = Math.max(1, Number.parseInt(url.searchParams.get('page') ?? '1', 10) || 1);
      const requested = Math.max(1, Number.parseInt(url.searchParams.get('pageSize') ?? '50', 10) || 50);
      const effective = options.overshootPageSize ? segments.length : Math.min(requested, segments.length);
      const start = options.overshootPageSize && pageNumber === 1 ? 0 : (pageNumber - 1) * effective;
      const items = segments.slice(start, start + effective);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          items,
          page: pageNumber,
          pageSize: effective,
          total: segments.length,
          hasMore: start + items.length < segments.length,
        }),
      });
      return;
    }

    if (method === 'GET' && /\/api\/v1\/projects\/[^/]+\/segments\/[^/]+$/.test(path)) {
      record(true);
      const segmentId = path.split('/').pop() ?? '';
      const segment = segments.find((candidate) => candidate.id === segmentId) ?? segments[0];
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(segment ?? {}),
      });
      return;
    }

    if (method === 'GET' && path.endsWith('/media/waveform-peaks')) {
      record(true);
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify(buildPeaks(PREVIEW_DURATION_MS)),
      });
      return;
    }

    if (method === 'GET' && path.endsWith('/output/download')) {
      record(true);
      // The presigned-looking query is not a secret: it is a fixture marker, and
      // it is deliberately *not* a real signature. It exists so the trace
      // scrubber has a realistically shaped presigned URL to deal with rather
      // than only clean ones. Where it actually lands is worth being precise
      // about, because it was measured rather than assumed: Playwright does NOT
      // put a `page.route`-fulfilled request in `trace.network` (verified: zero
      // matching records), so the URL reaches the raw trace through the DOM
      // snapshot's `<video src>` and is removed because snapshots are dropped
      // whole. The guarantee is not that redaction catches it - it is that
      // `findForbiddenContent` re-reads the *written* archive and fails the run
      // if any presigned parameter, bearer token or `Authorization` header
      // survived, whichever route it arrived by.
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          downloadUrl: `${PREVIEW_MEDIA_PATH}?X-Amz-Algorithm=AWS4-HMAC-SHA256&X-Amz-Credential=PERF_FIXTURE%2FCHANGE_ME&X-Amz-Expires=900&X-Amz-Signature=PERF_FIXTURE_NOT_A_SECRET`,
          expiresAt: '2026-01-15T12:15:00.000Z',
        }),
      });
      return;
    }

    record(false);
    await route.fallback();
  }

  await page.route('**/api/v1/**', handle);
  await page.route(PREVIEW_MEDIA_PATTERN, handlePreview);

  return {
    requests,
    paths: () => requests.map((request) => request.path),
    segments,
    close: async () => {
      await page.unroute('**/api/v1/**', handle);
      await page.unroute(PREVIEW_MEDIA_PATTERN, handlePreview);
    },
  };
}

/**
 * Fulfils a media request, honouring `Range`.
 *
 * Range is not optional. Without a 206 the media element reports
 * `seekable = [0, 0]`, every seek is a silent no-op that still fires `seeked`,
 * and the media-seek budget would pass while measuring nothing. That failure
 * mode was found by running it, and this comment is the receipt.
 */
async function fulfillRange(route: Route, bytes: Buffer, contentType: string): Promise<void> {
  const rangeHeader = route.request().headers()['range'];
  const match = typeof rangeHeader === 'string' ? /bytes=(\d+)-(\d*)/.exec(rangeHeader) : null;
  if (match === null) {
    await route.fulfill({
      status: 200,
      contentType,
      headers: { 'Accept-Ranges': 'bytes', 'Content-Length': String(bytes.length) },
      body: bytes,
    });
    return;
  }
  const start = Number.parseInt(match[1] ?? '0', 10);
  const end = match[2] === undefined || match[2] === '' ? bytes.length - 1 : Number.parseInt(match[2], 10);
  await route.fulfill({
    status: 206,
    contentType,
    headers: {
      'Accept-Ranges': 'bytes',
      'Content-Range': `bytes ${String(start)}-${String(end)}/${String(bytes.length)}`,
      'Content-Length': String(end - start + 1),
    },
    body: bytes.subarray(start, end + 1),
  });
}
