import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { CostDisplay } from './CostDisplay.js';

afterEach(() => {
  cleanup();
});

describe('CostDisplay', () => {
  it('formats USD', () => {
    render(<CostDisplay amountUsd={12.5} />);
    expect(screen.getByText('$12.50')).toBeDefined();
  });
});
