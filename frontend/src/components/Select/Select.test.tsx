import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Select } from './Select.js';

const OPTIONS = [
  { value: 'a', label: 'Alpha' },
  { value: 'b', label: 'Beta' },
];

afterEach(() => {
  cleanup();
});

describe('Select', () => {
  it('selects an option', () => {
    render(<Select label="Voice" options={OPTIONS} />);
    const box = screen.getByLabelText('Voice');
    fireEvent.change(box, { target: { value: 'b' } });
    expect((box as HTMLSelectElement).value).toBe('b');
  });
});
