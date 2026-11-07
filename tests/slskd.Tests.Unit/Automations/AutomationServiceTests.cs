// <copyright file="AutomationServiceTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.Automations;

using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using slskd.API.Native;
using slskd.Automations;
using slskd.LibraryHealth;
using slskd.Shares;
using slskd.Wishlist;
using Xunit;

public sealed class AutomationServiceTests : IDisposable
{
    private readonly Mock<IWishlistService> wishlist = new();
    private readonly Mock<IShareService> shares = new();
    private readonly Mock<ILibraryHealthService> libraryHealth = new();
    private readonly string shareRoot;
    private readonly string downloadsRoot;
    private readonly SqliteConnection keepAlive;
    private readonly AutomationService service;

    public AutomationServiceTests()
    {
        // A real, existing share root and downloads folder inside the test output folder (never %TEMP%).
        shareRoot = Path.Combine(AppContext.BaseDirectory, "automation-tests", Guid.NewGuid().ToString("N"));
        downloadsRoot = shareRoot + "-downloads";
        Directory.CreateDirectory(shareRoot);
        Directory.CreateDirectory(downloadsRoot);

        // Shared in-memory database; it lives as long as this connection stays open.
        var connectionString = $"Data Source=file:automations-{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();

        var options = new slskd.Options
        {
            Shares = new slskd.Options.SharesOptions { Directories = new[] { shareRoot } },
            Directories = new slskd.Options.DirectoriesOptions { Downloads = downloadsRoot },
        };
        var actions = new AutomationActions(
            wishlist.Object,
            libraryHealth.Object,
            shares.Object,
            Mock.Of<IStateMonitor<State>>(m => m.CurrentValue == new State()),
            Mock.Of<IOptionsMonitor<slskd.Options>>(m => m.CurrentValue == options),
            NullLogger<AutomationActions>.Instance);

        service = new AutomationService(connectionString, actions, NullLogger<AutomationService>.Instance);
    }

    public void Dispose()
    {
        keepAlive.Dispose();
        Directory.Delete(shareRoot, recursive: true);
        Directory.Delete(downloadsRoot, recursive: true);
    }

    [Fact]
    public async Task Crud_RoundTrips()
    {
        var created = await service.CreateAsync(Request("Nightly", AutomationTemplates.WishlistRetry, 60, true, ("limit", 5)));

        Assert.NotEqual(Guid.Empty, created.Id);
        Assert.Equal(5, created.Parameters["limit"].GetInt32());
        Assert.NotNull(created.NextRunAt);

        var fetched = await service.GetAsync(created.Id);
        Assert.Equal("Nightly", fetched!.Name);
        Assert.Equal(5, fetched.Parameters["limit"].GetInt32());
        Assert.Single(await service.ListAsync());

        var updated = await service.UpdateAsync(created.Id, Request("Renamed", AutomationTemplates.WishlistRetry, 0, false));
        Assert.Equal("Renamed", updated!.Name);
        Assert.Null(updated.NextRunAt);
        Assert.Empty(updated.Parameters);

        Assert.True(await service.DeleteAsync(created.Id));
        Assert.False(await service.DeleteAsync(created.Id));
        Assert.Null(await service.GetAsync(created.Id));
        Assert.Null(await service.UpdateAsync(created.Id, Request("x", AutomationTemplates.LocalDiagnostics, 0, false)));
    }

