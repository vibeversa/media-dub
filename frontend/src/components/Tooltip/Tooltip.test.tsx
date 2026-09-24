import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Tooltip } from './Tooltip.js';

afterEach(() => {
  cleanup();
});

describe('Tooltip', () => {
  it('shows on focus and closes on Escape', () => {
    render(
      <Tooltip label="Help" content="Plain hint">
        <span>i</span>
      </Tooltip>,
    );
    fireEvent.focus(screen.getByRole('button', { name: 'Help' }));
    expect(screen.getByRole('tooltip').textContent).toBe('Plain hint');
    fireEvent.keyDown(screen.getByRole('button', { name: 'Help' }), { key: 'Escape' });
    expect(screen.queryByRole('tooltip')).toBeNull();
  });
});
