import type { Meta, StoryObj } from '@storybook/react';
import { ToastProvider } from './Toast.js';
import { useToast } from './useToast.js';

function Demo(): React.JSX.Element {
  const { push } = useToast();
  return (
    <button
      type="button"
      onClick={() => {
        push('success', 'Saved');
      }}
    >
      Push toast
    </button>
  );
}

const meta: Meta = {
  title: 'Primitives/Toast',
  render: () => (
    <ToastProvider>
      <Demo />
    </ToastProvider>
  ),
};

export default meta;
type Story = StoryObj;

export const Default: Story = {};
