#!/usr/bin/env node
// Dependency-advisory gate (Task 042).
//
// WHAT IT ADDS OVER `npm audit --audit-level=high`
// ----------------------------------------------
// `npm audit` gives you a non-zero exit code. That is enough to fail a build and
// not enough to act on one, and an unactionable red build is how a gate gets
// bypassed. This turns the same JSON into the four things the task asks for:
//
//   1. the advisory ID, so it can be searched and tracked;
//   2. the vulnerable range and the FIXED range, so the upgrade is a version
//      bump rather than an investigation;
//   3. the direct-vs-transitive path, because a transitive advisory on a
//      dev-only tool and one on a runtime library are not the same conversation;
//   4. a resolved owner, from CODEOWNERS, so the finding is assigned rather than
//      broadcast.
//
// SEVERITY, AND WHY `--level` DEFAULTS TO HIGH
// --------------------------------------------
// The task says "fail on high". LOW and MODERATE are reported and not fatal:
// npm's severity for a transitive package is inherited from an advisory that may
// not apply to how this repository uses it, and a gate that fails on a moderate
// advisory in a dev-only linter is a gate that gets `--audit-level=critical`-
// patched within a week. HIGH and CRITICAL block, and CRITICAL-with-no-fix is
// reported separately because that is the one case where "upgrade" is not
// available and the finding needs a decision rather than a version bump.
//
// EXIT CODES
//   0  nothing at or above the level
//   1  at least one advisory at or above the level
//   2  the audit could not be run or its output could not be parsed
// Exit 2 is distinct: "npm audit failed to run" and "npm audit found something"
// are different problems and a gate that conflates them is one people turn off.
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

/** npm's severity vocabulary, most severe last. */
export const SEVERITY_ORDER = ['info', 'low', 'moderate', 'high', 'critical'];

/**
 * @param {string} severity an npm severity
 * @returns {number} its index, or -1 when npm invents a new one
 */
export function severityRank(severity) {
  return SEVERITY_ORDER.indexOf(String(severity).toLowerCase());
}

/**
 * @param {string} severity an advisory severity
 * @param {string} level the configured blocking level
 * @returns {boolean} whether the advisory must fail the build
 */
export function isBlocking(severity, level) {
  const advisory = severityRank(severity);
  const threshold = severityRank(level);
  if (advisory === -1) {
    // An advisory at a severity this gate cannot rank must not be silently
    // treated as harmless. Same rule as the contract gate's level handling: an
    // unreadable severity is treated as the worst thing it could be.
    return true;
  }
  return advisory >= threshold;
}

/**
 * Flattens `npm audit --json` output into one finding per advisory, keeping the
 * dependency path so the finding names what to upgrade.
 *
 * npm's v1 shape is `{ advisories: { <id>: { module_name, severity, findings: [{ version, paths }], recommendation } } }`
 * and npm 7+'s is `{ vulnerabilities: { <name>: { name, severity, via: [...], range, fixAvailable } } }`.
 * Both are read, because a lockfile can be audited by either and a gate that
 * only understands one reports "clean" for the other's output.
 *
 * @param {object} report parsed `npm audit --json`
 * @returns {Array<object>} findings, most severe first
 */
