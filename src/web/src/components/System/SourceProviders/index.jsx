import LocalizedText from "../../Shared/LocalizedText";
import { interfaceText, interfaceTextProps, useInterfaceLanguage } from "../../../lib/interfaceLocale";
import { getApiErrorMessage } from '../../../lib/apiErrorMessages';
import * as optionsApi from '../../../lib/options';
import {
  getProviderSchema,
  liveOptionsDocument,
  liveSettingsMatch,
  providerConfigKey,
  readProviderSettings,
  setProviderEnabled,
  writeProviderSettings,
} from '../../../lib/sourceProviderSettings';
import * as sourceProvidersApi from '../../../lib/sourceProviders';
import React, { useEffect, useMemo, useRef, useState } from 'react';
import {
  Button,
  Card,
  Checkbox,
  Form,
  Header,
  Icon,
  Label,
  Message,
  Modal,
  Popup,
  Segment,
  Statistic,
} from 'semantic-ui-react';
import * as YAML from 'yaml';

const asArray = (value) => (Array.isArray(value) ? value : []);

export const normalizeProvider = (provider = {}) => ({
  active: provider.active ?? provider.Active ?? false,
  capabilities: asArray(provider.capabilities ?? provider.Capabilities),
  // Until the backend returns these, fall back to the contract mapping.
  configKey:
    provider.configKey ?? provider.ConfigKey ?? providerConfigKey(provider.id ?? provider.Id),
  description: provider.description ?? provider.Description ?? '',
  disabledReason: provider.disabledReason ?? provider.DisabledReason ?? '',
  domain: provider.domain ?? provider.Domain ?? 'Any',
  enabled: provider.enabled ?? provider.Enabled ?? provider.active ?? provider.Active ?? false,
  id: provider.id ?? provider.Id ?? '',
  name: provider.name ?? provider.Name ?? '',
  networkPolicy: provider.networkPolicy ?? provider.NetworkPolicy ?? '',
  registered: provider.registered ?? provider.Registered ?? false,
  restartRequired: provider.restartRequired ?? provider.RestartRequired ?? false,
  riskLevel: provider.riskLevel ?? provider.RiskLevel ?? 'local',
  sortOrder: provider.sortOrder ?? provider.SortOrder ?? 100,
});

export const normalizeProfilePolicy = (policy = {}) => ({
  autoDownloadEnabled:
    policy.autoDownloadEnabled ?? policy.AutoDownloadEnabled ?? false,
  notes: policy.notes ?? policy.Notes ?? '',
  profileId: policy.profileId ?? policy.ProfileId ?? '',
  profileName: policy.profileName ?? policy.ProfileName ?? '',
  providerPriority: asArray(policy.providerPriority ?? policy.ProviderPriority),
});

// A real risk gradient, not a different hue per category: safest to most
// exposed maps onto the same four semantic colors used everywhere else.
const riskColor = (riskLevel) => {
  switch (riskLevel) {
    case 'local':
    case 'trusted-mesh':
      return 'green';
    case 'configured-network':
    case 'configured-lan':
      return 'blue';
    case 'public-network':
      return 'orange';
    case 'high-risk':
      return 'red';
    default:
      return 'grey';
  }
};

const getOption = (source, ...keys) => {
  for (const key of keys) {
    if (source && source[key] !== undefined) {
      return source[key];
    }
  }

  return undefined;
};

// A failed PUT /options/yaml answers with a plain-text reason such as
// "Invalid YAML configuration: ..."; show that instead of a generic text.
const errorText = (error) => {
  const data = error?.response?.data;

  return typeof data === 'string' && data.trim()
    ? data
    : getApiErrorMessage(error, { t: interfaceText });
};

const ENABLED_TOOLTIP =
  'Enable or disable this source provider in slskd.yml. Takes effect without restart unless the badge says otherwise.';
const SETTINGS_TOOLTIP =
  'Edit the provider limits, allowlists and credentials stored in slskd.yml.';
const SETTINGS_READ_ONLY_TOOLTIP =
  'View the provider settings slskd uses now. Read-only while remote configuration is disabled.';
