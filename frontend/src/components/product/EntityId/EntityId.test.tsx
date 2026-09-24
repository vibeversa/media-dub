import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { EntityId } from './EntityId.js';

afterEach(() => {
  cleanup();
});

describe('EntityId', () => {
  it('renders id as text', () => {
    render(<EntityId id="prj_123" label="Project" />);
    expect(screen.getByText('prj_123')).toBeDefined();
    expect(screen.getByRole('button', { name: 'Copy Project' })).toBeDefined();
  });
});
