// The product incident runbooks (Task 043C, R1/R4/R5).
//
// WHY THIS IS A GATE AND NOT A LINK CHECK IN A REVIEW
// ---------------------------------------------------
// Eight runbooks whose whole value is that a responder finds the right one in
// the first thirty seconds, with the right monitor, the right diagnostics view
// and the right escalation. Every property below fails silently when it is
// wrong:
//
//   * a runbook that is not linked from the index is never found during an
//     incident - and "it exists" reads as done in a task report;
//   * a uniform template is what makes the first thirty seconds work. One page
//     that puts Mitigation before Escalation teaches a responder that this
//     document cannot be trusted to be in order;
//   * a monitor or a diagnostics view that is renamed leaves a command that
//     returns nothing, and a responder reads "no data" as "nothing wrong";
//   * a runbook that restates a Plan A mechanism page is a fork. Two copies
//     diverge, and the copy nobody is maintaining is the one in the runbook.
//
// The two things this file deliberately does NOT assert:
//
//   * that the commands in a triage section WORK. Only a tabletop proves that,
//     and the tabletop is on the release calendar with its results recorded in
//     docs/runbooks/product/index.md. The gate covers the structural half, which
//     is the half that rots without anyone noticing;
//   * that every named monitor exists. Several do not, and naming that as a gap
//     inside the runbook (with an owner) is more useful than silently deleting
//     the reference. `test('every named monitor is either real or declared a
//     gap')` holds that line: a monitor name must resolve to a real alert, or
//     the runbook must say GAP and name an owner.
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const REPO_ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const PRODUCT = join(REPO_ROOT, 'docs/runbooks/product');
const INDEX = join(PRODUCT, 'index.md');
const OPS_INDEX = join(REPO_ROOT, 'docs/runbooks/index.md');

/**
 * Minimal multi-document YAML reader for the manifests, which are hand-written
 * and use only the block-mapping subset. Written here rather than pulling in a
 * parser for four fields per document: the alternative is a dependency, and a
 * dependency in a gate that has to run before anything is installed is its own
 * class of problem.
 *
 * Deliberately not a YAML implementation: it reads `kind:` and the `name:` that
 * follows `metadata:` at the document's top level, and nothing else. If a
 * manifest ever nests `name:` under something else, the reader returns fewer
 * documents and the `size >= 7` assertion below fails rather than silently
 * passing with an empty set.
 */
function yamlDocs(source) {
  return source
    .split(/^---$/m)
    .map((doc) => {
      const kind = doc.match(/^kind:\s*(\S+)\s*$/m)?.[1];
      const metadataBlock = doc.match(/^metadata:\s*\n((?:\s{2,}.*\n|\n)*)/m)?.[1] ?? '';
      const name = metadataBlock.match(/^\s+name:\s*(\S+)\s*$/m)?.[1];
      return kind ? { kind, metadata: { name } } : null;
    })
    .filter(Boolean);
}

/** The eight, named exactly as Task 043C instruction 1 names them. */
const REQUIRED_RUNBOOKS = [
  'auth-outage.md',
  'cdn-outage.md',
  'sse-outage.md',
  'frontend-deploy-failure.md',
  'notification-backlog.md',
  'contract-drift.md',
  'upload-surge-failure.md',
  'review-backlog-surge.md',
];

/**
 * The template. Order is asserted, not just presence, because a responder
 * triaging an incident reads top to bottom and the order is the reason the
 * triage section is capped.
 *
 * `## Signals` and `## Degraded-mode triage` and `## Access and audit` are
 * required on top of the Plan A five; the reasons are in the task and in
 * docs/runbooks/product/index.md.
 */
const SECTION_ORDER = [
  '## Signals',
  '## Symptoms',
  '## Five-minute triage',
  '## Degraded-mode triage',
  '## Mitigation',
  '## Escalation',
  '## Postmortem trigger',
  '## Access and audit',
];

