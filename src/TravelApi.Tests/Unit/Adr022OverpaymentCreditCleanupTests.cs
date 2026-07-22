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
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-022 fix S1 (credito fantasma, 2026-06-11): cuando un cobro deja la reserva sobre-pagada, se crea un
/// saldo a favor del cliente (<see cref="ClientCreditEntry"/>) + un <see cref="Payment"/> puente negativo
/// (Method=SaldoAFavor, AffectsCash=false) que saca el excedente del saldo de la reserva.
///
/// <para>Estos tests verifican que al ANULAR o EDITAR A LA BAJA ese cobro:</para>
/// <list type="bullet">
/// <item>si el saldo a favor NO fue usado -> se revierte el puente y se anula el credito (no queda credito
///   fantasma ni la deuda se infla por el puente);</item>
/// <item>si el saldo a favor YA fue usado -> se BLOQUEA la operacion con un error de negocio (no se compensa
///   automaticamente);</item>
/// <item>en una edicion a la baja que sigue sobrepagando -> el credito y el puente se ajustan al nuevo
///   excedente.</item>
/// </list>
///
/// <para><b>Nota InMemory</b>: estos tests usan el flujo real (CreatePaymentAsync genera el sobrepago, luego
/// Delete/Update lo limpian). InMemory no aplica CHECK/indices, pero el comportamiento de cuenta (saldo,
/// credito, puente) si se verifica.</para>
/// </summary>
public class Adr022OverpaymentCreditCleanupTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public Adr022OverpaymentCreditCleanupTests()
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

    private async Task<PaymentDto> RegisterOverpayingPaymentAsync(AppDbContext context, decimal amount)
    {
        var service = BuildPaymentService(context);
        // El resolver acepta el PublicId de la reserva (los tests sembraron la reserva con Id=1).
        var reservaPublicId = await context.Reservas.AsNoTracking().Where(r => r.Id == 1).Select(r => r.PublicId).FirstAsync();
        return await service.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reservaPublicId.ToString(), Amount = amount, Method = "Transfer" },
            CancellationToken.None);
    }

    private static Payment? FindLiveBridge(AppDbContext context, int sourcePaymentId)
        => context.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefault(p =>
                p.OriginalPaymentId == sourcePaymentId &&
                p.Method == OverpaymentCreditCleanup.BridgeMethod &&
                !p.IsDeleted);

    // ============================ DELETE — sobrepago NO consumido ============================

    [Fact]
    public async Task Delete_OverpayingPayment_NotConsumed_RevertsBridge_AnnulsCredit_BalanceCorrect()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        // Cobro 150 sobre deuda 100 -> credito 50 + puente -50. Reserva queda en 0.
        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();

        // Precondicion: el sobrepago se creo (credito + puente) y la reserva quedo saldada.
        var creditBefore = await context.ClientCreditEntries.AsNoTracking().FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(50m, creditBefore.RemainingBalance);
        Assert.NotNull(FindLiveBridge(context, sourcePaymentId));

        // Anular el cobro.
        await BuildPaymentService(context).DeletePaymentAsync(paymentDto.PublicId.ToString(), CancellationToken.None);

        // El credito quedo anulado (no borrado: la fila sigue, con RemainingBalance 0 y IsFullyConsumed).
        var creditAfter = await context.ClientCreditEntries.AsNoTracking().FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(0m, creditAfter.RemainingBalance);
        Assert.True(creditAfter.IsFullyConsumed);

        // El puente quedo revertido (soft-deleted) -> no infla la deuda.
        Assert.Null(FindLiveBridge(context, sourcePaymentId));

        // La reserva vuelve a su deuda original (100), no a 150 ni a 50: ni el cobro vivo ni el puente fantasma.
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(100m, reserva.Balance);
    }

    // ============================ DELETE — sobrepago YA consumido ============================

    [Fact]
    public async Task Delete_OverpayingPayment_CreditConsumed_Throws_AndPreservesEverything()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();

        // Simular consumo parcial del saldo a favor (un retiro de 20 de los 50).
        var credit = await context.ClientCreditEntries.FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        credit.RemainingBalance = 30m; // 50 - 20 usado
        context.ClientCreditWithdrawals.Add(new ClientCreditWithdrawal
        {
            ClientCreditEntryId = credit.Id, Amount = 20m, Kind = WithdrawalKind.Transfer,
            ExecutedByUserId = "u1", ExecutedByUserName = "User 1", ExecutedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        // Anular debe BLOQUEAR.
        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => BuildPaymentService(context).DeletePaymentAsync(paymentDto.PublicId.ToString(), CancellationToken.None));
        Assert.Contains("saldo a favor", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Nada se toco: el cobro sigue vivo, el credito intacto en 30, el puente vivo.
        var paymentStill = await context.Payments.AsNoTracking().FirstAsync(p => p.Id == sourcePaymentId);
        Assert.False(paymentStill.IsDeleted);
        var creditStill = await context.ClientCreditEntries.AsNoTracking().FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(30m, creditStill.RemainingBalance);
        Assert.NotNull(FindLiveBridge(context, sourcePaymentId));
    }

    // ============================ EDIT a la baja — sigue sobrepagando ============================

    [Fact]
    public async Task Edit_OverpayingPayment_StillOverpays_AdjustsCreditAndBridgeToNewExcess()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        // Cobro 150 sobre deuda 100 -> excedente 50.
        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();

        // Editar a 120 -> nuevo excedente 20.
        await BuildPaymentService(context).UpdatePaymentAsync(
            paymentDto.PublicId.ToString(),
            new UpdatePaymentRequest { Amount = 120m, Method = "Transfer" },
            CancellationToken.None);

        // El saldo a favor vivo del cliente quedo en 20 (no en 50). El credito viejo se anulo y se re-creo
        // uno fresco por el nuevo excedente.
        var liveCredits = await context.ClientCreditEntries.AsNoTracking()
            .Where(c => c.CustomerId == 1 && c.RemainingBalance > 0m)
            .ToListAsync();
        var liveCredit = Assert.Single(liveCredits);
        Assert.Equal(20m, liveCredit.RemainingBalance);

        // El puente vivo (atado al nuevo credito por el cobro fuente) saca exactamente 20.
        var bridge = FindLiveBridge(context, sourcePaymentId);
        Assert.NotNull(bridge);
        Assert.Equal(-20m, bridge!.Amount);

        // La reserva queda saldada en 0 (120 cobrado - 20 trasladado = 100 = la venta).
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(0m, reserva.Balance);
    }

    // ============================ EDIT a la baja — deja de sobrepagar ============================

    [Fact]
    public async Task Edit_OverpayingPayment_NoLongerOverpays_AnnulsCredit_RemovesBridge()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();

        // Editar a 80 -> ya no sobrepaga (deuda 100). El saldo a favor desaparece y la reserva queda con
        // deuda 20.
        await BuildPaymentService(context).UpdatePaymentAsync(
            paymentDto.PublicId.ToString(),
            new UpdatePaymentRequest { Amount = 80m, Method = "Transfer" },
            CancellationToken.None);

        // No queda ningun saldo a favor vivo.
        var liveCredits = await context.ClientCreditEntries.AsNoTracking()
            .Where(c => c.CustomerId == 1 && c.RemainingBalance > 0m)
            .ToListAsync();
        Assert.Empty(liveCredits);

        // No queda puente vivo.
        Assert.Null(FindLiveBridge(context, sourcePaymentId));

        // La reserva muestra la deuda real: 100 venta - 80 cobrado = 20.
        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(20m, reserva.Balance);
    }

    [Fact]
    public async Task Edit_OverpayingPayment_CreditConsumed_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();

        // Consumir todo el saldo a favor.
        var credit = await context.ClientCreditEntries.FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        credit.RemainingBalance = 0m;
        credit.IsFullyConsumed = true;
        context.ClientCreditWithdrawals.Add(new ClientCreditWithdrawal
        {
            ClientCreditEntryId = credit.Id, Amount = 50m, Kind = WithdrawalKind.Transfer,
            ExecutedByUserId = "u1", ExecutedByUserName = "User 1", ExecutedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => BuildPaymentService(context).UpdatePaymentAsync(
                paymentDto.PublicId.ToString(),
                new UpdatePaymentRequest { Amount = 120m, Method = "Transfer" },
                CancellationToken.None));
        Assert.Contains("saldo a favor", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ============================ Path legacy (ReservaService) ============================

    [Fact]
    public async Task LegacyDelete_OverpayingPayment_NotConsumed_RevertsBridge_AnnulsCredit()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();

        // Borrar por el path legacy nested (api/reservas/{id}/payments/{id}).
        await BuildReservaService(context).DeletePaymentAsync(reservaId: 1, paymentId: sourcePaymentId);

        var creditAfter = await context.ClientCreditEntries.AsNoTracking().FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(0m, creditAfter.RemainingBalance);
        Assert.Null(FindLiveBridge(context, sourcePaymentId));

        var reserva = await context.Reservas.AsNoTracking().FirstAsync(r => r.Id == 1);
        Assert.Equal(100m, reserva.Balance);
    }

    [Fact]
    public async Task LegacyDelete_OverpayingPayment_CreditConsumed_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();

        var credit = await context.ClientCreditEntries.FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        credit.RemainingBalance = 10m; // consumido parcialmente
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildReservaService(context).DeletePaymentAsync(reservaId: 1, paymentId: sourcePaymentId));
        Assert.Contains("saldo a favor", ex.Message, StringComparison.OrdinalIgnoreCase);

        var paymentStill = await context.Payments.AsNoTracking().FirstAsync(p => p.Id == sourcePaymentId);
        Assert.False(paymentStill.IsDeleted);
    }

    // ============================================================================================
    // S1-bis (fix 2026-06-11): el Payment PUENTE del saldo a favor NO se puede mutar directamente.
    // Borrarlo/editarlo a mano deja el credito vivo y devuelve el excedente a la reserva -> el
    // excedente existiria dos veces. Solo el sistema (cleanup) lo manipula, y ese path opera sobre
    // la entidad (no pasa por Delete/UpdatePaymentAsync), asi que la guarda no lo bloquea.
    // ============================================================================================

    private static Guid GetLiveBridgePublicId(AppDbContext context, int sourcePaymentId)
    {
        var bridge = FindLiveBridge(context, sourcePaymentId);
        Assert.NotNull(bridge);
        return bridge!.PublicId;
    }

    [Fact]
    public async Task DirectDelete_OfBridge_IsBlocked()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();
        var bridgePublicId = GetLiveBridgePublicId(context, sourcePaymentId);

        // Intentar borrar el puente DIRECTAMENTE (como si el usuario clickeara la fila negativa).
        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => BuildPaymentService(context).DeletePaymentAsync(bridgePublicId.ToString(), CancellationToken.None));
        Assert.Contains("respaldo interno", ex.Message, StringComparison.OrdinalIgnoreCase);

        // El puente sigue vivo y el credito intacto: no se corrompio nada.
        Assert.NotNull(FindLiveBridge(context, sourcePaymentId));
        var credit = await context.ClientCreditEntries.AsNoTracking().FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(50m, credit.RemainingBalance);
    }

    [Fact]
    public async Task DirectEdit_OfBridge_IsBlocked()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();
        var bridgePublicId = GetLiveBridgePublicId(context, sourcePaymentId);

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(
            () => BuildPaymentService(context).UpdatePaymentAsync(
                bridgePublicId.ToString(),
                new UpdatePaymentRequest { Amount = -10m, Method = OverpaymentCreditCleanup.BridgeMethod },
                CancellationToken.None));
        Assert.Contains("respaldo interno", ex.Message, StringComparison.OrdinalIgnoreCase);

        // El puente conserva su monto original (-50): no se edito.
        var bridge = FindLiveBridge(context, sourcePaymentId);
        Assert.NotNull(bridge);
        Assert.Equal(-50m, bridge!.Amount);
    }

    [Fact]
    public async Task DirectDelete_OfBridge_LegacyPath_IsBlocked()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();
        var bridgeId = (await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p =>
            p.OriginalPaymentId == sourcePaymentId && p.Method == OverpaymentCreditCleanup.BridgeMethod && !p.IsDeleted)).Id;

        // Path legacy nested (api/reservas/{id}/payments/{id}).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildReservaService(context).DeletePaymentAsync(reservaId: 1, paymentId: bridgeId));
        Assert.Contains("respaldo interno", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.NotNull(FindLiveBridge(context, sourcePaymentId));
    }

    [Fact]
    public async Task DirectEdit_OfBridge_LegacyPath_IsBlocked()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();
        var bridgeId = (await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(p =>
            p.OriginalPaymentId == sourcePaymentId && p.Method == OverpaymentCreditCleanup.BridgeMethod && !p.IsDeleted)).Id;

        // El path legacy exige Amount > 0; usamos un positivo cualquiera: la guarda del puente debe
        // disparar ANTES que cualquier recalculo (el bloqueo no depende del monto enviado).
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => BuildReservaService(context).UpdatePaymentAsync(
                reservaId: 1, paymentId: bridgeId,
                new Payment { Amount = 10m, Method = OverpaymentCreditCleanup.BridgeMethod, PaidAt = DateTime.UtcNow }));
        Assert.Contains("respaldo interno", ex.Message, StringComparison.OrdinalIgnoreCase);

        var bridge = FindLiveBridge(context, sourcePaymentId);
        Assert.NotNull(bridge);
        Assert.Equal(-50m, bridge!.Amount);
    }

    // ============================================================================================
    // S1-bis punto 2: el puente se OCULTA del historial de cobros de la reserva (ambos list paths).
    // El flujo del sistema (borrar el COBRO FUENTE con credito intacto) sigue andando: el cleanup
    // soft-deletea el puente sin que la guarda lo bloquee.
    // ============================================================================================

    [Fact]
    public async Task ReservaPaymentsList_ExcludesBridge_FrontFacingPath()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        await RegisterOverpayingPaymentAsync(context, amount: 150m);

        // Lista que consume el front (/payments/reserva/{id}): solo el cobro real, NO el puente.
        var list = (await BuildPaymentService(context).GetPaymentsForReservaAsync(1, CancellationToken.None)).ToList();
        Assert.Single(list);
        Assert.DoesNotContain(list, p => p.Method == OverpaymentCreditCleanup.BridgeMethod);
        Assert.Equal(150m, list[0].Amount);

        // "Recaudado" = suma de esta lista = lo que el cliente pagó de verdad (150), no 100.
        Assert.Equal(150m, list.Sum(p => p.Amount));
    }

    [Fact]
    public async Task ReservaPaymentsList_ExcludesBridge_LegacyNestedPath()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        await RegisterOverpayingPaymentAsync(context, amount: 150m);

        var list = (await BuildReservaService(context).GetReservaPaymentsAsync(1)).ToList();
        Assert.Single(list);
        Assert.DoesNotContain(list, p => p.Method == OverpaymentCreditCleanup.BridgeMethod);
        Assert.Equal(150m, list[0].Amount);
    }

    [Fact]
    public async Task SystemFlow_DeleteSourcePayment_StillSoftDeletesBridge_GuardDoesNotBlockInternalPath()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaWithPayerAsync(context, salePrice: 100m);

        var paymentDto = await RegisterOverpayingPaymentAsync(context, amount: 150m);
        var sourcePaymentId = await context.Payments.Where(p => p.PublicId == paymentDto.PublicId).Select(p => p.Id).FirstAsync();
        Assert.NotNull(FindLiveBridge(context, sourcePaymentId));

        // Borrar el COBRO FUENTE (no el puente). El cleanup interno debe poder soft-deletear el puente:
        // la guarda de mutacion directa NO debe bloquear este path (el cleanup opera sobre la entidad).
        await BuildPaymentService(context).DeletePaymentAsync(paymentDto.PublicId.ToString(), CancellationToken.None);

        // El puente quedo revertido y el credito anulado: el flujo del sistema sigue intacto.
        Assert.Null(FindLiveBridge(context, sourcePaymentId));
        var credit = await context.ClientCreditEntries.AsNoTracking().FirstAsync(c => c.SourcePaymentId == sourcePaymentId);
        Assert.Equal(0m, credit.RemainingBalance);
    }
}
