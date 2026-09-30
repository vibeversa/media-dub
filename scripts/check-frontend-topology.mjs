#!/usr/bin/env node
// Frontend topology check (Task 043A, instruction 4 / R4).
//
// WHAT IT DECIDES
// ---------------
// "Does anything in frontend/src reach the infrastructure directly?" The browser
// is allowed to talk to exactly one thing: the API origin, from `VITE_API_BASE_URL`.
// It is not allowed to talk to PostgreSQL, RabbitMQ, Redis, MinIO, or a worker's
// pod, and it cannot be allowed to: those are network peers the NetworkPolicies
// grant to the API, and a browser that could reach one would be a credential
// disclosure with a URL in front of it.
//
// WHY A GATE AND NOT A REVIEW RULE
// --------------------------------
// The failure it prevents is not hypothetical and it is not subtle. A developer
// debugging a 500 adds the connection string to a log line; a developer wiring a
// health indicator adds a `fetch` to the diagnostics endpoint and, halfway
// through, to the worker's port; a test fixture copied into `src/` brings a
// `postgres://` with it. Every one of those is a plausible two-line change that
// no reviewer would flag, because in a file full of URLs one more URL is not
// visible. It is also, in every case, a published credential - the bundle is
// downloadable by anyone who can load the page.
//
// WHY A MODULE AND NOT A SCRIPT
// -----------------------------
// Same reason as `tools/hosting-policy.mjs`: the rules are decisions, they are
// unit-tested here over synthetic sources, and
// `deploy/frontend/topology.test.mjs` points them at the real tree. The CLI below
// is only I/O - read files, print findings, set an exit code.
//
// MACHINE-READABLE OUTPUT
// -----------------------
//   FRONTEND_TOPOLOGY_RESULT reason=<REASON> status=<PASS|FAIL> files=<n> findings=<n>
//
//   OK                             nothing in frontend/src reaches infrastructure
//   FRONTEND_TOPOLOGY_VIOLATION    a file references a datastore, broker, cache,
//                                  object store, or worker directly
//   FRONTEND_TOPOLOGY_INPUT_MISSING  the source tree or frontend/src is absent -
//                                    a FAILURE, because a check that read nothing
//                                    has not cleared anything
//
// EXIT: 0 pass, 1 violation, 2 could not run.
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, extname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

export const REASON_OK = 'OK';
export const REASON_VIOLATION = 'FRONTEND_TOPOLOGY_VIOLATION';
export const REASON_INPUT_MISSING = 'FRONTEND_TOPOLOGY_INPUT_MISSING';

export const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');

/**
 * Paths excluded from the scan, each with the reason it is excluded. Paths are
 * relative to the scan root, which is `frontend/src`.
 *
 * A list with reasons rather than a filter, because an unexplained exclusion is
 * indistinguishable from an exclusion added to make a failure go away. This one
 * is asserted in `deploy/frontend/topology.test.mjs` to contain exactly these
 * entries with exactly these reasons, so adding a second is a visible edit.
 */
export const EXEMPT_PATHS = [
  {
    path: 'config/env.ts',
    reason:
      'This is the secret-shape catalogue: every rule it breaks, it breaks in order to detect that rule. Its ' +
      '`amqps?://`, `Host=…;Password=` and `Npgsql`/`RabbitMQ` references are matched against injected VALUES, ' +
      'never used as an endpoint - the first draft of this gate flagged all three and the only defensible fix was ' +
      'to exclude the file. Narrowing the rules around a regular-expression literal is exactly the kind of clever ' +
      'that stops working at the next edit.',
  },
];

const TEST_DIRECTORY = `${sep}__tests__${sep}`;
const TEST_FILE = /\.(?:test|spec)\.[cm]?[jt]sx?$/;
const STORY_FILE = /\.stories\.[cm]?[jt]sx?$/;
const SCANNED_EXTENSIONS = new Set(['.ts', '.tsx', '.mts', '.cts', '.js', '.jsx', '.mjs', '.cjs']);

