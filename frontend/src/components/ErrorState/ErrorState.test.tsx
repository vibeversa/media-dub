import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ErrorState } from './ErrorState.js';

afterEach(() => {
  cleanup();
});

describe('ErrorState', () => {
  it('retries and shows correlation id', () => {
    const onRetry = vi.fn();
    render(<ErrorState title="Load failed" correlationId="abc" onRetry={onRetry} />);
    expect(screen.getByRole('alert').textContent).toContain('abc');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });
});
