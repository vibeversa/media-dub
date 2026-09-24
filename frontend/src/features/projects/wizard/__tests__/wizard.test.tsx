import { QueryClientProvider } from '@tanstack/react-query';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { RouterProvider, createMemoryRouter } from 'react-router-dom';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import '../../../../i18n/i18n.js';
import {
  clearTokenProvider,
  restoreInnerFetchForTests,
  setInnerFetchForTests,
  setTokenProvider,
} from '../../../../api/client/index.js';
import { LocaleProvider } from '../../../../app/providers/LocaleProvider.js';
import { queryClient } from '../../../../app/providers/queryClient.js';
import { ROUTER_FUTURE_FLAGS, ROUTER_PROVIDER_FUTURE_FLAGS } from '../../../../app/router.js';
import { ToastProvider } from '../../../../components/Toast/Toast.js';
import { useAppStore } from '../../../../stores/index.js';
import { useAuthStore } from '../../../auth/authStore.js';
import { resetRestoreStartedForTests } from '../../../auth/useSession.js';
import { CreateWizard } from '../CreateWizard.js';
import { WIZARD_STORAGE_KEY } from '../draft.js';
import { useWizardStore } from '../wizardStore.js';

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  });
}

const mockFetch = vi.fn<typeof fetch>();

let createStatus = 201;
let createBody: unknown = null;
let lastCreateBody: unknown = null;

function createdProject(): unknown {
  return {
    id: 'prj_new',
    name: 'Pilot episode',
    status: 'Created',
    sourceLanguage: 'en',
    targetLanguage: 'es',
    settingsVersion: 1,
    configHash: 'server-hash',
    createdAt: '2024-01-15T12:00:00Z',
    updatedAt: '2024-01-15T12:00:00Z',
  };
}

function urlOf(input: RequestInfo | URL): string {
  if (typeof input === 'string') {
    return input;
  }
  if (input instanceof URL) {
    return input.href;
  }
  return (input as Request).url;
}

function installMock(): void {
  mockFetch.mockImplementation(async (input, init) => {
    const url = urlOf(input as RequestInfo | URL);
    const request = input as Request;
    const method = typeof request.method === 'string' && request.method !== '' ? request.method : ((init as RequestInit | undefined)?.method ?? 'GET');
    if (url.includes('/api/v1/projects') && method === 'POST') {
      const raw = (init as RequestInit | undefined)?.body;
      lastCreateBody = typeof raw === 'string' ? (JSON.parse(raw) as unknown) : null;
      const body = createBody ?? createdProject();
      return jsonResponse(body, createStatus);
    }
    return jsonResponse({});
  });
}

function renderWizard(initialEntry = '/projects/new') {
  const router = createMemoryRouter(
    [
      { path: '/projects/new', element: <CreateWizard /> },
      { path: '/projects', element: <div data-testid="page-projects" /> },
      { path: '/projects/:id', element: <div data-testid="page-project-details" /> },
      { path: '/projects/:id/media', element: <div data-testid="page-project-media" /> },
    ],
    { initialEntries: [initialEntry], future: { ...ROUTER_FUTURE_FLAGS } },
  );
  render(
    <QueryClientProvider client={queryClient}>
      <LocaleProvider>
        <ToastProvider>
          <RouterProvider router={router} future={{ ...ROUTER_PROVIDER_FUTURE_FLAGS }} />
        </ToastProvider>
      </LocaleProvider>
    </QueryClientProvider>,
  );
  return router;
}

function authenticate(): void {
  setTokenProvider(() => 'test-token');
  useAuthStore.setState({ status: 'authenticated' });
  useAppStore.getState().setSession('authenticated', ['project.view', 'project.edit']);
}

async function goToReview(): Promise<void> {
  fireEvent.change(screen.getByTestId('wizard-name'), { target: { value: 'Pilot episode' } });
  fireEvent.click(screen.getByTestId('wizard-next'));
  expect(await screen.findByTestId('wizard-step-language')).toBeDefined();
  fireEvent.click(screen.getByTestId('wizard-next'));
  expect(await screen.findByTestId('wizard-step-settings')).toBeDefined();
  fireEvent.click(screen.getByTestId('wizard-next'));
  expect(await screen.findByTestId('wizard-step-upload')).toBeDefined();
  fireEvent.click(screen.getByTestId('wizard-next'));
  expect(await screen.findByTestId('wizard-step-review')).toBeDefined();
}

beforeEach(() => {
  mockFetch.mockReset();
  setInnerFetchForTests(mockFetch);
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  window.localStorage.clear();
  useWizardStore.getState().resetWizardForTests();
  createStatus = 201;
  createBody = null;
  lastCreateBody = null;
  installMock();
});

afterEach(() => {
  cleanup();
  restoreInnerFetchForTests();
  clearTokenProvider();
  useAuthStore.getState().resetForTests();
  queryClient.clear();
  useAppStore.getState().resetForTests();
  resetRestoreStartedForTests();
  window.localStorage.clear();
  useWizardStore.getState().resetWizardForTests();
  vi.restoreAllMocks();
});

