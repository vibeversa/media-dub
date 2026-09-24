import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { RelativeTime } from './RelativeTime.js';

afterEach(() => {
  cleanup();
});

describe('RelativeTime', () => {
  it('renders invalid input as-is', () => {
    render(<RelativeTime value="not-a-date" />);
    expect(screen.getByText('not-a-date')).toBeDefined();
  });

  it('renders a past date', () => {
    render(<RelativeTime value={new Date(Date.now() - 60_000).toISOString()} locale="en" />);
    expect(screen.getByRole('time')).toBeDefined();
  });
});
