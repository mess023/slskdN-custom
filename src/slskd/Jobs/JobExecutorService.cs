// <copyright file="JobExecutorService.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Hosting;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using slskd.Destinations;
    using slskd.HashDb;
    using slskd.Integrations.MusicBrainz;
    using slskd.Wishlist;

    /// <summary>
    ///     Drives discography and label-crate jobs by handing each release to the Wishlist.
    ///     Pending release -> its own wishlist item (auto-download, no item filter so wishlist.filter decides the formats,
    ///     <see cref="WishlistItem.JobId"/> = "&lt;jobId&gt;:&lt;releaseId&gt;", the job's target directory) -> Running.
    ///     Running release -> Completed once its item enqueued a download; Failed after <see cref="MaxSearchAttempts"/>
    ///     searches without one (judged <see cref="SearchSettleTime"/> after the last search), or when the user deleted or
    ///     disabled the item. A release whose step throws keeps its status and is retried next cycle; after
    ///     <see cref="MaxConsecutiveErrors"/> errors in a row it fails and its item is disabled.
    ///     Items without an executor key (the user's own) are never reused or modified.
    /// </summary>
    public class JobExecutorService : BackgroundService
    {
        internal const int MaxSearchAttempts = 5;
        internal const int MaxConsecutiveErrors = 10;
        internal static readonly TimeSpan SearchSettleTime = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan MusicBrainzSpacing = TimeSpan.FromSeconds(1.1); // MusicBrainz allows ~1 request/s per IP
        private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

        private readonly IHashDbService hashDb;
        private readonly IDiscographyJobService discographyJobs;
        private readonly ILabelCrateJobService labelCrateJobs;
        private readonly IWishlistService wishlist;
        private readonly IMusicBrainzClient musicBrainz;
        private readonly IOptionsMonitor<slskd.Options> options;
        private readonly ILogger<JobExecutorService> log;

        // ponytail: in-memory error streak per "<jobId>:<releaseId>"; a restart resets it, which only delays giving up.
        private readonly Dictionary<string, int> errors = new();
        private DateTime lastMusicBrainzRequest = DateTime.MinValue;
        private bool warnedWishlistDisabled;

        public JobExecutorService(
            IHashDbService hashDb,
            IDiscographyJobService discographyJobs,
            ILabelCrateJobService labelCrateJobs,
            IWishlistService wishlist,
            IMusicBrainzClient musicBrainz,
            IOptionsMonitor<slskd.Options> options,
            ILogger<JobExecutorService> log)
        {
            this.hashDb = hashDb;
            this.discographyJobs = discographyJobs;
            this.labelCrateJobs = labelCrateJobs;
            this.wishlist = wishlist;
            this.musicBrainz = musicBrainz;
            this.options = options;
            this.log = log;
        }

        /// <summary>
        ///     Every run of characters that are not letters or digits becomes one space, so typographic quotes,
        ///     apostrophes, dashes, '/' and ':' never end up as search terms ("AC/DC – Back in Black" -> "AC DC Back in Black").
        /// </summary>
        internal static string NormalizeSearchText(string text) => Regex.Replace(text.Normalize(), @"[^\p{L}\p{N}]+", " ").Trim();

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            log.LogInformation("[JobExecutor] started (interval {Interval}s)", Interval.TotalSeconds);

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "[JobExecutor] cycle failed: {Message}", ex.Message);
                    }

                    await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }

            log.LogInformation("[JobExecutor] stopped");
        }

        /// <summary>
        ///     Advances every pending/running release of every pending/running job by one step.
        /// </summary>
        internal async Task RunOnceAsync(CancellationToken ct)
        {
            if (options.CurrentValue.Wishlist.Enabled)
            {
                warnedWishlistDisabled = false; // warn again if it gets disabled later
            }

            Dictionary<string, WishlistItem>? items = null; // loaded once, on the first job
            foreach (var status in new[] { "pending", "running" })
            {
                // ponytail: one page of 200 jobs per status per cycle; paginate when operators queue more than that.
                var page = await hashDb.GetJobListPageAsync(null, status, 200, 0, "created_at", descending: false, ct).ConfigureAwait(false);
                foreach (var job in page.Items)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!warnedWishlistDisabled && !options.CurrentValue.Wishlist.Enabled)
                    {
                        warnedWishlistDisabled = true;
                        log.LogWarning("[JobExecutor] wishlist.enabled is false: job releases stay running until the wishlist searches them");
                    }

                    try
                    {
                        items ??= await LoadExecutorItemsAsync().ConfigureAwait(false);
                        await ProcessJobAsync(job, items, ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        log.LogError(ex, "[JobExecutor] job {JobId} ({Type}) failed: {Message}", job.Id, job.Type, ex.Message);
                    }
                }
            }
        }

        private static string Key(string jobId, string releaseId) => $"{jobId}:{releaseId}";

        /// <summary>
        ///     A live item, or one that already downloaded, is reused; a dead one (disabled, nothing downloaded) is replaced.
        /// </summary>
        private static bool IsUsable(WishlistItem? item) => item != null && (item.Enabled || item.TotalDownloadCount > 0);

        private static string Year(string? date) => date?.Length >= 4 && date[..4].All(char.IsDigit) ? date[..4] : string.Empty;

        private static bool SameText(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        ///     True when <paramref name="itemText"/> is <paramref name="text"/>, with or without a year that an earlier cycle appended.
        /// </summary>
        private static bool SameAlbum(string itemText, string text)
            => SameText(itemText, text) || (itemText.Length == text.Length + 5 && itemText.StartsWith(text + " ", StringComparison.OrdinalIgnoreCase) && itemText[^4..].All(char.IsDigit));

        /// <summary>
        ///     The executor's items by key; the newest wins when a key occurs twice.
        /// </summary>
        private async Task<Dictionary<string, WishlistItem>> LoadExecutorItemsAsync()
        {
            // ponytail: full wishlist list once per cycle; add a query by JobId if wishlists grow to tens of thousands.
            return (await wishlist.ListAsync().ConfigureAwait(false))
                .Where(i => i.JobId != null)
                .GroupBy(i => i.JobId!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.CreatedAt).First(), StringComparer.OrdinalIgnoreCase);
        }

        private async Task ProcessJobAsync(JobListItem job, Dictionary<string, WishlistItem> items, CancellationToken ct)
        {
            var isDiscography = job.Type == "discography";
            var releases = isDiscography
                ? await hashDb.GetDiscographyReleaseJobsAsync(job.Id, ct).ConfigureAwait(false)
                : await hashDb.GetLabelCrateReleaseJobsAsync(job.Id, ct).ConfigureAwait(false);

            if (releases.Count == 0)
            {
                // Planned before creation refused empty jobs; recomputing the aggregate turns 0 releases into Failed.
                log.LogWarning("[JobExecutor] job {JobId} ({Type}) has no releases to download (none resolved when it was created); marking it failed", job.Id, job.Type);
                if (isDiscography)
                {
                    await discographyJobs.GetJobAsync(job.Id, ct).ConfigureAwait(false);
                }
                else
                {
                    await labelCrateJobs.GetJobAsync(job.Id, ct).ConfigureAwait(false);
                }

                return;
            }

            // 1. Resolve the search text once, only for pending releases that still need an item.
            var resolved = new Dictionary<string, (string Text, string Year)>(StringComparer.OrdinalIgnoreCase);
            var settled = new Dictionary<string, JobStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var release in releases.Where(r => r.Status == JobStatus.Pending && !IsUsable(items.GetValueOrDefault(Key(job.Id, r.ReleaseId)))))
            {
                try
                {
                    var found = await ResolveReleaseAsync(release.ReleaseId, ct).ConfigureAwait(false);
                    if (found == null)
                    {
                        log.LogWarning("[JobExecutor] no artist/title metadata for release {ReleaseId}", release.ReleaseId);
                        settled[release.ReleaseId] = JobStatus.Failed;
                    }
                    else
                    {
                        resolved[release.ReleaseId] = found.Value;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    settled[release.ReleaseId] = await OnErrorAsync(job.Id, release, items, ex).ConfigureAwait(false);
                }
            }

            // Two releases with the same text would search for (and download) the same album: add the year to each. This
            // also holds against this job's items from earlier cycles; such a sibling gets its year too unless it downloaded.
            // ponytail: MusicBrainz disambiguation is not fetched (MusicBrainzClient drops it); texts still equal after the
            // year stay equal. Add it to MusicBrainzClient's ReleaseResponse/AlbumTarget and the AlbumTargets cache, then append it here.
            var prefix = job.Id + ":";
            var siblings = items
                .Where(i => i.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsUsable(i.Value) && !resolved.ContainsKey(i.Key[prefix.Length..]))
                .ToList();
            var duplicates = resolved
                .Where(r => resolved.Any(o => o.Key != r.Key && SameText(o.Value.Text, r.Value.Text)) || siblings.Any(s => SameAlbum(s.Value.SearchText, r.Value.Text)))
                .ToList();
            foreach (var (releaseId, value) in duplicates)
            {
                resolved[releaseId] = ($"{value.Text} {value.Year}".Trim(), value.Year);
            }

            foreach (var (key, sibling) in siblings.Where(s => s.Value.TotalDownloadCount == 0 && duplicates.Any(d => SameText(d.Value.Text, s.Value.SearchText))))
            {
                var year = Year((await hashDb.GetAlbumTargetAsync(key[prefix.Length..], ct).ConfigureAwait(false))?.ReleaseDate);
                if (year.Length > 0)
                {
                    sibling.SearchText = $"{sibling.SearchText} {year}";
                    await wishlist.UpdateAsync(sibling).ConfigureAwait(false);
                }
            }

            var targetDirectory = isDiscography && resolved.Count > 0
                ? (await hashDb.GetDiscographyJobAsync(job.Id, ct).ConfigureAwait(false))?.TargetDirectory
                : null;

            // 2. Advance every pending/running release one step.
            foreach (var release in releases.Where(r => r.Status is JobStatus.Pending or JobStatus.Running))
            {
                if (!settled.TryGetValue(release.ReleaseId, out var next))
                {
                    try
                    {
                        next = await AdvanceReleaseAsync(job.Id, release, items, resolved, targetDirectory).ConfigureAwait(false);
                        errors.Remove(Key(job.Id, release.ReleaseId));
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        next = await OnErrorAsync(job.Id, release, items, ex).ConfigureAwait(false);
                    }
                }

                if (next == release.Status)
                {
                    continue;
                }

                log.LogInformation("[JobExecutor] job {JobId} release {ReleaseId}: {From} -> {To}", job.Id, release.ReleaseId, release.Status, next);
                if (isDiscography)
                {
                    await discographyJobs.SetReleaseStatusAsync(job.Id, release.ReleaseId, next, ct).ConfigureAwait(false);
                }
                else
                {
                    await labelCrateJobs.SetReleaseStatusAsync(job.Id, release.ReleaseId, next, ct).ConfigureAwait(false);
                }
            }
        }

        private async Task<JobStatus> AdvanceReleaseAsync(
            string jobId,
            DiscographyReleaseJobStatus release,
            Dictionary<string, WishlistItem> items,
            Dictionary<string, (string Text, string Year)> resolved,
            string? targetDirectory)
        {
            var key = Key(jobId, release.ReleaseId);
            items.TryGetValue(key, out var item);

            if (release.Status == JobStatus.Pending)
            {
                if (!IsUsable(item))
                {
                    items[key] = await wishlist.CreateAsync(new WishlistItem
                    {
                        SearchText = resolved[release.ReleaseId].Text,
                        Enabled = true,
                        AutoDownload = true,
                        JobId = key,

                        // Checked at job creation; checked again because older jobs stored it unchecked. Not allowed -> default.
                        DestinationDirectory = DownloadDestinationResolver.NormalizeExplicitPath(options.CurrentValue, targetDirectory),
                    }).ConfigureAwait(false);
                }

                return JobStatus.Running;
            }

            return await AdvanceRunningAsync(item, fresh: false).ConfigureAwait(false);
        }

        private async Task<JobStatus> AdvanceRunningAsync(WishlistItem? item, bool fresh)
        {
            if (item == null)
            {
                return JobStatus.Failed; // removed by the user
            }

            // ponytail: wishlist.max_downloads also caps album items (1 = one file), so Completed means "queued at least one
            // file". The max_downloads/album decision lands here and in WishlistService.AutoDownloadBestResultsAsync.
            if (item.TotalDownloadCount > 0)
            {
                return JobStatus.Completed;
            }

            if (!item.Enabled)
            {
                return JobStatus.Failed; // disabled by the user before anything was found
            }

            // The wishlist saves a search before that search's own auto-download: judge only a settled last search.
            if (item.TotalSearchCount < MaxSearchAttempts || (item.LastSearchedAt ?? DateTime.MinValue) > DateTime.UtcNow - SearchSettleTime)
            {
                return JobStatus.Running;
            }

            if (!fresh)
            {
                // Give up on the current row, not on this cycle's snapshot: a late download may have landed since.
                return await AdvanceRunningAsync(await wishlist.GetAsync(item.Id).ConfigureAwait(false), fresh: true).ConfigureAwait(false);
            }

            item.Enabled = false;
            await wishlist.UpdateAsync(item).ConfigureAwait(false);
            return JobStatus.Failed;
        }

        /// <summary>
        ///     The release keeps its status for the next cycle; after <see cref="MaxConsecutiveErrors"/> errors in a row it
        ///     fails and its item is disabled.
        /// </summary>
        private async Task<JobStatus> OnErrorAsync(string jobId, DiscographyReleaseJobStatus release, Dictionary<string, WishlistItem> items, Exception ex)
        {
            var key = Key(jobId, release.ReleaseId);
            var count = errors[key] = errors.GetValueOrDefault(key) + 1;
            if (count < MaxConsecutiveErrors)
            {
                log.LogWarning(ex, "[JobExecutor] job {JobId} release {ReleaseId}: error {Count}/{Max}, retrying next cycle: {Message}", jobId, release.ReleaseId, count, MaxConsecutiveErrors, ex.Message);
                return release.Status;
            }

            log.LogWarning(ex, "[JobExecutor] job {JobId} release {ReleaseId}: {Max} errors in a row, giving up: {Message}", jobId, release.ReleaseId, MaxConsecutiveErrors, ex.Message);
            if (items.TryGetValue(key, out var item) && item.Enabled)
            {
                item.Enabled = false;
                await wishlist.UpdateAsync(item).ConfigureAwait(false);
            }

            errors.Remove(key);
            return JobStatus.Failed;
        }

        /// <summary>
        ///     Search text and year from the local MusicBrainz cache, else from MusicBrainz (at most one request per
        ///     <see cref="MusicBrainzSpacing"/>, then cached); null when the release has no title.
        /// </summary>
        private async Task<(string Text, string Year)?> ResolveReleaseAsync(string releaseId, CancellationToken ct)
        {
            var local = await hashDb.GetAlbumTargetAsync(releaseId, ct).ConfigureAwait(false);
            var (artist, title, year) = (local?.Artist, local?.Title, Year(local?.ReleaseDate));

            if (string.IsNullOrWhiteSpace(title))
            {
                var wait = lastMusicBrainzRequest + MusicBrainzSpacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                lastMusicBrainzRequest = DateTime.UtcNow;
                var remote = await musicBrainz.GetReleaseAsync(releaseId, ct).ConfigureAwait(false);
                if (remote == null || string.IsNullOrWhiteSpace(remote.Title))
                {
                    return null;
                }

                await hashDb.UpsertAlbumTargetAsync(remote, ct).ConfigureAwait(false); // later lookups stay local
                (artist, title, year) = (remote.Artist, remote.Title, remote.Metadata.ReleaseDate?.Year.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
            }

            var text = NormalizeSearchText($"{artist} {title}");
            return text.Length == 0 ? null : (text, year);
        }
    }
}
