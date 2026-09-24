import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { CorrelationId } from './CorrelationId.js';

afterEach(() => {
  cleanup();
});

describe('CorrelationId', () => {
  it('renders value as text', () => {
    render(<CorrelationId value="corr-123" />);
    expect(screen.getByText(/corr-123/)).toBeDefined();
  });
});