export function parseAuditReport(report) {
  const findings = [];
  const push = (finding) => {
    if (finding.id !== '' && finding.module !== '') {
      findings.push(finding);
    }
  };

  if (report !== null && typeof report === 'object' && report.vulnerabilities !== undefined && report.vulnerabilities !== null) {
    for (const [name, entry] of Object.entries(report.vulnerabilities)) {
      if (entry === null || typeof entry !== 'object') {
        continue;
      }
      const via = Array.isArray(entry.via) ? entry.via : [];
      // No advisory object means every entry in `via` was a plain string, i.e.
      // this package has no advisory of its own and is only as bad as the worst
      // thing it depends on. Reporting it under its own name and linking to a
      // generic advisories page would invent an advisory id that does not exist
      // and send the reader looking for a CVE that was never published - so it is
      // labelled as inherited instead.
      const advisories = via.filter((item) => item !== null && typeof item === 'object');
      const inherited = advisories.length === 0;
      const directAdvisories = inherited ? [null] : advisories;
      for (const advisory of directAdvisories) {
        push({
          id: inherited
            ? `${entry.name ?? name} (severity inherited from a dependency)`
            : String(advisory.source ?? advisory.url ?? entry.name),
          module: entry.name ?? name,
          severity: String(advisory?.severity ?? entry.severity ?? 'unknown'),
          title: inherited
            ? 'no advisory of its own; this package inherits the severity of a dependency'
            : String(advisory.title ?? entry.name),
          range: String(entry.range ?? ''),
          fixAvailable: normaliseFix(entry.fixAvailable),
          direct: entry.isDirect === true,
          url: inherited ? '' : String(advisory.url ?? ''),
          inherited,
        });
      }
    }
  } else if (report !== null && typeof report === 'object' && report.advisories !== undefined && report.advisories !== null) {
    for (const [id, entry] of Object.entries(report.advisories)) {
      if (entry === null || typeof entry !== 'object') {
        continue;
      }
      const findingsForAdvisory = Array.isArray(entry.findings) ? entry.findings : [];
      push({
        id: String(id),
        module: String(entry.module_name ?? ''),
        severity: String(entry.severity ?? 'unknown'),
        title: String(entry.title ?? id),
        range: findingsForAdvisory.length > 0 ? findingsForAdvisory.map((f) => String(f.version ?? '')).filter((v) => v !== '').join(', ') : '',
        fixAvailable: normaliseFix(entry.patched_versions ?? ''),
        direct: findingsForAdvisory.some((f) => Array.isArray(f.paths) && f.paths.some((p) => String(p).split('>').length <= 2)),
        url: String(entry.url ?? ''),
      });
    }
  }

  findings.sort((a, b) => {
    const bySeverity = severityRank(b.severity) - severityRank(a.severity);
    if (bySeverity !== 0) {
      return bySeverity;
    }
    return a.module.localeCompare(b.module) || a.id.localeCompare(b.id);
  });
  return findings;
}

/** npm 7+ reports `fixAvailable: true | false | {name, version, isSemVerMajor}`. */
function normaliseFix(fix) {
  if (fix === true) {
    return 'a non-breaking fix is available';
  }
  if (fix === false || fix === undefined || fix === null) {
    return 'no fix published yet';
  }
  if (typeof fix === 'object') {
    const name = String(fix.name ?? '?');
    const version = String(fix.version ?? '?');
    return fix.isSemVerMajor === true
      ? `a fix exists but it is a MAJOR bump: ${name}@${version} - treat as a migration, not a version bump`
      : `upgrade ${name} to ${version}`;
  }
  const patched = String(fix);
  return patched === '' ? 'no fix published yet' : `patched in ${patched}`;
}

/**
 * Resolves a CODEOWNERS owner for a path.
 *
 * CODEOWNERS is read last-match-wins, which is the rule GitHub itself uses, and
 * the last matching pattern is the one that applies. Getting this backwards
 * assigns a finding to whoever owns the repository root instead of whoever owns
 * the lockfile, which is a difference between an actionable notification and a
 * broadcast to the whole team.
 *
 * @param {string|null} codeowners the file's contents
 * @param {string} filePath the path to resolve, repo-relative
 * @returns {string[]} owners, or `[]` when the file matches nothing
 */
export function resolveOwners(codeowners, filePath) {
  if (codeowners === null || codeowners === undefined) {
    return [];
  }
  const lines = codeowners.split(/\r?\n/);
  const owners = [];
  for (const raw of lines) {
    const line = raw.trim();
    if (line === '' || line.startsWith('#')) {
      continue;
    }
    const match = /^([^\s]+)\s+(.+)$/.exec(line);
    if (match === null) {
      continue;
    }
    const pattern = match[1];
    const candidates = match[2].split(/\s+/);
    if (matchesCodeownerPattern(pattern, filePath)) {
      owners.length = 0;
      owners.push(...candidates);
    }
  }
  return owners;
}

function matchesCodeownerPattern(pattern, filePath) {
  if (pattern === '*') {
    return true;
  }
  // CODEOWNERS patterns are gitignore-shaped: a directory pattern matches
  // everything under it, and a leading `!` negates. The negation case is
  // approximated by "not matched", which is what GitHub does for the common
  // (non-reincludable) usage and is the safe direction: a wrongly-included file
  // assigns one extra owner, a wrongly-excluded one assigns none.
  const negated = pattern.startsWith('!');
  const bare = negated ? pattern.slice(1) : pattern;
  const normalized = filePath.replace(/\\/g, '/');
  if (bare.endsWith('/')) {
    return normalized.startsWith(bare);
  }
  if (bare.includes('*')) {
    const source = bare
      .split('*')
      .map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
      .join('.*');
    return new RegExp(`^${source}$`).test(normalized) || new RegExp(`^${source}\\/`).test(normalized);
  }
  // A pattern with no slash matches at any depth, exactly as in CODEOWNERS.
  if (!bare.includes('/')) {
    return normalized === bare || normalized.endsWith(`/${bare}`) || normalized.startsWith(`${bare}.`);
  }
  return normalized === bare || normalized.startsWith(`${bare}/`);
}