/**
 * The Plan A mechanism page each product runbook must link, and must NOT
 * reimplement. The three shared names (auth-outage, notification-backlog,
 * contract-drift) are the interesting ones: a file with the same name in a
 * different directory is exactly the shape a fork takes, and the gate that
 * catches it is the one that names both paths.
 */
const MECHANISM_PAGE = {
  'auth-outage.md': '../auth-outage.md',
  'cdn-outage.md': '../cdn-cache-poison.md',
  'sse-outage.md': '../sse-degraded.md',
  'frontend-deploy-failure.md': '../deploy-failed.md',
  'notification-backlog.md': '../notification-backlog.md',
  'contract-drift.md': '../contract-drift.md',
  'upload-surge-failure.md': '../upload-surge.md',
  'review-backlog-surge.md': '../review-surge.md',
};

/** The task's eight product surfaces, in the task's own words. */
const PRODUCT_SURFACES = [
  'auth',
  'cdn',
  'sse',
  'deploy',
  'notification',
  'contract',
  'upload',
  'review',
];

// ---------------------------------------------------------------- presence ---

test('all eight product runbooks exist', () => {
  for (const name of REQUIRED_RUNBOOKS) {
    assert.ok(existsSync(join(PRODUCT, name)), `docs/runbooks/product/${name} is missing`);
  }
});

test('the product index exists', () => {
  assert.ok(existsSync(INDEX), 'docs/runbooks/product/index.md is missing');
});

test('every product runbook on disk is one the task names', () => {
  // The mirror of tools/runbooks-index.test.mjs's own-directory check. A ninth
  // page added without being in REQUIRED_RUNBOOKS is a page whose template this
  // file does not police and whose name nothing has agreed to.
  const onDisk = readdirSync(PRODUCT).filter((name) => name.endsWith('.md') && name !== 'index.md');
  for (const name of onDisk) {
    assert.ok(
      REQUIRED_RUNBOOKS.includes(name),
      `docs/runbooks/product/${name} is on disk but is not one of the eight; add it here and to the index`,
    );
  }
});

// ---------------------------------------------------------------- template ---

test('every product runbook carries the template sections, in order, none empty', () => {
  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const positions = SECTION_ORDER.map((heading) => text.indexOf(heading));

    for (let i = 0; i < SECTION_ORDER.length; i += 1) {
      const heading = SECTION_ORDER[i];
      assert.notEqual(positions[i], -1, `${name} is missing "${heading}"`);

      const body = text.slice(
        positions[i] + heading.length,
        i + 1 < SECTION_ORDER.length ? positions[i + 1] : text.length,
      );
      const meaningful = body
        .split('\n')
        .map((line) => line.trim())
        .filter((line) => line.length > 0 && !line.startsWith('#'));
      assert.ok(
        meaningful.length >= 3,
        `${name}: "${heading}" has fewer than three lines under it; a heading with nothing under it is padding`,
      );
    }

    for (let i = 1; i < positions.length; i += 1) {
      assert.ok(
        positions[i] > positions[i - 1],
        `${name}: "${SECTION_ORDER[i]}" appears before "${SECTION_ORDER[i - 1]}"`,
      );
    }
  }
});

/**
 * The user report each page must open with, in the words a user would use.
 * Per-page rather than one shared regex: a single alternation across eight pages
 * passes when seven of them quote nothing, which is the failure this is here to
 * catch.
 */
const USER_REPORT = {
  'auth-outage.md': /log in|login page|password is wrong|signed out/i,
  'cdn-outage.md': /white|blank|wrong build|doesn't work|yesterday's version/i,
  'sse-outage.md': /frozen|isn't moving|went backwards|stuck/i,
  'frontend-deploy-failure.md': /broken|every button|working (five|10) minutes ago|different version/i,
  'notification-backlog.md': /never got|didn't get|hours late|unread/i,
  'contract-drift.md': /field doesn't exist|404|doesn't have|signed out/i,
  'upload-surge-failure.md': /fails|failing|never finished|no media|finished/i,
  'review-backlog-surge.md': /waiting for review|hasn't (been )?(looked|touched)|awaiting review/i,
};

