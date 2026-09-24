import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import '../../../i18n/i18n.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { CostEstimateCard } from '../CostEstimateCard.js';

function renderCard(overrides: Record<string, unknown> = {}): void {
  render(
    <LocaleProvider>
      <CostEstimateCard
        estimate={{ amountUsd: 0.0667, segmentCount: 10, priceVersion: '1.0.0' }}
        currency="USD"
        monthToDate={12.5}
        quota={{ remaining: 4, resetsAt: '2026-09-25T00:00:00Z' }}
        configHash="cfg_abc"
        {...(overrides as object)}
      />
    </LocaleProvider>,
  );
}

describe('CostEstimateCard', () => {
  afterEach(() => {
    cleanup();
  });

  it('labels the cost figure as an estimate adjacent to the amount (R2)', () => {
    renderCard();
    expect(screen.getByTestId('preflight-estimate-label').textContent).toMatch(/estimate/i);
    const amount = screen.getByTestId('preflight-estimate-amount').textContent ?? '';
    expect(amount).toContain('$0.07');
    expect(amount).toMatch(/estimate/i);
  });

  it('shows quota remaining, remaining-after-run, spend, and the config hash', () => {
    renderCard();
    expect(screen.getByTestId('preflight-quota-remaining').textContent).toContain('4');
    expect(screen.getByTestId('preflight-quota-after').textContent).toContain('4');
    expect(screen.getByTestId('preflight-spend').textContent).toContain('$12.50');
    expect(screen.getByTestId('preflight-config-hash').textContent).toBe('cfg_abc');
    expect(screen.getByTestId('preflight-estimate-segments').textContent).toContain('10');
  });

  it('falls back gracefully when the config hash is missing', () => {
    renderCard({ configHash: undefined });
    expect(screen.getByTestId('preflight-config-hash').textContent).not.toBe('');
  });
});
