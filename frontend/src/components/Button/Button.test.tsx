import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Button } from './Button.js';

afterEach(() => {
  cleanup();
});

describe('Button', () => {
  it('renders variants and sizes', () => {
    render(
      <>
        <Button variant="primary">Primary</Button>
        <Button variant="danger" size="lg">
          Danger
        </Button>
      </>,
    );
    expect(screen.getByRole('button', { name: 'Primary' })).toBeDefined();
    expect(screen.getByRole('button', { name: 'Danger' })).toBeDefined();
  });

  it('handles click', () => {
    const onClick = vi.fn();
    render(<Button onClick={onClick}>Click</Button>);
    fireEvent.click(screen.getByRole('button', { name: 'Click' }));
    expect(onClick).toHaveBeenCalledTimes(1);
  });

  it('blocks interaction while loading', () => {
    render(<Button loading>Save</Button>);
    expect(screen.getByRole('button').hasAttribute('disabled')).toBe(true);
  });
});
