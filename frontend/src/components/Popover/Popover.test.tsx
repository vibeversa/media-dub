import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Popover } from './Popover.js';

afterEach(() => {
  cleanup();
});

describe('Popover', () => {
  it('toggles and closes on Escape', () => {
    render(<Popover label="More" content="Details" />);
    fireEvent.click(screen.getByRole('button', { name: 'More' }));
    expect(screen.getByRole('dialog', { name: 'More' }).textContent).toContain('Details');
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
