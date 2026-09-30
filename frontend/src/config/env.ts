import { z } from 'zod';
import { getEnv } from '../lib/env.js';

// Safe config injection (Task 043, instruction 2).
//
// WHAT THIS FILE IS
// -----------------
// The single reader for RUNTIME-READ configuration: the values that are baked
// into the bundle at build time, plus the `/version.json` document the CDN
// serves. `lib/env.ts` remains the only module allowed to touch
// `import.meta.env`; this one composes it and adds the runtime version, so the
// ESLint exemption stays a single line.
//
// WHY THE INJECTION IS ALLOWLISTED, NOT BLACKLISTED
// -------------------------------------------------
// The threat is a secret in the browser bundle. The bundle is public: it is
// downloadable by anyone who can load the page, and a CDN caches it, so a
// secret in `VITE_*` is a published secret. Vite inlines every `VITE_*` value
// into the JavaScript at build time with no warning and no way to un-inline it.
//
// A blacklist is the wrong shape for that. Blocking a fixed list of words
// (`SECRET`, `KEY`, `TOKEN`, ...) fails the moment somebody names a variable
// `VITE_DB_CRED` or `VITE_SIGNER`, and a name you did not think of is exactly
// the case that reaches production. An ALLOWLIST cannot fail that way: a
// variable that is not named in `DEPLOY_CONFIG_ALLOWLIST` is refused, whether
// or not its name looks dangerous. The blacklist is kept as a SECOND,
// independent check by `scripts/vite-env-audit.sh`, because two rules that fail
// in different directions catch more than one rule that fails in one.
//
// WHAT IS ALLOWED, AND WHY EACH ONE IS SAFE
// -----------------------------------------
//   VITE_API_BASE_URL    where the API is. A URL, not a credential.
//   VITE_CDN_ORIGIN      the CDN origin the client may load from. An origin, so
//                        it can be pinned in `connect-src` without a wildcard.
//   VITE_VERSION_TAG     the build tag. Identifies the bundle, grants nothing.
//   VITE_APP_VERSION     the semantic version shown in the footer.
//   VITE_SSE_ENABLED     a boolean switch, already required by `lib/env.ts`.
//   VITE_TELEMETRY_ENABLED ditto.
//
// The committed `frontend/.env.example` is generated FROM this list, so the
// example file and the allowlist cannot drift into disagreeing about what a
// safe variable is.

/**
 * The complete set of build-time variables allowed into the public bundle.
 * Order is the order they appear in `.env.example`; it is asserted by tests so a
 * reordering cannot silently change the generated example.
 */
export const DEPLOY_CONFIG_ALLOWLIST = [
  'VITE_API_BASE_URL',
  'VITE_CDN_ORIGIN',
  'VITE_VERSION_TAG',
  'VITE_APP_VERSION',
  'VITE_SSE_ENABLED',
  'VITE_TELEMETRY_ENABLED',
] as const;

export type DeployConfigKey = (typeof DEPLOY_CONFIG_ALLOWLIST)[number];

/** The path the CDN serves the deployed build's identity from. */
export const VERSION_JSON_PATH = '/version.json';

/** The path the API reports its own identity from. R2 requires the two to agree. */
export const API_VERSION_PATH = '/version';

/**
 * Why a key was refused. A closed set, so a CI step can branch on the reason
 * rather than parse prose, and so "it was blocked" is never a single opaque
 * boolean an operator has to reverse-engineer.
 */
export type ConfigRejectionReason =
  | 'NOT_ALLOWLISTED'
  | 'SECRET_SHAPED_NAME'
  | 'SECRET_SHAPED_VALUE'
  | 'EMPTY_VALUE'
  | 'UNSAFE_URL';

export interface ConfigRejection {
  readonly key: string;
  readonly reason: ConfigRejectionReason;
  readonly detail: string;
}

export interface EnvAuditResult {
  readonly ok: boolean;
  readonly accepted: readonly string[];
  readonly rejections: readonly ConfigRejection[];
}

/**
 * Substrings that make a NAME secret-shaped. Applied to allowlisted names too:
 * the allowlist says a name is permitted, this says a name is forbidden, and a
 * name that is both is a contradiction that must be resolved by removing it
 * from the allowlist rather than by choosing which rule wins.
 */
export const SECRET_NAME_MARKERS = [
  'SECRET',
  'KEY',
  'TOKEN',
  'PASSWORD',
  'PASSWD',
  'CREDENTIAL',
  'PRIVATE',
  'SIGNING',
  'PASSPHRASE',
] as const;

