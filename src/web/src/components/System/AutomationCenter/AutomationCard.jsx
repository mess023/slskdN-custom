// <copyright file="AutomationCard.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import { interfaceText } from '../../../lib/interfaceLocale';
import { formatInterval, formatTime, impactColor, newestRun, statusColor } from './helpers';
import LinkedOptions from './LinkedOptions';
import React from 'react';
import { Button, Card, Checkbox, Header, Icon, Label, Popup } from 'semantic-ui-react';

const itemName = (item) =>
  typeof item === 'string'
    ? item
    : item?.name || item?.title || item?.searchText || item?.path || JSON.stringify(item);

const RunDetails = ({ details }) => {
  if (!details || (typeof details === 'object' && Object.keys(details).length === 0)) return null;
  const items = Array.isArray(details.items) ? details.items : null;

  return (
    <details className="automation-run-details">
      <summary>{interfaceText('Details')}</summary>
      {items
        ? (
          <ul>
            {items.map((item, index) => <li key={index}>{itemName(item)}</li>)}
          </ul>
        )
        : <pre>{JSON.stringify(details, null, 2)}</pre>}
    </details>
  );
};

const AutomationCard = ({
  automation,
  template,
  busy,
  lastRun,
  remoteConfiguration,
  runningOptions,
  yamlDocument,
  onToggle,
  onRun,
  onEdit,
  onDelete,
  onYamlSaved,
}) => {
  const { name } = automation;
  const title = template ? interfaceText(template.title) : automation.type;
  const shown = newestRun(automation, lastRun);
  const shownAt = shown?.finishedAt || shown?.startedAt;

  return (
    <Card className="automation-recipe-card">
      <Card.Content>
        <div className="automation-recipe-head">
          <Header
            as="h4"
            className="automation-recipe-title"
          >
            <Icon name={template?.icon || 'magic'} />
            <Header.Content>
              {name}
              <Header.Subheader>{title}</Header.Subheader>
            </Header.Content>
          </Header>
          <Popup
            content={interfaceText(automation.enabled
              ? 'Disable {name}. The scheduler stops running it; manual runs still work.'
              : 'Enable {name}. The scheduler runs it on its interval.', { name })}
            position="top center"
            trigger={
              <Checkbox
                aria-label={interfaceText(automation.enabled ? 'Disable {name}' : 'Enable {name}', { name })}
                checked={Boolean(automation.enabled)}
                disabled={busy}
                onChange={(_event, { checked }) => onToggle(automation, checked)}
                toggle
              />
            }
          />
        </div>
        {template?.description && <Card.Description>{interfaceText(template.description)}</Card.Description>}
        <div className="automation-recipe-labels">
          <Popup
            content={interfaceText('How often the scheduler runs this automation.')}
            trigger={
              <Label basic>
                <Icon name="clock outline" />
                {formatInterval(automation.intervalMinutes)}
              </Label>
            }
          />
          {automation.nextRunAt && (
            <Popup
              content={interfaceText('Next scheduled run.')}
              trigger={
                <Label basic>
                  <Icon name="hourglass half" />
                  {interfaceText('Next')} {formatTime(automation.nextRunAt)}
                </Label>
              }
            />
          )}
          {template && (
            <>
              <Label
                basic
                color={impactColor(template.networkImpact)}
              >
                <Icon name="sitemap" />
                {interfaceText(template.networkImpact)}
              </Label>
              <Label basic>
                <Icon name="file outline" />
                {interfaceText(template.fileImpact)}
              </Label>
              <Label basic>
                <Icon name="lock" />
                {interfaceText(template.approvalGate)}
              </Label>
            </>
          )}
        </div>
        <div className="automation-last-run">
          {shown?.dryRun && (
            <Popup
              content={interfaceText('Preview only; nothing was changed.')}
              trigger={
                <Label
                  basic
                  color="teal"
                  size="small"
                >
                  <Icon name="eye" />
                  {interfaceText('Dry run')}
                </Label>
              }
            />
          )}
          <Popup
            content={interfaceText('Status and summary of the most recent run.')}
            trigger={
              <Label
                basic
                color={shown?.status ? statusColor(shown.status) : 'grey'}
                size="small"
              >
                {shown?.status || interfaceText('Not run yet')}
                {shownAt && <Label.Detail>{formatTime(shownAt)}</Label.Detail>}
              </Label>
            }
          />
          {shown?.summary && <p className="automation-run-summary">{shown.summary}</p>}
          <RunDetails details={shown?.details} />
        </div>
        {template?.linkedOptions?.length > 0 && (
          <LinkedOptions
            linkedOptions={template.linkedOptions}
            onSaved={onYamlSaved}
            remoteConfiguration={remoteConfiguration}
            runningOptions={runningOptions}
            yamlDocument={yamlDocument}
          />
        )}
      </Card.Content>
      <Card.Content extra>
        <div className="automation-recipe-actions">
          <Button.Group size="small">
            <Popup
              content={interfaceText('Preview what {name} would do without changing anything. The result shows on the card.', { name })}
              position="top center"
              trigger={
                <Button
                  aria-label={interfaceText('Dry run {name}', { name })}
                  basic
                  disabled={busy}
                  onClick={() => onRun(automation, true)}
                >
                  <Icon name="eye" />
                  {interfaceText('Dry run')}
                </Button>
              }
            />
            <Popup
              content={interfaceText('Execute {name} now through its real backend action, regardless of its schedule.', { name })}
              position="top center"
              trigger={
                <Button
                  aria-label={interfaceText('Run {name} now', { name })}
                  disabled={busy}
                  loading={busy}
                  onClick={() => onRun(automation, false)}
                  primary
                >
                  <Icon name="bolt" />
                  {interfaceText('Run now')}
                </Button>
              }
            />
          </Button.Group>
          <Button.Group size="small">
            <Popup
              content={interfaceText('Change name, interval or parameters.')}
              position="top center"
              trigger={
                <Button
                  aria-label={interfaceText('Edit {name}', { name })}
                  basic
                  disabled={busy}
                  icon="pencil"
                  onClick={() => onEdit(automation)}
                />
              }
            />
            <Popup
              content={interfaceText('Delete {name} and its run history. Asks for confirmation.', { name })}
              position="top center"
              trigger={
                <Button
                  aria-label={interfaceText('Delete {name}', { name })}
                  basic
                  color="red"
                  disabled={busy}
                  icon="trash"
                  onClick={() => onDelete(automation)}
                />
              }
            />
          </Button.Group>
        </div>
      </Card.Content>
    </Card>
  );
};

export default AutomationCard;
