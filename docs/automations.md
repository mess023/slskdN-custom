# Automations

An automation is a stored recipe: a template type, its parameters, an interval
and an enabled flag. The server runs it on schedule (or on demand) and keeps a
history of runs. Every template supports a **dry run**: it reports what a real
run would do without changing anything (no searches, scans, downloads or
written files). A dry run is recorded in the run history but does not update
the automation's `lastRun*`/`nextRunAt` fields.

## Templates

| type | parameters (default, range) | real run | dry run |
|---|---|---|---|
| `wishlist-retry` | `limit` int (3, 1-50), `includeDisabled` bool (false) | Searches up to `limit` wishlist items one after another, oldest-searched first (`IWishlistService.RunSearchAsync`). Skips disabled items (unless `includeDisabled`), items searched within `wishlist.interval_seconds` (the background wishlist cycle has them) and items whose search is already running. Items with auto-download on queue their downloads at once: there is no approval step. | Lists the items it would search and counts the skipped ones per reason; searches and downloads nothing |
| `library-health-scan` | `libraryPath` path (required, must lie inside a configured share directory) | Starts a Library Health scan of the folder (same service as `POST api/library/health/scans`); `skipped` when a scan is already running | Checks the path and counts its audio files (up to 5000, shown as `5000+`); starts no scan |
| `local-diagnostics` | none | Reports Soulseek connection state, share count and last share scan, whether the downloads folder exists and whether it is writable; the write test creates and deletes an empty `.slskdn-write-probe-<guid>` file in that folder | Same report without the write test: writes nothing, `details.downloads.writable` is `null` |
| `stale-cache-reminders` | `maxShareCacheAgeHours` int (24, 1-8760) | Triggers a share rescan when the last completed scan is older than the threshold or no scan has completed yet; `skipped` when the cache is fresh or a scan is running | Reports the cache age and whether a rescan would be triggered; starts none |

Run status: `succeeded`, `failed` (action threw or a precondition failed, e.g.
missing path, not logged in) or `skipped` (nothing to do, e.g. cache still
fresh, scan already running, no eligible wishlist items, every picked wishlist
item already being searched).

The `wishlist-retry` summary names the skipped items per reason, e.g.
`Would run 1 of 4 wishlist searches; skipped: 1 searched in the last 3600 s, 1 disabled, 1 over the limit of 1`
or `Started 2 of 3 wishlist searches (0 failed); skipped: 1 already being searched`.
`details.skipped` holds the same counts: `recentlySearched`, `disabled`,
`overLimit` and, for real runs, `alreadyRunning`.

A run cancelled because the server shuts down (or because the `Run now`
request was aborted) is not a failure: nothing is recorded and
`lastRunAt`/`nextRunAt` stay as they were, so the scheduler runs it again once
it is due. A cancellation the caller did not ask for (e.g. an internal timeout)
is recorded as `failed`.

## Linked options

`wishlist-retry` lists `linkedOptions` pointing at the related `slskd.yml`
settings. Each entry carries `yamlPath`, `label`, `type`, `default` (the value
used when the key is absent from the yaml), `description` (what the setting
does; the UI shows it as a tooltip) and `min` (the lowest value slskd.yml
accepts; omitted when there is none):

| yamlPath | type | default | min | what it does |
|---|---|---|---|---|
| `wishlist.enabled` | bool | `true` | | Turns the background wishlist cycle on or off; that cycle searches every enabled item in turn. `wishlist-retry` runs either way. |
| `wishlist.interval_seconds` | int | `3600` | `300` | Pause of the background cycle between passes. Once logged in, the cycle uses the wishlist interval the Soulseek server sends instead. `wishlist-retry` always uses this value: it skips items searched within this many seconds. |

`wishlist.auto_download` is not linked: no code reads it. Whether a search
downloads depends only on each wishlist item's own auto-download flag.

## API

Base route `api/automations` (also `api/v0/automations`). Every endpoint,
reads included, requires the Administrator role, like
`POST api/library/health/scans`: runs execute with server rights and run
details show paths and the Soulseek account.

| Method | Route | Body / query | Returns |
|---|---|---|---|
| GET | `/automations` | | `Automation[]` |
| POST | `/automations` | `AutomationRequest` | `Automation` (201) |
| GET | `/automations/{id}` | | `Automation` |
| PUT | `/automations/{id}` | `AutomationRequest` | `Automation` |
| DELETE | `/automations/{id}` | | 204 (also deletes its runs) |
| POST | `/automations/{id}/run?dryRun=true\|false` | | `AutomationRun` (synchronous); 404 when the automation does not exist or was deleted while the run was in progress (that run is not recorded) |
| GET | `/automations/{id}/runs?limit=20` | | `AutomationRun[]`, newest first |
| GET | `/automations/runs?limit=50&since=<ISO-8601>` | `since` optional | `AutomationRun[]` across all automations, newest first; with `since` only runs with `startedAt >= since`. `limit` is clamped to 1-1000 |
| GET | `/automations/templates` | | `AutomationTemplate[]` |

Unknown ids return 404. Validation errors return 400 with `{ "message": "..." }`:
`name` must be 1-100 characters, `type` must be a listed template,
`intervalMinutes` must be 0 (manual) or at least 1, and `parameters` must match
the template's `parameterSchema` (required keys present, correct JSON types, no
unknown keys, `int` values within `min`-`max`, e.g.
`parameters.limit must be between 1 and 50`). A `since` that is not a valid
date/time also returns 400.

```
AutomationRequest { name, type, parameters: { key: value }, intervalMinutes, enabled }
Automation       = AutomationRequest + { id, createdAt, updatedAt, lastRunAt, lastRunStatus, lastRunSummary, nextRunAt }
AutomationRun    { id, automationId, automationName, type, dryRun, startedAt, finishedAt, status, summary, details }
AutomationTemplate { type, title, description, icon, defaultIntervalMinutes, networkImpact, fileImpact, approvalGate,
                     parameterSchema: [{ key, label, type, required, default, description, min, max }],
                     linkedOptions: [{ yamlPath, label, type, default, description, min }] }
```

Fields that are `null` are omitted from API responses (e.g. `min`/`max` of a
`bool` parameter).

`nextRunAt` is `null` when the automation is disabled or manual
(`intervalMinutes = 0`); otherwise it is the last real run (or the last
create/update) plus the interval.

## Storage and scheduling

| What | Where / how |
|---|---|
| Automations and runs | SQLite file `automations.db` in the application directory (next to `wishlist.db`), tables `automations` and `automation_runs` |
| Run history cap | Last 200 runs per automation and 1000 in total; older runs are pruned on every new run |
| Scheduler | `AutomationSchedulerService` starts 15 s after boot and ticks every 30 s; it runs enabled automations with `nextRunAt <= now` one at a time |
| Concurrency | Real runs of the same automation never overlap: a manual run waits for a running scheduled one and vice versa, and a scheduled run whose `nextRunAt` moved meanwhile is dropped. Different automations may run at the same time; dry runs take no lock. Separately, `WishlistService` searches each wishlist item at most once at a time, whoever starts the search (background cycle, `search now`, an automation) |
| Deletion during a run | The run is recorded only while its automation exists (one `INSERT ... WHERE EXISTS`), so deleting an automation mid-run leaves no run behind |

Code lives in `src/slskd/Automations/`, the controller in
`src/slskd/API/Native/AutomationsController.cs`, registration in
`src/slskd/Bootstrap/AutomationServiceCollectionExtensions.cs`.