/**
 * Value shapes that are secret-shaped regardless of the variable's name.
 *
 * Entropy alone is deliberately NOT one of them. A hashed asset filename is
 * high-entropy, a CDN origin is not, and a rule that flags entropy fails on
 * every build output it is pointed at. These are shapes that only appear in
 * credential material.
 */
export const SECRET_VALUE_PATTERNS: readonly { id: string; re: RegExp; label: string }[] = [
  // A PEM block of any kind. The private-key variants are the dangerous ones;
  // a public key is not a secret, but shipping either from a build arg is a
  // mistake worth stopping.
  { id: 'pem-block', re: /-----BEGIN [A-Z ]*PRIVATE KEY-----/, label: 'a PEM private key block' },
  { id: 'jwt', re: /\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b/, label: 'a JWT' },
  // A PostgreSQL/Npgsql/RabbitMQ connection string. The password may be absent
  // and the string still leaks the topology, which `docs/topology.md` treats as
  // sensitive.
  { id: 'connection-string', re: /\b(?:Host|Server|Data Source)\s*=\s*[^;\s]+;[^;]*\b(?:Password|Pwd)\s*=/i, label: 'a connection string with a password' },
  { id: 'rabbit-uri', re: /\bamqps?:\/\/[^\s:@/]+:[^\s:@/]+@/, label: 'a broker URI with inline credentials' },
  { id: 's3-uri', re: /\bs3(?:a)?:\/\/[^\s:@/]+:[^\s:@/]+@/, label: 'an object-storage URI with inline credentials' },
  // `password=hunter2` in any query string or form body, which is how a secret
  // arrives when it was not in a connection string.
  { id: 'inline-credential', re: /[?&;](?:password|pwd|secret|token|api[-_]?key|signature|sig)\s*=/i, label: 'an inline credential parameter' },
  // AWS-style access key id. 20 characters, uppercase alnum, and it is a
  // recognisable shape rather than a guess.
  { id: 'aws-access-key', re: /\b(?:AKIA|ASIA)[0-9A-Z]{16}\b/, label: 'an AWS access key id' },
  // A bearer credential in a header-shaped string.
  { id: 'bearer-credential', re: /\bBearer\s+[A-Za-z0-9._~+/-]{20,}=*/, label: 'a bearer credential' },
];

/** Whether a variable name is secret-shaped. Pure. */
export function isSecretShapedName(name: string): boolean {
  const upper = name.toUpperCase();
  return SECRET_NAME_MARKERS.some((marker) => upper.includes(marker));
}

/** Which secret value pattern a value matches, or `null`. Pure. */
export function secretValueReason(value: string): { id: string; label: string } | null {
  for (const pattern of SECRET_VALUE_PATTERNS) {
    if (pattern.re.test(value)) {
      return { id: pattern.id, label: pattern.label };
    }
  }
  return null;
}

/**
 * Whether a value is an absolute http(s) URL, or the empty string (which means
 * "not set" and is handled separately). Pure.
 *
 * A `javascript:` or `data:` value in `VITE_API_BASE_URL` would be executed by
 * the browser on the first fetch, and a protocol-relative `//host` would resolve
 * against the CDN's own origin. Both are configuration mistakes that are
 * indistinguishable from an attack once baked.
 */
export function isSafeUrlValue(value: string): boolean {
  if (value.length === 0) {
    return true;
  }
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    return false;
  }
  return parsed.protocol === 'https:' || parsed.protocol === 'http:';
}

/**
 * Audits a candidate set of build-time variables against the allowlist and the
 * secret rules. Pure: no filesystem, no network, no environment access.
 *
 * Deliberately audits the *values it is given*, not `import.meta.env`. A gate
 * that reads the ambient environment audits whatever happens to be exported on
 * the machine running it, which is not what the published bundle contains.
 *
 * `allowlist` is a parameter rather than a closed-over constant so the
 * "allowlisted AND secret-shaped" branch is reachable from a test. With the real
 * list that branch is dead code — no allowlisted key matches a marker, which is
 * exactly why the marker rule has to be tested against a *hypothetical* list.
 * Testing it against the real one would assert only that today's list is clean,
 * which is a statement about the data, not about the rule.
 */
