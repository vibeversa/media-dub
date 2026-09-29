// Tests for the skipped-test gate (Task 042).
//
// This gate exists because a green job that ran no tests is indistinguishable
// from a green job that passed every test. These cover the reader against real
// TRX shapes - including the ones that are easy to get wrong and that would
// silently turn a skip into a pass:
//   * `<UnitTestResult ... />` written as a self-closing element,
//   * the skip reason nested in `<Output><ErrorInfo><Message>`,
//   * a test name containing characters the XML escaped,
//   * an outcome this reader has never heard of.
import { test } from 'node:test';
import assert from 'node:assert/strict';

import { countOutcomes, decodeXml, parseTrx, FAILURE_OUTCOMES, SKIP_OUTCOMES } from './trx-parse.mjs';

const HEADER = '<?xml version="1.0" encoding="UTF-8"?><TestRun id="1" name="run" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">';
const FOOTER = '</TestRun>';

function trx(body) {
  return `${HEADER}<Results>${body}</Results>${FOOTER}`;
}

test('a mixed run reports every outcome separately', () => {
  const document = trx(`
    <UnitTestResult testName="A.Passes" outcome="Passed" duration="00:00:00.01" />
    <UnitTestResult testName="B.Passes" outcome="Passed" duration="00:00:00.02" />
    <UnitTestResult testName="C.Fails" outcome="Failed" duration="00:00:00.03">
      <Output><ErrorInfo><Message>Assert.Equal() Failure</Message></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testName="D.Skips" outcome="Skipped" duration="00:00:00.00">
      <Output><ErrorInfo><Message>No container runtime available</Message></ErrorInfo></Output>
    </UnitTestResult>`);
  const results = parseTrx(document);
  assert.equal(results.length, 4);
  assert.deepEqual(countOutcomes(results), { passed: 2, failed: 1, skipped: 1, other: 0 });

  const skip = results.find((r) => r.outcome === 'Skipped');
  assert.match(skip.message, /No container runtime available/, 'the skip reason is the whole point of reading the file');
  const failure = results.find((r) => r.outcome === 'Failed');
  assert.match(failure.message, /Assert\.Equal/);
});

test('a self-closing result element is read like any other', () => {
  const document = trx('<UnitTestResult testName="Self.Closed" outcome="NotExecuted" />');
  const results = parseTrx(document);
  assert.equal(results.length, 1);
  assert.equal(results[0].name, 'Self.Closed');
  assert.equal(results[0].outcome, 'NotExecuted');
  assert.ok(SKIP_OUTCOMES.has(results[0].outcome), 'NotExecuted is a skip, not a pass');
});

test('a skip with no reason is still a skip', () => {
  const results = parseTrx(trx('<UnitTestResult testName="Silent.Skip" outcome="Skipped" />'));
  assert.equal(results[0].message, null);
  assert.deepEqual(countOutcomes(results), { passed: 0, failed: 0, skipped: 1, other: 0 });
});

test('an outcome this reader has never seen is not counted as a pass', () => {
  const results = parseTrx(trx('<UnitTestResult testName="D.Future" outcome="SomeNewOutcome" />'));
  assert.deepEqual(countOutcomes(results), { passed: 0, failed: 0, skipped: 0, other: 1 });
});

test('a passing dotnet test is self-closing and must not swallow the next result', () => {
  // Captured from this repository's own `dotnet test` output. A PASSING test is
  // written `<UnitTestResult ... />` with no children, and it is the common case,
  // so a reader that handles only the paired form matches forward to the next
  // `</UnitTestResult>` and reports one result for a run of many. That bug was in
  // the first version of this reader and this test is what caught it.
  const real = '<?xml version="1.0" encoding="UTF-8"?>'
    + '<TestRun id="run" name="run@2026-09-30" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
    + '<Results>'
    + '<UnitTestResult executionId="a" testId="t1" testName="Health.IsFullProfile" computerName="COM-807" duration="00:00:00.0038388" startTime="2026-09-30T00:08:12.95+03:30" endTime="2026-09-30T00:08:12.96+03:30" testType="13cdc9d9" outcome="Passed" testListId="8c84" relativeResultsDirectory="a" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" />'
    + '<UnitTestResult executionId="b" testId="t2" testName="Health.IsMinimalProfile" computerName="COM-807" duration="00:00:00.002" startTime="2026-09-30T00:08:12.96+03:30" endTime="2026-09-30T00:08:12.97+03:30" testType="13cdc9d9" outcome="Passed" testListId="8c84" relativeResultsDirectory="b" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" />'
    + '<UnitTestResult executionId="c" testId="t3" testName="Health.ProbeNeedsNetwork" computerName="COM-807" duration="00:00:00.1" startTime="2026-09-30T00:08:12.98+03:30" endTime="2026-09-30T00:08:13.08+03:30" testType="13cdc9d9" outcome="NotExecuted" testListId="8c84" relativeResultsDirectory="c" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
    + '<Output><ErrorInfo><Message>No container runtime is available on this machine</Message><StackTrace>   at TestFixtureBase.StartPostgresAsync()</StackTrace></ErrorInfo></Output>'
    + '<TestCategory>Category=Integration</TestCategory>'
    + '</UnitTestResult>'
    + '</Results></TestRun>';
  const results = parseTrx(real);
  assert.equal(results.length, 3, 'a self-closing pass must not swallow the results after it');
  assert.deepEqual(countOutcomes(results), { passed: 2, failed: 0, skipped: 1, other: 0 });
  assert.equal(results[0].name, 'Health.IsFullProfile');
  assert.equal(results[0].testId, 't1');
  assert.equal(results[2].outcome, 'NotExecuted');
  assert.match(results[2].message, /No container runtime/, 'the skip reason survives');
  assert.deepEqual(results[2].categories, ['Category=Integration']);
});

