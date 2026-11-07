// <copyright file="JobExecutorServiceTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.Jobs;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using slskd.HashDb;
using slskd.HashDb.Models;
using slskd.Integrations.MusicBrainz;
using slskd.Integrations.MusicBrainz.Models;
using slskd.Jobs;
using slskd.Wishlist;
using Xunit;

public class JobExecutorServiceTests
{
    private readonly Mock<IHashDbService> hashDb = new();
    private readonly Mock<IDiscographyJobService> discographyJobs = new();
    private readonly Mock<ILabelCrateJobService> labelCrateJobs = new();
    private readonly Mock<IWishlistService> wishlist = new();
    private readonly Mock<IMusicBrainzClient> musicBrainz = new();
    private readonly List<WishlistItem> wishlistItems = new();
    private readonly Dictionary<string, List<DiscographyReleaseJobStatus>> releases = new();
    private slskd.Options options = new();

    public JobExecutorServiceTests()
    {
        // A tiny store: created items show up in the next ListAsync, status changes land on the release rows.
        wishlist.Setup(w => w.ListAsync()).ReturnsAsync(() => wishlistItems.ToList());
        wishlist.Setup(w => w.CreateAsync(It.IsAny<WishlistItem>()))
            .Callback<WishlistItem>(wishlistItems.Add)
            .ReturnsAsync((WishlistItem i) => i);
        wishlist.Setup(w => w.UpdateAsync(It.IsAny<WishlistItem>())).ReturnsAsync((WishlistItem i) => i);
        wishlist.Setup(w => w.GetAsync(It.IsAny<Guid>())).ReturnsAsync((Guid id) => wishlistItems.FirstOrDefault(i => i.Id == id));
        hashDb.Setup(h => h.GetDiscographyReleaseJobsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string jobId, CancellationToken _) => releases.GetValueOrDefault(jobId) ?? new());
        hashDb.Setup(h => h.GetLabelCrateReleaseJobsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string jobId, CancellationToken _) => releases.GetValueOrDefault(jobId) ?? new());
        discographyJobs.Setup(d => d.SetReleaseStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobStatus>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, JobStatus, CancellationToken>(SetStatus)
            .Returns(Task.CompletedTask);
        labelCrateJobs.Setup(l => l.SetReleaseStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobStatus>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, JobStatus, CancellationToken>(SetStatus)
            .Returns(Task.CompletedTask);
        SetupJobPage("pending");
        SetupJobPage("running");
    }

    [Theory]
    [InlineData("AC/DC – Back in Black", "AC DC Back in Black")]
    [InlineData("Guns N’ Roses Appetite for Destruction", "Guns N Roses Appetite for Destruction")]
    [InlineData("Oasis (What’s the Story) Morning Glory?", "Oasis What s the Story Morning Glory")]
    [InlineData("David Bowie “Heroes”", "David Bowie Heroes")]
    [InlineData("Sigur Rós ( )", "Sigur Rós")]
    public void NormalizeSearchText_KeepsOnlyLettersAndDigits(string raw, string expected)
    {
        Assert.Equal(expected, JobExecutorService.NormalizeSearchText(raw));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)] // stored unchecked by an older build: the default directory instead
    public async Task NewItems_DownloadToTheJobsTargetDirectoryWhenItIsAllowed(bool allowed)
    {
        var downloads = Path.Combine(Path.GetTempPath(), "slskdn-executor-tests", "downloads");
        options = new slskd.Options { Directories = new slskd.Options.DirectoriesOptions { Downloads = downloads } };
        var stored = allowed ? Path.Combine(downloads, "jobs", "Artist") : Path.GetFullPath("/etc");
        SetupJobPage("pending", Job("job-11", "discography"));
        AddReleases("job-11", ("rel-1", JobStatus.Pending));
        Local("rel-1", "Artist", "Album");
        hashDb.Setup(h => h.GetDiscographyJobAsync("job-11", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscographyJob { JobId = "job-11", TargetDirectory = stored });

        await CreateService().RunOnceAsync(CancellationToken.None);

        Assert.Equal(allowed ? Path.GetFullPath(stored) : null, Assert.Single(wishlistItems).DestinationDirectory);
    }

    [Fact]
    public async Task PendingDiscographyJob_CreatesOneItemPerReleaseAndMarksReleasesRunning()
    {
        SetupJobPage("pending", Job("job-1", "discography"));
        AddReleases("job-1", ("rel-local", JobStatus.Pending), ("rel-remote", JobStatus.Pending));
        Local("rel-local", "AC/DC", "Back in Black");
        var remote = new AlbumTarget { MusicBrainzReleaseId = "rel-remote", Artist = "Guns N’ Roses", Title = "Appetite for Destruction" };
        musicBrainz.Setup(m => m.GetReleaseAsync("rel-remote", It.IsAny<CancellationToken>())).ReturnsAsync(remote);

        await CreateService().RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "AC DC Back in Black", "Guns N Roses Appetite for Destruction" }, wishlistItems.Select(i => i.SearchText));
        Assert.Equal(new[] { "job-1:rel-local", "job-1:rel-remote" }, wishlistItems.Select(i => i.JobId));
        Assert.All(wishlistItems, i =>
        {
            Assert.True(i.Enabled);
            Assert.True(i.AutoDownload);
            Assert.Equal(string.Empty, i.Filter); // wishlist.filter decides the formats
            Assert.Null(i.DestinationDirectory); // no target directory: the default download directory
        });
        hashDb.Verify(h => h.UpsertAlbumTargetAsync(remote, It.IsAny<CancellationToken>()), Times.Once); // resolved once, then local
        Assert.All(releases["job-1"], r => Assert.Equal(JobStatus.Running, r.Status));
        labelCrateJobs.Verify(l => l.SetReleaseStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobStatus>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunningReleases_FinishFromTheirOwnItemWithoutMetadataLookups()
    {
        SetupJobPage("running", Job("job-2", "label_crate"));
        AddReleases(
            "job-2",
            ("rel-done", JobStatus.Running),
            ("rel-exhausted", JobStatus.Running),
            ("rel-just-searched", JobStatus.Running),
            ("rel-waiting", JobStatus.Running),
            ("rel-gone", JobStatus.Running),
            ("rel-already", JobStatus.Completed));
        var longAgo = DateTime.UtcNow - JobExecutorService.SearchSettleTime - TimeSpan.FromMinutes(1);
        var exhausted = Item("job-2:rel-exhausted", enabled: true, searches: JobExecutorService.MaxSearchAttempts, lastSearched: longAgo);
        var justSearched = Item("job-2:rel-just-searched", enabled: true, searches: JobExecutorService.MaxSearchAttempts, lastSearched: DateTime.UtcNow);
        wishlistItems.AddRange(new[]
        {
            Item("job-2:rel-done", enabled: false, downloads: 12),
            exhausted,
            justSearched, // its auto-download may still be on the way
            Item("job-2:rel-waiting", enabled: true, searches: 1),
            Item("job-other:rel-gone", enabled: true, downloads: 3), // another job's item: not ours
        });

        await CreateService().RunOnceAsync(CancellationToken.None);

        Assert.Equal(
            new[] { JobStatus.Completed, JobStatus.Failed, JobStatus.Running, JobStatus.Running, JobStatus.Failed, JobStatus.Completed },
            releases["job-2"].Select(r => r.Status));
        Assert.False(exhausted.Enabled);
        Assert.True(justSearched.Enabled);
        wishlist.Verify(w => w.UpdateAsync(It.IsAny<WishlistItem>()), Times.Once);
        wishlist.Verify(w => w.CreateAsync(It.IsAny<WishlistItem>()), Times.Never);
        hashDb.Verify(h => h.GetAlbumTargetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        musicBrainz.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GivingUp_DecidesOnTheCurrentRowNotOnTheCycleSnapshot()
    {
        SetupJobPage("running", Job("job-10", "discography"));
        AddReleases("job-10", ("rel-late", JobStatus.Running));
        var snapshot = Item("job-10:rel-late", enabled: true, searches: JobExecutorService.MaxSearchAttempts, lastSearched: DateTime.UtcNow.AddHours(-1));
        wishlistItems.Add(snapshot);

        // Since the snapshot, a 6th search found the album and its auto-download was saved.
        wishlist.Setup(w => w.GetAsync(snapshot.Id))
            .ReturnsAsync(new WishlistItem { Id = snapshot.Id, JobId = snapshot.JobId, Enabled = false, TotalSearchCount = 6, TotalDownloadCount = 11, LastSearchedAt = DateTime.UtcNow });

        await CreateService().RunOnceAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Completed, releases["job-10"][0].Status);
        wishlist.Verify(w => w.UpdateAsync(It.IsAny<WishlistItem>()), Times.Never);
    }

    [Fact]
    public async Task SameTextInOneJob_GetsTheYearAndADownloadCompletesOnlyItsOwnRelease()
    {
        SetupJobPage("pending", Job("job-3", "discography"));
        AddReleases("job-3", ("rel-blue", JobStatus.Pending), ("rel-green", JobStatus.Pending));
        Local("rel-blue", "Weezer", "Weezer", "1994-05-10");
        Local("rel-green", "Weezer", "Weezer", "2001-05-15");
        var service = CreateService();

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "Weezer Weezer 1994", "Weezer Weezer 2001" }, wishlistItems.Select(i => i.SearchText));
        Assert.Equal(new[] { "job-3:rel-blue", "job-3:rel-green" }, wishlistItems.Select(i => i.JobId));

        wishlistItems[0].TotalDownloadCount = 9; // only the blue album was found
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { JobStatus.Completed, JobStatus.Running }, releases["job-3"].Select(r => r.Status));
    }

    [Fact]
    public async Task SameTextResolvedInALaterCycle_GetsTheYearAndSoDoesTheEarlierItem()
    {
        SetupJobPage("pending", Job("job-12", "discography"));
        AddReleases("job-12", ("rel-blue", JobStatus.Pending), ("rel-green", JobStatus.Pending));
        Local("rel-blue", "Weezer", "Weezer", "1994-05-10");
        hashDb.SetupSequence(h => h.GetAlbumTargetAsync("rel-green", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("MusicBrainz timed out")) // transient: resolved one cycle later
            .ReturnsAsync(new AlbumTargetEntry { ReleaseId = "rel-green", Artist = "Weezer", Title = "Weezer", ReleaseDate = "2001-05-15" });
        var service = CreateService();

        await service.RunOnceAsync(CancellationToken.None);
        Assert.Equal("Weezer Weezer", Assert.Single(wishlistItems).SearchText); // unique so far

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new[] { "Weezer Weezer 1994", "Weezer Weezer 2001" }, wishlistItems.Select(i => i.SearchText));
        Assert.Equal(new[] { "job-12:rel-blue", "job-12:rel-green" }, wishlistItems.Select(i => i.JobId));
        wishlist.Verify(w => w.UpdateAsync(It.Is<WishlistItem>(i => i.JobId == "job-12:rel-blue")), Times.Once);

        // A third copy that turns up later still collides with the texts that already carry a year.
        releases["job-12"].Add(new DiscographyReleaseJobStatus { ReleaseId = "rel-red", Status = JobStatus.Pending });
        Local("rel-red", "Weezer", "Weezer", "2009-11-03");

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal("Weezer Weezer 2009", wishlistItems.Last().SearchText);
        Assert.Equal("Weezer Weezer 1994", wishlistItems[0].SearchText);
        wishlist.Verify(w => w.UpdateAsync(It.IsAny<WishlistItem>()), Times.Once);
    }

    [Fact]
    public async Task UserWishlistItemWithSameText_IsNeverReusedOrModified()
    {
        SetupJobPage("pending", Job("job-4", "discography"));
        SetupJobPage("running", Job("job-5", "discography"));
        AddReleases("job-4", ("rel-new", JobStatus.Pending));
        AddReleases("job-5", ("rel-old", JobStatus.Running));
        Local("rel-new", "Artist", "Same");

        // The user's own item: same text, no job marker, exhausted and with downloads.
        var userItem = new WishlistItem { SearchText = "Artist Same", JobId = null, Enabled = true, TotalSearchCount = 99, TotalDownloadCount = 7 };
        wishlistItems.Add(userItem);

        await CreateService().RunOnceAsync(CancellationToken.None);

        // Pending: a new executor item is created instead of adopting the user's.
        Assert.Equal("job-4:rel-new", wishlistItems.Last().JobId);
        Assert.Equal(JobStatus.Running, releases["job-4"][0].Status);

        // Running: the executor's item is gone, so the release fails; the user's item is not completed from, disabled or updated.
        Assert.Equal(JobStatus.Failed, releases["job-5"][0].Status);
        wishlist.Verify(w => w.UpdateAsync(It.IsAny<WishlistItem>()), Times.Never);
        Assert.True(userItem.Enabled);
    }

    [Fact]
    public async Task RunningRelease_StaysRunningWhileTheWishlistIsDisabled()
    {
        options = new slskd.Options { Wishlist = new slskd.Options.WishlistOptions { Enabled = false } };
        SetupJobPage("running", Job("job-6", "discography"));
        AddReleases("job-6", ("rel-idle", JobStatus.Running));
        var item = Item("job-6:rel-idle", enabled: true);
        wishlistItems.Add(item);
        var service = CreateService();

        for (var cycle = 0; cycle < 250; cycle++) // longer than the old 240-cycle stall guard
        {
            await service.RunOnceAsync(CancellationToken.None);
        }

        discographyJobs.Verify(d => d.SetReleaseStatusAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobStatus>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(item.Enabled);
    }

    [Fact]
    public async Task ReleaseErrors_KeepTheStatusUntilTenInARow()
    {
        SetupJobPage("pending", Job("job-7", "discography"));
        AddReleases("job-7", ("rel-boom", JobStatus.Pending), ("rel-unknown", JobStatus.Pending), ("rel-ok", JobStatus.Pending));
        hashDb.Setup(h => h.GetAlbumTargetAsync("rel-boom", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db locked"));
        musicBrainz.Setup(m => m.GetReleaseAsync("rel-unknown", It.IsAny<CancellationToken>())).ReturnsAsync((AlbumTarget?)null);
        Local("rel-ok", "A", "B");
        var service = CreateService();

        for (var cycle = 1; cycle < JobExecutorService.MaxConsecutiveErrors; cycle++)
        {
            await service.RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal(new[] { JobStatus.Pending, JobStatus.Failed, JobStatus.Running }, releases["job-7"].Select(r => r.Status));

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(JobStatus.Failed, releases["job-7"][0].Status);
        Assert.Single(wishlistItems); // only rel-ok got an item
    }

    [Fact]
    public async Task MusicBrainzLookups_AreSpacedAndHappenOncePerRelease()
    {
        SetupJobPage("pending", Job("job-8", "discography"));
        AddReleases("job-8", ("rel-a", JobStatus.Pending), ("rel-b", JobStatus.Pending));
        var calls = new List<DateTime>();
        musicBrainz.Setup(m => m.GetReleaseAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add(DateTime.UtcNow))
            .ReturnsAsync((string id, CancellationToken _) => new AlbumTarget { MusicBrainzReleaseId = id, Artist = "Artist", Title = id });
        var service = CreateService();

        await service.RunOnceAsync(CancellationToken.None);
        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, calls.Count); // running releases never look metadata up again
        Assert.True(calls[1] - calls[0] >= TimeSpan.FromSeconds(1), $"MusicBrainz calls {(calls[1] - calls[0]).TotalMilliseconds} ms apart");
    }

    [Fact]
    public async Task JobWithoutReleases_IsMarkedFailed()
    {
        SetupJobPage("pending", Job("job-9", "discography"));

        await CreateService().RunOnceAsync(CancellationToken.None);

        discographyJobs.Verify(d => d.GetJobAsync("job-9", It.IsAny<CancellationToken>()), Times.Once); // recomputes 0 releases to Failed
        wishlist.Verify(w => w.CreateAsync(It.IsAny<WishlistItem>()), Times.Never);
    }

    private static JobListItem Job(string id, string type) => new(id, type, "pending", DateTimeOffset.UtcNow, 1, 0, 0);

    private static WishlistItem Item(string jobId, bool enabled, int searches = 0, int downloads = 0, DateTime? lastSearched = null)
        => new() { SearchText = jobId, JobId = jobId, Enabled = enabled, TotalSearchCount = searches, TotalDownloadCount = downloads, LastSearchedAt = lastSearched };

    private JobExecutorService CreateService() => new(
        hashDb.Object,
        discographyJobs.Object,
        labelCrateJobs.Object,
        wishlist.Object,
        musicBrainz.Object,
        Mock.Of<IOptionsMonitor<slskd.Options>>(m => m.CurrentValue == options),
        NullLogger<JobExecutorService>.Instance);

    private void SetupJobPage(string status, params JobListItem[] items)
    {
        hashDb.Setup(h => h.GetJobListPageAsync(null, status, It.IsAny<int>(), 0, It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobListPage(items, items.Length));
    }

    private void AddReleases(string jobId, params (string Id, JobStatus Status)[] rows)
        => releases[jobId] = rows.Select(r => new DiscographyReleaseJobStatus { ReleaseId = r.Id, Status = r.Status }).ToList();

    private void Local(string releaseId, string artist, string title, string date = "")
        => hashDb.Setup(h => h.GetAlbumTargetAsync(releaseId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlbumTargetEntry { ReleaseId = releaseId, Artist = artist, Title = title, ReleaseDate = date });

    private void SetStatus(string jobId, string releaseId, JobStatus status, CancellationToken ct)
        => releases[jobId].Single(r => r.ReleaseId == releaseId).Status = status;
}
