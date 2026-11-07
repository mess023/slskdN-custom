// <copyright file="RunHistory.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import { interfaceText } from '../../../lib/interfaceLocale';
import { formatTime, statusColor } from './helpers';
import React from 'react';
import { Button, Header, Icon, Label, Popup, Table } from 'semantic-ui-react';

const RunHistory = ({ runs, loading, onRefresh, onCopy }) => (
  <div className="automation-history-panel">
    <div className="automation-recipe-head">
      <Header as="h4">
        <Icon name="history" />
        <Header.Content>
          {interfaceText('Run history')}
          {runs.length > 0 && (
            <Header.Subheader>
              {interfaceText('Latest {count} runs across all automations', { count: runs.length })}
            </Header.Subheader>
          )}
        </Header.Content>
      </Header>
      <div>
        <Popup
          content={interfaceText('Copy this table as tab-separated text for operator review.')}
          position="top center"
          trigger={
            <Button
              aria-label={interfaceText('Copy automation run history')}
              onClick={onCopy}
              size="small"
            >
              <Icon name="copy" />
              {interfaceText('Copy History')}
            </Button>
          }
        />
        <Popup
          content={interfaceText('Reload the most recent runs from the server.')}
          position="top center"
          trigger={
            <Button
              aria-label={interfaceText('Refresh run history')}
              icon="refresh"
              loading={loading}
              onClick={onRefresh}
              size="small"
            />
          }
        />
      </div>
    </div>
    {runs.length === 0
      ? <p>{interfaceText('No runs recorded yet. Dry runs and scheduled runs both appear here.')}</p>
      : (
        <Table
          celled
          compact
          size="small"
        >
          <Table.Header>
            <Table.Row>
              <Table.HeaderCell>{interfaceText('Time')}</Table.HeaderCell>
              <Table.HeaderCell>{interfaceText('Automation')}</Table.HeaderCell>
              <Table.HeaderCell>{interfaceText('Type')}</Table.HeaderCell>
              <Table.HeaderCell>{interfaceText('Mode')}</Table.HeaderCell>
              <Table.HeaderCell>{interfaceText('Status')}</Table.HeaderCell>
              <Table.HeaderCell>{interfaceText('Summary')}</Table.HeaderCell>
            </Table.Row>
          </Table.Header>
          <Table.Body>
            {runs.map((run) => (
              <Table.Row key={run.id}>
                <Table.Cell>{formatTime(run.startedAt)}</Table.Cell>
                <Table.Cell>{run.automationName}</Table.Cell>
                <Table.Cell>{run.type}</Table.Cell>
                <Table.Cell>
                  {run.dryRun
                    ? (
                      <Popup
                        content={interfaceText('Preview only; nothing was changed.')}
                        trigger={<Label basic color="teal" size="tiny">{interfaceText('dry run')}</Label>}
                      />
                    )
                    : interfaceText('run')}
                </Table.Cell>
                <Table.Cell>
                  <Label
                    color={statusColor(run.status)}
                    size="tiny"
                  >
                    {run.status}
                  </Label>
                </Table.Cell>
                <Table.Cell>{run.summary}</Table.Cell>
              </Table.Row>
            ))}
          </Table.Body>
        </Table>
      )}
  </div>
);

export default RunHistory;