describe('step flow', () => {
  it('walks basics to review with validated forward motion', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();

    fireEvent.change(screen.getByTestId('wizard-name'), { target: { value: 'Pilot episode' } });
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-step-language')).toBeDefined();

    fireEvent.click(screen.getByTestId('wizard-back'));
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    expect((screen.getByTestId('wizard-name') as HTMLInputElement).value).toBe('Pilot episode');
  });

  it('blocks Next on an empty name with an inline error (R1)', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-field-errors')).toBeDefined();
    expect(screen.queryByTestId('wizard-step-language')).toBeNull();
  });

  it('blocks mismatched languages on the language step', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    fireEvent.change(screen.getByTestId('wizard-name'), { target: { value: 'Pilot' } });
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-step-language')).toBeDefined();
    fireEvent.change(screen.getByTestId('wizard-target'), { target: { value: 'en' } });
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-field-errors')).toBeDefined();
    expect(screen.queryByTestId('wizard-step-settings')).toBeNull();
  });
});

describe('immutable language notices (R2)', () => {
  it('warns on the language step and repeats the notice on review', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    fireEvent.change(screen.getByTestId('wizard-name'), { target: { value: 'Pilot' } });
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-immutable-notice')).toBeDefined();

    await goToReviewFromLanguage();
    expect(await screen.findByTestId('wizard-immutable-repeat')).toBeDefined();
    expect(screen.getByTestId('wizard-immutable-repeat').textContent).toContain('es');
  });

  async function goToReviewFromLanguage(): Promise<void> {
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-step-settings')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-step-upload')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-step-review')).toBeDefined();
  }
});

describe('review transparency (R4)', () => {
  it('shows version, hash preview, and the human summary', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    await goToReview();
    expect(screen.getByTestId('wizard-config-version').textContent).toContain('1');
    const hashText = screen.getByTestId('wizard-config-hash').textContent ?? '';
    expect(hashText).toMatch(/[0-9a-f]{8}/);
    expect(screen.getByTestId('wizard-hash-note')).toBeDefined();
    expect(screen.getByTestId('wizard-summary-name').textContent).toContain('Pilot episode');
    expect(screen.getByTestId('wizard-summary-target').textContent).toContain('es');
  });
});

describe('start submission', () => {
  it('creates with upload-later and opens the media tab', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    await goToReview();
    fireEvent.click(screen.getByTestId('wizard-start'));
    expect(await screen.findByTestId('page-project-media')).toBeDefined();
    expect(window.localStorage.getItem(WIZARD_STORAGE_KEY)).toBeNull();
    const wire = lastCreateBody as Record<string, unknown>;
    expect(wire['name']).toBe('Pilot episode');
    expect(wire['sourceLanguage']).toBe('en');
    expect(wire['targetLanguage']).toBe('es');
    expect(wire['processingSettings']).toMatchObject({ schemaVersion: 1 });
  });

  it('maps server 400 onto fields with the draft intact (R1)', async () => {
    authenticate();
    createStatus = 400;
    createBody = {
      error: {
        code: 'VALIDATION_FAILED',
        message: 'Name must be 1..200 chars.',
        correlationId: 'corr-create',
        details: {},
      },
    };
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    await goToReview();
    fireEvent.click(screen.getByTestId('wizard-start'));
    expect(await screen.findByTestId('wizard-server-fields')).toBeDefined();
    expect(screen.getByTestId('wizard-server-field-name')).toBeDefined();
    expect(window.localStorage.getItem(WIZARD_STORAGE_KEY)).not.toBeNull();
    fireEvent.click(screen.getByTestId('wizard-goto-basics'));
    expect((screen.getByTestId('wizard-name') as HTMLInputElement).value).toBe('Pilot episode');
  });

  it('requires a file for upload-now before starting', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    fireEvent.change(screen.getByTestId('wizard-name'), { target: { value: 'Pilot' } });
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-step-language')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-goto-upload'));
    expect(await screen.findByTestId('wizard-step-upload')).toBeDefined();
    fireEvent.click(screen.getByTestId('wizard-upload-now'));
    fireEvent.click(screen.getByTestId('wizard-next'));
    expect(await screen.findByTestId('wizard-field-errors')).toBeDefined();
    expect(screen.queryByTestId('wizard-step-review')).toBeNull();
  });
});

describe('discard', () => {
  it('clears the draft behind confirmation and returns to the list', async () => {
    authenticate();
    renderWizard();
    expect(await screen.findByTestId('wizard-step-basics')).toBeDefined();
    fireEvent.change(screen.getByTestId('wizard-name'), { target: { value: 'temp' } });
    expect(window.localStorage.getItem(WIZARD_STORAGE_KEY)).toContain('temp');
    fireEvent.click(screen.getByTestId('wizard-discard'));
    fireEvent.click(screen.getByRole('button', { name: 'Discard draft' }));
    expect(await screen.findByTestId('page-projects')).toBeDefined();
    expect(window.localStorage.getItem(WIZARD_STORAGE_KEY)).toBeNull();
  });
});
