import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Combobox } from './Combobox.js';

const OPTIONS = [
  { value: 'en', label: 'English' },
  { value: 'es', label: 'Spanish' },
];

afterEach(() => {
  cleanup();
});

describe('Combobox', () => {
  it('filters and picks with keyboard', () => {
    const onChange = vi.fn();
    render(<Combobox label="Language" options={OPTIONS} onChange={onChange} />);
    const box = screen.getByRole('combobox');
    fireEvent.focus(box);
    fireEvent.change(box, { target: { value: 'span' } });
    expect(screen.getByRole('option', { name: 'Spanish' })).toBeDefined();
    fireEvent.keyDown(box, { key: 'Enter' });
    expect(onChange).toHaveBeenCalledWith('es');
  });
});
