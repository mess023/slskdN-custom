# System Admin Surfaces

This guide maps the current System area to the operator work it supports.
Advanced features should be controlled from these guided surfaces where
possible, with raw YAML left as the escape hatch.

The System tab menu labels admin and experimental panels directly. Keep those
labels current when a panel changes maturity or starts exposing privileged
actions.

## Start Here

- **System -> Info**: daemon status, version, pending restart/reconnect signals,
  setup health, and diagnostic bundles.
- **System -> Options**: raw YAML view/edit when remote configuration is enabled.
- **System -> Logs / Events / Metrics / Jobs**: operational history and current
  background work.

## Job execution

Discography, album and label-crate jobs (`POST api/jobs/discography`,
`POST api/jobs/mb-release`, `POST api/jobs/label-crate`, scene jobs) are only
plans: a job id plus one release row per release. An album job is a
discography job with one release.

Creation refuses a job that would have nothing to do: it answers 422 with a
plain-text reason and stores nothing when no release resolves (no MusicBrainz
releases for the artist and profile, an unknown release MBID, a label name
that is not in the local cache) or when `target_dir` is not allowed. Label
crates match label **names** of releases slskdN already resolved through
MusicBrainz (the local AlbumTargets cache); there is no MusicBrainz label
lookup, so a label MBID matches nothing.

`target_dir` (discography and album jobs): empty means the default download
directory, a relative path goes under it, and an absolute path must lie inside
`directories.downloads` or a `destinations.folders` path. The job stores the
normalized absolute path.

`JobExecutorService` runs the jobs. Every 30 seconds it takes every pending or
running job and advances each release one step:

- **Pending release**: resolve artist + title (local AlbumTargets first, else
  MusicBrainz at most once per 1.1 s; the answer is cached in AlbumTargets),
  create a Wishlist item for this release, then mark the release running. The
  search text is artist + title with everything except letters and digits
  replaced by spaces; when two releases of one job get the same text, both get
  the release year appended, also when the second one resolves in a later
  cycle (for example after a transient MusicBrainz error): the earlier item is
  then renamed too, unless it already downloaded something. Releases with the
  same text and year still search identically. The item has auto-download on and no filter of
  its own (`wishlist.filter` decides the formats), carries
  `WishlistItem.JobId = "<jobId>:<releaseId>"` and the job's target directory
  as `DestinationDirectory`, so its auto-downloads land in
  `<target>/<remote folder>/`. An existing item with that key is reused while
  it is enabled or has downloaded; a user's own items are never touched. A
  release without artist/title metadata fails.
- **Running release**: completed once its item has queued at least one
  download (`wishlist.max_downloads`, when set, also caps album items). Failed when the
  item was deleted, or disabled by hand before anything downloaded, or after 5
  searches without a download; that last check waits until the latest search
  is at least 2 minutes old and re-reads the item first, then the executor
  disables the item. Running releases never query MusicBrainz.
- **Wishlist off**: with `wishlist.enabled: false` the items are not searched,
  so releases stay running. The executor logs one warning, and warns again
  only after the wishlist was switched back on and off.
- **Errors**: an error in a release step keeps the release's status and the
  next cycle retries it; after 10 errors in a row the release fails and its
  item is disabled. Other releases and jobs carry on.
- A job without releases (planned before creation refused them) is reported
  failed; the executor logs why.

What the user sees: **System -> Jobs** moves from pending to running on the
first cycle and to completed/failed per release (the job status is derived
from its releases); **Wishlist** gains one item per release, which the normal
wishlist loop searches and downloads. **New job** takes an artist MBID
(discography), a release MBID (album) or a label name (label crate). Create
stays disabled until the MBID is valid, and a refused job shows the server's
reason inside the dialog. Discography profiles pick release groups by
MusicBrainz primary type only, one release per group: Core = every album
(live albums and compilations included), Extended = albums and EPs, All =
every type including singles.

## Guided Flow Map

Use these current surfaces for the G5 guided-flow tracks before adding another
page:

- **Network Health**: System -> Network gives a local readiness score,
  connectivity diagnostics, DHT exposure notices, and a copyable report without
  contacting peers.
- **Improve Downloads**: System -> Swarm Analytics, System -> Source Providers,
  and Transfers expose multisource/rescue posture, provider priority, and active
  transfer state. Keep live acquisition actions explicit.
- **Discover Music**: Search, Wishlist, Discovery Graph, System -> Source
  Providers, and System -> MediaCore cover manual discovery, background search,
  provider capability review, and pod/native discovery checks.
- **Share Collection**: Collections and System -> Shares cover collection share
  actions and local share-scan posture; System -> Library Health covers
  review-first repair/export work.
- **Play Local Files**: the persistent player, collection play actions, and
  System -> Library Health expose local playback and library readiness.
- **Join Listening Party**: Messages/pod routes and the persistent player cover
  current listen-along entry points; keep new broadcast features opt-in and
  privacy-reviewed.

## Policies

**System -> Policies** is the guided YAML editor for operator settings that used
to be easy to miss.

Global download exclusions are primarily managed from **Download filters** in
the Downloads header, where users are already reviewing transfers. The Policies
panel retains the same editor as an advanced operator/YAML surface.

It covers:

- Webhooks and scripts: names, events, target URL/command, timeout, retry count,
  and certificate-error posture.
- Transfer policy: upload/download slots, speed ceilings, direct retry policy,
  auto-replace, scheduled-limit enablement, and the advanced global download
  exclusion editor.
- Security and access: auth disablement, explicit no-auth CIDRs, JWT key/TTL,
  API keys, HTTPS certificate settings, force HTTPS, and HTTP rate limits.
- Search/network policy: incoming search filters, search throttles, managed
  blacklist, DHT enablement, LAN-only mode, bootstrap routers, Scene Pod Bridge,
  and rescue mode.
- Retention/storage: search/event/log retention, transfer/file history retention,
  share cache workers/retention, and optional share media-attribute probing.

The panel writes YAML only. It does not test hooks, run scripts, contact peers,
restart the daemon, mutate transfers, validate provider credentials, or modify
files.

## Experience

**System -> Experience** stores browser-local preferences that page-specific
surfaces can consume.

It covers:

- Search ranking profile, preferred condition, duplicate folding, and action
  preview density.
- Discovery filters, confidence floor, stale age, and evidence-detail
  preference.
- Player queue, radio, ratings, history, scrobble, visualizer, and keyboard
  posture.
- Messages dense mode, pinned restore, unread badges, user filtering, and local
  search preference.

These preferences are stored in browser `localStorage`. Saving them does not
change daemon configuration or execute any network/file action.

## Integrations

**System -> Integrations** groups provider and destination setup:

- VPN status and port-forwarding visibility.
- Lidarr status, wanted sync, path mapping, and safe manual-import handoff.
- Metadata provider settings for Chromaprint, AcoustID, MusicBrainz, and Lidarr.
- Live and bounded recent metadata-processing activity for HashDb hashing,
  Chromaprint, AcoustID, MusicBrainz, and optional auto-tagging. The surface
  shows only the downloaded file basename, stage, timing, result, and concise
  failure/skip reason; raw fingerprints and full local paths are not exposed.
- Notification providers: Pushbullet, Ntfy, and Pushover.
- Source-feed imports: Spotify, YouTube, and Last.fm.
- FTP completed-download upload settings.
- Servarr readiness and media-server execution contracts for Plex,
  Jellyfin/Emby, and Navidrome.
- Federation diagnostics for ActivityPub and pod-signing posture.

Runtime apply actions are explicit. Provider credential checks and import/sync
actions should remain user-triggered and visibly rate-limited.

See [Federation Diagnostics](federation-diagnostics.md) for the read-only checks
and privacy posture.

## Legacy Client Bridge

**System -> Bridge** configures and controls the optional local legacy-client
bridge. Its dashboard refreshes every ten seconds only while visible, rejects
overlapping slow requests, retains the last successful snapshot after transient
failures, and ignores uptime-only response changes that do not affect the
rendered controls or statistics. Start, stop, and save remain explicit operator
actions.

