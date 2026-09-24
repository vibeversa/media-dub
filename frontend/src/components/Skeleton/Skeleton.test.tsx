import { cleanup, render } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Skeleton } from './Skeleton.js';

afterEach(() => {
  cleanup();
});

describe('Skeleton', () => {
  it('renders requested lines', () => {
    const { container } = render(<Skeleton lines={2} />);
    expect(container.querySelectorAll('.dp-skel-line').length).toBe(2);
  });
});
