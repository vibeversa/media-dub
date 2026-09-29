#!/usr/bin/env node
// Contract-compatibility analysis (Task 042).
//
// WHY THIS FILE EXISTS SEPARATELY FROM THE SHELL DRIVER
// ------------------------------------------------------
// `scripts/openapi-diff.sh` is the gate: it resolves the two documents, installs
// a checksum-pinned oasdiff, runs it, and turns its output into GitHub
// annotations. That is all I/O and it is hard to test honestly.
//
// The two *decisions* the task requires are pure functions of two parsed JSON
// documents, and they are the parts that can silently become useless:
//
//   1. "did the contract change at all?" - so `openapi:version` knows when a
//      version bump is owed. A naive byte comparison makes every reflow of a
//      description a version bump, which trains people to bump versions
//      reflexively and destroys the signal the check exists to carry.
//   2. "is the version bump real?" - so a bump that does not accompany a
//      change, or that does not accompany a breaking change, is visible.
//
// So they live here, hermetic (Node stdlib only, no network, no clock, no
// filesystem) and are unit-tested in `openapi-compat.test.mjs`.
// `scripts/openapi-diff.sh` never re-implements them.
import { createHash } from 'node:crypto';

const HTTP_METHODS = ['get', 'put', 'post', 'delete', 'patch', 'options', 'head', 'trace'];

/**
 * Resolves the security requirements that actually apply to one operation.
 *
 * An operation's own `security` REPLACES the document-level `security`; it does
 * not extend it. That rule is the whole reason this cannot be a diff of two
 * fields: reading them as "base scopes plus head scopes" inverts the meaning of
 * every operation that overrides the document default, which is most of them in
 * this bundle (the anonymous health and OpenAPI routes).
 *
 * @param {object} doc an OpenAPI document
 * @param {string} pathKey the path template
 * @param {string} method a lower-case HTTP method
 * @returns {Map<string, string[]>} scheme name -> required scopes, sorted
 */
export function effectiveSecurity(doc, pathKey, method) {
  const operation = doc?.paths?.[pathKey]?.[method];
  if (operation !== undefined && Array.isArray(operation.security)) {
    return securityMap(operation.security);
  }
  if (Array.isArray(doc?.security)) {
    return securityMap(doc.security);
  }
  return new Map();
}

function securityMap(requirements) {
  const map = new Map();
  for (const requirement of requirements) {
    if (requirement === null || typeof requirement !== 'object') {
      continue;
    }
    for (const [scheme, scopes] of Object.entries(requirement)) {
      // An alternative requirement for the same scheme is a disjunction, not a
      // union. Unioning them would report "no new scope" for a change that
      // actually removed an authentication path.
      const list = Array.isArray(scopes) ? [...scopes].map(String).sort() : [];
      if (map.has(scheme)) {
        const existing = map.get(scheme);
        map.set(scheme, [...new Set([...existing, ...list])].sort());
      } else {
        map.set(scheme, list);
      }
    }
  }
  return map;
}

/**
 * Operations whose effective security became STRICTER.
 *
 * This is implemented here rather than left to oasdiff because the pinned tool
 * does not report it. Verified against oasdiff v1.11.7: adding
 * `security: [{Bearer: ["projects.read"]}]` to an operation that previously
 * inherited the document-level `[{Bearer: []}]` produces `[]` - no breaking
 * change. That is a real hole, and the task names the case explicitly ("changed
 * auth scope"), so the gate does not depend on the tool noticing.
 *
 * A relaxation (a scope removed, a scheme added as an alternative, `security`
 * cleared) is deliberately NOT reported as breaking: it does not break a client
 * that already authenticates. It is a security-relevant change and a reviewer
 * should see it, so it comes back as a separate list rather than being dropped.
 *
 * @param {object} base the base document
 * @param {object} head the head document
 * @returns {{ breaking: Array<object>, relaxed: Array<object> }}
 */
