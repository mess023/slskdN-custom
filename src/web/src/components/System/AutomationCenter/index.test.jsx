// <copyright file="index.test.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import AutomationCenter from './index';
import * as automationsApi from '../../../lib/automations';
import { resetInterfaceLanguage, setInterfaceLanguage } from '../../../lib/interfaceLocale';
import * as optionsApi from '../../../lib/options';
import { stubLocaleCatalogFetch } from '../../../test/localeCatalogFetchMock';
import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import React from 'react';
import { toast } from 'react-toastify';
import * as YAML from 'yaml';

vi.mock('react-toastify', () => ({
  toast: { error: vi.fn(), info: vi.fn() },
}));

vi.mock('../../../lib/automations', () => ({
  createAutomation: vi.fn(),
  deleteAutomation: vi.fn(),
  getAllRuns: vi.fn(),
  getAutomations: vi.fn(),
  getTemplates: vi.fn(),
  runAutomation: vi.fn(),
  updateAutomation: vi.fn(),
}));

vi.mock('../../../lib/options', () => ({
  getYaml: vi.fn(),
  updateYaml: vi.fn(),
}));

const templates = [
  {
    approvalGate: 'Download approval',
    defaultIntervalMinutes: 120,
    description: 'Retry wishlist searches.',
    fileImpact: 'Downloads after approval',
    icon: 'heart',
    linkedOptions: [
      { default: true, description: 'Run wishlist searches in the background.', label: 'Wishlist enabled', min: null, type: 'bool', yamlPath: 'wishlist.enabled' },
      { default: 3600, description: 'Seconds between background wishlist searches.', label: 'Wishlist interval (seconds)', min: 300, type: 'int', yamlPath: 'wishlist.interval_seconds' },
    ],
    networkImpact: 'Public peers possible',
    parameterSchema: [
      { default: 3, description: 'Max searches per run', key: 'limit', label: 'Limit', max: 50, min: 1, required: false, type: 'int' },
      { default: false, description: 'Include disabled', key: 'includeDisabled', label: 'Include disabled', required: false, type: 'bool' },
    ],
    title: 'Wishlist Retry',
    type: 'wishlist-retry',
  },
  {
    approvalGate: 'None',
    defaultIntervalMinutes: 0,
    description: 'Scan a library path.',
    fileImpact: 'Read-only',
    icon: 'stethoscope',
    linkedOptions: [],
    networkImpact: 'Local',
    parameterSchema: [
      { default: null, description: 'Path to scan', key: 'libraryPath', label: 'Library path', required: true, type: 'path' },
    ],
    title: 'Library Health Scan',
    type: 'library-health-scan',
  },
];

const wishlistAutomation = {
  createdAt: '2026-11-01T10:00:00Z',
  enabled: true,
  id: 'auto-1',
  intervalMinutes: 120,
  lastRunAt: '2026-11-06T10:00:00Z',
  lastRunStatus: 'succeeded',
  lastRunSummary: 'Ran 2 of 5 wishlist searches',
  name: 'Nightly wishlist',
  nextRunAt: '2026-11-07T10:00:00Z',
  parameters: { includeDisabled: false, limit: 3 },
  type: 'wishlist-retry',
  updatedAt: '2026-11-01T10:00:00Z',
};

const runs = [
  {
    automationId: 'auto-1',
    automationName: 'Nightly wishlist',
    details: { items: [] },
    dryRun: false,
    finishedAt: new Date().toISOString(),
    id: 'run-1',
    startedAt: new Date().toISOString(),
    status: 'failed',
    summary: 'Soulseek disconnected',
    type: 'wishlist-retry',
  },
  {
    automationId: 'auto-1',
    automationName: 'Nightly wishlist',
    details: {},
    dryRun: true,
    finishedAt: '2026-11-05T10:00:00Z',
    id: 'run-2',
    startedAt: '2026-11-05T10:00:00Z',
    status: 'succeeded',
    summary: 'Would run 3 of 5 wishlist searches',
    type: 'wishlist-retry',
  },
];

const yaml = 'wishlist:\n  enabled: false\n  interval_seconds: 3600\n';

