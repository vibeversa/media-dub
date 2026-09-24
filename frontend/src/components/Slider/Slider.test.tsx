import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Slider } from './Slider.js';

afterEach(() => {
  cleanup();
});

describe('Slider', () => {
  it('changes value', () => {
    render(<Slider label="Volume" min={0} max={100} defaultValue={20} />);
    const box = screen.getByLabelText(/Volume/);
    fireEvent.change(box, { target: { value: '42' } });
    expect((box as HTMLInputElement).value).toBe('42');
  });
});
