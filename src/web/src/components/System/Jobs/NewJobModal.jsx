// <copyright file="NewJobModal.jsx" company="slskdN Team">
// Copyright (c) slskdN Team. All rights reserved.
// </copyright>

import LocalizedText from '../../Shared/LocalizedText';
import { interfaceText } from '../../../lib/interfaceLocale';
import * as jobsLibrary from '../../../lib/jobs';
import React, { useState } from 'react';
import { toast } from 'react-toastify';
import { Button, Form, Icon, Message, Modal, Popup } from 'semantic-ui-react';

const MBID_PATTERN = /^[\da-f]{8}-[\da-f]{4}-[\da-f]{4}-[\da-f]{4}-[\da-f]{12}$/iu;

// Built per render with literal interfaceText calls: translated in the active language and
// visible to the locale coverage test.
const jobTypes = () => ({
  discography: {
    create: ({ id, profile, targetDirectory }) =>
      jobsLibrary.createDiscographyJob({ artistId: id, profile, targetDirectory }),
    hasTargetDirectory: true,
    idHelp: interfaceText('MusicBrainz artist MBID, e.g. from the artist page URL (musicbrainz.org/artist/<MBID>).'),
    idLabel: interfaceText('MusicBrainz artist MBID'),
    isMbid: true,
    text: interfaceText('Discography'),
  },
  album: {
    create: ({ id, targetDirectory }) =>
      jobsLibrary.createMbReleaseJob({ mbReleaseId: id, targetDir: targetDirectory }),
    hasTargetDirectory: true,
    idHelp: interfaceText('MusicBrainz release MBID, e.g. from the release page URL (musicbrainz.org/release/<MBID>).'),
    idLabel: interfaceText('MusicBrainz release MBID'),
    isMbid: true,
    text: interfaceText('Album'),
  },
  label_crate: {
    create: ({ id }) => jobsLibrary.createLabelCrateJob({ labelName: id }),
    hasTargetDirectory: false,
    idHelp: interfaceText('Record label name, e.g. "Warp Records"; only releases already in the local MusicBrainz cache are used.'),
    idLabel: interfaceText('Label name'),
    isMbid: false,
    text: interfaceText('Label crate'),
  },
});

// The backend maps only the MusicBrainz primary type (Album, EP, Single, other), so live albums
// and compilations count as albums.
const profileOptions = () => [
  { key: 'CoreDiscography', text: interfaceText('Core discography (all albums, including live albums and compilations)'), value: 'CoreDiscography' },
  { key: 'ExtendedDiscography', text: interfaceText('Extended discography (albums and EPs)'), value: 'ExtendedDiscography' },
  { key: 'AllReleases', text: interfaceText('All releases (every type, including singles)'), value: 'AllReleases' },
];

// The job endpoints answer 422 with a plain-text reason (no releases resolved, target directory
// not allowed); show it as-is.
const errorText = (error) => {
  const data = error?.response?.data;
  return typeof data === 'string' && data.trim() ? data : interfaceText('Failed to create job');
};

const NewJobModal = ({ onCreated }) => {
  const [open, setOpen] = useState(false);
  const [type, setType] = useState('discography');
  const [id, setId] = useState('');
  const [profile, setProfile] = useState('CoreDiscography');
  const [targetDirectory, setTargetDirectory] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState('');
  const types = jobTypes();
  const jobType = types[type];
  const trimmedId = id.trim();
  const idInvalid = jobType.isMbid && trimmedId !== '' && !MBID_PATTERN.test(trimmedId);

  const close = () => {
    setOpen(false);
    setId('');
    setTargetDirectory('');
    setError('');
  };

  const create = async () => {
    setSubmitting(true);
    setError('');
    try {
      const response = await jobType.create({
        id: trimmedId,
        profile,
        targetDirectory: targetDirectory.trim(),
      });
      toast.success(interfaceText('Planned job {job_id}', { job_id: response?.job_id }));
      close();
      onCreated?.();
    } catch (createError) {
      console.error(createError);
      setError(errorText(createError));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <>
      <Popup
        content={interfaceText('Plan a new download job: a whole discography, a single album, or a label crate.')}
        position="top center"
        trigger={(
          <Button
            aria-label={interfaceText('New job')}
            icon
            onClick={() => setOpen(true)}
            primary
          >
            <Icon name="plus" />
            <LocalizedText source={'New job'} />
          </Button>
        )}
      />
      <Modal
        onClose={close}
        open={open}
        size="small"
      >
        <Modal.Header><LocalizedText source={'New job'} /></Modal.Header>
        <Modal.Content>
          <Form>
            <Popup
              content={interfaceText('Discography: every release of an artist matching the profile. Album: one MusicBrainz release. Label crate: a batch of releases from one record label.')}
              position="top left"
              trigger={(
                <Form.Select
                  label={interfaceText('Job type')}
                  onChange={(_event, { value }) => {
                    setType(value);
                    setId('');
                    setError('');
                  }}
                  options={Object.entries(types).map(([key, { text }]) => ({ key, text, value: key }))}
                  value={type}
                />
              )}
            />
            <Popup
              content={jobType.idHelp}
              position="top left"
              trigger={(
                <Form.Input
                  aria-label={jobType.idLabel}
                  autoFocus
                  error={idInvalid && {
                    content: interfaceText('Not a valid MBID: copy the 36-character ID from the MusicBrainz page URL.'),
                  }}
                  id="new-job-id"
                  label={jobType.idLabel}
                  onChange={(_event, { value }) => setId(value)}
                  required
                  value={id}
                />
              )}
            />
            {type === 'discography' && (
              <Popup
                content={interfaceText('Picks release groups by their MusicBrainz primary type (secondary types such as live or compilation are not checked) and plans one release from each group.')}
                position="top left"
                trigger={(
                  <Form.Select
                    label={interfaceText('Profile')}
                    onChange={(_event, { value }) => setProfile(value)}
                    options={profileOptions()}
                    value={profile}
                  />
                )}
              />
            )}
            {jobType.hasTargetDirectory && (
              <Popup
                content={interfaceText('Downloads for this job land in this directory, which must be inside the downloads directory or another allowed destination root; empty means the default downloads directory.')}
                position="top left"
                trigger={(
                  <Form.Input
                    aria-label={interfaceText('Target directory')}
                    label={interfaceText('Target directory (optional)')}
                    onChange={(_event, { value }) => setTargetDirectory(value)}
                    placeholder={interfaceText('Default downloads directory')}
                    value={targetDirectory}
                  />
                )}
              />
            )}
            {error && (
              <Message
                negative
                role="alert"
                size="small"
              >
                {error}
              </Message>
            )}
          </Form>
        </Modal.Content>
        <Modal.Actions>
          <Popup
            content={interfaceText('Close the dialog without creating a job.')}
            position="top center"
            trigger={<Button onClick={close}><LocalizedText source={'Cancel'} /></Button>}
          />
          <Popup
            content={interfaceText('Plan the job; each release then becomes an auto-download wishlist item.')}
            position="top right"
            trigger={(
              <span>
                <Button
                  disabled={!trimmedId || idInvalid || submitting}
                  loading={submitting}
                  onClick={create}
                  primary
                >
                  <LocalizedText source={'Create'} />
                </Button>
              </span>
            )}
          />
        </Modal.Actions>
      </Modal>
    </>
  );
};

export default NewJobModal;
