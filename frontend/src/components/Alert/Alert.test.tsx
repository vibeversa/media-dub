import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Alert } from './Alert.js';

afterEach(() => {
  cleanup();
});

describe('Alert', () => {
  it('renders title and pre-formatted details as text', () => {
    render(<Alert tone="error" title="Failed" details="<b>no html</b>" />);
    expect(screen.getByRole('alert').textContent).toContain('<b>no html</b>');
    expect(screen.getByRole('alert').querySelector('b')).toBeNull();
  });
});
