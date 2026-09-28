// Task 039B: component state-matrix gap closure.
//
// Closes every below-80% branch/function gap in `src/components` runtime
// files (the 039A gap report). Each case pins the non-color signal
// (role/aria/text — never color-only, supporting 041C) and, for failure
// states, the recovery action (retry/dismiss/close per §11.6). Storybook
// stories are excluded from coverage by policy (see `docs/coverage.md`);
// this spec owns the runtime behavior instead.
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Card } from '../Card/Card.js';
import { Combobox } from '../Combobox/Combobox.js';
import { CommandMenu } from '../CommandMenu/CommandMenu.js';
import { Drawer } from '../Drawer/Drawer.js';
import { EmptyState } from '../EmptyState/EmptyState.js';
import { ErrorState } from '../ErrorState/ErrorState.js';
import { Modal } from '../Modal/Modal.js';
import { Pagination } from '../Pagination/Pagination.js';
import { Panel } from '../Panel/Panel.js';
import { ProgressBar } from '../ProgressBar/ProgressBar.js';
import { Ring } from '../Ring/Ring.js';
import { Slider } from '../Slider/Slider.js';
import { StatusBadge } from '../StatusBadge/StatusBadge.js';
import { isKnownStatus, statusToVariant, warnUnknownStatus } from '../StatusBadge/statusMap.js';
import { Tabs } from '../Tabs/Tabs.js';
import { ToastProvider } from '../Toast/Toast.js';
import { useToast } from '../Toast/useToast.js';
import { Tooltip } from '../Tooltip/Tooltip.js';
import { CorrelationId } from '../product/CorrelationId/CorrelationId.js';
import { CostDisplay } from '../product/CostDisplay/CostDisplay.js';
import { EntityId } from '../product/EntityId/EntityId.js';
import { ProviderBadge } from '../product/ProviderBadge/ProviderBadge.js';
import { QuotaMeter } from '../product/QuotaMeter/QuotaMeter.js';
import { RelativeTime } from '../product/RelativeTime/RelativeTime.js';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('Card/Panel title matrix', () => {
  it('renders Card without a title (no heading, children intact)', () => {
    render(
      <Card>
        <span data-testid="card-child">body</span>
      </Card>,
    );
    expect(screen.getByTestId('card-child').textContent).toBe('body');
    expect(screen.queryByRole('heading')).toBeNull();
  });

  it('renders Panel without a title', () => {
    render(
      <Panel>
        <span data-testid="panel-child">body</span>
      </Panel>,
    );
    expect(screen.getByTestId('panel-child').textContent).toBe('body');
    expect(screen.queryByRole('heading')).toBeNull();
  });
});