test('every documented failure outcome is a failure', () => {
  for (const outcome of ['Failed', 'Error', 'Timeout', 'Aborted']) {
    assert.ok(FAILURE_OUTCOMES.has(outcome), `${outcome} must be a failure`);
  }
  const results = FAILURE_OUTCOMES;
  assert.equal(results.size, 4);
});

test('categories are read, in any order, and drive the exemption', () => {
  const document = trx(`
    <UnitTestResult testName="Soak.One" outcome="Skipped">
      <TestCategory>Soak</TestCategory>
    </UnitTestResult>
    <UnitTestResult testName="Migration.Compat" outcome="Skipped">
      <TestCategory>Slow</TestCategory>
      <TestCategory>Soak</TestCategory>
    </UnitTestResult>
    <UnitTestResult testName="Uncategorised" outcome="Skipped" />`);
  const results = parseTrx(document);
  assert.deepEqual(results[0].categories, ['Soak']);
  assert.deepEqual(results[1].categories, ['Slow', 'Soak']);
  assert.deepEqual(results[2].categories, []);
});

test('XML entities in names and messages are decoded, not left raw', () => {
  const document = trx(`<UnitTestResult testName="A &amp; B &lt;tag&gt;" outcome="Skipped">
      <Output><ErrorInfo><Message>expected &lt;null&gt; but got &quot;x&quot; &amp; &apos;y&apos;</Message></ErrorInfo></Output>
    </UnitTestResult>`);
  const results = parseTrx(document);
  assert.equal(results[0].name, 'A & B <tag>');
  assert.equal(results[0].message, 'expected <null> but got "x" & \'y\'');
});

test('decodeXml leaves an unknown entity verbatim rather than dropping it', () => {
  // A test name that is still recognisable beats a test name that has been
  // silently mangled into a different one.
  assert.equal(decodeXml('a &nbsp; b'), 'a &nbsp; b');
  assert.equal(decodeXml('&#65;&#x42;'), 'AB');
});

test('CDATA-wrapped output is unwrapped', () => {
  const document = trx(`<UnitTestResult testName="Cdata" outcome="Skipped">
      <Output><ErrorInfo><Message><![CDATA[stack trace <not markup> here]]></Message></ErrorInfo></Output>
    </UnitTestResult>`);
  const results = parseTrx(document);
  assert.equal(results[0].message, 'stack trace <not markup> here');
});

test('a document that is not TRX raises rather than reporting zero tests', () => {
  // This is the important failure: an unreadable results file that yields an
  // empty result set would let the gate pass on a run that produced no evidence.
  assert.throws(() => parseTrx('<html><body>404</body></html>'), /not a TRX document/);
  assert.throws(() => parseTrx(''), /not a TRX document/);
  assert.throws(() => parseTrx(undefined), /not a TRX document/);
});

test('an empty TestRun parses to an empty result set rather than throwing', () => {
  // Distinguished from "not TRX" on purpose: a run that genuinely executed nothing
  // is data the gate must see, and it is reported as zero tests.
  assert.deepEqual(parseTrx(trx('')), []);
  assert.deepEqual(countOutcomes(parseTrx(trx(''))), { passed: 0, failed: 0, skipped: 0, other: 0 });
});

test('a test name containing angle brackets in the body does not confuse the reader', () => {
  const document = trx(`<UnitTestResult testName="Real" outcome="Passed">
      <Output><StdOut>expected &lt;b&gt;bold&lt;/b&gt; got &lt;i&gt;italic&lt;/i&gt;</StdOut></Output>
    </UnitTestResult>
    <UnitTestResult testName="Second" outcome="Passed" />`);
  const results = parseTrx(document);
  assert.equal(results.length, 2, 'the body of one result must not swallow the next');
  assert.equal(results[1].name, 'Second');
});
