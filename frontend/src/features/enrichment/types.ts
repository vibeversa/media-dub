/**
 * Optional-enrichment domain views (Task 044, Plan B §19.1–§19.3).
 *
 * Every parser here is pure, total and secret-free by construction. Enrichment
 * runs in **optional** workers and its payloads come from provider output the
 * platform does not control, so the parsers are the security boundary that the
 * UI types are not:
 *
 * - `isForbiddenEnrichmentKey` drops the same key families the admin types
 *   drop (`SECRET_KEY_FRAGMENTS` in `features/admin/types.ts`), plus provider
 *   endpoints and URLs. A provider that echoes its own base URL into a result
 *   must not have it reach a DOM node.
 * - Nothing is rendered as HTML, and free text is capped on the way in.
 * - Numbers are clamped rather than trusted: a `score` of `4.2` is shown as
 *   `1.00`, because the alternative is a UI that reports a confidence above
 *   100% and an operator who believes it.
 *
 * Every payload is treated as OPTIONAL. A row is either present-and-parseable
 * or dropped; a malformed entry is skipped, never rendered half-parsed and
 * never fatal to the panel around it.
 */

import { isForbiddenAdminKey } from '../admin/types.js';

/** Enrichment artifact kinds the panel knows how to label. */
export const VIDEO_INTEL_KINDS = ['scene-cut', 'overlay', 'face', 'active-speaker'] as const;

export type VideoIntelKind = (typeof VIDEO_INTEL_KINDS)[number];

/**
 * The lip-sync method note the score always ships with (R3).
 *
 * A bare "0.94" is a number with no provenance, and a reader cannot tell a
 * measured sync offset from a heuristic guess. The platform's own
 * enrichment payload emits `lipSyncScore` with no method field at all
 * (`EnrichmentPayload.BuildLipSyncJson`), so the client states the method
 * rather than leaving it blank — and when the backend does supply one, that
 * value wins. `LIPSYNC_METHOD_FALLBACK` is the honest label for "the worker
 * scored this without a measured ground truth".
 */
export const LIPSYNC_METHOD_FALLBACK = 'heuristic v1';

const MAX_LABEL_LENGTH = 160;
const MAX_ID_LENGTH = 80;

function toRecord(value: unknown): Record<string, unknown> | undefined {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
    ? (value as Record<string, unknown>)
    : undefined;
}

function pick(record: Record<string, unknown> | undefined, ...keys: readonly string[]): unknown {
  if (record === undefined) {
    return undefined;
  }
  for (const key of keys) {
    const value = record[key];
    if (value !== undefined && value !== null) {
      return value;
    }
  }
  return undefined;
}

/**
 * Whether a payload key must never survive parsing.
 *
 * Delegates the shared secret families to the admin predicate rather than
 * restating them — a second copy of that list is exactly how one of the two
 * drifts — and adds the enrichment-only shapes: anything that looks like a
 * provider endpoint, a model path, or a URL. Enrichment results come from a
 * local sidecar that reports its own `baseUrl` and `model` path, and those are
 * operator internals, not user-facing artifact data.
 */
export function isForbiddenEnrichmentKey(key: string): boolean {
  if (isForbiddenAdminKey(key)) {
    return true;
  }
  const flat = key.toLowerCase().replace(/[^a-z0-9]/g, '');
  const enrichmentOnly = [
    'endpoint',
    'baseurl',
    'host',
    'hostname',
    'devicepath',
    'modelfilesystempath',
    'checkpoint',
    'weights',
    'inferenceurl',
    'callback',
    'webhook',
    'proxy',
  ];
  return enrichmentOnly.some((fragment) => flat.includes(fragment));
}

function text(raw: unknown, maxLength: number): string {
  if (typeof raw === 'string') {
    return raw.trim().slice(0, maxLength);
  }
  if (typeof raw === 'number' && Number.isFinite(raw)) {
    return String(raw);
  }
  return '';
}

/**
 * Reads an identifier from either a bare string or a row object.
 *
 * Both shapes occur: an artifact's own id comes from the row, while a
 * `segmentId` reference is a bare value. A `safeId` that only understood
 * objects silently produced `''` for every reference, which made every link
 * read as "unlinked" — a panel that looks correct and links to nothing.
 *
 * A secret-shaped value is never a usable id, so it resolves to `''` and the
 * caller drops or degrades the row rather than rendering it.
 */
