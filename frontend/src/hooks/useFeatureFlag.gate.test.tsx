// Task 048, R1 — the single-shared-hook gate.
//
// WHAT IT DECIDES
// ---------------
// "Is there any second way to find out whether a feature flag is on?"
//
// The failure it prevents is not a crash. It is drift: a second module reads
// `/me` directly, or reads `VITE_ENABLE_*` directly, or hard-codes one of the
// three `/me` wire spellings. Each of those is a small, locally reasonable
// line, and the result is that "is enrichment on?" has two answers depending on
// which module you ask - which is precisely what Task 044 shipped, and what this
// task exists to undo. The rules below make that a failing suite rather than a
// review comment.
//
// WHY A GREP AND NOT AN ESLINT RULE
// --------------------------------
// Because the three rules are about *values*, not about module boundaries, and
// an ESLint rule for them would need its own type information:
//
//   - the `/me` wire spelling is data (`ME_FEATURE_FLAG_WIRE_KEYS`), so the rule
//     has to read the vocabulary out of the module that owns it rather than
//     repeat it - a rule that repeated the vocabulary would have two copies of
//     it, which is the defect it is checking for;
//   - `VITE_ENABLE_*` is a string family, and `scripts/vite-env-audit.sh` owns
//     which names are allowlisted; this gate only asserts WHERE they may be
//     read, and that the reader of last resort is `config/env.ts`;
//   - "issues a `/me` request" is a call shape, not an import edge.
//
// WHY THIS FILE IS NOT IN `__tests__/`
// ------------------------------------
// Because Task 048's own validation command is
// `npm run test -- src/hooks/useFeatureFlag`, and vitest treats a positional
// filter as a path prefix: it selects this file and the unit suite and nothing
// else. Putting the gate under `__tests__/useFeatureFlag.*.test.tsx` would make
// that command match NO files and exit 1, so the validation would prove
// nothing. Co-location next to the hook is also a repository convention
// (`src/components/**/X.test.tsx`, `src/i18n/pseudo.spec.tsx`).
//
// EVERY RULE IS DRIVEN WITH SYNTHETIC SOURCES FIRST
// -------------------------------------------------
// A gate that has only ever seen a clean tree is a gate with no evidence that it
// can fail. Each rule is exercised against a source written the way the mistake
// is actually written, and the real tree is scanned second. The allowlists are
// asserted to be exactly the expected entries, so widening one is a visible edit.

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { join, relative, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { DEPLOY_CONFIG_ALLOWLIST } from '../config/env.js';
import { meFeatureFlagWireKeys } from '../config/featureFlags.js';

const SRC = resolve(process.cwd(), 'src');

export type FlagReadRuleId =
  | 'WIRE_KEY_UNDECLARED'
  | 'BOOTSTRAP_FLAG_UNDECLARED'
  | 'ME_REQUEST_UNDECLARED'
  | 'ME_DOCUMENT_UNDECLARED';

export interface FlagReadRule {
  readonly id: FlagReadRuleId;
  /** What the rule forbids, for the failure message. */
  readonly what: string;
  /** Paths (relative to `frontend/src`) allowed to trip it, each with a reason. */
  readonly allowed: Readonly<Record<string, string>>;
  /** Every match in a comment-stripped source, as a reporting string. */
  readonly find: (code: string) => readonly string[];
}

function escapeForRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

/**
 * Strips comments, so prose about a flag cannot fail the rule that governs
 * reading one.
 *
 * `config/featureFlags.ts` explains why the wire spellings are exactly what they
 * are, and that explanation necessarily names them. A scanner that cannot tell a
 * comment from code does not enforce a rule - it forbids a word, and the first
 * response to a forbidden word is a vaguer explanation.
 *
 * Strings are preserved, including the `//` inside a URL: `https://api` must not
 * swallow the rest of the line. That is the one real hazard in a regex comment
 * stripper and it is asserted directly below.
 */
export function stripComments(source: string): string {
  let out = '';
  let index = 0;
  // Which string delimiter is open, if any. `null` means code.
  let quote: '"' | "'" | '`' | null = null;
  while (index < source.length) {
    const rest = source.slice(index);
    if (quote !== null) {
      if (rest.startsWith('\\')) {
        out += rest.slice(0, 2);
        index += 2;
        continue;
      }
      if (rest.startsWith(quote)) {
        out += quote;
        index += 1;
        quote = null;
        continue;
      }
      out += rest[0] as string;
      index += 1;
      continue;
    }
    if (rest.startsWith('//')) {
      const newline = source.indexOf('\n', index);
      index = newline === -1 ? source.length : newline;
      out += '\n';
      continue;
    }
    if (rest.startsWith('/*')) {
      const close = source.indexOf('*/', index + 2);
      index = close === -1 ? source.length : close + 2;
      // Keep newlines so a line-based reading of the result stays honest.
      out += source.slice(0, 0) + '\n';
      continue;
    }
    if (rest.startsWith('"') || rest.startsWith("'") || rest.startsWith('`')) {
      quote = rest[0] as '"' | "'" | '`';
      out += quote;
      index += 1;
      continue;
    }
    out += rest[0] as string;
    index += 1;
  }
  return out;
}

function matchesIn(code: string, pattern: RegExp): readonly string[] {
  const global = new RegExp(pattern.source, pattern.flags.includes('g') ? pattern.flags : `${pattern.flags}g`);
  const found = code.match(global);
  return found === null ? [] : [...new Set(found)];
}

/**
 * The rules. Subjects come from the modules that own them - the wire vocabulary
 * from `config/featureFlags.ts`, the allowlisted bootstrap names from
 * `config/env.ts` - so adding a flag or a variable cannot leave a rule checking
 * a list that has drifted from reality.
 */
export function flagReadRules(): readonly FlagReadRule[] {
  const wireKeys = meFeatureFlagWireKeys();
  const bootstrapNames = DEPLOY_CONFIG_ALLOWLIST.filter((key) => key.startsWith('VITE_ENABLE_'));
  return [
    {
      id: 'WIRE_KEY_UNDECLARED',
      what: 'a `/me` feature-flag wire spelling outside the vocabulary module',
      allowed: {
        'config/featureFlags.ts':
          'the one declaration of ME_FEATURE_FLAG_WIRE_KEYS. A second declaration is a vocabulary that will disagree with itself at the first rename.',
      },
      find: (code) => wireKeys.flatMap((wireKey) => matchesIn(code, new RegExp(`\\b${escapeForRegExp(wireKey)}\\b`, 'g'))),
    },
    {
      id: 'BOOTSTRAP_FLAG_UNDECLARED',
      what: 'a `VITE_ENABLE_*` literal outside the deploy-config module',
      allowed: {
        'config/env.ts':
          'the one reader of the raw variables. config/featureFlags.ts asks for the resolved booleans, so a VITE_ENABLE_* name outside env.ts is a second reader of a value that belongs to one place.',
      },
      find: (code) =>
        bootstrapNames.flatMap((name) => matchesIn(code, new RegExp(`\\b${escapeForRegExp(name)}\\b`, 'g'))),
    },
    {
      id: 'ME_REQUEST_UNDECLARED',
      what: "a direct `GET /me` request outside the auth API module",
      allowed: {
        'features/auth/api.ts':
          'the one `/me` document reader. A second one is the second `/me` read Task 044 paid for, and it can disagree with the permissions from the same document.',
      },
      find: (code) =>
        matchesIn(
          code,
          /\b(?:apiFetch|fetch|request|get|post|put|patch|del)\s*(?:<[^>\n]{0,120}>)?\s*\(\s*(?:`|'|")[^`'"\n]*\/me\b/g,
        ).map((match) => match.slice(0, 80)),
    },
    {
      id: 'ME_DOCUMENT_UNDECLARED',
      what: 'a caller of `fetchMeDocument` outside the flag hook and the session store',
      allowed: {
        'hooks/useFeatureFlag.ts':
          'the flag hook reads the document to resolve flags - that is its job.',
        'features/auth/authStore.ts':
          'reads the same document for permissions and locale, and seeds the flag cache from it. One document, one read, two consumers.',
      },
      // `(?<!function )` so the DECLARATION in `features/auth/api.ts` is not a
      // call: a rule that flagged the module which defines the reader would
      // have to allowlist it, and then a call inside that module would be
      // exempt too.
      find: (code) => matchesIn(code, /(?<!function )\bfetchMeDocument\s*\(/g).map(() => 'fetchMeDocument('),
    },
  ];
}

function walk(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (entry === 'node_modules' || entry === 'generated') {
        continue;
      }
      walk(full, out);
      continue;
    }
    if (/\.(ts|tsx)$/.test(entry)) {
      out.push(full);
    }
  }
  return out;
}

/** Production source only: tests are where wire documents get constructed. */
function productionSources(): Map<string, string> {
  const files = new Map<string, string>();
  for (const file of walk(SRC)) {
    const rel = relative(SRC, file);
    if (rel.includes('__tests__') || /\.(test|spec)\.tsx?$/.test(rel)) {
      continue;
    }
    files.set(rel, stripComments(readFileSync(file, 'utf8')));
  }
  return files;
}

/** The scan, over a file map. Pure, so synthetic sources can drive it. */
export function findFlagReadOffences(
  files: ReadonlyMap<string, string>,
  rules: readonly FlagReadRule[],
): readonly string[] {
  const offences: string[] = [];
  for (const [file, code] of files) {
    for (const rule of rules) {
      if (rule.allowed[file] !== undefined) {
        continue;
      }
      for (const match of rule.find(code)) {
        offences.push(`${rule.id} ${file} -> ${match}`);
      }
    }
  }
  return offences;
}

// =============================================================================
// The comment stripper, which every rule depends on
// =============================================================================

describe('stripComments', () => {
  it('removes a line comment and keeps the code around it', () => {
    const stripped = stripComments('const a = 1; // VITE_ENABLE_ANALYTICS\nconst b = 2;');
    expect(stripped).not.toContain('VITE_ENABLE_ANALYTICS');
    expect(stripped).toContain('const a = 1;');
    expect(stripped).toContain('const b = 2;');
  });

  it('removes a block comment, newlines included', () => {
    const stripped = stripComments('const a = 1;\n/* lipSyncEnabled\n   over two lines */\nconst b = 2;');
    expect(stripped).not.toContain('lipSyncEnabled');
    expect(stripped).toContain('const a = 1;');
    expect(stripped).toContain('const b = 2;');
  });

  it('does not treat the // inside a URL as a comment', () => {
    // The one real hazard: a string containing `//` must survive intact, or a
    // rule could be silenced by an unrelated line above the thing it checks.
    const stripped = stripComments("const base = 'https://api.example.com';\nconst flag = 'VITE_ENABLE_ANALYTICS';");
    expect(stripped).toContain('https://api.example.com');
    expect(stripped).toContain('VITE_ENABLE_ANALYTICS');
  });

  it('survives an unterminated block comment', () => {
    expect(stripComments('const a = 1; /* never closed')).toContain('const a = 1;');
  });
});

// =============================================================================
// Each rule, driven with a source written the way the mistake is written
// =============================================================================

describe('each rule fires on the mistake and stays quiet otherwise', () => {
  const rules = flagReadRules();
  const rule = (id: FlagReadRuleId): FlagReadRule => {
    const found = rules.find((candidate) => candidate.id === id);
    if (found === undefined) {
      throw new Error(`no such rule: ${id}`);
    }
    return found;
  };

  const files = (entries: Readonly<Record<string, string>>): ReadonlyMap<string, string> =>
    new Map(Object.entries(entries).map(([name, source]) => [name, stripComments(source)]));

  it('flags a second declaration of a /me wire spelling', () => {
    const id = 'WIRE_KEY_UNDECLARED';
    const offender = files({ 'features/enrichment/panel.tsx': "const key = 'lipSyncEnabled';\n" });
    expect(findFlagReadOffences(offender, [rule(id)])).toEqual([`${id} features/enrichment/panel.tsx -> lipSyncEnabled`]);
    // The declaration itself, and a mention in prose, are both fine.
    expect(
      findFlagReadOffences(files({ 'config/featureFlags.ts': "videoIntelligenceEnabled: ['videoIntelligenceEnabled'],\n" }), [
        rule(id),
      ]),
    ).toEqual([]);
    expect(
      findFlagReadOffences(files({ 'features/enrichment/panel.tsx': '// the server sends videoIntelligenceEnabled\n' }), [
        rule(id),
      ]),
    ).toEqual([]);
  });

  it('flags a second reader of a VITE_ENABLE_* value', () => {
    const id = 'BOOTSTRAP_FLAG_UNDECLARED';
    const offender = files({ 'features/admin/OpsDashboard.tsx': "const on = import.meta.env.VITE_ENABLE_DIAGNOSTICS;\n" });
    expect(findFlagReadOffences(offender, [rule(id)])).toEqual([
      `${id} features/admin/OpsDashboard.tsx -> VITE_ENABLE_DIAGNOSTICS`,
    ]);
    expect(findFlagReadOffences(files({ 'config/env.ts': "raw['VITE_ENABLE_ANALYTICS'],\n" }), [rule(id)])).toEqual([]);
  });

  it('flags a second /me request', () => {
    const id = 'ME_REQUEST_UNDECLARED';
    for (const source of [
      "await apiFetch<unknown>('/me');\n",
      "await fetch('/api/v1/me');\n",
      "await apiClient.get('/me');\n",
    ]) {
      expect(findFlagReadOffences(files({ 'features/enrichment/gate.ts': source }), [rule(id)])).toHaveLength(1);
    }
    // A path that merely starts with the same letters is not `/me`.
    expect(findFlagReadOffences(files({ 'features/x.ts': "await apiFetch('/meets');\n" }), [rule(id)])).toEqual([]);
    // And the real reader is allowed.
    expect(
      findFlagReadOffences(files({ 'features/auth/api.ts': "return apiFetch<unknown>('/me');\n" }), [rule(id)]),
    ).toEqual([]);
  });

  it('flags a caller of the /me document reader', () => {
    const id = 'ME_DOCUMENT_UNDECLARED';
    const offender = files({ 'features/enrichment/gate.ts': 'const raw = await fetchMeDocument();\n' });
    expect(findFlagReadOffences(offender, [rule(id)])).toEqual([
      `${id} features/enrichment/gate.ts -> fetchMeDocument(`,
    ]);
  });

  it('carries exactly the allowlists this task wrote, each with a reason', () => {
    // Widening an allowlist must be a deliberate edit with prose attached, not
    // a side effect of making something pass.
    expect(Object.fromEntries(rules.map((r) => [r.id, Object.keys(r.allowed).sort()]))).toEqual({
      WIRE_KEY_UNDECLARED: ['config/featureFlags.ts'],
      BOOTSTRAP_FLAG_UNDECLARED: ['config/env.ts'],
      ME_REQUEST_UNDECLARED: ['features/auth/api.ts'],
      ME_DOCUMENT_UNDECLARED: ['features/auth/authStore.ts', 'hooks/useFeatureFlag.ts'],
    });
    for (const candidate of rules) {
      for (const [file, reason] of Object.entries(candidate.allowed)) {
        expect(existsSync(join(SRC, file))).toBe(true);
        expect(reason.length).toBeGreaterThan(40);
      }
    }
  });
});

// =============================================================================
// The real tree
// =============================================================================

describe('R1 the real tree has exactly one flag source', () => {
  it('has no ad-hoc flag read outside the allowlists', () => {
    expect(findFlagReadOffences(productionSources(), flagReadRules())).toEqual([]);
  });

  it('the rules are not vacuous: the real modules trip their own pattern', () => {
    const files = productionSources();
    const code = files.get('config/featureFlags.ts') ?? '';
    // Every wire spelling the vocabulary declares is really written in the one
    // file allowed to declare it: a rename that broke the map would leave the
    // gate with nothing to match.
    for (const wireKey of meFeatureFlagWireKeys()) {
      expect(code).toContain(wireKey);
    }
    const env = files.get('config/env.ts') ?? '';
    for (const name of DEPLOY_CONFIG_ALLOWLIST.filter((key) => key.startsWith('VITE_ENABLE_'))) {
      expect(env).toContain(name);
    }
    expect(files.get('features/auth/api.ts') ?? '').toContain("'/me'");
    expect(files.get('hooks/useFeatureFlag.ts') ?? '').toContain('fetchMeDocument(');
  });

  /**
   * The required unit suite, found by the path Task 048 names.
   *
   * This is a POINTER, not evidence: vitest treats a positional filter as a
   * path pattern, so `npm run test -- src/hooks/useFeatureFlag` selects this
   * file and cannot select `hooks/__tests__/useFeatureFlag.test.tsx` - a filter
   * that cannot reach the suite the task asked for would make the task's own
   * validation command pass on a suite that had been deleted or gutted. So the
   * validation-selected file checks that the suite is still there and still
   * names the four cases the task listed. The suite itself runs in the full
   * `npm run test`, and `npm run test -- src/hooks/__tests__/useFeatureFlag`
   * runs it on its own.
   */
  it('the unit suite the task names exists and still names its four cases', () => {
    const required = join(SRC, 'hooks', '__tests__', 'useFeatureFlag.test.tsx');
    expect(existsSync(required)).toBe(true);
    const source = readFileSync(required, 'utf8');
    expect(source).toContain("from '../useFeatureFlag.js'");
    for (const title of [
      // true / false straight out of `/me`
      'reports true and false from /me for every stated flag',
      // missing flag, unknown key
      'is off for a key this build has never heard of',
      // `/me` wins over bootstrap
      'prefers /me over bootstrap where both speak',
      // flag ON, permission OFF, still denied
      'denies a flag-ON session with a non-elevated permission list',
    ]) {
      expect(source).toContain(`it('${title}'`);
    }
  });
});

// =============================================================================
// R5 — an admin surface and an enrichment surface both call the hook
// =============================================================================

describe('R5 the hook is wired into the admin and enrichment surfaces', () => {
  const CALL = /\buseFeatureFlag\s*\(/g;

  function callSites(area: string): readonly string[] {
    const prefix = join(SRC, area);
    const found: string[] = [];
    for (const [file, code] of productionSources()) {
      if (!file.startsWith(relative(SRC, prefix))) {
        continue;
      }
      const count = (code.match(CALL) ?? []).length;
      for (let index = 0; index < count; index += 1) {
        found.push(file);
      }
    }
    return found;
  }

  it('an admin surface calls it', () => {
    // Task 036's AdminPage decides the operator-only local-GPU section with the
    // shared hook; the section's panel re-reads the same flag itself.
    expect(callSites('features/admin')).toContain('features/admin/AdminPage.tsx');
    expect(callSites('features/admin')).toContain('features/admin/LocalGpuPanel.tsx');
  });

  it('an enrichment surface calls it', () => {
    expect(callSites('features/enrichment')).toContain('features/enrichment/EnrichmentGate.tsx');
  });

  it('and the complete set of call sites is the three this task wired', () => {
    // A call site is fine; a second evaluation rule is not. The four rules
    // above are what make that structural - this assertion is the readable
    // version of them, and it is exhaustive so a new surface has to be added
    // here on purpose rather than appearing by accident.
    const files = productionSources();
    const callers = [...files.keys()]
      .filter((file) => file !== 'hooks/useFeatureFlag.ts')
      .filter((file) => (files.get(file) ?? '').match(CALL) !== null)
      .sort();
    expect(callers).toEqual([
      'features/admin/AdminPage.tsx',
      'features/admin/LocalGpuPanel.tsx',
      'features/enrichment/EnrichmentGate.tsx',
    ]);
  });
});