test('every product runbook opens with what a user says', () => {
  // The pages are indexed by user report, so the opening has to be the user's
  // words. A page that opens with the component name is a mechanism page that
  // ended up in the wrong directory.
  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const intro = text.split('## Signals')[0];

    assert.ok(
      /\*\*What a user says:\*\*/.test(intro),
      `${name} does not open with "**What a user says:**" before ## Signals`,
    );
    assert.ok(
      USER_REPORT[name].test(intro),
      `${name} does not quote a user report in its opening paragraph (expected /${USER_REPORT[name].source}/)`,
    );
    // A user report is quoted, not paraphrased into a specification.
    assert.ok(
      intro.includes('"') || intro.includes('“'),
      `${name} does not quote the user report in quotation marks`,
    );
  }
});

// ------------------------------------------------------------------ signals ---

test('every product runbook names a monitor/dashboard, a diagnostics view and a rollback', () => {
  // The three links the task requires (038 monitor, 036 diagnostics, 043B
  // rollback). A row that is only prose is a row a responder cannot click.
  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const signals = text.slice(text.indexOf('## Signals'), text.indexOf('## Symptoms'));

    assert.ok(/Monitor \/ dashboard/.test(signals), `${name} has no "Monitor / dashboard" row`);
    assert.ok(
      /Diagnostics view \(036\)/.test(signals),
      `${name} has no "Diagnostics view (036)" row`,
    );
    assert.ok(/Rollback \(043B\)/.test(signals), `${name} has no "Rollback (043B)" row`);

    // And the rollback row must actually link the procedure.
    assert.ok(
      /\(\.\.\/\.\.\/rollout\.md\)/.test(signals),
      `${name}'s rollback row does not link ../../rollout.md`,
    );
  }
});

