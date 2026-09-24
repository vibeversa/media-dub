import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Tabs } from './Tabs.js';

afterEach(() => {
  cleanup();
});

describe('Tabs', () => {
  it('switches panels with click and arrows', () => {
    render(
      <Tabs
        items={[
          { id: 'a', label: 'Alpha', content: 'Panel A' },
          { id: 'b', label: 'Beta', content: 'Panel B' },
        ]}
      />,
    );
    fireEvent.click(screen.getByRole('tab', { name: 'Beta' }));
    expect(screen.getByRole('tabpanel').textContent).toBe('Panel B');
    fireEvent.keyDown(screen.getByRole('tab', { name: 'Beta' }), { key: 'ArrowLeft' });
    expect(screen.getByRole('tabpanel').textContent).toBe('Panel A');
  });
});
