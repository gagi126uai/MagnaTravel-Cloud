using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class CupoAllocationServiceTests
{
    [Fact]
    public async Task AllocateAsync_ConcurrentRequests_OnlyOneSucceeds()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setupContext = new AppDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
            setupContext.Cupos.Add(new Cupo
            {
                Name = "Cupo Patagonia",
                ProductType = "Tour",
                TravelDate = DateTime.UtcNow.Date,
                Capacity = 1,
                OverbookingLimit = 0,
                Reserved = 0,
                RowVersion = Guid.NewGuid()
            });
            await setupContext.SaveChangesAsync();
        }

        var errors = new ConcurrentBag<Exception>();
        var successCount = 0;
        var startSignal = new TaskCompletionSource<bool>();

        async Task ExecuteAsync()
        {
            try
            {
                await using var context = new AppDbContext(options);
                var service = new CupoAllocationService(context);
                await startSignal.Task;
                await service.AllocateAsync(1, 1, null, CancellationToken.None);
                Interlocked.Increment(ref successCount);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
        }

        var tasks = new[]
        {
            Task.Run(ExecuteAsync),
            Task.Run(ExecuteAsync)
        };

        startSignal.SetResult(true);
        await Task.WhenAll(tasks);

        Assert.Equal(1, successCount);
        Assert.Single(errors);
        Assert.Contains(errors, error =>
            error is CupoOverbookingException || error is CupoConcurrencyException);

        await using var verifyContext = new AppDbContext(options);
        var cupo = await verifyContext.Cupos.FirstAsync();
        Assert.Equal(1, cupo.Reserved);
    }
}
