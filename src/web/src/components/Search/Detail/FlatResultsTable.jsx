import api from '../../../lib/api';
import { useInterfaceLanguage } from '../../../lib/interfaceLocale';
import { formatRegionalNumber, useRegionalLocale } from '../../../lib/regionalFormat';
import { getLocalStorageItem, setLocalStorageItem } from '../../../lib/storage';
import * as transfers from '../../../lib/transfers';
import {
  formatAttributes,
  formatBytes,
  formatSeconds,
  getDirectoryName,
  getFileName,
} from '../../../lib/util';
import { buildDownloadRequestFiles, buildSearchItemActionPath } from '../Response';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import { Link } from 'react-router-dom';
import { Button, Checkbox, Icon, Label, Table } from 'semantic-ui-react';

const SORT_STORAGE_KEY = 'slskd-search-flat-sort';
const ROW_HEIGHT = 34;
const OVERSCAN = 20;

const asArray = (value) => (Array.isArray(value) ? value : []);

// One row per file, across every response. Keeps a reference to the response so
// downloads can be grouped per peer again.
export const flattenResponses = (responses) =>
  asArray(responses).flatMap((response, responseIndex) => {
    const toRow = (file, fileIndex, locked) => {
      const lossless = Boolean(file.sampleRate) && Boolean(file.bitDepth);
      return {
        directory: getDirectoryName(file.filename),
        file,
        fileIndex,
        free: response.hasFreeUploadSlot ? -1 : (response.queueLength ?? 0),
        key: `${response.username}\u0000${file.filename}`,
        locked,
        name: getFileName(file.filename),
        // lossless always ranks above lossy; within each group by resolution/bitrate
        quality: lossless
          ? 100_000 + file.bitDepth * 1_000 + file.sampleRate / 1_000
          : (file.bitRate ?? 0),
        response,
        responseIndex,
        size: file.size ?? 0,
        speed: response.uploadSpeed ?? 0,
        username: response.username,
      };
    };

    return [
      ...asArray(response.files).map((file, index) => toRow(file, index, false)),
      ...asArray(response.lockedFiles).map((file, index) => toRow(file, index, true)),
    ];
  });

const columns = [
  { key: 'username', label: 'User', numeric: false },
  { key: 'free', label: 'Free', numeric: true },
  { key: 'speed', label: 'K/s', numeric: true },
  { key: 'directory', label: 'Folder', numeric: false },
  { key: 'name', label: 'File', numeric: false },
  { key: 'size', label: 'Size', numeric: true },
  { key: 'quality', label: 'Attributes', numeric: true },
];

const readStoredSort = () => {
  try {
    const stored = JSON.parse(getLocalStorageItem(SORT_STORAGE_KEY, 'null'));
    if (columns.some((column) => column.key === stored?.column)) return stored;
  } catch {
    // ignore malformed storage
  }

  return { column: 'size', direction: 'descending' };
};

export const sortRows = (rows, { column, direction }) => {
  const numeric = columns.find((c) => c.key === column)?.numeric;
  const sign = direction === 'ascending' ? 1 : -1;
  return [...rows].sort((a, b) => {
    const left = a[column];
    const right = b[column];
    const result = numeric ? left - right : String(left).localeCompare(String(right));
    return result * sign || a.key.localeCompare(b.key);
  });
};

const findScrollParent = (element) => {
  let node = element?.parentElement;
  while (node) {
    if (/(auto|scroll)/u.test(window.getComputedStyle(node).overflowY)) return node;
    node = node.parentElement;
  }

  return null;
};

