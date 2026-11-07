// <copyright file="automations.test.js" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import api from './api';
import * as automations from './automations';

vi.mock('./api', () => ({
  __esModule: true,
  default: {
    delete: vi.fn(),
    get: vi.fn(),
    post: vi.fn(),
    put: vi.fn(),
  },
}));

describe('automations', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('reads templates, automations and a single automation', async () => {
    api.get.mockResolvedValue({ data: [{ type: 'wishlist-retry' }] });

    expect(await automations.getTemplates()).toEqual([{ type: 'wishlist-retry' }]);
    expect(api.get).toHaveBeenCalledWith('/automations/templates');

    await automations.getAutomations();
    expect(api.get).toHaveBeenCalledWith('/automations');

    await automations.getAutomation('a/b');
    expect(api.get).toHaveBeenCalledWith('/automations/a%2Fb');
  });

  it('creates and updates with the request body', async () => {
    const request = { enabled: true, intervalMinutes: 60, name: 'x', parameters: { limit: 3 }, type: 'wishlist-retry' };
    api.post.mockResolvedValue({ data: { id: '1', ...request } });
    api.put.mockResolvedValue({ data: { id: '1', ...request } });

    expect(await automations.createAutomation(request)).toEqual({ id: '1', ...request });
    expect(api.post).toHaveBeenCalledWith('/automations', request);

    await automations.updateAutomation('1', request);
    expect(api.put).toHaveBeenCalledWith('/automations/1', request);
  });

  it('deletes by id', async () => {
    api.delete.mockResolvedValue({});

    await automations.deleteAutomation('1');

    expect(api.delete).toHaveBeenCalledWith('/automations/1');
  });

  it('runs with the dryRun query flag and returns the run', async () => {
    api.post.mockResolvedValue({ data: { status: 'succeeded', summary: 'ok' } });

    expect(await automations.runAutomation('1', { dryRun: true })).toEqual({ status: 'succeeded', summary: 'ok' });
    expect(api.post).toHaveBeenCalledWith('/automations/1/run?dryRun=true');

    await automations.runAutomation('1');
    expect(api.post).toHaveBeenCalledWith('/automations/1/run?dryRun=false');
  });

  it('reads run history per automation and across all', async () => {
    api.get.mockResolvedValue({ data: [] });

    await automations.getRuns('1', 5);
    expect(api.get).toHaveBeenCalledWith('/automations/1/runs?limit=5');

    await automations.getAllRuns();
    expect(api.get).toHaveBeenCalledWith('/automations/runs?limit=50');
  });

  it('passes since as an encoded ISO-8601 query value', async () => {
    api.get.mockResolvedValue({ data: [] });

    await automations.getAllRuns(1000, '2026-11-06T12:00:00.000+01:00');

    expect(api.get).toHaveBeenCalledWith('/automations/runs?limit=1000&since=2026-11-06T12%3A00%3A00.000%2B01%3A00');
  });
});
