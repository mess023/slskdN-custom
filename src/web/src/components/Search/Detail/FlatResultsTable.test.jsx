import FlatResultsTable, { flattenResponses, sortRows } from './FlatResultsTable';
import api from '../../../lib/api';
import * as transfers from '../../../lib/transfers';
import { formatAttributes, formatSeconds } from '../../../lib/util';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import React from 'react';
import { MemoryRouter } from 'react-router-dom';

vi.mock('../../../lib/api', () => ({ default: { post: vi.fn(async () => ({})) } }));
vi.mock('../../../lib/transfers', () => ({ download: vi.fn(async () => ({})) }));

const responses = [
  {
    files: [
      { bitRate: 320, filename: 'C:\\music\\album\\small.mp3', length: 278, size: 100 },
      { bitDepth: 16, filename: 'C:\\music\\album\\big.flac', length: 278, sampleRate: 44_100, size: 300 },
    ],
    hasFreeUploadSlot: true,
    lockedFiles: [],
    queueLength: 0,
    uploadSpeed: 2_048,
    username: 'alice',
  },
  {
    files: [{ bitRate: 192, filename: '/share/medium.mp3', size: 200 }],
    hasFreeUploadSlot: false,
    lockedFiles: [{ filename: '/share/locked.mp3', size: 50 }],
    queueLength: 7,
    uploadSpeed: 0,
    username: 'bob',
  },
];

const renderTable = (props = {}) =>
  render(
    <MemoryRouter>
      <FlatResultsTable
        destination="/downloads"
        rows={flattenResponses(responses)}
        searchId="search-1"
        searchText="album"
        {...props}
      />
    </MemoryRouter>,
  );

const bodyRows = () => screen.getAllByRole('row').slice(1).filter((row) => !row.classList.contains('search-flat-spacer'));
const fileNames = () => bodyRows().map((row) => within(row).getAllByRole('cell')[5].textContent);

describe('FlatResultsTable', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    localStorage.clear();
  });

  it('flattens every file and locked file into one row each', () => {
    const rows = flattenResponses(responses);
    expect(rows).toHaveLength(4);
    expect(rows.filter((row) => row.locked)).toHaveLength(1);
    expect(rows[0]).toMatchObject({ directory: 'C:\\music\\album', free: -1, name: 'small.mp3', username: 'alice' });
    expect(rows[2]).toMatchObject({ free: 7, name: 'medium.mp3', username: 'bob' });
  });

  it('sorts by size descending by default and flips when the header is clicked', () => {
    renderTable();
    expect(fileNames()).toEqual(['big.flac', 'medium.mp3', 'small.mp3', 'locked.mp3']);
    expect(screen.getByRole('columnheader', { name: 'Size' })).toHaveAttribute('aria-sort', 'descending');

    fireEvent.click(screen.getByRole('columnheader', { name: 'Size' }));
    expect(fileNames()).toEqual(['locked.mp3', 'small.mp3', 'medium.mp3', 'big.flac']);
    expect(JSON.parse(localStorage.getItem('slskd-search-flat-sort'))).toEqual({ column: 'size', direction: 'ascending' });

    fireEvent.click(screen.getByRole('columnheader', { name: 'User' }));
    expect(fileNames()).toEqual(['big.flac', 'small.mp3', 'locked.mp3', 'medium.mp3']);
  });

  it('ranks lossless above lossy when sorting by attributes', () => {
    const rows = flattenResponses(responses);
    expect(sortRows(rows, { column: 'quality', direction: 'descending' })[0].name).toBe('big.flac');
  });

  it('shows SoulseekQt style columns: free slot, speed in K/s, exact bytes and attributes', () => {
    renderTable();
    const [big, medium] = bodyRows();
    const cells = (row) => within(row).getAllByRole('cell').map((cell) => cell.textContent);
    expect(cells(big).slice(1)).toEqual(['alice', 'Yes', '2', 'C:\\music\\album', 'big.flac', '300', `${formatAttributes(responses[0].files[1])}, ${formatSeconds(278)}`]);
    expect(cells(medium).slice(1)).toEqual(['bob', '7', '', '/share', 'medium.mp3', '200', '192 Kbps']);
    expect(within(big).getAllByRole('cell')[6]).toHaveAttribute('title', '300 B');
  });

  it('downloads the selection grouped per peer', async () => {
    renderTable();
    fireEvent.click(screen.getByLabelText('Select C:\\music\\album\\big.flac'));
    fireEvent.click(screen.getByLabelText('Select /share/medium.mp3'));
    fireEvent.click(screen.getByLabelText('Select /share/locked.mp3'));
    expect(screen.getByText('Files selected: 3')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Download selected files' }));

    await waitFor(() => expect(transfers.download).toHaveBeenCalledTimes(2));
    expect(transfers.download).toHaveBeenCalledWith(expect.objectContaining({
      destination: '/downloads',
      files: [expect.objectContaining({ filename: 'C:\\music\\album\\big.flac', searchId: 'search-1' })],
      username: 'alice',
    }));
    expect(transfers.download).toHaveBeenCalledWith(expect.objectContaining({
      files: [
        expect.objectContaining({ filename: '/share/medium.mp3' }),
        expect.objectContaining({ filename: '/share/locked.mp3' }),
      ],
      username: 'bob',
    }));
    expect(api.post).not.toHaveBeenCalled();
  });

  it('routes bridged results through the search item action endpoint', async () => {
    const bridged = [{ ...responses[1], sourceProviders: ['pod'] }];
    renderTable({ rows: flattenResponses(bridged) });
    fireEvent.click(screen.getByLabelText('Select all files'));
    fireEvent.click(screen.getByRole('button', { name: 'Download selected files' }));

    await waitFor(() => expect(api.post).toHaveBeenCalledTimes(2));
    expect(api.post).toHaveBeenCalledWith('/searches/search-1/items/0%3A0/download?destination=%2Fdownloads');
    expect(transfers.download).not.toHaveBeenCalled();
  });

  it('selects a range with shift-click', () => {
    renderTable();
    fireEvent.click(screen.getByLabelText('Select C:\\music\\album\\big.flac'));
    fireEvent.click(screen.getByLabelText('Select C:\\music\\album\\small.mp3'), { shiftKey: true });
    expect(screen.getByText('Files selected: 3')).toBeInTheDocument();
  });
});