test('every named monitor is either a real alert, a real dashboard, or declared a gap with an owner', () => {
  // The check that keeps the gap declarations honest. Without it, a runbook can
  // say "GAP" forever and the gap becomes invisible; and a name that is neither
  // real nor declared is a command that returns nothing, which a responder reads
  // as "nothing is wrong".
  const alerts = readFileSync(join(REPO_ROOT, 'deploy/observability/alerts.yml'), 'utf8');
  const dashboards = readdirSync(join(REPO_ROOT, 'deploy/observability/dashboards'))
    .filter((name) => name.endsWith('.json'))
    .map((name) => name.replace(/\.json$/, ''))
    .join(' ');

  // Metric names are the Prometheus exposition form: lowercase with underscores
  // (`sse_connections_total`), and a CamelCase name is an ALERT. Matching either
  // shape and nothing else is deliberate - a bare `[A-Z][A-Za-z]+` also matches
  // `NetworkPolicy` and every other Kubernetes kind, which are not monitors and
  // would make this assertion about a different thing than it appears to be.
  //
  // A metric must also carry a recognised NAME SUFFIX. Without that, a label
  // VALUE is indistinguishable from a metric: `chunk_received` (the `stage` label
  // of `upload.funnel_total`) is a real string the repository declares, and a
  // regex that treated it as a metric would have to either fail on a correct
  // runbook or be loosened until it asserted nothing. Counters end `_total`,
  // durations `_ms`, and the rest of the shapes below are the ones this
  // repository's frozen facades actually use.
  const METRIC_SUFFIX = /(_total|_ms|_bytes|_count|_sum|_bucket|_seconds|_ratio|_depth|_duration|_recovered|_started|_failed|_dropped)$/;
  const METRIC_LIKE = /`[a-z][a-z0-9]*(?:_[a-z0-9]+)+`/g;
  const ALERT_LIKE = /`[A-Z][A-Za-z0-9]*(Rate|Depth|High|Low|Error|Count|None|Zero|NonZero)\w*`/g;

  // The declared set, read from the two files that declare it. Frozen names
  // ("renaming breaks dashboards") make this the source of truth.
  //
  // Two kinds of token live in those files and a runbook legitimately names both:
  //   * metric names  - `sse.connections_total` in C#, `sse_connections_total` in
  //     the exposition format, so the dot is folded to an underscore;
  //   * label VALUES  - `chunk_received` is a `stage` label of
  //     `upload.funnel_total`, and a runbook that explains the funnel has to name
  //     it. Treating a label value as a metric name is what made this assertion
  //     fail on a correct runbook, and loosening it until it passed would have
  //     made the assertion vacuous, so the vocabulary is collected instead.
  const declared = new Set();
  for (const file of [
    'src/DubbingPlatform.Api/Observability/BackendMetrics.cs',
    'src/DubbingPlatform.Api/Sse/SseEnvelope.cs',
  ]) {
    const source = readFileSync(join(REPO_ROOT, file), 'utf8');
    for (const match of source.matchAll(/"([a-z][a-z0-9_.]*)"/g)) {
      declared.add(match[1].replace(/\./g, '_'));
    }
  }
  assert.ok(declared.size > 20, `only ${declared.size} tokens were read from the metric declarations`);

  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const signals = text.slice(text.indexOf('## Signals'), text.indexOf('## Symptoms'));

    for (const match of signals.matchAll(METRIC_LIKE)) {
      const metric = match[0].slice(1, -1);
      // `dlq_depth` and `api_latency` are the frozen SLO facade's names
      // (docs/observability/slos.md), which is a third source.
      const real =
        declared.has(metric) ||
        alerts.includes(metric) ||
        dashboards.includes(metric) ||
        readFileSync(join(REPO_ROOT, 'docs/observability/slos.md'), 'utf8').includes(metric) ||
        readFileSync(join(REPO_ROOT, 'docs/operations/support-access.md'), 'utf8').includes(metric);
      assert.ok(
        real || METRIC_SUFFIX.test(metric),
        `${name} names "${metric}", which is neither a metric the repository emits or graphs, nor a name with a metric suffix`,
      );
    }

    for (const match of signals.matchAll(ALERT_LIKE)) {
      const alert = match[0].slice(1, -1);
      assert.ok(
        alerts.includes(`alert: ${alert}`),
        `${name} names the alert "${alert}", which is not in deploy/observability/alerts.yml`,
      );
    }

    // A declared gap must name the team that owns it. "GAP" alone is a shrug, and
    // a shrug in a runbook is indistinguishable from a page that was never
    // checked - which is the state this gate exists to prevent.
    //
    // Per signals-table ROW, which is the correct unit: a gap belongs to one row
    // and its owner belongs to the same row. A block-wide regex with a character
    // budget is the wrong shape twice over - it fails a correctly-attributed gap
    // whose cell is simply long, and it would accept an Owner belonging to a
    // different row entirely.
    //
    // The rows must also be single-line, which is asserted separately below: a
    // wrapped row is a rendering bug that loses the tail of the cell, Owner
    // included, and one was found exactly this way.
    const rows = signals
      .split('\n')
      .map((line) => line.trim())
      .filter((line) => line.startsWith('|') && line.endsWith('|'));

    for (const row of rows) {
      if (!/\bGAP\b/.test(row)) continue;
      assert.ok(
        /Owner:?\s*`?\d{3}[A-Z]?`?/.test(row),
        `${name} declares a GAP in this signals row without naming an owning task: ${row.slice(0, 110)}…`,
      );
    }

    // A row in the Signals table must be a single line. A wrapped table row is a
    // rendering bug - the continuation line becomes a paragraph, so the rendered
    // table silently loses the end of the cell, including the Owner.
    for (const line of signals.split('\n')) {
      const trimmed = line.trim();
      if (trimmed && !trimmed.startsWith('|') && !trimmed.startsWith('#')) {
        assert.fail(
          `${name} has a wrapped line inside its ## Signals table ("${trimmed.slice(0, 70)}…"). ` +
            'A table row cannot wrap in Markdown: the continuation renders as a paragraph and the row loses its tail.',
        );
      }
    }
  }
});

