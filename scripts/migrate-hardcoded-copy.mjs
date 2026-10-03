#!/usr/bin/env node
// One-shot i18n migration for GAP-022.
//
// Moves the hard-coded user-facing copy the extraction gate counts
// (`jsx-text`, `user-facing-prop`, `copy-fallback`) into
// `frontend/src/i18n/locales/en/<ns>.json` and rewrites the call sites to
// `t('ns:key')`. The English value is the literal verbatim, so every rendered
// string — and therefore every existing `getByText` assertion — is unchanged.
//
// Mechanical by construction, which is the point: it rewrites AST positions it
// can prove, and refuses (with a report) anything it cannot prove. Run it with
// `--dry` first; it is idempotent, so re-running after hand-fixes is safe.
//
// Usage: node scripts/migrate-hardcoded-copy.mjs [--dry] [--only <substr>]
import { readFileSync, readdirSync, statSync, writeFileSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { createRequire } from 'node:module';

import {
  ARIA_NAME_ATTRS,
  BASELINE_PATH,
  EXEMPT_FILE_RE,
  FRONTEND_SRC,
  REPO_ROOT,
  USER_FACING_PROPS,
} from './check-no-hardcoded-copy.mjs';

const require = createRequire(import.meta.url);
const ts = require(join(REPO_ROOT, 'frontend', 'node_modules', 'typescript'));

const DRY = process.argv.includes('--dry');
const ONLY = (() => {
  const index = process.argv.indexOf('--only');
  return index >= 0 ? process.argv[index + 1] : undefined;
})();

/** Feature directory → translation namespace. New namespaces are additive. */
const NAMESPACE_BY_DIR = new Map([
  ['activity', 'activity'],
  ['admin', 'admin'],
  ['auth', 'auth'],
  ['cost', 'cost'],
  ['dashboard', 'dashboard'],
  ['enrichment', 'enrichment'],
  ['exports', 'exports'],
  ['notifications', 'notifications'],
  ['processing', 'processing'],
  ['projects', 'projects'],
  ['quality', 'quality'],
  ['review', 'review'],
  ['settings', 'settings'],
  ['timeline', 'timeline'],
  ['transcript', 'transcript'],
  ['translation', 'translation'],
  ['uploads', 'uploads'],
  ['voices', 'voices'],
  ['workspace', 'workspace'],
]);

const COPYISH_NAME =
  /(^|[^a-z])(title|label|message|description|heading|summary|caption|tooltip|placeholder|emptyText|errorText|hint|note|helpText|actionLabel|statusText)([^a-z]|$)/i;

function hasLetter(text) {
  return /\p{L}/u.test(text);
}

function wordCount(text) {
  return text.trim().split(/\s+/).filter(Boolean).length;
}

function isProse(text) {
  return wordCount(text) >= 3 && hasLetter(text);
}

function listFiles(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === 'dist' || entry === 'storybook-static') {
      continue;
    }
    const full = join(dir, entry);
    const stats = statSync(full);
    if (stats.isDirectory()) {
      listFiles(full, out);
    } else if (/\.(ts|tsx)$/.test(entry) && !EXEMPT_FILE_RE.test(entry)) {
      const rel = relative(FRONTEND_SRC, full);
      const top = rel.split('/')[0];
      if (top === 'components' || top === 'i18n' || top === 'api' || top === 'telemetry' || top === 'types' || top === 'mocks' || top === 'config') {
        continue;
      }
      out.push(full);
    }
  }
  return out;
}

function namespaceFor(relativePath) {
  const parts = relativePath.split('/');
  if (parts[0] === 'features' && parts[1] !== undefined) {
    return NAMESPACE_BY_DIR.get(parts[1]) ?? 'common';
  }
  return 'common';
}

/** Key segment from the literal: kebab words, no interpolation, bounded. */
function slugOf(text, fallback) {
  const cleaned = text
    .replace(/\{\{[^}]+\}\}/g, ' ')
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim()
    .toLowerCase();
  const words = cleaned.split(/\s+/).filter(Boolean).slice(0, 6);
  const slug = words.join('-').slice(0, 48);
  return slug === '' ? fallback : slug;
}

