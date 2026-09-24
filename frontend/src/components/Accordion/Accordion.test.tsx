import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Accordion } from './Accordion.js';

afterEach(() => {
  cleanup();
});

describe('Accordion', () => {
  it('expands and collapses', () => {
    render(<Accordion items={[{ id: 'a', title: 'Title', content: 'Body' }]} />);
    fireEvent.click(screen.getByRole('button', { name: 'Title' }));
    expect(screen.getByRole('region').textContent).toBe('Body');
    fireEvent.click(screen.getByRole('button', { name: 'Title' }));
    expect(screen.queryByRole('region')).toBeNull();
  });
});