/**
 * Paths the browser is allowed to name in a source literal.
 *
 * Four groups, and the last one is the interesting one:
 *   loopback      - the local dev server and the cross-layer rig.
 *   reserved      - `.example.com` and friends are IANA-reserved and can never
 *                   resolve, so they are the only safe thing to put in a
 *                   committed file. A real hostname in `frontend/src` is a
 *                   deployment detail in application code, and it is the thing
 *                   that makes a bundle environment-specific.
 *   namespaces    - `www.w3.org` appears in XML namespace constants, which are
 *                   identifiers rather than endpoints.
 *   cdn           - the CDN origin itself, which is a *document* origin and is
 *                   still an `VITE_*` value at runtime. Naming it as a literal
 *                   would defeat the injection the whole allowlist exists for.
 */
export const ALLOWED_HOST_SUFFIXES = ['.example.com', '.example', '.invalid', '.test', '.localhost', '.localdomain'];
export const ALLOWED_HOSTS = new Set(['localhost', '127.0.0.1', '::1', '0.0.0.0', 'www.w3.org']);

/**
 * The infrastructure references that are a finding.
 *
 * Every pattern requires something AFTER the scheme (`://` followed by an
 * alphanumeric, or a host-ish token) rather than matching the bare scheme. That
 * is not a stylistic choice: `features/exports/types.ts` contains
 * `lowered.includes('s3://')` to STRIP internal storage URLs from text before
 * rendering it, and a rule that matched the bare scheme would fail the build for
 * the code that exists to prevent the leak. A check that fires on the defensive
 * use of a string gets disabled, and then it checks nothing.
 */