export function authScopeChanges(base, head) {
  const breaking = [];
  const relaxed = [];
  for (const pathKey of new Set([...Object.keys(base?.paths ?? {}), ...Object.keys(head?.paths ?? {})])) {
    for (const method of HTTP_METHODS) {
      const inBase = base?.paths?.[pathKey]?.[method] !== undefined;
      const inHead = head?.paths?.[pathKey]?.[method] !== undefined;
      // A wholly added or wholly removed operation is already reported by
      // oasdiff; re-reporting it here would double every annotation.
      if (inBase !== inHead) {
        continue;
      }
      if (!inBase) {
        continue;
      }
      const label = `${method.toUpperCase()} ${pathKey}`;
      const before = effectiveSecurity(base, pathKey, method);
      const after = effectiveSecurity(head, pathKey, method);

      for (const [scheme, scopes] of after) {
        const previous = before.get(scheme);
        if (previous === undefined) {
          // The scheme was not required at all before. That is only a break if
          // it replaced the previous requirements; a second alternative is a
          // relaxation.
          if (before.size === 0) {
            breaking.push({ operation: label, detail: `now requires '${scheme}', which the base document did not require at all` });
          } else {
            relaxed.push({ operation: label, detail: `adds '${scheme}' as an alternative authentication path` });
          }
          continue;
        }
        const added = scopes.filter((scope) => !previous.includes(scope));
        if (added.length > 0) {
          breaking.push({ operation: label, detail: `now requires ${scheme} scope(s) ${JSON.stringify(added)}, which the base did not` });
        }
        const removed = previous.filter((scope) => !scopes.includes(scope));
        if (removed.length > 0) {
          relaxed.push({ operation: label, detail: `drops ${scheme} scope(s) ${JSON.stringify(removed)}` });
        }
      }
      for (const [scheme, scopes] of before) {
        if (!after.has(scheme)) {
          breaking.push({
            operation: label,
            detail: `no longer accepts '${scheme}'${scopes.length > 0 ? ` with scope(s) ${JSON.stringify(scopes)}` : ''}; a client authenticating that way is now rejected`,
          });
        }
      }
    }
  }
  return { breaking, relaxed };
}

/** Exit reasons this module can produce. The shell driver maps them to exit codes. */
export const REASON = {
  OK: 'OK',
  BREAKING: 'CONTRACT_BREAKING',
  VERSION_BUMP_REQUIRED: 'CONTRACT_VERSION_BUMP_REQUIRED',
  VERSION_INVALID: 'CONTRACT_VERSION_INVALID',
  VERSION_REGRESSED: 'CONTRACT_VERSION_REGRESSED',
  MAJOR_WITHOUT_ROUTE: 'CONTRACT_MAJOR_WITHOUT_ROUTE',
  ROUTE_WITHOUT_MAJOR: 'CONTRACT_ROUTE_WITHOUT_MAJOR',
  DIFF_UNREADABLE: 'CONTRACT_DIFF_UNREADABLE',
};

/**
 * Keys that carry documentation rather than contract.
 *
 * Removing them is what makes "the contract changed" mean "the *machine-visible*
 * surface changed". `info.description` on this bundle is a 2 kB paragraph; a
 * typo fix in it must not demand a version bump. Everything not listed here is
 * treated as contract, including `x-` extensions, because an `x-` extension can
 * be a validation constraint that clients honour.
 */
const DOC_KEYS = new Set(['description', 'summary', 'example', 'examples', 'externalDocs', 'deprecated']);

/**
 * `info.version` is deliberately NOT part of the contract projection.
 *
 * This is the difference between a gate that means something and one that
 * cannot fail. If the version field counted as a contract change, then
 * "the contract changed but the version did not move" would be unsatisfiable -
 * bumping the version would itself register as the change that the bump was
 * required by. The version is the *answer* to the question, so it cannot also be
 * an input to it.
 *
 * @param {unknown} value any JSON node
 * @param {boolean} insideInfo true when the node is the `info` object
 * @returns {unknown} the canonical projection of the node
 */
function canonicalNode(value, insideInfo = false) {
  if (Array.isArray(value)) {
    return value.map((item) => canonicalNode(item, false));
  }
  if (value === null || typeof value !== 'object') {
    return value;
  }
  const out = {};
  for (const key of Object.keys(value).sort()) {
    if (DOC_KEYS.has(key)) {
      continue;
    }
    if (insideInfo && key === 'version') {
      continue;
    }
    out[key] = canonicalNode(value[key], key === 'info');
  }
  return out;
}

