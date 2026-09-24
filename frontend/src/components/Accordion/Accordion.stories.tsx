import type { Meta, StoryObj } from '@storybook/react';
import { Accordion } from './Accordion.js';

const meta: Meta<typeof Accordion> = {
  title: 'Primitives/Accordion',
  component: Accordion,
  args: {
    items: [
      { id: 'a', title: 'First', content: 'First body' },
      { id: 'b', title: 'Second', content: 'Second body' },
    ],
  },
};

export default meta;
type Story = StoryObj<typeof Accordion>;

export const Default: Story = {};
