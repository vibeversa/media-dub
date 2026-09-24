import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { ProviderBadge } from './ProviderBadge.js';

afterEach(() => {
  cleanup();
});

describe('ProviderBadge', () => {
  it('renders provider health', () => {
    render(<ProviderBadge status="Healthy" provider="tts" />);
    expect(screen.getByText('tts: Healthy')).toBeDefined();
  });
});
