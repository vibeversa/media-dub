import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Input } from './Input.js';

afterEach(() => {
  cleanup();
});

describe('Input', () => {
  it('associates label and input', () => {
    render(<Input label="Name" placeholder="Ada" />);
    expect(screen.getByLabelText('Name')).toBeDefined();
  });

  it('announces errors', () => {
    render(<Input label="Email" error="Required" />);
    expect(screen.getByRole('alert').textContent).toBe('Required');
  });

  it('emits changes', () => {
    render(<Input label="Name" />);
    const box = screen.getByLabelText('Name');
    fireEvent.change(box, { target: { value: 'Ada' } });
    expect((box as HTMLInputElement).value).toBe('Ada');
  });
});
