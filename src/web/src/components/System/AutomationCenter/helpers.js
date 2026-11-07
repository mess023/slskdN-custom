// <copyright file="helpers.js" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import { getApiErrorMessage } from '../../../lib/apiErrorMessages';
import { interfaceText } from '../../../lib/interfaceLocale';
import { getRegionalLocale } from '../../../lib/regionalFormat';

export const formatTime = (value) =>
  value ? new Date(value).toLocaleString(getRegionalLocale()) : '—';

// A plain-text body ("Invalid YAML configuration: ...") or { message } carries the server's reason: show it as-is.
export const serverReason = (error, fallback) => {
  const data = error?.response?.data;
  const reason = typeof data === 'string' ? data : data?.message;
  return typeof reason === 'string' && reason.trim()
    ? reason
    : getApiErrorMessage(error, { t: interfaceText }, fallback);
};

// Every /automations endpoint requires the Administrator role.
export const automationErrorText = (error, fallback) =>
  error?.response?.status === 403
    ? interfaceText('Administrator role required.')
    : serverReason(error, fallback);

const runTime = (run) => new Date(run?.finishedAt || run?.startedAt || 0).getTime();

/**
 * The run a card shows. The server keeps lastRun* for real runs only (lastRunAt = finish time), so the run
 * started on this page, possibly a dry run, wins while it is at least as new; a newer server run replaces it.
 */
export const newestRun = (automation, localRun) => {
  const serverRun = automation.lastRunAt
    ? { finishedAt: automation.lastRunAt, status: automation.lastRunStatus, summary: automation.lastRunSummary }
    : undefined;
  return localRun && (!serverRun || runTime(localRun) >= runTime(serverRun)) ? localRun : serverRun;
};

/** Why a number input is not a whole number within min/max (each may be null); '' when fine or blank. */
export const numberProblem = (value, { max, min } = {}) => {
  const text = String(value ?? '').trim();
  if (text === '') return '';
  const number = Number(text);
  if (!Number.isInteger(number)) return interfaceText('Enter a whole number.');
  if (min != null && number < min) return interfaceText('Minimum is {min}.', { min });
  if (max != null && number > max) return interfaceText('Maximum is {max}.', { max });
  return '';
};

export const statusColor = (status) =>
  ({ failed: 'red', skipped: 'grey', succeeded: 'green' })[status] || 'grey';

export const impactColor = (impact = '') => {
  if (/public/i.test(impact)) return 'orange';
  if (/trusted|metadata|configured|local network/i.test(impact)) return 'blue';
  return 'green';
};

export const formatInterval = (minutes) =>
  Number(minutes) > 0 ? interfaceText('Every {minutes} min', { minutes }) : interfaceText('Manual');

/** Build the initial form values for a template (defaults as strings for inputs, bools as bools). */
export const defaultParameters = (template) =>
  Object.fromEntries(
    (template?.parameterSchema || []).map((field) => [
      field.key,
      field.type === 'bool'
        ? Boolean(field.default)
        : field.default == null ? '' : String(field.default),
    ]),
  );

/** Coerce string form values to the JSON types the backend validates against. */
export const coerceParameters = (template, values = {}) => {
  const result = {};
  for (const field of template?.parameterSchema || []) {
    const raw = values[field.key];
    if (field.type === 'bool') {
      result[field.key] = Boolean(raw);
    } else if (field.type === 'int') {
      const trimmed = String(raw ?? '').trim();
      if (trimmed !== '') result[field.key] = Number(trimmed);
    } else {
      const trimmed = String(raw ?? '').trim();
      if (trimmed !== '') result[field.key] = trimmed;
    }
  }

  return result;
};

export const missingRequired = (template, values = {}) =>
  (template?.parameterSchema || [])
    .filter((field) => field.required && field.type !== 'bool')
    .filter((field) => String(values[field.key] ?? '').trim() === '')
    .map((field) => (field.label ? interfaceText(field.label) : field.key));

export const formatRunHistoryText = (runs) =>
  [
    'slskdN automation run history',
    'time\tautomation\ttype\tmode\tstatus\tsummary',
    ...runs.map((run) =>
      [
        formatTime(run.startedAt),
        run.automationName,
        run.type,
        run.dryRun ? 'dry run' : 'run',
        run.status,
        run.summary,
      ].join('\t'),
    ),
  ].join('\n');