## Source Providers

**System -> Source Providers** is the acquisition provider catalog. It shows
provider registration, active/disabled state, risk level, capabilities,
network policy, disabled reasons, and acquisition-profile priority chains.
**Needs Config** marks a provider that is enabled but stays inactive because
configuration is missing (the card shows the reason); it only appears while
acquisition planning is on. **Ready** counts the registered, enabled providers
that do not need configuration.

With `remote_configuration: true` each card has an **Enabled** toggle and a
**Settings** dialog; both edit the provider's section in `slskd.yml` (see
below). Enabling a `high-risk` provider asks for confirmation first. After a
toggle or **Save settings** the page waits for the server to reload the
configuration: it polls for about 3 s (5 tries, 600 ms apart) until the server
holds the new values, then refreshes the cards. If the server does not reload
the file in that time (for example with `flags.no_config_watch`), it warns
instead. A stored secret (`secret_key`, `password`, `bearer_token`) is never
shown: a blank field keeps it, and **Clear stored value** removes it from
`slskd.yml` on save. Without `remote_configuration`, **Settings** opens
read-only with the values slskd uses now (from `GET /api/options`), and the
toggles are disabled.

The catalog itself is observational. It does not start searches, browse
peers, probe DHT, download files, or validate credentials.

Each provider is bound to its own section under `virtual_soulfind_v2.backends`
in `slskd.yml` (the API reports the path as `configKey`). Changes apply live
when the file is saved; no restart is needed unless `flags.no_config_watch`
is set (then the API reports `restartRequired: true`). Exception: torrent
`private_only` and `disable_dht` take effect after a restart, because the
torrent engine sets up its DHT node once. The API's `restartRequired` does not
cover these two; the Settings dialog marks them **Restart required**.

`active` means: v2 enabled, provider registered, `enabled: true`, and the
minimum configuration present:

| Provider | Minimum configuration |
|---|---|
| HTTP, WebDAV | a non-empty `domain_allowlist` |
| S3 | `endpoint` or `region`, plus a non-empty `bucket_allowlist` |
| Torrent | with `private_only: true`: a valid `host:port` in `invite_list` and `allowed_peer_sources` `InviteList` or `Both` |
| Local library, Soulseek, native mesh, mesh DHT, LAN | nothing extra |

An empty allowlist admits nothing: `domain_allowlist` for HTTP and WebDAV,
`bucket_allowlist` for S3. `http.require_https: true` rejects plain `http://`
URLs when candidates are found, validated, and fetched.
`allowed_peer_sources` accepts `Overlay`, `InviteList`, or `Both` (case is
ignored). `password`, `bearer_token`, and `secret_key` are redacted in API
output.

The example lists the keys the backends use. `soulseek.max_searches_per_minute`,
`soulseek.max_parallel_searches`, `soulseek.max_browses_per_minute`,
`http.max_requests_per_minute`, `http.request_timeout_ms`, and the `mesh`
section are still accepted but not used yet (see [config.md](config.md)); the
Settings dialog does not offer them.

```yaml
virtual_soulfind_v2:
  enabled: true
  backends:
    local_library:
      enabled: true
    soulseek:
      enabled: true
      min_upload_speed_bytes_per_sec: 51200
      search_timeout_ms: 10000
    native_mesh:
      enabled: false
      min_trust_score: 0.3
      max_candidates_per_item: 20
    mesh_dht:
      enabled: false
      min_trust_score: 0.3
      max_candidates_per_item: 20
    http:
      enabled: false
      domain_allowlist: []          # e.g. [files.example.org]
      max_file_size_bytes: 500000000
      validation_timeout_seconds: 10
      require_https: false
    webdav:
      enabled: false
      domain_allowlist: []
      max_file_size_bytes: 500000000
      validation_timeout_seconds: 15
      username: ""
      password: ""
      bearer_token: ""
    s3:
      enabled: false
      endpoint: ""                  # empty = AWS S3 (then set region)
      region: ""
      access_key: ""
      secret_key: ""
      force_path_style: true
      bucket_allowlist: []
      max_file_size_bytes: 500000000
      validation_timeout_seconds: 15
    lan:
      enabled: false
      allowed_networks: []          # empty = 192.168.0.0/16, 10.0.0.0/8, 172.16.0.0/12
      allowed_hostnames: []
      max_candidates_per_item: 10
    torrent:
      enabled: false
      min_seeders: 3
      private_only: true
      disable_dht: true
      disable_pex: true
      allowed_peer_sources: Both
      invite_list: []
```

