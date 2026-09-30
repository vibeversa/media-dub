import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  DEPLOY_CONFIG_ALLOWLIST,
  API_VERSION_PATH,
  VERSION_JSON_PATH,
  auditDeployConfig,
  compareVersions,
  isSafeUrlValue,
  isSecretShapedName,
  parseRuntimeVersion,
  renderEnvExample,
  secretValueReason,
} from '../env.js';
import type { DeployConfigKey } from '../env.js';

const ALLOWLIST_VALUES: Readonly<Record<DeployConfigKey, string>> = {
  VITE_API_BASE_URL: 'https://api.dubbing.example.com',
  VITE_CDN_ORIGIN: 'https://cdn.dubbing.example.com',
  VITE_VERSION_TAG: 'v1.4.2',
  VITE_APP_VERSION: '1.4.2',
  VITE_SSE_ENABLED: 'true',
  VITE_TELEMETRY_ENABLED: 'false',
};

const allEntries = (): [string, string][] =>
  DEPLOY_CONFIG_ALLOWLIST.map((key) => [key, ALLOWLIST_VALUES[key]]);

// Resolved from the vitest root rather than from `import.meta.url`: under the
// jsdom environment `import.meta.url` is an http URL, so `fileURLToPath` on it
// throws "The URL must be of scheme file" before a single assertion runs - a
// suite that fails at import time and reports zero tests, which is the hardest
// failure shape to read. The vitest root is `frontend/`, the only directory the
// runner is ever pointed at, so this is a stable anchor.
const EXAMPLE_PATH = join(process.cwd(), '.env.example');

/** Parses a `.env` file into pairs. Comments and blank lines are skipped. */
function readEnvFile(text: string): [string, string][] {
  return text
    .split('\n')
    .flatMap((line): [string, string][] => {
      const match = /^([A-Z0-9_]+)=(.*)$/.exec(line.trim());
      return match === null ? [] : [[match[1]!, match[2]!]];
    });
}

