import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { Modal } from './Modal.js';

afterEach(() => {
  cleanup();
});

describe('Modal', () => {
  it('traps focus and closes on Escape', () => {
    const onClose = vi.fn();
    const trigger = document.createElement('button');
    trigger.textContent = 'trigger';
    document.body.appendChild(trigger);
    trigger.focus();
    render(
      <Modal open title="Delete" onClose={onClose}>
        <button type="button">Confirm</button>
      </Modal>,
    );
    expect(screen.getByRole('dialog', { name: 'Delete' })).toBeDefined();
    fireEvent.keyDown(document, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledTimes(1);
    trigger.remove();
  });

  it('renders nothing when closed', () => {
    render(
      <Modal open={false} title="Hidden" onClose={() => {}}>
        body
      </Modal>,
    );
    expect(screen.queryByRole('dialog')).toBeNull();
  });
});
