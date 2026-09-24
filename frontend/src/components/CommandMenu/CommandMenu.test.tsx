import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { CommandMenu } from './CommandMenu.js';

afterEach(() => {
  cleanup();
});

describe('CommandMenu', () => {
  it('filters and selects with Enter', () => {
    const onSelect = vi.fn();
    render(
      <CommandMenu
        items={[
          { id: 'a', label: 'Go to dashboard' },
          { id: 'b', label: 'Go to review' },
        ]}
        onSelect={onSelect}
      />,
    );
    fireEvent.change(screen.getByLabelText('Search commands'), { target: { value: 'review' } });
    fireEvent.keyDown(screen.getByLabelText('Search commands'), { key: 'Enter' });
    expect(onSelect).toHaveBeenCalledWith('b');
  });
});
