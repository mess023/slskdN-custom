// <copyright file="index.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import * as automationsApi from '../../../lib/automations';
import { interfaceText, useInterfaceLanguage } from '../../../lib/interfaceLocale';
import * as optionsApi from '../../../lib/options';
import AutomationCard from './AutomationCard';
import AutomationFormModal from './AutomationFormModal';
import { automationErrorText as errorText, formatRunHistoryText } from './helpers';
import RunHistory from './RunHistory';
import * as YAML from 'yaml';
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { toast } from 'react-toastify';
import { Button, Card, Header, Icon, Message, Popup, Segment, Statistic } from 'semantic-ui-react';

const HISTORY_LIMIT = 50;
// GET /automations/runs clamps limit to this (AutomationService.MaxRunsTotal).
const RUNS_LIMIT_MAX = 1000;
// The server keeps only this many runs per automation (AutomationService.MaxRunsPerAutomation).
const RUNS_PER_AUTOMATION_MAX = 200;
const DAY_MS = 24 * 60 * 60 * 1000;

const getOption = (source, ...keys) => {
  for (const key of keys) {
    if (source && Object.prototype.hasOwnProperty.call(source, key)) return source[key];
  }

  return undefined;
};

const AutomationCenter = ({ options = {} }) => {
  useInterfaceLanguage();
  const remoteConfiguration = Boolean(getOption(options, 'remoteConfiguration', 'RemoteConfiguration'));
  const [templates, setTemplates] = useState([]);
  const [automations, setAutomations] = useState([]);
  const [runs, setRuns] = useState([]);
  const [failed24h, setFailed24h] = useState({ count: 0, truncated: false });
  const [yamlDocument, setYamlDocument] = useState(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState('');
  const [busyIds, setBusyIds] = useState(() => new Set());
  const [lastRuns, setLastRuns] = useState({});
  const [modal, setModal] = useState(null); // null | { automation: null | Automation }
  const [copyStatus, setCopyStatus] = useState('');

  const loadRuns = useCallback(async () => {
    const [latest, lastDay] = await Promise.all([
      automationsApi.getAllRuns(HISTORY_LIMIT),
      automationsApi.getAllRuns(RUNS_LIMIT_MAX, new Date(Date.now() - DAY_MS).toISOString()),
    ]);
    setRuns(latest);
    const perAutomation = new Map();
    for (const run of lastDay) perAutomation.set(run.automationId, (perAutomation.get(run.automationId) ?? 0) + 1);
    setFailed24h({
      count: lastDay.filter((run) => run.status === 'failed').length,
      // The count is a lower bound when the page is full, or when retention may have pruned in-window runs:
      // an in-window run is only pruned once its automation has RUNS_PER_AUTOMATION_MAX newer (so also in-window) runs.
      truncated: lastDay.length >= RUNS_LIMIT_MAX
        || [...perAutomation.values()].some((count) => count >= RUNS_PER_AUTOMATION_MAX),
    });
  }, []);

  const loadYaml = useCallback(async () => {
    try {
      setYamlDocument(YAML.parseDocument(await optionsApi.getYaml()));
    } catch {
      setYamlDocument(null);
    }
  }, []);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError('');
    try {
      const [nextTemplates, nextAutomations] = await Promise.all([
        automationsApi.getTemplates(),
        automationsApi.getAutomations(),
        loadRuns(),
      ]);
      setTemplates(Array.isArray(nextTemplates) ? nextTemplates : []);
      setAutomations(Array.isArray(nextAutomations) ? nextAutomations : []);
      if ((nextTemplates || []).some((template) => template.linkedOptions?.length)) {
        await loadYaml();
      }
    } catch (error) {
      setLoadError(errorText(error, interfaceText('Failed to load automations.')));
    } finally {
      setLoading(false);
    }
  }, [loadRuns, loadYaml]);

  useEffect(() => {
    load();
  }, [load]);

  const templateByType = useMemo(
    () => Object.fromEntries(templates.map((template) => [template.type, template])),
    [templates],
  );

  const enabledCount = useMemo(() => automations.filter((automation) => automation.enabled).length, [automations]);

  const refreshAutomations = async () => {
    setAutomations(await automationsApi.getAutomations());
  };

  // Busy is per card: only the card whose own request is in flight is locked.
  const busyAction = async (id, failure, action) => {
    setBusyIds((current) => new Set(current).add(id));
    try {
      await action();
    } catch (error) {
      toast.error(errorText(error, failure));
    } finally {
      setBusyIds((current) => {
        const next = new Set(current);
        next.delete(id);
        return next;
      });
    }
  };

  const toRequest = (automation) => ({
    enabled: automation.enabled,
    intervalMinutes: automation.intervalMinutes,
    name: automation.name,
    parameters: automation.parameters || {},
    type: automation.type,
  });

  const toggle = (automation, enabled) =>
    busyAction(automation.id, interfaceText('Failed to update automation.'), async () => {
      const updated = await automationsApi.updateAutomation(automation.id, { ...toRequest(automation), enabled });
      setAutomations((current) => current.map((item) => (item.id === automation.id ? updated : item)));
    });

  const run = (automation, dryRun) =>
    busyAction(automation.id, interfaceText('Run failed.'), async () => {
      const result = await automationsApi.runAutomation(automation.id, { dryRun });
      setLastRuns((current) => ({ ...current, [automation.id]: result }));
      toast[result?.status === 'failed' ? 'error' : 'info'](`${automation.name}: ${result?.summary || result?.status}`);
      await Promise.all([refreshAutomations(), loadRuns()]);
    });

  const remove = async (automation) => {
    if (!window.confirm(interfaceText('Delete automation "{name}"?', { name: automation.name }))) return;
    await busyAction(automation.id, interfaceText('Failed to delete automation.'), async () => {
      await automationsApi.deleteAutomation(automation.id);
      setAutomations((current) => current.filter((item) => item.id !== automation.id));
      await loadRuns();
    });
  };

  const submitModal = async (request) => {
    if (modal?.automation) {
      await automationsApi.updateAutomation(modal.automation.id, request);
    } else {
      await automationsApi.createAutomation(request);
    }

    await refreshAutomations();
  };

  // Also reloads the automations so each card's last run and Next match the history rows.
  const refreshRuns = async () => {
    try {
      await Promise.all([refreshAutomations(), loadRuns()]);
    } catch (error) {
      toast.error(errorText(error, interfaceText('Failed to load run history.')));
    }
  };

  const copyHistory = async () => {
    const text = formatRunHistoryText(runs);
    if (!navigator.clipboard?.writeText) {
      setCopyStatus(interfaceText('Clipboard unavailable; copy the run history manually.'));
      return;
    }

    try {
      await navigator.clipboard.writeText(text);
      setCopyStatus(interfaceText('Run history copied.'));
    } catch {
      setCopyStatus(interfaceText('Unable to copy run history.'));
    }
  };

  const newButton = (
    <Popup
      content={interfaceText('Create an automation from a server-side template. It runs on the server, on a schedule or manually.')}
      position="top center"
      trigger={
        <Button
          disabled={loading || templates.length === 0}
          onClick={() => setModal({ automation: null })}
          primary
          size="small"
        >
          <Icon name="plus" />
          {interfaceText('New automation')}
        </Button>
      }
    />
  );

  return (
    <div className="automation-center">
      <Header as="h3">
        <Icon name="magic" />
        <Header.Content>
          {interfaceText('Automation Center')}
          <Header.Subheader>
            {interfaceText('Server-side automations built from templates. Dry run previews, Run now executes, the scheduler handles the rest.')}
          </Header.Subheader>
        </Header.Content>
      </Header>
      <div className="automation-center-actions">
        {newButton}
        <Popup
          content={interfaceText('Reload templates, automations and run history from the server.')}
          position="top center"
          trigger={
            <Button
              aria-label={interfaceText('Refresh automations')}
              icon="refresh"
              loading={loading}
              onClick={load}
              size="small"
            />
          }
        />
      </div>
      {loadError && <Message negative size="small">{loadError}</Message>}
      {copyStatus && <Message info size="small">{copyStatus}</Message>}

      <Statistic.Group
        className="automation-summary"
        size="small"
        widths="three"
      >
        <Statistic>
          <Statistic.Value>{automations.length}</Statistic.Value>
          <Statistic.Label>{interfaceText('Automations')}</Statistic.Label>
        </Statistic>
        <Statistic color="green">
          <Statistic.Value>{enabledCount}</Statistic.Value>
          <Statistic.Label>{interfaceText('Enabled')}</Statistic.Label>
        </Statistic>
        <Popup
          content={interfaceText('Failed runs (dry runs included) started in the last 24 hours. "≥" means some runs in that window were not counted (more than {limit} in total, or older runs already removed because an automation keeps only its latest {perAutomation}), so there may be more failures.', { limit: RUNS_LIMIT_MAX, perAutomation: RUNS_PER_AUTOMATION_MAX })}
          position="top center"
          trigger={
            <Statistic color={failed24h.count > 0 ? 'red' : 'grey'}>
              <Statistic.Value>{failed24h.truncated ? `≥ ${failed24h.count}` : failed24h.count}</Statistic.Value>
              <Statistic.Label>{interfaceText('Failed (24h)')}</Statistic.Label>
            </Statistic>
          }
        />
      </Statistic.Group>

      {!loading && automations.length === 0 && !loadError && (
        <Segment
          placeholder
          textAlign="center"
        >
          <Header icon>
            <Icon name="magic" />
            {interfaceText('No automations yet')}
            <Header.Subheader>
              {templates.length === 0
                ? interfaceText('The server exposes no automation templates.')
                : interfaceText('Pick a template, set an interval, and the server runs it for you. Start with a dry run to preview.')}
            </Header.Subheader>
          </Header>
          {newButton}
        </Segment>
      )}

      <Card.Group
        className="automation-recipe-grid"
        itemsPerRow={2}
        stackable
      >
        {automations.map((automation) => (
          <AutomationCard
            automation={automation}
            busy={busyIds.has(automation.id)}
            key={automation.id}
            lastRun={lastRuns[automation.id]}
            onDelete={remove}
            onEdit={(item) => setModal({ automation: item })}
            onRun={run}
            onToggle={toggle}
            onYamlSaved={loadYaml}
            remoteConfiguration={remoteConfiguration}
            runningOptions={options}
            template={templateByType[automation.type]}
            yamlDocument={yamlDocument}
          />
        ))}
      </Card.Group>

      <RunHistory
        loading={loading}
        onCopy={copyHistory}
        onRefresh={refreshRuns}
        runs={runs}
      />

      {modal && (
        <AutomationFormModal
          automation={modal.automation}
          onClose={() => setModal(null)}
          onSubmit={submitModal}
          templates={templates}
        />
      )}
    </div>
  );
};

export default AutomationCenter;