function safeId(raw: unknown): string {
  const value =
    typeof raw === 'string'
      ? raw.trim().slice(0, MAX_ID_LENGTH)
      : text(pick(toRecord(raw), 'id', 'Id', 'artifactId', 'ArtifactId'), MAX_ID_LENGTH);
  return isForbiddenEnrichmentKey(value) ? '' : value;
}

/**
 * Reads an id field off a row under any of the given names.
 *
 * Separate from `safeId` because a row's id key is not always `id`: a lip-sync
 * row keys its reference `segmentId`. A single `safeId` that only understood
 * `id` produced `''` for every segment reference, which made every link read as
 * "unlinked" — a panel that looks correct and links to nothing.
 */
function safeIdFrom(row: Record<string, unknown>, ...keys: readonly string[]): string {
  return safeId(pick(row, ...keys));
}

/** True when ANY key of a payload row is forbidden. Pure. */
function rowCarriesForbiddenKey(row: Record<string, unknown>): boolean {
  return Object.keys(row).some((key) => isForbiddenEnrichmentKey(key));
}

/** A timestamp string, or `''`. Pure. */
function timestamp(raw: unknown): string {
  return text(pick(toRecord(raw), 'atMs', 'at', 'timestampMs', 'startMs'), 24);
}

function itemsOf(raw: unknown, ...keys: readonly string[]): unknown[] {
  if (Array.isArray(raw)) {
    return raw;
  }
  const nested = pick(toRecord(raw), ...keys);
  return Array.isArray(nested) ? nested : [];
}

/**
 * One video-intel artifact (scene cut, detected overlay, face track,
 * active-speaker span).
 *
 * `segmentId` is a LINK, not a join. It names a core segment so a reader can
 * jump to it; nothing about the core transcript or timeline is derived from it,
 * and a link to a segment that no longer exists degrades to `Gone` rather than
 * silently disappearing (see `resolveSegmentLink`).
 */
export interface VideoIntelArtifactView {
  readonly id: string;
  readonly kind: VideoIntelKind | 'other';
  readonly label: string;
  readonly atMs: string;
  /** Segment this artifact is *about*; may be absent for whole-video results. */
  readonly segmentId: string;
  /** Text region or bounding hint, when the provider supplies one. Never HTML. */
  readonly detail: string;
}

export interface VideoIntelView {
  readonly artifacts: readonly VideoIntelArtifactView[];
  /** Provider label, e.g. `local-vision`. Names a provider, never an endpoint. */
  readonly model: string;
  readonly generatedAt: string;
  /** True when the payload carried nothing usable; the panel renders EmptyState. */
  readonly empty: boolean;
}

function normalizeKind(raw: unknown): VideoIntelKind | 'other' {
  const value = text(raw, 40).toLowerCase();
  for (const kind of VIDEO_INTEL_KINDS) {
    if (value === kind || value.replace(/[-_]/g, '') === kind.replace('-', '')) {
      return kind;
    }
  }
  return 'other';
}

/**
 * Parses `GET /projects/{id}/enrichment/video-intel`.
 *
 * Whole-document or bare-array tolerant, because this endpoint is
 * frontend-anticipated (the backend provisions it when the enrichment worker
 * ships) and a provisional shape is better handled here than by a crash. Rows
 * with no id or a secret-shaped id are dropped; they cannot be linked,
 * de-duplicated or reasoned about.
 */
export function parseVideoIntel(raw: unknown): VideoIntelView {
  const document = toRecord(raw);
  const artifacts: VideoIntelArtifactView[] = [];
  const seen = new Set<string>();
  for (const entry of itemsOf(raw, 'artifacts', 'items', 'results', 'scenes')) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    // A row that carries a credential, a provider endpoint or an internal path
    // is dropped whole. Filtering per-field would let a row through with the
    // offending value removed and the rest of it rendered, which is worse:
    // the reader sees a result the backend produced under conditions nobody
    // has reviewed.
    if (rowCarriesForbiddenKey(row)) {
      continue;
    }
    const id = safeId(row);
    if (id === '' || seen.has(id)) {
      continue;
    }
    seen.add(id);
    artifacts.push({
      id,
      kind: normalizeKind(pick(row, 'kind', 'type', 'artifactType')),
      label: text(pick(row, 'label', 'name', 'title', 'text'), MAX_LABEL_LENGTH),
      atMs: timestamp(row),
      segmentId: safeIdFrom(row, 'segmentId', 'segment', 'segment_id'),
      detail: text(pick(row, 'detail', 'region', 'overlayText', 'description'), MAX_LABEL_LENGTH),
    });
  }
  const model = text(pick(document, 'model', 'modelId', 'provider'), 60);
  return {
    artifacts,
    model: isForbiddenEnrichmentKey(model) ? '' : model,
    generatedAt: text(pick(document, 'generatedAt', 'createdAt', 'completedAt'), 40),
    empty: artifacts.length === 0,
  };
}

