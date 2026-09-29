// Tests for the dependency-advisory gate (Task 042).
//
// The gate's job is to be actionable, so these cover the two things that make it
// actionable and the one that could make it quietly useless:
//
//   * a real npm 7+ `vulnerabilities` report and a real npm 6 `advisories` report
//     both parse, because a lockfile can be audited by either;
//   * an advisory at a severity the gate cannot rank blocks rather than passes;
//   * CODEOWNERS is resolved last-match-wins, the rule GitHub uses - getting it
//     backwards assigns every finding to the repository-root owner, which is
//     indistinguishable from having no owner at all.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { dirname } from 'node:path';

import {
  OWNER_PLACEHOLDER,
  SEVERITY_ORDER,
  isBlocking,
  isPlaceholderOwner,
  parseAuditReport,
  quoteForShell,
  renderReport,
  resolveNpm,
  resolveOwners,
  severityRank,
} from './npm-audit-gate.mjs';

test('npm is invoked in a way that works on both runners and on a developer host', () => {
  // Three attempts were needed, and the first two were each correct on one
  // platform and broken on the other - the worst possible split for a gate,
  // because it passes in CI and fails on every developer's machine.
  //   1. `spawnSync('npm', ...)`  -> ENOENT on Windows (`npm.cmd`, and Node does
  //      not consult PATHEXT for a bare name).
  //   2. `spawnSync('npm.cmd')`   -> EINVAL on Windows since the CVE-2024-27980
  //      fix: a batch file cannot be spawned without a shell.
  //   3. run npm's own CLI script with the current Node: no shell, same everywhere.
  const withExecPath = resolveNpm({ npm_execpath: '/usr/lib/node_modules/npm/bin/npm-cli.js' }, 'linux', '/usr/bin');
  assert.equal(withExecPath[0].shell, false);
  assert.deepEqual(withExecPath[0].args, ['/usr/lib/node_modules/npm/bin/npm-cli.js']);
  assert.match(withExecPath[0].why, /npm_execpath/);

  // With no npm_execpath, the CLI beside Node is preferred over any shell.
  const besideNode = resolveNpm({}, 'win32', dirname(process.execPath));
  assert.ok(
    besideNode.some((c) => !c.shell),
    'a shell-free candidate must exist before the shell fallback is reached',
  );
  assert.equal(besideNode[besideNode.length - 1].shell, process.platform === 'win32');
  if (process.platform !== 'win32') {
    assert.equal(besideNode[besideNode.length - 1].command, 'npm');
  } else {
    assert.equal(besideNode[besideNode.length - 1].command, 'npm.cmd');
  }
});

test('a path that cannot be quoted safely is refused rather than passed to a shell', () => {
  assert.equal(quoteForShell('C:\\Users\\a\\frontend'), '"C:\\Users\\a\\frontend"');
  assert.equal(quoteForShell('C:\\Users\\My Name\\frontend'), '"C:\\Users\\My Name\\frontend"');
  // A newline in a path is a second command; a double quote is a broken one.
  assert.equal(quoteForShell('a\nb'), null);
  assert.equal(quoteForShell('a"b'), null);
});

test('severity ranks the npm vocabulary and rejects anything else', () => {
  assert.deepEqual(SEVERITY_ORDER, ['info', 'low', 'moderate', 'high', 'critical']);
  assert.equal(severityRank('info'), 0);
  assert.equal(severityRank('CRITICAL'), 4);
  assert.equal(severityRank('spicy'), -1);
  assert.equal(isBlocking('critical', 'high'), true);
  assert.equal(isBlocking('high', 'high'), true, 'the level is inclusive: "fail on high" means high fails');
  assert.equal(isBlocking('moderate', 'high'), false);
  assert.equal(isBlocking('moderate', 'moderate'), true);
});

test('a severity the gate cannot rank blocks, it does not pass', () => {
  // The same rule as the contract gate's level handling. npm inventing a
  // severity must not make every advisory quietly non-blocking.
  assert.equal(isBlocking('spicy', 'critical'), true);
  assert.equal(isBlocking(undefined, 'critical'), true);
  assert.equal(isBlocking('unknown', 'high'), true);
});

