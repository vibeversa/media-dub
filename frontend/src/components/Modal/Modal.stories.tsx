import type { Meta, StoryObj } from '@storybook/react';
import { Modal } from './Modal.js';

const meta: Meta<typeof Modal> = {
  title: 'Primitives/Modal',
  component: Modal,
  args: { open: true, title: 'Delete project', children: 'This cannot be undone.' },
};

export default meta;
type Story = StoryObj<typeof Modal>;

export const Open: Story = {};
export const Closed: Story = { args: { open: false } };
