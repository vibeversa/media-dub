import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Radio } from './Radio.js';

afterEach(() => {
  cleanup();
});

describe('Radio', () => {
  it('selects', () => {
    render(<Radio label="A" name="g" />);
    const box = screen.getByLabelText('A');
    fireEvent.click(box);
    expect((box as HTMLInputElement).checked).toBe(true);
  });
});