/**
 * Deterministic, documentation-free projection of a document.
 *
 * Object keys are sorted so key order cannot read as a change; arrays keep their
 * order, because array order is meaningful for `required`, `enum`, `allOf` and
 * `parameters` (a reordered `required` list is a client-visible change and
 * `openapi-typescript-codegen` emits it in order).
 *
 * @param {unknown} value any JSON node
 * @returns {string} a stable serialization
 */
export function canonicalContract(value) {
  return JSON.stringify(canonicalNode(value));
}

/**
 * True when anything a client can bind to changed between the two documents.
 *
 * @param {object} base the `main` document
 * @param {object} head the pull-request document
 */
export function contractChanged(base, head) {
  return canonicalContract(base) !== canonicalContract(head);
}

/**
 * @param {object} doc an OpenAPI document
 * @returns {unknown} `info.version`, or undefined when the document omits it
 */
export function documentVersion(doc) {
  return doc?.info?.version;
}

/**
 * Parses `info.version` into comparable parts.
 *
 * Accepts `v1`, `1`, `1.2`, `v1.2.3` - the shapes this repository and the
 * OpenAPI ecosystem actually use. A version that does not parse is a *failure*,
 * not a warning: an unparseable version cannot be compared, so the bump rule
 * would silently degrade into "no opinion".
 *
 * @param {unknown} raw the raw `info.version`
 * @returns {{ major: number, minor: number, patch: number, raw: string }|null}
 */
export function parseVersion(raw) {
  if (typeof raw !== 'string') {
    return null;
  }
  const match = /^v?(\d+)(?:\.(\d+))?(?:\.(\d+))?$/.exec(raw.trim());
  if (match === null) {
    return null;
  }
  return {
    major: Number(match[1]),
    minor: match[2] === undefined ? 0 : Number(match[2]),
    patch: match[3] === undefined ? 0 : Number(match[3]),
    raw: raw.trim(),
  };
}

/**
 * Compares two versions numerically, not lexically.
 *
 * Lexical comparison is the bug this exists to prevent: `"v10"` sorts before
 * `"v9"` as a string, so a tenth API line would read as a downgrade.
 *
 * @param {string} a
 * @param {string} b
 * @returns {-1|0|1|null} null when either side is unparseable
 */
export function compareVersions(a, b) {
  const left = parseVersion(a);
  const right = parseVersion(b);
  if (left === null || right === null) {
    return null;
  }
  const leftParts = [left.major, left.minor, left.patch];
  const rightParts = [right.major, right.minor, right.patch];
  for (let i = 0; i < leftParts.length; i += 1) {
    if (leftParts[i] !== rightParts[i]) {
      return leftParts[i] < rightParts[i] ? -1 : 1;
    }
  }
  return 0;
}

/**
 * The next version to suggest when a bump is owed.
 *
 * An unchanged-major bump goes to the next *minor* (`v1` -> `v1.1`), because an
 * additive change does not get a new API line. A breaking change suggests the
 * next major, which is also the only bump that is legal here: a new major
 * implies a new `/api/vN` route prefix, and the route check enforces the pair.
 *
 * @param {string} current the base version
 * @param {boolean} breaking whether oasdiff found a breaking change
 * @returns {string|null} the suggested version, or null when unparseable
 */
export function suggestVersion(current, breaking) {
  const parsed = parseVersion(current);
  if (parsed === null) {
    return null;
  }
  if (breaking) {
    return `v${parsed.major + 1}`;
  }
  if (parsed.raw.startsWith('v') || parsed.raw.startsWith('V')) {
    return `${parsed.raw[0]}${parsed.major}.${parsed.minor + 1}`;
  }
  return `${parsed.major}.${parsed.minor + 1}`;
}

/**
 * The major of the versioned route prefix declared by a document's `servers`
 * entry (`/api/v1` -> 1). Null when the document declares no server, a
 * non-versioned url, or a relative url with no trailing version segment.
 *
 * @param {object} doc an OpenAPI document
 * @returns {number|null}
 */
