// <copyright file="JobApiControllerTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.Jobs;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using slskd.API.Native;
using slskd.Integrations.MusicBrainz;
using slskd.Integrations.MusicBrainz.Models;
using slskd.Jobs;
using slskd.Jobs.API;
using Xunit;

public class JobApiControllerTests
{
    [Fact]
    public async Task DiscographyGet_WithBlankJobId_ReturnsBadRequest()
    {
        var controller = new DiscographyJobsController(Mock.Of<IDiscographyJobService>());

        var result = await controller.Get("   ", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task LabelCrateGet_WithBlankJobId_ReturnsBadRequest()
    {
        var controller = new LabelCrateJobsController(Mock.Of<ILabelCrateJobService>());

        var result = await controller.Get("   ", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task CreateDiscographyJob_WhenServiceRejects_Returns422WithPlainTextReason()
    {
        var discography = new Mock<IDiscographyJobService>();
        discography.Setup(s => s.CreateJobAsync(It.IsAny<DiscographyJobRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JobRejectedException("MusicBrainz returned no releases for this artist and profile."));

        var result = await CreateController(discography: discography).CreateDiscographyJob(
            new DiscographyJobRequest { ArtistId = "artist-1" },
            CancellationToken.None);

        AssertRejected(result, "MusicBrainz returned no releases for this artist and profile.");
    }

    [Fact]
    public async Task CreateLabelCrateJob_WhenServiceRejects_Returns422WithPlainTextReason()
    {
        var labelCrate = new Mock<ILabelCrateJobService>();
        labelCrate.Setup(s => s.CreateJobAsync(It.IsAny<LabelCrateJobRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JobRejectedException("No releases for this label."));

        var result = await CreateController(labelCrate: labelCrate).CreateLabelCrateJob(
            new LabelCrateJobRequest { LabelName = "Warp" },
            CancellationToken.None);

        AssertRejected(result, "No releases for this label.");
    }

    [Fact]
    public async Task CreateMbReleaseJob_WhenReleaseDoesNotResolve_Returns422AndCreatesNothing()
    {
        var discography = new Mock<IDiscographyJobService>();

        var result = await CreateController(discography: discography).CreateMbReleaseJob(
            new MbReleaseJobRequest("release-1", string.Empty),
            CancellationToken.None);

        AssertRejected(result, "MusicBrainz returned no release with an artist for this MBID.");
        discography.Verify(s => s.CreateJobAsync(It.IsAny<DiscographyJobRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CreateMbReleaseJob_WhenServiceRejectsTargetDirectory_Returns422()
    {
        var musicBrainz = new Mock<IMusicBrainzClient>();
        musicBrainz.Setup(m => m.GetReleaseAsync("release-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AlbumTarget { MusicBrainzReleaseId = "release-1", MusicBrainzArtistId = "artist-1" });
        var discography = new Mock<IDiscographyJobService>();
        discography.Setup(s => s.CreateJobAsync(It.IsAny<DiscographyJobRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new JobRejectedException("Target directory must be inside the downloads directory or a configured destination folder."));

        var result = await CreateController(discography: discography, musicBrainz: musicBrainz).CreateMbReleaseJob(
            new MbReleaseJobRequest("release-1", "/etc"),
            CancellationToken.None);

        AssertRejected(result, "Target directory must be inside the downloads directory or a configured destination folder.");
    }

    private static void AssertRejected(IActionResult result, string reason)
    {
        var content = Assert.IsType<ContentResult>(result);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, content.StatusCode);
        Assert.Equal("text/plain", content.ContentType);
        Assert.Equal(reason, content.Content);
    }

    private static JobsController CreateController(
        Mock<IDiscographyJobService>? discography = null,
        Mock<ILabelCrateJobService>? labelCrate = null,
        Mock<IMusicBrainzClient>? musicBrainz = null)
        => new(
            (discography ?? new Mock<IDiscographyJobService>()).Object,
            (labelCrate ?? new Mock<ILabelCrateJobService>()).Object,
            (musicBrainz ?? new Mock<IMusicBrainzClient>()).Object,
            NullLogger<JobsController>.Instance);
}
