using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Hangfire;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class FinanceReadModelTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;

    public FinanceReadModelTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
    }

    [Fact]
    public async Task GetCollectionsSummaryAsync_ShouldReturnDebtAndUrgencyMetrics()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Customers.Add(new Customer { Id = 10, FullName = "Ana Cliente" });
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1,
                NumeroReserva = "F-2026-1001",
                Name = "Reserva urgente",
                Status = EstadoReserva.Confirmed,
                PayerId = 10,
                TotalSale = 1000m,
                TotalPaid = 250m,
                Balance = 750m,
                StartDate = DateTime.UtcNow.Date.AddDays(2)
            },
            new Reserva
            {
                Id = 2,
                NumeroReserva = "F-2026-1002",
                Name = "Reserva pendiente",
                Status = EstadoReserva.Traveling,
                PayerId = 10,
                TotalSale = 800m,
                TotalPaid = 0m,
                Balance = 800m,
                StartDate = DateTime.UtcNow.Date.AddDays(20)
            });
        context.Payments.Add(new Payment
        {
            Id = 1,
            ReservaId = 1,
            Amount = 250m,
            PaidAt = DateTime.UtcNow,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            Method = "Transfer"
        });
        await context.SaveChangesAsync();

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(x => x.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                RequireFullPaymentForOperativeStatus = true,
                RequireFullPaymentForVoucher = true,
                UpcomingUnpaidReservationAlertDays = 7
            });

        var service = new PaymentService(context, null!, Mock.Of<IMapper>(), settingsMock.Object, NullLogger<PaymentService>.Instance);

        var summary = await service.GetCollectionsSummaryAsync(CancellationToken.None);

        Assert.Equal(1550m, summary.PendingAmount);
        Assert.Equal(250m, summary.CollectedThisMonth);
        Assert.Equal(1, summary.UrgentReservationsCount);
        Assert.Equal(750m, summary.UrgentPendingAmount);
        Assert.Equal(2, summary.BlockedOperationalCount);
        Assert.Equal(2, summary.BlockedVoucherCount);
    }

    [Fact]
    public async Task GetCashSummaryAsync_ShouldOnlyReturnCashMetrics()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Payments.Add(new Payment
        {
            Id = 1,
            Amount = 500m,
            PaidAt = DateTime.UtcNow,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true,
            Method = "Transfer"
        });
        context.SupplierPayments.Add(new SupplierPayment
        {
            Id = 1,
            SupplierId = 1,
            Amount = 120m,
            PaidAt = DateTime.UtcNow,
            Method = "Transfer"
        });
        context.ManualCashMovements.Add(new ManualCashMovement
        {
            Id = 1,
            Direction = CashMovementDirections.Expense,
            Amount = 30m,
            OccurredAt = DateTime.UtcNow,
            Method = "Cash",
            Category = "Caja",
            Description = "Ajuste",
            CreatedBy = "Admin"
        });
        await context.SaveChangesAsync();

        var service = new TreasuryService(context, null!);

        var summary = await service.GetCashSummaryAsync(CancellationToken.None);

        Assert.Equal(500m, summary.CashInThisMonth);
        Assert.Equal(150m, summary.CashOutThisMonth);
        Assert.Equal(350m, summary.NetCashThisMonth);
    }

    [Fact]
    public async Task GetInvoicingWorklistAsync_ShouldClassifyReadyAndOverrideReservations()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Customers.Add(new Customer { Id = 20, FullName = "Carlos Fiscal" });
        context.Reservas.AddRange(
            new Reserva
            {
                Id = 1,
                NumeroReserva = "F-2026-2001",
                Name = "Reserva lista",
                Status = EstadoReserva.Confirmed,
                PayerId = 20,
                TotalSale = 1200m,
                TotalPaid = 1200m,
                Balance = 0m
            },
            new Reserva
            {
                Id = 2,
                NumeroReserva = "F-2026-2002",
                Name = "Reserva con deuda",
                Status = EstadoReserva.Confirmed,
                PayerId = 20,
                TotalSale = 900m,
                TotalPaid = 300m,
                Balance = 600m
            });
        await context.SaveChangesAsync();

        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock
            .Setup(x => x.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                AfipInvoiceControlMode = AfipInvoiceControlModes.AllowAgentOverrideWithReason
            });

        var service = new InvoiceService(
            context,
            null!, // EntityReferenceResolver
            Mock.Of<IAfipService>(),
            Mock.Of<IInvoicePdfService>(),
            Mock.Of<IMapper>(),
            Mock.Of<IBackgroundJobClient>(),
            Mock.Of<ILogger<InvoiceService>>(),
            settingsMock.Object,
            BuildUserManager());

        var worklist = await service.GetInvoicingWorklistAsync(new TravelApi.Application.DTOs.InvoicingWorklistQuery { Status = "all" }, CancellationToken.None);

        Assert.Collection(
            worklist.Items,
            ready =>
            {
                Assert.Equal("F-2026-2001", ready.NumeroReserva);
                Assert.Equal("ready", ready.FiscalStatus);
            },
            blocked =>
            {
                Assert.Equal("F-2026-2002", blocked.NumeroReserva);
                Assert.Equal("override", blocked.FiscalStatus);
                Assert.True(blocked.RequiresOverride);
            });
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object,
            null!,
            null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!,
            null!,
            null!,
            null!);
    }
}
