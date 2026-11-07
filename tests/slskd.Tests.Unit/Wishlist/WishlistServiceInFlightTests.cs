// <copyright file="WishlistServiceInFlightTests.cs" company="slskdN Team">
//     Copyright (c) slskdN Team. All rights reserved.
// </copyright>
namespace slskd.Tests.Unit.Wishlist;

using System.Linq.Expressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using slskd.Search;
using slskd.Transfers.Downloads;
using slskd.Transfers.Ranking;
using slskd.Wishlist;
using Soulseek;
using SlskdSearch = slskd.Search.Search;

public sealed class WishlistServiceInFlightTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly DbContextOptions<WishlistDbContext> dbOptions;

    public WishlistServiceInFlightTests()
    {
        connection.Open();
        dbOptions = new DbContextOptionsBuilder<WishlistDbContext>().UseSqlite(connection).Options;
        using var context = new WishlistDbContext(dbOptions);
        context.Database.EnsureCreated();
    }

    public void Dispose() => connection.Dispose();

    [Fact]
    public async Task RunSearch_WhileTheSameItemIsBeingSearched_SkipsWithoutStartingASecondSearch()
    {
        var itemId = Guid.NewGuid();
        using (var context = new WishlistDbContext(dbOptions))
        {
            context.WishlistItems.Add(new WishlistItem { Id = itemId, SearchText = "artist title", MaxResults = 25 });
            await context.SaveChangesAsync();
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var searchService = new Mock<ISearchService>();
        searchService
            .Setup(s => s.StartAsync(It.IsAny<Guid>(), It.IsAny<SearchQuery>(), It.IsAny<SearchScope>(), It.IsAny<SearchOptions>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<Guid?>()))
            .Returns(async () =>
            {
                started.TrySetResult();
                await release.Task;
                return new SlskdSearch { State = SearchStates.Requested };
            });
        searchService
            .Setup(s => s.FindAsync(It.IsAny<Expression<Func<SlskdSearch, bool>>>(), It.IsAny<bool>()))
            .ReturnsAsync(new SlskdSearch { State = SearchStates.Completed });

        var service = new WishlistService(
            new Factory(dbOptions),
            Mock.Of<IDbContextFactory<slskd.Transfers.TransfersDbContext>>(),
            searchService.Object,
            Mock.Of<ISoulseekClient>(),
            new TestOptionsMonitor<slskd.Options>(new slskd.Options()),
            Mock.Of<ISourceRankingService>(),
            Mock.Of<IDownloadService>());

        try
        {
            var first = service.RunSearchAsync(itemId);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // The background cycle, "search now" and wishlist-retry all end up here: the second caller gets null at once.
            Assert.Null(await service.RunSearchAsync(itemId).WaitAsync(TimeSpan.FromSeconds(10)));

            release.SetResult();
            Assert.NotNull(await first);

            // Once the first search has finished the item can be searched again.
            Assert.NotNull(await service.RunSearchAsync(itemId));
            searchService.Verify(
                s => s.StartAsync(It.IsAny<Guid>(), It.IsAny<SearchQuery>(), It.IsAny<SearchScope>(), It.IsAny<SearchOptions>(), It.IsAny<List<string>>(), It.IsAny<string>(), It.IsAny<Guid?>()),
                Times.Exactly(2));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private sealed class Factory(DbContextOptions<WishlistDbContext> options) : IDbContextFactory<WishlistDbContext>
    {
        public WishlistDbContext CreateDbContext() => new(options);
    }
}
