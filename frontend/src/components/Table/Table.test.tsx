import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { Table } from './Table.js';

afterEach(() => {
  cleanup();
});

describe('Table', () => {
  it('renders rows and headers', () => {
    render(
      <Table
        caption="Voices"
        getRowId={(r: { id: string }) => r.id}
        columns={[{ key: 'id', header: 'ID', render: (r: { id: string }) => r.id }]}
        rows={[{ id: 'v1' }, { id: 'v2' }]}
      />,
    );
    expect(screen.getByRole('columnheader', { name: 'ID' })).toBeDefined();
    expect(screen.getByRole('cell', { name: 'v1' })).toBeDefined();
  });
});
