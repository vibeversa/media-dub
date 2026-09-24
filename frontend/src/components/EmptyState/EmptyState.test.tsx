import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { EmptyState } from './EmptyState.js';

afterEach(() => {
  cleanup();
});

describe('EmptyState', () => {
  it('renders title and action', () => {
    render(<EmptyState title="No projects" description="Create one." action={<button type="button">New</button>} />);
    expect(screen.getByText('No projects')).toBeDefined();
    expect(screen.getByRole('button', { name: 'New' })).toBeDefined();
  });
});
