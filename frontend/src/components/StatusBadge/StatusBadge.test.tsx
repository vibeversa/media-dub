import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { StatusBadge } from './StatusBadge.js';
import { statusToVariant } from './statusMap.js';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('StatusBadge', () => {
  it('maps known statuses to tokens', () => {
    expect(statusToVariant('Completed')).toBe('success');
    expect(statusToVariant('Failed')).toBe('error');
    expect(statusToVariant('Running')).toBe('processing');
    expect(statusToVariant('Open')).toBe('review');
    expect(statusToVariant('Cancelled')).toBe('cancelled');
    render(<StatusBadge status="Completed" />);
    expect(screen.getByText('Completed')).toBeDefined();
  });

  it('falls back to neutral on unknown runtime strings', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    render(<StatusBadge status="SomethingNew" />);
    expect(screen.getByText('SomethingNew')).toBeDefined();
    expect(warn).toHaveBeenCalledTimes(1);
  });

  it('covers every generated-client output/review/preview state', () => {
    const states = ['Ready', 'Generating', 'Partial', 'Unavailable', 'Queued', 'Approved', 'Rejected', 'Healthy', 'Degraded', 'Down'] as const;
    for (const s of states) {
      expect(() => statusToVariant(s)).not.toThrow();
    }
  });
});