const RESTART_TOOLTIP =
  'Changes to this provider only take effect after slskd restarts.';
const FIELD_RESTART_TOOLTIP =
  'The torrent engine picks this up at startup; restart slskd for a change to take full effect.';
const CLEAR_SECRET_TOOLTIP =
  'Remove the stored value from slskd.yml when you save.';
const CANCEL_TOOLTIP =
  'Close this dialog without saving the provider settings.';
const SAVE_TOOLTIP =
  'Write these settings to slskd.yml and wait until slskd has applied them.';
const NO_REMOTE_CONFIGURATION =
  'Remote configuration is disabled. These controls show the settings shape, but YAML saving is disabled until remote configuration is enabled.';
const READ_ONLY_NOTE =
  'Read-only because remote configuration is disabled. These are the values slskd uses now; change them in slskd.yml.';
const WAITING_FOR_RELOAD =
  'Saved to slskd.yml; waiting for the server to reload the configuration.';
const CONFIG_NOT_RELOADED =
  'Saved to slskd.yml; the server has not reloaded the configuration yet (check flags.no_config_watch).';
const HIGH_RISK_CONFIRM =
  '{name} is a high-risk provider. Enable it anyway?';

// The server picks slskd.yml up through a debounced file watcher, so the
// first GET after a write usually still returns the old state.
const RELOAD_ATTEMPTS = 5;
const RELOAD_DELAY_MS = 600;
const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