test('a real npm 7+ vulnerabilities report parses into per-advisory findings', () => {
  const report = {
    auditReportVersion: 2,
    vulnerabilities: {
      vite: {
        name: 'vite',
        severity: 'high',
        isDirect: true,
        range: '<5.4.12',
        fixAvailable: { name: 'vite', version: '5.4.12', isSemVerMajor: false },
        via: [
          {
            source: 1106949,
            name: 'vite',
            dependency: 'vite',
            title: 'Vite dev server option server.fs.deny can be bypassed',
            url: 'https://github.com/advisories/GHSA-x574-m823-4x7w',
            severity: 'high',
          },
        ],
      },
      esbuild: {
        name: 'esbuild',
        severity: 'moderate',
        isDirect: true,
        range: '<0.21.3',
        fixAvailable: { name: 'esbuild', version: '0.21.3', isSemVerMajor: false },
        via: [
          {
            source: 1106500,
            name: 'esbuild',
            title: 'esbuild enables any website to send requests to the development server',
            url: 'https://github.com/advisories/GHSA-67mh-4wv8-2f99',
            severity: 'moderate',
          },
        ],
      },
      'follow-redirects': {
        name: 'follow-redirects',
        severity: 'low',
        isDirect: false,
        range: '<1.15.4',
        fixAvailable: false,
        via: ['axios'],
      },
    },
  };
  const findings = parseAuditReport(report);
  assert.equal(findings.length, 3);
  // Most severe first, so the top of the report is the thing to act on.
  assert.deepEqual(findings.map((f) => f.severity), ['high', 'moderate', 'low']);
  assert.equal(findings[0].id, '1106949', 'the advisory id is what a reader can search and track');
  assert.match(findings[0].url, /GHSA-/);
  assert.equal(findings[0].module, 'vite');
  assert.equal(findings[0].direct, true);
  assert.match(findings[0].fixAvailable, /upgrade vite to 5\.4\.12/);

  // A transitive package with only a string in `via` has no advisory object, so
  // it is reported against its own name rather than silently dropped - and
  // marked as INHERITED, because its severity is not its own. Reporting it with
  // a fabricated advisory id and a link to a page that would not contain it sends
  // the reader looking for a CVE that was never published.
  const transitive = findings.find((f) => f.module === 'follow-redirects');
  assert.equal(transitive.direct, false);
  assert.equal(transitive.inherited, true);
  assert.match(transitive.id, /severity inherited from a dependency/);
  assert.equal(transitive.url, '', 'an inherited finding must not carry an advisory link');
  assert.equal(transitive.fixAvailable, 'no fix published yet');
  // A package with a real advisory is NOT inherited, and does carry its id.
  assert.equal(findings[0].inherited, false);
  assert.equal(findings[0].id, '1106949');
});

test('the report separates inherited findings and does not link them', () => {
  const findings = parseAuditReport({
    vulnerabilities: {
      eslint: { name: 'eslint', severity: 'low', isDirect: true, range: '9.10.0 - 9.26.0', fixAvailable: true, via: ['@eslint/plugin-kit'] },
      vite: {
        name: 'vite',
        severity: 'moderate',
        isDirect: true,
        range: '<=6.4.2',
        fixAvailable: true,
        via: [{ source: 1107323, name: 'vite', title: 'x', url: 'https://github.com/advisories/GHSA-g4jq-h2w9-997c', severity: 'moderate' }],
      },
    },
  });
  const report = renderReport(findings, 'high', ['@org/frontend'], 'frontend/package-lock.json');
  assert.match(report, /`eslint` \(inherited\)/);
  assert.doesNotMatch(report, /\[`eslint`\]/, 'an inherited finding must not be a link');
  assert.match(report, /GHSA-g4jq-h2w9-997c/);
  assert.match(report, /1 of these have no advisory of their own/);
});

test('a fix that is a major bump is called a migration, not a version bump', () => {
  const findings = parseAuditReport({
    vulnerabilities: {
      react: {
        name: 'react',
        severity: 'high',
        isDirect: true,
        range: '<19.0.0',
        fixAvailable: { name: 'react', version: '19.0.0', isSemVerMajor: true },
        via: [{ source: 1, name: 'react', title: 'x', url: 'https://example.invalid', severity: 'high' }],
      },
    },
  });
  assert.match(findings[0].fixAvailable, /MAJOR bump/);
  assert.match(findings[0].fixAvailable, /migration/);
});

test('a real npm 6 advisories report parses too', () => {
  const report = {
    actions: { 'npm:server-dereference': { severity: 'critical', resolution: { fix: null } } },
    advisories: {
      1000000: {
        id: 1000000,
        module_name: 'lodash',
        severity: 'critical',
        title: 'Prototype Pollution',
        url: 'https://npmjs.com/advisories/1000000',
        findings: [{ version: '4.17.15', paths: ['lodash'] }],
        patched_versions: '>=4.17.19',
      },
    },
  };
  const findings = parseAuditReport(report);
  assert.equal(findings.length, 1);
  assert.equal(findings[0].id, '1000000');
  assert.equal(findings[0].module, 'lodash');
  assert.equal(findings[0].severity, 'critical');
  assert.match(findings[0].fixAvailable, /patched in >=4\.17\.19/);
  assert.equal(findings[0].direct, true, 'a one-element dependency path is a direct dependency');
});