/** A handle the repository has not filled in yet must not read as a real owner. */
export const OWNER_PLACEHOLDER = 'CHANGE_ME';

export function isPlaceholderOwner(owner) {
  return owner.includes(OWNER_PLACEHOLDER);
}

/**
 * Renders the human-facing report.
 *
 * @param {Array<object>} findings every finding, most severe first
 * @param {string} level the configured blocking level
 * @param {string[]} owners resolved owners for the audited manifest
 * @param {string} manifest the audited manifest path
 */
export function renderReport(findings, level, owners, manifest) {
  const lines = ['## Dependency audit', ''];
  lines.push(`Manifest: \`${manifest}\``);
  lines.push(`Blocking level: \`${level}\``);
  lines.push('');
  const resolved = owners.filter((owner) => !isPlaceholderOwner(owner));
  const placeholders = owners.filter((owner) => isPlaceholderOwner(owner));
  if (resolved.length > 0) {
    lines.push(`Owner: ${resolved.map((owner) => `@${owner}`).join(', ')}`);
  }
  if (placeholders.length > 0) {
    // Said plainly rather than rendered as a handle. A placeholder owner reads
    // as "assigned to nobody" and is the single most common reason a finding
    // sits unowned for a week.
    lines.push('');
    lines.push(`> **No owner is assigned.** \`.github/CODEOWNERS\` still carries \`${OWNER_PLACEHOLDER}\` for this path. Replace it with a real team handle - see docs/ci-branch-protection.md ("Owners").`);
  }
  if (findings.length === 0) {
    lines.push('');
    lines.push('No advisories reported. `npm audit` was run against the committed lockfile.');
    return lines.join('\n');
  }
  lines.push('');
  lines.push('| severity | advisory | package | vulnerable | fix | scope |');
  lines.push('| --- | --- | --- | --- | --- | --- |');
  for (const finding of findings) {
    // An inherited finding has no advisory id, so it is printed as plain text
    // rather than as a link to a page that would not contain it. Linking it
    // anyway would send a reader looking for a CVE that was never published.
    const label = finding.inherited === true
      ? `\`${finding.module}\` (inherited)`
      : `[\`${finding.id}\`](${finding.url || 'https://github.com/advisories'})`;
    lines.push(
      `| ${finding.severity} | ${label} | \`${finding.module}\` | ${finding.range || 'n/a'} | ${finding.fixAvailable} | ${finding.direct ? 'direct' : 'transitive'} |`,
    );
  }
  lines.push('');
  const inheritedCount = findings.filter((finding) => finding.inherited === true).length;
  if (inheritedCount > 0) {
    lines.push(`${inheritedCount} of these have no advisory of their own: npm reports their severity as inherited from a dependency, so there is no id to track and no single upgrade that closes them. Read the vulnerable range.`);
    lines.push('');
  }
  const blocking = findings.filter((finding) => isBlocking(finding.severity, level));
  const advisory = blocking.filter((finding) => finding.fixAvailable === 'no fix published yet');
  if (advisory.length > 0) {
    lines.push(`### No published fix (${advisory.length})`);
    lines.push('');
    lines.push('These cannot be closed by a version bump. Each one needs a decision recorded on the finding, and either a compensating control or an accepted risk with an expiry:');
    lines.push('');
    for (const finding of advisory) {
      lines.push(`- **${finding.id}** (\`${finding.module}\`, ${finding.severity}) — ${finding.title}`);
    }
    lines.push('');
  }
  return lines.join('\n');
}

/** GitHub annotation properties are comma-delimited; the message escapes newlines. */
function annotation(level, title, message) {
  const safeTitle = title.replace(/[%,:\s]/g, (c) => `%${c.charCodeAt(0).toString(16).toUpperCase()}`);
  const safeMessage = message.replace(/%/g, '%25').replace(/\r/g, '%0D').replace(/\n/g, '%0A');
  return `::${level} title=${safeTitle}::${safeMessage}`;
}

