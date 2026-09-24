import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Ring } from './Ring.js';

afterEach(() => {
  cleanup();
});

describe('Ring', () => {
  it('exposes progress', () => {
    render(<Ring value={25} label="Sync" />);
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('25');
  });
});