export function routeMajor(doc) {
  const url = Array.isArray(doc?.servers) && doc.servers.length > 0 ? doc.servers[0]?.url : undefined;
  if (typeof url !== 'string') {
    return null;
  }
  const match = /\/v(\d+)$/.exec(url.replace(/\/+$/, ''));
  return match === null ? null : Number(match[1]);
}

/**
 * The `openapi:version` policy, evaluated over both documents.
 *
 * Five outcomes, and each one names the pair that caused it so the annotation
 * can be acted on without opening the diff:
 *
 *   `unchanged` - the contract is byte-identical once documentation is removed.
 *   `bumped`    - the contract changed and the version moved forward.
 *   `required`  - the contract changed and the version did not move. This is
 *                 the failure the task asks for.
 *   `invalid`   - either side is not a parseable version.
 *   `regressed` - the version moved backwards, which is worse than not moving
 *                 at all: it makes every previously published bundle look
 *                 newer than this one.
 *
 * A version that moves while the contract is identical is reported as a
 * `spurious` warning, not a failure. It is almost always an accident, but it is
 * not a compatibility hazard, and a gate that blocks on it teaches people to
 * reach for `--no-verify`.
 *
 * @param {object} base the `main` document
 * @param {object} head the pull-request document
 * @param {{ breaking?: boolean }} [options]
 */
export function evaluateVersionPolicy(base, head, options = {}) {
  const baseVersion = documentVersion(base);
  const headVersion = documentVersion(head);
  const changed = contractChanged(base, head);

  const result = {
    baseVersion: baseVersion ?? null,
    headVersion: headVersion ?? null,
    changed,
    breaking: options.breaking === true,
    status: REASON.OK,
    annotations: [],
  };

  if (parseVersion(baseVersion) === null || parseVersion(headVersion) === null) {
    result.status = REASON.VERSION_INVALID;
    result.annotations.push(
      `info.version must look like v1, v1.2 or v1.2.3 (base=${JSON.stringify(baseVersion)}, head=${JSON.stringify(headVersion)}).`,
    );
    return result;
  }

  const order = compareVersions(baseVersion, headVersion);
  if (order === null) {
    result.status = REASON.VERSION_INVALID;
    return result;
  }
  if (order > 0) {
    result.status = REASON.VERSION_REGRESSED;
    result.annotations.push(
      `info.version moved backwards: ${baseVersion} -> ${headVersion}. Downgrading makes every published bundle look newer than this one.`,
    );
    return result;
  }

  if (!changed) {
    if (order < 0) {
      result.status = 'spurious';
      result.annotations.push(
        `info.version moved ${baseVersion} -> ${headVersion} but the contract is unchanged (documentation-only edits are excluded). Drop the bump or make the change.`,
      );
    }
    return result;
  }

  if (order === 0) {
    result.status = REASON.VERSION_BUMP_REQUIRED;
    const suggestion = suggestVersion(baseVersion, result.breaking);
    result.annotations.push(
      `the contract changed but info.version is still ${headVersion}. Bump it${suggestion === null ? '' : ` to ${suggestion}`} in src/DubbingPlatform.Api/OpenApi/openapi.v1.json and run \`make generate-api\`.`,
    );
    return result;
  }

  result.status = 'bumped';

  // A new API line is only real if the route moved with it. Without this pair,
  // bumping `info.version` to `v2` while `servers[].url` still says `/api/v1`
  // produces a document that claims a v2 API behind a v1 route - the exact
  // confusion a version field is supposed to prevent - and the reverse produces
  // a v2 route served by a document that still calls itself v1.
  //
  // The pairing is only checked when BOTH documents declare a versioned route.
  // A document with no `servers` entry is not incoherent; it is unversioned,
  // and oasdiff already reports a moved or removed server as a change.
  const baseRouteMajor = routeMajor(base);
  const headRouteMajor = routeMajor(head);
  if (baseRouteMajor !== null && headRouteMajor !== null) {
    const majorMoved = parseVersion(headVersion).major !== parseVersion(baseVersion).major;
    if (majorMoved && headRouteMajor === baseRouteMajor) {
      result.status = REASON.MAJOR_WITHOUT_ROUTE;
      result.annotations.push(
        `info.version moved to a new major (${baseVersion} -> ${headVersion}) but servers[0].url still points at v${headRouteMajor}. A new API line needs a new route prefix.`,
      );
    } else if (!majorMoved && headRouteMajor !== baseRouteMajor) {
      result.status = REASON.ROUTE_WITHOUT_MAJOR;
      result.annotations.push(
        `servers[0].url moved to a different API line (v${baseRouteMajor} -> v${headRouteMajor}) while info.version stayed on ${headVersion}.`,
      );
    }
  }
  return result;
}

