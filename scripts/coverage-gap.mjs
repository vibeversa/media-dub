// Task 039A: coverage-gap reporter for the frontend and the backend unit
// suite. Task 039C adds the `--backend` mode.
//
// Frontend (default): reads `frontend/coverage/coverage-summary.json` (emitted
// by `npm run test --prefix frontend -- --coverage` via the `json-summary`
// reporter in `frontend/vite.config.ts`).
//
// Backend (`--backend`): reads every `tests/TestResults/**\/coverage.json`
// emitted by coverlet (`dotnet test --filter FullyQualifiedName~UnitTests
// --collect:"XPlat Code Coverage" --settings tests/coverage.runsettings`),
// merges the per-module payloads, and reports only the files in the 039C
// unit scope declared in `BACKEND_SCOPE` below. Everything outside that scope
// is a recorded exclusion with an owner and an expiry in `docs/coverage.md`
// (hosting/composition, EF migrations, DTO record shapes, and any type whose
// collaborators need Postgres, Redis, RabbitMQ, object storage, or a live
// HTTP client — per task 039C instruction 3 those are re-tagged Integration
// and owned by 006-013 / 040, never by a unit test).
//
// Contract:
// - One `COVERAGE_GAP:<path> <metric>=<pct>...` line per below-target file
//   (paths + counts only; never source excerpts, per the security policy).
// - Empty stdout + exit 0 means the 80% target is met (039B/039C are done).
// - This script is a report, not a gate: it exits 0 even with gaps. The gates
//   are the vitest `thresholds` in `vite.config.ts` and
//   `scripts/presence-gate.mjs`.
// - Missing input or tool version drift fails loudly (exit 2) with a version
//   message, never a silent zero-coverage pass.

import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const TARGET_PCT = 80;
const METRICS = ['lines', 'branches', 'functions', 'statements'];
const BACKEND_METRICS = ['lines', 'branches'];

const scriptsDir = dirname(fileURLToPath(import.meta.url));
const repoRoot = resolve(scriptsDir, '..');
const frontendDir = join(repoRoot, 'frontend');
const backendMode = process.argv.slice(2).includes('--backend');

/**
 * 039C unit scope (task file instruction 1 + Context list). Each entry is a
 * repo-relative path or directory prefix. A file in scope is reported below
 * `TARGET_PCT`; a file outside is a documented exclusion, not a gap.
 * Mirrored in `docs/coverage.md` ("039C unit scope").
 */
