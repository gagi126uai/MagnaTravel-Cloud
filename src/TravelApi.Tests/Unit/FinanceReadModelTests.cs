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
        var now = DateTime.UtcNow;

        // ADR-022 capa 4: la caja sale del LIBRO DE CAJA (asientos), no de unir Payments+SupplierPayments+
        // ManualCashMovements al vuelo. Un cobro (Income 500), un pago a proveedor (Expense 120) y un ajuste
        // manual (Expense 30): CashIn 500, CashOut 150, neto 350.
        context.CashLedgerEntries.AddRange(
            new CashLedgerEntry
            {
                Direction = CashMovementDirections.Income, Amount = 500m, Currency = Monedas.ARS,
                Method = "Transfer", OccurredAt = now, SourceType = CashLedgerSourceTypes.CustomerPayment
            },
            new CashLedgerEntry
            {
                Direction = CashMovementDirections.Expense, Amount = 120m, Currency = Monedas.ARS,
                Method = "Transfer", OccurredAt = now, SourceType = CashLedgerSourceTypes.SupplierPayment
            },
            new CashLedgerEntry
            {
                Direction = CashMovementDirections.Expense, Amount = 30m, Currency = Monedas.ARS,
                Method = "Cash", OccurredAt = now, SourceType = CashLedgerSourceTypes.ManualAdjustment
            });
        await context.SaveChangesAsync();

        // ADR-022 fix S2: la SALIDA de caja es costo -> se enmascara sin cobranzas.see_cost. Este test verifica
        // la AGREGACION de metricas, asi que usa un caller CON see_cost (ve CashOut real). El enmascarado en si
        // se prueba aparte (Adr022Tanda3Tests.CashSummary_WithoutSeeCost_MasksCashOut_KeepsCashIn).
        var service = BuildTreasuryCanSeeCost(context);

        var summary = await service.GetCashSummaryAsync(cancellationToken: CancellationToken.None);

        Assert.Equal(500m, summary.CashInThisMonth);
        Assert.Equal(150m, summary.CashOutThisMonth);
        Assert.Equal(350m, summary.NetCashThisMonth);
    }

    /// <summary>Tesoreria con un caller que SI ve costos (mismo patron que Adr022Tanda3Tests).</summary>
    private static TreasuryService BuildTreasuryCanSeeCost(AppDbContext context)
    {
        const string userId = "see-cost-user";
        var claims = new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId) };
        var identity = new System.Security.Claims.ClaimsIdentity(claims, "Test");
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(identity)
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new System.Collections.Generic.HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        return new TreasuryService(context, null!, financePositionService: null,
            httpContextAccessor: accessor, permissionResolver: resolver.Object);
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
