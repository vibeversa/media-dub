import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { ToastProvider } from './Toast.js';
import { useToast } from './useToast.js';

function Pusher(): React.JSX.Element {
  const { push } = useToast();
  return (
    <button
      type="button"
      onClick={() => {
        push('info', 'Saved');
      }}
    >
      Push
    </button>
  );
}

function CapPusher(): React.JSX.Element {
  const { push } = useToast();
  return (
    <button
      type="button"
      onClick={() => {
        push('info', 'm1');
        push('info', 'm2');
        push('info', 'm3');
        push('info', 'm4');
      }}
    >
      PushMany
    </button>
  );
}

afterEach(() => {
  cleanup();
});

describe('Toast', () => {
  it('dedupes identical messages', () => {
    render(
      <ToastProvider>
        <Pusher />
      </ToastProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Push' }));
    fireEvent.click(screen.getByRole('button', { name: 'Push' }));
    expect(screen.getAllByText('Saved').length).toBe(1);
    expect(screen.getByRole('region', { name: 'Notifications' }).getAttribute('aria-live')).toBe('polite');
  });

  it('caps at 3 visible', () => {
    render(
      <ToastProvider>
        <CapPusher />
      </ToastProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'PushMany' }));
    const region = screen.getByRole('region', { name: 'Notifications' });
    expect(region.children.length).toBe(3);
    expect(region.textContent).not.toContain('m1');
  });
});
