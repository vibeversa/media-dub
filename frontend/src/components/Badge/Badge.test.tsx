import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Badge } from './Badge.js';

afterEach(() => {
  cleanup();
});

describe('Badge', () => {
  it('renders tones', () => {
    render(
      <>
        <Badge tone="success">Done</Badge>
        <Badge tone="error">Failed</Badge>
      </>,
    );
    expect(screen.getByText('Done')).toBeDefined();
    expect(screen.getByText('Failed')).toBeDefined();
  });
});
