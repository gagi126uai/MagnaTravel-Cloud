using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TravelApi.Data;
using TravelApi.Models;
using TravelApi.Services.Bsp;
using Xunit;

namespace TravelApi.Tests.Unit;

public class BspImportServiceTests
{
    [Fact]
    public async Task ImportAsync_InvalidAmounts_Throws()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setupContext = new AppDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
        }

        await using var context = new AppDbContext(options);
        var service = new BspImportService(context, new IBspImportParser[]
        {
            new CsvBspImportParser()
        });

        var content = "TicketNumber,ReservationReference,IssueDate,Currency,BaseAmount,TaxAmount,TotalAmount\n" +
                      "123,REF-1,2024-06-01,USD,100,10,120";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ImportAsync("bsp.csv", "csv", content, CancellationToken.None));
    }

    [Fact]
    public async Task ImportAsync_MatchesReservation_CreatesReconciliation()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setupContext = new AppDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
            var customer = new Customer
            {
                FullName = "Cliente BSP",
                Email = "bsp@example.com",
                DocumentNumber = "DOC-1",
                Address = "Test"
            };
            setupContext.Customers.Add(customer);
            await setupContext.SaveChangesAsync();
            setupContext.Reservations.Add(new Reservation
            {
                ReferenceCode = "REF-100",
                Status = "Confirmed",
                ProductType = "Flight",
                TotalAmount = 110m,
                BasePrice = 100m,
                Commission = 0m,
                SupplierName = "BSP",
                CustomerId = customer.Id
            });
            await setupContext.SaveChangesAsync();
        }

        await using var context = new AppDbContext(options);
        var service = new BspImportService(context, new IBspImportParser[]
        {
            new CsvBspImportParser()
        });

        var content = "TicketNumber,ReservationReference,IssueDate,Currency,BaseAmount,TaxAmount,TotalAmount\n" +
                      "ABC123,REF-100,2024-06-01,USD,100,10,110";

        var batch = await service.ImportAsync("bsp.csv", "csv", content, CancellationToken.None);

        var reconciliation = batch.Reconciliations.Single();
        Assert.Equal("Matched", reconciliation.Status);
        Assert.Equal(0m, reconciliation.DifferenceAmount);
        Assert.NotNull(reconciliation.ReservationId);
    }

    [Fact]
    public async Task CloseBatchAsync_AllMatched_CreatesAccountingEntries()
    {
        await using var connection = new SqliteConnection("Filename=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        await using (var setupContext = new AppDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync();
            var customer = new Customer
            {
                FullName = "Cliente BSP",
                Email = "bsp@example.com",
                DocumentNumber = "DOC-2",
                Address = "Test"
            };
            setupContext.Customers.Add(customer);
            await setupContext.SaveChangesAsync();
            setupContext.Reservations.Add(new Reservation
            {
                ReferenceCode = "REF-200",
                Status = "Confirmed",
                ProductType = "Flight",
                TotalAmount = 150m,
                BasePrice = 140m,
                Commission = 0m,
                SupplierName = "BSP",
                CustomerId = customer.Id
            });
            await setupContext.SaveChangesAsync();
        }

        await using var context = new AppDbContext(options);
        var service = new BspImportService(context, new IBspImportParser[]
        {
            new CsvBspImportParser()
        });

        var content = "TicketNumber,ReservationReference,IssueDate,Currency,BaseAmount,TaxAmount,TotalAmount\n" +
                      "XYZ789,REF-200,2024-06-01,USD,140,10,150";

        var batch = await service.ImportAsync("bsp.csv", "csv", content, CancellationToken.None);
        var closedBatch = await service.CloseBatchAsync(batch.Id, CancellationToken.None);

        Assert.Equal("Closed", closedBatch.Status);
        Assert.NotNull(closedBatch.ClosedAt);

        var accountingEntries = await context.AccountingEntries
            .Include(entry => entry.Lines)
            .ToListAsync();

        Assert.Single(accountingEntries);
        Assert.Equal(2, accountingEntries[0].Lines.Count);
    }
}
