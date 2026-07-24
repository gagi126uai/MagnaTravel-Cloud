using System;
using System.Collections.Generic;
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
/// ADR-050 (2026-07-24, "Volver atrás deshace la anulación entera", decisión firmada del dueño): "Volver
/// atrás" sobre una reserva Cancelada por un ACTO de anular (<c>Reserva.AnnulledAt != null</c>) deshace TODO
/// lo que ese acto movió: revive los servicios cancelados EN ese acto, retira (tacha, F-6) el saldo a favor
/// que dejó el camino "anular sin factura", aborta la cancelación (<c>BookingCancellation</c>) que anclaba el
/// receivable del operador, y transiciona la reserva de vuelta a <c>InManagement</c>.
///
/// <para>Cubre: (i) el caso crítico — tras el undo, recalcular la plata NO vuelve a anular la reserva; (ii)
/// estado previo del servicio restaurado exacto; (iii) un servicio cancelado individualmente ANTES del acto
/// de anular NO revive; (iv) el saldo a favor intacto queda tachado con contra-asiento y permite re-anular
/// después; (v) el saldo ya consumido bloquea el undo con el texto exacto; (vi) una ND de multa ya emitida
/// bloquea el undo; (vii) un reembolso del operador ya recibido bloquea el undo (regresión del gate D2
/// existente); (viii) multimoneda — un crédito tachado por moneda; (ix) el BC-ancla queda Aborted.</para>
///
/// <para><b>Nota InMemory</b>: usa el flujo real del service. InMemory no soporta transacciones (la rama
/// IsRelational corre el mismo cuerpo sin transacción); la atomicidad/concurrencia REAL (idempotencia bajo
/// doble clic o carrera Serializable) se valida en integración Postgres — NO cubierta acá.</para>
/// </summary>
public class Adr050UndoAnnulmentTests
{
    private const string ValidReason = "Cliente desistio del viaje por fuerza mayor";
    private const string ValidUndoReason = "El cliente se arrepintio de la anulacion y quiere seguir viajando";

    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"undo-annulment-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static readonly IMapper Mapper =
        new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

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

