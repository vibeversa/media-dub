// The rollout-record writer's tests (Task 043B).
//
// The class of bug this file exists for: a record whose fields are written by a
// script and whose documentation is written by a human, merged by a shell
// pipeline, and read by a gate that reports "Unexpected non-whitespace character
// after JSON at position 12" when it goes wrong. Both rollout runners did that
// on their first run. A merge that can be unit-tested is a merge that cannot.
import test from 'node:test';
import assert from 'node:assert/strict';

import { mergeRecord, renderRecord } from './rollout-record.mjs';

test('the committed $comment survives a run that does not carry one', () => {
  const previous = { $comment: ['why these fields exist'], date: '2026-01-01', result: 'NEVER_RUN' };
  const next = { date: '2026-09-30', result: 'PASS' };
  const merged = mergeRecord(previous, next);
  assert.deepEqual(merged.$comment, ['why these fields exist']);
  assert.equal(merged.date, '2026-09-30');
  assert.equal(merged.result, 'PASS');
});

test('a run that carries its own $comment wins', () => {
  const merged = mergeRecord({ $comment: ['old'] }, { $comment: ['new'], date: 'x' });
  assert.deepEqual(merged.$comment, ['new']);
});

test('a record with no previous file is valid and has no $comment', () => {
  const merged = mergeRecord(null, { date: '2026-09-30', result: 'PASS' });
  assert.equal(merged.$comment, undefined);
  assert.doesNotThrow(() => JSON.parse(renderRecord(merged)));
});

test('the merged record parses, which is the entire point', () => {
  // The regression, as a test: splicing a comment block and a second object
  // produces two top-level values, and this is the assertion that catches it.
  const rendered = renderRecord(mergeRecord({ $comment: ['a', 'b'], date: 'old' }, { date: 'new' }));
  const parsed = JSON.parse(rendered);
  assert.deepEqual(parsed.$comment, ['a', 'b']);
  assert.equal(parsed.date, 'new');
  assert.ok(rendered.endsWith('\n'), 'a record file ends with a newline, so a diff is one line');
});

test('$comment is always the FIRST key', () => {
  const rendered = renderRecord(mergeRecord({ date: 'old', $comment: ['docs'] }, { result: 'PASS' }));
  assert.match(rendered, /^\{\n {2}"\$comment": \[/);
});

test('every other key is replaced, never merged across runs', () => {
  // Merging measurements would produce a record describing a run that never
  // happened: an old `targets` array left in place beside a new `date` reads as
  // a fresh run of the old measurement.
  const merged = mergeRecord({ date: 'old', targets: [{ deployment: 'x' }] }, { date: 'new' });
  assert.deepEqual(merged.targets, [{ deployment: 'x' }], 'a key the run did not set is carried over');
  const replaced = mergeRecord({ date: 'old', targets: [{ deployment: 'x' }] }, { date: 'new', targets: [] });
  assert.deepEqual(replaced.targets, [], 'a key the run DID set replaces the old value, even when empty');
});

test('a non-object on either side is tolerated rather than producing nonsense', () => {
  assert.equal(mergeRecord(['not', 'an', 'object'], { date: 'x' }).date, 'x');
  assert.equal(mergeRecord({ date: 'old' }, 'not an object').date, 'old');
  assert.doesNotThrow(() => JSON.parse(renderRecord(mergeRecord(null, null))));
});