function camelFrom(relativePath) {
  const base = relativePath.split('/').pop().replace(/\.tsx?$/, '');
  const camel = base
    .replace(/[^A-Za-z0-9]+(.)/g, (_m, c) => c.toUpperCase())
    .replace(/^[A-Z]/, (c) => c.toLowerCase());
  return camel === '' ? 'copy' : camel;
}

function attributeName(node) {
  const name = node.name;
  if (name === undefined) {
    return '';
  }
  if (ts.isIdentifier(name)) {
    return name.text;
  }
  if (ts.isStringLiteral(name)) {
    return name.text;
  }
  return '';
}

/**
 * Component/hook names the codemod may inject `useTranslation()` into: a
 * capitalized declaration (React components) or a `use*` function (custom
 * hooks, where the hook order is still top-level). Anything else — a plain
 * helper, a formatter, a fetch wrapper — is refused so the migration cannot
 * break the Rules of Hooks.
 */
function canHostTranslationHook(fn, sourceFile) {
  // `export const X = memo(function X() {})` is a component too, so a named
  // function expression counts even when its parent is a `memo(...)` call.
  if (
    !ts.isFunctionDeclaration(fn) &&
    !ts.isFunctionExpression(fn) &&
    !ts.isVariableDeclaration(fn.parent)
  ) {
    return false;
  }
  const name = fn.name ?? (ts.isVariableDeclaration(fn.parent) ? fn.parent.name : undefined);
  if (name === undefined || !ts.isIdentifier(name)) {
    return false;
  }
  if (/^use[A-Z]/.test(name.text)) {
    return true;
  }
  return /^[A-Z]/.test(name.text);
}

/** Nearest enclosing function-like declaration, used to scope the `t` hook. */
function enclosingFunction(node) {
  let current = node.parent;
  while (current !== undefined) {
    if (
      ts.isFunctionDeclaration(current) ||
      ts.isFunctionExpression(current) ||
      ts.isArrowFunction(current) ||
      ts.isMethodDeclaration(current)
    ) {
      return current;
    }
    current = current.parent;
  }
  return undefined;
}

/**
 * Where to insert `const { t } = useTranslation();` in a function body: the
 * first statement's start plus that statement's own indentation, so the
 * inserted line matches the code around it.
 */
function firstStatementInsertion(fn) {
  const body = fn.body;
  if (body === undefined || !ts.isBlock(body)) {
    return undefined;
  }
  const first = body.statements[0];
  if (first === undefined) {
    return undefined;
  }
  const at = first.getStart(sourceOf(fn));
  // Two spaces, the repo's indentation; the statement's own leading trivia
  // already carries the real column, so measuring it here would only inherit
  // a nested-block indent.
  return { at, indent: '  ' };
}

let currentSourceFile = undefined;
function sourceOf(node) {
  return currentSourceFile ?? node.getSourceFile();
}

/**
 * Collects the edits for one file: `{ start, end, replacement, key, value }`.
 * Returns `{ edits, hasTranslation }`; files without `t` in scope are reported
 * rather than rewritten, so the migration never invents a hook it cannot place.
 */