const SourceProviders = ({ options = {} }) => {
  useInterfaceLanguage();
  const remoteConfiguration = Boolean(
    getOption(options, 'remoteConfiguration', 'RemoteConfiguration'),
  );
  const [catalog, setCatalog] = useState({
    acquisitionPlanningEnabled: false,
    providers: [],
  });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  // { text, positive | negative | warning | waiting }
  const [saveMessage, setSaveMessage] = useState(null);
  // { provider, readOnly, schema, values, secretsConfigured } while the modal is open.
  const [editor, setEditor] = useState(null);

  const providers = useMemo(
    () =>
      catalog.providers
        .filter((provider) => provider && typeof provider === 'object' && !Array.isArray(provider))
        .map(normalizeProvider)
        .sort((left, right) => left.sortOrder - right.sortOrder),
    [catalog.providers],
  );
  const profilePolicies = useMemo(
    () => asArray(catalog.profilePolicies)
      .filter((policy) => policy && typeof policy === 'object' && !Array.isArray(policy))
      .map(normalizeProfilePolicy),
    [catalog.profilePolicies],
  );
  // Live state: while planning runs, an enabled provider that stays inactive
  // is missing configuration, and disabledReason says what is missing.
  const needsConfiguration = (provider) => catalog.acquisitionPlanningEnabled
    && provider.registered
    && provider.enabled
    && !provider.active
    && Boolean(provider.disabledReason);
  const activeCount = providers.filter((provider) => provider.active).length;
  const readyCount = providers.filter(
    (provider) => provider.registered && provider.enabled && !needsConfiguration(provider),
  ).length;

  const load = async () => {
    setLoading(true);
    setError('');

    try {
      setCatalog(await sourceProvidersApi.getSourceProviders());
    } catch (loadError) {
      setError(getApiErrorMessage(loadError, { t: interfaceText }));
    } finally {
      setLoading(false);
    }
  };

  // The save path polls for a few seconds; never touch state after unmount.
  const mountedRef = useRef(true);
  useEffect(() => {
    mountedRef.current = true;
    load();

    return () => {
      mountedRef.current = false;
    };
  }, []);

  const loadDocument = async () => YAML.parseDocument(await optionsApi.getYaml());

  // Polls `fetchNext` until `matches` accepts an answer and returns it, or
  // returns null once the retry budget runs out (or the page unmounted).
  const pollUntil = async (fetchNext, matches) => {
    for (let attempt = 1; attempt <= RELOAD_ATTEMPTS; attempt += 1) {
      const next = await fetchNext().catch(() => null);
      if (!mountedRef.current) return null;
      if (next && matches(next)) return next;
      if (attempt < RELOAD_ATTEMPTS) await sleep(RELOAD_DELAY_MS);
    }

    return null;
  };

  // Shared save path: re-read slskd.yml, apply one mutation, write it back,
  // then show a waiting state until waitForReload(document) reports that the
  // server reflects the change (true) or gave up (false).
  const persist = async (mutate, successText, waitForReload) => {
    setSaving(true);
    setSaveMessage(null);

    try {
      const document = await loadDocument();
      mutate(document);
      await optionsApi.updateYaml({ yaml: document.toString() });
      if (!mountedRef.current) return true;
      setSaveMessage({ text: interfaceText(WAITING_FOR_RELOAD), waiting: true });

      const reloaded = await waitForReload(document);
      if (!mountedRef.current) return true;
      setSaveMessage(reloaded
        ? { positive: true, text: successText }
        : { text: interfaceText(CONFIG_NOT_RELOADED), warning: true });

      return true;
    } catch (saveError) {
      if (mountedRef.current) {
        setSaveMessage({ negative: true, text: errorText(saveError) });
      }

      return false;
    } finally {
      if (mountedRef.current) setSaving(false);
    }
  };

  const toggleProvider = (provider, enabled) => {
    if (
      enabled
      && provider.riskLevel === 'high-risk'
      && !window.confirm(interfaceText(HIGH_RISK_CONFIRM, { name: provider.name }))
    ) {
      return false;
    }

    return persist(
      (document) => setProviderEnabled(document, provider.configKey, enabled),
      interfaceText(enabled ? "{name} enabled." : "{name} disabled.", { name: provider.name }),
      async () => {
        // Optimistic: the toggle keeps its new state while the server catches up.
        setCatalog((current) => ({
          ...current,
          providers: asArray(current.providers).map((entry) => (
            (entry?.id ?? entry?.Id) === provider.id ? { ...entry, enabled } : entry
          )),
        }));

        const next = await pollUntil(
          sourceProvidersApi.getSourceProviders,
          (answer) => asArray(answer.providers)
            .map(normalizeProvider)
            .some((entry) => entry.id === provider.id && entry.enabled === enabled),
        );
        if (next) setCatalog(next);

        return Boolean(next);
      },
    );
  };

  const openEditor = async (provider) => {
    const schema = getProviderSchema(provider.id);
    // slskd.yml is only readable with remote configuration; without it the
    // dialog shows the live values read-only.
    const readOnly = !remoteConfiguration;
    setSaveMessage(null);

    try {
      const source = readOnly
        ? liveOptionsDocument(await optionsApi.getCurrent())
        : await loadDocument();
      const { secretsConfigured, values } = readProviderSettings(
        source,
        provider.configKey,
        schema,
      );
      setEditor({
        provider,
        readOnly,
        schema,
        secretsConfigured,
        values: Object.fromEntries(
          schema.map((definition) => [
            definition.key,
            definition.type === 'list'
              ? values[definition.key].join('\n')
              : values[definition.key],
          ]),
        ),
      });
    } catch (openError) {
      setSaveMessage({ negative: true, text: errorText(openError) });
    }
  };

  const closeEditor = () => {
    setEditor(null);
    setSaveMessage(null);
  };

  const updateEditorValue = (key, value) => {
    setEditor((current) => current && ({
      ...current,
      values: { ...current.values, [key]: value },
    }));
  };

  const saveEditor = async () => {
    if (!editor || editor.readOnly) {
      return;
    }

    const { provider, schema, values } = editor;
    const saved = await persist(
      (document) => writeProviderSettings(document, provider.configKey, values, schema),
      interfaceText("{name} settings saved.", { name: provider.name }),
      async (document) => {
        const live = await pollUntil(
          optionsApi.getCurrent,
          (current) => liveSettingsMatch(current, document, provider.configKey, schema),
        );
        // The catalogue reads the same live options, so active and
        // disabledReason reflect the saved settings from here on.
        if (mountedRef.current) await load();

        return Boolean(live);
      },
    );

    if (saved) {
      setEditor(null);
    }
  };

  const controlsDisabled = !remoteConfiguration || saving;
  const settingsTooltip = remoteConfiguration ? SETTINGS_TOOLTIP : SETTINGS_READ_ONLY_TOOLTIP;

  const saveMessageBox = saveMessage && (
    <Message
      info={saveMessage.waiting}
      negative={saveMessage.negative}
      positive={saveMessage.positive}
      size="small"
      warning={saveMessage.warning}
    >
      {saveMessage.waiting && (
        <Icon
          loading
          name="spinner"
        />
      )}
      {saveMessage.text}
    </Message>
  );

  const renderField = (definition) => {
    const value = editor.values[definition.key];
    const label = interfaceText(definition.label);
    const stored = editor.secretsConfigured[definition.key];
    const common = {
      'aria-label': label,
      disabled: controlsDisabled,
      id: `source-provider-field-${definition.key}`,
    };
    const onChange = (_, { checked, value: next }) =>
      updateEditorValue(definition.key, definition.type === 'bool' ? Boolean(checked) : next);
    const tooltip = definition.restartRequired
      ? `${interfaceText(definition.description)} ${interfaceText(FIELD_RESTART_TOOLTIP)}`
      : interfaceText(definition.description);
    let control;
    let clearControl = null;

    switch (definition.type) {
      case 'bool':
        control = (
          <Form.Field>
            <Checkbox
              {...common}
              checked={Boolean(value)}
              label={label}
              onChange={onChange}
              toggle
            />
            {definition.restartRequired && (
              <>
                {' '}
                <Label color="orange">
                  <Icon name="redo" />
                  <LocalizedText source={"Restart required"} />
                </Label>
              </>
            )}
          </Form.Field>
        );
        break;
      case 'list':
        control = (
          <Form.TextArea
            {...common}
            label={label}
            onChange={onChange}
            placeholder={interfaceText("One entry per line")}
            rows={3}
            value={value ?? ''}
          />
        );
        break;
      case 'enum':
        control = (
          <Form.Select
            {...common}
            label={label}
            onChange={onChange}
            options={(definition.options ?? []).map((option) => ({
              key: option,
              text: option,
              value: option,
            }))}
            value={value ?? definition.default}
          />
        );
        break;
      case 'secret':
        // A blank field keeps the stored value; null (the clear control) removes it.
        control = (
          <Form.Input
            {...common}
            autoComplete="new-password"
            label={label}
            onChange={onChange}
            placeholder={stored && value !== null
              ? interfaceText("unchanged")
              : interfaceText("not set")}
            type="password"
            value={value ?? ''}
          />
        );
        if (stored && !editor.readOnly) {
          clearControl = (
            <Popup
              content={interfaceText(CLEAR_SECRET_TOOLTIP)}
              position="top left"
              trigger={
                <Checkbox
                  aria-label={interfaceText("Clear stored {name}", { name: label })}
                  checked={value === null}
                  disabled={controlsDisabled}
                  label={interfaceText("Clear stored value")}
                  onChange={(_, { checked }) => updateEditorValue(definition.key, checked ? null : '')}
                  style={{ marginTop: '0.5em' }}
                />
              }
             {...interfaceTextProps(CLEAR_SECRET_TOOLTIP)}/>
          );
        }

        break;
      default:
        control = (
          <Form.Input
            {...common}
            label={label}
            onChange={onChange}
            step={definition.type === 'float' ? 'any' : undefined}
            type={definition.type === 'int' || definition.type === 'float' ? 'number' : 'text'}
            value={value ?? ''}
          />
        );
    }

    return (
      <div
        className="source-provider-field"
        key={definition.key}
      >
        <Popup
          content={tooltip}
          position="top left"
          trigger={<div>{control}</div>}
         {...interfaceTextProps(definition.description)}/>
        {clearControl}
      </div>
    );
  };

  return (
    <div className="source-providers">
      {loading && (
        <Message info>
          <Icon
            loading
            name="spinner"
          />
          <LocalizedText source={"Loading source providers"} /></Message>
      )}
      {error && (
        <Message
          error
          header={interfaceText("Source providers unavailable")}
          content={error}
         {...interfaceTextProps("Source providers unavailable")}/>
      )}
      {!remoteConfiguration && (
        <Message
          info
          size="small"
        >
          <LocalizedText source={NO_REMOTE_CONFIGURATION} /></Message>
      )}
      {/* While the settings dialog is open its own content shows the message. */}
      {!editor && saveMessageBox}
      <Segment>
        <div className="source-providers-header">
          <Header as="h3">
            <Icon name="random" />
            <Header.Content>
              <LocalizedText source={"Source Providers"} /><Header.Subheader>
                <LocalizedText source={"Search, discovery, download, and verification sources available to acquisition planning."} /></Header.Subheader>
            </Header.Content>
          </Header>
          <Popup
            content={interfaceText("Reload the provider catalogue and current enablement state.")}
            position="top center"
            trigger={
              <Button
                aria-label={interfaceText("Refresh source providers")}
                icon="refresh"
                onClick={load}
               {...interfaceTextProps("Refresh source providers")}/>
            }
           {...interfaceTextProps("Reload the provider catalogue and current enablement state.")}/>
        </div>
        <Statistic.Group
          className="source-providers-summary"
          size="small"
        >
          <Statistic>
            <Statistic.Value>{activeCount}</Statistic.Value>
            <Statistic.Label><LocalizedText source={"Active"} /></Statistic.Label>
          </Statistic>
          <Statistic>
            <Statistic.Value>{providers.length}</Statistic.Value>
            <Statistic.Label><LocalizedText source={"Known"} /></Statistic.Label>
          </Statistic>
          <Statistic>
            <Statistic.Value>{readyCount}</Statistic.Value>
            <Statistic.Label><LocalizedText source={"Ready"} /></Statistic.Label>
          </Statistic>
        </Statistic.Group>
        {!catalog.acquisitionPlanningEnabled && (
          <Message
            info
            header={interfaceText("Acquisition planning is disabled")}
            content={interfaceText("Providers remain visible here so enablement gaps are inspectable before automation is turned on.")}
           {...interfaceTextProps("Acquisition planning is disabled")}/>
        )}
      </Segment>
      <Card.Group
        className="source-provider-grid"
        itemsPerRow={3}
        stackable
      >
        {providers.map((provider) => (
          <Card
            className="source-provider-card"
            key={provider.id}
          >
            <Card.Content>
              <div className="source-provider-title-row">
                <Card.Header>{provider.name}</Card.Header>
                <Label color={provider.active ? 'green' : 'grey'}>
                  <Icon name={provider.active ? 'check circle' : 'pause circle'} />
                  {provider.active ? <LocalizedText source={"Active"} /> : <LocalizedText source={"Disabled"} />}
                </Label>
              </div>
              <Card.Meta>{provider.domain}</Card.Meta>
              <Card.Description>{provider.description}</Card.Description>
              <div className="source-provider-labels">
                <Label color={riskColor(provider.riskLevel)}>
                  {provider.riskLevel}
                </Label>
                {/* Registration is a fact, not a state that needs attention. */}
                <Label basic>
                  {provider.registered ? <LocalizedText source={"Registered"} /> : <LocalizedText source={"Not Registered"} />}
                </Label>
                {needsConfiguration(provider) && (
                  <Label color="yellow"><LocalizedText source={"Needs Config"} /></Label>
                )}
                {provider.restartRequired && (
                  <Popup
                    content={interfaceText(RESTART_TOOLTIP)}
                    trigger={
                      <Label color="orange">
                        <Icon name="redo" />
                        <LocalizedText source={"Restart required"} />
                      </Label>
                    }
                   {...interfaceTextProps(RESTART_TOOLTIP)}/>
                )}
              </div>
            </Card.Content>
            <Card.Content>
              <Label.Group size="small">
                {provider.capabilities.map((capability) => (
                  <Label key={capability}>{capability}</Label>
                ))}
              </Label.Group>
            </Card.Content>
            <Card.Content extra>
              <p className="source-provider-policy">{provider.networkPolicy}</p>
              {provider.disabledReason && (
                <p className="source-provider-disabled">{provider.disabledReason}</p>
              )}
              <div className="source-provider-controls">
                <Popup
                  content={interfaceText(ENABLED_TOOLTIP)}
                  trigger={
                    <Checkbox
                      aria-label={interfaceText("Enable {name}", { name: provider.name })}
                      checked={provider.enabled}
                      disabled={controlsDisabled}
                      label={interfaceText("Enabled")}
                      onChange={(_, { checked }) => toggleProvider(provider, Boolean(checked))}
                      toggle
                    />
                  }
                 {...interfaceTextProps(ENABLED_TOOLTIP)}/>
                {getProviderSchema(provider.id).length > 0 && (
                  <Popup
                    content={interfaceText(settingsTooltip)}
                    trigger={
                      <Button
                        aria-label={interfaceText("{name} settings", { name: provider.name })}
                        basic
                        compact
                        content={interfaceText("Settings")}
                        disabled={saving}
                        icon="setting"
                        onClick={() => openEditor(provider)}
                        size="small"
                      />
                    }
                   {...interfaceTextProps(settingsTooltip)}/>
                )}
              </div>
            </Card.Content>
          </Card>
        ))}
      </Card.Group>
      <Modal
        aria-modal="true"
        closeIcon
        onClose={closeEditor}
        open={Boolean(editor)}
        role="dialog"
        size="small"
      >
        {editor && (
          <>
            <Modal.Header>
              <Icon name="setting" />
              {interfaceText("{name} settings", { name: editor.provider.name })}
            </Modal.Header>
            <Modal.Content>
              {saveMessageBox}
              {editor.readOnly && (
                <Message
                  info
                  size="small"
                >
                  <LocalizedText source={READ_ONLY_NOTE} /></Message>
              )}
              <p className="source-provider-config-key">
                <code>{editor.provider.configKey}</code>
              </p>
              <Form onSubmit={saveEditor}>
                {editor.schema.map(renderField)}
              </Form>
            </Modal.Content>
            <Modal.Actions>
              <Popup
                content={interfaceText(CANCEL_TOOLTIP)}
                trigger={
                  <Button
                    content={interfaceText(editor.readOnly ? "Close" : "Cancel")}
                    disabled={saving}
                    onClick={closeEditor}
                  />
                }
               {...interfaceTextProps(CANCEL_TOOLTIP)}/>
              {!editor.readOnly && (
                <Popup
                  content={interfaceText(SAVE_TOOLTIP)}
                  trigger={
                    <Button
                      content={interfaceText("Save settings")}
                      disabled={controlsDisabled}
                      loading={saving}
                      onClick={saveEditor}
                      primary
                    />
                  }
                 {...interfaceTextProps(SAVE_TOOLTIP)}/>
              )}
            </Modal.Actions>
          </>
        )}
      </Modal>
      <Segment>
        <Header as="h4">
          <Icon name="sliders horizontal" />
          <Header.Content>
            <LocalizedText source={"Profile Provider Priority"} /><Header.Subheader>
              <LocalizedText source={"Read-only routing policy for each acquisition profile."} /></Header.Subheader>
          </Header.Content>
        </Header>
        <Card.Group
          className="source-provider-policy-grid"
          itemsPerRow={2}
          stackable
        >
          {profilePolicies.map((policy) => (
            <Card key={policy.profileId}>
              <Card.Content>
                <div className="source-provider-title-row">
                  <Card.Header>{policy.profileName}</Card.Header>
                  <Label color={policy.autoDownloadEnabled ? 'green' : 'grey'}>
                    <Icon name={policy.autoDownloadEnabled ? 'play' : 'pause'} />
                    {policy.autoDownloadEnabled ? <LocalizedText source={"Auto"} /> : <LocalizedText source={"Manual"} />}
                  </Label>
                </div>
                <Card.Description>{policy.notes}</Card.Description>
              </Card.Content>
              <Card.Content>
                <Label.Group size="small">
                  {policy.providerPriority.map((providerId, index) => (
                    <Label key={`${policy.profileId}-${providerId}`}>
                      {index + 1}. {providerId}
                    </Label>
                  ))}
                </Label.Group>
              </Card.Content>
            </Card>
          ))}
        </Card.Group>
      </Segment>
    </div>
  );
};

export default SourceProviders;
