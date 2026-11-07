// <copyright file="AutomationFormModal.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import { interfaceText } from '../../../lib/interfaceLocale';
import {
  automationErrorText,
  coerceParameters,
  defaultParameters,
  impactColor,
  missingRequired,
  numberProblem,
} from './helpers';
import React, { useState } from 'react';
import { Button, Checkbox, Form, Icon, Label, Message, Modal, Popup } from 'semantic-ui-react';

const initialForm = (automation, templates) => {
  if (automation) {
    const template = templates.find((item) => item.type === automation.type);
    const parameters = { ...defaultParameters(template) };
    for (const [key, value] of Object.entries(automation.parameters || {})) {
      parameters[key] = typeof value === 'boolean' ? value : String(value ?? '');
    }

    return {
      enabled: Boolean(automation.enabled),
      intervalMinutes: String(automation.intervalMinutes ?? 0),
      name: automation.name || '',
      parameters,
      type: automation.type,
    };
  }

  return { enabled: true, intervalMinutes: '0', name: '', parameters: {}, type: '' };
};

const AutomationFormModal = ({ automation, templates, onClose, onSubmit }) => {
  const [form, setForm] = useState(() => initialForm(automation, templates));
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');
  const template = templates.find((item) => item.type === form.type);
  const editing = Boolean(automation);

  const pickTemplate = (next) => {
    setForm((current) => ({
      ...current,
      intervalMinutes: String(next.defaultIntervalMinutes ?? 0),
      name: current.name || interfaceText(next.title),
      parameters: defaultParameters(next),
      type: next.type,
    }));
  };

  const setParameter = (key, value) =>
    setForm((current) => ({ ...current, parameters: { ...current.parameters, [key]: value } }));

  const missing = missingRequired(template, form.parameters);
  const interval = Number(form.intervalMinutes);
  const intervalValid = Number.isInteger(interval) && interval >= 0;
  // Checked against the template's min/max before saving; the server re-checks and answers 400 with its reason.
  const problems = Object.fromEntries(
    (template?.parameterSchema || [])
      .filter((field) => field.type === 'int')
      .map((field) => [field.key, numberProblem(form.parameters[field.key], field)]),
  );
  const fieldsValid = intervalValid && !Object.values(problems).some(Boolean);
  const canSave = Boolean(template) && form.name.trim() !== '' && missing.length === 0 && fieldsValid;

  const submit = async () => {
    setSaving(true);
    setError('');
    try {
      await onSubmit({
        enabled: form.enabled,
        intervalMinutes: interval,
        name: form.name.trim(),
        parameters: coerceParameters(template, form.parameters),
        type: form.type,
      });
      onClose();
    } catch (submitError) {
      setError(automationErrorText(submitError, interfaceText('Save failed.')));
    } finally {
      setSaving(false);
    }
  };

  const renderField = (field) => {
    const value = form.parameters[field.key];
    const name = field.label ? interfaceText(field.label) : field.key;
    const label = `${name}${field.required ? ' *' : ''}`;
    const control = field.type === 'bool'
      ? (
        <Form.Field key={field.key}>
          <Checkbox
            aria-label={name}
            checked={Boolean(value)}
            label={label}
            onChange={(_event, { checked }) => setParameter(field.key, checked)}
            toggle
          />
        </Form.Field>
      )
      : (
        <Form.Input
          aria-label={name}
          error={problems[field.key] || false}
          fluid
          icon={field.type === 'path' ? 'folder open outline' : undefined}
          key={field.key}
          label={label}
          max={field.type === 'int' ? field.max ?? undefined : undefined}
          min={field.type === 'int' ? field.min ?? undefined : undefined}
          onChange={(_event, { value: next }) => setParameter(field.key, next)}
          required={field.required}
          type={field.type === 'int' ? 'number' : 'text'}
          value={value ?? ''}
        />
      );

    return field.description
      ? (
        <Popup
          content={interfaceText(field.description)}
          key={field.key}
          position="top left"
          trigger={<div>{control}</div>}
        />
      )
      : control;
  };

  return (
    <Modal
      aria-modal="true"
      closeIcon
      onClose={onClose}
      open
      role="dialog"
      size="small"
    >
      <Modal.Header>
        {editing ? interfaceText('Edit automation') : interfaceText('New automation')}
      </Modal.Header>
      <Modal.Content>
        <Form>
          {!editing && (
            <Form.Field>
              <label>{interfaceText('Template')}</label>
              <div
                aria-label={interfaceText('Template')}
                className="automation-template-list"
                role="radiogroup"
              >
                {templates.map((item) => (
                  <Popup
                    content={interfaceText(item.description)}
                    key={item.type}
                    position="top left"
                    trigger={
                      <div className="automation-template-option">
                        <Checkbox
                          aria-label={interfaceText(item.title)}
                          checked={form.type === item.type}
                          label={interfaceText(item.title)}
                          onChange={() => pickTemplate(item)}
                          radio
                        />
                        <div className="automation-recipe-labels">
                          <Label
                            basic
                            color={impactColor(item.networkImpact)}
                            size="tiny"
                          >
                            <Icon name="sitemap" />
                            {interfaceText(item.networkImpact)}
                          </Label>
                          <Label
                            basic
                            size="tiny"
                          >
                            <Icon name="file outline" />
                            {interfaceText(item.fileImpact)}
                          </Label>
                          <Label
                            basic
                            size="tiny"
                          >
                            <Icon name="lock" />
                            {interfaceText(item.approvalGate)}
                          </Label>
                        </div>
                      </div>
                    }
                  />
                ))}
              </div>
            </Form.Field>
          )}
          {template && (
            <>
              {editing && (
                <Message
                  info
                  size="small"
                >
                  <Icon name={template.icon} />
                  {interfaceText(template.title)}: {interfaceText(template.description)}
                </Message>
              )}
              <Popup
                content={interfaceText('Display name shown on the card and in run history (1-100 characters).')}
                position="top left"
                trigger={
                  <div>
                    <Form.Input
                      aria-label={interfaceText('Name')}
                      fluid
                      label={interfaceText('Name')}
                      maxLength={100}
                      onChange={(_event, { value }) => setForm((current) => ({ ...current, name: value }))}
                      required
                      value={form.name}
                    />
                  </div>
                }
              />
              <Popup
                content={interfaceText('How often the scheduler runs this automation. 0 means manual only: it never runs on its own, you trigger it with Run now.')}
                position="top left"
                trigger={
                  <div>
                    <Form.Input
                      aria-label={interfaceText('Interval (minutes)')}
                      error={!intervalValid}
                      fluid
                      label={interfaceText('Interval (minutes, 0 = manual)')}
                      min={0}
                      onChange={(_event, { value }) => setForm((current) => ({ ...current, intervalMinutes: value }))}
                      type="number"
                      value={form.intervalMinutes}
                    />
                  </div>
                }
              />
              <Popup
                content={interfaceText('Disabled automations stay listed and can still be run manually, but the scheduler skips them.')}
                position="top left"
                trigger={
                  <Form.Field>
                    <Checkbox
                      aria-label={interfaceText('Enabled')}
                      checked={form.enabled}
                      label={interfaceText('Enabled')}
                      onChange={(_event, { checked }) => setForm((current) => ({ ...current, enabled: checked }))}
                      toggle
                    />
                  </Form.Field>
                }
              />
              {(template.parameterSchema || []).length > 0 && (
                <>
                  <label className="automation-form-section">{interfaceText('Parameters')}</label>
                  {template.parameterSchema.map(renderField)}
                </>
              )}
            </>
          )}
          {error && <Message negative size="small">{error}</Message>}
        </Form>
      </Modal.Content>
      <Modal.Actions>
        <Popup
          content={interfaceText('Close this dialog without saving the automation.')}
          position="top right"
          trigger={
            <span>
              <Button
                disabled={saving}
                onClick={onClose}
              >
                {interfaceText('Cancel')}
              </Button>
            </span>
          }
        />
        <Popup
          content={
            canSave
              ? interfaceText('Save the automation on the server. Enabled automations with an interval start running on schedule.')
              : missing.length > 0
                ? interfaceText('Required: {fields}', { fields: missing.join(', ') })
                : template && !fieldsValid
                  ? interfaceText('Fix the highlighted fields first.')
                  : interfaceText('Pick a template and enter a name first.')
          }
          position="top right"
          trigger={
            <span>
              <Button
                disabled={!canSave || saving}
                loading={saving}
                onClick={submit}
                primary
              >
                {editing ? interfaceText('Save') : interfaceText('Create')}
              </Button>
            </span>
          }
        />
      </Modal.Actions>
    </Modal>
  );
};

export default AutomationFormModal;
