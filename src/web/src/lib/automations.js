// <copyright file="automations.js" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import api from './api';

const encode = (id) => encodeURIComponent(id);

export const getTemplates = async () =>
  (await api.get('/automations/templates')).data;

export const getAutomations = async () => (await api.get('/automations')).data;

export const getAutomation = async (id) =>
  (await api.get(`/automations/${encode(id)}`)).data;

export const createAutomation = async (request) =>
  (await api.post('/automations', request)).data;

export const updateAutomation = async (id, request) =>
  (await api.put(`/automations/${encode(id)}`, request)).data;

export const deleteAutomation = async (id) => {
  await api.delete(`/automations/${encode(id)}`);
};

export const runAutomation = async (id, { dryRun = false } = {}) =>
  (await api.post(`/automations/${encode(id)}/run?dryRun=${dryRun ? 'true' : 'false'}`)).data;

export const getRuns = async (id, limit = 20) =>
  (await api.get(`/automations/${encode(id)}/runs?limit=${Number(limit)}`)).data;

// since (ISO-8601, optional): only runs started at or after it.
export const getAllRuns = async (limit = 50, since) =>
  (await api.get(`/automations/runs?limit=${Number(limit)}${since ? `&since=${encodeURIComponent(since)}` : ''}`)).data;
