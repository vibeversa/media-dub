// Task 041C: the speaker list's accessible structure.
//
// The list was a `role="listbox"` of `role="option"` rows, each wrapping a real
// `<button>`. This task's axe scan reported three rules on the voices screen
// that all traced back to that one decision:
//
//   `nested-interactive` (serious) - a focusable control inside a role that must
//     not contain one;
//   `target-size` (serious) - the scroll container's `tabIndex={0}` put a
//     320x8 sliver of an overflow box in the tab order;
//   `landmark-unique` (moderate) - every `PreviewPlayer` was labelled
//     "Voice preview", so a two-voice list had two identically-named landmarks.
//
// The E2E audit (`e2e/a11y/axe.spec.ts`) is the gate. These cases are the cheap
// unit-level guard that names the specific attributes when it regresses, because
// "axe found nested-interactive on the voices screen" is a much worse error
// message than "the option role came back".

import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import '../../../i18n/i18n.js';
import { setInnerFetchForTests, setTokenProvider } from '../../../api/client/index.js';
import { LocaleProvider } from '../../../app/providers/LocaleProvider.js';
import { queryClient } from '../../../app/providers/queryClient.js';
import { Card } from '../../../components/Card/Card.js';
import { Panel } from '../../../components/Panel/Panel.js';
import { ToastProvider } from '../../../components/Toast/Toast.js';
import { useAppStore } from '../../../stores/index.js';
import { useAuthStore } from '../../auth/authStore.js';
import { PreviewPlayer } from '../PreviewPlayer.js';
import { SpeakerList } from '../SpeakerList.js';

function jsonResponse(body: unknown): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } });
}

function speakerRow(id: string, name: string): Record<string, unknown> {
  return {
    id,
    projectId: 'prj_1',
    speakerKey: 'SPEAKER_00',
    displayName: name,
    segmentCount: 2,
    firstAppearanceMs: 1000,
    lastAppearanceMs: 5000,
    confidence: 0.97,
    assignedVoice: null,
  };
}

async function mockFetch(input: RequestInfo | URL): Promise<Response> {
  const url = typeof input === 'string' ? input : input instanceof URL ? input.href : input.url;
  if (url.includes('/available-voices')) {
    return jsonResponse({
      voices: [
        {
          voiceProfileId: 'voice_1',
          voiceId: 'stock-es-1',
          provider: 'acme',
          language: 'es',
          type: 'Stock',
          cloningEnabled: false,
          consentStatus: 'valid',
          isDefault: true,
        },
        {
          voiceProfileId: 'voice_2',
          voiceId: 'stock-es-2',
          provider: 'acme',
          language: 'es',
          type: 'Stock',
          cloningEnabled: false,
          consentStatus: 'valid',
          isDefault: false,
        },
      ],
    });
  }
  if (url.includes('/speakers')) {
    return jsonResponse({
      items: [speakerRow('spk_alice', 'Alice'), speakerRow('spk_bob', 'Bob')],
      page: 1,
      pageSize: 100,
      total: 2,
      hasMore: false,
    });
  }
  return jsonResponse({});
}

/** Renders and returns the container, so structure assertions can query into it. */
function renderWithProviders(node: React.ReactNode): { container: HTMLElement } {
  return render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <MemoryRouter>{node}</MemoryRouter>
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
}

