using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// fix bugs #6 y #9 (2026-06-17): la conversion de SOBREPAGO a saldo a favor del cliente vive ahora en un
/// helper UNICO (<see cref="OverpaymentCreditConverter"/>) compartido por los tres caminos que cobran o
/// recalculan: el canonico (<c>PaymentService.CreatePaymentAsync</c>), el legacy anidado
/// (<c>ReservaService.AddPaymentAsync</c>, POST /api/reservas/{id}/payments) y la restauracion de un cobro
/// anulado (<c>PaymentService.RestorePaymentAsync</c>).
///
/// <para>Antes el legacy y el restore NO convertian el sobrepago, dejando el excedente atrapado como saldo
/// negativo en la reserva (invisible al bolsillo del cliente y a FC4). Estos tests cubren:</para>
/// <list type="bullet">
/// <item>BUG #6: cobro con sobrepago por el path legacy -> se crea el <see cref="ClientCreditEntry"/>;</item>
/// <item>BUG #9: restaurar un cobro que habia generado sobrepago -> se reconstruye el saldo a favor;</item>
/// <item>regresion: el path canonico sigue creando el saldo a favor igual;</item>
/// <item>sin sobrepago (pago exacto) por cualquier camino -> NO se crea credito.</item>
/// </list>
///
/// <para><b>Nota InMemory</b>: usa el flujo real de los servicios. InMemory no aplica CHECK/indices, pero el
/// comportamiento de cuenta (saldo de reserva, credito, puente) si se verifica.</para>
/// </summary>
public class OverpaymentCreditConverterTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public OverpaymentCreditConverterTests()
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

    private PaymentService BuildPaymentService(AppDbContext context)
        => new(context, new EntityReferenceResolver(context), _mapper, _settingsServiceMock.Object, NullLogger<PaymentService>.Instance);

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private ReservaService BuildReservaService(AppDbContext context)
        => new(context, _mapper, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

    /// <summary>
    /// Reserva confirmada con un servicio que sustenta una venta de <paramref name="salePrice"/>, con un
    /// pagador (PayerId) para que el sobrepago tenga bolsillo de cliente a donde ir.
    /// </summary>
    private static async Task SeedReservaWithPayerAsync(AppDbContext context, decimal salePrice = 100m)
    {
        context.Customers.Add(new Customer { Id = 1, FullName = "Cliente Test" });
        context.Reservas.Add(new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = EstadoReserva.Confirmed,
            PayerId = 1,
            TotalSale = salePrice,
            TotalCost = 0m,
            Balance = salePrice,
            TotalPaid = 0m
        });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", ConfirmationNumber = "ABC", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = salePrice, NetCost = 0m, Commission = salePrice,
            ConfirmedAt = DateTime.UtcNow.AddDays(-1),
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static Payment? FindLiveBridge(AppDbContext context, int sourcePaymentId)
        => context.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefault(p =>
                p.OriginalPaymentId == sourcePaymentId &&
                p.Method == OverpaymentCreditCleanup.BridgeMethod &&
                !p.IsDeleted);

    // ============================ BUG #6 — path legacy AddPaymentAsync ============================

    [Fact]
    public async Task LegacyAddPayment_WithOverpayment_CreatesClientCredit_AndBridge()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        // Cobro 150 por el path anidado (POST /api/reservas/{id}/payments) sobre una deuda de 100.
        var dto = await BuildReservaService(context).AddPaymentAsync(
            reservaId: 1,
            new Payment { Amount = 150m, Method = "Transfer" });

        var sourcePaymentId = await context.Payments
            .Where(p => p.PublicId == dto.PublicId).Select(p => p.Id).FirstAsync();

        // BUG #6: antes este path NO convertia el excedente. Ahora el saldo a favor de 50 existe en el bolsillo.
        var credit = await context.ClientCreditEntries.AsNoTracking()
            .FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(50m, credit.CreditedAmount);
        Assert.Equal(50m, credit.RemainingBalance);
        Assert.Equal(1, credit.CustomerId);
        Assert.Equal(Monedas.Normalizar("ARS"), credit.Currency);

        // El puente negativo saca el excedente del saldo de la reserva.
        var bridge = FindLiveBridge(context, sourcePaymentId);
        Assert.NotNull(bridge);
        Assert.Equal(-50m, bridge!.Amount);

        // La reserva queda saldada en 0 (150 cobrado - 50 trasladado = 100 = la venta), no sobre-pagada.
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, reserva.Balance);
    }

    [Fact]
    public async Task LegacyAddPayment_ExactPayment_DoesNotCreateCredit()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        // Pago EXACTO: 100 sobre deuda 100. No hay excedente, no debe crearse saldo a favor ni puente.
        var dto = await BuildReservaService(context).AddPaymentAsync(
            reservaId: 1,
            new Payment { Amount = 100m, Method = "Transfer" });

        var sourcePaymentId = await context.Payments
            .Where(p => p.PublicId == dto.PublicId).Select(p => p.Id).FirstAsync();

        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());
        Assert.Null(FindLiveBridge(context, sourcePaymentId));

        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, reserva.Balance);
    }

    // ============================ BUG #9 — restaurar un cobro anulado ============================

    [Fact]
    public async Task RestorePayment_ThatGeneratedOverpayment_RebuildsClientCredit()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var service = BuildPaymentService(context);

        // 1) Cobro con sobrepago (150 sobre 100) por el path canonico -> credito 50 + puente -50.
        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();
        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reservaPublicId.ToString(), Amount = 150m, Method = "Transfer" },
            CancellationToken.None);
        var sourcePaymentId = await context.Payments
            .Where(p => p.PublicId == dto.PublicId).Select(p => p.Id).FirstAsync();

        // 2) Anular el cobro: el cleanup limpia el saldo a favor (credito anulado + puente soft-deleted).
        await service.DeletePaymentAsync(dto.PublicId.ToString(), CancellationToken.None);
        var creditAfterDelete = await context.ClientCreditEntries.AsNoTracking()
            .FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(0m, creditAfterDelete.RemainingBalance);
        Assert.Null(FindLiveBridge(context, sourcePaymentId));

        // 3) RESTAURAR el cobro. BUG #9: antes el restore re-asentaba caja pero NO reconstruia el saldo a
        //    favor, dejando el excedente atrapado. Ahora la conversion vuelve a correr (idempotente).
        //    Se usa la sobrecarga por Id interno: la sobrecarga por PublicId no resuelve un pago soft-deleted
        //    (su resolver aplica el filtro global !IsDeleted). El controller de restore usa el Id real.
        await service.RestorePaymentAsync(sourcePaymentId, CancellationToken.None);

        // Hay un saldo a favor VIVO de 50 otra vez (el viejo quedo anulado; se creo uno fresco).
        var liveCredits = await context.ClientCreditEntries.AsNoTracking()
            .Where(c => c.CustomerId == 1 && c.RemainingBalance > 0m)
            .ToListAsync();
        var liveCredit = Assert.Single(liveCredits);
        Assert.Equal(50m, liveCredit.RemainingBalance);

        // Hay un puente vivo de -50 atado al cobro restaurado.
        var bridge = FindLiveBridge(context, sourcePaymentId);
        Assert.NotNull(bridge);
        Assert.Equal(-50m, bridge!.Amount);

        // La reserva queda saldada en 0 (no sobre-pagada con el excedente atrapado).
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, reserva.Balance);
    }

    [Fact]
    public async Task RestorePayment_WithoutOverpayment_DoesNotCreateCredit()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var service = BuildPaymentService(context);

        // Cobro EXACTO (100 sobre 100), sin sobrepago.
        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();
        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reservaPublicId.ToString(), Amount = 100m, Method = "Transfer" },
            CancellationToken.None);
        var sourcePaymentId = await context.Payments
            .Where(p => p.PublicId == dto.PublicId).Select(p => p.Id).FirstAsync();

        await service.DeletePaymentAsync(dto.PublicId.ToString(), CancellationToken.None);
        // Sobrecarga por Id interno: el pago soft-deleted no es resoluble por PublicId (filtro !IsDeleted).
        await service.RestorePaymentAsync(sourcePaymentId, CancellationToken.None);

        // Restaurar un cobro que nunca sobrepago no debe inventar un saldo a favor.
        Assert.Empty(await context.ClientCreditEntries.AsNoTracking().ToListAsync());

        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, reserva.Balance);
    }

    // ============================ REGRESION — el path canonico sigue igual ============================

    [Fact]
    public async Task CanonicalCreatePayment_WithOverpayment_StillCreatesClientCredit()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var service = BuildPaymentService(context);
        var reservaPublicId = await context.Reservas.AsNoTracking()
            .Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reservaPublicId.ToString(), Amount = 150m, Method = "Transfer" },
            CancellationToken.None);
        var sourcePaymentId = await context.Payments
            .Where(p => p.PublicId == dto.PublicId).Select(p => p.Id).FirstAsync();

        var credit = await context.ClientCreditEntries.AsNoTracking()
            .FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(50m, credit.RemainingBalance);

        var bridge = FindLiveBridge(context, sourcePaymentId);
        Assert.NotNull(bridge);
        Assert.Equal(-50m, bridge!.Amount);

        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, reserva.Balance);
    }
}
