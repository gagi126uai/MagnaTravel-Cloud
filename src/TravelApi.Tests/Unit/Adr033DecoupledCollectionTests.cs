using System;
using System.Collections.Generic;
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
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-033 (2026-06-16): cobro desacoplado del estado operativo. Cubre lo que ADR-033 introduce y que NO
/// estaba cubierto por la cobertura de ADR-032 (que vive en Adr032CollectableStateRuleTests, ya actualizada):
/// <list type="bullet">
///   <item>B1/C9: cerrar una reserva NO marca su lead como Ganado (split de listas).</item>
///   <item>E5/B2: FC4 acepta destino Closed con deuda y conserva INV-096 / INV-095.</item>
///   <item>E4/B6: CancelServiceAsync rechaza en reserva no-viva (terminal/pre-venta).</item>
///   <item>E4/B4: revert Cancelled -> InManagement solo sin NC/credito/refund (query D2).</item>
///   <item>E7/A5: estado de cobro derivado POR MONEDA (deuda USD + saldo a favor ARS = "ConDeuda").</item>
/// </list>
/// </summary>
public class Adr033DecoupledCollectionTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

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

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto
              {
                  PublicId = r.PublicId, NumeroReserva = r.NumeroReserva, Name = r.Name, Status = r.Status
              });

        return new ReservaService(context, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    // =====================================================================================================
    // B1 / C9 — cerrar una reserva NO marca su lead como Ganado.
    // =====================================================================================================

    [Fact]
    public async Task ClosingReserva_DoesNotMarkSourceLeadAsWon()
    {
        // El lead-won usa ActiveCollectionStatuses (SIN Closed). Una reserva que avanza a Closed (Finalizada)
        // NO debe ganar el lead: cerrar no es una "venta nueva concretada".
        await using var ctx = NewContext();
        // Lead todavia NO ganado (simula un lead que nunca paso por el disparo del set-en-firme).
        var lead = new Lead { FullName = "Caro", Phone = "1199887766", Status = LeadStatus.Contacted };
        ctx.Leads.Add(lead);

        // Reserva en Traveling, saldada (Balance 0), linkeada al lead aun Contacted.
        var reserva = new Reserva
        {
            Name = "Viaje Caro",
            NumeroReserva = "RES-033-1",
            Status = EstadoReserva.Traveling,
            SourceLeadId = lead.Id,
            Balance = 0m,
            ConfirmedSale = 0m,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);
        // Traveling -> Closed (cierre limpio: Balance <= 0).
        await service.UpdateStatusAsync(reserva.Id, EstadoReserva.Closed, actorUserId: "u1");

        var refreshedReserva = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Closed, refreshedReserva!.Status); // sí se cerro

        var refreshedLead = await ctx.Leads.FindAsync(lead.Id);
        Assert.Equal(LeadStatus.Contacted, refreshedLead!.Status); // NO se gano por cerrar
        Assert.Null(refreshedLead.ClosedAt);
    }

    // =====================================================================================================
    // E5 / B2 — FC4 acepta destino Closed con deuda; conserva INV-096 (no firme) e INV-095 (per-moneda).
    // =====================================================================================================

    private static ClientCreditService NewClientCreditService(AppDbContext ctx)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true });
        return new ClientCreditService(
            ctx,
            Mock.Of<IBookingCancellationService>(),
            Mock.Of<IApprovalRequestService>(),
            Mock.Of<IAuditService>(),
            settings.Object,
            NullLogger<ClientCreditService>.Instance);
    }

    private static async Task<(Customer customer, ClientCreditEntry entry)> SeedCreditAsync(
        AppDbContext ctx, string currency, decimal amount)
    {
        var customer = new Customer { FullName = "Cliente FC4-033", TaxCondition = "Consumidor Final", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var entry = new ClientCreditEntry
        {
            CustomerId = customer.Id,
            Currency = currency,
            CreditedAmount = amount,
            RemainingBalance = amount,
            CreatedAt = DateTime.UtcNow,
            SourcePaymentId = 999, // origen sobrepago
        };
        ctx.ClientCreditEntries.Add(entry);
        await ctx.SaveChangesAsync();
        return (customer, entry);
    }

    private static async Task<Reserva> SeedTargetReservaAsync(
        AppDbContext ctx, int customerId, string status, string currency, decimal salePrice)
    {
        var reserva = new Reserva
        {
            NumeroReserva = "R-033-T",
            Name = "Reserva destino 033",
            Status = status,
            PayerId = customerId,
            TotalSale = salePrice,
            ConfirmedSale = salePrice,
            Balance = salePrice,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Hotel sustento",
            ConfirmationNumber = "OK-033",
            Status = "Confirmado",
            Currency = currency,
            DepartureDate = DateTime.UtcNow.AddDays(20),
            SalePrice = salePrice,
            NetCost = 0m,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();
        return reserva;
    }

    [Fact]
    public async Task Fc4_AppliedToClosedTargetWithDebt_NowSucceeds()
    {
        // ADR-033 E5: una Finalizada (Closed) con deuda en la moneda del bolsillo SI acepta saldo a favor.
        await using var ctx = NewContext();
        var (customer, entry) = await SeedCreditAsync(ctx, Monedas.ARS, 500m);
        var target = await SeedTargetReservaAsync(ctx, customer.Id, EstadoReserva.Closed, Monedas.ARS, 1000m);

        var service = NewClientCreditService(ctx);
        var request = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: 200m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: target.PublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

        await service.WithdrawAsync(entry.PublicId, request, userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        var withdrawal = await ctx.ClientCreditWithdrawals.AsNoTracking().FirstAsync();
        var bridge = await ctx.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(p => p.AppliedFromCreditWithdrawalId == withdrawal.Id);
        Assert.Equal(200m, bridge.Amount);
        Assert.Equal(target.Id, bridge.ReservaId);
    }

    [Fact]
    public async Task Fc4_AppliedToNonFirmTarget_StillThrowsInv096()
    {
        // ADR-033 E5: el contrato de FC4 se conserva — destino no firme (Budget) sigue rechazando con INV-096.
        await using var ctx = NewContext();
        var (customer, entry) = await SeedCreditAsync(ctx, Monedas.ARS, 500m);
        var target = await SeedTargetReservaAsync(ctx, customer.Id, EstadoReserva.Budget, Monedas.ARS, 1000m);

        var service = NewClientCreditService(ctx);
        var request = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: 100m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: target.PublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.WithdrawAsync(entry.PublicId, request, userId: "user1", userName: null, ct: CancellationToken.None));
        Assert.Equal("INV-096", ex.InvariantCode);
    }

    [Fact]
    public async Task Fc4_CurrencyMismatchOnClosedTarget_StillThrowsInv095()
    {
        // ADR-033 E5: la validacion per-moneda (INV-095) se conserva — un bolsillo USD no paga una deuda ARS,
        // ni siquiera en un destino Closed firme.
        await using var ctx = NewContext();
        var (customer, entry) = await SeedCreditAsync(ctx, Monedas.USD, 500m); // bolsillo en USD
        var target = await SeedTargetReservaAsync(ctx, customer.Id, EstadoReserva.Closed, Monedas.ARS, 1000m); // deuda ARS

        var service = NewClientCreditService(ctx);
        var request = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: 100m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: target.PublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.WithdrawAsync(entry.PublicId, request, userId: "user1", userName: null, ct: CancellationToken.None));
        Assert.Equal("INV-095", ex.InvariantCode);
    }

    // =====================================================================================================
    // E4 / B6 — CancelServiceAsync rechaza en reserva no-viva.
    // =====================================================================================================

    private static BookingCancellationService BuildCancellationService(AppDbContext ctx)
    {
        var settingsMock = new Mock<IOperationalFinanceSettingsService>();
        settingsMock.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true, OperatorRefundTimeoutDays = 60 });

        return new BookingCancellationService(
            ctx,
            Mock.Of<IInvoiceService>(),
            Mock.Of<IApprovalRequestService>(),
            Mock.Of<IAuditService>(),
            NullLogger<BookingCancellationService>.Instance,
            settingsMock.Object,
            Mock.Of<IFiscalLiquidationCalculator>(),
            Mock.Of<IAdminUserCountService>());
    }

    [Theory]
    [InlineData("Lost")]
    [InlineData("Cancelled")]
    [InlineData("Closed")]
    [InlineData("Quotation")]
    [InlineData("Budget")]    // G3 (2026-06-24): en pre-venta el servicio se BORRA, no se cancela
    [InlineData("Traveling")] // G3/ADR-035: en viaje no se cancela (se corrige por NC/ajuste)
    [InlineData("PendingOperatorRefund")]
    public async Task CancelService_OnDeadReserva_Rejects(string status)
    {
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-033-C",
            Name = "Reserva 033 cancel",
            PayerId = customer.Id,
            Status = status,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 1000m,
            SalePrice = 1500m,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        var service = BuildCancellationService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CancelServiceAsync(
                new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Intento en estado muerto"),
                userId: "vendedor-1", userName: "Vendedor", ct: CancellationToken.None));
    }

    [Fact]
    public async Task CancelService_OnLiveReserva_Succeeds()
    {
        // Contraste: en un estado vivo (Confirmed) el servicio SI se cancela (el gate de estado no estorba).
        await using var ctx = NewContext();
        var customer = new Customer { FullName = "Cliente", IsActive = true };
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        ctx.Customers.Add(customer);
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-033-CL",
            Name = "Reserva 033 cancel viva",
            PayerId = customer.Id,
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            Status = "Confirmado",
            NetCost = 1000m,
            SalePrice = 1500m,
            Currency = "ARS",
        };
        ctx.HotelBookings.Add(hotel);
        await ctx.SaveChangesAsync();

        var service = BuildCancellationService(ctx);

        var result = await service.CancelServiceAsync(
            new CancelServiceRequest(reserva.PublicId, "Hotel", hotel.PublicId, "Baja valida"),
            userId: "vendedor-1", userName: "Vendedor", ct: CancellationToken.None);

        Assert.Equal(1, result.CancelledServicesCount);
    }

    // =====================================================================================================
    // E4 / B4 — revert Cancelled -> InManagement solo sin huella fiscal/plata (query D2).
    // =====================================================================================================

    private static async Task<Reserva> SeedCancelledReservaAsync(AppDbContext ctx)
    {
        var customer = new Customer { FullName = "Cliente revert", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-033-RV",
            Name = "Reserva 033 revert",
            PayerId = customer.Id,
            Status = EstadoReserva.Cancelled,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();
        return reserva;
    }

    [Fact]
    public async Task RevertCancelled_WithoutTrace_ReopensToInManagement()
    {
        await using var ctx = NewContext();
        var reserva = await SeedCancelledReservaAsync(ctx);
        var service = NewReservaService(ctx);

        await service.RevertStatusAsync(
            reserva.PublicId.ToString(),
            new RevertStatusRequest(EstadoReserva.InManagement, AuthorizedBySuperiorUserId: null, Reason: "Cliente retoma el viaje"),
            actorUserId: "admin1", actorUserName: "Admin", actorIsAdmin: true, ct: CancellationToken.None);

        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.InManagement, refreshed!.Status);
    }

    [Fact]
    public async Task RevertCancelled_WithCreditNote_Blocked()
    {
        await using var ctx = NewContext();
        var reserva = await SeedCancelledReservaAsync(ctx);
        // Huella fiscal: una BookingCancellation con NC emitida (CreditNoteInvoiceId != null).
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reserva.Id,
            Status = BookingCancellationStatus.Closed,
            CreditNoteInvoiceId = 555,
        });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RevertStatusAsync(
                reserva.PublicId.ToString(),
                new RevertStatusRequest(EstadoReserva.InManagement, AuthorizedBySuperiorUserId: null, Reason: "Intento"),
                actorUserId: "admin1", actorUserName: "Admin", actorIsAdmin: true, ct: CancellationToken.None));
        Assert.Contains("nota de credito", ex.Message);

        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, refreshed!.Status); // no se reabrio
    }

    [Fact]
    public async Task RevertCancelled_WithClientCredit_Blocked()
    {
        await using var ctx = NewContext();
        var reserva = await SeedCancelledReservaAsync(ctx);
        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            Status = BookingCancellationStatus.ClientCreditApplied,
        };
        ctx.BookingCancellations.Add(bc);
        await ctx.SaveChangesAsync();

        // Huella de plata: un saldo a favor originado por esta cancelacion.
        ctx.ClientCreditEntries.Add(new ClientCreditEntry
        {
            CustomerId = reserva.PayerId!.Value,
            Currency = Monedas.ARS,
            CreditedAmount = 300m,
            RemainingBalance = 300m,
            CreatedAt = DateTime.UtcNow,
            BookingCancellationId = bc.Id,
        });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RevertStatusAsync(
                reserva.PublicId.ToString(),
                new RevertStatusRequest(EstadoReserva.InManagement, AuthorizedBySuperiorUserId: null, Reason: "Intento"),
                actorUserId: "admin1", actorUserName: "Admin", actorIsAdmin: true, ct: CancellationToken.None));

        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, refreshed!.Status);
    }

    // =====================================================================================================
    // E7 / A5 — estado de cobro derivado POR MONEDA.
    // =====================================================================================================

    [Fact]
    public void CollectionStatus_DebtUsdAndCreditArs_IsWithDebt()
    {
        // Debe USD (positivo) y tiene saldo a favor ARS (negativo) -> "ConDeuda" gana.
        var status = ReservaCollectionStatus.Derive(new[] { 100m, -50m });
        Assert.Equal(ReservaCollectionStatus.WithDebt, status);
    }

    [Fact]
    public void CollectionStatus_OnlyCredit_IsCreditBalance()
    {
        var status = ReservaCollectionStatus.Derive(new[] { -50m, 0m });
        Assert.Equal(ReservaCollectionStatus.CreditBalance, status);
    }

    [Fact]
    public void CollectionStatus_AllZero_IsSettled()
    {
        var status = ReservaCollectionStatus.Derive(new[] { 0m, 0m });
        Assert.Equal(ReservaCollectionStatus.Settled, status);
    }

    [Fact]
    public void CollectionStatus_Empty_IsSettled()
    {
        var status = ReservaCollectionStatus.Derive(Array.Empty<decimal>());
        Assert.Equal(ReservaCollectionStatus.Settled, status);
    }

    // =====================================================================================================
    // H1 (2026-06-24) — overload con senales de actividad: distingue "SinMovimientos" de "Saldado".
    // Bug: una reserva NUEVA en gestion sin cargos ni cobros mostraba "Pagada".
    // =====================================================================================================

    [Fact]
    public void CollectionStatus_NoChargesNoPayments_IsNoCharges()
    {
        // Reserva nueva: saldo 0, no vendio nada, no cobro nada -> "SinMovimientos" (NO "Saldado"/"pagada").
        var status = ReservaCollectionStatus.Derive(new[]
        {
            new ReservaCollectionLine(balance: 0m, hasCharges: false, hasPayments: false)
        });
        Assert.Equal(ReservaCollectionStatus.NoCharges, status);
    }

    [Fact]
    public void CollectionStatus_NoLines_IsNoCharges()
    {
        // Sin lineas de plata (reserva sin servicios): "SinMovimientos".
        var status = ReservaCollectionStatus.Derive(Array.Empty<ReservaCollectionLine>());
        Assert.Equal(ReservaCollectionStatus.NoCharges, status);
    }

    [Fact]
    public void CollectionStatus_ChargedAndPaidInFull_IsSettled()
    {
        // Hubo venta y se cobro todo (saldo 0 con actividad) -> "Saldado" (el "pagada" legitimo).
        var status = ReservaCollectionStatus.Derive(new[]
        {
            new ReservaCollectionLine(balance: 0m, hasCharges: true, hasPayments: true)
        });
        Assert.Equal(ReservaCollectionStatus.Settled, status);
    }

    [Fact]
    public void CollectionStatus_WithChargesButUnpaid_IsWithDebt()
    {
        // Vendio y aun no cobro: saldo positivo -> "ConDeuda".
        var status = ReservaCollectionStatus.Derive(new[]
        {
            new ReservaCollectionLine(balance: 1000m, hasCharges: true, hasPayments: false)
        });
        Assert.Equal(ReservaCollectionStatus.WithDebt, status);
    }

    [Fact]
    public void CollectionStatus_OverpaidCredit_IsCreditBalance()
    {
        // Cobro de mas: saldo a favor -> "SaldoAFavor" (gana sobre cualquier "sin movimientos").
        var status = ReservaCollectionStatus.Derive(new[]
        {
            new ReservaCollectionLine(balance: -200m, hasCharges: true, hasPayments: true)
        });
        Assert.Equal(ReservaCollectionStatus.CreditBalance, status);
    }

    [Fact]
    public void CollectionStatus_DebtInOneCurrency_NoActivityInOther_IsWithDebt()
    {
        // Deuda en una moneda gana, aunque otra moneda no tenga movimientos.
        var status = ReservaCollectionStatus.Derive(new[]
        {
            new ReservaCollectionLine(balance: 500m, hasCharges: true, hasPayments: false),
            new ReservaCollectionLine(balance: 0m, hasCharges: false, hasPayments: false)
        });
        Assert.Equal(ReservaCollectionStatus.WithDebt, status);
    }

    [Fact]
    public void CollectionStatus_PaymentButZeroBalanceAndNoCharges_IsSettled()
    {
        // Caso borde: entro plata pero no hay cargos (p. ej. saldo a favor que luego se aplico y quedo 0).
        // Hubo actividad (un cobro) -> "Saldado", no "SinMovimientos".
        var status = ReservaCollectionStatus.Derive(new[]
        {
            new ReservaCollectionLine(balance: 0m, hasCharges: false, hasPayments: true)
        });
        Assert.Equal(ReservaCollectionStatus.Settled, status);
    }

    // =====================================================================================================
    // A1 / E2 — cobro A NIVEL SERVICIO (CreatePaymentAsync) sobre una reserva FINALIZADA (Closed) con deuda.
    // La regla de dominio (IsCollectable/EnsureCollectable) ya esta cubierta a nivel unitario; aca probamos
    // el path de servicio end-to-end: que el cobro se REGISTRE, se impute y el Balance quede recalculado.
    // =====================================================================================================

    /// <summary>
    /// Arma un PaymentService real (resolver + mapper reales, sin HttpContext = ownership legacy/bypass).
    /// El cobro a nivel servicio necesita resolver el PublicId de la reserva e imputar contra la cuenta
    /// por moneda, por eso usamos las piezas reales y no mocks.
    /// </summary>
    private static PaymentService NewPaymentService(AppDbContext ctx)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());

        var mapper = new MapperConfiguration(c => c.AddProfile<TravelApi.Application.Mappings.MappingProfile>())
            .CreateMapper();

        return new PaymentService(
            ctx,
            new TravelApi.Infrastructure.Persistence.EntityReferenceResolver(ctx),
            mapper,
            settings.Object,
            NullLogger<PaymentService>.Instance);
    }

    /// <summary>
    /// Siembra una reserva en el estado pedido con un servicio Confirmado por <paramref name="salePrice"/>
    /// y la cuenta por moneda ya persistida (Balance = salePrice, sin pagos). Asi el cobro arranca de una
    /// deuda real en esa moneda y el recalculo posterior tiene de donde restar.
    /// </summary>
    private static async Task<Reserva> SeedReservaWithServiceDebtAsync(
        AppDbContext ctx, string status, string currency, decimal salePrice)
    {
        var customer = new Customer { FullName = "Cliente cobro 033", IsActive = true };
        ctx.Customers.Add(customer);
        await ctx.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-033-PAY",
            Name = "Reserva cobro 033",
            Status = status,
            PayerId = customer.Id,
            TotalSale = salePrice,
            ConfirmedSale = salePrice,
            Balance = salePrice,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Hotel sustento cobro",
            ConfirmationNumber = "OK-PAY-033",
            Status = "Confirmado",
            Currency = currency,
            DepartureDate = DateTime.UtcNow.AddDays(10),
            SalePrice = salePrice,
            NetCost = 0m,
            CreatedAt = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        // Persistimos la cuenta por moneda (ReservaMoneyByCurrency) para que el cobro arranque de una deuda
        // real en esta moneda (el recalculo de CreatePaymentAsync la vuelve a calcular tras imputar).
        await TravelApi.Infrastructure.Reservations.ReservaMoneyPersister.PersistAsync(
            ctx, reserva.Id, CancellationToken.None);
        return reserva;
    }

    [Fact]
    public async Task CreatePayment_OnClosedReservaWithDebt_IsRegisteredAndImputed()
    {
        // ADR-033 A1/E2: una Finalizada (Closed) con deuda SI admite cobro. El cobro debe registrarse,
        // imputarse a la cuenta por moneda y dejar el Balance recalculado (1000 - 400 = 600).
        await using var ctx = NewContext();
        var reserva = await SeedReservaWithServiceDebtAsync(ctx, EstadoReserva.Closed, Monedas.ARS, 1000m);
        var service = NewPaymentService(ctx);

        var request = new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            Amount = 400m,
            Method = "Transfer",
        };

        var dto = await service.CreatePaymentAsync(request, CancellationToken.None);

        // El cobro real quedo registrado (positivo, mueve caja).
        var payment = await ctx.Payments.AsNoTracking().FirstAsync(p => p.PublicId == dto.PublicId);
        Assert.Equal(400m, payment.Amount);
        Assert.True(payment.AffectsCash);

        // La cuenta por moneda quedo imputada: 1000 de deuda - 400 cobrados = 600 pendientes en ARS.
        var row = await ctx.ReservaMoneyByCurrency.AsNoTracking()
            .FirstAsync(m => m.ReservaId == reserva.Id && m.Currency == Monedas.ARS);
        Assert.Equal(600m, row.Balance);

        // No se gatillo el circuito de sobrepago (no hubo excedente): no hay saldo a favor.
        Assert.False(await ctx.ClientCreditEntries.AsNoTracking().AnyAsync(c => c.SourceReservaId == reserva.Id));
    }

    [Fact]
    public async Task CreatePayment_OverpayOnClosedReservaWithDebt_TriggersClientCreditBridge()
    {
        // ADR-033 A1/E2 + ADR-022 §4.9: un sobrepago sobre una Closed-con-deuda dispara el MISMO circuito de
        // sobrepago -> saldo a favor que en estados vivos. La regla nueva (cobrable = firme + deuda) NO lo
        // bloquea: la reserva tenia deuda al momento del guard. Se cobra 1500 sobre una deuda de 1000.
        await using var ctx = NewContext();
        var reserva = await SeedReservaWithServiceDebtAsync(ctx, EstadoReserva.Closed, Monedas.ARS, 1000m);
        var service = NewPaymentService(ctx);

        var request = new CreatePaymentRequest
        {
            ReservaId = reserva.PublicId.ToString(),
            Amount = 1500m,
            Method = "Transfer",
        };

        var dto = await service.CreatePaymentAsync(request, CancellationToken.None);

        // El cobro real (1500) entro a caja.
        var realPayment = await ctx.Payments.AsNoTracking().FirstAsync(p => p.PublicId == dto.PublicId);
        Assert.Equal(1500m, realPayment.Amount);
        Assert.True(realPayment.AffectsCash);

        // El excedente (500) se traslado al bolsillo del cliente como saldo a favor (origen sobrepago).
        var credit = await ctx.ClientCreditEntries.AsNoTracking()
            .FirstAsync(c => c.SourceReservaId == reserva.Id);
        Assert.Equal(reserva.PayerId, credit.CustomerId);
        Assert.Equal(Monedas.ARS, credit.Currency);
        Assert.Equal(500m, credit.CreditedAmount);
        Assert.Equal(500m, credit.RemainingBalance);

        // El puente del sobrepago: NEGATIVO, NO mueve caja, atado al cobro fuente. La regla nueva no lo bloqueo.
        var bridge = await ctx.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(p => p.ReservaId == reserva.Id
                && p.Method == TravelApi.Infrastructure.Reservations.OverpaymentCreditCleanup.BridgeMethod);
        Assert.Equal(-500m, bridge.Amount);
        Assert.False(bridge.AffectsCash);
        Assert.Equal(realPayment.Id, bridge.OriginalPaymentId);

        // El puente dejo la reserva saldada en ARS (1000 deuda - 1500 cobrados + 500 trasladados = 0).
        var row = await ctx.ReservaMoneyByCurrency.AsNoTracking()
            .FirstAsync(m => m.ReservaId == reserva.Id && m.Currency == Monedas.ARS);
        Assert.Equal(0m, row.Balance);
    }

    // =====================================================================================================
    // E4 / B4 (D2) — revert Cancelled -> InManagement BLOQUEADO cuando hubo devolucion del operador.
    // Ya hay tests para NC (CreditNoteInvoiceId) y para saldo a favor (ClientCreditEntry); faltaba el de
    // refund recibido (BookingCancellation.ReceivedRefundAmount > 0), tercer disyunto de la query D2.
    // =====================================================================================================

    [Fact]
    public async Task RevertCancelled_WithOperatorRefundReceived_Blocked()
    {
        await using var ctx = NewContext();
        var reserva = await SeedCancelledReservaAsync(ctx);
        // Huella de plata: el operador ya devolvio plata por esta cancelacion (ReceivedRefundAmount > 0).
        // Reabrir sin deshacer ese movimiento por su circuito dejaria la plata descuadrada -> la query D2 rechaza.
        ctx.BookingCancellations.Add(new BookingCancellation
        {
            ReservaId = reserva.Id,
            Status = BookingCancellationStatus.Closed,
            ReceivedRefundAmount = 300m,
        });
        await ctx.SaveChangesAsync();

        var service = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RevertStatusAsync(
                reserva.PublicId.ToString(),
                new RevertStatusRequest(EstadoReserva.InManagement, AuthorizedBySuperiorUserId: null, Reason: "Intento"),
                actorUserId: "admin1", actorUserName: "Admin", actorIsAdmin: true, ct: CancellationToken.None));
        Assert.Contains("reintegro del operador", ex.Message);

        var refreshed = await ctx.Reservas.FindAsync(reserva.Id);
        Assert.Equal(EstadoReserva.Cancelled, refreshed!.Status); // no se reabrio
    }
}
