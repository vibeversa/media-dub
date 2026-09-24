import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { QuotaMeter } from './QuotaMeter.js';

afterEach(() => {
  cleanup();
});

describe('QuotaMeter', () => {
  it('flags over-threshold usage', () => {
    render(<QuotaMeter used={90} quota={100} label="Storage" />);
    expect(screen.getByRole('meter').getAttribute('aria-valuenow')).toBe('90');
  });
});
