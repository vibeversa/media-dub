import type { ReactNode } from 'react';

export interface CostDisplayProps {
  readonly amountUsd: number;
  readonly currency?: string;
  readonly locale?: string;
}

/** Currency formatting; amounts stay numeric until render. */
export function CostDisplay({ amountUsd, currency = 'USD', locale = 'en' }: CostDisplayProps): ReactNode {
  let text = `${amountUsd.toFixed(2)} ${currency}`;
  try {
    text = new Intl.NumberFormat(locale, { style: 'currency', currency }).format(amountUsd);
  } catch {
    // Keep the fallback above.
  }
  return <span>{text}</span>;
}
