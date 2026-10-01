#!/usr/bin/env node
// Write a rollout record, preserving its committed `$comment` block.
//
//   node tools/rollout-record.mjs write <new-record.json> [<existing-record.json>]
//
// Reads the new record and, when an existing record is supplied, merges it over
// the previous one so the `$comment` documentation survives. Writes to stdout.
// The logic is `mergeRecord` in tools/rollout-record.mjs, which is unit-tested;
// this file is the argument handling.
import { readFileSync } from 'node:fs';
import { mergeRecord, renderRecord } from '../tools/rollout-record.mjs';

const [command, newPath, existingPath] = process.argv.slice(2);

if (command !== 'write' || newPath === undefined) {
  process.stderr.write('usage: rollout-record.mjs write <new-record.json> [<existing-record.json>]\n');
  process.exit(2);
}

function readJson(path) {
  try {
    return JSON.parse(readFileSync(path, 'utf8'));
  } catch (error) {
    process.stderr.write(`rollout-record: ${path}: ${error.message}\n`);
    process.exit(2);
    return null;
  }
}

const next = readJson(newPath);
const previous = existingPath === undefined ? null : readJson(existingPath);
process.stdout.write(renderRecord(mergeRecord(previous, next)));