describe('EmptyState/ErrorState matrix', () => {
  it('renders EmptyState without description or action (title text only)', () => {
    render(<EmptyState title="Nothing here" />);
    expect(screen.getByText('Nothing here')).toBeDefined();
  });

  it('renders ErrorState title-only (no message, no ref, no retry)', () => {
    render(<ErrorState title="Failed" />);
    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Failed');
    expect(screen.queryByRole('button', { name: 'Retry' })).toBeNull();
  });

  it('renders ErrorState with message, ref, and a working retry (recovery action)', () => {
    const onRetry = vi.fn();
    render(<ErrorState title="Failed" message="Try again." correlationId="corr-1" onRetry={onRetry} />);
    const alert = screen.getByRole('alert');
    expect(alert.textContent).toContain('Try again.');
    expect(alert.textContent).toContain('Ref: corr-1');
    fireEvent.click(screen.getByRole('button', { name: 'Retry' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });
});

describe('ProgressBar/Ring/Slider matrix', () => {
  it('renders ProgressBar with a label, custom max, and clamped values', () => {
    const { unmount } = render(<ProgressBar value={30} max={60} label="Upload" />);
    const bar = screen.getByRole('progressbar', { name: 'Upload' });
    expect(bar.getAttribute('aria-valuenow')).toBe('50');
    unmount();
    cleanup();
    render(<ProgressBar value={150} />);
    expect(screen.getByRole('progressbar', { name: 'Progress' }).getAttribute('aria-valuenow')).toBe('100');
  });

  it('renders Ring with a label and clamps negative values', () => {
    render(<Ring value={-5} label="Quota" />);
    expect(screen.getByRole('progressbar', { name: 'Quota' }).getAttribute('aria-valuenow')).toBe('0');
  });

  it('renders Ring with the default accessible label', () => {
    render(<Ring value={25} />);
    const bar = screen.getByRole('progressbar', { name: 'Progress' });
    expect(bar.getAttribute('aria-valuenow')).toBe('25');
  });

  it('renders Slider readout when a value is set and honors a custom id', () => {
    render(<Slider label="Volume" id="vol" value={7} onChange={() => {}} />);
    expect(screen.getByText('Volume (7)')).toBeDefined();
    expect(screen.getByLabelText('Volume (7)').getAttribute('id')).toBe('vol');
  });
});

describe('statusMap matrix (non-color signals)', () => {
  it('maps one status per tone', () => {
    expect(statusToVariant('Completed')).toBe('success');
    expect(statusToVariant('Pending')).toBe('warning');
    expect(statusToVariant('Running')).toBe('processing');
    expect(statusToVariant('Open')).toBe('review');
    expect(statusToVariant('Failed')).toBe('error');
    expect(statusToVariant('Cancelled')).toBe('cancelled');
    expect(statusToVariant('Created')).toBe('info');
  });

  it('throws on unknown statuses (exhaustive switch)', () => {
    expect(() => statusToVariant('BOGUS' as never)).toThrow('Unhandled status: BOGUS');
  });

  it('classifies known vs unknown status strings', () => {
    expect(isKnownStatus('Failed')).toBe(true);
    expect(isKnownStatus('BOGUS')).toBe(false);
  });

  it('warns with plain text for unknown statuses (dev-visible, non-color)', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    warnUnknownStatus('BOGUS');
    expect(warn).toHaveBeenCalledWith('[StatusBadge] unknown status: BOGUS');
  });

  it('renders unknown statuses as neutral text (never crashes, never color-only)', () => {
    const warn = vi.spyOn(console, 'warn').mockImplementation(() => {});
    render(<StatusBadge status="BOGUS" />);
    expect(screen.getByText('BOGUS')).toBeDefined();
    expect(warn).toHaveBeenCalledWith('[StatusBadge] unknown status: BOGUS');
  });
});

describe('Tabs matrix', () => {
  const items = [
    { id: 'a', label: 'Alpha', content: <span data-testid="tab-content-a">A</span> },
    { id: 'b', label: 'Beta', content: <span data-testid="tab-content-b">B</span> },
  ];

  it('honors defaultId on first paint', () => {
    render(<Tabs items={items} defaultId="b" />);
    expect(screen.getByTestId('tab-content-b')).toBeDefined();
    expect(screen.getByRole('tab', { name: 'Beta' }).getAttribute('aria-selected')).toBe('true');
  });

  it('renders no panel for an empty item list (never crashes)', () => {
    render(<Tabs items={[]} />);
    expect(screen.getByRole('tablist')).toBeDefined();
    expect(screen.queryByRole('tabpanel')).toBeNull();
  });

  it('moves with ArrowLeft (roving tabindex, non-color signal)', () => {
    render(<Tabs items={items} defaultId="b" />);
    const tabB = screen.getByRole('tab', { name: 'Beta' });
    tabB.focus();
    fireEvent.keyDown(tabB, { key: 'ArrowLeft' });
    expect(screen.getByRole('tab', { name: 'Alpha' }).getAttribute('aria-selected')).toBe('true');
    expect(screen.getByTestId('tab-content-a')).toBeDefined();
  });
});

describe('Toast matrix', () => {
  function Dismisser(): React.JSX.Element {
    const { push, dismiss, toasts } = useToast();
    return (
      <div>
        <button
          type="button"
          onClick={() => {
            push('error', 'Boom');
          }}
        >
          Push
        </button>
        {toasts.map((t) => (
          <button
            key={t.id}
            type="button"
            onClick={() => {
              dismiss(t.id);
            }}
          >
            Dismiss-{t.id}
          </button>
        ))}
      </div>
    );
  }

  it('dismisses via the toast close button (recovery action)', () => {
    render(
      <ToastProvider>
        <Dismisser />
      </ToastProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Push' }));
    expect(screen.getByText('Boom')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Dismiss: Boom' }));
    expect(screen.queryByText('Boom')).toBeNull();
  });

  it('dismisses via the context dismiss fn', () => {
    render(
      <ToastProvider>
        <Dismisser />
      </ToastProvider>,
    );
    fireEvent.click(screen.getByRole('button', { name: 'Push' }));
    const dismissBtn = screen.getByRole('button', { name: /Dismiss-\d+/ });
    fireEvent.click(dismissBtn);
    expect(screen.queryByText('Boom')).toBeNull();
  });

  it('throws outside the provider (fail-fast, never silent)', () => {
    function Outside(): React.JSX.Element {
      useToast();
      return <span>nope</span>;
    }
    expect(() => render(<Outside />)).toThrow('useToast must be used inside ToastProvider.');
  });
});

describe('Tooltip matrix', () => {
  it('opens on hover/focus and closes on leave/blur/Escape (text content, not color)', () => {
    render(
      <Tooltip label="More info" content="Plain help text">
        <span>trigger</span>
      </Tooltip>,
    );
    const trigger = screen.getByRole('button', { name: 'More info' });
    expect(screen.queryByRole('tooltip')).toBeNull();
    fireEvent.mouseEnter(trigger);
    expect(screen.getByRole('tooltip').textContent).toBe('Plain help text');
    fireEvent.mouseLeave(trigger);
    expect(screen.queryByRole('tooltip')).toBeNull();
    fireEvent.focus(trigger);
    expect(screen.getByRole('tooltip')).toBeDefined();
    fireEvent.keyDown(trigger, { key: 'Escape' });
    expect(screen.queryByRole('tooltip')).toBeNull();
    fireEvent.focus(trigger);
    expect(screen.getByRole('tooltip')).toBeDefined();
    fireEvent.blur(trigger);
    expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('closes on document Escape while open', () => {
    render(
      <Tooltip label="More info" content="Plain help text">
        <span>trigger</span>
      </Tooltip>,
    );
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'More info' }));
    expect(screen.getByRole('tooltip')).toBeDefined();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(screen.queryByRole('tooltip')).toBeNull();
  });
});

describe('Combobox matrix', () => {
  const options = [
    { value: 'en', label: 'English' },
    { value: 'ar', label: 'Arabic' },
  ];

  function renderCombo(props?: Partial<React.ComponentProps<typeof Combobox>>): void {
    render(<Combobox label="Language" options={options} onChange={() => {}} {...props} />);
  }

  it('opens on focus and filters to no-matches text', () => {
    const onChange = vi.fn();
    renderCombo({ onChange });
    const input = screen.getByRole('combobox');
    expect(input.getAttribute('placeholder')).toBe('Search…');
    fireEvent.focus(input);
    expect(screen.getByRole('listbox')).toBeDefined();
    fireEvent.change(input, { target: { value: 'zzz' } });
    expect(screen.getByText('No matches.')).toBeDefined();
    expect(onChange).not.toHaveBeenCalled();
  });

  it('shows the selected label as placeholder and navigates with arrows + Enter', () => {
    const onChange = vi.fn();
    renderCombo({ value: 'ar', onChange });
    const input = screen.getByRole('combobox');
    expect(input.getAttribute('placeholder')).toBe('Arabic');
    fireEvent.focus(input);
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'ArrowUp' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onChange).toHaveBeenCalledWith('en');
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('closes on Escape and blur, chooses on option mousedown', () => {
    const onChange = vi.fn();
    renderCombo({ onChange, placeholder: 'Pick one' });
    const input = screen.getByRole('combobox');
    expect(input.getAttribute('placeholder')).toBe('Pick one');
    fireEvent.focus(input);
    expect(screen.getByRole('listbox')).toBeDefined();
    fireEvent.keyDown(input, { key: 'Escape' });
    expect(screen.queryByRole('listbox')).toBeNull();
    fireEvent.focus(input);
    fireEvent.blur(input);
    expect(screen.queryByRole('listbox')).toBeNull();
    fireEvent.focus(input);
    fireEvent.mouseDown(screen.getByRole('option', { name: 'Arabic' }));
    expect(onChange).toHaveBeenCalledWith('ar');
  });
});

describe('CommandMenu matrix', () => {
  const items = [
    { id: 'new', label: 'New project' },
    { id: 'open', label: 'Open project' },
  ];

  it('filters, arrows, Enters, and mouse-selects (custom placeholder)', () => {
    const onSelect = vi.fn();
    render(<CommandMenu items={items} onSelect={onSelect} placeholder="Find…" />);
    const input = screen.getByLabelText('Search commands');
    expect(input.getAttribute('placeholder')).toBe('Find…');
    fireEvent.change(input, { target: { value: 'open' } });
    expect(screen.queryByRole('option', { name: 'New project' })).toBeNull();
    fireEvent.keyDown(input, { key: 'ArrowDown' });
    fireEvent.keyDown(input, { key: 'ArrowUp' });
    fireEvent.keyDown(input, { key: 'Enter' });
    expect(onSelect).toHaveBeenCalledWith('open');
  });

  it('selects on option mousedown with the default placeholder', () => {
    const onSelect = vi.fn();
    render(<CommandMenu items={items} onSelect={onSelect} />);
    expect(screen.getByLabelText('Search commands').getAttribute('placeholder')).toBe('Type a command…');
    fireEvent.mouseDown(screen.getByRole('option', { name: 'New project' }));
    expect(onSelect).toHaveBeenCalledWith('new');
  });
});

describe('Drawer/Modal matrix', () => {
  it('renders nothing when closed (Drawer)', () => {
    render(
      <Drawer open={false} title="Filters" onClose={() => {}}>
        <span>child</span>
      </Drawer>,
    );
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('closes on overlay click but not on inner clicks (Drawer)', () => {
    const onClose = vi.fn();
    render(
      <Drawer open title="Filters" onClose={onClose}>
        <span data-testid="drawer-child">child</span>
      </Drawer>,
    );
    const dialog = screen.getByRole('dialog', { name: 'Filters' });
    expect(dialog).toBeDefined();
    fireEvent.mouseDown(screen.getByTestId('drawer-child'));
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.mouseDown(dialog);
    expect(onClose).not.toHaveBeenCalled();
    const overlay = dialog.parentElement;
    expect(overlay).toBeDefined();
    fireEvent.mouseDown(overlay!);
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('closes on Escape (Drawer)', () => {
    const onClose = vi.fn();
    render(
      <Drawer open title="Filters" onClose={onClose}>
        <span>child</span>
      </Drawer>,
    );
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('renders nothing when closed (Modal)', () => {
    render(
      <Modal open={false} title="Confirm" onClose={() => {}}>
        <span>child</span>
      </Modal>,
    );
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('closes on overlay/Escape but not on inner clicks (Modal)', () => {
    const onClose = vi.fn();
    render(
      <Modal open title="Confirm" onClose={onClose}>
        <span data-testid="modal-child">child</span>
      </Modal>,
    );
    const dialog = screen.getByRole('dialog', { name: 'Confirm' });
    fireEvent.mouseDown(screen.getByTestId('modal-child'));
    expect(onClose).not.toHaveBeenCalled();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    fireEvent.mouseDown(dialog.parentElement!);
    expect(onClose).toHaveBeenCalledTimes(2);
  });

  it('traps Tab focus and restores it on close (Modal)', () => {
    const onClose = vi.fn();
    function Harness({ open }: { readonly open: boolean }): React.JSX.Element {
      return (
        <div>
          <button type="button">outside</button>
          <Modal open={open} title="Confirm" onClose={onClose}>
            <button type="button">inner</button>
          </Modal>
        </div>
      );
    }
    const { rerender } = render(<Harness open={false} />);
    const outside = screen.getByRole('button', { name: 'outside' });
    outside.focus();
    rerender(<Harness open={true} />);
    expect(screen.getByRole('dialog', { name: 'Confirm' })).toBeDefined();
    fireEvent.keyDown(document, { key: 'Enter' });
    const inner = screen.getByRole('button', { name: 'inner' });
    inner.focus();
    fireEvent.keyDown(document, { key: 'Tab' });
    expect(document.activeElement).toBe(inner);
    fireEvent.keyDown(document, { key: 'Tab', shiftKey: true });
    expect(document.activeElement).toBe(inner);
    rerender(<Harness open={false} />);
    expect(document.activeElement).toBe(outside);
  });
});

describe('Pagination matrix', () => {
  it('pages forward/back with text signals and disables at the edges', () => {
    const onPageChange = vi.fn();
    const { rerender } = render(<Pagination page={2} pageSize={10} total={30} onPageChange={onPageChange} />);
    expect(screen.getByText('Page 2 of 3')).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Previous' }));
    expect(onPageChange).toHaveBeenCalledWith(1);
    fireEvent.click(screen.getByRole('button', { name: 'Next' }));
    expect(onPageChange).toHaveBeenCalledWith(3);
    rerender(<Pagination page={1} pageSize={10} total={30} onPageChange={onPageChange} />);
    expect(screen.getByRole('button', { name: 'Previous' }).hasAttribute('disabled')).toBe(true);
    rerender(<Pagination page={3} pageSize={10} total={30} onPageChange={onPageChange} />);
    expect(screen.getByRole('button', { name: 'Next' }).hasAttribute('disabled')).toBe(true);
  });
});

describe('product components matrix', () => {
  function stubClipboard(resolve: boolean): void {
    Object.defineProperty(window.navigator, 'clipboard', {
      value: {
        writeText: resolve ? vi.fn().mockResolvedValue(undefined) : vi.fn().mockRejectedValue(new Error('denied')),
      },
      configurable: true,
    });
  }

  afterEach(() => {
    // jsdom ships no clipboard; restore the absent state after stubbed cases.
    if ('clipboard' in window.navigator) {
      try {
        // @ts-expect-error test-only removal of the stubbed clipboard
        delete window.navigator.clipboard;
      } catch {
        // Keep the stub when deletion is refused.
      }
    }
  });

  it('copies the correlation id (resolve + reject paths)', async () => {
    stubClipboard(true);
    render(<CorrelationId value="corr-9" />);
    expect(screen.getByText(/corr: corr-9/)).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Copy correlation id' }));
    await vi.waitFor(() => {
      expect(window.navigator.clipboard.writeText).toHaveBeenCalledWith('corr-9');
    });
    cleanup();
    stubClipboard(false);
    render(<CorrelationId value="corr-9" />);
    fireEvent.click(screen.getByRole('button', { name: 'Copy correlation id' }));
    await vi.waitFor(() => {
      expect(window.navigator.clipboard.writeText).toHaveBeenCalledWith('corr-9');
    });
  });

  it('falls back to plain text for unformattable currencies/locales', () => {
    render(<CostDisplay amountUsd={12.5} currency="XX!" />);
    expect(screen.getByText('12.50 XX!')).toBeDefined();
    cleanup();
    render(<CostDisplay amountUsd={12.5} currency="USD" locale="!!!" />);
    expect(screen.getByText('12.50 USD')).toBeDefined();
  });

  it('copies entity ids with label, copied state, and no-clipboard path', async () => {
    render(<EntityId id="prj_1" />);
    expect(screen.getByRole('button', { name: 'Copy id' })).toBeDefined();
    fireEvent.click(screen.getByRole('button', { name: 'Copy id' }));
    expect(screen.getByRole('button', { name: 'Copy id' }).textContent).toBe('Copy');
    cleanup();
    stubClipboard(true);
    render(<EntityId id="prj_1" label="Project" />);
    fireEvent.click(screen.getByRole('button', { name: 'Copy Project' }));
    expect(await screen.findByRole('button', { name: 'Copy Project' })).toBeDefined();
    await vi.waitFor(() => {
      expect(screen.getByText('Copied')).toBeDefined();
    });
  });

  it('labels provider badges with the provider name (text, not color)', () => {
    render(<ProviderBadge status="Healthy" provider="acme" />);
    expect(screen.getByText('acme: Healthy')).toBeDefined();
    cleanup();
    render(<ProviderBadge status="Healthy" />);
    expect(screen.getByText('Healthy')).toBeDefined();
  });

  it('meters quota with label, threshold breach, and usage text', () => {
    render(<QuotaMeter used={90} quota={100} label="Storage" warningThreshold={0.8} />);
    const meter = screen.getByRole('meter', { name: 'Storage' });
    expect(meter.getAttribute('aria-valuenow')).toBe('90');
    expect(screen.getByText('90 / 100')).toBeDefined();
    cleanup();
    render(<QuotaMeter used={10} quota={100} />);
    expect(screen.getByRole('meter', { name: 'Quota' }).getAttribute('aria-valuenow')).toBe('10');
  });

  it('renders relative, absolute-fallback, and invalid time', () => {
    const fiveMinAgo = new Date(Date.now() - 5 * 60_000).toISOString();
    render(<RelativeTime value={fiveMinAgo} locale="en" />);
    expect(screen.getByText('5 minutes ago')).toBeDefined();
    cleanup();
    const threeHoursAgo = new Date(Date.now() - 3 * 3_600_000).toISOString();
    render(<RelativeTime value={threeHoursAgo} locale="en" />);
    expect(screen.getByText('3 hours ago')).toBeDefined();
    cleanup();
    const threeDaysAgo = new Date(Date.now() - 3 * 86_400_000).toISOString();
    render(<RelativeTime value={threeDaysAgo} locale="en" />);
    expect(screen.getByText('3 days ago')).toBeDefined();
    cleanup();
    render(<RelativeTime value="not-a-date" />);
    expect(screen.getByText('not-a-date')).toBeDefined();
    cleanup();
    const recent = new Date(Date.now() - 60_000).toISOString();
    render(<RelativeTime value={recent} locale="!!!" />);
    const fallback = document.querySelector('time');
    expect(fallback?.getAttribute('dateTime')).toBe(recent);
    expect(fallback?.textContent).toContain(recent.slice(0, 10));
    cleanup();
    render(<RelativeTime value={recent} />);
    const navFallback = document.querySelector('time');
    expect(navFallback?.getAttribute('dateTime')).toBe(recent);
    expect(navFallback?.textContent?.length).toBeGreaterThan(0);
    cleanup();
    const rtf = Intl.RelativeTimeFormat;
    try {
      Object.defineProperty(Intl, 'RelativeTimeFormat', {
        value: function () {
          throw new RangeError('unsupported locale');
        },
        configurable: true,
        writable: true,
      });
      render(<RelativeTime value={recent} locale="en" />);
      const absolute = document.querySelector('time');
      expect(absolute?.getAttribute('dateTime')).toBe(recent);
      expect(absolute?.textContent?.length).toBeGreaterThan(0);
    } finally {
      Object.defineProperty(Intl, 'RelativeTimeFormat', { value: rtf, configurable: true, writable: true });
    }
  });
});