describe('the deploy-config allowlist', () => {
  it('accepts every allowlisted key with a safe value', () => {
    const result = auditDeployConfig(allEntries());

    expect(result.rejections).toEqual([]);
    expect(result.ok).toBe(true);
    expect(result.accepted).toHaveLength(DEPLOY_CONFIG_ALLOWLIST.length);
  });

  it('refuses a VITE_* key that is not on the list, whatever it is named', () => {
    // The whole point of an allowlist: an unlisted key is refused without anyone
    // having to guess that its name sounds dangerous.
    for (const name of ['VITE_DB_PASSWORD', 'VITE_CRED', 'VITE_SIGNER', 'VITE_X', 'VITE_CONNECTION_STRING']) {
      const result = auditDeployConfig([...allEntries(), [name, 'whatever']]);
      expect(result.ok).toBe(false);
      expect(result.rejections).toContainEqual(expect.objectContaining({ key: name, reason: 'NOT_ALLOWLISTED' }));
    }
  });

  it('refuses a non-VITE_ variable entirely', () => {
    const result = auditDeployConfig([...allEntries(), ['NODE_OPTIONS', '--max-old-space-size=4096']]);

    expect(result.ok).toBe(false);
    expect(result.rejections[0]!.reason).toBe('NOT_ALLOWLISTED');
  });

  it('refuses an allowlisted key whose NAME is secret-shaped', () => {
    // The contradiction case. A hypothetical list that permits
    // `VITE_SIGNING_KEY` is on the list AND forbidden; that is a decision to make
    // by editing the list, not by picking a rule. A hypothetical list is used
    // because the real one has no such key — testing the branch against the real
    // list would only assert that today's data is clean, not that the rule works.
    const result = auditDeployConfig(
      [['VITE_SIGNING_KEY', 'not-a-real-secret']],
      [...DEPLOY_CONFIG_ALLOWLIST, 'VITE_SIGNING_KEY'],
    );

    expect(result.ok).toBe(false);
    expect(result.rejections[0]!.reason).toBe('SECRET_SHAPED_NAME');
  });

  it('reports NOT_ALLOWLISTED for a secret-shaped name that is also unlisted', () => {
    // The allowlist check runs first, so a name that is both unlisted and
    // secret-shaped is reported as unlisted. Both are failures; the reason only
    // decides which instruction the message gives.
    const result = auditDeployConfig([['VITE_SIGNING_KEY', 'x']]);

    expect(result.rejections[0]!.reason).toBe('NOT_ALLOWLISTED');
  });

  it('refuses an allowlisted key with an empty value', () => {
    // An empty injected value passes an "is it configured" check while producing
    // a request to no origin at runtime.
    const result = auditDeployConfig([['VITE_API_BASE_URL', '   ']]);

    expect(result.ok).toBe(false);
    expect(result.rejections[0]!.reason).toBe('EMPTY_VALUE');
  });

  it.each([
    ['a PEM private key', '-----BEGIN RSA PRIVATE KEY-----\nMIIEow==\n-----END RSA PRIVATE KEY-----'],
    ['a JWT', 'eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk'],
    ['a postgres connection string', 'Host=db.internal;Database=dubbing;Username=app;Password=hunter2'],
    ['a broker URI with credentials', 'amqps://dubbing:s3cr3t@broker.internal:5671/dubbing'],
    ['an object-storage URI with credentials', 's3://AKIAIOSFODNN7EXAMPLE:secret@bucket/dubbing'],
    ['a password in a query string', 'https://api.example.com/x?password=hunter2'],
    ['an AWS access key id', 'AKIAIOSFODNN7EXAMPLE'],
    ['a bearer credential', 'Bearer abcdefghijklmnopqrstuvwxyz0123456789'],
  ])('refuses a VITE_* value carrying %s', (_label, value) => {
    const result = auditDeployConfig([['VITE_API_BASE_URL', value]]);

    expect(result.ok).toBe(false);
    expect(result.rejections[0]!.reason).toBe('SECRET_SHAPED_VALUE');
  });

  it('does not mistake a high-entropy asset name for a secret', () => {
    // Entropy is deliberately not a rule: every hashed bundle filename trips an
    // entropy check, and a check that fires on the build output fires on
    // everything.
    expect(secretValueReason('index-Bq7Yk2Mx9pL4nR8vT3wZ6hJ5cD1fG0aE7uI2oK9nM4pQ6rS8tU0vW3xY5z.json')).toBeNull();
    expect(secretValueReason('https://api.dubbing.example.com')).toBeNull();
    expect(secretValueReason('v1.4.2')).toBeNull();
  });

  it('rejects a non-http(s) API base URL', () => {
    for (const value of ['javascript:alert(1)', 'data:text/html,<script>', '//evil.example.com']) {
      const result = auditDeployConfig([['VITE_API_BASE_URL', value]]);
      expect(result.ok).toBe(false);
      expect(result.rejections[0]!.reason).toBe('UNSAFE_URL');
    }
  });

  it('accepts plain http for a local development origin', () => {
    // Refusing http would make local development impossible; the CSP and the
    // HTTPS redirect are where transport is enforced.
    expect(isSafeUrlValue('http://localhost:5000')).toBe(true);
    expect(isSafeUrlValue('https://api.example.com')).toBe(true);
    expect(isSafeUrlValue('')).toBe(true);
  });
});

describe('secret-shaped name detection', () => {
  it.each(['SECRET', 'KEY', 'TOKEN', 'PASSWORD', 'PASSWD', 'CREDENTIAL', 'PRIVATE', 'SIGNING', 'PASSPHRASE'])(
    'flags %s',
    (marker) => {
      expect(isSecretShapedName(`VITE_SOMETHING_${marker}`)).toBe(true);
    },
  );

  it('does not flag the allowlist itself', () => {
    for (const key of DEPLOY_CONFIG_ALLOWLIST) {
      expect(isSecretShapedName(key)).toBe(false);
    }
  });
});

