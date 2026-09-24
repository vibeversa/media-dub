#!/usr/bin/env node
// Token gate (Task 016, R1): no hardcoded hex outside tokens.css.
// Scans frontend/src, frontend/.storybook, configs for #[0-9a-fA-F]{3,8}.
// tokens.css is the only allowed location. Exits non-zero on violation.
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const frontendRoot = path.join(repoRoot, 'frontend');

const SCAN_ROOTS = [
  path.join(frontendRoot, 'src'),
  path.join(frontendRoot, '.storybook'),
  path.join(frontendRoot, 'tailwind.config.ts'),
  path.join(frontendRoot, 'vite.config.ts'),
  path.join(frontendRoot, 'postcss.config.js'),
  path.join(frontendRoot, 'eslint.config.js'),
];

const HEX_RE = /#[0-9a-fA-F]{3,8}\b/g;
const ALLOWED_FILE = path.join(frontendRoot, 'src', 'styles', 'tokens.css');

function* walk(dir) {
  let entries = [];
  try {
    entries = fs.readdirSync(dir, { withFileTypes: true });
  } catch {
    return;
  }
  for (const entry of entries) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === 'node_modules' || entry.name === 'dist' || entry.name === 'storybook-static') {
        continue;
      }
      yield* walk(full);
    } else if (/\.(ts|tsx|js|mjs|cjs|css)$/.test(entry.name)) {
      yield full;
    }
  }
}

const violations = [];
for (const root of SCAN_ROOTS) {
  let stat = null;
  try {
    stat = fs.statSync(root);
  } catch {
    continue;
  }
  const files = stat.isDirectory() ? [...walk(root)] : [root];
  for (const file of files) {
    if (path.resolve(file) === path.resolve(ALLOWED_FILE)) {
      continue;
    }
    // Generated API client is owned by Task 014; skip it.
    if (file.includes(`${path.sep}api${path.sep}generated${path.sep}`)) {
      continue;
    }
    const text = fs.readFileSync(file, 'utf8');
    const matches = text.match(HEX_RE);
    if (matches) {
      violations.push(`${path.relative(repoRoot, file)}: ${[...new Set(matches)].join(', ')}`);
    }
  }
}

if (violations.length > 0) {
  console.error('TOKEN DRIFT: hardcoded hex outside frontend/src/styles/tokens.css');
  for (const v of violations) {
    console.error(`  ${v}`);
  }
  process.exit(1);
}
console.log('check-no-hex: no hardcoded hex outside tokens.css.');
