// <copyright file="NewJobModal.test.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import * as jobsLibrary from '../../../lib/jobs';
import NewJobModal from './NewJobModal';
import userEvent from '@testing-library/user-event';
import { render, screen, waitFor } from '@testing-library/react';
import React from 'react';
import { toast } from 'react-toastify';

vi.mock('../../../lib/jobs');
vi.mock('react-toastify', () => ({
  toast: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

const artistMbid = 'a74b1b7f-71a5-4011-9441-d0b5e4122711';
const releaseMbid = 'b84ee12a-09ef-421b-82de-0441a926375b';
const invalidHint = /Not a valid MBID/;

const openModal = async (user, jobTypeText) => {
  render(<NewJobModal />);
  await user.click(screen.getByRole('button', { name: 'New job' }));
  if (jobTypeText) {
    await user.click(screen.getByRole('option', { name: jobTypeText }));
  }
};

describe('NewJobModal', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it('keeps Create disabled with an inline hint until the artist MBID is valid', async () => {
    const user = userEvent.setup();
    await openModal(user);
    const input = screen.getByLabelText('MusicBrainz artist MBID');

    await user.type(input, 'Radiohead');

    expect(screen.getByText(invalidHint)).toBeInTheDocument();
    expect(input).toHaveAttribute('aria-invalid', 'true');
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled();

    await user.clear(input);
    await user.type(input, artistMbid);

    expect(screen.queryByText(invalidHint)).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeEnabled();
  });

  it('validates the release MBID for album jobs', async () => {
    const user = userEvent.setup();
    jobsLibrary.createMbReleaseJob.mockResolvedValue({ job_id: 'album-1', status: 'pending' });
    await openModal(user, 'Album');
    const input = screen.getByLabelText('MusicBrainz release MBID');

    await user.type(input, 'OK Computer');
    expect(screen.getByText(invalidHint)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Create' })).toBeDisabled();

    await user.clear(input);
    await user.type(input, releaseMbid);
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => {
      expect(jobsLibrary.createMbReleaseJob).toHaveBeenCalledWith({ mbReleaseId: releaseMbid, targetDir: '' });
    });
  });

  it('sends a label crate as a label name without MBID validation or target directory', async () => {
    const user = userEvent.setup();
    jobsLibrary.createLabelCrateJob.mockResolvedValue({ job_id: 'crate-1', status: 'pending' });
    await openModal(user, 'Label crate');

    expect(screen.queryByLabelText('Target directory')).not.toBeInTheDocument();
    await user.type(screen.getByLabelText('Label name'), 'Warp Records');
    expect(screen.queryByText(invalidHint)).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Create' }));

    await waitFor(() => {
      expect(jobsLibrary.createLabelCrateJob).toHaveBeenCalledWith({ labelName: 'Warp Records' });
    });
  });

  it('shows the 422 reason inside the dialog and keeps it open', async () => {
    const user = userEvent.setup();
    const reason = `No releases found for artist ${artistMbid}`;
    const error = new Error('Request failed with status code 422');
    error.response = { data: reason, status: 422 };
    jobsLibrary.createDiscographyJob.mockRejectedValue(error);
    await openModal(user);

    await user.type(screen.getByLabelText('MusicBrainz artist MBID'), artistMbid);
    await user.click(screen.getByRole('button', { name: 'Create' }));

    expect(await screen.findByText(reason)).toBeInTheDocument();
    expect(toast.error).not.toHaveBeenCalled();
    expect(toast.success).not.toHaveBeenCalled();
    expect(screen.getByLabelText('MusicBrainz artist MBID')).toHaveValue(artistMbid);
  });

  it('describes what each discography profile selects', async () => {
    const user = userEvent.setup();
    await openModal(user);

    expect(screen.getByRole('option', { name: 'Core discography (all albums, including live albums and compilations)' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'Extended discography (albums and EPs)' })).toBeInTheDocument();
    expect(screen.getByRole('option', { name: 'All releases (every type, including singles)' })).toBeInTheDocument();
    expect(screen.getByLabelText('Target directory')).toHaveAttribute('placeholder', 'Default downloads directory');
  });
});