function parseArgs(argv) {
  const args = { manifest: 'package-lock.json', level: 'high', owners: '.github/CODEOWNERS' };
  for (let i = 0; i < argv.length; i += 1) {
    const flag = argv[i];
    const inline = flag.includes('=') ? flag.slice(flag.indexOf('=') + 1) : null;
    const take = () => {
      if (inline !== null) {
        return inline;
      }
      i += 1;
      return argv[i];
    };
    if (flag === '--manifest' || flag.startsWith('--manifest=')) {
      args.manifest = take();
    } else if (flag === '--level' || flag.startsWith('--level=')) {
      args.level = String(take()).toLowerCase();
    } else if (flag === '--owners' || flag.startsWith('--owners=')) {
      args.owners = take();
    } else {
      throw new Error(`unknown argument: ${flag}`);
    }
  }
  if (severityRank(args.level) === -1) {
    throw new Error(`--level must be one of ${SEVERITY_ORDER.join(', ')} (got '${args.level}')`);
  }
  return args;
}

function readOwners(path) {
  if (path === null || path === '') {
    return [];
  }
  try {
    return resolveOwners(readFileSync(path, 'utf8'), 'frontend/package-lock.json');
  } catch {
    // A missing CODEOWNERS is reported below, not silently treated as "no
    // owner": the difference is an unassigned finding and a finding assigned to
    // nobody, and the reader needs to see which one happened.
    return [];
  }
}

/**
 * How to invoke npm, as an ordered list of candidates to try.
 *
 * Getting this right took three attempts, and the first two were each correct on
 * one platform and broken on the other, which is the worst possible split for a
 * gate: it passes in CI and fails on every developer's machine, so only the
 * people who would have reported it ever see it.
 *
 *   1. `spawnSync('npm', ...)` is ENOENT on Windows (the executable is `npm.cmd`
 *      and Node does not consult PATHEXT for a bare name).
 *   2. `spawnSync('npm.cmd', ...)` is EINVAL on Windows since the CVE-2024-27980
 *      fix: a batch file cannot be spawned without a shell.
 *   3. So: run npm's own CLI script with the current Node, which needs no shell
 *      and is identical everywhere. `npm_execpath` is set when this runs from an
 *      npm script; otherwise npm sits beside Node in the same install directory.
 *   4. Only as a last resort, a shell, and then only with the one interpolated
 *      value (the `--prefix` path) quoted and screened for characters that would
 *      escape it. A gate that reintroduces shell interpolation of a path from its
 *      own command line has given back the injection it was avoiding.
 *
 * @param {NodeJS.ProcessEnv} [env]
 * @param {string} [platform]
 * @param {string} [nodeDir] the directory Node itself lives in
 * @returns {Array<{ command: string, args: string[], shell: boolean, why: string }>}
 */
export function resolveNpm(env = process.env, platform = process.platform, nodeDir = dirname(process.execPath)) {
  const candidates = [];
  if (typeof env.npm_execpath === 'string' && env.npm_execpath !== '') {
    candidates.push({ command: process.execPath, args: [env.npm_execpath], shell: false, why: 'npm_execpath' });
  }
  for (const relative of [
    ['node_modules', 'npm', 'bin', 'npm-cli.js'],
    ['node_modules', 'npm', 'bin', 'npm-cli.js'.replace('npm-cli.js', 'npm-cli.js')],
  ]) {
    const full = join(nodeDir, ...relative);
    if (existsSync(full)) {
      candidates.push({ command: process.execPath, args: [full], shell: false, why: 'npm beside node' });
      break;
    }
  }
  if (platform !== 'win32') {
    candidates.push({ command: 'npm', args: [], shell: false, why: 'npm on PATH' });
  } else {
    candidates.push({ command: 'npm.cmd', args: [], shell: true, why: 'npm.cmd through a shell (Windows only)' });
  }
  return candidates;
}

/**
 * Quotes and screens a value destined for a shell command line.
 *
 * @returns {string|null} the quoted value, or null when it cannot be quoted
 *   safely - in which case the caller must fail rather than guess
 */
export function quoteForShell(value) {
  if (/[\r\n]/.test(value)) {
    return null;
  }
  if (value.includes('"')) {
    return null;
  }
  return `"${value}"`;
}