// ponytail: fixed-height row windowing with spacer rows instead of react-window,
// which cannot render inside a <table>. Switch to measured heights if rows wrap.
const useRowWindow = (containerRef, count) => {
  const [viewport, setViewport] = useState({ first: 0, visible: 2 * OVERSCAN });

  useEffect(() => {
    const scroller = findScrollParent(containerRef.current);
    const update = () => {
      const body = containerRef.current?.querySelector('tbody');
      if (!body) return;
      const viewportTop = scroller ? scroller.getBoundingClientRect().top : 0;
      const viewportHeight = scroller ? scroller.clientHeight : window.innerHeight;
      const next = {
        first: Math.max(0, Math.floor((viewportTop - body.getBoundingClientRect().top) / ROW_HEIGHT)),
        visible: Math.ceil(viewportHeight / ROW_HEIGHT),
      };
      setViewport((current) =>
        current.first === next.first && current.visible === next.visible ? current : next,
      );
    };

    update();
    const target = scroller ?? window;
    target.addEventListener('scroll', update, { passive: true });
    window.addEventListener('resize', update);
    return () => {
      target.removeEventListener('scroll', update);
      window.removeEventListener('resize', update);
    };
  }, [containerRef]);

  return [
    Math.max(0, viewport.first - OVERSCAN),
    Math.min(count, viewport.first + viewport.visible + OVERSCAN),
  ];
};