    [Theory]
    [InlineData("", AutomationTemplates.WishlistRetry, 0, "name")]
    [InlineData("ok", "dashboard-refresh", 0, "type")]
    [InlineData("ok", AutomationTemplates.WishlistRetry, -1, "intervalMinutes")]
    [InlineData("ok", AutomationTemplates.LibraryHealthScan, 0, "libraryPath")]
    public async Task Create_RejectsInvalidRequests(string name, string type, int interval, string expectedInMessage)
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Request(name, type, interval, true)));
        Assert.Contains(expectedInMessage, ex.Message);
    }

    [Fact]
    public async Task Create_RejectsWrongParameterTypeAndUnknownParameter()
    {
        var wrongType = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Request("ok", AutomationTemplates.WishlistRetry, 0, true, ("limit", "three"))));
        Assert.Contains("limit", wrongType.Message);

        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Request("ok", AutomationTemplates.LocalDiagnostics, 0, true, ("bogus", 1))));
        Assert.Contains("bogus", unknown.Message);
    }

    [Theory]
    [InlineData(AutomationTemplates.WishlistRetry, "limit", 0, "parameters.limit must be between 1 and 50")]
    [InlineData(AutomationTemplates.WishlistRetry, "limit", 51, "parameters.limit must be between 1 and 50")]
    [InlineData(AutomationTemplates.StaleCacheReminders, "maxShareCacheAgeHours", 0, "parameters.maxShareCacheAgeHours must be between 1 and 8760")]
    [InlineData(AutomationTemplates.StaleCacheReminders, "maxShareCacheAgeHours", 300000000, "parameters.maxShareCacheAgeHours must be between 1 and 8760")]
    public async Task CreateAndUpdate_RejectOutOfRangeIntParametersWith400(string type, string key, int value, string message)
    {
        var create = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Request("ok", type, 0, true, (key, value))));
        Assert.Equal(message, create.Message);

        var existing = await service.CreateAsync(Request("ok", type, 0, true));
        var update = await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateAsync(existing.Id, Request("ok", type, 0, true, (key, value))));
        Assert.Equal(message, update.Message);

        var badRequest = Assert.IsType<BadRequestObjectResult>(await new AutomationsController(service).Create(Request("ok", type, 0, true, (key, value))));
        Assert.Equal(message, JsonSerializer.SerializeToElement(badRequest.Value).GetProperty("message").GetString());
    }

    [Fact]
    public void Templates_IntParametersCarryTheirRange()
    {
        var limit = Assert.Single(AutomationTemplates.Find(AutomationTemplates.WishlistRetry)!.ParameterSchema, p => p.Key == "limit");
        Assert.Equal(1, limit.Min);
        Assert.Equal(50, limit.Max);
        var maxAge = Assert.Single(AutomationTemplates.Find(AutomationTemplates.StaleCacheReminders)!.ParameterSchema, p => p.Key == "maxShareCacheAgeHours");
        Assert.Equal(1, maxAge.Min);
        Assert.Equal(8760, maxAge.Max);

        // Every int parameter is bounded on both sides and its default lies inside the range.
        Assert.All(
            AutomationTemplates.All.SelectMany(t => t.ParameterSchema).Where(p => p.Type == "int"),
            p => Assert.InRange((int)p.Default!, p.Min!.Value, p.Max!.Value));

        var json = JsonSerializer.SerializeToElement(limit, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(1, json.GetProperty("min").GetInt32());
        Assert.Equal(50, json.GetProperty("max").GetInt32());
    }

    [Fact]
    public void Controller_RequiresTheAdministratorRoleForEveryEndpoint()
    {
        var authorize = Assert.Single(typeof(AutomationsController).GetCustomAttributes<AuthorizeAttribute>(inherit: true));
        Assert.Equal(AuthPolicy.Any, authorize.Policy);
        Assert.Equal(AuthRole.AdministratorOnly, authorize.Roles);

        // Action attributes only add requirements to the controller's; none may opt out.
        var actions = typeof(AutomationsController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Assert.NotEmpty(actions);
        Assert.Empty(typeof(AutomationsController).GetCustomAttributes<AllowAnonymousAttribute>(inherit: true));
        Assert.All(actions, action => Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(inherit: true)));
    }

    [Fact]
    public void ComputeNextRunAt_FollowsEnabledIntervalAndLastRun()
    {
        var updated = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var lastRun = updated.AddHours(1);

        Assert.Null(AutomationService.ComputeNextRunAt(false, 10, lastRun, updated));
        Assert.Null(AutomationService.ComputeNextRunAt(true, 0, lastRun, updated));
        Assert.Equal(updated.AddMinutes(10), AutomationService.ComputeNextRunAt(true, 10, null, updated));
        Assert.Equal(lastRun.AddMinutes(10), AutomationService.ComputeNextRunAt(true, 10, lastRun, updated));
    }

    [Fact]
    public void WishlistRetryTemplate_LinksOnlyOptionsThatMatterAndPromisesNoApproval()
    {
        var template = AutomationTemplates.Find(AutomationTemplates.WishlistRetry)!;
        var linked = template.LinkedOptions;
        var defaults = new slskd.Options.WishlistOptions();
        var intervalRange = typeof(slskd.Options.WishlistOptions).GetProperty(nameof(defaults.IntervalSeconds))!.GetCustomAttribute<RangeAttribute>()!;

        Assert.Equal(defaults.Enabled, Assert.Single(linked, o => o.YamlPath == "wishlist.enabled").Default);
        var interval = Assert.Single(linked, o => o.YamlPath == "wishlist.interval_seconds");
        Assert.Equal(defaults.IntervalSeconds, interval.Default);
        Assert.Equal((int)intervalRange.Minimum, interval.Min);
        Assert.Contains("Soulseek server", interval.Description);
        Assert.All(linked, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));

        // Nothing reads wishlist.auto_download: each item's own flag decides, so it is not a setting retries depend on.
        Assert.DoesNotContain(linked, o => o.YamlPath == "wishlist.auto_download");

        // A real run downloads at once for items with auto-download on; there is no approval step to promise.
        Assert.DoesNotContain("approval", template.FileImpact, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("No approval step", template.ApprovalGate);

        var json = JsonSerializer.SerializeToElement(interval, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(3600, json.GetProperty("default").GetInt32());
        Assert.Equal(300, json.GetProperty("min").GetInt32());
        Assert.Equal(interval.Description, json.GetProperty("description").GetString());
    }

    [Fact]
    public async Task WishlistRetry_DryRun_PreviewsWithoutSearching_AndExplainsSkips()
    {
        SetupWishlistItems();
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 0, true, ("limit", 1)));

        var run = await service.RunAsync(automation.Id, dryRun: true);

        Assert.True(run!.DryRun);
        Assert.Equal(AutomationRunStatus.Succeeded, run.Status);
        Assert.Equal("Would run 1 of 4 wishlist searches; skipped: 1 searched in the last 3600 s, 1 disabled, 1 over the limit of 1", run.Summary);
        Assert.Equal("oldest", run.Details.GetProperty("items")[0].GetProperty("searchText").GetString());
        var skipped = run.Details.GetProperty("skipped");
        Assert.Equal(1, skipped.GetProperty("recentlySearched").GetInt32());
        Assert.Equal(1, skipped.GetProperty("disabled").GetInt32());
        Assert.Equal(1, skipped.GetProperty("overLimit").GetInt32());
        wishlist.Verify(w => w.RunSearchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);

        Assert.Null((await service.GetAsync(automation.Id))!.LastRunAt);
        Assert.Single(await service.ListRunsAsync(automation.Id, 20));
    }

    [Fact]
    public async Task WishlistRetry_Run_SearchesAndRecordsRun_SkippingItemsSearchedWithinTheInterval()
    {
        var items = SetupWishlistItems();
        wishlist.Setup(w => w.RunSearchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync(new slskd.Search.Search());
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 30, true, ("limit", 3)));

        var run = await service.RunAsync(automation.Id, dryRun: false);

        Assert.Equal(AutomationRunStatus.Succeeded, run!.Status);
        Assert.Equal("Started 2 of 2 wishlist searches (0 failed); skipped: 1 searched in the last 3600 s, 1 disabled", run.Summary);
        wishlist.Verify(w => w.RunSearchAsync(items[1].Id, It.IsAny<CancellationToken>()), Times.Once);
        wishlist.Verify(w => w.RunSearchAsync(items[3].Id, It.IsAny<CancellationToken>()), Times.Once);
        wishlist.Verify(w => w.RunSearchAsync(items[0].Id, It.IsAny<CancellationToken>()), Times.Never); // searched just now: the background cycle has it
        wishlist.Verify(w => w.RunSearchAsync(items[2].Id, It.IsAny<CancellationToken>()), Times.Never);

        var after = (await service.GetAsync(automation.Id))!;
        Assert.Equal(run.FinishedAt, after.LastRunAt);
        Assert.Equal(AutomationRunStatus.Succeeded, after.LastRunStatus);
        Assert.Equal(run.Summary, after.LastRunSummary);
        Assert.Equal(run.FinishedAt.AddMinutes(30), after.NextRunAt);

        var runs = await service.ListRunsAsync(null, 50);
        Assert.Equal(run.Id, Assert.Single(runs).Id);
        Assert.Null(await service.RunAsync(Guid.NewGuid(), dryRun: false));
    }

    [Fact]
    public async Task WishlistRetry_Run_ItemsWhoseSearchIsAlreadyRunning_AreSkipped()
    {
        SetupWishlistItems();
        wishlist.Setup(w => w.RunSearchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).ReturnsAsync((slskd.Search.Search)null);
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 30, true));

        var run = await service.RunAsync(automation.Id, dryRun: false);

        Assert.Equal(AutomationRunStatus.Skipped, run!.Status);
        Assert.Equal("Started 0 of 2 wishlist searches (0 failed); skipped: 2 already being searched, 1 searched in the last 3600 s, 1 disabled", run.Summary);
        Assert.Equal(2, run.Details.GetProperty("skipped").GetProperty("alreadyRunning").GetInt32());
    }

    [Fact]
    public async Task WishlistRetry_CancelledDuringASearch_RecordsNothingAndKeepsTheSchedule()
    {
        SetupWishlistItems();
        using var cts = new CancellationTokenSource();
        wishlist.Setup(w => w.RunSearchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns((Guid _, CancellationToken token) =>
            {
                cts.Cancel(); // host shutdown while the search runs
                return Task.FromCanceled<slskd.Search.Search>(token);
            });
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 30, true));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync(automation.Id, dryRun: false, cts.Token));

        var after = (await service.GetAsync(automation.Id))!;
        Assert.Null(after.LastRunAt);
        Assert.Equal(automation.NextRunAt, after.NextRunAt);
        Assert.Empty(await service.ListRunsAsync(null, 50));
        wishlist.Verify(w => w.RunSearchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_CancellationNotRequestedByTheCaller_IsRecordedAsAFailure()
    {
        // e.g. an internal timeout: the caller's token is not cancelled, so this is a failed run, not a shutdown.
        wishlist.Setup(w => w.ListAsync()).ThrowsAsync(new TaskCanceledException("timed out"));
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 30, true));

        var run = await service.RunAsync(automation.Id, dryRun: false);

        Assert.Equal(AutomationRunStatus.Failed, run!.Status);
        Assert.Equal(run.FinishedAt, (await service.GetAsync(automation.Id))!.LastRunAt);
    }

    [Fact]
    public async Task WhileARealRunIsInProgress_DryRunsAndOtherAutomationsAreNotBlocked()
    {
        SetupWishlistItems();
        shares.Setup(s => s.ListScansAsync(It.IsAny<long>())).ReturnsAsync(Array.Empty<Scan>());
        var (searching, release) = BlockSearches();
        var retry = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 0, true));
        var diagnostics = await service.CreateAsync(Request("Diag", AutomationTemplates.LocalDiagnostics, 0, true));

        try
        {
            var realRun = service.RunAsync(retry.Id, dryRun: false);
            await searching.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var dry = await service.RunAsync(retry.Id, dryRun: true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(dry!.DryRun);
            var other = await service.RunAsync(diagnostics.Id, dryRun: false).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(diagnostics.Id, other!.AutomationId);
            Assert.False(realRun.IsCompleted);

            release.SetResult();
            Assert.Equal(AutomationRunStatus.Succeeded, (await realRun)!.Status);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Delete_DuringARun_LeavesNoRunBehind()
    {
        SetupWishlistItems();
        var (searching, release) = BlockSearches();
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 0, true));

        try
        {
            var run = service.RunAsync(automation.Id, dryRun: false);
            await searching.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(await service.DeleteAsync(automation.Id));
            release.SetResult();

            Assert.Null(await run);
            Assert.Empty(await service.ListRunsAsync(null, 50));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task ListRuns_Since_ReturnsOnlyRunsStartedAtOrAfterIt_NewestFirst()
    {
        wishlist.Setup(w => w.ListAsync()).ReturnsAsync(new List<WishlistItem>());
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 0, true));
        var first = await service.RunAsync(automation.Id, dryRun: true);
        var since = first!.StartedAt.AddTicks(1);
        var second = await service.RunAsync(automation.Id, dryRun: true);
        var third = await service.RunAsync(automation.Id, dryRun: true);

        Assert.Equal(new[] { third!.Id, second!.Id }, (await service.ListRunsAsync(null, 50, since)).Select(r => r.Id));
        Assert.Equal(third.Id, Assert.Single(await service.ListRunsAsync(null, 1, since)).Id); // the limit still applies
        Assert.Equal(2, (await service.ListRunsAsync(null, 50, since.ToOffset(TimeSpan.FromHours(2)))).Count); // compared as instants
        Assert.Equal(3, (await service.ListRunsAsync(null, 50)).Count);

        var ok = Assert.IsType<OkObjectResult>(await new AutomationsController(service).AllRuns(50, since));
        Assert.Equal(2, Assert.IsAssignableFrom<IReadOnlyList<AutomationRun>>(ok.Value).Count);
    }

    [Fact]
    public async Task LocalDiagnostics_DryRunWritesNothing_RealRunProbesTheDownloadsFolder()
    {
        shares.Setup(s => s.ListScansAsync(It.IsAny<long>())).ReturnsAsync(Array.Empty<Scan>());
        var automation = await service.CreateAsync(Request("Diag", AutomationTemplates.LocalDiagnostics, 0, true));

        var dry = await service.RunAsync(automation.Id, dryRun: true);

        var dryDownloads = dry!.Details.GetProperty("downloads");
        Assert.True(dryDownloads.GetProperty("exists").GetBoolean());
        Assert.Equal(JsonValueKind.Null, dryDownloads.GetProperty("writable").ValueKind); // no probe file, so writability is unknown

        var real = await service.RunAsync(automation.Id, dryRun: false);

        Assert.True(real!.Details.GetProperty("downloads").GetProperty("writable").GetBoolean());
        Assert.Empty(Directory.EnumerateFileSystemEntries(downloadsRoot)); // the probe is deleted again
    }

    [Fact]
    public async Task LibraryHealthScan_DryRun_OutsideShareRoots_FailsWithoutTouchingTheFilesystem()
    {
        var outside = Path.Combine(AppContext.BaseDirectory, "not-a-share");
        var automation = await service.CreateAsync(Request("Scan", AutomationTemplates.LibraryHealthScan, 0, true, ("libraryPath", outside)));

        var run = await service.RunAsync(automation.Id, dryRun: true);

        Assert.Equal(AutomationRunStatus.Failed, run!.Status);
        Assert.Equal("Library Health scans must target a configured share directory", run.Summary);
        Assert.False(run.Details.GetProperty("withinShareRoots").GetBoolean());
        Assert.False(run.Details.TryGetProperty("exists", out _));
        Assert.False(run.Details.TryGetProperty("audioFiles", out _));
        libraryHealth.Verify(l => l.StartScanAsync(It.IsAny<LibraryHealthScanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LibraryHealthScan_RunStartsScanOnce_DryRunDoesNot()
    {
        File.WriteAllBytes(Path.Combine(shareRoot, "track.flac"), Array.Empty<byte>());
        File.WriteAllBytes(Path.Combine(shareRoot, "cover.jpg"), Array.Empty<byte>());
        libraryHealth.Setup(l => l.StartScanAsync(It.IsAny<LibraryHealthScanRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync("scan-1");
        var automation = await service.CreateAsync(Request("Scan", AutomationTemplates.LibraryHealthScan, 0, true, ("libraryPath", shareRoot)));

        var dry = await service.RunAsync(automation.Id, dryRun: true);

        Assert.Equal(AutomationRunStatus.Succeeded, dry!.Status);
        Assert.Equal(1, dry.Details.GetProperty("audioFiles").GetInt32());
        libraryHealth.Verify(l => l.StartScanAsync(It.IsAny<LibraryHealthScanRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        var real = await service.RunAsync(automation.Id, dryRun: false);

        Assert.Equal(AutomationRunStatus.Succeeded, real!.Status);
        Assert.Equal("scan-1", real.Details.GetProperty("scanId").GetString());
        libraryHealth.Verify(l => l.StartScanAsync(It.Is<LibraryHealthScanRequest>(r => !string.IsNullOrEmpty(r.LibraryPath)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StaleCacheReminders_DryRunReports_RunRescansOnlyWhenStale()
    {
        shares.Setup(s => s.ListScansAsync(It.IsAny<long>())).ReturnsAsync(Array.Empty<Scan>());
        shares.Setup(s => s.ScanAsync()).Returns(Task.CompletedTask);
        var automation = await service.CreateAsync(Request("Reminder", AutomationTemplates.StaleCacheReminders, 0, true, ("maxShareCacheAgeHours", 1)));

        var dry = await service.RunAsync(automation.Id, dryRun: true);

        Assert.Equal(AutomationRunStatus.Succeeded, dry!.Status);
        Assert.Contains("would be triggered", dry.Summary);
        shares.Verify(s => s.ScanAsync(), Times.Never);

        var real = await service.RunAsync(automation.Id, dryRun: false);

        Assert.Equal(AutomationRunStatus.Succeeded, real!.Status);
        Assert.Contains("rescan triggered", real.Summary);
        shares.Verify(s => s.ScanAsync(), Times.Once);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        shares.Setup(s => s.ListScansAsync(It.IsAny<long>())).ReturnsAsync(new[] { new Scan { StartedAt = now - 1000, EndedAt = now } });

        var fresh = await service.RunAsync(automation.Id, dryRun: false);

        Assert.Equal(AutomationRunStatus.Skipped, fresh!.Status);
        shares.Verify(s => s.ScanAsync(), Times.Once);
    }

    [Fact]
    public async Task RunHistory_IsTrimmedPerAutomation()
    {
        wishlist.Setup(w => w.ListAsync()).ReturnsAsync(new List<WishlistItem>());
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 0, true));

        for (var i = 0; i <= AutomationService.MaxRunsPerAutomation; i++)
        {
            await service.RunAsync(automation.Id, dryRun: true);
        }

        Assert.Equal(AutomationService.MaxRunsPerAutomation, (await service.ListRunsAsync(automation.Id, 1000)).Count);
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheAutomationsRuns()
    {
        wishlist.Setup(w => w.ListAsync()).ReturnsAsync(new List<WishlistItem>());
        var automation = await service.CreateAsync(Request("Retry", AutomationTemplates.WishlistRetry, 0, true));
        await service.RunAsync(automation.Id, dryRun: true);
        Assert.Single(await service.ListRunsAsync(automation.Id, 20));

        Assert.True(await service.DeleteAsync(automation.Id));

        Assert.Empty(await service.ListRunsAsync(automation.Id, 20));
        Assert.Empty(await service.ListRunsAsync(null, 20));
    }

    [Fact]
    public async Task RunDueAsync_RunsOnlyDueEnabledAutomations()
    {
        var due = await service.CreateAsync(Request("due", AutomationTemplates.LocalDiagnostics, 1, true));
        await service.CreateAsync(Request("disabled", AutomationTemplates.LocalDiagnostics, 1, false));
        await service.CreateAsync(Request("manual", AutomationTemplates.LocalDiagnostics, 0, true));

        Assert.Empty(await service.RunDueAsync(DateTimeOffset.UtcNow));

        var runs = await service.RunDueAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.Equal(due.Id, Assert.Single(runs).AutomationId);
        Assert.False(runs[0].DryRun);
        Assert.NotNull((await service.GetAsync(due.Id))!.LastRunAt);
    }

    [Fact]
    public async Task RunDueAsync_SkipsAutomationsDisabledOrDeletedDuringTheBatch()
    {
        var first = await service.CreateAsync(Request("first", AutomationTemplates.WishlistRetry, 1, true));
        await Task.Delay(5); // distinct createdAt so the batch order is first, disabled, deleted
        var disabled = await service.CreateAsync(Request("disabled", AutomationTemplates.WishlistRetry, 1, true));
        await Task.Delay(5);
        var deleted = await service.CreateAsync(Request("deleted", AutomationTemplates.WishlistRetry, 1, true));

        // While the first automation runs, the "user" disables one and deletes another.
        var interfered = false;
        wishlist.Setup(w => w.ListAsync()).ReturnsAsync(() =>
        {
            if (!interfered)
            {
                interfered = true;
                service.UpdateAsync(disabled.Id, Request("disabled", AutomationTemplates.WishlistRetry, 1, false)).GetAwaiter().GetResult();
                service.DeleteAsync(deleted.Id).GetAwaiter().GetResult();
            }

            return new List<WishlistItem>();
        });

        var runs = await service.RunDueAsync(DateTimeOffset.UtcNow.AddMinutes(2));

        Assert.Equal(first.Id, Assert.Single(runs).AutomationId);
        Assert.Null((await service.GetAsync(disabled.Id))!.LastRunAt);
        Assert.Null(await service.GetAsync(deleted.Id));
        Assert.Single(await service.ListRunsAsync(null, 50));
    }

    // Searched within wishlist.interval_seconds (default 3600), never searched, disabled, searched two hours ago.
    private List<WishlistItem> SetupWishlistItems()
    {
        var items = new List<WishlistItem>
        {
            new() { SearchText = "recent", Enabled = true, LastSearchedAt = DateTime.UtcNow },
            new() { SearchText = "oldest", Enabled = true, LastSearchedAt = null },
            new() { SearchText = "disabled", Enabled = false, LastSearchedAt = null },
            new() { SearchText = "stale", Enabled = true, LastSearchedAt = DateTime.UtcNow.AddHours(-2) },
        };
        wishlist.Setup(w => w.ListAsync()).ReturnsAsync(items);
        return items;
    }

    // The first wishlist search blocks until release is completed.
    private (TaskCompletionSource Searching, TaskCompletionSource Release) BlockSearches()
    {
        var searching = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        wishlist.Setup(w => w.RunSearchAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())).Returns(async () =>
        {
            searching.TrySetResult();
            await release.Task;
            return new slskd.Search.Search();
        });
        return (searching, release);
    }

    private static AutomationRequest Request(string name, string type, int interval, bool enabled, params (string Key, object Value)[] parameters) => new()
    {
        Name = name,
        Type = type,
        IntervalMinutes = interval,
        Enabled = enabled,
        Parameters = parameters.ToDictionary(p => p.Key, p => JsonSerializer.SerializeToElement(p.Value)),
    };
}