    private static IHttpContextAccessor AdminContext()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "admin-1"),
            new(ClaimTypes.Role, "Admin"),
        };
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) };
        return new HttpContextAccessor { HttpContext = ctx };
    }

    private static IOperationalFinanceSettingsService SettingsService()
    {
        var mock = new Mock<IOperationalFinanceSettingsService>();
        mock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return mock.Object;
    }

    /// <summary>Service de cancelaciones REAL (no un mock): AbortActiveAnnulmentForRevertAsync solo toca
    /// _db + _auditService, asi que el ctor minimo con el resto mockeado alcanza.</summary>
    private static IBookingCancellationService BuildCancellationService(AppDbContext ctx) =>
        new BookingCancellationService(
            ctx, new Mock<IInvoiceService>().Object, new Mock<IApprovalRequestService>().Object,
            new Mock<IAuditService>().Object, NullLogger<BookingCancellationService>.Instance,
            SettingsService(), new Mock<IFiscalLiquidationCalculator>().Object,
            new Mock<IAdminUserCountService>().Object);

    /// <summary>ReservaService con el ancla de operador CABLEADA (Admin: bypassa authz).</summary>
    private static ReservaService BuildReservaServiceWithAnchor(AppDbContext ctx) =>
        new(ctx, Mapper, SettingsService(), BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: AdminContext(),
            autoStateService: null, auditService: null,
            cancellationService: BuildCancellationService(ctx));

    /// <summary>ReservaService SIN el ancla de operador (la mayoría de los tests no la necesitan: sin plata al
    /// operador nunca se crea BookingCancellation, y el undo simplemente no tiene nada que abortar).</summary>
    private static ReservaService BuildReservaService(AppDbContext ctx) =>
        new(ctx, Mapper, SettingsService(), BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: AdminContext());

    private static async Task<(int ReservaId, Guid ReservaPublicId, int ServiceId)> SeedFirmReservaAsync(
        AppDbContext ctx, decimal arsSale = 100m, string serviceStatus = "Confirmado")
    {
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-UNDO-1", Name = "Reserva test", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, TotalSale = arsSale, Balance = arsSale,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S-ARS", ConfirmationNumber = "ABC", Status = serviceStatus,
            Currency = "ARS", DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = arsSale, NetCost = 0m, Commission = arsSale,
            ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
        };
        ctx.Servicios.Add(service);
        ctx.Payments.Add(new Payment
        {
            ReservaId = reserva.Id, Amount = arsSale, Currency = "ARS", Method = "Transfer",
            Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        return (reserva.Id, reserva.PublicId, service.Id);
    }

    private static Task<ReservaDto> AnnulAsync(ReservaService service, Guid reservaPublicId) =>
        service.AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u1", actorUserName: "User One");

    private static Task<ReservaDto> UndoAsync(ReservaService service, Guid reservaPublicId, string? reason = null) =>
        service.RevertStatusAsync(
            reservaPublicId.ToString(),
            new RevertStatusRequest(EstadoReserva.InManagement, null, reason ?? ValidUndoReason),
            actorUserId: "admin-1", actorUserName: "Admin Uno", actorIsAdmin: true);

    // ================= (ii) estado previo restaurado + saldo/servicio ok =================

    [Fact]
    public async Task Undo_RestoresServiceStatus_ClearsAnnulledAt_AndVoidsCredit()
    {
        await using var ctx = NewContext();
        var (reservaId, reservaPublicId, serviceId) = await SeedFirmReservaAsync(ctx, arsSale: 100m);

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reservaPublicId);

        // Precondicion del test: la anulacion dejo su huella.
        var annulled = await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.Cancelled, annulled.Status);
        Assert.NotNull(annulled.AnnulledAt);
        Assert.Equal("u1", annulled.AnnulledByUserId);
        var cancelledService = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == serviceId);
        Assert.Equal("Cancelado", cancelledService.Status);
        Assert.Equal("Confirmado", cancelledService.StatusBeforeCancellation);

        var dto = await UndoAsync(service, reservaPublicId);

        Assert.Equal(EstadoReserva.InManagement, dto.Status);

        var reverted = await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.InManagement, reverted.Status);
        Assert.Null(reverted.AnnulledAt);
        Assert.Null(reverted.AnnulledByUserId);
        // Balance = 0: el servicio revivido vuelve a estar confirmado (ConfirmedSale=100) Y el cobro ORIGINAL
        // (nunca tocado, solo se tachó el puente) vuelve a contar como pagado (TotalPaid=100) -> 100-100=0,
        // EXACTAMENTE el mismo estado "pagado en firme" que tenía la reserva antes de anular.
        Assert.Equal(0m, reverted.Balance);

        var revivedService = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == serviceId);
        Assert.Equal("Confirmado", revivedService.Status); // restaurado EXACTO, no un generico.
        Assert.Null(revivedService.CancelledAt);
        Assert.Null(revivedService.CancelledByUserId);
        Assert.Null(revivedService.StatusBeforeCancellation);

        // El credito quedo tachado con CONTRA-ASIENTO (F-6): no se borro nada.
        var credit = await ctx.ClientCreditEntries.AsNoTracking().SingleAsync(c => c.SourceReservaId == reservaId);
        Assert.Equal(0m, credit.RemainingBalance);
        Assert.True(credit.IsFullyConsumed);
        var withdrawal = await ctx.ClientCreditWithdrawals.AsNoTracking()
            .SingleAsync(w => w.ClientCreditEntryId == credit.Id);
        Assert.Equal(WithdrawalKind.VoidedByAnnulmentUndo, withdrawal.Kind);
        Assert.Equal(100m, withdrawal.Amount);

        // El puente quedo TACHADO (soft-delete), no borrado.
        var bridge = await ctx.Payments.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(p => p.ReservaId == reservaId && p.Method == CancellationToClientCreditConverter.BridgeMethod);
        Assert.True(bridge.IsDeleted);
    }

    // ================= (i) EL CRÍTICO: tras el undo, recalcular NO vuelve a anular =================

    [Fact]
    public async Task Undo_ThenRecalculatingMoney_DoesNotReannulTheReserva()
    {
        await using var ctx = NewContext();
        var (reservaId, reservaPublicId, _) = await SeedFirmReservaAsync(ctx, arsSale: 100m);

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reservaPublicId);
        await UndoAsync(service, reservaPublicId);

        // Simula lo que el motor de estados/recalculo de plata haria en la proxima escritura de la reserva
        // (ADR-048): si el orden revive->transicion->persisters no se respetara, este recalculo posterior
        // podria "re-derivar" la reserva de vuelta a Cancelled al ver servicios ya no-cancelados con un
        // Status stale. No debe pasar: el commit del undo ya dejo todo consistente.
        await ReservaMoneyPersister.PersistAsync(ctx, reservaId, CancellationToken.None);

        var reserva = await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.InManagement, reserva.Status);
    }

    // ================= (iii) pre-cancelado individualmente (antes del acto de anular) NO revive =================

    [Fact]
    public async Task Undo_DoesNotRevive_ServiceCancelledIndividually_BeforeTheAnnulmentAct()
    {
        await using var ctx = NewContext();
        var (reservaId, reservaPublicId, hotelServiceId) = await SeedFirmReservaAsync(ctx, arsSale: 100m);

        // Segundo servicio, cancelado INDIVIDUALMENTE (uno por uno) ANTES del acto de anular — simulando lo
        // que MarkTypedServiceCancelledAsync/StampServiceCancellation dejarian.
        var preCancelled = new ServicioReserva
        {
            ReservaId = reservaId, ServiceType = "Traslado", ProductType = "Traslado",
            Description = "S-PRECANCELADO", ConfirmationNumber = "XYZ", Status = "Cancelado",
            StatusBeforeCancellation = "Solicitado",
            Currency = "ARS", DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = 10m, NetCost = 0m, Commission = 10m,
            CancelledAt = DateTime.UtcNow.AddMinutes(-30), CancelledByUserId = "vendedor-1",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Servicios.Add(preCancelled);
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx);
        // El acto de anular ocurre DESPUES del pre-cancelado (annulledAt > preCancelled.CancelledAt).
        await AnnulAsync(service, reservaPublicId);
        await UndoAsync(service, reservaPublicId);

        var revivedHotel = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == hotelServiceId);
        Assert.Equal("Confirmado", revivedHotel.Status); // este SI se revive (se cancelo EN el acto de anular).

        var stillCancelled = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == preCancelled.Id);
        Assert.Equal("Cancelado", stillCancelled.Status); // este NO se revive (decision (3) del dueño).
        Assert.NotNull(stillCancelled.CancelledAt);
    }

    // ================= (iv) saldo intacto -> re-anular DESPUES del undo funciona (crea credito nuevo) =================

    [Fact]
    public async Task Undo_ThenReAnnul_CreatesANewCredit_AndDoesNotGetBlockedByTheOldTachedBridge()
    {
        await using var ctx = NewContext();
        var (reservaId, reservaPublicId, _) = await SeedFirmReservaAsync(ctx, arsSale: 100m);

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reservaPublicId);
        await UndoAsync(service, reservaPublicId);

        // Tras el undo la reserva volvio a InManagement CON el cobro original vivo (nunca se tocó, solo el
        // puente se tachó) -> se puede volver a anular.
        var dto = await service.AnnulWithPaymentsToCreditAsync(
            reservaPublicId.ToString(), reason: ValidReason, actorUserId: "u2", actorUserName: "User Two");
        Assert.Equal(EstadoReserva.Cancelled, dto.Status);

        var credits = await ctx.ClientCreditEntries.AsNoTracking()
            .Where(c => c.SourceReservaId == reservaId).ToListAsync();
        Assert.Equal(2, credits.Count); // el viejo tachado + el nuevo.
        Assert.Single(credits, c => !c.IsFullyConsumed && c.RemainingBalance == 100m);

        var liveBridges = await ctx.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ReservaId == reservaId
                     && p.Method == CancellationToClientCreditConverter.BridgeMethod && !p.IsDeleted)
            .ToListAsync();
        Assert.Single(liveBridges); // solo el bridge NUEVO esta vivo; el viejo sigue tachado.
    }

    // ================= (v) saldo YA consumido -> bloquea con el texto exacto =================

    [Fact]
    public async Task Undo_Blocked_WhenTheAnnulCreditWasAlreadyConsumed()
    {
        await using var ctx = NewContext();
        var (reservaId, reservaPublicId, _) = await SeedFirmReservaAsync(ctx, arsSale: 100m);

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reservaPublicId);

        // El cliente ya USO el saldo a favor en otra reserva (simulado directo: RemainingBalance < CreditedAmount).
        var credit = await ctx.ClientCreditEntries.SingleAsync(c => c.SourceReservaId == reservaId);
        credit.RemainingBalance = 0m;
        credit.IsFullyConsumed = true;
        await ctx.SaveChangesAsync();

        // FIX B1 (review frontend, 2026-07-24): la excepcion tipada nueva, NO InvalidOperationException pelada
        // (aunque hereda de ella) — el frontend la distingue por Code, no por el largo del texto.
        var ex = await Assert.ThrowsAsync<UndoAnnulmentBlockedException>(() => UndoAsync(service, reservaPublicId));
        Assert.Equal("Ese saldo a favor ya se usó en otra reserva. No se puede deshacer la anulación.", ex.Message);
        Assert.Equal("UNDO_ANNULMENT_BLOCKED", UndoAnnulmentBlockedException.Code);
        Assert.IsAssignableFrom<InvalidOperationException>(ex); // sigue cayendo en un catch generico si hiciera falta.

        // Nada se toco: la reserva sigue Cancelled.
        var reserva = await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        Assert.Equal(EstadoReserva.Cancelled, reserva.Status);
    }

    // ================= (vi) ND de multa YA emitida -> bloquea =================

    [Fact]
    public async Task Undo_Blocked_WhenThePenaltyDebitNoteWasAlreadyIssued()
    {
        await using var ctx = NewContext();
        var (reservaId, reservaPublicId, _) = await SeedFirmReservaAsync(ctx, arsSale: 100m);

        var supplier = new Supplier { Name = "Operador Test", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reservaPublicId);

        // Una ND de multa YA se emitio para esta reserva (comprobante fiscal sellado — simetrico a la NC).
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reservaId, CustomerId = (await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId)).PayerId!.Value,
            SupplierId = supplier.Id, Status = BookingCancellationStatus.AwaitingOperatorRefund,
            Reason = "Multa del operador", DraftedAt = DateTime.UtcNow, DraftedByUserId = "u1",
            AmountPaidAtCancellation = 0m, EstimatedRefundAmount = 0m, ReceivedRefundAmount = 0m,
            DebitNoteInvoiceId = 999,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
            IsLegacyPreCancellationModel = false,
        });
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<UndoAnnulmentBlockedException>(() => UndoAsync(service, reservaPublicId));
        Assert.Equal("Ya se emitió la nota de débito de la multa. No se puede deshacer la anulación.", ex.Message);
    }

    // ================= (vii) refund del operador YA recibido -> bloquea (regresion del gate D2 existente) =================

    [Fact]
    public async Task Undo_Blocked_WhenOperatorAlreadyRefunded_RegressionOfExistingD2Gate()
    {
        await using var ctx = NewContext();
        var (reservaId, reservaPublicId, _) = await SeedFirmReservaAsync(ctx, arsSale: 100m);

        var supplier = new Supplier { Name = "Operador Test", IsActive = true };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reservaPublicId);

        var payerId = (await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId)).PayerId!.Value;
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reservaId, CustomerId = payerId, SupplierId = supplier.Id,
            Status = BookingCancellationStatus.ClientCreditApplied,
            Reason = "Reembolso ya recibido", DraftedAt = DateTime.UtcNow, DraftedByUserId = "u1",
            AmountPaidAtCancellation = 0m, EstimatedRefundAmount = 0m, ReceivedRefundAmount = 100m,
            FiscalSnapshot = new FiscalSnapshot { Source = ExchangeRateSource.Unset, FetchedAt = default },
            IsLegacyPreCancellationModel = false,
        });
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<UndoAnnulmentBlockedException>(() => UndoAsync(service, reservaPublicId));
        Assert.Contains("nota de credito, un saldo a favor o un reintegro del operador", ex.Message);
    }

    // ================= (viii) multimoneda: un credito tachado por moneda =================

    [Fact]
    public async Task Undo_MultiCurrency_VoidsOneCreditPerCurrency()
    {
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-UNDO-MULTI", Name = "Reserva multimoneda", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, TotalSale = 150m, Balance = 150m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Hotel", ProductType = "Hotel", Description = "S-ARS",
            ConfirmationNumber = "A1", Status = "Confirmado", Currency = "ARS",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 100m, NetCost = 0m, Commission = 100m,
            ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
        });
        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Hotel", ProductType = "Hotel", Description = "S-USD",
            ConfirmationNumber = "A2", Status = "Confirmado", Currency = "USD",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 50m, NetCost = 0m, Commission = 50m,
            ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
        });
        ctx.Payments.Add(new Payment
        {
            ReservaId = reserva.Id, Amount = 100m, Currency = "ARS", Method = "Transfer",
            Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
        });
        ctx.Payments.Add(new Payment
        {
            ReservaId = reserva.Id, Amount = 50m, Currency = "USD", Method = "Transfer",
            Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reserva.PublicId);
        await UndoAsync(service, reserva.PublicId);

        var credits = await ctx.ClientCreditEntries.AsNoTracking()
            .Where(c => c.SourceReservaId == reserva.Id).ToListAsync();
        Assert.Equal(2, credits.Count);
        Assert.All(credits, c => Assert.True(c.IsFullyConsumed));
        Assert.All(credits, c => Assert.Equal(0m, c.RemainingBalance));

        var bridges = await ctx.Payments.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.ReservaId == reserva.Id && p.Method == CancellationToClientCreditConverter.BridgeMethod)
            .ToListAsync();
        Assert.Equal(2, bridges.Count);
        Assert.All(bridges, b => Assert.True(b.IsDeleted));
    }

    // ================= (ix) el BC-ancla del operador queda Aborted =================

    [Fact]
    public async Task Undo_AbortsTheOperatorAnchorBookingCancellation()
    {
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        var supplier = new Supplier { Name = "Operador Test", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-UNDO-ANCHOR", Name = "Reserva con operador", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, Balance = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            Status = "Confirmado", NetCost = 50_000m, SalePrice = 75_000m, Currency = "ARS",
        });
        // Pago YA hecho al operador: sin factura, esto es lo que EnsureOperatorReceivableAnchorLinesAsync ancla.
        ctx.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50_000m,
            Currency = "ARS", Method = "Transferencia",
        });
        ctx.Payments.Add(new Payment
        {
            ReservaId = reserva.Id, Amount = 75_000m, Currency = "ARS", Method = "Transfer",
            Status = "Paid", EntryType = PaymentEntryTypes.Payment, AffectsCash = true, PaidAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = BuildReservaServiceWithAnchor(ctx);
        await AnnulAsync(service, reserva.PublicId);

        var anchorBc = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.ReservaId == reserva.Id);
        Assert.NotEqual(BookingCancellationStatus.Aborted, anchorBc.Status); // vivo, anclando el receivable.

        await UndoAsync(service, reserva.PublicId);

        var abortedBc = await ctx.BookingCancellations.AsNoTracking().SingleAsync(b => b.Id == anchorBc.Id);
        Assert.Equal(BookingCancellationStatus.Aborted, abortedBc.Status);
    }

    // ================= (viii ampliado) DirectCancel (sin cobros) tambien se puede deshacer =================

    [Fact]
    public async Task Undo_DirectCancel_WithoutPayments_RevivesService_NoCreditToVoid()
    {
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-UNDO-DIRECT", Name = "Reserva sin cobros", Status = EstadoReserva.Confirmed,
            PayerId = customer.Id, TotalSale = 100m, Balance = 100m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var svc = new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Hotel", ProductType = "Hotel", Description = "S-ARS",
            ConfirmationNumber = "ABC", Status = "Confirmado", Currency = "ARS",
            DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 100m, NetCost = 0m, Commission = 100m,
            ConfirmedAt = DateTime.UtcNow.AddDays(-1), CreatedAt = DateTime.UtcNow,
        };
        ctx.Servicios.Add(svc);
        await ctx.SaveChangesAsync();
        // Sin cobros -> DirectCancel: la reserva se anula SIN generar saldo a favor.

        var service = BuildReservaService(ctx);
        await AnnulAsync(service, reserva.PublicId);
        Assert.Empty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());

        var dto = await UndoAsync(service, reserva.PublicId);
        Assert.Equal(EstadoReserva.InManagement, dto.Status);

        var revived = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == svc.Id);
        Assert.Equal("Confirmado", revived.Status);
        Assert.Null(revived.CancelledAt);
    }

    // ================= Reserva Cancelada SIN acto de anular (AnnulledAt null) -> el undo NO aplica =================

    [Fact]
    public async Task Revert_ReservaCancelledWithoutAnnulmentAct_DoesNotAttemptUndo_JustFlipsStatus()
    {
        await using var ctx = NewContext();
        // Reserva que llega a Cancelled por cancelar sus servicios uno por uno, SIN pasar por "Anular"
        // (AnnulledAt queda null). Decision (3) del dueño: el revert NO intenta deshacer nada.
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "F-UNDO-NOACT", Name = "Reserva cancelada sin anular", Status = EstadoReserva.Cancelled,
            PayerId = customer.Id, Balance = 0m, AnnulledAt = null,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx);
        var dto = await UndoAsync(service, reserva.PublicId);

        Assert.Equal(EstadoReserva.InManagement, dto.Status);
        // No hay ClientCreditEntry ni BookingCancellation involucrados: el camino generico ni los toca.
        Assert.Empty(await ctx.ClientCreditEntries.AsNoTracking().ToListAsync());
    }

    // ================= FIX B1 (migracion, 2026-07-24): escenario LEGACY con AnnulledAt backfilleado =================
    //
    // Simula lo que deja el SQL de backfill de la migracion Adr050_UndoAnnulment sobre una fila que ya estaba
    // Cancelled ANTES de que la columna AnnulledAt existiera (la obra "anular sin factura" esta viva en PROD
    // desde 2026-07-23): AnnulledAt = MAX(CancelledAt) de los servicios cancelados de esa reserva. El SQL en si
    // no se puede correr en InMemory (no soporta CTEs/UPDATE crudo) — este test cubre el comportamiento del
    // MOTOR asumiendo que el backfill ya corrio, no el SQL de la migracion en si (eso se verifica leyendo la
    // migracion + corriendo la query de control contra PROD antes del deploy).

    [Fact]
    public async Task Undo_WithBackfilledAnnulledAt_RevivesServicesFromTheAct_AndExcludesEarlierIndividualCancellations()
    {
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente Test", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        // "annulledAt" es el valor que el backfill hubiera calculado: el MAX(CancelledAt) de los servicios
        // cancelados EN el acto. "individualCancellationBefore" simula un servicio cancelado uno-por-uno DIAS
        // antes de ese acto (no debe revivir).
        var annulledAt = new DateTime(2026, 07, 20, 10, 00, 00, DateTimeKind.Utc);
        var individualCancellationBefore = annulledAt.AddDays(-2);

        var reserva = new Reserva
        {
            NumeroReserva = "F-UNDO-BACKFILL", Name = "Reserva legacy backfilleada", Status = EstadoReserva.Cancelled,
            PayerId = customer.Id, Balance = 0m, AnnulledAt = annulledAt, AnnulledByUserId = null,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        // Dos servicios cancelados EN el acto (CancelledAt == annulledAt, el edge case del "empate exacto" que
        // el filtro CancelledAt >= AnnulledAt de ReviveServicesCancelledDuringAnnulment SI revive).
        var hotelInAct = new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Hotel", ProductType = "Hotel", Description = "S-HOTEL",
            ConfirmationNumber = "H1", Status = "Cancelado", StatusBeforeCancellation = "Confirmado",
            Currency = "ARS", DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 100m, NetCost = 0m,
            Commission = 100m, CancelledAt = annulledAt, CancelledByUserId = "u-legacy", CreatedAt = DateTime.UtcNow,
        };
        var transferInAct = new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Traslado", ProductType = "Traslado", Description = "S-TRASLADO",
            ConfirmationNumber = "T1", Status = "Cancelado", StatusBeforeCancellation = "Solicitado",
            Currency = "ARS", DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 20m, NetCost = 0m,
            Commission = 20m, CancelledAt = annulledAt, CancelledByUserId = "u-legacy", CreatedAt = DateTime.UtcNow,
        };
        // Cancelado individualmente ANTES del acto de anular: NO revive (decision (3) del dueño; el mismo
        // filtro que ya cubre el test "Undo_DoesNotRevive_ServiceCancelledIndividually...", pero acá arrancando
        // desde un AnnulledAt que vino del BACKFILL, no de un AnnulWithPaymentsToCreditAsync real).
        var preCancelled = new ServicioReserva
        {
            ReservaId = reserva.Id, ServiceType = "Asistencia", ProductType = "Asistencia", Description = "S-PRE",
            ConfirmationNumber = "P1", Status = "Cancelado", StatusBeforeCancellation = "Solicitado",
            Currency = "ARS", DepartureDate = DateTime.UtcNow.AddDays(15), SalePrice = 5m, NetCost = 0m,
            Commission = 5m, CancelledAt = individualCancellationBefore, CancelledByUserId = "vendedor-1",
            CreatedAt = DateTime.UtcNow,
        };
        ctx.Servicios.AddRange(hotelInAct, transferInAct, preCancelled);
        await ctx.SaveChangesAsync();

        var service = BuildReservaService(ctx);
        var dto = await UndoAsync(service, reserva.PublicId);

        Assert.Equal(EstadoReserva.InManagement, dto.Status);

        var revivedHotel = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == hotelInAct.Id);
        Assert.Equal("Confirmado", revivedHotel.Status);
        Assert.Null(revivedHotel.CancelledAt);

        var revivedTransfer = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == transferInAct.Id);
        Assert.Equal("Solicitado", revivedTransfer.Status);
        Assert.Null(revivedTransfer.CancelledAt);

        var stillCancelled = await ctx.Servicios.AsNoTracking().SingleAsync(s => s.Id == preCancelled.Id);
        Assert.Equal("Cancelado", stillCancelled.Status); // cancelado ANTES del acto backfilleado -> no revive.
        Assert.NotNull(stillCancelled.CancelledAt);

        var reverted = await ctx.Reservas.AsNoTracking().SingleAsync(r => r.Id == reserva.Id);
        Assert.Null(reverted.AnnulledAt);
    }
}