const BACKEND_SCOPE = [
  // Status mapping and lifecycle state machines.
  'src/DubbingPlatform.Domain/Entities/ProjectStatusProjection.cs',
  'src/DubbingPlatform.Application/StateMachines/',
  // Permission evaluation.
  'src/DubbingPlatform.Application/Authorization/',
  // Settings-schema validation and configuration hashing.
  'src/DubbingPlatform.Application/Validation/',
  'src/DubbingPlatform.Application/Projects/ProjectSettingsGuard.cs',
  'src/DubbingPlatform.Application/Projects/ProjectConfigHash.cs',
  'src/DubbingPlatform.Application/Projects/ProjectExceptions.cs',
  'src/DubbingPlatform.Application/Configuration/ConfigurationHashCalculator.cs',
  'src/DubbingPlatform.Application/Configuration/ExecutionSnapshotCalculator.cs',
  'src/DubbingPlatform.Application/Options/ProviderEndpointValidator.cs',
  'src/DubbingPlatform.Application/Options/ProviderOptions.cs',
  'src/DubbingPlatform.Application/Options/MockBehaviorOptions.cs',
  'src/DubbingPlatform.Application/Options/LocalInferenceOptions.cs',
  'src/DubbingPlatform.Application/Options/QuotaOptions.cs',
  'src/DubbingPlatform.Application/Options/DiagnosticsOptionsHolder.cs',
  'src/DubbingPlatform.Application/Options/AuthOptions.cs',
  'src/DubbingPlatform.Application/Options/AuthRateLimitOptions.cs',
  'src/DubbingPlatform.Application/Options/DeploymentOptions.cs',
  'src/DubbingPlatform.Application/Options/CorsOptions.cs',
  'src/DubbingPlatform.Application/Options/SecurityOptions.cs',
  'src/DubbingPlatform.Application/Options/TranslationOptions.cs',
  'src/DubbingPlatform.Application/Options/TtsOptions.cs',
  'src/DubbingPlatform.Application/Options/MixingOptions.cs',
  'src/DubbingPlatform.Application/Options/PreviewOptions.cs',
  'src/DubbingPlatform.Application/Options/SegmentOptions.cs',
  'src/DubbingPlatform.Application/Options/GoogleProviderOptions.cs',
  'src/DubbingPlatform.Application/Options/OpenAiProviderOptions.cs',
  'src/DubbingPlatform.Application/Options/AzureProviderOptions.cs',
  // Selection-version support.
  'src/DubbingPlatform.Domain/Entities/SegmentSelection.cs',
  'src/DubbingPlatform.Domain/Entities/SegmentOverlap.cs',
  'src/DubbingPlatform.Domain/Entities/SegmentContextAssignment.cs',
  'src/DubbingPlatform.Domain/Entities/OverlapGroup.cs',
  'src/DubbingPlatform.Domain/Entities/StageUnitCompletion.cs',
  'src/DubbingPlatform.Domain/Entities/TranscriptVersion.cs',
  'src/DubbingPlatform.Domain/Entities/TranslationVersion.cs',
  'src/DubbingPlatform.Application/Segments/SegmentApiExceptions.cs',
  // Notification dedup and recipient resolution.
  'src/DubbingPlatform.Application/Notifications/',
  'src/DubbingPlatform.Domain/Entities/Notification.cs',
  // Voice-preview quota and consent evaluation.
  'src/DubbingPlatform.Domain/Voice/ConsentGate.cs',
  'src/DubbingPlatform.Domain/Entities/VoicePreviewJob.cs',
  'src/DubbingPlatform.Domain/Entities/SpeakerVoiceAssignment.cs',
  'src/DubbingPlatform.Domain/Entities/ConsentRecord.cs',
  'src/DubbingPlatform.Domain/Entities/VoiceProfile.cs',
  // `PreviewAudio` is the pure WAV/duration helper. The three orchestration
  // services that drive a `DbContext` (and call `ExecuteSqlRawAsync`, which no
  // in-memory provider supports) are recorded exclusions — see docs/coverage.md.
  'src/DubbingPlatform.Application/Previews/PreviewAudio.cs',
  'src/DubbingPlatform.Application/Voices/VoiceCompatibility.cs',
  'src/DubbingPlatform.Application/Voices/VoiceApiExceptions.cs',
  // Diagnostics aggregation math and access predicates.
  'src/DubbingPlatform.Application/Diagnostics/',
  // Error-code mapping.
  'src/DubbingPlatform.Application/Errors/',
  'src/DubbingPlatform.Application/Exceptions/',
  'src/DubbingPlatform.Api/Errors/',
  'src/DubbingPlatform.Api/Middleware/ErrorResponse.cs',
  'src/DubbingPlatform.Domain/Exceptions/',
  // Idempotency-key handling and retention math.
  'src/DubbingPlatform.Application/Processing/ProcessingIdempotency.cs',
  'src/DubbingPlatform.Application/Services/IdempotencyInProgressException.cs',
  'src/DubbingPlatform.Application/Services/IdempotencyRetention.cs',
  'src/DubbingPlatform.Domain/Entities/IdempotencyRecord.cs',
  // Correlation propagation.
  'src/DubbingPlatform.Api/Middleware/CorrelationIdMiddleware.cs',
  'src/DubbingPlatform.Api/Middleware/CorrelationMiddleware.cs',
  // Signed-URL expiry and storage-key policy helpers.
  'src/DubbingPlatform.Application/Storage/SignedUrlPolicy.cs',
  'src/DubbingPlatform.Application/Storage/StorageKeyBuilder.cs',
  'src/DubbingPlatform.Api/Services/SignedUrlService.cs',
  // Formatting, export generation, and duration/timeline math.
  'src/DubbingPlatform.Application/Exports/SrtGenerator.cs',
  'src/DubbingPlatform.Application/Exports/WebVttGenerator.cs',
  'src/DubbingPlatform.Application/Exports/TimelineJsonGenerator.cs',
  'src/DubbingPlatform.Application/Exports/TranscriptJsonGenerator.cs',
  'src/DubbingPlatform.Application/Exports/TranslationJsonGenerator.cs',
  'src/DubbingPlatform.Application/Exports/SpeakerMetadataGenerator.cs',
  'src/DubbingPlatform.Application/Exports/QualityReportGenerator.cs',
  'src/DubbingPlatform.Application/Exports/ExportFormatParser.cs',
  'src/DubbingPlatform.Application/Exports/ExportProfileValidator.cs',
  'src/DubbingPlatform.Application/Services/DurationEstimator.cs',
  'src/DubbingPlatform.Application/Services/GuidUtility.cs',
  'src/DubbingPlatform.Domain/ValueObjects/TimingWindow.cs',
  'src/DubbingPlatform.Domain/ValueObjects/TimeRange.cs',
  'src/DubbingPlatform.Domain/ValueObjects/Money.cs',
  'src/DubbingPlatform.Domain/ValueObjects/ContentHash.cs',
  'src/DubbingPlatform.Domain/ValueObjects/ProviderModelReference.cs',
  'src/DubbingPlatform.Domain/ValueObjects/ConfigurationHash.cs',
  'src/DubbingPlatform.Domain/ValueObjects/ExecutionSnapshotHash.cs',
  'src/DubbingPlatform.Domain/ValueObjects/PromptHash.cs',
  'src/DubbingPlatform.Domain/ValueObjects/ProviderRouteHash.cs',
  // Query construction, tenant scoping, and pagination.
  'src/DubbingPlatform.Application/Common/PaginatedResult.cs',
  'src/DubbingPlatform.Application/Projects/ProjectListQuery.cs',
  'src/DubbingPlatform.Application/Security/RedisKeys.cs',
  'src/DubbingPlatform.Application/MultiTenancy/TenantGuard.cs',
  'src/DubbingPlatform.Domain/Identity/PublicIdMapper.cs',
  'src/DubbingPlatform.Api/Models/PublicIdParser.cs',
  // Security policy helpers.
  'src/DubbingPlatform.Application/Security/SecretPolicy.cs',
  'src/DubbingPlatform.Application/Security/SecretRedactor.cs',
  // Enrichment gating and SSE payload policy.
  'src/DubbingPlatform.Application/Enrichment/',
  'src/DubbingPlatform.Api/Sse/',
  // Activity projection/mapping.
  'src/DubbingPlatform.Application/Activity/',
  'src/DubbingPlatform.Domain/Entities/ActivityEvent.cs',
  // DTO request validation.
  'src/DubbingPlatform.Api/Validation/DtoValidators.cs',
];

