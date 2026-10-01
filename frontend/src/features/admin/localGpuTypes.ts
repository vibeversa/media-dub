/**
 * Operator-only local-GPU health domain views (Task 044, §19.3, R4).
 *
 * WHY THIS LIVES IN `features/admin/` AND NOT IN `features/enrichment/`
 * ---------------------------------------------------------------
 * The local-GPU panel is an ADMIN surface: it is mounted inside the Task 036
 * role-gated Admin section and reports infrastructure inventory. It began life
 * in `features/enrichment/` because it shares an enrichment flag, and that was
 * the wrong home for a structural reason rather than an aesthetic one: the
 * admin route chunk would then have imported three enrichment modules
 * (parsers, query hook, state components), which is exactly what the R6 import
 * gate is there to prevent — and it would have made the gate's rule "no
 * enrichment module in any chunk" unachievable without an exemption for the
 * admin chunk, i.e. without weakening the gate to accommodate the code.
 *
 * So the operator surface is self-contained here. `features/enrichment/` is
 * then purely user-facing and its modules are reachable only from the media
 * route, which is what the gate asserts. The two surfaces share the FLAG
 * (`enrichmentFlags.ts`) and nothing else, which is the right amount of sharing:
 * the flag decides whether a surface exists, and a surface decides what it
 * shows.
 *
 * SECRET-FREE BY CONSTRUCTION
 * ---------------------------
 * Every field here is operator-only material (accelerator, model revision,
 * device count, latency) and none of it is a credential. The parser is built as
 * if it were one anyway: endpoint/host/credential-shaped keys are dropped,
 * because a local-inference sidecar's health document routinely contains its
 * own `baseUrl`, `model` path and API key, and none of that belongs on a page
 * an ordinary user can reach.
 */

import { isForbiddenAdminKey } from './types.js';

export type LocalGpuStatus = 'Healthy' | 'Degraded' | 'Down' | 'Unknown';

export interface LocalGpuView {
  readonly provider: string;
  readonly status: LocalGpuStatus;
  readonly model: string;
  readonly modelVersion: string;
  readonly device: string;
  readonly latencyMsP95: number | undefined;
  readonly lastSuccessAt: string;
  readonly deviceCount: number | undefined;
  /** True when the payload described no device at all. */
  readonly empty: boolean;
}

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
 * Keys an operator health document must never contribute, beyond the shared
 * admin secret families.
 *
 * These are the shapes a sidecar health payload actually contains — its own
 * `baseUrl`, a filesystem path to a checkpoint, a webhook. The task's security
 * section restricts GPU/device detail to the elevated role; a payload key is
 * not rendered, and a check that only covered `apiKey` would let `endpoint`
 * through.
 */
const OPERATOR_ONLY_FORBIDDEN_KEYS: readonly string[] = [
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

/** Whether an operator health payload key must never render. Pure. */
export function isForbiddenLocalGpuKey(key: string): boolean {
  if (isForbiddenAdminKey(key)) {
    return true;
  }
  const flat = key.toLowerCase().replace(/[^a-z0-9]/g, '');
  return OPERATOR_ONLY_FORBIDDEN_KEYS.some((fragment) => flat.includes(fragment));
}

/**
 * Normalizes a provider status string.
 *
 * Unrecognised values resolve to `Unknown`, never to `Healthy`. An operator
 * panel that renders a new provider status word as good news is the specific
 * way this panel could make things worse than having no panel — the same
 * failure the Task 043 runbooks keep warning about with stale dashboards.
 * Pure.
 */
export function resolveLocalGpuStatus(raw: unknown): LocalGpuStatus {
  const value = text(raw, 40).toLowerCase();
  if (value === 'healthy' || value === 'ok' || value === 'ready' || value === 'up') {
    return 'Healthy';
  }
  if (value === 'degraded' || value === 'warning') {
    return 'Degraded';
  }
  if (value === 'down' || value === 'unhealthy' || value === 'failed') {
    return 'Down';
  }
  return 'Unknown';
}

/** Parses the operator local-GPU health document. Pure; never throws. */
export function parseLocalGpu(raw: unknown): LocalGpuView {
  const document = toRecord(raw) ?? {};
  // Accept the document itself or a `localGpu` / `device` envelope, but only
  // when that key holds an OBJECT — `device: 'cuda:0'` is a device name, not a
  // nested document, and descending into it would erase the field.
  const nested = toRecord(pick(document, 'localGpu', 'device', 'gpu'));
  const row = nested ?? document;
  const latencyRaw = pick(row, 'latencyMsP95', 'latencyMs', 'p95LatencyMs');
  const deviceCountRaw = pick(row, 'deviceCount', 'gpuCount');
  const providerRaw = text(pick(row, 'provider', 'name'), 60);
  const modelRaw = text(pick(row, 'model', 'modelId'), 60);
  const modelVersion = text(pick(row, 'modelVersion', 'version', 'revision'), 40);
  const device = text(pick(row, 'device', 'deviceProfile', 'accelerator'), 60);
  return {
    provider: isForbiddenLocalGpuKey(providerRaw) ? '' : providerRaw,
    status: resolveLocalGpuStatus(pick(row, 'status', 'state')),
    model: isForbiddenLocalGpuKey(modelRaw) ? '' : modelRaw,
    modelVersion,
    device,
    latencyMsP95:
      typeof latencyRaw === 'number' && Number.isFinite(latencyRaw) && latencyRaw >= 0 ? Math.round(latencyRaw) : undefined,
    lastSuccessAt: text(pick(row, 'lastSuccessAt', 'lastSeenAt', 'observedAt'), 40),
    deviceCount:
      typeof deviceCountRaw === 'number' && Number.isFinite(deviceCountRaw) && deviceCountRaw > 0
        ? Math.floor(deviceCountRaw)
        : undefined,
    // "Empty" means the payload described no device at all. It is what turns a
    // silent-but-200 answer into `UnknownState` rather than an empty card.
    empty: providerRaw === '' && modelRaw === '' && modelVersion === '' && device === '',
  };
}