// Task 039A R4: test-ownership presence gate.
//
// Enforces `docs/test-ownership.md`: every feature area must have at least one
// spec. An area passes when (a) a `*.test.*`/`*.spec.*` file exists under its
// directory, or (b) at least one spec anywhere under `frontend/src` imports
// the area (covers shared state such as `src/stores`, exercised via importing
// suites). A missing area fails with its owning task ID:
//
//   PRESENCE_GAP:<area> owned by <task> (no spec under <area> and no spec imports it)
//
// This gate authors no specs; it reports the gap for 039B/039C (frontend) and
// 039C (backend). Excluded by policy (see docs/test-ownership.md):
// `src/api/generated` (generated, Task 014), `src/types` + `src/styles`
// (no runtime logic).

import { readdirSync, readFileSync, statSync } from 'node:fs';
import { dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const frontendSrc = resolve(scriptsDir, '..', 'frontend', 'src');

const SPEC_RE = /\.(test|spec)\.(ts|tsx)$/;

// area: directory relative to frontend/src. owner: owning execution task ID.
const AREAS = [
  { area: 'src/app', owner: '018' },
  { area: 'src/components', owner: '016' },
  { area: 'src/api', owner: '017' },
  { area: 'src/hooks', owner: '026' },
  { area: 'src/i18n', owner: '018' },
  { area: 'src/lib', owner: '015' },
  { area: 'src/mocks', owner: '039A' },
  { area: 'src/stores', owner: '018' },
  { area: 'src/telemetry', owner: '038' },
  { area: 'src/features/activity', owner: '035A' },
  { area: 'src/features/admin', owner: '036' },
  { area: 'src/features/auth', owner: '019' },
  { area: 'src/features/cost', owner: '035A' },
  { area: 'src/features/dashboard', owner: '020' },
  { area: 'src/features/exports', owner: '033' },
  { area: 'src/features/notifications', owner: '034' },
  { area: 'src/features/processing', owner: '024' },
  { area: 'src/features/projects', owner: '021' },
  { area: 'src/features/quality', owner: '032' },
  { area: 'src/features/review', owner: '031' },
  { area: 'src/features/settings', owner: '035B' },
  { area: 'src/features/timeline', owner: '030' },
  { area: 'src/features/transcript', owner: '027' },
  { area: 'src/features/translation', owner: '028' },
  { area: 'src/features/uploads', owner: '023' },
  { area: 'src/features/voices', owner: '029' },
];

function walk(dir, out) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      if (entry === 'node_modules') {
        continue;
      }
      walk(full, out);
      continue;
    }
    out.push(full);
  }
}

const allFiles = [];
walk(frontendSrc, allFiles);
const specFiles = allFiles.filter((file) => SPEC_RE.test(file));

const failures = [];
for (const { area, owner } of AREAS) {
  const prefix = join(frontendSrc, area.split('/').slice(1).join('/'));
  const direct = specFiles.filter((file) => file === prefix || file.startsWith(`${prefix}${sep}`));
  if (direct.length > 0) {
    continue;
  }
  const base = area.split('/').at(-1) ?? area;
  const importRe = new RegExp(`(from\\s+['"][^'"]*/${base}/|from\\s+['"][^'"]*/${base}['"]|\\(\\s*['"][^'"]*/${base}/)`);
  const importer = specFiles.find((file) => importRe.test(readFileSync(file, 'utf8')));
  if (importer === undefined) {
    failures.push(`PRESENCE_GAP:${area} owned by ${owner} (no spec under ${area} and no spec imports it)`);
  }
}

if (failures.length > 0) {
  for (const failure of failures) {
    process.stderr.write(`${failure}\n`);
  }
  process.exit(1);
}
process.stdout.write(`PRESENCE_OK:${AREAS.length} areas with specs\n`);
