// Task 017 barrel: the only legal way to reach generated API shapes.
// Feature code imports generated types and the transport exclusively through
// this module; deep imports of `api/generated` are rejected by
// `no-restricted-imports` in eslint.config.js.
export * from '../generated/index.js';
export * from './httpClient.js';