/**
 * oasdiff's severity levels, as its JSON reports them.
 *
 * These are NUMBERS, not the `ERR`/`WARN`/`INFO` strings the text formatter
 * prints. Verified against the pinned build (oasdiff v1.11.7): a removed path
 * serialises as `"level":3` and the same change in `--format text` reads
 * "error". A parser that looked for the string would classify every breaking
 * change as level-less and, in a stricter version of this gate, drop it - so the
 * mapping is explicit and both representations are accepted.
 */
const LEVEL_NAMES = { 1: 'INFO', 2: 'WARN', 3: 'ERR' };
const LEVEL_NUMBERS = { ERR: 3, WARN: 2, INFO: 1 };

/**
 * @param {unknown} level oasdiff's raw `level`
 * @returns {'ERR'|'WARN'|'INFO'} the named level, defaulting to `ERR`
 *
 * The default is `ERR` on purpose. A change the pinned tool reported as
 * breaking, carrying a level this gate cannot read, must be treated as the most
 * severe thing it could be - never as the least. An unreadable severity that
 * downgrades a breaking change to a warning is exactly the silent-green failure
 * the tool pin exists to prevent.
 */
export function levelName(level) {
  if (typeof level === 'number') {
    return LEVEL_NAMES[level] ?? 'ERR';
  }
  if (typeof level === 'string') {
    return LEVEL_NUMBERS[level.toUpperCase()] !== undefined ? level.toUpperCase() : 'ERR';
  }
  return 'ERR';
}

/**
 * True when a change is severe enough to block on.
 *
 * `oasdiff breaking` only ever emits breaking changes, so in practice every
 * change blocks. The WARN band is included because oasdiff gained the ability to
 * emit warnings from `breaking` (the `--fail-on WARN` flag exists for exactly
 * that), and a change that arrives as a warning must not be silently green
 * before anyone notices the flag exists.
 *
 * @param {string} level a normalized level name
 */
export function isBlockingLevel(level) {
  return level === 'ERR' || level === 'WARN';
}

/**
 * Normalizes whatever oasdiff produced into a flat list of changes.
 *
 * oasdiff's JSON envelope has changed shape across releases (a bare array, a
 * `diff.breaking` object with per-severity arrays, and per-change sub-objects
 * that gained and lost fields), and a parser that assumes one of them turns a
 * tool upgrade into a green gate. So this walks the payload and keeps any
 * object that looks like a change, which is stable across those shapes.
 *
 * @param {unknown} payload the parsed `--format json` output
 * @returns {Array<{ id: string, level: string, text: string, operation: string|null, component: string|null, path: string|null }>}
 */
export function normalizeChanges(payload) {
  const out = [];
  const seen = new Set();
  walk(payload, out, seen);
  out.sort((a, b) => (a.id === b.id ? a.text.localeCompare(b.text) : a.id.localeCompare(b.id)));
  return out;
}

function walk(value, out, seen) {
  if (value === null || typeof value !== 'object') {
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      walk(item, out, seen);
    }
    return;
  }
  const id = typeof value.id === 'string' ? value.id : null;
  const text = typeof value.text === 'string' ? value.text : null;
  if (id !== null || text !== null) {
    // oasdiff splits the operation across `operation` (the HTTP method) and
    // `path` (the template). Joined here so every consumer - the annotation, the
    // report table, the dedupe key - names the operation the same way, and so a
    // removed operation reads as `GET /projects/{id}` rather than a bare `GET`.
    const method = typeof value.operation === 'string' ? value.operation : null;
    const path = typeof value.path === 'string' ? value.path : null;
    const operationPath = typeof value.operationPath === 'string' ? value.operationPath : null;
    const location = operationPath ?? (method !== null || path !== null ? [method, path].filter((part) => part !== null).join(' ') : null);
    const normalized = {
      id: id ?? 'unknown',
      level: levelName(value.level),
      text: text ?? id ?? 'change with no description',
      operation: location === '' ? null : location,
      component: typeof value.component === 'string' ? value.component : null,
      path,
    };
    // oasdiff reports one change per affected method for a removed path, and can
    // report the same change under both the path and the component view of one
    // document. Dedupe on identity-plus-location, never on id alone, or three
    // real breaks on one route collapse into one annotation.
    const key = `${normalized.id}|${normalized.text}|${normalized.operation ?? ''}`;
    if (!seen.has(key)) {
      seen.add(key);
      out.push(normalized);
    }
  }
  for (const key of Object.keys(value)) {
    walk(value[key], out, seen);
  }
}

