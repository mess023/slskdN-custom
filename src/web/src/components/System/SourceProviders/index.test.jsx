// <copyright file="index.test.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import SourceProviders, {
  normalizeProfilePolicy,
  normalizeProvider,
} from '.';
import * as optionsApi from '../../../lib/options';
import * as sourceProvidersApi from '../../../lib/sourceProviders';
import {
  act,
  configure,
  fireEvent,
  getConfig,
  render,
  screen,
  waitFor,
  within,
} from '@testing-library/react';
import React from 'react';
import YAML from 'yaml';

// Under NODE_ENV=test react-fomantic-ui binds Card/Icon click handlers in a passive
// effect, so a click straight after a findBy/waitFor could hit a just-mounted card
// before React flushed it ("Cannot call an event handler while rendering"). Let the
// pending passive effects (queued with setImmediate by the scheduler) run first.
// The real setImmediate is kept because some tests install fake timers.
const realSetImmediate = setImmediate;
const { asyncWrapper } = getConfig();
configure({
  asyncWrapper: (callback) => asyncWrapper(async () => {
    const result = await callback();
    await new Promise((resolve) => realSetImmediate(resolve));
    return result;
  }),
});

vi.mock('../../../lib/sourceProviders', () => ({
  getSourceProviders: vi.fn(),
}));

vi.mock('../../../lib/options', () => ({
  getCurrent: vi.fn(),
  getYaml: vi.fn(),
  updateYaml: vi.fn(),
}));

const remoteOptions = { remoteConfiguration: true };
const WAITING = 'Saved to slskd.yml; waiting for the server to reload the configuration.';
const NOT_RELOADED =
  'Saved to slskd.yml; the server has not reloaded the configuration yet (check flags.no_config_watch).';
const NO_HOSTS = 'Enabled but no allowlisted hosts configured.';

const httpProvider = {
  active: false,
  capabilities: ['download'],
  configKey: 'virtual_soulfind_v2.backends.http',
  description: 'Direct HTTP downloads from allowlisted hosts.',
  disabledReason: 'Disabled in configuration',
  domain: 'Any',
  enabled: false,
  id: 'Http',
  name: 'HTTP',
  networkPolicy: 'Allowlisted hosts only.',
  registered: true,
  requiresConfiguration: true,
  restartRequired: true,
  riskLevel: 'configured-network',
  sortOrder: 40,
};

// Enabled without hosts, as the server reports it before the allowlist arrives.
const enabledHttp = { ...httpProvider, disabledReason: NO_HOSTS, enabled: true, restartRequired: false };
const activeHttp = { ...enabledHttp, active: true, disabledReason: null };

const torrentProvider = {
  active: false,
  capabilities: ['search', 'download', 'checksum'],
  description: 'Explicitly configured private torrent or magnet sources.',
  disabledReason: 'Disabled by default.',
  domain: 'Any',
  enabled: false,
  id: 'Torrent',
  name: 'Private Torrent',
  networkPolicy: 'High-risk provider.',
  registered: true,
  requiresConfiguration: true,
  riskLevel: 'high-risk',
  sortOrder: 90,
};

const webDavProvider = {
  active: true,
  configKey: 'virtual_soulfind_v2.backends.webdav',
  enabled: true,
  id: 'WebDav',
  name: 'WebDAV',
  registered: true,
  riskLevel: 'configured-network',
  sortOrder: 50,
};

// GET /api/options: camelCase names, values as the server bound them.
const liveHttp = (http) => ({
  virtualSoulfindV2: {
    backends: {
      http: {
        domainAllowlist: [],
        maxFileSizeBytes: 500_000_000,
        requireHttps: false,
        validationTimeoutSeconds: 10,
        ...http,
      },
    },
  },
});

const savedYaml = () => YAML.parse(optionsApi.updateYaml.mock.calls[0][0].yaml);
const catalogWith = (...providers) => ({ acquisitionPlanningEnabled: true, providers });
const cardOf = (name) => screen.getByText(name).closest('.card');
const statisticValue = (label) => screen.getByText(label).closest('.statistic').querySelector('.value');

const saveHttpAllowlist = async () => {
  fireEvent.click(await screen.findByRole('button', { name: 'HTTP settings' }));
  fireEvent.change(await screen.findByLabelText('Domain allowlist'), {
    target: { value: 'files.example.org' },
  });
  fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));
};