// The initial load settles inside act: Semantic's event callbacks are set in effects under NODE_ENV=test,
// so a click on freshly mounted cards before those effects ran throws "Cannot call an event handler while rendering".
const renderCenter = async (options = { remoteConfiguration: true }) => {
  await act(async () => {
    render(<AutomationCenter options={options} />);
  });
};

const waitForLoad = async () => {
  await waitFor(() => expect(screen.getByRole('button', { name: 'Edit Nightly wishlist' })).toBeInTheDocument());
};

const cardOf = (name) => screen.getByRole('button', { name: `Edit ${name}` }).closest('.card');

const enabledLinkedInterval = async () => {
  const interval = await screen.findByLabelText('Wishlist interval (seconds)');
  await waitFor(() => expect(interval).toBeEnabled());
  return interval;
};

describe('AutomationCenter', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    automationsApi.getTemplates.mockResolvedValue(templates);
    automationsApi.getAutomations.mockResolvedValue([wishlistAutomation]);
    automationsApi.getAllRuns.mockResolvedValue(runs);
    automationsApi.createAutomation.mockImplementation(async (request) => ({ id: 'auto-2', ...request }));
    automationsApi.updateAutomation.mockImplementation(async (id, request) => ({ ...wishlistAutomation, ...request, id }));
    automationsApi.deleteAutomation.mockResolvedValue(undefined);
    optionsApi.getYaml.mockResolvedValue(yaml);
    optionsApi.updateYaml.mockResolvedValue({});
    Object.assign(navigator, { clipboard: { writeText: vi.fn().mockResolvedValue(undefined) } });
    vi.spyOn(window, 'confirm').mockReturnValue(true);
  });

  afterEach(async () => {
    await resetInterfaceLanguage();
    vi.unstubAllGlobals();
  });

  it('renders automations with template info and summary statistics', async () => {
    await renderCenter();
    await waitForLoad();

    expect(automationsApi.getTemplates).toHaveBeenCalled();
    expect(automationsApi.getAutomations).toHaveBeenCalled();
    expect(automationsApi.getAllRuns).toHaveBeenCalledWith(50);
    expect(screen.getAllByText('Wishlist Retry').length).toBeGreaterThan(0);
    expect(screen.getByText('Every 120 min')).toBeInTheDocument();
    expect(screen.getByText('Ran 2 of 5 wishlist searches')).toBeInTheDocument();
    expect(screen.getByText('Failed (24h)').previousSibling).toHaveTextContent('1');
    expect(screen.getByText('Enabled').previousSibling).toHaveTextContent('1');
  });

  it('shows an empty state with a New automation button when nothing exists', async () => {
    automationsApi.getAutomations.mockResolvedValue([]);
    await renderCenter();

    await waitFor(() => expect(screen.getByText('No automations yet')).toBeInTheDocument());
    expect(screen.getAllByRole('button', { name: /New automation/ }).length).toBe(2);
  });

  it('creates an automation from a template with coerced parameters', async () => {
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getAllByRole('button', { name: /New automation/ })[0]);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByLabelText('Wishlist Retry'));

    expect(within(dialog).getByLabelText('Name')).toHaveValue('Wishlist Retry');
    expect(within(dialog).getByLabelText('Interval (minutes)')).toHaveValue(120);
    expect(within(dialog).getByLabelText('Limit')).toHaveValue(3);

    fireEvent.change(within(dialog).getByLabelText('Name'), { target: { value: 'My retry' } });
    fireEvent.change(within(dialog).getByLabelText('Interval (minutes)'), { target: { value: '30' } });
    fireEvent.change(within(dialog).getByLabelText('Limit'), { target: { value: '5' } });
    fireEvent.click(within(dialog).getByLabelText('Include disabled'));
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(automationsApi.createAutomation).toHaveBeenCalledWith({
      enabled: true,
      intervalMinutes: 30,
      name: 'My retry',
      parameters: { includeDisabled: true, limit: 5 },
      type: 'wishlist-retry',
    }));
    await waitFor(() => expect(automationsApi.getAutomations).toHaveBeenCalledTimes(2));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });

  it('blocks creation while a required parameter is empty', async () => {
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getAllByRole('button', { name: /New automation/ })[0]);
    const dialog = await screen.findByRole('dialog');
    fireEvent.click(within(dialog).getByLabelText('Library Health Scan'));

    expect(within(dialog).getByRole('button', { name: 'Create' })).toBeDisabled();
    fireEvent.change(within(dialog).getByLabelText('Library path'), { target: { value: '/music' } });
    expect(within(dialog).getByRole('button', { name: 'Create' })).toBeEnabled();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Create' }));

    await waitFor(() => expect(automationsApi.createAutomation).toHaveBeenCalledWith(
      expect.objectContaining({ intervalMinutes: 0, parameters: { libraryPath: '/music' }, type: 'library-health-scan' }),
    ));
  });

  it('edits an existing automation through PUT', async () => {
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByRole('button', { name: 'Edit Nightly wishlist' }));
    const dialog = await screen.findByRole('dialog');
    expect(within(dialog).getByLabelText('Name')).toHaveValue('Nightly wishlist');
    fireEvent.change(within(dialog).getByLabelText('Limit'), { target: { value: '7' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    await waitFor(() => expect(automationsApi.updateAutomation).toHaveBeenCalledWith('auto-1', {
      enabled: true,
      intervalMinutes: 120,
      name: 'Nightly wishlist',
      parameters: { includeDisabled: false, limit: 7 },
      type: 'wishlist-retry',
    }));
  });

  it('runs a dry run and shows the returned summary and item details inline', async () => {
    automationsApi.runAutomation.mockResolvedValue({
      automationId: 'auto-1',
      details: { items: [{ name: 'Public Domain Suite' }, 'Archive Concert'] },
      dryRun: true,
      id: 'run-3',
      startedAt: '2026-11-07T01:00:00Z',
      status: 'succeeded',
      summary: 'Would run 2 of 5 wishlist searches',
    });
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByRole('button', { name: 'Dry run Nightly wishlist' }));

    await waitFor(() => expect(automationsApi.runAutomation).toHaveBeenCalledWith('auto-1', { dryRun: true }));
    expect(await screen.findByText('Would run 2 of 5 wishlist searches')).toBeInTheDocument();
    expect(screen.getByText('Public Domain Suite')).toBeInTheDocument();
    expect(screen.getByText('Archive Concert')).toBeInTheDocument();
    expect(toast.info).toHaveBeenCalledWith('Nightly wishlist: Would run 2 of 5 wishlist searches');
    // History plus the 24h failure window, on load and again after the run.
    await waitFor(() => expect(automationsApi.getAllRuns).toHaveBeenCalledTimes(4));
  });

  it('runs now without the dry run flag', async () => {
    automationsApi.runAutomation.mockResolvedValue({ status: 'failed', summary: 'Soulseek disconnected' });
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByRole('button', { name: 'Run Nightly wishlist now' }));

    await waitFor(() => expect(automationsApi.runAutomation).toHaveBeenCalledWith('auto-1', { dryRun: false }));
    expect(toast.error).toHaveBeenCalledWith('Nightly wishlist: Soulseek disconnected');
  });

  it('toggles enabled through PUT', async () => {
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByLabelText('Disable Nightly wishlist'));

    await waitFor(() => expect(automationsApi.updateAutomation).toHaveBeenCalledWith('auto-1', {
      enabled: false,
      intervalMinutes: 120,
      name: 'Nightly wishlist',
      parameters: { includeDisabled: false, limit: 3 },
      type: 'wishlist-retry',
    }));
    expect(await screen.findByLabelText('Enable Nightly wishlist')).toBeInTheDocument();
  });

  it('deletes after confirmation and skips when cancelled', async () => {
    window.confirm.mockReturnValueOnce(false);
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByRole('button', { name: 'Delete Nightly wishlist' }));
    expect(automationsApi.deleteAutomation).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'Delete Nightly wishlist' }));
    await waitFor(() => expect(automationsApi.deleteAutomation).toHaveBeenCalledWith('auto-1'));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Edit Nightly wishlist' })).not.toBeInTheDocument());
  });

  it('renders run history rows and copies them as text', async () => {
    await renderCenter();
    await waitForLoad();

    expect(screen.getByText('Soulseek disconnected')).toBeInTheDocument();
    expect(screen.getByText('Would run 3 of 5 wishlist searches')).toBeInTheDocument();
    expect(screen.getByText('dry run')).toBeInTheDocument();

    fireEvent.click(screen.getByRole('button', { name: 'Copy automation run history' }));

    await waitFor(() => expect(navigator.clipboard.writeText).toHaveBeenCalledWith(
      expect.stringContaining('Nightly wishlist\twishlist-retry\tdry run\tsucceeded\tWould run 3 of 5 wishlist searches'),
    ));
    expect(screen.getByText('Run history copied.')).toBeInTheDocument();
  });

  it('saves linked slskd.yml settings through setIn + updateYaml', async () => {
    await renderCenter();
    await waitForLoad();

    const enabledToggle = await screen.findByLabelText('Wishlist enabled');
    await waitFor(() => expect(enabledToggle).toBeEnabled());
    expect(screen.getByLabelText('Wishlist interval (seconds)')).toHaveValue(3600);

    fireEvent.click(enabledToggle);
    fireEvent.change(screen.getByLabelText('Wishlist interval (seconds)'), { target: { value: '600' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save slskd.yml settings' }));

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    const saved = YAML.parseDocument(optionsApi.updateYaml.mock.calls[0][0].yaml);
    expect(saved.getIn(['wishlist', 'enabled'])).toBe(true);
    expect(saved.getIn(['wishlist', 'interval_seconds'])).toBe(600);
    expect(await screen.findByText('slskd.yml updated.')).toBeInTheDocument();
  });

  it('shows linked option defaults when the yaml section is absent', async () => {
    optionsApi.getYaml.mockResolvedValue('web:\n  authentication: {}\n');
    await renderCenter();
    await waitForLoad();

    const enabledToggle = await screen.findByLabelText('Wishlist enabled');
    await waitFor(() => expect(enabledToggle).toBeEnabled());
    expect(enabledToggle).toBeChecked();
    expect(screen.getByLabelText('Wishlist interval (seconds)')).toHaveValue(3600);
  });

  it('writes only the linked keys the user changed', async () => {
    optionsApi.getYaml.mockResolvedValue('web:\n  authentication: {}\n');
    await renderCenter();
    await waitForLoad();

    const enabledToggle = await screen.findByLabelText('Wishlist enabled');
    await waitFor(() => expect(enabledToggle).toBeEnabled());
    fireEvent.change(screen.getByLabelText('Wishlist interval (seconds)'), { target: { value: '600' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save slskd.yml settings' }));

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    const saved = YAML.parseDocument(optionsApi.updateYaml.mock.calls[0][0].yaml);
    expect(saved.getIn(['wishlist', 'interval_seconds'])).toBe(600);
    expect(saved.hasIn(['wishlist', 'enabled'])).toBe(false);
    expect(saved.toJS().web).toEqual({ authentication: {} });
  });

  it('removes a linked int key when the field is cleared', async () => {
    await renderCenter();
    await waitForLoad();

    const interval = await screen.findByLabelText('Wishlist interval (seconds)');
    await waitFor(() => expect(interval).toBeEnabled());
    expect(interval).toHaveValue(3600);
    fireEvent.change(interval, { target: { value: '' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save slskd.yml settings' }));

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    const saved = YAML.parseDocument(optionsApi.updateYaml.mock.calls[0][0].yaml);
    expect(saved.hasIn(['wishlist', 'interval_seconds'])).toBe(false);
    expect(saved.getIn(['wishlist', 'enabled'])).toBe(false);
  });

  it('reports a failed run history refresh instead of rejecting', async () => {
    await renderCenter();
    await waitForLoad();

    automationsApi.getAllRuns.mockRejectedValueOnce(new Error('runs down'));
    fireEvent.click(screen.getByRole('button', { name: 'Refresh run history' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith(expect.stringMatching(/runs down|Failed to load run history/)));
    expect(screen.getByRole('button', { name: 'Edit Nightly wishlist' })).toBeInTheDocument();
  });

  it('disables linked yaml controls when remote configuration is off but keeps automation actions', async () => {
    await renderCenter({ remoteConfiguration: false });
    await waitForLoad();

    expect(screen.getByText(/Remote configuration is disabled/)).toBeInTheDocument();
    expect(screen.getByLabelText('Wishlist enabled')).toBeDisabled();
    expect(screen.getByLabelText('Wishlist interval (seconds)')).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Save slskd.yml settings' })).toBeDisabled();
    expect(screen.getByRole('button', { name: 'Dry run Nightly wishlist' })).toBeEnabled();
    expect(screen.getByRole('button', { name: 'Run Nightly wishlist now' })).toBeEnabled();
  });

  it('shows a load error when the API fails', async () => {
    automationsApi.getTemplates.mockRejectedValue(new Error('boom'));
    await renderCenter();

    expect(await screen.findByText(/Failed to load automations|boom/)).toBeInTheDocument();
  });

  it('explains a 403 as a missing Administrator role', async () => {
    automationsApi.getTemplates.mockRejectedValue({ response: { data: '', status: 403 } });
    await renderCenter();

    expect(await screen.findByText('Administrator role required.')).toBeInTheDocument();
  });

  it('shows the role text when a run is forbidden', async () => {
    automationsApi.runAutomation.mockRejectedValue({ response: { data: '', status: 403 } });
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByRole('button', { name: 'Run Nightly wishlist now' }));

    await waitFor(() => expect(toast.error).toHaveBeenCalledWith('Administrator role required.'));
  });

  it('keeps each card busy only while its own request is in flight', async () => {
    const scan = {
      ...wishlistAutomation,
      id: 'auto-2',
      lastRunAt: null,
      lastRunStatus: null,
      lastRunSummary: null,
      name: 'Music scan',
      parameters: { libraryPath: '/music' },
      type: 'library-health-scan',
    };
    automationsApi.getAutomations.mockResolvedValue([wishlistAutomation, scan]);
    const pending = {};
    automationsApi.runAutomation.mockImplementation((id) => new Promise((resolve) => {
      pending[id] = resolve;
    }));
    await renderCenter();
    await waitForLoad();
    const runWishlist = () => screen.getByRole('button', { name: 'Run Nightly wishlist now' });
    const dryRunScan = () => screen.getByRole('button', { name: 'Dry run Music scan' });

    fireEvent.click(runWishlist());
    await waitFor(() => expect(runWishlist()).toBeDisabled());
    fireEvent.click(dryRunScan());
    await waitFor(() => expect(dryRunScan()).toBeDisabled());
    expect(runWishlist()).toBeDisabled();

    await act(async () => pending['auto-1']({ status: 'succeeded', summary: 'Ran 3 of 5 wishlist searches' }));
    await waitFor(() => expect(runWishlist()).toBeEnabled());
    expect(dryRunScan()).toBeDisabled();

    await act(async () => pending['auto-2']({ dryRun: true, status: 'succeeded', summary: 'Would scan /music' }));
    await waitFor(() => expect(dryRunScan()).toBeEnabled());
  });

  it('shows a local dry run only while it is newer than the server last run', async () => {
    automationsApi.runAutomation.mockResolvedValue({
      automationId: 'auto-1',
      details: {},
      dryRun: true,
      finishedAt: '2026-11-07T14:00:05Z',
      id: 'run-9',
      startedAt: '2026-11-07T14:00:00Z',
      status: 'succeeded',
      summary: 'Would run 5 of 812 wishlist searches',
    });
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByRole('button', { name: 'Dry run Nightly wishlist' }));

    expect(await within(cardOf('Nightly wishlist')).findByText('Would run 5 of 812 wishlist searches')).toBeInTheDocument();
    expect(within(cardOf('Nightly wishlist')).getByText('Dry run', { selector: '.label' })).toBeInTheDocument();
    expect(within(cardOf('Nightly wishlist')).queryByText('Ran 2 of 5 wishlist searches')).not.toBeInTheDocument();

    // The scheduler then runs it for real and fails; Refresh shows that newer server run.
    automationsApi.getAutomations.mockResolvedValue([{
      ...wishlistAutomation,
      lastRunAt: '2026-11-07T14:31:00Z',
      lastRunStatus: 'failed',
      lastRunSummary: 'Started 0 of 5 wishlist searches (5 failed)',
    }]);
    fireEvent.click(screen.getByRole('button', { name: 'Refresh automations' }));

    expect(await within(cardOf('Nightly wishlist')).findByText('Started 0 of 5 wishlist searches (5 failed)')).toBeInTheDocument();
    expect(within(cardOf('Nightly wishlist')).queryByText('Would run 5 of 812 wishlist searches')).not.toBeInTheDocument();
    expect(within(cardOf('Nightly wishlist')).queryByText('Dry run', { selector: '.label' })).not.toBeInTheDocument();
  });

  it('reloads the automations when the run history is refreshed', async () => {
    await renderCenter();
    await waitForLoad();
    automationsApi.getAutomations.mockResolvedValue([{ ...wishlistAutomation, lastRunAt: '2026-11-07T14:31:00Z', lastRunSummary: 'Ran 5 of 5 wishlist searches' }]);

    fireEvent.click(screen.getByRole('button', { name: 'Refresh run history' }));

    expect(await within(cardOf('Nightly wishlist')).findByText('Ran 5 of 5 wishlist searches')).toBeInTheDocument();
  });

  it('counts failed runs of the last 24 hours through the since query', async () => {
    const dayRuns = [
      { ...runs[0], id: 'f-1' },
      { ...runs[0], id: 'f-2' },
      { ...runs[0], id: 's-1', status: 'succeeded' },
    ];
    automationsApi.getAllRuns.mockImplementation(async (_limit, since) => (since ? dayRuns : runs));
    const before = Date.now();
    await renderCenter();
    await waitForLoad();

    const [limit, since] = automationsApi.getAllRuns.mock.calls.find((call) => call[1]);
    expect(limit).toBe(1000);
    expect(new Date(since).getTime()).toBeGreaterThanOrEqual(before - 24 * 60 * 60 * 1000);
    expect(new Date(since).getTime()).toBeLessThanOrEqual(Date.now() - 24 * 60 * 60 * 1000);
    expect(automationsApi.getAllRuns).toHaveBeenCalledWith(50);
    expect(screen.getByText('Failed (24h)').previousSibling).toHaveTextContent('2');
    expect(screen.getByText('Latest 2 runs across all automations')).toBeInTheDocument();
  });

  it('shows the 24h failure count as a lower bound when the window hits the server limit', async () => {
    const full = Array.from({ length: 1000 }, (_, index) => ({
      ...runs[1],
      id: `r-${index}`,
      status: index < 3 ? 'failed' : 'succeeded',
    }));
    automationsApi.getAllRuns.mockImplementation(async (_limit, since) => (since ? full : runs));
    await renderCenter();
    await waitForLoad();

    expect(screen.getByText('Failed (24h)').previousSibling).toHaveTextContent('≥ 3');
  });

  it('shows the 24h failure count as a lower bound when per-automation retention may have pruned the window', async () => {
    // 200 runs of one automation in the window: older in-window runs may already be deleted by retention.
    const retained = Array.from({ length: 200 }, (_, index) => ({
      ...runs[1],
      id: `r-${index}`,
      status: index < 2 ? 'failed' : 'succeeded',
    }));
    automationsApi.getAllRuns.mockImplementation(async (_limit, since) => (since ? retained : runs));
    await renderCenter();
    await waitForLoad();

    expect(screen.getByText('Failed (24h)').previousSibling).toHaveTextContent('≥ 2');
  });

  it('shows the server reason when slskd.yml rejects the linked settings', async () => {
    const reason = 'Invalid YAML configuration: Wishlist.IntervalSeconds must be between 300 and 2147483647';
    optionsApi.updateYaml.mockRejectedValue({ response: { data: reason, status: 400 } });
    await renderCenter();
    await waitForLoad();

    fireEvent.change(await enabledLinkedInterval(), { target: { value: '900' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save slskd.yml settings' }));

    expect(await screen.findByText(reason)).toBeInTheDocument();
  });

  it('blocks a linked number below its minimum before saving', async () => {
    await renderCenter();
    await waitForLoad();
    const interval = await enabledLinkedInterval();
    expect(interval).toHaveAttribute('min', '300');

    fireEvent.change(interval, { target: { value: '120' } });

    expect(screen.getByText('Minimum is 300.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Save slskd.yml settings' })).toBeDisabled();
    expect(optionsApi.updateYaml).not.toHaveBeenCalled();
  });

  it('shows each linked option description as its tooltip', async () => {
    await renderCenter();
    await waitForLoad();
    const interval = await enabledLinkedInterval();

    fireEvent.mouseEnter(interval.closest('.field').parentElement);

    expect(await screen.findByText('Seconds between background wishlist searches. (wishlist.interval_seconds)')).toBeInTheDocument();
  });

  it('shows effective values and locks a field that an override controls', async () => {
    await renderCenter({ remoteConfiguration: true, wishlist: { enabled: false, intervalSeconds: 600 } });
    await waitForLoad();

    // slskd.yml says 3600, slskd runs with 600: an env var, argument or overlay wins.
    const interval = await screen.findByLabelText('Wishlist interval (seconds)');
    await waitFor(() => expect(screen.getByLabelText('Wishlist enabled')).toBeEnabled());
    expect(interval).toBeDisabled();
    expect(interval).toHaveValue(3600);
    expect(screen.getByText('600')).toBeInTheDocument();
    expect(screen.getAllByText(/^Locked: an environment variable/)).toHaveLength(1);
    // wishlist.enabled matches slskd.yml, so it stays editable.
    expect(screen.getByText('Off')).toBeInTheDocument();

    fireEvent.mouseEnter(interval.closest('.field').parentElement);
    await waitFor(() => expect(screen.getAllByText(/^Locked: an environment variable/)).toHaveLength(2));
  });

  it('never writes an unsaved edit of a field that became locked', async () => {
    let rerender;
    await act(async () => {
      ({ rerender } = render(<AutomationCenter options={{ remoteConfiguration: true, wishlist: { enabled: false, intervalSeconds: 3600 } }} />));
    });
    await waitForLoad();
    fireEvent.change(await enabledLinkedInterval(), { target: { value: '900' } });

    // The running value changes (override pushed over the hub) before the edit is saved: the field locks.
    rerender(<AutomationCenter options={{ remoteConfiguration: true, wishlist: { enabled: false, intervalSeconds: 600 } }} />);
    await waitFor(() => expect(screen.getByLabelText('Wishlist interval (seconds)')).toBeDisabled());
    fireEvent.click(screen.getByRole('button', { name: 'Save slskd.yml settings' }));

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalled());
    expect(optionsApi.updateYaml.mock.calls[0][0].yaml).not.toContain('900');
  });

  it('validates parameter min/max and shows the server reason for a rejected save', async () => {
    automationsApi.updateAutomation.mockRejectedValue({ response: { data: 'parameters.limit must be between 1 and 50', status: 400 } });
    await renderCenter();
    await waitForLoad();

    fireEvent.click(screen.getByRole('button', { name: 'Edit Nightly wishlist' }));
    const dialog = await screen.findByRole('dialog');
    const limit = within(dialog).getByLabelText('Limit');
    expect(limit).toHaveAttribute('min', '1');
    expect(limit).toHaveAttribute('max', '50');

    fireEvent.change(limit, { target: { value: '0' } });
    expect(within(dialog).getByText('Minimum is 1.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();
    fireEvent.change(limit, { target: { value: '51' } });
    expect(within(dialog).getByText('Maximum is 50.')).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save' })).toBeDisabled();

    fireEvent.change(limit, { target: { value: '7' } });
    fireEvent.click(within(dialog).getByRole('button', { name: 'Save' }));

    expect(await within(dialog).findByText('parameters.limit must be between 1 and 50')).toBeInTheDocument();
  });

  it('explains the Cancel button of the automation dialog', async () => {
    await renderCenter();
    await waitForLoad();
    fireEvent.click(screen.getByRole('button', { name: 'Edit Nightly wishlist' }));
    const dialog = await screen.findByRole('dialog');

    fireEvent.mouseEnter(within(dialog).getByRole('button', { name: 'Cancel' }).parentElement);

    expect(await screen.findByText('Close this dialog without saving the automation.')).toBeInTheDocument();
  });

  it('translates server template texts and the interval label', async () => {
    stubLocaleCatalogFetch(vi);
    await setInterfaceLanguage('de');
    automationsApi.getAutomations.mockResolvedValue([{ ...wishlistAutomation, intervalMinutes: 0 }]);
    await renderCenter();

    expect(await screen.findByText('Öffentliche Peers möglich')).toBeInTheDocument();
    expect(screen.getAllByText('Wunschliste erneut versuchen').length).toBeGreaterThan(0);
    expect(screen.getByText('Manuell')).toBeInTheDocument();
  });
});