describe('the committed .env.example', () => {
  it('is byte-identical to what the allowlist renders', () => {
    // The file and the allowlist drifting apart is how a key ends up in the
    // example file having never been decided safe. Re-rendering the committed
    // file and diffing is the only check that catches it, and it is a byte
    // comparison rather than a key-set comparison: a hand-edited header is
    // exactly the sort of drift that changes nobody's behaviour and is worth
    // noticing.
    const committed = readFileSync(EXAMPLE_PATH, 'utf8');
    const values: Record<string, string> = {};
    for (const [key, value] of readEnvFile(committed)) {
      values[key] = value;
    }

    expect(renderEnvExample(values)).toBe(committed.replace(/\r\n/g, '\n'));
  });

  it('contains every allowlisted key and no others', () => {
    const declared = readEnvFile(readFileSync(EXAMPLE_PATH, 'utf8')).map(([key]) => key);

    expect(declared).toEqual([...DEPLOY_CONFIG_ALLOWLIST]);
  });

  it('passes its own audit', () => {
    const entries = readEnvFile(readFileSync(EXAMPLE_PATH, 'utf8'));

    expect(auditDeployConfig(entries).ok).toBe(true);
  });

  it('never carries a CHANGE_ME placeholder', () => {
    // `CHANGE_ME` in a committed example file is a value a build will happily
    // bake in and serve, pointing at nothing. Per-environment values come from
    // `deploy/config-inject.sh`, which the build then verifies.
    expect(readFileSync(EXAMPLE_PATH, 'utf8')).not.toContain('CHANGE_ME');
  });
});

describe('the runtime version document', () => {
  it('lives at a fixed path the CDN and the API can both be asked for', () => {
    expect(VERSION_JSON_PATH).toBe('/version.json');
    expect(API_VERSION_PATH).toBe('/version');
  });

  it('parses a well-formed document', () => {
    const parsed = parseRuntimeVersion({
      release: 'v1.4.2',
      commit: '9e107d9d372bb6826bd81d3542a419d6',
      openapiVersion: 'v1',
      builtAtUtc: '2026-09-30T10:00:00Z',
    });

    expect(parsed.release).toBe('v1.4.2');
    expect(parsed.commit).toBe('9e107d9d372bb6826bd81d3542a419d6');
  });

  it('throws on a malformed document rather than defaulting', () => {
    // Defaulting would turn a detectable skew into a silent one, which is the
    // opposite of what the comparison is for.
    expect(() => parseRuntimeVersion({ release: 'v1' })).toThrow(/version\.json/);
    expect(() => parseRuntimeVersion({ release: '', commit: 'a', openapiVersion: 'v1', builtAtUtc: 't' })).toThrow();
    expect(() => parseRuntimeVersion(null)).toThrow();
    expect(() => parseRuntimeVersion('v1.4.2')).toThrow();
  });
});

describe('version comparison', () => {
  const served = parseRuntimeVersion({
    release: 'v1.4.2',
    commit: 'abc',
    openapiVersion: 'v1',
    builtAtUtc: 't',
  });

  it('reports MATCH for the same release', () => {
    expect(compareVersions('v1.4.2', served)).toBe('MATCH');
  });

  it('reports MISMATCH for a different release', () => {
    expect(compareVersions('v1.4.1', served)).toBe('MISMATCH');
  });

  it('reports UNKNOWN when the document could not be read', () => {
    // Not a special case of MATCH.
    expect(compareVersions('v1.4.2', null)).toBe('UNKNOWN');
  });

  it('reports UNKNOWN when either side is blank', () => {
    expect(compareVersions('', served)).toBe('UNKNOWN');
    expect(compareVersions('v1.4.2', { ...served, release: '  ' })).toBe('UNKNOWN');
  });

  it('ignores surrounding whitespace, because the tag is often a git describe', () => {
    expect(compareVersions('  v1.4.2  ', served)).toBe('MATCH');
  });
});