/** True when a repo-relative backend path belongs to the 039C unit scope. */
function inBackendScope(rel) {
  return BACKEND_SCOPE.some((entry) => (entry.endsWith('/') ? rel.startsWith(entry) : rel === entry));
}

function fail(message) {
  process.stderr.write(`${message}\n`);
  process.exit(2);
}

function toRepoRelative(path) {
  const rel = relative(repoRoot, path).replace(/\\/g, '/');
  return rel.startsWith('..') ? path.replace(/\\/g, '/') : rel;
}

function findCoverageJson(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      findCoverageJson(full, out);
    } else if (entry === 'coverage.json') {
      out.push(full);
    }
  }
  return out;
}

function findCoverageCobertura(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) {
      findCoverageCobertura(full, out);
    } else if (entry === 'coverage.cobertura.xml') {
      out.push(full);
    }
  }
  return out;
}

/** Candidate roots: the runsettings run writes `tests/TestResults`. */
function coverageRoots() {
  const roots = [join(repoRoot, 'tests', 'TestResults')];
  const testsDir = join(repoRoot, 'tests');
  if (existsSync(testsDir)) {
    for (const entry of readdirSync(testsDir)) {
      const nested = join(testsDir, entry, 'TestResults');
      if (existsSync(nested)) {
        roots.push(nested);
      }
    }
  }
  return roots.filter((root) => existsSync(root));
}

/**
 * Parses a coverlet `json` payload into `{ lines: Map, branches: Map }` keyed
 * by absolute source path, summing hits across test projects. A branch is keyed
 * by `Line:Path:Ordinal` so a branch instrumented by four projects is still
 * counted once in the denominator.
 */
