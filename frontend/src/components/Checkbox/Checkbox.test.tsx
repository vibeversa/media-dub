import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Checkbox } from './Checkbox.js';

afterEach(() => {
  cleanup();
});

describe('Checkbox', () => {
  it('toggles', () => {
    render(<Checkbox label="Consent" />);
    const box = screen.getByLabelText('Consent');
    fireEvent.click(box);
    expect((box as HTMLInputElement).checked).toBe(true);
  });
});