test('every named diagnostics view is a route the admin API actually serves', () => {
  // 036's views are `[HttpGet]` attributes on AdminController. A renamed route
  // leaves a runbook command that returns the `{*unmatched}` catch-all 404,
  // which is indistinguishable from "no backlog".
  const adminController = readFileSync(
    join(REPO_ROOT, 'src/DubbingPlatform.Api/Controllers/AdminController.cs'),
    'utf8',
  );
  const routes = [...adminController.matchAll(/\[HttpGet\("([^"]+)"/g)].map((m) => m[1]);

  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const signals = text.slice(text.indexOf('## Signals'), text.indexOf('## Symptoms'));
    const cited = [...signals.matchAll(/`\/api\/v1\/admin\/([a-z0-9/{}-]+)`/g)].map((m) => m[1]);

    for (const route of cited) {
      // Normalise the {param} segments, since the controller declares them too.
      const normalised = route.replace(/\{[^}]+\}/g, '{x}');
      assert.ok(
        routes.some((r) => r.replace(/\{[^}]+\}/g, '{x}') === normalised),
        `${name} cites /api/v1/admin/${route}, which AdminController does not serve`,
      );
    }
  }
});

// ------------------------------------------------------- role and audit (R4) ---

test('every product runbook states the role restriction and the audit requirement (R4)', () => {
  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const section = text.slice(text.indexOf('## Access and audit'));

    assert.ok(
      /RequireTenantAdmin/.test(section),
      `${name} does not name the RequireTenantAdmin policy in ## Access and audit`,
    );
    // The policy shape, restated rather than linked: a responder who has to open
    // another document to learn whether they are allowed to do the thing will
    // do the thing. The 401/403 pair is the part that matters most, because a
    // ProjectViewer hitting a 403 during an incident is the policy working and
    // the natural reaction is to treat it as a second fault.
    assert.ok(
      /TenantAdmin/.test(section) && /Service/.test(section),
      `${name} does not name both allowed roles (Service, TenantAdmin)`,
    );
    assert.ok(
      /401/.test(section) && /403/.test(section),
      `${name} does not restate that anonymous is 401 and ProjectViewer is 403`,
    );
    assert.ok(
      /ProjectViewer/.test(section),
      `${name} does not name ProjectViewer; that is the role whose 403 gets mistaken for a fault`,
    );
    assert.ok(
      /audit_events/.test(section),
      `${name} does not say that a privileged action must be attributable in audit_events`,
    );
    assert.ok(
      /support-access\.md/.test(section),
      `${name} does not link docs/operations/support-access.md`,
    );
  }
});