test('an audit report with no advisories produces no findings', () => {
  assert.deepEqual(parseAuditReport({ vulnerabilities: {}, metadata: {} }), []);
  assert.deepEqual(parseAuditReport({ advisories: {} }), []);
  assert.deepEqual(parseAuditReport({}), []);
  assert.deepEqual(parseAuditReport(null), []);
});

test('CODEOWNERS resolves last-match-wins, the rule GitHub uses', () => {
  const codeowners = [
    '# comment lines are ignored',
    '',
    '*                       @org/engineering',
    'frontend/               @org/frontend',
    'frontend/package-lock.json  @org/frontend-platform',
    'deploy/                 @org/platform',
    'deploy/helm/            @org/helm-owners   @org/platform',
  ].join('\n');
  assert.deepEqual(resolveOwners(codeowners, 'src/DubbingPlatform.Api/Program.cs'), ['@org/engineering']);
  assert.deepEqual(resolveOwners(codeowners, 'frontend/src/main.tsx'), ['@org/frontend']);
  // The LAST matching rule wins, which is the whole point: the lockfile has its
  // own owner even though `frontend/` also matches.
  assert.deepEqual(resolveOwners(codeowners, 'frontend/package-lock.json'), ['@org/frontend-platform']);
  assert.deepEqual(resolveOwners(codeowners, 'deploy/helm/dubbing/values.yaml'), ['@org/helm-owners', '@org/platform']);
  assert.deepEqual(resolveOwners(codeowners, 'deploy/k8s/api-deployment.yaml'), ['@org/platform']);
});

test('a CODEOWNERS pattern with no slash matches at any depth', () => {
  const codeowners = '*.sln @org/dotnet\ntools/ @org/tooling';
  assert.deepEqual(resolveOwners(codeowners, 'DubbingPlatform.sln'), ['@org/dotnet']);
  assert.deepEqual(resolveOwners(codeowners, 'tools/openapi-gate.mjs'), ['@org/tooling']);
  assert.deepEqual(resolveOwners(codeowners, 'README.md'), []);
});

test('an absent CODEOWNERS file resolves to no owners rather than to everyone', () => {
  assert.deepEqual(resolveOwners(null, 'frontend/package-lock.json'), []);
  assert.deepEqual(resolveOwners(undefined, 'frontend/package-lock.json'), []);
  assert.deepEqual(resolveOwners('', 'frontend/package-lock.json'), []);
});

test('an unfilled CHANGE_ME owner is reported as unowned, not as a handle', () => {
  const codeowners = 'frontend/ @CHANGE_ME/frontend';
  assert.deepEqual(resolveOwners(codeowners, 'frontend/package-lock.json'), ['@CHANGE_ME/frontend']);
  assert.equal(isPlaceholderOwner('@CHANGE_ME/frontend'), true);
  assert.equal(isPlaceholderOwner('@org/frontend'), false);
});

test('the report names the advisory, the vulnerable range, the fix and the owner', () => {
  const findings = parseAuditReport({
    vulnerabilities: {
      vite: {
        name: 'vite',
        severity: 'critical',
        isDirect: true,
        range: '<5.4.12',
        fixAvailable: false,
        via: [{ source: 1106949, name: 'vite', title: 'Dev server fs.deny bypass', url: 'https://github.com/advisories/GHSA-x574', severity: 'critical' }],
      },
    },
  });
  const report = renderReport(findings, 'high', ['@org/frontend-platform'], 'frontend/package-lock.json');
  assert.match(report, /1106949/);
  assert.match(report, /GHSA-x574/);
  assert.match(report, /<5\.4\.12/);
  assert.match(report, /no fix published yet/);
  assert.match(report, /@org\/frontend-platform/);
  // A finding with no published fix needs a decision, not a version bump, and
  // the report has to say that rather than implying an upgrade will fix it.
  assert.match(report, /No published fix/);
  assert.match(report, /needs a decision/);
});

test('a placeholder owner produces a visible warning, not a silent unassigned finding', () => {
  const report = renderReport([], 'high', ['@CHANGE_ME/frontend'], 'frontend/package-lock.json');
  assert.match(report, /No owner is assigned/);
  assert.match(report, /ci-branch-protection\.md/);
  assert.doesNotMatch(report, /@CHANGE_ME\/frontend`/, 'the placeholder is named as a gap, not rendered as an owner');
});

test('a clean lockfile reports cleanly and still says it audited something', () => {
  const report = renderReport([], 'high', ['@org/frontend'], 'frontend/package-lock.json');
  assert.match(report, /No advisories reported/);
  assert.match(report, /committed lockfile/);
  assert.doesNotMatch(report, /\| severity \|/, 'an empty table is noise');
});