beforeEach(() => {
  queryClient.clear();
  setTokenProvider(() => 'test-token');
  setInnerFetchForTests(mockFetch);
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('SpeakerList structure (Task 041C)', () => {
  it('does not claim listbox semantics it does not implement', async () => {
    renderWithProviders(<SpeakerList projectId="prj_1" selectedSpeakerId="spk_alice" />);
    await screen.findByTestId('voices-row-spk_alice');

    // A `listbox` promises arrow-key navigation and single-select options.
    // Neither existed, and each option wrapped a real button, which is what
    // `nested-interactive` fired on.
    expect(screen.queryByRole('listbox')).toBeNull();
    expect(screen.queryByRole('option')).toBeNull();

    // The scroll container is not a tab stop. Its contents already are, and
    // making the container focusable is what put an 8px sliver in the tab order.
    const scroll = screen.getByTestId('voices-list-scroll');
    expect(scroll.getAttribute('tabindex')).toBeNull();
    expect(scroll.getAttribute('role')).toBeNull();
  });

  it('exposes the selected speaker with aria-current, not a bare visual flag', async () => {
    renderWithProviders(<SpeakerList projectId="prj_1" selectedSpeakerId="spk_alice" />);
    await screen.findByTestId('voices-row-spk_alice');

    const alice = screen.getByTestId('voices-select-spk_alice');
    const bob = screen.getByTestId('voices-select-spk_bob');

    expect(alice.getAttribute('aria-current')).toBe('true');
    expect(bob.getAttribute('aria-current')).toBeNull();

    // Both rows are real buttons with accessible names, which is what a
    // listbox's options were pretending to be.
    expect(alice.tagName).toBe('BUTTON');
    expect(bob.getAttribute('aria-label')).toBe('Select speaker Bob');
  });

  it('keeps data-selected as a styling hook while the a11y signal is aria-current', async () => {
    renderWithProviders(<SpeakerList projectId="prj_1" selectedSpeakerId="spk_bob" />);
    await screen.findByTestId('voices-row-spk_bob');

    // The visual treatment and the existing suite read this attribute, so it
    // stays; it is simply no longer the accessible signal.
    expect(screen.getByTestId('voices-row-spk_bob').getAttribute('data-selected')).toBe('true');
    await waitFor(() => {
      expect(screen.getByTestId('voices-row-spk_alice').getAttribute('data-selected')).toBe('false');
    });
  });
});

describe('PreviewPlayer landmark naming (Task 041C)', () => {
  it('names its landmark with the voice id so repeated instances differ', () => {
    // `PreviewPlayer` uses `useMutation`, so it needs the QueryClientProvider
    // even though the assertion never fires a request.
    const { container } = renderWithProviders(
      <PreviewPlayer projectId="prj_1" speakerId="spk_1" voiceId="stock-es-1" />,
    );
    const section = container.querySelector('section[data-testid="voices-preview"]');
    expect(section).not.toBeNull();
    expect(section?.getAttribute('aria-label')).toBe('Voice preview: stock-es-1');
  });

  it('produces distinguishable labels for two voices in one list', () => {
    // The exact condition `landmark-unique` reported. Two instances, two names.
    const { container } = renderWithProviders(
      <>
        <PreviewPlayer projectId="prj_1" speakerId="spk_1" voiceId="stock-es-1" />
        <PreviewPlayer projectId="prj_1" speakerId="spk_1" voiceId="stock-es-2" />
      </>,
    );
    const labels = [...container.querySelectorAll('section[data-testid="voices-preview"]')].map(
      (node) => node.getAttribute('aria-label'),
    );
    expect(labels).toHaveLength(2);
    expect(new Set(labels).size, 'landmark names must be unique within a page').toBe(2);
  });
});

describe('heading levels (Task 041C)', () => {
  it('a Card title is an h2, so a page h1 is not followed by a skipped level', () => {
    // The dashboard rendered h1 -> h3 and axe reported `heading-order`. Card is
    // the primitive every dashboard card uses, so the fix belongs here.
    const { container } = render(
      <section>
        <h1>Dashboard</h1>
        <Card title="Projects">body</Card>
      </section>,
    );
    const title = container.querySelector('.dp-card-title');
    expect(title?.tagName).toBe('H2');

    const levels = [...container.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
      Number.parseInt(node.tagName.slice(1), 10),
    );
    expect(levels).toEqual([1, 2]);
  });

  it('a Panel title is an h3, one level below the card that contains it', () => {
    const { container } = render(
      <section>
        <h1>Dashboard</h1>
        <Card title="Projects">
          <Panel title="Recent">body</Panel>
        </Card>
      </section>,
    );
    const levels = [...container.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
      Number.parseInt(node.tagName.slice(1), 10),
    );
    expect(levels).toEqual([1, 2, 3]);
  });

  it('accepts an explicit level for a card nested inside another section', () => {
    // A hardcoded level is right until the component is used one level deeper.
    const { container } = render(
      <section>
        <h1>Page</h1>
        <Card title="Top">
          <Card title="Nested" titleLevel={3}>
            body
          </Card>
        </Card>
      </section>,
    );
    const levels = [...container.querySelectorAll('h1, h2, h3, h4, h5, h6')].map((node) =>
      Number.parseInt(node.tagName.slice(1), 10),
    );
    expect(levels).toEqual([1, 2, 3]);
  });
});
