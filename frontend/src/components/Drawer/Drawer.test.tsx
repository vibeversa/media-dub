import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Drawer } from './Drawer.js';

afterEach(() => {
  cleanup();
});

describe('Drawer', () => {
  it('closes on Escape and restores focus', () => {
    const onClose = vi.fn();
    render(
      <Drawer open title="Filters" onClose={onClose}>
        <button type="button">Apply</button>
      </Drawer>,
    );
    expect(screen.getByRole('dialog', { name: 'Filters' })).toBeDefined();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });
});
