import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Textarea } from './Textarea.js';

afterEach(() => {
  cleanup();
});

describe('Textarea', () => {
  it('renders label and error', () => {
    render(<Textarea label="Notes" error="Too long" />);
    expect(screen.getByLabelText('Notes')).toBeDefined();
    expect(screen.getByRole('alert').textContent).toBe('Too long');
  });
});
