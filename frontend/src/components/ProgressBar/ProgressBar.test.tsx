import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { ProgressBar } from './ProgressBar.js';

afterEach(() => {
  cleanup();
});

describe('ProgressBar', () => {
  it('clamps and exposes ARIA values', () => {
    render(<ProgressBar value={150} label="Upload" />);
    expect(screen.getByRole('progressbar').getAttribute('aria-valuenow')).toBe('100');
  });
});