export function auditDeployConfig(
  entries: readonly (readonly [string, string])[],
  allowlist: readonly string[] = DEPLOY_CONFIG_ALLOWLIST,
): EnvAuditResult {
  const rejections: ConfigRejection[] = [];
  const accepted: string[] = [];

  for (const [key, value] of entries) {
    if (!allowlist.includes(key)) {
      rejections.push({
        key,
        reason: 'NOT_ALLOWLISTED',
        detail:
          `${key} is not in DEPLOY_CONFIG_ALLOWLIST. Every VITE_* value is inlined into the public bundle by Vite, so the allowlist is the only thing standing between a configuration value and a published one. Add the key to the allowlist only if the value is safe for anyone who can load the page to read.`,
      });
      continue;
    }

    if (isSecretShapedName(key)) {
      rejections.push({
        key,
        reason: 'SECRET_SHAPED_NAME',
        detail: `${key} is allowlisted but its name matches ${SECRET_NAME_MARKERS.join('/')}. A name that is both permitted and forbidden is a contradiction: remove it from the allowlist and decide what the real, non-secret variable is called.`,
      });
      continue;
    }

    if (value.trim().length === 0) {
      rejections.push({
        key,
        reason: 'EMPTY_VALUE',
        detail: `${key} is set to an empty value. An empty injected value is worse than an absent one: it passes a "is it configured?" check while producing a request to no origin at runtime.`,
      });
      continue;
    }

    const secret = secretValueReason(value);
    if (secret !== null) {
      rejections.push({
        key,
        reason: 'SECRET_SHAPED_VALUE',
        detail: `${key} has a value matching ${secret.label}. Whatever it is, it must not be baked into a public bundle: rotate it if it is a real credential, and find the server-side path for it.`,
      });
      continue;
    }

    if ((key === 'VITE_API_BASE_URL' || key === 'VITE_CDN_ORIGIN') && !isSafeUrlValue(value)) {
      rejections.push({
        key,
        reason: 'UNSAFE_URL',
        detail: `${key} must be an absolute http(s) URL. A javascript:, data: or protocol-relative value here executes in the browser or silently repoints at the CDN origin.`,
      });
      continue;
    }

    accepted.push(key);
  }

  return { ok: rejections.length === 0, accepted, rejections };
}

/**
 * Renders the committed `.env.example` from the allowlist. Pure, and used by
 * `deploy/config-inject.sh` and by a test so the file and the list cannot drift.
 */
export function renderEnvExample(values: Readonly<Record<string, string>>): string {
  const lines = [
    '# Public frontend environment.',
    '#',
    '# GENERATED FROM DEPLOY_CONFIG_ALLOWLIST (frontend/src/config/env.ts). Do not add a key',
    '# here that is not in that list: Vite inlines every VITE_* value into the public',
    '# bundle, and a key that is not allowlisted is a value nobody has decided is safe.',
    '# Back-end secrets, connection strings and signing keys never belong in this file.',
    '#',
    '# Local defaults. Per-environment values are injected by',
    '# `deploy/config-inject.sh`, which writes `frontend/.env.production`.',
  ];
  for (const key of DEPLOY_CONFIG_ALLOWLIST) {
    const value = values[key];
    if (value !== undefined) {
      lines.push(`${key}=${value}`);
    }
  }
  return `${lines.join('\n')}\n`;
}

/** The `/version.json` document the CDN serves. */
export interface RuntimeVersion {
  /** The build tag, e.g. `v1.4.2`. Must equal `/version`'s `release`. */
  readonly release: string;
  /** Full commit sha. Must equal `/version`'s `commit`. */
  readonly commit: string;
  /** The API's OpenAPI `info.version`. Must equal `/version`'s `openapiVersion`. */
  readonly openapiVersion: string;
  /** ISO-8601 UTC build timestamp. Informational. */
  readonly builtAtUtc: string;
}

const runtimeVersionSchema = z.object({
  release: z.string().min(1),
  commit: z.string().min(1),
  openapiVersion: z.string().min(1),
  builtAtUtc: z.string().min(1),
});

/**
 * Parses `/version.json`. Pure.
 *
 * A malformed document is an error rather than a default, because the whole
 * point of comparing it against the build tag is to notice that the CDN is
 * serving something other than what the user is running. Defaulting would turn
 * a detectable skew into a silent one.
 */
export function parseRuntimeVersion(raw: unknown): RuntimeVersion {
  const parsed = runtimeVersionSchema.safeParse(raw);
  if (!parsed.success) {
    const issues = parsed.error.issues.map((issue) => `${issue.path.join('.') || '(root)'}: ${issue.message}`);
    throw new Error(`Malformed ${VERSION_JSON_PATH}: ${issues.join('; ')}`);
  }
  return parsed.data;
}

export type VersionMatch = 'MATCH' | 'MISMATCH' | 'UNKNOWN';

