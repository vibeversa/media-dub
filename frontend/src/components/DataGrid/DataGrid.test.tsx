import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { DataGrid } from './DataGrid.js';

afterEach(() => {
  cleanup();
});

describe('DataGrid', () => {
  it('navigates rows with keyboard and activates', () => {
    const onRowActivate = vi.fn();
    render(
      <DataGrid
        caption="Segments"
        getRowId={(r: { id: string }) => r.id}
        columns={[{ key: 'id', header: 'ID', render: (r: { id: string }) => r.id }]}
        rows={[{ id: 's1' }, { id: 's2' }]}
        onRowActivate={onRowActivate}
      />,
    );
    const grid = screen.getByRole('grid');
    fireEvent.keyDown(grid, { key: 'ArrowDown' });
    fireEvent.keyDown(grid, { key: 'Enter' });
    expect(onRowActivate).toHaveBeenCalledWith({ id: 's2' }, 1);
  });
});
