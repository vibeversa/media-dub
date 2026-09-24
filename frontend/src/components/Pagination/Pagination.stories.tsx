import type { Meta, StoryObj } from '@storybook/react';
import { Pagination } from './Pagination.js';

const meta: Meta<typeof Pagination> = {
  title: 'Primitives/Pagination',
  component: Pagination,
  args: { page: 1, pageSize: 20, total: 60 },
};

export default meta;
type Story = StoryObj<typeof Pagination>;

export const First: Story = {};
export const Last: Story = { args: { page: 3 } };
