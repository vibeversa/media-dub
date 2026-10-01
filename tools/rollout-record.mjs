// The rollout-record writer (Task 043B).
//
// WHAT IT IS FOR
// --------------
// `deploy/rollout/rehearse-rollback.sh` and `deploy/rollout/compat-smoke.sh`
// both produce a JSON record that is COMMITTED, and both records carry a
// hand-written `$comment` block explaining what each field is FOR. A shell
// heredoc cannot write that block and a new object at the same time: splicing
// the block in with `sed` and then printing a second `{...}` produces two
// top-level values, and JSON.parse's answer is
//
//   Unexpected non-whitespace character after JSON at position 12
//
// which names neither the cause nor the line. Both scripts did that. This is
// the fix, and it is a module rather than more `sed` because a record writer is
// worth a unit test and a `sed` pipeline is not.
//
// THE RULE
// --------
// The `$comment` block is the record's documentation and it is written by a
// human, once. A run overwrites the MEASUREMENTS and never the documentation.
// If the new record has its own `$comment` (a caller passing one explicitly), it
// wins; otherwise the previous block is carried over; otherwise the record is
// written without one, which is valid and which the gate accepts - the gate
// checks fields, not prose.
//
// Every other key in the new record REPLACES the previous value. A record is a
// measurement plus its documentation, and merging measurements across runs would
// produce a record that describes a run that never happened.

/** Merges a new record over an existing one, preserving `$comment`. Pure. */
export function mergeRecord(previous, next) {
  const base = previous !== null && typeof previous === 'object' && !Array.isArray(previous) ? previous : {};
  const addition = next !== null && typeof next === 'object' && !Array.isArray(next) ? next : {};
  const merged = { ...base, ...addition };
  const comment = Array.isArray(addition.$comment)
    ? addition.$comment
    : Array.isArray(base.$comment)
      ? base.$comment
      : undefined;
  if (comment === undefined) {
    delete merged.$comment;
  } else {
    // First key, always: a record whose documentation is at the bottom is a
    // record nobody scrolls to the bottom to read.
    const { $comment: _ignored, ...rest } = merged;
    return { $comment: comment, ...rest };
  }
  return merged;
}

/** The record as pretty JSON with a trailing newline. */
export function renderRecord(record) {
  return `${JSON.stringify(record, null, 2)}\n`;
}
