import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { IconButton } from './IconButton.js';

afterEach(() => {
  cleanup();
});

describe('IconButton', () => {
  it('exposes an accessible label', () => {
    render(<IconButton label="Close">X</IconButton>);
    expect(screen.getByRole('button', { name: 'Close' })).toBeDefined();
  });

  it('handles click', () => {
    const onClick = vi.fn();
    render(
      <IconButton label="Copy" onClick={onClick}>
        C
      </IconButton>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Copy' }));
    expect(onClick).toHaveBeenCalledTimes(1);
  });
});
