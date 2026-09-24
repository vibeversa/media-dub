#!/usr/bin/env node
// Drift gate (Task 014): regenerates the TypeScript client into a temp dir
// and diffs it against the committed frontend/src/api/generated/.
// Any difference fails with `API DRIFT: run make generate-api and commit`.
// Hermetic (Node stdlib only); also verifies the OPENAPI_VERSION stamp hash
// matches the current bundle, so editing the bundle without regenerating
// fails even before file comparison.
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const bundlePath = path.join(repoRoot, 'src', 'DubbingPlatform.Api', 'OpenApi', 'openapi.v1.json');
const committedDir = path.join(repoRoot, 'frontend', 'src', 'api', 'generated');
const generateScript = path.join(here, 'generate-client.mjs');

function fail(lines) {
  console.error('API DRIFT: run make generate-api and commit');
  for (const line of lines) {
    console.error('  ' + line);
  }
  process.exit(1);
}

function listFiles(dir) {
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  const files = [];
  for (const entry of entries.sort((a, b) => (a.name < b.name ? -1 : a.name > b.name ? 1 : 0))) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      for (const nested of listFiles(full)) {
        files.push(path.join(entry.name, nested));
      }
    } else {
      files.push(entry.name);
    }
  }
  return files;
}

if (!fs.existsSync(bundlePath)) {
  console.error('check-api-drift: bundle not found at ' + path.relative(repoRoot, bundlePath));
  process.exit(1);
}
if (!fs.existsSync(committedDir)) {
  fail(['committed output missing: ' + path.relative(repoRoot, committedDir)]);
}

const bundleBytes = fs.readFileSync(bundlePath);
const bundleHash = createHash('sha256').update(bundleBytes).digest('hex');
const stampPath = path.join(committedDir, 'OPENAPI_VERSION');
if (!fs.existsSync(stampPath)) {
  fail(['committed OPENAPI_VERSION stamp is missing.']);
}
const stamp = fs.readFileSync(stampPath, 'utf8');
if (!stamp.includes('bundle=sha256:' + bundleHash)) {
  fail(['bundle hash mismatch: openapi.v1.json changed without regenerating (expected bundle=sha256:' + bundleHash + ').']);
}

const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'api-generated-'));
try {
  execFileSync(process.execPath, [generateScript, '--out=' + tempDir], { cwd: repoRoot, stdio: 'pipe' });
} catch (err) {
  const detail = err && err.stderr ? String(err.stderr) : String((err && err.message) || err);
  console.error('check-api-drift: regeneration failed:\n' + detail);
  process.exit(1);
}

const committed = new Set(listFiles(committedDir));
const fresh = new Set(listFiles(tempDir));
const diffs = [];
for (const name of [...committed].sort()) {
  if (!fresh.has(name)) {
    diffs.push('removed: ' + name);
  }
}
for (const name of [...fresh].sort()) {
  if (!committed.has(name)) {
    diffs.push('added: ' + name);
  }
}
for (const name of [...committed].sort()) {
  if (!fresh.has(name)) {
    continue;
  }
  const a = fs.readFileSync(path.join(committedDir, name));
  const b = fs.readFileSync(path.join(tempDir, name));
  if (!a.equals(b)) {
    diffs.push('changed: ' + name);
  }
}
fs.rmSync(tempDir, { recursive: true, force: true });

if (diffs.length > 0) {
  fail(diffs);
}
console.log('check-api-drift: generated client matches the committed bundle.');
