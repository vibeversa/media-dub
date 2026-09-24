import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Switch } from './Switch.js';

afterEach(() => {
  cleanup();
});

describe('Switch', () => {
  it('toggles with click and keyboard', () => {
    const onCheckedChange = vi.fn();
    render(<Switch label="SSE" checked={false} onCheckedChange={onCheckedChange} />);
    const sw = screen.getByRole('switch', { name: 'SSE' });
    fireEvent.click(sw);
    expect(onCheckedChange).toHaveBeenCalledWith(true);
    fireEvent.keyDown(sw, { key: 'Enter' });
  });

  it('reflects checked state', () => {
    render(<Switch label="SSE" checked onCheckedChange={() => {}} />);
    expect(screen.getByRole('switch').getAttribute('aria-checked')).toBe('true');
  });
});