// --- Lip sync ----------------------------------------------------------------

/**
 * The lip-sync score for one segment, with the method note attached.
 *
 * `score` is already clamped to `[0, 1]` at parse time — see `parseLipSync`.
 */
export interface LipSyncSegmentView {
  readonly segmentId: string;
  /** `undefined` when the payload carried no numeric score for this segment. */
  readonly score: number | undefined;
  /** Rendered next to the score. Never empty: falls back to the documented note. */
  readonly method: string;
  /** False when the worker scored but published no transformed asset. */
  readonly assetAvailable: boolean;
  /** Why the download is hidden. Empty string when it is available. */
  readonly assetUnavailableReason: string;
}

/**
 * The project-level transformed asset, when the payload publishes one.
 *
 * Distinct from the core output/export descriptors of Task 033: it comes from
 * the enrichment endpoint, has its own id space and its own lifecycle, and is
 * rendered only inside `LipSyncPanel`.
 */
export interface LipSyncAssetView {
  readonly id: string;
  readonly format: string;
  readonly sizeBytes: number | undefined;
}

export interface LipSyncView {
  readonly segments: readonly LipSyncSegmentView[];
  /** Overall project score, when the payload reports one. */
  readonly overallScore: number | undefined;
  readonly overallMethod: string;
  readonly model: string;
  /** The separate transformed asset, or `undefined` when none was published. */
  readonly asset: LipSyncAssetView | undefined;
  /** True when scores exist but no transformed asset does. */
  readonly scoreWithoutAsset: boolean;
  readonly empty: boolean;
}

/** Clamps to `[0, 1]`; anything non-numeric is `undefined`. Pure. */
function clampScore(raw: unknown): number | undefined {
  if (typeof raw !== 'number' || !Number.isFinite(raw)) {
    return undefined;
  }
  if (raw < 0) {
    return 0;
  }
  if (raw > 1) {
    return 1;
  }
  return raw;
}

/**
 * Whether a display value looks like a URL, an endpoint or a filesystem path.
 *
 * Key-shape rules cannot catch these: a provider is perfectly entitled to put
 * its own `baseUrl` in a `method` field, and a value-shaped check is the only
 * rule that survives it. Pure.
 */
function isEndpointShapedValue(value: string): boolean {
  return (
    /^[a-z][a-z0-9+.-]*:\/\//i.test(value) ||
    value.startsWith('/') ||
    value.includes('\\') ||
    /\b\d{1,3}(?:\.\d{1,3}){3}\b/.test(value)
  );
}

/**
 * The method note for a score.
 *
 * The backend's value wins when it is present, safe and endpoint-shaped-free;
 * otherwise the documented fallback. The reason a *clamped* number still
 * renders the fallback is that clamping hides the fact that the upstream value
 * was out of range, and a reader who cannot see that deserves to be told the
 * number is a heuristic.
 */
export function resolveLipSyncMethod(raw: unknown): string {
  const value = text(raw, 60);
  if (value === '' || isEndpointShapedValue(value)) {
    return LIPSYNC_METHOD_FALLBACK;
  }
  return value;
}

/**
 * Parses `GET /projects/{id}/enrichment/lip-sync`.
 *
 * Accepts the array form (`[{segmentId, lipSyncScore}, …]`) and the envelope
 * form. Asset availability is read per row and per document: a payload may
 * publish one transformed file for the whole project, or one per segment, and
 * either satisfies the download. When scores exist and no asset does, the
 * panel renders the score with the download hidden **and a reason** — a
 * silently missing download button is indistinguishable from a bug.
 */
