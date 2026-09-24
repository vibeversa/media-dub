import { readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * R5 gate: no hidden-concern inputs may exist anywhere in the wizard.
 *
 * Scans every wizard source file (components, store, pure helpers) for
 * field-like occurrences of the forbidden stems. Import lines are excluded:
 * shared seams (`app/providers/*`, component barrels) legitimately mention
 * those directories without rendering inputs. The scan itself lives outside
 * the wizard directory so the gate never flags its own wording.
 */
const WIZARD_DIR = join(process.cwd(), 'src', 'features', 'projects', 'wizard');

const FORBIDDEN = [/provider/i, /\bmodels?\b/i, /\bqueues?\b/i];

function wizardFiles(): string[] {
  const entries = readdirSync(WIZARD_DIR, { withFileTypes: true });
  const files: string[] = [];
  for (const entry of entries) {
    if (entry.isFile() && /\.(ts|tsx)$/.test(entry.name)) {
      files.push(join(WIZARD_DIR, entry.name));
    }
    if (entry.isDirectory() && entry.name === '__tests__') {
      continue;
    }
  }
  return files;
}

function withoutImports(text: string): string {
  return text
    .split('\n')
    .filter((line) => !/^\s*import\s/.test(line))
    .join('\n');
}

describe('wizard input surface (R5)', () => {
  it('contains no hidden-concern fields in source', () => {
    const hits: string[] = [];
    for (const file of wizardFiles()) {
      const text = withoutImports(readFileSync(file, 'utf8'));
      for (const pattern of FORBIDDEN) {
        const match = text.match(pattern);
        if (match !== null) {
          hits.push(`${file}: ${match[0]}`);
        }
      }
    }
    expect(hits).toEqual([]);
  });
});