describe('SourceProviders', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    optionsApi.getYaml.mockResolvedValue('web:\n  authentication: {}\n');
    optionsApi.updateYaml.mockResolvedValue({});
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('shows active and disabled provider enablement', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue({
      acquisitionPlanningEnabled: true,
      profilePolicies: [
        {
          autoDownloadEnabled: false,
          notes: 'Prefer trusted mesh candidates, then fall back to public Soulseek compatibility.',
          profileId: 'mesh-preferred',
          profileName: 'Mesh Preferred',
          providerPriority: ['LocalLibrary', 'NativeMesh', 'MeshDht', 'Soulseek'],
        },
      ],
      providers: [
        {
          active: true,
          capabilities: ['search', 'download'],
          description: 'Public Soulseek search and peer transfer path.',
          domain: 'Music',
          id: 'Soulseek',
          name: 'Soulseek',
          networkPolicy: 'Rate-limited searches.',
          registered: true,
          requiresConfiguration: false,
          riskLevel: 'public-network',
          sortOrder: 10,
        },
        torrentProvider,
      ],
    });

    render(<SourceProviders />);

    expect(await screen.findByText('Soulseek')).toBeInTheDocument();
    expect(screen.getByText('Private Torrent')).toBeInTheDocument();
    expect(screen.getAllByText('Active').length).toBeGreaterThan(0);
    expect(screen.getByText('Disabled')).toBeInTheDocument();
    expect(screen.getByText('Disabled by default.')).toBeInTheDocument();
    expect(screen.getByText('Profile Provider Priority')).toBeInTheDocument();
    expect(screen.getByText('Mesh Preferred')).toBeInTheDocument();
    expect(screen.getByText('2. NativeMesh')).toBeInTheDocument();
    expect(screen.getByText('Manual')).toBeInTheDocument();
  });

  it('derives Needs Config and Ready from the live provider state', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(
      {
        active: true,
        enabled: true,
        id: 'Soulseek',
        name: 'Soulseek',
        registered: true,
        requiresConfiguration: true,
        sortOrder: 10,
      },
      enabledHttp,
      torrentProvider,
    ));

    render(<SourceProviders options={remoteOptions} />);

    await screen.findByText('Soulseek');
    // Only the enabled provider that stays inactive for lack of configuration.
    expect(screen.getAllByText('Needs Config')).toHaveLength(1);
    expect(within(cardOf('HTTP')).getByText('Needs Config')).toBeInTheDocument();
    expect(within(cardOf('HTTP')).getByText(NO_HOSTS)).toBeInTheDocument();
    expect(statisticValue('Ready')).toHaveTextContent('1');
  });

  it('keeps providers visible when acquisition planning is disabled', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue({
      acquisitionPlanningEnabled: false,
      providers: [
        {
          active: false,
          capabilities: ['search'],
          description: 'Already indexed or shared files on this slskdN node.',
          disabledReason: 'VirtualSoulfind v2 acquisition planning is disabled.',
          domain: 'Music',
          enabled: true,
          id: 'LocalLibrary',
          name: 'Local Library',
          networkPolicy: 'No peer traffic.',
          registered: true,
          requiresConfiguration: false,
          riskLevel: 'local',
          sortOrder: 0,
        },
      ],
    });

    render(<SourceProviders />);

    expect(await screen.findByText('Acquisition planning is disabled')).toBeInTheDocument();
    expect(screen.getByText('Local Library')).toBeInTheDocument();
    expect(
      screen.getByText('VirtualSoulfind v2 acquisition planning is disabled.'),
    ).toBeInTheDocument();
    // Inactive because planning is off, not because configuration is missing.
    expect(screen.queryByText('Needs Config')).not.toBeInTheDocument();
    await waitFor(() =>
      expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(1),
    );
  });

  it('normalizes malformed nested provider and policy arrays', () => {
    expect(normalizeProvider({
      capabilities: { search: true },
      id: 'bad-provider',
    })).toMatchObject({
      capabilities: [],
      id: 'bad-provider',
    });
    expect(normalizeProfilePolicy({
      profileId: 'bad-policy',
      providerPriority: { provider: 'Soulseek' },
    })).toMatchObject({
      profileId: 'bad-policy',
      providerPriority: [],
    });
  });

  it('falls back to the contract config key and active flag when the API omits them', () => {
    expect(normalizeProvider({ active: true, id: 'WebDav' })).toMatchObject({
      configKey: 'virtual_soulfind_v2.backends.webdav',
      enabled: true,
      restartRequired: false,
    });
  });

  it('toggles enabled through the yaml document and reloads providers', async () => {
    sourceProvidersApi.getSourceProviders
      .mockResolvedValueOnce(catalogWith(httpProvider))
      .mockResolvedValue(catalogWith({ ...httpProvider, active: true, enabled: true }));

    render(<SourceProviders options={remoteOptions} />);

    const toggle = await screen.findByRole('checkbox', { name: 'Enable HTTP' });
    expect(toggle).not.toBeChecked();
    expect(screen.getByText('Restart required')).toBeInTheDocument();

    fireEvent.click(toggle);

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    expect(optionsApi.getYaml).toHaveBeenCalledTimes(1);
    expect(savedYaml()).toEqual({
      virtual_soulfind_v2: { backends: { http: { enabled: true } } },
      web: { authentication: {} },
    });
    await waitFor(() =>
      expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(2),
    );
    expect(await screen.findByText('HTTP enabled.')).toBeInTheDocument();
    expect(toggle).toBeChecked();
  });

  it('keeps the optimistic toggle and retries until the server reports the new state', async () => {
    vi.useFakeTimers();
    sourceProvidersApi.getSourceProviders
      .mockResolvedValueOnce(catalogWith(httpProvider))
      .mockResolvedValueOnce(catalogWith(httpProvider))
      .mockResolvedValueOnce(catalogWith(httpProvider))
      .mockResolvedValue(catalogWith({ ...httpProvider, active: true, enabled: true }));

    render(<SourceProviders options={remoteOptions} />);

    const toggle = await screen.findByRole('checkbox', { name: 'Enable HTTP' });
    fireEvent.click(toggle);

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(2));
    // Stale server answer: the optimistic value must not snap back.
    expect(toggle).toBeChecked();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(600);
    });
    expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(3);
    expect(toggle).toBeChecked();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(600);
    });
    expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(4);
    expect(toggle).toBeChecked();
    expect(screen.getAllByText('Active').length).toBeGreaterThan(0);
    expect(screen.queryByText(/has not reloaded the configuration/u)).not.toBeInTheDocument();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(5_000);
    });
    expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(4);
  });

  it('warns when the server never reloads the configuration', async () => {
    vi.useFakeTimers();
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(httpProvider));

    render(<SourceProviders options={remoteOptions} />);

    const toggle = await screen.findByRole('checkbox', { name: 'Enable HTTP' });
    fireEvent.click(toggle);

    await waitFor(() => expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(2));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(4 * 600);
    });

    expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(6);
    expect(toggle).toBeChecked();
    expect(screen.getByText(NOT_RELOADED)).toBeInTheDocument();
  });

  it('asks for confirmation before enabling a high-risk provider', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(false);
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(torrentProvider));

    render(<SourceProviders options={remoteOptions} />);

    const toggle = await screen.findByRole('checkbox', { name: 'Enable Private Torrent' });
    fireEvent.click(toggle);

    expect(confirm).toHaveBeenCalledWith('Private Torrent is a high-risk provider. Enable it anyway?');
    expect(optionsApi.updateYaml).not.toHaveBeenCalled();
    expect(toggle).not.toBeChecked();

    confirm.mockReturnValue(true);
    sourceProvidersApi.getSourceProviders.mockResolvedValue(
      catalogWith({ ...torrentProvider, active: true, enabled: true }),
    );
    fireEvent.click(toggle);

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    expect(savedYaml().virtual_soulfind_v2.backends.torrent).toEqual({ enabled: true });
    expect(await screen.findByText('Private Torrent enabled.')).toBeInTheDocument();
  });

  it('opens the settings modal from the schema and saves an int and a list', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(httpProvider));
    optionsApi.getYaml.mockResolvedValue([
      'virtual_soulfind_v2:',
      '  backends:',
      '    http:',
      '      enabled: false',
      '      max_file_size_bytes: 1200',
    ].join('\n'));
    optionsApi.getCurrent.mockResolvedValue(liveHttp({
      domainAllowlist: ['a.example.com', 'b.example.com'],
      maxFileSizeBytes: 2500,
    }));

    render(<SourceProviders options={remoteOptions} />);

    fireEvent.click(await screen.findByRole('button', { name: 'HTTP settings' }));

    const allowlist = await screen.findByLabelText('Domain allowlist');
    const maxSize = screen.getByLabelText('Max file size (bytes)');
    expect(maxSize).toHaveValue(1200);
    expect(screen.getByLabelText('Validation timeout (s)')).toHaveValue(10);
    expect(screen.getByRole('checkbox', { name: 'Require HTTPS' })).not.toBeChecked();
    expect(screen.queryByLabelText('Max requests per minute')).not.toBeInTheDocument();

    fireEvent.change(maxSize, { target: { value: '2500' } });
    fireEvent.change(allowlist, { target: { value: 'a.example.com\nb.example.com' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    expect(savedYaml().virtual_soulfind_v2.backends.http).toEqual({
      domain_allowlist: ['a.example.com', 'b.example.com'],
      enabled: false,
      max_file_size_bytes: 2500,
      require_https: false,
      validation_timeout_seconds: 10,
    });
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(screen.getByText('HTTP settings saved.')).toBeInTheDocument();
  });

  it('waits until the server applies saved settings before refreshing the card', async () => {
    vi.useFakeTimers();
    // The server re-reads slskd.yml only after its file watcher fires.
    let polls = 0;
    const applied = () => polls >= 3;
    optionsApi.getYaml.mockResolvedValue('virtual_soulfind_v2:\n  backends:\n    http:\n      enabled: true\n');
    optionsApi.getCurrent.mockImplementation(async () => {
      polls += 1;

      return liveHttp({ domainAllowlist: applied() ? ['files.example.org'] : [] });
    });
    sourceProvidersApi.getSourceProviders.mockImplementation(
      async () => catalogWith(applied() ? activeHttp : enabledHttp),
    );

    render(<SourceProviders options={remoteOptions} />);
    await saveHttpAllowlist();

    const dialog = screen.getByRole('dialog');
    expect(await within(dialog).findByText(WAITING)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save settings' })).toBeDisabled();
    expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(1);

    await act(async () => {
      await vi.advanceTimersByTimeAsync(2 * 600);
    });

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(polls).toBe(3);
    expect(screen.getByText('HTTP settings saved.')).toBeInTheDocument();
    expect(within(cardOf('HTTP')).getByText('Active')).toBeInTheDocument();
    expect(screen.queryByText(NO_HOSTS)).not.toBeInTheDocument();
    expect(screen.queryByText('Needs Config')).not.toBeInTheDocument();
  });

  it('warns when saved settings never reach the server', async () => {
    vi.useFakeTimers();
    optionsApi.getCurrent.mockResolvedValue(liveHttp({}));
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(enabledHttp));

    render(<SourceProviders options={remoteOptions} />);
    await saveHttpAllowlist();

    expect(await within(screen.getByRole('dialog')).findByText(WAITING)).toBeInTheDocument();
    await act(async () => {
      await vi.advanceTimersByTimeAsync(4 * 600);
    });

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    expect(optionsApi.getCurrent).toHaveBeenCalledTimes(5);
    expect(sourceProvidersApi.getSourceProviders).toHaveBeenCalledTimes(2);
    expect(screen.getByText(NOT_RELOADED)).toBeInTheDocument();
  });

  it('shows a failed save inside the dialog with the server reason', async () => {
    const reason = 'Invalid YAML configuration: Value was either too large or too small for an Int32.';
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(httpProvider));
    optionsApi.updateYaml.mockRejectedValue({ response: { data: reason, status: 400 } });

    render(<SourceProviders options={remoteOptions} />);
    await saveHttpAllowlist();

    const dialog = screen.getByRole('dialog');
    expect(await within(dialog).findByText(reason)).toBeInTheDocument();
    expect(within(dialog).getByRole('button', { name: 'Save settings' })).toBeEnabled();
    expect(optionsApi.getCurrent).not.toHaveBeenCalled();
  });

  it('removes a stored secret only when it is cleared', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(webDavProvider));
    optionsApi.getYaml.mockResolvedValue([
      'virtual_soulfind_v2:',
      '  backends:',
      '    webdav:',
      '      enabled: true',
      '      domain_allowlist: [dav.example.com]',
      '      password: keep-me',
      '      bearer_token: old-token',
    ].join('\n'));
    optionsApi.getCurrent.mockResolvedValue({
      virtualSoulfindV2: { backends: { webDav: { domainAllowlist: ['dav.example.com'], maxFileSizeBytes: 500_000_000, validationTimeoutSeconds: 15 } } },
    });

    render(<SourceProviders options={remoteOptions} />);
    fireEvent.click(await screen.findByRole('button', { name: 'WebDAV settings' }));

    const clearToken = await screen.findByRole('checkbox', { name: 'Clear stored Bearer token' });
    expect(screen.getByRole('checkbox', { name: 'Clear stored Password' })).not.toBeChecked();
    fireEvent.mouseEnter(clearToken.parentElement);
    expect(await screen.findByText('Remove the stored value from slskd.yml when you save.')).toBeInTheDocument();

    fireEvent.click(clearToken);
    expect(clearToken).toBeChecked();
    fireEvent.click(screen.getByRole('button', { name: 'Save settings' }));

    await waitFor(() => expect(optionsApi.updateYaml).toHaveBeenCalledTimes(1));
    expect(savedYaml().virtual_soulfind_v2.backends.webdav).toEqual({
      domain_allowlist: ['dav.example.com'],
      enabled: true,
      max_file_size_bytes: 500_000_000,
      password: 'keep-me',
      validation_timeout_seconds: 15,
    });
  });

  it('marks the torrent DHT settings restart-required and reads peer sources in any casing', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(torrentProvider));
    optionsApi.getYaml.mockResolvedValue([
      'virtual_soulfind_v2:',
      '  backends:',
      '    torrent:',
      '      allowed_peer_sources: both',
    ].join('\n'));

    render(<SourceProviders options={remoteOptions} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Private Torrent settings' }));
    await screen.findByRole('checkbox', { name: 'Private only' });

    const dialog = screen.getByRole('dialog');
    const badges = within(dialog).getAllByText('Restart required');
    expect(badges).toHaveLength(2);
    fireEvent.mouseEnter(badges[0]);
    expect(await screen.findByText(
      'Only accept private torrents. The torrent engine picks this up at startup; restart slskd for a change to take full effect.',
    )).toBeInTheDocument();

    const peerSources = within(dialog).getByLabelText('Allowed peer sources');
    expect(peerSources.querySelector('.text')).toHaveTextContent('Both');
  });

  it('explains the dialog buttons', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(httpProvider));

    render(<SourceProviders options={remoteOptions} />);
    fireEvent.click(await screen.findByRole('button', { name: 'HTTP settings' }));

    fireEvent.mouseEnter(await screen.findByRole('button', { name: 'Cancel' }));
    expect(await screen.findByText('Close this dialog without saving the provider settings.')).toBeInTheDocument();
    fireEvent.mouseEnter(screen.getByRole('button', { name: 'Save settings' }));
    expect(await screen.findByText(
      'Write these settings to slskd.yml and wait until slskd has applied them.',
    )).toBeInTheDocument();
  });

  it('opens the settings read-only without remote configuration', async () => {
    sourceProvidersApi.getSourceProviders.mockResolvedValue(catalogWith(httpProvider));
    optionsApi.getCurrent.mockResolvedValue(liveHttp({
      domainAllowlist: ['files.example.org'],
      requireHttps: true,
    }));

    render(<SourceProviders options={{ remoteConfiguration: false }} />);

    const toggle = await screen.findByRole('checkbox', { name: 'Enable HTTP' });
    expect(toggle).toBeDisabled();
    expect(screen.getByText(/Remote configuration is disabled/u)).toBeInTheDocument();
    fireEvent.click(toggle);
    expect(optionsApi.updateYaml).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'HTTP settings' }));

    const dialog = await screen.findByRole('dialog');
    expect(await within(dialog).findByText(
      'Read-only because remote configuration is disabled. These are the values slskd uses now; change them in slskd.yml.',
    )).toBeInTheDocument();
    const allowlist = within(dialog).getByLabelText('Domain allowlist');
    expect(allowlist).toHaveValue('files.example.org');
    expect(allowlist).toBeDisabled();
    expect(within(dialog).getByRole('checkbox', { name: 'Require HTTPS' })).toBeChecked();
    expect(within(dialog).queryByRole('button', { name: 'Save settings' })).not.toBeInTheDocument();
    expect(optionsApi.getYaml).not.toHaveBeenCalled();

    fireEvent.click(within(dialog).getByRole('button', { name: 'Close' }));
    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
  });
});