/**
 * Compares the baked build tag against what the CDN is serving. Pure.
 *
 * `UNKNOWN` is a first-class outcome, not a special case of `MATCH`. The edge
 * case this exists for is "the CDN is serving stale HTML after a deploy": the
 * user's bundle is one release old, the assets it references were replaced, and
 * the symptom is a white screen on navigation. That is only detectable by
 * comparing, and the comparison is only meaningful if "could not tell" is a
 * third state rather than folded into agreement.
 */
export function compareVersions(bakedTag: string, served: RuntimeVersion | null): VersionMatch {
  if (served === null) {
    return 'UNKNOWN';
  }
  const baked = bakedTag.trim();
  const servedTag = served.release.trim();
  if (baked.length === 0 || servedTag.length === 0) {
    return 'UNKNOWN';
  }
  return baked === servedTag ? 'MATCH' : 'MISMATCH';
}

/** The full public configuration, as the application sees it. */
export interface DeployConfig {
  readonly apiBaseUrl: string;
  readonly cdnOrigin: string;
  readonly versionTag: string;
  readonly appVersion: string;
  readonly sseEnabled: boolean;
  readonly telemetryEnabled: boolean;
}

let cachedConfig: DeployConfig | undefined;

/**
 * The public configuration, read once and validated.
 *
 * `VITE_CDN_ORIGIN` is optional: a local `vite dev` server and the cross-layer
 * rig have no CDN, and a required variable would mean the local workflow carries
 * a fake origin. It falls back to the API origin, which is the correct default
 * for a single-origin local build and is the value the CSP `connect-src` already
 * permits.
 */
export function getDeployConfig(): DeployConfig {
  if (cachedConfig === undefined) {
    const env = getEnv();
    // The two hosting variables are read here rather than through `getEnv()`.
    // `lib/env.ts` is the validation boundary for the variables the application
    // makes requests with; resolving "unset means fall back to the API origin"
    // is this module's job, and duplicating the schema would create two places
    // to change when a variable is added.
    const raw = import.meta.env as unknown as Record<string, string | undefined>;
    const cdnOrigin = (raw['VITE_CDN_ORIGIN'] ?? '').trim() || env.apiBaseUrl;
    const versionTag = (raw['VITE_VERSION_TAG'] ?? '').trim() || env.appVersion;

    const result = auditDeployConfig([
      ['VITE_API_BASE_URL', env.apiBaseUrl],
      ['VITE_CDN_ORIGIN', cdnOrigin],
      ['VITE_VERSION_TAG', versionTag],
      ['VITE_APP_VERSION', env.appVersion],
      ['VITE_SSE_ENABLED', String(env.sseEnabled)],
      ['VITE_TELEMETRY_ENABLED', String(env.telemetryEnabled)],
    ]);
    if (!result.ok) {
      throw new Error(
        `Invalid frontend configuration: ${result.rejections.map((rejection) => rejection.detail).join(' ')}`,
      );
    }

    cachedConfig = {
      apiBaseUrl: env.apiBaseUrl,
      cdnOrigin,
      versionTag,
      appVersion: env.appVersion,
      sseEnabled: env.sseEnabled,
      telemetryEnabled: env.telemetryEnabled,
    };
  }
  return cachedConfig;
}

/** Test-only reset for the module cache. Never used in production code. */
export function resetDeployConfigCache(): void {
  cachedConfig = undefined;
}

/**
 * Fetches `/version.json` and compares it to the baked build tag.
 *
 * Two failure modes are separated deliberately, because the caller responds to
 * them differently: a `MISMATCH` means the user is on a stale bundle and should
 * be told to reload, while a thrown error means the document could not be read
 * (offline, a proxy that strips it, a 404 because the deploy predates this
 * file) and there is nothing to tell the user.
 */
export async function checkServedVersion(
  fetchImpl: typeof fetch,
  bakedTag: string,
  signal?: AbortSignal,
): Promise<{ match: VersionMatch; served: RuntimeVersion | null }> {
  let served: RuntimeVersion | null = null;
  try {
    const response = await fetchImpl(VERSION_JSON_PATH, {
      cache: 'no-store',
      credentials: 'omit',
      signal,
    });
    if (response.ok) {
      served = parseRuntimeVersion(await response.json());
    }
  } catch {
    // A version check that cannot reach the CDN is not a version mismatch. The
    // caller shows nothing rather than a banner that is wrong.
    served = null;
  }
  return { match: compareVersions(bakedTag, served), served };
}
