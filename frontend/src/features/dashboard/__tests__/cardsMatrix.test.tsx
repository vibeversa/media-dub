// Task 039B: dashboard state-matrix gap closure.
//
// Direct prop-matrix specs for the four below-80% dashboard cards. Each
// failure state asserts its recovery action (per-card retry per §11.6) and
// each status asserts a non-color signal (role/text, supporting 041C).
// Page-level loading/empty/error/stale/permission states stay in
// `dashboard.test.tsx`; this file closes the card-level branch gaps.
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, describe, expect, it, vi } from 'vitest';
import '../../../i18n/i18n.js';
import { BacklogCard } from '../BacklogCard.js';
import { ProviderWarnings } from '../ProviderWarnings.js';
import { RecentOutputs } from '../RecentOutputs.js';
import { StatCard } from '../StatCard.js';

afterEach(() => {
  cleanup();
});

function renderCard(element: React.JSX.Element): void {
  render(<MemoryRouter>{element}</MemoryRouter>);
}

describe('StatCard matrix', () => {
  it('renders the per-card error with retry when the section is missing', () => {
    const onRetry = vi.fn();
    renderCard(<StatCard counts={undefined} onRetry={onRetry} />);
    const alert = screen.getByTestId('dashboard-stat-card');
    expect(alert.getAttribute('role')).toBe('alert');
    fireEvent.click(screen.getByTestId('dashboard-stat-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });
});

describe('BacklogCard matrix', () => {
  it('renders the per-card error with retry when the section is missing', () => {
    const onRetry = vi.fn();
    renderCard(<BacklogCard backlog={undefined} onRetry={onRetry} />);
    const alert = screen.getByTestId('dashboard-backlog');
    expect(alert.getAttribute('role')).toBe('alert');
    fireEvent.click(screen.getByTestId('dashboard-backlog-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });
});

describe('RecentOutputs matrix', () => {
  it('renders the per-card error with retry when the section is missing', () => {
    const onRetry = vi.fn();
    renderCard(<RecentOutputs outputs={undefined} onRetry={onRetry} />);
    expect(screen.getByTestId('dashboard-recent-outputs').getAttribute('role')).toBe('alert');
    fireEvent.click(screen.getByTestId('dashboard-outputs-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('renders the inline empty message for zero outputs (not an error)', () => {
    renderCard(<RecentOutputs outputs={[]} onRetry={() => {}} />);
    const card = screen.getByTestId('dashboard-recent-outputs');
    expect(card.getAttribute('role')).not.toBe('alert');
    expect(card.textContent).toContain('No outputs yet');
  });

  it('omits the timestamp when createdAt is empty (text signal, no crash)', () => {
    renderCard(
      <RecentOutputs
        outputs={[{ id: 'out-1', projectId: 'prj_1', mediaKind: 'audio', container: 'mp3', createdAt: '' }]}
        onRetry={() => {}}
      />,
    );
    const row = screen.getByTestId('dashboard-output-out-1');
    expect(row.querySelector('time')).toBeNull();
    expect(screen.getByTestId('dashboard-output-link-out-1').getAttribute('href')).toBe('/projects/prj_1');
  });

  it('renders the unknown-project span when the owner is missing', () => {
    renderCard(
      <RecentOutputs
        outputs={[{ id: 'out-2', projectId: '', mediaKind: 'video', container: 'mp4', createdAt: '2024-01-15T12:00:00Z' }]}
        onRetry={() => {}}
      />,
    );
    expect(screen.getByTestId('dashboard-output-out-2').textContent).toContain('Unknown project');
    expect(screen.queryByTestId('dashboard-output-link-out-2')).toBeNull();
  });
});

describe('ProviderWarnings matrix', () => {
  it('renders the per-card error with retry when the section is missing', () => {
    const onRetry = vi.fn();
    renderCard(<ProviderWarnings warnings={undefined} onRetry={onRetry} />);
    expect(screen.getByTestId('dashboard-warnings').getAttribute('role')).toBe('alert');
    fireEvent.click(screen.getByTestId('dashboard-warnings-retry'));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('renders tenant-wide warnings as plain text (no project link)', () => {
    renderCard(
      <ProviderWarnings warnings={[{ code: 'QUOTA_LOW', message: 'Quota is low.', projectId: undefined }]} onRetry={() => {}} />,
    );
    expect(screen.getByTestId('dashboard-warning-QUOTA_LOW').textContent).toContain('Quota is low.');
    expect(screen.queryByTestId('dashboard-warning-link-QUOTA_LOW')).toBeNull();
    expect(screen.getByTestId('dashboard-warnings-link').getAttribute('href')).toBe('/projects');
  });
});