test('every product runbook forbids pasting secrets into a ticket', () => {
  // The task's security requirement. Checked as a per-page property because the
  // failure is a responder pasting a bearer token into a ticket, and a policy in
  // the index does not stop that.
  //
  // Whitespace is normalised FIRST: these pages are hard-wrapped, and a sentence
  // whose prohibition and its object are split across a line break ("Do not\n
  // paste … session tokens") is a complete sentence that a `.{0,80}` over the
  // raw text cannot see. Asserting on the raw text would therefore fail on the
  // pages that state the rule most precisely.
  for (const name of REQUIRED_RUNBOOKS) {
    const flat = readFileSync(join(PRODUCT, name), 'utf8').replace(/\s+/g, ' ');
    assert.ok(/ticket/i.test(flat), `${name} never mentions a ticket`);
    assert.ok(
      /(no|never|do not|don't)[^.]{0,120}(token|password|secret|cookie|dsn|bearer)/i.test(flat),
      `${name} does not forbid putting credential material into a ticket`,
    );
  }
});

test('the example ids in the product runbooks are synthetic, not real-looking', () => {
  // The task's security requirement, asserted rather than trusted: no runbook
  // should carry a hardcoded credential of any shape, and the only identifiers
  // that appear must be the documented synthetic prefixes.
  const FORBIDDEN = [
    /eyJ[A-Za-z0-9_-]{10,}/, // a JWT
    /Bearer\s+[A-Za-z0-9._-]{20,}/, // a literal bearer token
    /[?&]access_token=[A-Za-z0-9._-]{20,}/, // a literal SSE token
    /AKIA[0-9A-Z]{16}/, // an AWS access key id
    /-----BEGIN [A-Z ]*PRIVATE KEY-----/, // a key
    /Password=[^;"'\s]*[0-9]/i, // an inline password
  ];

  for (const name of [...REQUIRED_RUNBOOKS, 'index.md']) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    for (const pattern of FORBIDDEN) {
      const match = text.match(pattern);
      assert.equal(
        match,
        null,
        `docs/runbooks/product/${name} contains something shaped like a credential: ${match?.[0]?.slice(0, 12)}…`,
      );
    }
  }

  // The public ids used must be the synthetic prefixes, so a reader can tell at
  // a glance that nothing here is a real tenant.
  const idLike = readFileSync(join(PRODUCT, 'sse-outage.md'), 'utf8').match(/\b[a-z]{3}_01[A-Z0-9]+/g) ?? [];
  for (const id of idLike) {
    assert.ok(
      /^(prj|usr|ntf|ten|up|seg|rev|exp|vpj|run|spk|act)_01[A-Z]{6,}/.test(id),
      `sse-outage.md uses an id that is not the documented synthetic shape: ${id}`,
    );
  }
});

// ---------------------------------------------------------------- no fork ----

test('every product runbook links its Plan A mechanism page (R5)', () => {
  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const page = MECHANISM_PAGE[name];
    assert.ok(
      text.includes(`(${page})`),
      `${name} does not link its mechanism page ${page}; R5 says link Plan A, do not fork`,
    );
  }
});

test('no product runbook restates a mechanism instead of linking it', () => {
  // The fork check, and the reason the three shared names are the interesting
  // case. A product page that documents session mechanics, nginx inheritance or
  // queue redrive semantics is a second copy that will diverge.
  //
  // The test is deliberately narrow: it looks for MECHANISM-specific vocabulary
  // in the MITIGATION section, because "how do I fix this" is where a fork
  // happens. Restating a symptom in the symptoms section is not a fork.
  const MECHANISM_VOCABULARY = [
    /last-100 envelope/i,
    /last 100 events/i,
    /X-Accel-Buffering: no;? set by/i,
    /BYPASSRLS role definition/i,
    /effective_cache_size/i,
    /innodb_buffer_pool/i,
  ];

  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    const mitigation = text.slice(text.indexOf('## Mitigation'), text.indexOf('## Escalation'));

    for (const pattern of MECHANISM_VOCABULARY) {
      assert.equal(
        mitigation.match(pattern),
        null,
        `${name}'s Mitigation restates mechanism detail (${pattern}); link ${MECHANISM_PAGE[name]} instead`,
      );
    }
  }
});

test('the three product runbooks that share a name with a mechanism page both exist and both point at the other', () => {
  // auth-outage, notification-backlog, contract-drift exist in BOTH
  // directories. That is a real hazard: a responder who searches the repo for
  // "auth outage" finds two files and has to know which one to read. Each must
  // therefore name the other explicitly in its own text, not merely in a table
  // of links, so that whichever one is opened first it says where the other is.
  const SHARED = ['auth-outage.md', 'notification-backlog.md', 'contract-drift.md'];
  for (const name of SHARED) {
    const product = readFileSync(join(PRODUCT, name), 'utf8');
    const mechanism = readFileSync(join(REPO_ROOT, 'docs/runbooks', name), 'utf8');

    assert.ok(
      product.includes(`(../${name})`),
      `docs/runbooks/product/${name} does not link ../${name}; two files with this name need to point at each other`,
    );
    assert.ok(
      /deliberately|same name/i.test(product),
      `docs/runbooks/product/${name} does not explain why there are two files with this name`,
    );
    assert.ok(
      mechanism.includes('product/index.md'),
      `docs/runbooks/${name} does not point a reader to the product runbook set; a responder who finds the mechanism page first needs the symptom triage`,
    );
  }
});