## Automation Center

**System -> Automations** manages the server-side automations. Every call
needs the Administrator role; other users see "Administrator role required.".
Each card shows the template, interval, next run, impact labels, last run and
related `slskd.yml` settings, with an enable toggle and **Dry run**, **Run
now**, **Edit** and **Delete**. A card shows the newer of two runs: the
server's last real run and the run started from this page. A dry-run result
carries a **Dry run** label until the server records a newer real run.
**Failed (24h)** counts failed runs, dry runs included, that started in the
last 24 hours; it reads up to 1000 runs and shows "≥ N" when it hits that
limit, or when one automation has 200 or more runs in the window (the server
keeps only the latest 200 runs per automation, so older failures may already
be gone). The run history table lists the latest 50 runs across all automations;
its Refresh also reloads the cards.

**Related slskd.yml settings** show each setting's effective value: the value
slskd runs with now. When that differs from `slskd.yml` (an environment
variable, a command-line argument, a runtime override such as the Wishlist
page's auto-search toggle, or a saved change that is not applied yet), the
field is locked with a warning, because editing `slskd.yml` would have no
effect. A locked field is never written on Save, even when it holds an
unsaved edit from before it locked. Number fields enforce the server's minimum (e.g.
`wishlist.interval_seconds` >= 300), the create/edit dialog enforces each
template parameter's min/max, and a rejected save shows the server's reason.
Template titles, descriptions, impact labels, parameter labels and the
interval label are translated through the locale catalogs.

The server side (templates, API, scheduler, storage) is documented in
[automations.md](automations.md).

## Security

**System -> Security** shows runtime security posture and advanced adversarial
settings. Use **System -> Policies** for auth/API-key/HTTPS/rate-limit YAML
changes; use **System -> Security** for live security dashboards and specialized
privacy/anonymity controls. Its local dashboard snapshot refreshes every 30
seconds only while visible, does not overlap slow requests, retains the last
successful result across transient failures, and mounts only the selected
Status or Adversarial pane.

## Network And Mesh

Use:

- **System -> Network** for Soulseek, public BitTorrent DHT rendezvous, mesh-DHT,
  and mesh-transport health and public exposure notes.
- **System -> Mesh** for mesh evidence, realm subject-index review, and
  conflict surfaces. It also includes Soulseek mesh rendezvous controls for the
  opt-in `slskdn-mesh-v1` public interest tag and runtime peer capability
  records discovered from signed slskdN descriptors. Transport status reads
  are passive: they report the last NAT result collected by mesh startup,
  descriptor refresh, or the explicit NAT-detection action without launching
  STUN probes from dashboard polling.
- **System -> Swarm Analytics** for multi-source/rescue performance review.

## Library Health

**System -> Library Health** surfaces scan status, issue review, reports,
replacement search seeds, quarantine review packets, and safe-fix manifests.
Exports are review-first and do not create remediation jobs or mutate files by
themselves.

This scan status is for Library Health jobs. It does not currently expose the
per-file pipeline that runs after an audio download: HashDb hashing,
Chromaprint extraction, AcoustID lookup, MusicBrainz recording lookup, and
optional auto-tagging. Those stages are currently observable only through
logs; successful hash storage and AcoustID resolution are logged, while some
fingerprint details are debug-level and raw fingerprints are intentionally not
shown. A bounded provider activity/history surface is tracked follow-up work.
