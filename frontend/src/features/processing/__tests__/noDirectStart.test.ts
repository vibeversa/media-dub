import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

function readSource(relative: string): string {
  return readFileSync(join(process.cwd(), 'src', ...relative.split('/')), 'utf8');
}

describe('no direct-start code path (R1)', () => {
  it('keeps startProcessing out of the project list page', () => {
    expect(readSource('features/projects/ProjectsPage.tsx')).not.toContain('startProcessing');
  });

  it('keeps startProcessing out of the project details page', () => {
    expect(readSource('app/pages/ProjectDetailsPage.tsx')).not.toContain('startProcessing');
  });

  it('keeps startProcessing out of the creation wizard', () => {
    expect(readSource('features/projects/wizard/CreateWizard.tsx')).not.toContain('startProcessing');
  });

  it('confines the single start call to the preflight dialog', () => {
    const dialog = readSource('features/processing/PreflightDialog.tsx');
    expect(dialog).toContain('.startProcessing(');
    expect(dialog.match(/\.startProcessing\(/g)?.length).toBe(1);
  });
});