function mergeJsonPayloads(payloads) {
  const merged = new Map();
  for (const file of payloads) {
    const payload = JSON.parse(readFileSync(file, 'utf8'));
    for (const byFile of Object.values(payload)) {
      for (const [path, classes] of Object.entries(byFile)) {
        const target = merged.get(path) ?? { lines: new Map(), branches: new Map() };
        merged.set(path, target);
        for (const methods of Object.values(classes)) {
          for (const data of Object.values(methods)) {
            for (const [line, hits] of Object.entries(data.Lines ?? {})) {
              // `max`, not `sum`: the same instrument appears in every test
              // project's payload, and summing would double-count the
              // denominator once per project.
              target.lines.set(line, Math.max(target.lines.get(line) ?? 0, hits));
            }
            for (const branch of data.Branches ?? []) {
              const key = `${branch.Line}:${branch.Path}:${branch.Ordinal}`;
              // Coverlet's json schema carries `Hits`; tolerate `Count` too.
              const hits = branch.Hits ?? branch.Count ?? 0;
              target.branches.set(key, Math.max(target.branches.get(key) ?? 0, hits));
            }
          }
        }
      }
    }
  }
  return merged;
}

function decodeXml(value) {
  return value
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

/**
 * Parses a coverlet `cobertura` payload. Used when the run was collected
 * without `tests/coverage.runsettings` (the task's validation command), where
 * coverlet writes `tests/<Project>/TestResults/coverage.cobertura.xml` and no
 * json. Two shape details are handled explicitly:
 * - `filename` is emitted relative to `src/` (e.g. `DubbingPlatform.Api\Errors\
 *   ApiError.cs`), so it is re-rooted onto the repo's `src/` directory to
 *   produce the same absolute keys the json reader yields.
 * - `condition-coverage="50% (1/2)"` gives exact per-line branch totals, so the
 *   branch ratio matches the json reader instead of averaging per-class rates.
 */
function mergeCoberturaPayloads(payloads) {
  const merged = new Map();
  for (const file of payloads) {
    const xml = readFileSync(file, 'utf8');
    for (const classMatch of xml.matchAll(/<class\b[^>]*\bfilename="([^"]+)"[^>]*>([\s\S]*?)<\/class>/g)) {
      const rel = decodeXml(classMatch[1]).replace(/\\/g, '/');
      const path = rel.startsWith('src/') ? rel : `src/${rel}`;
      const absolute = join(repoRoot, ...path.split('/'));
      const target = merged.get(absolute) ?? { lines: new Map(), branches: new Map() };
      merged.set(absolute, target);
      for (const lineMatch of classMatch[2].matchAll(/<line\b[^>]*>/g)) {
        const tag = lineMatch[0];
        const number = /\bnumber="(\d+)"/.exec(tag)?.[1];
        if (number === undefined) {
          continue;
        }
        const hits = Number.parseInt(/\bhits="(\d+)"/.exec(tag)?.[1] ?? '0', 10);
        // `max`, not `sum`: the same instrument appears in every test project's
        // payload, and summing would double-count the denominator per project.
        target.lines.set(number, Math.max(target.lines.get(number) ?? 0, hits));
        const condition = /\bcondition-coverage="[^"]*\((\d+)\/(\d+)\)"/.exec(tag);
        if (condition !== null) {
          const key = `c:${number}`;
          const existing = target.branches.get(key) ?? { covered: 0, total: 0 };
          existing.covered = Math.max(existing.covered, Number.parseInt(condition[1], 10));
          existing.total = Math.max(existing.total, Number.parseInt(condition[2], 10));
          target.branches.set(key, existing);
        }
      }
    }
  }
  return merged;
}

/** Merges every coverlet payload and reports the in-scope files below target. */
function reportBackend() {
  const roots = coverageRoots();
  if (roots.length === 0) {
    fail(
      'COVERAGE_SUMMARY_MISSING: no tests/**/TestResults found; run ' +
        "'dotnet test --filter FullyQualifiedName~UnitTests --collect:\"XPlat Code Coverage\" first.",
    );
  }
  const jsonPayloads = roots.flatMap((root) => findCoverageJson(root));
  const coberturaPayloads = roots.flatMap((root) => findCoverageCobertura(root));
  if (jsonPayloads.length === 0 && coberturaPayloads.length === 0) {
    fail(
      'COVERAGE_SUMMARY_MISSING: no coverage.json / coverage.cobertura.xml under tests/**/TestResults; run ' +
        "'dotnet test --filter FullyQualifiedName~UnitTests --collect:\"XPlat Code Coverage\" first.",
    );
  }

  // `json` first (the runsettings run); cobertura is the fallback for the plain
  // collector run, which emits no json. Both yield the same merged shape.
  const merged = jsonPayloads.length > 0
    ? mergeJsonPayloads(jsonPayloads)
    : mergeCoberturaPayloads(coberturaPayloads);

  const lines = [];
  for (const [path, target] of merged) {
    const rel = toRepoRelative(path);
    if (!inBackendScope(rel)) {
      continue;
    }
    const lineTotal = target.lines.size;
    const lineCovered = [...target.lines.values()].filter((hits) => hits > 0).length;
    // The json reader stores a hit count; the cobertura reader a {covered,total}.
    const branchCovered = [...target.branches.values()].reduce(
      (sum, b) => sum + (typeof b === 'number' ? (b > 0 ? 1 : 0) : b.covered),
      0,
    );
    const branchTotal = [...target.branches.values()].reduce(
      (sum, b) => sum + (typeof b === 'number' ? 1 : b.total),
      0,
    );
    const below = [];
    const linePct = lineTotal === 0 ? 100 : (lineCovered / lineTotal) * 100;
    if (linePct < TARGET_PCT) {
      below.push(`lines=${linePct.toFixed(1)}`);
    }
    if (branchTotal > 0) {
      const branchPct = (branchCovered / branchTotal) * 100;
      if (branchPct < TARGET_PCT) {
        below.push(`branches=${branchPct.toFixed(1)}`);
      }
    }
    if (below.length > 0) {
      lines.push(`COVERAGE_GAP:${rel} ${below.join(' ')}`);
    }
  }
  lines.sort();
  for (const line of lines) {
    process.stdout.write(`${line}\n`);
  }
}

