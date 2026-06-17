using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-022 §4.5 (fix 2026-06-17): el camino legacy anidado de baja de cobro
/// (DELETE /api/reservas/{id}/payments/{pid} -> ReservaService.DeletePaymentAsync) DEBE escribir el
/// contra-asiento de caja, igual que el camino canonico de /api/payments. Antes no lo hacia y la caja
/// quedaba inflada (el asiento de ingreso seguia vivo sin su reversa).
/// </summary>
public class ReservaServiceDeletePaymentLedgerTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceDeletePaymentLedgerTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static IHttpContextAccessor BuildContextAccessor(string userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Name, "Admin Test") };
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private ReservaService BuildService(AppDbContext context)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(),
               NullLogger<ReservaService>.Instance, Mock.Of<IUserPermissionResolver>(),
               BuildContextAccessor("admin-1"));

    private static Reserva SeedReservaWithCashPayment(AppDbContext ctx, bool withLiveLedgerEntry)
    {
        var reserva = new Reserva
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = EstadoReserva.InManagement,
            TotalSale = 1000m
        };
        ctx.Reservas.Add(reserva);

        var payment = new Payment
        {
            Id = 1,
            PublicId = Guid.NewGuid(),
            ReservaId = 1,
            Amount = 100m,
            Currency = Monedas.ARS,
            Method = "Cash",
            Status = "Paid",
            AffectsCash = true,
            PaidAt = DateTime.UtcNow
        };
        ctx.Payments.Add(payment);

        if (withLiveLedgerEntry)
        {
            // Asiento vigente del cobro (el que CreatePaymentAsync habria escrito en el camino real).
            ctx.CashLedgerEntries.Add(CashLedgerEntryFactory.ForPayment(payment, "admin-1", "Admin Test"));
        }

        ctx.SaveChanges();
        return reserva;
    }

    [Fact]
    public async Task DeletePayment_Legacy_WritesContraEntry_AndNetsToZero()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        var reserva = SeedReservaWithCashPayment(ctx, withLiveLedgerEntry: true);
        var service = BuildService(ctx);

        await service.DeletePaymentAsync(reserva.PublicId.ToString(), "1", CancellationToken.None);

        var entries = await ctx.CashLedgerEntries.AsNoTracking()
            .Where(e => e.PaymentId == 1).ToListAsync();

        // Hay dos asientos: el original (ahora revertido) y su reversa.
        Assert.Equal(2, entries.Count);

        var original = entries.Single(e => !e.IsReversal);
        var reversal = entries.Single(e => e.IsReversal);

        Assert.True(original.IsReversed);                               // el viejo salio de "vigentes"
        Assert.Equal(CashMovementDirections.Income, original.Direction);

        Assert.Equal(original.Id, reversal.ReversedEntryId);            // la reversa apunta al original
        Assert.Equal(CashMovementDirections.Expense, reversal.Direction); // direccion invertida
        Assert.Equal(100m, reversal.Amount);
        Assert.False(reversal.IsReversed);

        // No queda NINGUN asiento vigente del cobro (ni reversado ni reversa): el neto de caja es 0.
        var liveEntries = entries.Where(e => !e.IsReversal && !e.IsReversed).ToList();
        Assert.Empty(liveEntries);
    }

    [Fact]
    public async Task UpdatePayment_Legacy_ChangingAmount_ResyncsLedger_NetIsNewAmount()
    {
        await using var ctx = new AppDbContext(_dbOptions);
        SeedReservaWithCashPayment(ctx, withLiveLedgerEntry: true); // cobro 100 + asiento vigente 100
        var service = BuildService(ctx);

        await service.UpdatePaymentAsync(1, 1, new Payment
        {
            Amount = 150m,                 // sube de 100 a 150
            Method = "Cash",
            Currency = Monedas.ARS,
            PaidAt = DateTime.UtcNow
        });

        var entries = await ctx.CashLedgerEntries.AsNoTracking()
            .Where(e => e.PaymentId == 1).ToListAsync();

        // viejo (100, revertido) + reversa (-100) + nuevo (150) = tres asientos.
        Assert.Equal(3, entries.Count);

        var live = entries.Where(e => !e.IsReversal && !e.IsReversed).ToList();
        var liveEntry = Assert.Single(live);                       // un unico asiento vigente
        Assert.Equal(CashMovementDirections.Income, liveEntry.Direction);
        Assert.Equal(150m, liveEntry.Amount);                      // con el monto NUEVO

        var old = entries.Single(e => !e.IsReversal && e.Amount == 100m && e.IsReversed);
        Assert.Contains(entries, e => e.IsReversal && e.ReversedEntryId == old.Id && e.Amount == 100m);

        // Neto de caja del cobro: 100 - 100 + 150 = 150 (Income - Expense).
        var net = entries.Sum(e => e.Direction == CashMovementDirections.Income ? e.Amount : -e.Amount);
        Assert.Equal(150m, net);
    }

    [Fact]
    public async Task DeletePayment_Legacy_NoLedgerEntry_DoesNotThrow_NoReversal()
    {
        // Cobro legacy sin asiento (anterior al backfill de ADR-022): el helper es no-op tolerante,
        // no crashea ni inventa una reversa.
        await using var ctx = new AppDbContext(_dbOptions);
        var reserva = SeedReservaWithCashPayment(ctx, withLiveLedgerEntry: false);
        var service = BuildService(ctx);

        await service.DeletePaymentAsync(reserva.PublicId.ToString(), "1", CancellationToken.None);

        Assert.False(await ctx.CashLedgerEntries.AnyAsync());
        var payment = await ctx.Payments.IgnoreQueryFilters().FirstAsync(p => p.Id == 1);
        Assert.True(payment.IsDeleted); // el soft-delete del cobro igual ocurre
    }
}