/**
 * Parses oasdiff's `--format json` output, falling back to line mode.
 *
 * @param {string} raw stdout of `oasdiff breaking --format json`
 * @returns {{ changes: Array<object>, format: 'json'|'text', error?: string }}
 */
export function parseOasdiffOutput(raw) {
  const trimmed = (raw ?? '').trim();
  if (trimmed === '') {
    return { changes: [], format: 'json' };
  }
  try {
    return { changes: normalizeChanges(JSON.parse(trimmed)), format: 'json' };
  } catch {
    const changes = trimmed
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line !== '')
      .map((line) => ({ id: 'text', level: 'ERR', text: line, operation: null, component: null }));
    return { changes, format: 'text' };
  }
}

/**
 * Renders GitHub Actions error annotations, capped so a single breaking change
 * cannot produce 4 000 annotation lines (the cap is itself annotated, so a
 * truncated failure is never mistaken for a complete one).
 *
 * @param {Array<{ id: string, text: string, operation: string|null }>} changes
 * @param {{ file?: string, max?: number }} [options]
 * @returns {string[]} annotation lines
 */
export function renderChangeAnnotations(changes, options = {}) {
  const file = options.file ?? 'src/DubbingPlatform.Api/OpenApi/openapi.v1.json';
  const max = options.max ?? 50;
  const lines = [];
  const shown = changes.slice(0, max);
  for (const change of shown) {
    const title = escapeProperty(change.operation === null ? change.id : `${change.id} (${change.operation})`);
    lines.push(`::error file=${file},title=${title}::${escapeData(`[${change.level}] ${change.text}`)}`);
  }
  if (changes.length > shown.length) {
    lines.push(
      `::error file=${file},title=contract breaking changes truncated::${changes.length - shown.length} further change(s) not annotated; see the attached contract-diff artifact.`,
    );
  }
  return lines;
}

/**
 * GitHub annotation properties are comma-delimited, and a space in a property
 * value truncates it - which silently drops the operation a reviewer needs, so
 * both are escaped rather than left to a reader to work out.
 */
function escapeProperty(value) {
  return value.replace(/[%,:\s]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
}

/** Annotation message: literal newlines become `%0A`. */
function escapeData(value) {
  return value.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
}

/**
 * @param {Array<object>} changes
 * @returns {string} the markdown body written to the job summary and artifact
 */
export function renderBreakingReport(changes) {
  const lines = [
    '## OpenAPI breaking changes',
    '',
    `${changes.length} breaking change(s) between the base and head documents.`,
    '',
    '| id | level | operation | change |',
    '| --- | --- | --- | --- |',
  ];
  for (const change of changes) {
    lines.push(
      `| \`${change.id}\` | ${change.level} | ${change.operation ?? '-'} | ${String(change.text).replace(/\|/g, '\\|').replace(/\n/g, ' ')} |`,
    );
  }
  lines.push('');
  lines.push('An additive change does not need a new API line: add the route, the schema or the enum value,');
  lines.push('regenerate the client and merge. A breaking change needs a new major route prefix, a');
  lines.push('deprecation window on the old operation, or a reviewer-accepted `X-BREAKING-ALLOW` marker.');
  return lines.join('\n');
}

/**
 * @param {string} text
 * @returns {string} sha256 hex, used by the driver to name the artifact
 */
export function digest(text) {
  return createHash('sha256').update(text).digest('hex');
}