if (backendMode) {
  reportBackend();
  process.exit(0);
}

function majorOf(version) {
  const cleaned = version.trim().replace(/^[~^>=< ]+/, '');
  const match = /^(\d+)\./.exec(cleaned);
  return match ? match[1] : undefined;
}

// Coverage tool version drift: vitest and @vitest/coverage-v8 must share a
// major (both lockfile-pinned in frontend/package.json).
let pkg;
try {
  pkg = JSON.parse(readFileSync(join(frontendDir, 'package.json'), 'utf8'));
} catch {
  fail('COVERAGE_TOOL_VERSION_MISMATCH: cannot read frontend/package.json.');
}
const devDeps = pkg.devDependencies ?? {};
const vitestMajor = devDeps['vitest'] === undefined ? undefined : majorOf(String(devDeps['vitest']));
const coverageMajor =
  devDeps['@vitest/coverage-v8'] === undefined ? undefined : majorOf(String(devDeps['@vitest/coverage-v8']));
if (vitestMajor === undefined || coverageMajor === undefined) {
  fail(
    `COVERAGE_TOOL_VERSION_MISMATCH: vitest (${devDeps['vitest'] ?? 'missing'}) and ` +
      `@vitest/coverage-v8 (${devDeps['@vitest/coverage-v8'] ?? 'missing'}) must both be pinned in frontend/package.json.`,
  );
}
if (vitestMajor !== coverageMajor) {
  fail(
    `COVERAGE_TOOL_VERSION_MISMATCH: vitest major ${vitestMajor} != @vitest/coverage-v8 major ${coverageMajor}; ` +
      `align the lockfile-pinned versions (see docs/coverage.md).`,
  );
}
if (devDeps['msw'] === undefined) {
  fail('COVERAGE_TOOL_VERSION_MISMATCH: msw must be pinned in frontend/package.json (see frontend/src/mocks/README.md).');
}

let summary;
try {
  summary = JSON.parse(readFileSync(join(frontendDir, 'coverage', 'coverage-summary.json'), 'utf8'));
} catch {
  fail(
    'COVERAGE_SUMMARY_MISSING: frontend/coverage/coverage-summary.json not found; ' +
      "run 'npm run test --prefix frontend -- --coverage' first.",
  );
}

const lines = [];
for (const key of Object.keys(summary).sort()) {
  if (key === 'total') {
    continue;
  }
  const entry = summary[key];
  const rel = relative(frontendDir, key).replace(/\\/g, '/');
  const display = rel.startsWith('..') ? key : rel;
  const below = [];
  for (const metric of METRICS) {
    const pct = entry?.[metric]?.pct;
    if (typeof pct !== 'number') {
      fail(`COVERAGE_SUMMARY_MALFORMED: ${display} is missing metric ${metric}.`);
    }
    if (pct < TARGET_PCT) {
      below.push(`${metric}=${pct.toFixed(1)}`);
    }
  }
  if (below.length > 0) {
    lines.push(`COVERAGE_GAP:${display} ${below.join(' ')}`);
  }
}

for (const line of lines) {
  process.stdout.write(`${line}\n`);
}