export const FORBIDDEN_PATTERNS = [
  {
    id: 'postgres-endpoint',
    label: 'a PostgreSQL endpoint',
    re: /\b(?:postgres(?:ql)?|postgresql\+[a-z0-9]+):\/\/[A-Za-z0-9]/gi,
    why: 'The browser has no business holding a database connection string, and Vite inlines it into a bundle anyone can download.',
  },
  {
    id: 'npgsql-client',
    label: 'a .NET PostgreSQL client reference',
    re: /\b(?:Npgsql|Microsoft\.Data\.SqlClient)\b/g,
    why: 'A server-side driver name in frontend/src means server-side code is being shipped to the browser.',
  },
  {
    id: 'broker-endpoint',
    label: 'a message-broker endpoint',
    re: /\bamqps?:\/\/[A-Za-z0-9]/gi,
    why: 'A browser cannot reach RabbitMQ, and a broker URI with inline credentials in a bundle is a published credential.',
  },
  {
    id: 'broker-client',
    label: 'a broker client or server reference',
    re: /\b(?:RabbitMQ\.Client|amqplib|rabbitmq(?:-server)?\b)/gi,
    why: 'Publishing to the bus from a browser bypasses every stage the API enforces, and there is no authorization model for it.',
  },
  {
    id: 'redis-endpoint',
    label: 'a Redis endpoint',
    re: /\brediss?:\/\/[A-Za-z0-9]/gi,
    why: 'Redis has no authentication in this deployment; anything that can open a connection can read every session.',
  },
  {
    id: 'redis-client',
    label: 'a Redis client or server reference',
    re: /\b(?:StackExchange\.Redis|redis-server|redis-cli)\b/g,
    why: 'Same as the endpoint: there is no client-side story for a cache the API treats as a private store.',
  },
  {
    id: 'object-storage-endpoint',
    label: 'an object-storage endpoint',
    // A host must follow. `s3://` on its own is the prefix a UI matches to
    // recognise and strip an internal URL; `s3://bucket/key` is a request.
    re: /\bs3a?:\/\/[A-Za-z0-9][^\s'"`)]*/gi,
    why: 'Object storage is reached through presigned URLs issued by the API. A client with the bucket URL has the bucket.',
  },
  {
    id: 'object-storage-client',
    label: 'an object-storage SDK or MinIO reference',
    re: /(?:\bminio\b|@aws-sdk\/client-s3\b|aws4fetch\b)/gi,
    why: 'An SDK in the browser bundle means credentials are either in the bundle or about to be requested at runtime.',
  },
  {
    id: 'worker-endpoint',
    label: 'a worker host',
    re: /\b(?:worker-(?:gpu|ai|export|render|media|media-prep|control|maintenance)|dubbing-worker)\b/gi,
    why: 'Workers are internal, unauthenticated consumers of the bus. A browser that can name one can drive the pipeline.',
  },
  {
    id: 'connection-string',
    label: 'a semicolon-delimited connection string',
    // The middle segment is `[^"']*` and NOT `[^;]*`, so the password does not
    // have to be the SECOND pair. A real Npgsql string is
    // `Host=…;Database=…;Username=…;Password=…`, and the narrower middle class
    // matched only a two-pair shape nobody writes. This is the same defect that
    // was found in `SECRET_VALUE_PATTERNS` while writing this file.
    re: /\b(?:Host|Server|Data Source)\s*=\s*[^;\s"']+;[^"']*\b(?:Password|Pwd)\s*=/gi,
    why: 'An ADO.NET connection string in the browser bundle is a published password, whatever the variable is called.',
  },
  {
    id: 'infrastructure-port',
    label: 'a hard-coded datastore port',
    // A colon immediately before the digits, so `61000` and `90000` - millisecond
    // durations, which this codebase is full of - cannot match.
    re: /:\s*(?:5432|5672|5671|6379|6380|9000|9090|15672|27017)\b/g,
    why: 'A browser bundle naming the database or broker port is a request to something the NetworkPolicies exist to keep unreachable.',
  },
];

/** Anything that looks like `scheme://host[:port][/path]`. */
const URL_LITERAL = /\b([a-z][a-z0-9+.-]*):\/\/([^\s'"`<>)\\]+)/gi;

/** Whether a scanned file is in the scan at all. Pure. */
export function isScannableFile(relativePath) {
  const normalized = relativePath.split('\\').join('/');
  if (EXEMPT_PATHS.some((entry) => normalized === entry.path)) {
    return false;
  }
  if (normalized.includes(TEST_DIRECTORY)) return false;
  if (TEST_FILE.test(normalized)) return false;
  if (STORY_FILE.test(normalized)) return false;
  // Both forms, because the scan root makes this path relative and the suffix
  // form alone silently stops matching the moment the file moves up a level.
  if (normalized === 'testSetup.ts' || normalized.endsWith('/testSetup.ts')) return false;
  return SCANNED_EXTENSIONS.has(extname(normalized));
}

/**
 * Blanks out comments while preserving every character position, so a finding
 * still reports the line it is really on.
 *
 * THE `//` RULE IS THE INTERESTING ONE. A `//` that is immediately preceded by
 * `:` is NOT a comment - it is the `//` of a URL (`https://`, `amqps://`,
 * `s3://`), and blanking it would delete the very strings the URL-literal rule
 * has to read. Commented-out code in this repository's history contains both
 * forms on one line often enough that getting this backwards turns the gate
 * into noise, and getting it right costs one character of lookahead.
 *
 * Block comments are blanked without nesting, which matches JavaScript: `/* /*`
 * is not a nested comment, it is a syntax error. Backticks are NOT treated as
 * strings here, because a template literal routinely interpolates a URL
 * (`${apiBase}/v1/...`) and blanking it would hide the variable that matters.
 */
export function maskComments(source) {
  const out = source.split('');
  let inBlock = false;
  for (let i = 0; i < source.length; i += 1) {
    const ch = source[i];
    const next = source[i + 1];
    if (inBlock) {
      if (ch === '*' && next === '/') {
        out[i] = ' ';
        out[i + 1] = ' ';
        i += 1;
        inBlock = false;
      } else if (ch !== '\n') {
        out[i] = ' ';
      }
      continue;
    }
    if (ch === '/' && next === '*') {
      out[i] = ' ';
      out[i + 1] = ' ';
      i += 1;
      inBlock = true;
      continue;
    }
    if (ch === '/' && next === '/') {
      const previous = i > 0 ? source[i - 1] : '';
      if (previous !== ':') {
        for (let j = i; j < source.length && source[j] !== '\n'; j += 1) {
          out[j] = ' ';
        }
        // `i` is left where it is so the loop resumes normally. An earlier version
        // used `break` here, which abandoned the REST OF THE FILE the first time
        // it saw a line comment - so a block comment on a later line, and every
        // real finding after it, went unexamined while the gate reported PASS.
        // A masking bug that removes findings is indistinguishable from a clean
        // tree, which is exactly why this is asserted with a fixture that has a
        // line comment and a block comment.
        continue;
      }
    }
  }
  return out.join('');
}

/** The 1-based line number of a character offset. Pure. */
export function lineOf(source, offset) {
  let line = 1;
  for (let i = 0; i < offset && i < source.length; i += 1) {
    if (source[i] === '\n') line += 1;
  }
  return line;
}

/**
 * The host an absolute URL literal names, or `null`. Pure.
 *
 * `new URL` is not used: a literal in a comment-shaped string is often not a URL
 * at all, and `new URL('https://')` throwing on malformed input would turn a
 * documentation string into a crash rather than into a finding.
 */
export function hostOf(urlLiteral) {
  const match = /^[a-z][a-z0-9+.-]*:\/\/([^/?#\s]+)/i.exec(urlLiteral);
  if (match === null) return null;
  let authority = match[1];
  // Strip userinfo: `https://user:pw@host` names `host`, and the credential half
  // is a separate finding handled by the secret rules.
  const at = authority.lastIndexOf('@');
  if (at >= 0) authority = authority.slice(at + 1);
  if (authority.startsWith('[')) {
    const close = authority.indexOf(']');
    return close >= 0 ? authority.slice(1, close) : null;
  }
  const colon = authority.indexOf(':');
  return (colon >= 0 ? authority.slice(0, colon) : authority).toLowerCase();
}

/** Whether a host is on the allowlist. Pure. */
export function isAllowedHost(host) {
  if (typeof host !== 'string' || host.length === 0) return false;
  if (ALLOWED_HOSTS.has(host.toLowerCase())) return true;
  const lowered = host.toLowerCase();
  return ALLOWED_HOST_SUFFIXES.some((suffix) => lowered.endsWith(suffix));
}

/**
 * Every finding in one source file. Pure - no filesystem, no network.
 *
 * `relativePath` is used only in the messages, so a synthetic source can be
 * scanned by a test with a name that says what it is.
 */
export function scanSource(source, relativePath) {
  const findings = [];
  const masked = maskComments(source);

  for (const rule of FORBIDDEN_PATTERNS) {
    // A fresh RegExp per rule: the patterns are declared with /g, and a shared
    // lastIndex across files would make the second file silently miss matches.
    const pattern = new RegExp(rule.re.source, rule.re.flags);
    for (let match = pattern.exec(masked); match !== null; match = pattern.exec(masked)) {
      findings.push({
        rule: rule.id,
        line: lineOf(masked, match.index),
        match: match[0].trim(),
        message:
          `${relativePath}:${lineOf(masked, match.index)}: ${rule.label} (${rule.id}) - ` +
          `"${match[0].trim()}". ${rule.why}`,
      });
    }
  }

  for (let match = URL_LITERAL.exec(masked); match !== null; match = URL_LITERAL.exec(masked)) {
    const scheme = match[1].toLowerCase();
    if (scheme === 'http' || scheme === 'https') {
      const host = hostOf(match[0]);
      if (host !== null && !isAllowedHost(host)) {
        findings.push({
          rule: 'off-allowlist-host',
          line: lineOf(masked, match.index),
          match: host,
          message:
            `${relativePath}:${lineOf(masked, match.index)}: an absolute URL naming '${host}' (off-allowlist-host). ` +
            'The browser may reach exactly one origin and it is VITE_API_BASE_URL. A real hostname written into ' +
            'frontend/src is a deployment detail in application code, and it is what makes one bundle ' +
            'environment-specific.',
        });
      }
    } else if (scheme !== 'wss' && scheme !== 'ws' && !['data', 'blob', 'about', 'mailto', 'javascript'].includes(scheme)) {
      findings.push({
        rule: 'non-http-scheme',
        line: lineOf(masked, match.index),
        match: scheme,
        message:
          `${relativePath}:${lineOf(masked, match.index)}: a '${scheme}://' literal (non-http-scheme). The only ` +
          'schemes the browser may be pointed at here are http, https, ws and wss.',
      });
    }
  }

  findings.sort((left, right) => left.line - right.line || left.rule.localeCompare(right.rule));
  return findings;
}

/** Recursively lists the scannable files under a directory. I/O; the rules are above. */
export function listScannableFiles(rootDirectory) {
  const found = [];
  const walk = (directory) => {
    let entries;
    try {
      entries = readdirSync(directory, { withFileTypes: true });
    } catch (error) {
      throw new Error(`cannot read ${directory}: ${error instanceof Error ? error.message : String(error)}`);
    }
    for (const entry of entries) {
      const full = join(directory, entry.name);
      if (entry.isDirectory()) {
        walk(full);
      } else if (entry.isFile()) {
        const relativePath = relative(rootDirectory, full).split(sep).join('/');
        if (isScannableFile(relativePath)) {
          found.push(relativePath);
        }
      }
    }
  };
  const info = statSync(rootDirectory);
  if (!info.isDirectory()) {
    throw new Error(`${rootDirectory} is not a directory`);
  }
  walk(rootDirectory);
  return found.sort();
}

function main(argv) {
  const explicit = argv.find((argument) => !argument.startsWith('-'));
  const sourceRoot = resolve(explicit ?? join(REPO_ROOT, 'frontend', 'src'));
  const relativeTo = sourceRoot.endsWith(`${sep}frontend${sep}src`) ? join(sourceRoot, '..', '..') : sourceRoot;

  let files;
  try {
    files = listScannableFiles(sourceRoot);
  } catch (error) {
    console.error(`::error::${error instanceof Error ? error.message : String(error)}`);
    console.error(
      'A topology check that read no files has cleared nothing. frontend/src must exist; this is a failure, not a skip.',
    );
    console.error(`FRONTEND_TOPOLOGY_RESULT reason=${REASON_INPUT_MISSING} status=FAIL files=0 findings=0`);
    return 2;
  }

  const findings = [];
  for (const relativePath of files) {
    const source = readFileSync(join(sourceRoot, relativePath), 'utf8');
    for (const finding of scanSource(source, relativePath)) {
      findings.push({ ...finding, file: join(relativeTo, 'frontend', 'src', relativePath) });
    }
  }

  for (const finding of findings) {
    console.error(`::error::${finding.message}`);
  }

  const status = findings.length === 0 ? 'PASS' : 'FAIL';
  const reason = findings.length === 0 ? REASON_OK : REASON_VIOLATION;
  console.log(
    `check-frontend-topology: ${files.length} file(s) scanned under ${sourceRoot}, ` +
      `${findings.length} finding(s).`,
  );
  if (findings.length > 0) {
    for (const finding of findings) {
      console.log(`  ${reason === REASON_OK ? 'ok' : 'FAIL'}  ${finding.file}:${finding.line}  ${finding.rule}`);
    }
  }
  console.log(`FRONTEND_TOPOLOGY_RESULT reason=${reason} status=${status} files=${files.length} findings=${findings.length}`);
  return findings.length === 0 ? 0 : 1;
}

const invokedDirectly =
  process.argv[1] !== undefined && resolve(process.argv[1]) === resolve(fileURLToPath(import.meta.url));
if (invokedDirectly) {
  process.exitCode = main(process.argv.slice(2));
}
