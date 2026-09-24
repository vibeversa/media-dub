import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Card } from './Card.js';

afterEach(() => {
  cleanup();
});

describe('Card', () => {
  it('renders title and body', () => {
    render(<Card title="Usage">Body</Card>);
    expect(screen.getByText('Usage')).toBeDefined();
    expect(screen.getByText('Body')).toBeDefined();
  });
});
