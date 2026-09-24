import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Breadcrumbs } from './Breadcrumbs.js';

afterEach(() => {
  cleanup();
});

describe('Breadcrumbs', () => {
  it('marks the current page', () => {
    render(<Breadcrumbs items={[{ label: 'Projects', href: '/projects' }, { label: 'Detail' }]} />);
    expect(screen.getByText('Detail').getAttribute('aria-current')).toBe('page');
  });
});