const FlatResultsTable = ({ destination, disabled, rows, searchId, searchText }) => {
  const { t, languageFor, directionFor } = useInterfaceLanguage();
  const regionalLocale = useRegionalLocale();
  const localized = (source, parameters) => (
    <span dir={directionFor(source)} lang={languageFor(source)}>
      {t(source, parameters)}
    </span>
  );
  const number = (value) => formatRegionalNumber(value, undefined, regionalLocale);

  const [sort, setSort] = useState(readStoredSort);
  const [selected, setSelected] = useState(() => new Set());
  const [lastIndex, setLastIndex] = useState(null);
  const [download, setDownload] = useState({ status: 'idle' });
  const containerRef = useRef(null);

  const sortedRows = useMemo(() => sortRows(rows, sort), [rows, sort]);
  const [start, end] = useRowWindow(containerRef, sortedRows.length);
  const rowByKey = useMemo(() => new Map(rows.map((row) => [row.key, row])), [rows]);
  const selectedRows = [...selected].map((key) => rowByKey.get(key)).filter(Boolean);
  const selectedSize = selectedRows.reduce((total, row) => total + row.size, 0);
  const allSelected = sortedRows.length > 0 && sortedRows.every((row) => selected.has(row.key));

  const changeSort = (column) => {
    const next = sort.column === column
      ? { column, direction: sort.direction === 'ascending' ? 'descending' : 'ascending' }
      : { column, direction: columns.find((c) => c.key === column).numeric ? 'descending' : 'ascending' };
    setSort(next);
    setLocalStorageItem(SORT_STORAGE_KEY, JSON.stringify(next));
  };

  const toggleRows = (targetRows, checked) => {
    setSelected((current) => {
      const next = new Set(current);
      targetRows.forEach((row) => (checked ? next.add(row.key) : next.delete(row.key)));
      return next;
    });
    setDownload({ status: 'idle' });
  };

  const toggleRow = (event, index, checked) => {
    // React's change event does not expose shiftKey; the native click does
    const shift = event.nativeEvent?.shiftKey ?? event.shiftKey;
    const range = shift && lastIndex !== null && lastIndex !== index
      ? sortedRows.slice(Math.min(lastIndex, index), Math.max(lastIndex, index) + 1)
      : [sortedRows[index]];
    toggleRows(range, checked);
    setLastIndex(index);
  };

  const downloadSelected = async () => {
    setDownload({ status: 'inProgress' });
    try {
      const perUser = Map.groupBy(selectedRows, (row) => row.username);
      await Promise.all([...perUser.values()].map((userRows) => {
        const { response, responseIndex, username } = userRows[0];
        if (asArray(response.sourceProviders).length > 0 && searchId) {
          return Promise.all(userRows.map((row) => api.post(buildSearchItemActionPath(
            searchId,
            `${responseIndex}:${row.fileIndex}`,
            'download',
            destination,
          ))));
        }

        return transfers.download({
          destination,
          files: buildDownloadRequestFiles(userRows.map((row) => row.file), searchId, searchText),
          username,
        });
      }));
      setDownload({ status: 'complete' });
    } catch (error) {
      const detail = error.response?.data;
      setDownload({
        error: typeof detail === 'string' && detail ? detail : error.message,
        status: 'error',
      });
    }
  };

  return (
    <div className="search-flat-results" ref={containerRef}>
      <div className="search-list-selection-toolbar">
        <span className="search-list-selection-summary">
          {localized('Files selected: {count}', { count: number(selectedRows.length) })}
          {', '}
          {formatBytes(selectedSize)}
        </span>
        <Button
          aria-label={t('Download selected files')}
          dir={directionFor('Download selected files')}
          disabled={disabled || selectedRows.length === 0 || download.status === 'inProgress'}
          icon="download"
          lang={languageFor('Download selected files')}
          loading={download.status === 'inProgress'}
          content={t('Download')}
          onClick={downloadSelected}
          primary
        />
        {download.status === 'complete' && <Icon color="green" name="checkmark" size="large" />}
        {download.status === 'error' && (
          <Label basic color="red">
            {localized('Download failed')} {String(download.error ?? '')}
          </Label>
        )}
      </div>
      <Table className="search-flat-table" compact="very" selectable sortable unstackable>
        <Table.Header>
          <Table.Row>
            <Table.HeaderCell className="filelist-selector">
              <Checkbox
                aria-label={t('Select all files')}
                checked={allSelected}
                disabled={disabled || sortedRows.length === 0}
                fitted
                onChange={(_event, data) => toggleRows(sortedRows, data.checked)}
              />
            </Table.HeaderCell>
            {columns.map((column) => (
              <Table.HeaderCell
                aria-sort={sort.column === column.key ? sort.direction : undefined}
                className={`search-flat-${column.key}`}
                key={column.key}
                onClick={() => changeSort(column.key)}
                sorted={sort.column === column.key ? sort.direction : undefined}
              >
                {localized(column.label)}
              </Table.HeaderCell>
            ))}
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {start > 0 && (
            <tr aria-hidden className="search-flat-spacer">
              <td colSpan={columns.length + 1} style={{ height: start * ROW_HEIGHT }} />
            </tr>
          )}
          {sortedRows.slice(start, end).map((row, offset) => {
            const index = start + offset;
            const { file } = row;
            return (
              <Table.Row
                active={selected.has(row.key)}
                className={row.locked ? 'search-flat-locked' : undefined}
                key={row.key}
              >
                <Table.Cell className="filelist-selector">
                  <Checkbox
                    aria-label={t('Select {filename}', { filename: file.filename })}
                    checked={selected.has(row.key)}
                    disabled={disabled}
                    fitted
                    onChange={(event, data) => toggleRow(event, index, data.checked)}
                  />
                </Table.Cell>
                <Table.Cell className="search-flat-username">
                  <Link
                    state={{ user: row.username }}
                    title={t('Browse files')}
                    to={`/browse?user=${encodeURIComponent(row.username)}`}
                  >
                    {row.username}
                  </Link>
                </Table.Cell>
                <Table.Cell
                  className="search-flat-free"
                  title={t(row.free < 0 ? 'Free upload slot available' : 'No free upload slot')}
                >
                  {row.free < 0 ? localized('Yes') : number(row.free)}
                </Table.Cell>
                <Table.Cell className="search-flat-speed">
                  {row.speed > 0 ? number(Math.round(row.speed / 1_024)) : ''}
                </Table.Cell>
                <Table.Cell className="search-flat-directory" title={row.directory}>
                  {row.directory}
                </Table.Cell>
                <Table.Cell className="search-flat-name" title={file.filename}>
                  {row.locked && <Icon name="lock" title={t('Locked')} />}
                  {row.name}
                </Table.Cell>
                <Table.Cell className="search-flat-size" title={formatBytes(row.size)}>
                  {number(row.size)}
                </Table.Cell>
                <Table.Cell className="search-flat-quality">
                  {[formatAttributes(file), formatSeconds(file.length)].filter(Boolean).join(', ')}
                </Table.Cell>
              </Table.Row>
            );
          })}
          {end < sortedRows.length && (
            <tr aria-hidden className="search-flat-spacer">
              <td colSpan={columns.length + 1} style={{ height: (sortedRows.length - end) * ROW_HEIGHT }} />
            </tr>
          )}
        </Table.Body>
      </Table>
    </div>
  );
};

export default FlatResultsTable;