// ------------------------------------------------------------------- links ---

test('every relative link in the product index and the runbooks resolves', () => {
  const files = [INDEX, ...REQUIRED_RUNBOOKS.map((name) => join(PRODUCT, name))];
  let checked = 0;

  for (const file of files) {
    const text = readFileSync(file, 'utf8');
    const links = [...text.matchAll(/\]\(([^)#][^)]*\.md)(?:#[^)]*)?\)/g)].map((match) => match[1]);
    for (const link of links) {
      const target = resolve(dirname(file), link);
      assert.ok(
        existsSync(target),
        `${relative(REPO_ROOT, file)} links ${link}, which does not exist (${relative(REPO_ROOT, target)})`,
      );
      checked += 1;
    }
  }
  assert.ok(checked >= REQUIRED_RUNBOOKS.length * 2, `only ${checked} links were checked; the regex is probably wrong`);
});

test('no product runbook names a Kubernetes workload that does not exist', () => {
  // The `kubectl` commands are the part of a runbook most likely to be wrong
  // and least likely to be noticed, because a responder copies one, it fails
  // with "deployments.apps \"api\" not found", and the natural reading is that
  // the namespace is wrong rather than the name. Task 043B renamed the API
  // Deployment `api` -> `dubbing-api` and the runbooks written before it were
  // all wrong; this is the check that would have caught it at review time.
  //
  // Read the names out of the committed manifests, so the assertion follows a
  // rename instead of needing to be updated alongside one.
  const manifests = [
    'deploy/k8s/api-deployment.yaml',
    'deploy/k8s/frontend/deployment.yaml',
    'deploy/k8s/workers-control.yaml',
    'deploy/k8s/workers-media-prep.yaml',
    'deploy/k8s/workers-media-render.yaml',
    'deploy/k8s/workers-ai.yaml',
    'deploy/k8s/workers-export.yaml',
    'deploy/k8s/workers-maintenance.yaml',
  ];

  const workloadNames = new Set();
  for (const file of manifests) {
    const source = readFileSync(join(REPO_ROOT, file), 'utf8');
    for (const doc of yamlDocs(source)) {
      if (doc?.kind === 'Deployment' && doc?.metadata?.name) workloadNames.add(doc.metadata.name);
    }
  }
  assert.ok(workloadNames.size >= 7, `only ${workloadNames.size} deployment name(s) read from the manifests`);

  // Only the `kubectl` invocations are checked. Anchored to `kubectl` because
  // `deploy/observability`, `deploy/backup` and `deploy/k8s` are real paths in
  // these pages and are not workloads — matching every `deploy/<word>` would
  // make the assertion about file paths instead of about commands.
  const KUBECTL_WORKLOAD = /kubectl\b[^\n]*?\bdeploy\/([a-z0-9][a-z0-9-]*)/g;

  // Per-page, pages that DO name a workload by `deploy/<name>`. Two of the eight
  // are database- and API-side runbooks with no `kubectl deploy/…` command at all,
  // so requiring one per page would be requiring noise; requiring it across the
  // set is what stops the regex from being quietly wrong.
  let totalChecked = 0;
  for (const name of REQUIRED_RUNBOOKS) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    for (const match of text.matchAll(KUBECTL_WORKLOAD)) {
      const workload = match[1];
      totalChecked += 1;
      assert.ok(
        workloadNames.has(workload),
        `${name} runs \`kubectl … deploy/${workload}\`, which is not a Deployment in deploy/k8s (known: ${[...workloadNames].sort().join(', ')})`,
      );
    }
  }
  assert.ok(
    totalChecked >= 10,
    `only ${totalChecked} \`kubectl … deploy/<workload>\` commands were checked across the set; the regex is probably wrong`,
  );

  // And every Deployment in the manifests should be reachable from somewhere in
  // the product set, which is a cheap inverse: a workload nobody can diagnose is
  // a gap, and the list is short enough to state.
  const allWorkloads = new Set();
  for (const name of REQUIRED_RUNBOOKS) {
    for (const match of readFileSync(join(PRODUCT, name), 'utf8').matchAll(KUBECTL_WORKLOAD)) {
      allWorkloads.add(match[1]);
    }
  }
  for (const workload of allWorkloads) {
    assert.ok(workloadNames.has(workload), `the product set names deploy/${workload}, which is not in deploy/k8s`);
  }
});

test('every product runbook is linked from the product index and the ops index', () => {
  const productIndex = readFileSync(INDEX, 'utf8');
  const opsIndex = readFileSync(OPS_INDEX, 'utf8');

  for (const name of REQUIRED_RUNBOOKS) {
    assert.ok(
      productIndex.includes(`(${name})`),
      `docs/runbooks/product/index.md does not link ${name}`,
    );
    // The ops index is where a responder actually starts. A product runbook
    // reachable only from its own subdirectory is a runbook that is not found.
    assert.ok(
      opsIndex.includes(`product/${name}`),
      `docs/runbooks/index.md does not link product/${name}; the symptom index is where a responder starts`,
    );
  }
  assert.ok(
    opsIndex.includes('product/index.md'),
    'docs/runbooks/index.md does not link the product runbook set at all',
  );
});

// ------------------------------------------------------- drill evidence (R3) ---

test('the product index records a runbook tabletop with a date and its gaps', () => {
  // The task's testing requirement: "runbook tabletop (walk one runbook against
  // staging, record gaps)". A gate cannot prove the commands work, so what it
  // can do is require that a walk happened, that it is dated, and that the
  // result includes the gaps rather than a bare "PASS".
  const text = readFileSync(INDEX, 'utf8');
  const walk = text.slice(text.indexOf('## Tabletop record'));

  assert.ok(walk.length > 0, 'the product index has no ## Tabletop record section');

  const dated = walk.match(/\|\s*(\d{4}-\d{2}-\d{2})\s*\|/g) ?? [];
  assert.ok(dated.length >= 1, 'the tabletop record has no dated row');

  // A walk with no gaps listed is a walk that was not really done, so the record
  // must either list gaps or say in so many words that there were none.
  assert.ok(
    /gap/i.test(walk),
    'the tabletop record does not mention gaps; a walk with no recorded gaps cannot be distinguished from a walk that was not done',
  );
  // And it must be honest about what was NOT covered.
  assert.ok(
    /unverified|was not|not covered|could not|no staging/i.test(walk),
    'the tabletop record does not state what it could not verify; a tabletop with no stated limits overstates its result',
  );
});

test('the tabletop record names the runbooks-index gate as the structural counterpart', () => {
  // Cross-reference, so a reader of either file can find the other. The gate
  // covers structure; the tabletop covers semantics; neither is sufficient alone.
  const text = readFileSync(INDEX, 'utf8');
  assert.ok(
    text.includes('tools/product-runbooks.test.mjs'),
    'the product index does not name the structural gate that polices it',
  );
});

// ------------------------------------------------------------------ backup ----

test('every product runbook that mitigates by acting on data links the backup coverage', () => {
  // Two of the eight can be mitigated by a data action (a session row, a
  // membership). Those are exactly the two where "delete the row" is the wrong
  // move, and the page must point at the coverage table and the drill rather
  // than leaving the responder to decide.
  for (const name of ['notification-backlog.md', 'review-backlog-surge.md']) {
    const text = readFileSync(join(PRODUCT, name), 'utf8');
    assert.ok(
      text.includes('../../backup.md'),
      `${name} can be mitigated by a data action and does not link docs/backup.md`,
    );
  }
});
