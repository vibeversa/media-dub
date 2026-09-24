import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Panel } from './Panel.js';

afterEach(() => {
  cleanup();
});

describe('Panel', () => {
  it('renders', () => {
    render(<Panel title="Quality">Body</Panel>);
    expect(screen.getByText('Quality')).toBeDefined();
  });
});
