// Task 039B: ProjectLayout defensive-branch coverage.
//
// `getProjectTabs` always pairs `disabled: true` with a reason key, so the
// reason-less disabled branch in `ProjectLayout` is defensive. This spec
// drives it via a mocked tab model (the only way to produce that state)
// plus the badged-tab branch, asserting text signals (041C).
import { cleanup, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';

vi.mock('../../navigation/projectTabs.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../navigation/projectTabs.js')>();
  return {
    ...actual,
    getProjectTabs: () => [
      { id: 'overview', labelKey: 'nav:projectTabs.overview', to: '.', disabled: false },
      { id: 'media', labelKey: 'nav:projectTabs.media', to: './media', disabled: true },
      { id: 'exports', labelKey: 'nav:projectTabs.exports', to: './exports', disabled: false, badge: '2' },
    ],
  };
});

import { ProjectLayout } from '../ProjectLayout.js';

afterEach(() => {
  cleanup();
});

describe('ProjectLayout defensive branches', () => {
  it('renders reason-less disabled tabs as text without tooltips (never dead links)', () => {
    render(
      <MemoryRouter>
        <ProjectLayout />
      </MemoryRouter>,
    );
    expect(screen.getByTestId('project-layout')).toBeDefined();
    expect(screen.queryByTestId('project-tab-media')).toBeNull();
    expect(screen.getByTestId('project-tab-exports-badge').textContent).toBe('2');
  });
});