export function parseLipSync(raw: unknown): LipSyncView {
  const document = toRecord(raw);
  const segments: LipSyncSegmentView[] = [];
  const seen = new Set<string>();
  const documentAsset = readAsset(pick(document, 'asset', 'artifact', 'output'));
  let anyAsset = documentAsset !== undefined;

  for (const entry of itemsOf(raw, 'segments', 'items', 'results', 'scores')) {
    const row = toRecord(entry);
    if (row === undefined) {
      continue;
    }
    if (rowCarriesForbiddenKey(row)) {
      continue;
    }
    const segmentId = safeIdFrom(row, 'segmentId', 'segment', 'segment_id');
    if (segmentId === '' || seen.has(segmentId)) {
      continue;
    }
    seen.add(segmentId);
    const rowAsset = readAsset(pick(row, 'asset', 'artifact', 'output'));
    if (rowAsset !== undefined) {
      anyAsset = true;
    }
    const score = clampScore(pick(row, 'lipSyncScore', 'score', 'syncScore'));
    const assetAvailable = (rowAsset ?? documentAsset) !== undefined;
    segments.push({
      segmentId,
      score,
      method: resolveLipSyncMethod(pick(row, 'method', 'methodNote', 'scoreMethod')),
      assetAvailable,
      assetUnavailableReason:
        assetAvailable || score === undefined
          ? ''
          : 'The scored audio is available; the transformed asset was not published.',
    });
  }

  const overallScore = clampScore(pick(document, 'lipSyncScore', 'overallScore', 'score'));
  const overallMethod = resolveLipSyncMethod(pick(document, 'method', 'methodNote', 'scoreMethod'));
  const hasAnyScore = overallScore !== undefined || segments.some((segment) => segment.score !== undefined);
  return {
    segments,
    overallScore,
    overallMethod,
    model: text(pick(document, 'model', 'modelId', 'provider'), 60),
    asset: documentAsset,
    scoreWithoutAsset: hasAnyScore && !anyAsset,
    empty: segments.length === 0 && overallScore === undefined,
  };
}

/**
 * Reads a transformed-asset descriptor. Returns `undefined` when the payload
 * describes no asset — which is a normal, expected state, not an error.
 *
 * Requires a usable `id`: an asset with no id cannot be downloaded, so
 * reporting it as available would produce a button that cannot work.
 */
function readAsset(raw: unknown): LipSyncAssetView | undefined {
  const row = toRecord(raw);
  if (row === undefined) {
    return undefined;
  }
  const id = safeId(row);
  if (id === '') {
    return undefined;
  }
  const sizeRaw = pick(row, 'sizeBytes', 'bytes', 'contentLength');
  return {
    id,
    format: text(pick(row, 'format', 'contentType', 'extension'), 40),
    sizeBytes: typeof sizeRaw === 'number' && Number.isFinite(sizeRaw) && sizeRaw >= 0 ? Math.floor(sizeRaw) : undefined,
  };
}

// --- Segment link resolution (GoneState) ------------------------------------

/**
 * Whether an artifact's link target still exists among the core segments.
 *
 * A video-intel artifact outlives the segment it points at: a reviewer
 * deletes a segment and the artifact row survives in the enrichment payload.
 * Rather than rendering a link into nothing (or dropping the row, which hides
 * data that still exists), the link degrades to an explicit gone marker. The
 * `knownSegmentIds` set is supplied by the caller from the core segment list;
 * an EMPTY set means "no core segment list available", which is treated as
 * unknown-not-gone, because a panel that cannot see segments must not declare
 * them deleted.
 */
export function resolveSegmentLink(
  segmentId: string,
  knownSegmentIds: ReadonlySet<string> | undefined,
): 'linked' | 'gone' | 'unlinked' {
  if (segmentId === '') {
    return 'unlinked';
  }
  if (knownSegmentIds === undefined || knownSegmentIds.size === 0) {
    return 'unlinked';
  }
  return knownSegmentIds.has(segmentId) ? 'linked' : 'gone';
}

/** Human byte size for an enrichment asset, or `undefined`. Pure. */
export function formatEnrichmentFileSize(sizeBytes: number | undefined): string | undefined {
  if (sizeBytes === undefined || !Number.isFinite(sizeBytes) || sizeBytes < 0) {
    return undefined;
  }
  const units = ['B', 'KB', 'MB', 'GB'];
  let value = Math.floor(sizeBytes);
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value = Math.floor(value / 1024);
    unit += 1;
  }
  return `${String(value)} ${String(units[unit] ?? 'B')}`;
}

/** Two-decimal score text, or `—` for a missing score. Pure. */
export function formatLipSyncScore(score: number | undefined): string {
  return score === undefined ? '—' : score.toFixed(2);
}