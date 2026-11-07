// <copyright file="DiscographyJobService.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Jobs
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Text.Json.Serialization;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
    using slskd.Destinations;
    using slskd.HashDb;
    using slskd.Integrations.MusicBrainz;

    public interface IDiscographyJobService
    {
        Task<string> CreateJobAsync(DiscographyJobRequest request, CancellationToken ct = default);

        Task<DiscographyJob?> GetJobAsync(string jobId, CancellationToken ct = default);

        Task SetReleaseStatusAsync(string jobId, string releaseId, JobStatus status, CancellationToken ct = default);
    }

    public class DiscographyJobService : IDiscographyJobService
    {
        private readonly IDiscographyProfileService profileService;
        private readonly IArtistReleaseGraphService graphService;
        private readonly IHashDbService hashDb;
        private readonly IOptionsMonitor<slskd.Options> options;
        private readonly ILogger<DiscographyJobService> log;

        public DiscographyJobService(
            IDiscographyProfileService profileService,
            IArtistReleaseGraphService graphService,
            IHashDbService hashDb,
            IOptionsMonitor<slskd.Options> options,
            ILogger<DiscographyJobService> log)
        {
            this.profileService = profileService;
            this.graphService = graphService;
            this.hashDb = hashDb;
            this.options = options;
            this.log = log;
        }

        public async Task<string> CreateJobAsync(DiscographyJobRequest request, CancellationToken ct = default)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ArtistId))
            {
                throw new ArgumentException("ArtistId is required", nameof(request));
            }

            var targetDirectory = ResolveTargetDirectory(request.TargetDirectory);
            var profile = request.Profile;
            var releaseIds = request.ReleaseIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                ?? await profileService.GetReleaseIdsForProfileAsync(request.ArtistId, profile, ct).ConfigureAwait(false);
            if (releaseIds.Count == 0)
            {
                throw new JobRejectedException("MusicBrainz returned no releases for this artist and profile.");
            }

            var graph = await graphService.GetArtistReleaseGraphAsync(request.ArtistId, forceRefresh: false, ct).ConfigureAwait(false);
            var job = new DiscographyJob
            {
                JobId = Guid.NewGuid().ToString("N"),
                ArtistId = request.ArtistId,
                ArtistName = graph?.Name ?? request.ArtistId,
                Profile = profile,
                TargetDirectory = targetDirectory,
                ReleaseIds = releaseIds,
                TotalReleases = releaseIds.Count,
                Status = JobStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
            };

            // Seed release sub-jobs as pending
            var releaseJobs = releaseIds
                .Select(id => new DiscographyReleaseJobStatus { ReleaseId = id, Status = JobStatus.Pending })
                .ToList();

            await hashDb.UpsertDiscographyReleaseJobsAsync(job.JobId, releaseJobs, ct).ConfigureAwait(false);
            await hashDb.UpsertDiscographyJobAsync(job, ct).ConfigureAwait(false);

            log.LogInformation("[DiscographyJob] Planned job {JobId} for artist {Artist} with {Count} releases (profile {Profile})",
                job.JobId, job.ArtistName, job.TotalReleases, profile);

            return job.JobId;
        }

        public async Task<DiscographyJob?> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            var job = await hashDb.GetDiscographyJobAsync(jobId, ct).ConfigureAwait(false);
            if (job == null)
            {
                return null;
            }

            var releaseStatuses = await hashDb.GetDiscographyReleaseJobsAsync(jobId, ct).ConfigureAwait(false);
            if (releaseStatuses.Count == 0 && job.ReleaseIds?.Count > 0)
            {
                // No sub-jobs persisted yet, seed them
                var seeds = job.ReleaseIds.Select(id => new DiscographyReleaseJobStatus { ReleaseId = id, Status = JobStatus.Pending });
                await hashDb.UpsertDiscographyReleaseJobsAsync(job.JobId, seeds, ct).ConfigureAwait(false);
                releaseStatuses = seeds.ToList();
            }

            var completedReleases = 0;
            var failedReleases = 0;
            var anyRunning = false;
            var anyPending = false;
            foreach (var release in releaseStatuses)
            {
                completedReleases += release.Status == JobStatus.Completed ? 1 : 0;
                failedReleases += release.Status == JobStatus.Failed ? 1 : 0;
                anyRunning |= release.Status == JobStatus.Running;
                anyPending |= release.Status == JobStatus.Pending;
            }

            var totalReleases = releaseStatuses.Count;
            var status = totalReleases == 0
                ? JobStatus.Failed // nothing to run, ever (creation refuses this now; older jobs end here)
                : completedReleases == totalReleases
                    ? JobStatus.Completed
                    : failedReleases > 0 && !anyRunning && !anyPending
                        ? JobStatus.Failed
                        : JobStatus.Running;

            var aggregateChanged =
                job.TotalReleases != totalReleases ||
                job.CompletedReleases != completedReleases ||
                job.FailedReleases != failedReleases ||
                job.Status != status;
            job.TotalReleases = totalReleases;
            job.CompletedReleases = completedReleases;
            job.FailedReleases = failedReleases;
            job.Status = status;

            if (aggregateChanged)
            {
                await hashDb.UpsertDiscographyJobAsync(job, ct).ConfigureAwait(false);
            }

            return job;
        }

        public async Task SetReleaseStatusAsync(string jobId, string releaseId, JobStatus status, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(releaseId))
            {
                return;
            }

            await hashDb.SetDiscographyReleaseJobStatusAsync(jobId, releaseId, status, ct).ConfigureAwait(false);

            // Recalculate aggregate status
            await GetJobAsync(jobId, ct).ConfigureAwait(false);
        }

        /// <summary>
        ///     Empty stays empty (default download directory). A relative path goes under the default download
        ///     directory; the result must stay inside an allowed download root.
        /// </summary>
        private string ResolveTargetDirectory(string? targetDirectory)
        {
            if (string.IsNullOrWhiteSpace(targetDirectory))
            {
                return string.Empty;
            }

            var current = options.CurrentValue;
            return DownloadDestinationResolver.NormalizeExplicitPath(
                    current,
                    Path.Combine(DownloadDestinationResolver.GetDefaultPath(current), targetDirectory.Trim()))
                ?? throw new JobRejectedException("Target directory must be inside the downloads directory or a configured destination folder.");
        }
    }

    public class DiscographyJobRequest
    {
        [JsonPropertyName("artist_id")]
        public string ArtistId { get; set; } = string.Empty;

        [JsonPropertyName("profile")]
        public DiscographyProfile Profile { get; set; } = DiscographyProfile.CoreDiscography;

        [JsonPropertyName("target_dir")]
        public string TargetDirectory { get; set; } = string.Empty;

        [JsonPropertyName("release_ids")]
        public List<string>? ReleaseIds { get; set; }
    }
}