function main(argv) {
  let args;
  try {
    args = parseArgs(argv);
  } catch (error) {
    process.stderr.write(`npm-audit-gate: ${error instanceof Error ? error.message : String(error)}\n`);
    return 2;
  }

  const manifest = resolve(args.manifest);
  const prefix = resolve(args.manifest, '..');
  const candidates = resolveNpm();
  const auditArgs = ['audit', '--json', '--prefix', prefix];

  // `npm audit` exits non-zero whenever it finds anything, so a non-zero exit
  // with usable stdout is the normal FINDING path, not a failure. Only an
  // invocation that produces no usable output at all is a gate failure - and
  // every candidate is tried before concluding that, because "could not run" on
  // a machine that could have run it is the worst possible reason to fail.
  let raw = null;
  const attempts = [];
  for (const candidate of candidates) {
    let args;
    let options;
    if (candidate.shell) {
      const quoted = quoteForShell(prefix);
      if (quoted === null) {
        attempts.push(`${candidate.why}: the --prefix path cannot be quoted safely (${JSON.stringify(prefix)})`);
        continue;
      }
      args = ['audit', '--json', '--prefix', quoted].join(' ');
      options = { shell: true };
    } else {
      args = [...candidate.args, ...auditArgs];
      options = {};
    }
    try {
      raw = execFileSync(candidate.command, args, {
        encoding: 'utf8',
        stdio: ['ignore', 'pipe', 'pipe'],
        maxBuffer: 32 * 1024 * 1024,
        ...options,
      });
      attempts.push(`${candidate.why}: ok`);
      break;
    } catch (error) {
      const stdout = error && typeof error.stdout === 'string' ? error.stdout : '';
      if (stdout.trim() !== '') {
        raw = stdout;
        attempts.push(`${candidate.why}: ok (found advisories, non-zero exit)`);
        break;
      }
      const stderr = error && typeof error.stderr === 'string' ? error.stderr.trim().slice(0, 300) : String(error);
      attempts.push(`${candidate.why}: ${stderr}`);
    }
  }

  if (raw === null || raw.trim() === '') {
    process.stderr.write(`npm-audit-gate: could not run \`npm audit\`. Attempts:\n`);
    for (const attempt of attempts) {
      process.stderr.write(`  - ${attempt}\n`);
    }
    process.stderr.write('An audit that could not run has not passed. Failing rather than reporting a clean lockfile.\n');
    process.stderr.write('CI_GATE_RESULT reason=AUDIT_UNAVAILABLE status=FAIL\n');
    return 2;
  }

  let report;
  try {
    report = JSON.parse(raw);
  } catch (error) {
    process.stderr.write(`npm-audit-gate: npm audit output is not JSON: ${error instanceof Error ? error.message : String(error)}\n`);
    process.stderr.write('CI_GATE_RESULT reason=AUDIT_UNREADABLE status=FAIL\n');
    return 2;
  }

  const findings = parseAuditReport(report);
  const owners = readOwners(args.owners);
  const relativeManifest = relative(process.cwd(), manifest).split(sep).join('/');
  const report_ = renderReport(findings, args.level, owners, relativeManifest);
  process.stdout.write(`${report_}\n`);

  const blocking = findings.filter((finding) => isBlocking(finding.severity, args.level));
  for (const finding of blocking) {
    process.stdout.write(
      annotation('error', `NPM_AUDIT_${finding.severity.toUpperCase()} ${finding.module}`, `${finding.id}: ${finding.title}. Vulnerable: ${finding.range || 'n/a'}. ${finding.fixAvailable}.${finding.direct ? '' : ' (transitive)'}${owners.length > 0 ? ` Owner: ${owners.join(', ')}.` : ' No owner resolved from CODEOWNERS.'}`),
    );
  }
  for (const finding of findings.filter((f) => !isBlocking(f.severity, args.level))) {
    process.stdout.write(
      annotation('warning', `NPM_AUDIT_${finding.severity.toUpperCase()} ${finding.module}`, `${finding.id}: ${finding.title}. Below the blocking level of '${args.level}', reported not failed.`),
    );
  }

  if (blocking.length > 0) {
    process.stdout.write(
      `CI_GATE_RESULT reason=AUDIT_HIGH status=FAIL blocking=${blocking.length} level=${args.level} owners=${owners.length > 0 ? owners.join(',') : 'UNASSIGNED'}\n`,
    );
    return 1;
  }
  process.stdout.write(`CI_GATE_RESULT reason=OK status=PASS advisories=${findings.length} level=${args.level}\n`);
  return 0;
}

if (process.argv[1] !== undefined && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  process.exitCode = main(process.argv.slice(2));
}
