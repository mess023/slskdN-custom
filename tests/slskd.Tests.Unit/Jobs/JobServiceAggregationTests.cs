// <copyright file="JobServiceAggregationTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.Jobs;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using slskd.HashDb;
using slskd.Integrations.MusicBrainz;
using slskd.Jobs;
using Xunit;

public class JobServiceAggregationTests
{
    private static readonly string Downloads = Path.Combine(Path.GetTempPath(), "slskdn-job-tests", "downloads");

    private static DiscographyJobService CreateDiscographyService(Mock<IHashDbService> hashDb, Mock<IDiscographyProfileService>? profile = null)
    {
        var options = new slskd.Options { Directories = new slskd.Options.DirectoriesOptions { Downloads = Downloads } };
        return new(
            (profile ?? new Mock<IDiscographyProfileService>()).Object,
            Mock.Of<IArtistReleaseGraphService>(),
            hashDb.Object,
            Mock.Of<IOptionsMonitor<slskd.Options>>(m => m.CurrentValue == options),
            NullLogger<DiscographyJobService>.Instance);
    }

    [Fact]
    public async Task DiscographyCreateJobAsync_WithNoReleases_RejectsAndStoresNothing()
    {
        var hashDb = new Mock<IHashDbService>();
        var profile = new Mock<IDiscographyProfileService>();
        profile.Setup(p => p.GetReleaseIdsForProfileAsync("artist-1", It.IsAny<DiscographyProfile>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());

        var ex = await Assert.ThrowsAsync<JobRejectedException>(() =>
            CreateDiscographyService(hashDb, profile).CreateJobAsync(new DiscographyJobRequest { ArtistId = "artist-1" }));

        Assert.Equal("MusicBrainz returned no releases for this artist and profile.", ex.Message);
        hashDb.Verify(h => h.UpsertDiscographyJobAsync(It.IsAny<DiscographyJob>(), It.IsAny<CancellationToken>()), Times.Never);
        hashDb.Verify(h => h.UpsertDiscographyReleaseJobsAsync(It.IsAny<string>(), It.IsAny<IEnumerable<DiscographyReleaseJobStatus>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LabelCrateCreateJobAsync_WithNoReleasesInLocalCache_RejectsAndStoresNothing()
    {
        var hashDb = new Mock<IHashDbService>();
        hashDb.Setup(h => h.GetReleaseIdsByLabelAsync("Warp", It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());
        var service = new LabelCrateJobService(hashDb.Object, NullLogger<LabelCrateJobService>.Instance);

        var ex = await Assert.ThrowsAsync<JobRejectedException>(() =>
            service.CreateJobAsync(new LabelCrateJobRequest { LabelName = "Warp" }));

        Assert.Contains("local MusicBrainz cache", ex.Message);
        hashDb.Verify(h => h.UpsertLabelCrateJobAsync(It.IsAny<LabelCrateJob>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetJobAsync_WithNoReleases_IsFailedInsteadOfPendingForever()
    {
        var hashDb = new Mock<IHashDbService>();
        hashDb.Setup(h => h.GetDiscographyJobAsync("job-0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscographyJob { JobId = "job-0", Status = JobStatus.Pending });
        hashDb.Setup(h => h.GetDiscographyReleaseJobsAsync("job-0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscographyReleaseJobStatus>());
        hashDb.Setup(h => h.GetLabelCrateJobAsync("job-0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LabelCrateJob { JobId = "job-0", Status = JobStatus.Pending });
        hashDb.Setup(h => h.GetLabelCrateReleaseJobsAsync("job-0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<DiscographyReleaseJobStatus>());

        var discography = await CreateDiscographyService(hashDb).GetJobAsync("job-0");
        var labelCrate = await new LabelCrateJobService(hashDb.Object, NullLogger<LabelCrateJobService>.Instance).GetJobAsync("job-0");

        Assert.Equal(JobStatus.Failed, discography!.Status);
        Assert.Equal(JobStatus.Failed, labelCrate!.Status);
        hashDb.Verify(h => h.UpsertDiscographyJobAsync(It.Is<DiscographyJob>(j => j.Status == JobStatus.Failed), It.IsAny<CancellationToken>()), Times.Once);
        hashDb.Verify(h => h.UpsertLabelCrateJobAsync(It.Is<LabelCrateJob>(j => j.Status == JobStatus.Failed), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Artist/Album", true)]
    [InlineData("../outside", false)]
    [InlineData("/etc", false)]
    public async Task DiscographyCreateJobAsync_PutsTargetDirectoryUnderDownloadsOrRejectsIt(string targetDirectory, bool allowed)
    {
        var hashDb = new Mock<IHashDbService>();
        var service = CreateDiscographyService(hashDb);
        var request = new DiscographyJobRequest { ArtistId = "artist-1", TargetDirectory = targetDirectory, ReleaseIds = new List<string> { "rel-1" } };

        if (!allowed)
        {
            await Assert.ThrowsAsync<JobRejectedException>(() => service.CreateJobAsync(request));
            hashDb.Verify(h => h.UpsertDiscographyJobAsync(It.IsAny<DiscographyJob>(), It.IsAny<CancellationToken>()), Times.Never);
            return;
        }

        await service.CreateJobAsync(request);

        hashDb.Verify(h => h.UpsertDiscographyJobAsync(
            It.Is<DiscographyJob>(j => j.TargetDirectory == Path.GetFullPath(Path.Combine(Downloads, "Artist", "Album"))),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task HashDbListAdapter_DelegatesBoundedPageWithoutLoadingCompleteLists()
    {
        var expected = new JobListPage(Array.Empty<JobListItem>(), 123);
        var hashDb = new Mock<IHashDbService>();
        hashDb.Setup(service => service.GetJobListPageAsync(
                "discography",
                "running",
                25,
                50,
                "created_at",
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var adapter = new HashDbJobServiceListAdapter(hashDb.Object);

        var actual = await adapter.GetJobListPageAsync(
            "discography",
            "running",
            25,
            50,
            "created_at",
            true);

        Assert.Same(expected, actual);
        hashDb.Verify(service => service.ListDiscographyJobsAsync(It.IsAny<CancellationToken>()), Times.Never);
        hashDb.Verify(service => service.ListLabelCrateJobsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DiscographyGetJobAsync_WritesOnlyWhenDerivedAggregateChanges()
    {
        var job = new DiscographyJob
        {
            JobId = "job-1",
            TotalReleases = 2,
            CompletedReleases = 1,
            FailedReleases = 0,
            Status = JobStatus.Running,
        };
        var releases = new List<DiscographyReleaseJobStatus>
        {
            new() { ReleaseId = "release-1", Status = JobStatus.Completed },
            new() { ReleaseId = "release-2", Status = JobStatus.Pending },
        };
        var hashDb = new Mock<IHashDbService>();
        hashDb.Setup(service => service.GetDiscographyJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        hashDb.Setup(service => service.GetDiscographyReleaseJobsAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);
        hashDb.Setup(service => service.UpsertDiscographyJobAsync(It.IsAny<DiscographyJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = CreateDiscographyService(hashDb);

        var unchanged = await service.GetJobAsync("job-1");

        Assert.NotNull(unchanged);
        hashDb.Verify(value => value.UpsertDiscographyJobAsync(
            It.IsAny<DiscographyJob>(),
            It.IsAny<CancellationToken>()), Times.Never);

        releases[1].Status = JobStatus.Completed;
        var changed = await service.GetJobAsync("job-1");

        Assert.NotNull(changed);
        Assert.Equal(JobStatus.Completed, changed!.Status);
        Assert.Equal(2, changed.CompletedReleases);
        hashDb.Verify(value => value.UpsertDiscographyJobAsync(
            It.Is<DiscographyJob>(persisted =>
                persisted.Status == JobStatus.Completed &&
                persisted.CompletedReleases == 2),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LabelCrateGetJobAsync_WritesOnlyWhenDerivedAggregateChanges()
    {
        var job = new LabelCrateJob
        {
            JobId = "job-1",
            TotalReleases = 2,
            CompletedReleases = 1,
            FailedReleases = 0,
            Status = JobStatus.Running,
        };
        var releases = new List<DiscographyReleaseJobStatus>
        {
            new() { ReleaseId = "release-1", Status = JobStatus.Completed },
            new() { ReleaseId = "release-2", Status = JobStatus.Pending },
        };
        var hashDb = new Mock<IHashDbService>();
        hashDb.Setup(service => service.GetLabelCrateJobAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        hashDb.Setup(service => service.GetLabelCrateReleaseJobsAsync("job-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(releases);
        hashDb.Setup(service => service.UpsertLabelCrateJobAsync(It.IsAny<LabelCrateJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var service = new LabelCrateJobService(
            hashDb.Object,
            NullLogger<LabelCrateJobService>.Instance);

        var unchanged = await service.GetJobAsync("job-1");

        Assert.NotNull(unchanged);
        hashDb.Verify(value => value.UpsertLabelCrateJobAsync(
            It.IsAny<LabelCrateJob>(),
            It.IsAny<CancellationToken>()), Times.Never);

        releases[1].Status = JobStatus.Failed;
        var changed = await service.GetJobAsync("job-1");

        Assert.NotNull(changed);
        Assert.Equal(JobStatus.Failed, changed!.Status);
        Assert.Equal(1, changed.FailedReleases);
        hashDb.Verify(value => value.UpsertLabelCrateJobAsync(
            It.Is<LabelCrateJob>(persisted =>
                persisted.Status == JobStatus.Failed &&
                persisted.FailedReleases == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
