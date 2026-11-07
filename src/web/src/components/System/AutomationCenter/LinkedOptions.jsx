// <copyright file="LinkedOptions.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import { interfaceText } from '../../../lib/interfaceLocale';
import * as optionsApi from '../../../lib/options';
import { numberProblem, serverReason } from './helpers';
import * as YAML from 'yaml';
import React, { useEffect, useMemo, useState } from 'react';
import { Button, Checkbox, Form, Icon, Label, Message, Popup } from 'semantic-ui-react';

const pathOf = (yamlPath) => yamlPath.split('.');

// Absent keys show the backend default (option.default) instead of false/''.
const readValues = (document, linkedOptions) =>
  Object.fromEntries(
    linkedOptions.map((option) => {
      const value = document?.getIn(pathOf(option.yamlPath)) ?? option.default;
      return [
        option.yamlPath,
        option.type === 'bool' ? Boolean(value) : value == null ? '' : String(value),
      ];
    }),
  );

// The running options (GET /options, pushed live over the application hub) use camelCase keys:
// wishlist.interval_seconds -> wishlist.intervalSeconds. undefined = not known.
const runningValue = (options, yamlPath) =>
  pathOf(yamlPath).reduce(
    (node, key) => node?.[key.replace(/_([a-z0-9])/g, (_match, next) => next.toUpperCase())],
    options,
  );

/**
 * A3: editable slskd.yml settings a template points at (template.linkedOptions).
 * Reads the live yaml document, writes back via setIn + updateYaml, and shows the effective (running) value next to it.
 */
const LinkedOptions = ({ linkedOptions, remoteConfiguration, runningOptions, yamlDocument, onSaved }) => {
  const loaded = useMemo(() => readValues(yamlDocument, linkedOptions), [yamlDocument, linkedOptions]);
  const [values, setValues] = useState(loaded);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState(null);

  useEffect(() => {
    setValues(loaded);
  }, [loaded]);

  const rows = linkedOptions.map((option) => {
    const running = runningValue(runningOptions, option.yamlPath);
    // Env vars, command-line arguments and runtime overlays beat slskd.yml; so does a save that is not applied yet.
    const overridden = Boolean(yamlDocument) && running !== undefined && (option.type === 'bool'
      ? Boolean(running) !== loaded[option.yamlPath]
      : String(running ?? '') !== loaded[option.yamlPath]);
    // A locked field is never written, so it must not block saving the others.
    const problem = option.type === 'int' && !overridden ? numberProblem(values[option.yamlPath], option) : '';
    return { option, overridden, problem, running };
  });
  const locked = new Set(rows.filter((row) => row.overridden).map((row) => row.option.yamlPath));

  const save = async () => {
    setSaving(true);
    setMessage(null);
    try {
      const document = YAML.parseDocument(await optionsApi.getYaml());
      for (const option of linkedOptions) {
        const raw = values[option.yamlPath];
        // Only touch keys the user changed; untouched defaults stay out of the file. Locked fields are never written.
        if (raw === loaded[option.yamlPath] || locked.has(option.yamlPath)) continue;

        const path = pathOf(option.yamlPath);
        if (option.type === 'bool') {
          document.setIn(path, Boolean(raw));
          continue;
        }

        const text = String(raw ?? '').trim();
        if (text === '') {
          // Blank means "use the backend default": remove the key rather than writing 0/''.
          if (document.hasIn(path)) document.deleteIn(path);
          continue;
        }

        document.setIn(path, option.type === 'int' ? Number(text) : text);
      }

      await optionsApi.updateYaml({ yaml: document.toString() });
      setMessage({ positive: true, text: interfaceText('slskd.yml updated.') });
      onSaved?.();
    } catch (error) {
      setMessage({ negative: true, text: serverReason(error, interfaceText('Unable to save slskd.yml.')) });
    } finally {
      setSaving(false);
    }
  };

  const disabled = !remoteConfiguration || saving || !yamlDocument;
  const overrideText = interfaceText('Locked: an environment variable, command-line argument or runtime override sets this value, or a saved change is not applied yet. Editing slskd.yml here would have no effect.');

  return (
    <div className="automation-linked-options">
      <Popup
        content={interfaceText('Real slskd.yml settings this automation depends on. Saving writes to slskd.yml through the options API; the automation itself runs regardless of these values.')}
        position="top left"
        trigger={<strong>{interfaceText('Related slskd.yml settings')}</strong>}
      />
      {!remoteConfiguration && (
        <Message
          info
          size="tiny"
        >
          {interfaceText('Remote configuration is disabled. These controls show the settings shape, but YAML saving is disabled until remote configuration is enabled.')}
        </Message>
      )}
      {/* warning: Semantic hides warning messages inside a form unless the form itself is in the warning state. */}
      <Form
        size="small"
        warning={rows.some((row) => row.overridden)}
      >
        {rows.map(({ option, overridden, problem, running }) => {
          const label = interfaceText(option.label);
          const tooltip = overridden
            ? overrideText
            : option.description ? `${interfaceText(option.description)} (${option.yamlPath})` : option.yamlPath;
          return (
            <div key={option.yamlPath}>
              <Popup
                content={tooltip}
                position="top left"
                trigger={
                  <div>
                    {option.type === 'bool'
                      ? (
                        <Form.Field>
                          <Checkbox
                            aria-label={label}
                            checked={Boolean(values[option.yamlPath])}
                            disabled={disabled || overridden}
                            label={label}
                            onChange={(_event, { checked }) =>
                              setValues((current) => ({ ...current, [option.yamlPath]: checked }))}
                            toggle
                          />
                        </Form.Field>
                      )
                      : (
                        <Form.Input
                          aria-label={label}
                          disabled={disabled || overridden}
                          error={problem || false}
                          fluid
                          label={label}
                          min={option.min ?? undefined}
                          onChange={(_event, { value }) =>
                            setValues((current) => ({ ...current, [option.yamlPath]: value }))}
                          type={option.type === 'int' ? 'number' : 'text'}
                          value={values[option.yamlPath] ?? ''}
                        />
                      )}
                  </div>
                }
              />
              {running !== undefined && (
                <Popup
                  content={interfaceText('The value slskd runs with now, after environment variables, command-line arguments and runtime overrides.')}
                  position="top left"
                  trigger={
                    <Label
                      basic
                      color={overridden ? 'orange' : undefined}
                      size="tiny"
                    >
                      {overridden && <Icon name="warning sign" />}
                      {interfaceText('Effective value')}
                      <Label.Detail>
                        {option.type === 'bool' ? interfaceText(running ? 'On' : 'Off') : String(running ?? '')}
                      </Label.Detail>
                    </Label>
                  }
                />
              )}
              {overridden && (
                <Message
                  size="tiny"
                  warning
                >
                  {overrideText}
                </Message>
              )}
            </div>
          );
        })}
        <Popup
          content={interfaceText('Write these values to slskd.yml. Other settings in the file are left untouched.')}
          position="top left"
          trigger={
            <span>
              <Button
                disabled={disabled || rows.some((row) => row.problem)}
                loading={saving}
                onClick={save}
                size="tiny"
              >
                {interfaceText('Save slskd.yml settings')}
              </Button>
            </span>
          }
        />
        {message && (
          <Message
            negative={message.negative}
            positive={message.positive}
            size="tiny"
          >
            {message.text}
          </Message>
        )}
      </Form>
    </div>
  );
};

export default LinkedOptions;