function collectEdits(filePath, text) {
  const kind = filePath.endsWith('.tsx') ? ts.ScriptKind.TSX : ts.ScriptKind.TS;
  const sourceFile = ts.createSourceFile(filePath, text, ts.ScriptTarget.ESNext, true, kind);
  currentSourceFile = sourceFile;
  const relativePath = relative(FRONTEND_SRC, filePath);
  const namespace = namespaceFor(relativePath);
  const prefix = camelFrom(relativePath);
  const importsTranslation = /from ['"]react-i18next['"]/.test(text);
  const hookInsertions = new Map();
  /**
   * Whether `t` is already in scope at `fn`: either this function declares it,
   * or an enclosing function does (a nested component inside a translated one
   * closes over it). Scoped per function, not per file, so a memo-wrapped lane
   * component in a file whose *other* component uses `t` still gets one.
   */
  const coveredByTranslation = (fn) => {
    const source = sourceFile.getFullText();
    let current = fn;
    while (current !== undefined) {
      if (ts.isFunctionLike(current) && current.body !== undefined && ts.isBlock(current.body)) {
        const start = current.body.getStart(sourceFile);
        const end = current.body.getEnd();
        if (/useTranslation\(/.test(source.slice(start, end))) {
          return true;
        }
      }
      current = enclosingFunction(current.parent);
    }
    return false;
  };

  const noteHook = (fn) => {
    if (coveredByTranslation(fn)) {
      return true;
    }
    if (fn === undefined || !canHostTranslationHook(fn, sourceFile)) {
      return false;
    }
    if (!hookInsertions.has(fn)) {
      hookInsertions.set(fn, firstStatementInsertion(fn));
    }
    return true;
  };
  const edits = [];
  const usedKeys = new Set();
  const refused = [];

  const keyFor = (valueText) => {
    const base = `${prefix}.${slugOf(valueText, 'copy')}`;
    let key = base;
    let counter = 2;
    while (usedKeys.has(key)) {
      key = `${base}${String(counter)}`;
      counter += 1;
    }
    usedKeys.add(key);
    return key;
  };

  const visit = (node) => {
    // Rule 1 — JSX text.
    if (ts.isJsxText(node)) {
      const trimmed = node.text === undefined ? '' : node.text.trim();
      if (trimmed !== '' && hasLetter(trimmed)) {
        const host = noteHook(enclosingFunction(node));
        if (!host) {
          refused.push({ line: sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1, kind: 'jsx-text' });
        } else {
          const key = keyFor(trimmed);
          // On a JsxText node `getStart()` already sits on the first
          // non-whitespace character and `getEnd()` includes the trailing
          // whitespace, so the trimmed span is exactly
          // [getStart, getStart + trimmed.length). Replacing that span and
          // nothing else keeps every space the author wrote outside the
          // expression, so the rendered string is byte-identical.
          const start = node.getStart(sourceFile);
          edits.push({
            start,
            end: start + trimmed.length,
            replacement: `{t('${namespace}:${key}')}`,
            key,
            value: trimmed,
          });
        }
      }
    }

    // Rule 2 — user-facing prop carrying a literal or template.
    if (ts.isJsxAttribute(node) && node.initializer !== undefined) {
      const name = attributeName(node);
      if (USER_FACING_PROPS.includes(name) || ARIA_NAME_ATTRS.includes(name)) {
        const init = node.initializer;
        const stringNode = ts.isStringLiteral(init)
          ? init
          : ts.isNoSubstitutionTemplateLiteral(init)
            ? init
            : ts.isJsxExpression(init) &&
                init.expression !== undefined &&
                (ts.isTemplateExpression(init.expression) || ts.isNoSubstitutionTemplateLiteral(init.expression))
              ? init.expression
              : undefined;
        // A TemplateExpression has no `text` (it has `head`/`templateSpans`), so
        // the emptiness check must not require one.
        const isEmpty =
          stringNode !== undefined &&
          ts.isTemplateExpression(stringNode) === false &&
          (stringNode.text === undefined || stringNode.text.trim() === '');
        if (stringNode !== undefined && !isEmpty) {
          const host = noteHook(enclosingFunction(node));
          if (!host) {
            refused.push({ line: sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1, kind: 'user-facing-prop' });
          } else if (ts.isTemplateExpression(stringNode)) {
            // Every `${…}` substitution becomes an i18next `{{…}}` placeholder.
            // An identifier keeps its own name; anything else gets `v0`, `v1`,
            // … and is passed through the interpolation values object, so the
            // rendered string is byte-identical to the template it replaces.
            const source = sourceFile.getFullText();
            const names = [];
            const values = [];
            for (const [index, span] of stringNode.templateSpans.entries()) {
              const expression = span.expression;
              const name = ts.isIdentifier(expression) ? expression.text : `v${String(index)}`;
              names.push(name);
              values.push(`${name}: ${source.slice(expression.getStart(sourceFile), expression.getEnd()).replace(/\s+/g, ' ')}`);
            }
            let value = stringNode.head.text;
            for (const [index, span] of stringNode.templateSpans.entries()) {
              value += `{{${names[index]}}}${span.literal.text}`;
            }
            const key = keyFor(value);
            const options = names.length === 0 ? '' : `, { ${values.join(', ')} }`;
            edits.push({
              start: node.getStart(sourceFile),
              end: node.getEnd(),
              replacement: `${name}={t('${namespace}:${key}'${options})}`,
              key,
              value,
            });
          } else {
            const key = keyFor(stringNode.text.trim());
            edits.push({
              start: node.getStart(sourceFile),
              end: node.getEnd(),
              replacement: `${name}={t('${namespace}:${key}')}`,
              key,
              value: stringNode.text.trim(),
            });
          }
        }
      }
    }

    // Rule 3 — prose literal in a `??`/`||` fallback or a copy-named const.
    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      if (node.text !== undefined && isProse(node.text)) {
        const parent = node.parent;
        const inFallback =
          ts.isBinaryExpression(parent) &&
          parent.right === node &&
          parent.operatorToken !== undefined &&
          (parent.operatorToken.kind === ts.SyntaxKind.QuestionQuestionToken ||
            parent.operatorToken.kind === ts.SyntaxKind.BarBarToken);
        const inCopyConst =
          ts.isVariableDeclaration(parent) &&
          parent.initializer === node &&
          ts.isIdentifier(parent.name) &&
          COPYISH_NAME.test(parent.name.text);
        if (inFallback || inCopyConst) {
          const fn = enclosingFunction(node);
          const line = sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1;
          if (!noteHook(fn) || fn === undefined || firstStatementInsertion(fn) === undefined) {
            refused.push({ line, kind: 'copy-fallback' });
          } else {
            const key = keyFor(node.text.trim());
            edits.push({
              start: node.getStart(sourceFile),
              end: node.getEnd(),
              replacement: `t('${namespace}:${key}')`,
              key,
              value: node.text.trim(),
              needsT: inCopyConst ? fn : undefined,
            });
          }
        }
      }
    }

    ts.forEachChild(node, visit);
  };


  ts.forEachChild(sourceFile, visit);
  currentSourceFile = undefined;
  if (process.env['MIGRATE_DEBUG'] === '1') {
    for (const [fn, at] of hookInsertions) {
      const name = fn.name ? fn.name.text : 'arrow';
      console.error(`hook ${name} at ${JSON.stringify(at)}: ${JSON.stringify(text.slice(Math.max(0, at - 30), at + 30))}`);
    }
  }
  return {
    edits: edits.sort((a, b) => b.start - a.start),
    hookInsertions: [...hookInsertions.entries()].filter(([, at]) => at !== undefined),
    needsImport: !importsTranslation,
    refused,
    namespace,
    relativePath,
  };
}

function applyEdits(text, edits) {
  let out = text;
  for (const edit of edits) {
    out = out.slice(0, edit.start) + edit.replacement + out.slice(edit.end);
  }
  return out;
}

function setNested(bundle, dottedKey, value) {
  const parts = dottedKey.split('.');
  let node = bundle;
  for (const part of parts.slice(0, -1)) {
    if (typeof node[part] !== 'object' || node[part] === null || Array.isArray(node[part])) {
      node[part] = {};
    }
    node = node[part];
  }
  node[parts[parts.length - 1]] = value;
}

const files = listFiles(FRONTEND_SRC)
  .map((filePath) => ({ filePath, relativePath: relative(FRONTEND_SRC, filePath) }))
  .filter((entry) => ONLY === undefined || entry.relativePath.includes(ONLY));

const bundleWrites = new Map();
let changedFiles = 0;
let totalEdits = 0;
const allRefused = [];

for (const { filePath, relativePath } of files) {
  const text = readFileSync(filePath, 'utf8');
  const { edits, refused, namespace, hookInsertions, needsImport } = collectEdits(filePath, text);
  for (const entry of refused) {
    allRefused.push(`${relativePath}:${entry.line} ${entry.kind}`);
  }
  if (edits.length === 0) {
    continue;
  }

  // Hook and import insertions shift every offset after them, so all three
  // edit kinds go into one descending-position pass over the ORIGINAL text.
  const patches = edits.map((edit) => ({ start: edit.start, end: edit.end, replacement: edit.replacement }));
  for (const [, position] of hookInsertions) {
    patches.push({
      start: position.at,
      end: position.at,
      replacement: `${position.indent}const { t } = useTranslation();\n`,
    });
  }
  if (hookInsertions.length > 0 && needsImport) {
    const lastImport = text.match(/^import .*;$/gm);
    const line = "import { useTranslation } from 'react-i18next';";
    if (lastImport !== null && lastImport.length > 0) {
      const anchor = text.lastIndexOf(lastImport[lastImport.length - 1]) + lastImport[lastImport.length - 1].length;
      patches.push({ start: anchor, end: anchor, replacement: `\n${line}` });
    } else {
      patches.push({ start: 0, end: 0, replacement: `${line}\n` });
    }
  }
  // Globally descending: an edit below another must never be applied first,
  // or the positions of the later ones shift out from under it.
  patches.sort((a, b) => b.start - a.start);
  const updated = applyEdits(text, patches);
  totalEdits += edits.length;
  changedFiles += 1;
  if (!DRY) {
    writeFileSync(filePath, updated);
  }

  if (!bundleWrites.has(namespace)) {
    bundleWrites.set(namespace, new Map());
  }
  for (const edit of edits) {
    bundleWrites.get(namespace).set(edit.key, edit.value);
  }
}

/**
 * Registers any namespace bundle the migration created in the resource
 * registry, so a new feature directory cannot leave `t('ns:key')` resolving to
 * the key itself. Idempotent: an already-registered namespace is a no-op.
 */
function registerNamespace(namespace) {
  const registryPath = join(FRONTEND_SRC, 'i18n', 'resources.ts');
  let registry = readFileSync(registryPath, 'utf8');
  // Already wired (import + list + EN_RESOURCES) → nothing to do.
  if (new RegExp(`\\n  ${namespace}: en[A-Z]`).test(registry)) {
    return false;
  }
  const imported = `import en${namespace[0].toUpperCase()}${namespace.slice(1)} from './locales/en/${namespace}.json';`;
  registry = registry.replace(
    "import enActivity from './locales/en/activity.json';",
    `import enActivity from './locales/en/activity.json';\n${imported}`,
  );
  registry = registry.replace("  'activity',\n  'settings',", `  'activity',\n  '${namespace}',\n  'settings',`);
  // Anchored on the `activity` entry so several new namespaces compose instead
  // of fighting over one replacement.
  registry = registry.replace('  activity: enActivity,', `  activity: enActivity,\n  ${namespace}: en${namespace[0].toUpperCase()}${namespace.slice(1)},`);
  writeFileSync(registryPath, registry);
  return true;
}

for (const [namespace, entries] of bundleWrites) {
  if (!DRY && registerNamespace(namespace)) {
    console.log(`registered namespace: ${namespace}`);
  }
  const bundlePath = join(FRONTEND_SRC, 'i18n', 'locales', 'en', `${namespace}.json`);
  const bundle = exists(bundlePath) ? JSON.parse(readFileSync(bundlePath, 'utf8')) : {};
  for (const [key, value] of entries) {
    setNested(bundle, key, value);
  }
  if (!DRY) {
    writeFileSync(bundlePath, `${JSON.stringify(bundle, null, 2)}\n`);
  }
  console.log(`${DRY ? 'would write' : 'wrote'} ${relative(REPO_ROOT, bundlePath)} (+${entries.size} keys)`);
}

// Bundles that already exist on disk still need registering when a previous
// run wrote them (idempotent re-run of this script).
if (!DRY) {
  for (const namespace of NAMESPACE_BY_DIR.values()) {
    const bundlePath = join(FRONTEND_SRC, 'i18n', 'locales', 'en', `${namespace}.json`);
    if (exists(bundlePath) && registerNamespace(namespace)) {
      console.log(`registered namespace: ${namespace}`);
    }
  }
}

console.log(`MIGRATE_COPY ${DRY ? 'DRY-RUN' : 'APPLIED'} files=${changedFiles} literals=${totalEdits} refused=${allRefused.length}`);
for (const line of allRefused) {
  console.log(`  refused: ${line}`);
}
console.log('next: hand-fix the refused lines, then `node scripts/check-no-hardcoded-copy.mjs --write-baseline`.');

function exists(path) {
  try {
    statSync(path);
    return true;
  } catch {
    return false;
  }
}