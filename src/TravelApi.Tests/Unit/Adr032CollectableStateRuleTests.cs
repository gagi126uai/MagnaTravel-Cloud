using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-032 (2026-06-15) + ADR-033 (2026-06-16): regla de cobrabilidad de los pagos del cliente.
///
/// <para>ADR-033 SUPERSEDE la regla de estado de ADR-032: la cobrabilidad pasa a ser "venta firme +
/// deuda real" (incluye Closed con deuda), y el gate de ESTADO para editar/borrar un cobro se ELIMINA (la
/// inmutabilidad fiscal/puente queda). Este archivo conserva la cobertura de ADR-032 que sigue vigente y la
/// ACTUALIZA donde ADR-033 cambio el comportamiento. Verifica:</para>
/// <list type="bullet">
///   <item>la regla pura de dominio (IsCollectableStatus helper / IsSaleFirmStatus / EnsureCollectable);</item>
///   <item>alta de cobro por los DOS caminos rechaza en pre-venta/terminal-no-firme y AHORA permite Closed con deuda;</item>
///   <item>el rechazo del path anidado llega al CONTROLLER real como 409;</item>
///   <item>FC4 (saldo a favor aplicado) sigue tirando BusinessInvariantViolationException + INV-096 (B2);</item>
///   <item>el puente de sobrepago y el puente FC4 siguen creandose;</item>
///   <item>editar/borrar en terminal SIN atadura fiscal AHORA funciona (ADR-033 E3); con recibo/CAE sigue bloqueado por lo fiscal;</item>
///   <item>anular en estado terminal funciona y deja rastro (soft-delete + contra-asiento).</item>
/// </list>
///
/// <para><b>Nota InMemory</b>: el provider InMemory no aplica CHECK constraints ni transacciones. Estos
/// tests verifican el COMPORTAMIENTO de la regla y los servicios; la atomicidad real se cubre en integracion.</para>
/// </summary>
public class Adr032CollectableStateRuleTests
{
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsMock;

    public Adr032CollectableStateRuleTests()
    {
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        _settingsMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableNewCancellationFlow = true });
    }

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private PaymentService BuildPaymentService(AppDbContext context)
        => new(
            context,
            new EntityReferenceResolver(context),
            _mapper,
            _settingsMock.Object,
            NullLogger<PaymentService>.Instance);

    private ReservaService BuildReservaService(AppDbContext context)
        => new(
            context,
            _mapper,
            _settingsMock.Object,
            BuildUserManager(),
            NullLogger<ReservaService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null);

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

    /// <summary>
    /// Crea una reserva con UN servicio confirmado que sustenta su deuda exigible (ConfirmedSale).
    /// El estado se pasa por parametro para barrer cobrables y no-cobrables.
    /// </summary>
    private static async Task<Reserva> SeedReservaAsync(AppDbContext context, string status, decimal salePrice = 1000m)
    {
        var customer = new Customer { FullName = "Cliente ADR-032", TaxCondition = "Consumidor Final", IsActive = true };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var reserva = new Reserva
        {
            NumeroReserva = "R-032",
            Name = "Reserva ADR-032",
            Status = status,
            PayerId = customer.Id,
            TotalSale = salePrice,
            ConfirmedSale = salePrice,
            Balance = salePrice,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.Servicios.Add(new ServicioReserva
        {
            ReservaId = reserva.Id,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Hotel sustento",
            ConfirmationNumber = "OK-1",
            Status = "Confirmado",
            Currency = Monedas.ARS,
            DepartureDate = DateTime.UtcNow.AddDays(20),
            SalePrice = salePrice,
            NetCost = 0m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        return reserva;
    }

    // =====================================================================================================
    // Regla pura de dominio.
    // =====================================================================================================

    // ADR-036 (2026-06-21): ActiveCollectionStatuses = {InManagement, Confirmed}. Traveling salio (en viaje
    // no se cobra) y ToSettle murio.
    [Theory]
    [InlineData("InManagement")]
    [InlineData("Confirmed")]
    [InlineData("inmanagement")] // case-insensitive
    public void IsCollectableStatus_ReturnsTrue_ForCollectable(string status)
        => Assert.True(EstadoReserva.IsCollectableStatus(status));

    [Theory]
    [InlineData("Quotation")]
    [InlineData("Budget")]
    [InlineData("Lost")]
    [InlineData("Cancelled")]
    [InlineData("Closed")]
    [InlineData("Traveling")] // ADR-036: en viaje no se cobra
    [InlineData("ToSettle")]  // ADR-036: estado eliminado
    [InlineData("PendingOperatorRefund")]
    [InlineData("Archived")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCollectableStatus_ReturnsFalse_ForNonCollectable(string? status)
        => Assert.False(EstadoReserva.IsCollectableStatus(status));

    [Fact]
    public void EnsureCollectable_Throws_OnNonFirm_WithSaleFirmMessage()
    {
        // ADR-033: una Cancelada no es venta firme -> mensaje "pasala a En gestion primero".
        var reserva = new Reserva { Status = EstadoReserva.Cancelled, Balance = 1000m };
        var ex = Assert.Throws<InvalidOperationException>(() => reserva.EnsureCollectable());
        Assert.Equal(Reserva.NotSaleFirmForChargeMessage, ex.Message);
    }

    [Fact]
    public void EnsureCollectable_Throws_OnFirmButZeroBalance_WithNoPendingMessage()
    {
        // ADR-033: firme pero sin saldo -> mensaje distinto "no hay saldo pendiente para cobrar".
        var reserva = new Reserva { Status = EstadoReserva.Confirmed, Balance = 0m };
        var ex = Assert.Throws<InvalidOperationException>(() => reserva.EnsureCollectable());
        Assert.Equal(Reserva.NoPendingBalanceForChargeMessage, ex.Message);
    }

    [Fact]
    public void EnsureCollectable_Passes_OnClosedWithDebt()
    {
        // ADR-033 (caso semilla): una Finalizada con deuda AHORA es cobrable -> no tira.
        var reserva = new Reserva { Status = EstadoReserva.Closed, Balance = 500m };
        reserva.EnsureCollectable(); // no exception
        Assert.True(reserva.IsCollectable());
    }

    // ADR-036 (2026-06-21): SaleFirmStatuses = {InManagement, Confirmed, Closed}. Traveling salio (en viaje
    // no se cobra; prepago puro) y ToSettle murio.
    [Theory]
    [InlineData("InManagement")]
    [InlineData("Confirmed")]
    [InlineData("Closed")] // ADR-033: Closed firme
    [InlineData("closed")] // case-insensitive
    public void IsSaleFirmStatus_ReturnsTrue_ForFirm(string status)
        => Assert.True(EstadoReserva.IsSaleFirmStatus(status));

    [Theory]
    [InlineData("Quotation")]
    [InlineData("Budget")]
    [InlineData("Lost")]
    [InlineData("Cancelled")]
    [InlineData("Traveling")] // ADR-036: en viaje no es firme cobrable
    [InlineData("ToSettle")]  // ADR-036: estado eliminado
    [InlineData("PendingOperatorRefund")]
    [InlineData("Archived")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSaleFirmStatus_ReturnsFalse_ForNonFirm(string? status)
        => Assert.False(EstadoReserva.IsSaleFirmStatus(status));

    // =====================================================================================================
    // ALTA — Camino A (PaymentService.CreatePaymentAsync).
    // =====================================================================================================

    // ADR-036 (2026-06-21): cobrable = {InManagement, Confirmed, Closed-con-deuda}. Traveling y ToSettle
    // salieron de la lista (en viaje no se cobra; ToSettle murio).
    [Theory]
    [InlineData("InManagement")]
    [InlineData("Confirmed")]
    [InlineData("Closed")] // ADR-033: Finalizada con deuda AHORA es cobrable.
    public async Task CreatePayment_OnCollectable_Succeeds(string status)
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, status);
        var service = BuildPaymentService(context);

        var dto = await service.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        Assert.Equal(300m, dto.Amount);
    }

    [Theory]
    [InlineData("Quotation")]
    [InlineData("Budget")]
    [InlineData("Lost")]
    [InlineData("Cancelled")]
    [InlineData("Traveling")] // ADR-036: en viaje no se cobra
    [InlineData("PendingOperatorRefund")]
    [InlineData("Archived")]
    public async Task CreatePayment_OnNonCollectable_Rejects(string status)
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, status);
        var service = BuildPaymentService(context);

        // Tanda de saneo (2026-07-22): PaymentService.CreatePaymentAsync ahora rechaza con
        // PaymentValidationException (mensaje de negocio), no con InvalidOperationException "a secas". xUnit
        // exige tipo EXACTO en Assert.ThrowsAsync<T>, asi que el test se actualiza al tipo nuevo.
        await Assert.ThrowsAsync<PaymentValidationException>(() =>
            service.CreatePaymentAsync(
                new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
                CancellationToken.None));

        // No quedo ningun cobro vivo.
        Assert.Equal(0, await context.Payments.CountAsync(p => !p.IsDeleted));
    }

    // =====================================================================================================
    // ALTA — Camino B (endpoint anidado, ReservaService.AddPaymentAsync) — EL AGUJERO.
    // =====================================================================================================

    // ADR-036 (2026-06-21): cobrable por el path anidado = {InManagement, Confirmed, Closed-con-deuda}.
    [Theory]
    [InlineData("InManagement")]
    [InlineData("Confirmed")]
    [InlineData("Closed")] // ADR-033: Finalizada con deuda AHORA es cobrable por el path anidado tambien.
    public async Task NestedAddPayment_OnCollectable_Succeeds(string status)
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, status);
        var service = BuildReservaService(context);

        var dto = await service.AddPaymentAsync(reserva.Id, new Payment { Amount = 250m, Method = "Cash" });

        Assert.Equal(250m, dto.Amount);
    }

    [Theory]
    [InlineData("Quotation")]
    [InlineData("Budget")]
    [InlineData("Lost")]
    [InlineData("Cancelled")]
    [InlineData("Traveling")] // ADR-036: en viaje no se cobra
    [InlineData("PendingOperatorRefund")]
    [InlineData("Archived")]
    public async Task NestedAddPayment_OnNonCollectable_Rejects(string status)
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, status);
        var service = BuildReservaService(context);

        // Antes de ADR-032 este path NO miraba el estado: cobraba en Cancelada/Perdida. Ahora rechaza.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddPaymentAsync(reserva.Id, new Payment { Amount = 250m, Method = "Cash" }));

        Assert.Equal(0, await context.Payments.CountAsync(p => !p.IsDeleted));
    }

    // =====================================================================================================
    // D2 — el rechazo del endpoint anidado llega al CONTROLLER real como 409 Conflict.
    // =====================================================================================================

    [Fact]
    public async Task NestedAddPayment_OnCancelled_ThroughController_Returns409()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Cancelled);
        var service = BuildReservaService(context);

        var controller = new ReservasController(
            service,
            Mock.Of<IVoucherService>(),
            Mock.Of<ITimelineService>(),
            Mock.Of<ISupplierService>(),
            Mock.Of<IEntityReferenceResolver>(),
            Mock.Of<IBookingService>(),
            NullLogger<ReservasController>.Instance);

        var result = await controller.AddPayment(
            reserva.PublicId.ToString(),
            new ReservationPaymentUpsertRequest(
                Amount: 100m,
                PaidAt: DateTime.UtcNow,
                Method: "Cash",
                Reference: null,
                Notes: null),
            CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    // =====================================================================================================
    // B1 — FC4 (saldo a favor aplicado) conserva BusinessInvariantViolationException + INV-096.
    // =====================================================================================================

    [Fact]
    public async Task Fc4_AppliedToNonCollectable_StillThrowsInv096_NotGenericInvalidOperation()
    {
        await using var context = CreateContext();
        var service = BuildClientCreditService(context);

        var customer = new Customer { FullName = "Cliente FC4", TaxCondition = "Consumidor Final", IsActive = true };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var entry = new ClientCreditEntry
        {
            CustomerId = customer.Id,
            Currency = Monedas.ARS,
            CreditedAmount = 500m,
            RemainingBalance = 500m,
            CreatedAt = DateTime.UtcNow,
            SourcePaymentId = 999, // origen sobrepago
        };
        context.ClientCreditEntries.Add(entry);

        var target = await SeedReservaAsync(context, EstadoReserva.Budget); // destino NO cobrable
        target.PayerId = customer.Id;
        await context.SaveChangesAsync();

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

    // =====================================================================================================
    // Puentes: siguen creandose en estados cobrables (no se rompen).
    // =====================================================================================================

    [Fact]
    public async Task OverpaymentBridge_OnCollectable_StillCreatesClientCredit()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed, salePrice: 1000m);
        var service = BuildPaymentService(context);

        // Cobro que sobrepaga (1500 > 1000) en una reserva cobrable -> debe generar saldo a favor.
        await service.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 1500m, Method = "Transfer" },
            CancellationToken.None);

        var credit = await context.ClientCreditEntries.AsNoTracking().FirstOrDefaultAsync(c => c.SourceReservaId == reserva.Id);
        Assert.NotNull(credit);
        Assert.Equal(500m, credit!.CreditedAmount);
    }

    [Fact]
    public async Task Fc4Bridge_OnCollectableTarget_StillCreatesBridgePayment()
    {
        await using var context = CreateContext();
        var service = BuildClientCreditService(context);

        var customer = new Customer { FullName = "Cliente FC4 OK", TaxCondition = "Consumidor Final", IsActive = true };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var entry = new ClientCreditEntry
        {
            CustomerId = customer.Id,
            Currency = Monedas.ARS,
            CreditedAmount = 500m,
            RemainingBalance = 500m,
            CreatedAt = DateTime.UtcNow,
            SourcePaymentId = 999,
        };
        context.ClientCreditEntries.Add(entry);

        var target = await SeedReservaAsync(context, EstadoReserva.Confirmed); // destino cobrable
        target.PayerId = customer.Id;
        await context.SaveChangesAsync();

        var request = new WithdrawClientCreditRequest(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: 200m,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: target.PublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

        await service.WithdrawAsync(entry.PublicId, request, userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        var withdrawal = await context.ClientCreditWithdrawals.AsNoTracking().FirstAsync();
        var bridge = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(p => p.AppliedFromCreditWithdrawalId == withdrawal.Id);
        Assert.Equal(200m, bridge.Amount);
        Assert.False(bridge.AffectsCash);
        Assert.Equal(target.Id, bridge.ReservaId);
    }

    // =====================================================================================================
    // ADR-033 (E3/A2) habia liberado editar/borrar en terminal sin atadura fiscal. ADR-035 (2026-06-19,
    // Decision punto 3) REINTRODUJO un gate ACOTADO a los TERMINALES: editar/borrar un cobro NO se permite en
    // {Closed, Cancelled, Lost, PendingOperatorRefund} — ahi la correccion es ANULAR con rastro
    // (AnnulPaymentAsync, cubierto mas abajo). Los tests de abajo reflejan la regla NUEVA (409 en terminal).
    // El guard fiscal y la anulacion siguen igual.
    // =====================================================================================================

    [Fact]
    public async Task UpdatePayment_OnTerminal_RejectedByStateGate_Adr035()
    {
        await using var context = CreateContext();
        // Creamos el cobro mientras la reserva es cobrable, luego la pasamos a terminal (sin recibo/CAE).
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        await MoveReservaToStatusAsync(context, reserva.Id, EstadoReserva.Cancelled);

        // ADR-035: en terminal NO se edita el cobro (la salida es anularlo). 409 (PaymentValidationException,
        // Tanda de saneo 2026-07-22: el camino CANONICO ya no tira InvalidOperationException "a secas").
        await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.UpdatePaymentAsync(
                p.PublicId.ToString(),
                new UpdatePaymentRequest { Amount = 400m, Method = "Cash" },
                CancellationToken.None));

        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
        Assert.Equal(300m, payment.Amount); // sin cambios
    }

    [Fact]
    public async Task DeletePayment_OnTerminal_RejectedByStateGate_Adr035()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        await MoveReservaToStatusAsync(context, reserva.Id, EstadoReserva.Closed);

        // ADR-035: en terminal NO se borra el cobro (la salida es anularlo). 409 (PaymentValidationException,
        // Tanda de saneo 2026-07-22: el camino CANONICO ya no tira InvalidOperationException "a secas").
        await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.DeletePaymentAsync(p.PublicId.ToString(), CancellationToken.None));

        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
        Assert.False(payment.IsDeleted); // sigue vivo
    }

    [Fact]
    public async Task NestedDeletePayment_OnTerminal_RejectedByStateGate_Adr035()
    {
        // ADR-035 (2026-06-19): el path LEGACY anidado (ReservaService.DeletePaymentAsync int-overload, via
        // ReservasController DELETE /api/reservas/{id}/payments/{paymentId}) AHORA tambien gatea por estado,
        // igual que el camino canonico. Antes (ADR-033) este path borraba libre en terminal — el agujero.
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var reservaService = BuildReservaService(context);
        var p = await reservaService.AddPaymentAsync(reserva.Id, new Payment { Amount = 300m, Method = "Cash" });
        var paymentId = await context.Payments.Where(x => x.PublicId == p.PublicId).Select(x => x.Id).FirstAsync();

        await MoveReservaToStatusAsync(context, reserva.Id, EstadoReserva.Cancelled);

        // En terminal NO se borra el cobro (sin recibo/CAE): la salida con rastro es anularlo.
        // 409 (InvalidOperationException), mismo bloqueo que el camino canonico (PaymentService).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reservaService.DeletePaymentAsync(reserva.Id, paymentId));

        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == paymentId);
        Assert.False(payment.IsDeleted); // sigue vivo
    }

    [Fact]
    public async Task NestedUpdatePayment_OnTerminal_RejectedByStateGate_Adr035()
    {
        // ADR-035 (2026-06-19): equivalente del de arriba para EDITAR por el path legacy anidado
        // (ReservaService.UpdatePaymentAsync int-overload, via ReservasController PUT
        // /api/reservas/{id}/payments/{paymentId}). En terminal NO se edita; la salida es anular.
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var reservaService = BuildReservaService(context);
        var p = await reservaService.AddPaymentAsync(reserva.Id, new Payment { Amount = 300m, Method = "Cash" });
        var paymentId = await context.Payments.Where(x => x.PublicId == p.PublicId).Select(x => x.Id).FirstAsync();

        await MoveReservaToStatusAsync(context, reserva.Id, EstadoReserva.Closed);

        // En terminal NO se edita el cobro: 409 (InvalidOperationException), mismo bloqueo que el canonico.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reservaService.UpdatePaymentAsync(
                reserva.Id, paymentId, new Payment { Amount = 400m, Method = "Transfer" }));

        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == paymentId);
        Assert.Equal(300m, payment.Amount); // sin cambios
    }

    // =====================================================================================================
    // ADR-035 (2026-06-19) — CRUCE DE COHERENCIA EFECTIVO: CanEditOrDeletePayment vs el enforcement REAL de
    // los DOS caminos (canonico PaymentService y legacy anidado ReservaService int-overloads). El test
    // ReservaCapabilitiesCrossCheckTests.CanEditOrDeletePayment_MatchesNonTerminalStates_Exactly cruza la
    // politica contra su PROPIO conjunto de terminales; este la cruza contra lo que los servicios HACEN, que
    // es lo que protege la plata. Sin esto, un camino podia desincronizarse en silencio (el agujero que
    // motivo este fix: el legacy no gateaba).
    // =====================================================================================================

    [Theory]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    [InlineData(EstadoReserva.Lost)]
    [InlineData(EstadoReserva.PendingOperatorRefund)]
    public async Task EditOrDeletePayment_OnTerminal_BothPathsRejectLikeThePolicy_Adr035(string terminalStatus)
    {
        // Precondicion del cruce: la politica dice que en este estado NO se edita/borra. Si esto cambiara,
        // el InlineData esta mal y el test deja de tener sentido.
        Assert.False(
            ReservaCapabilityPolicy
                .For(new ReservaCapabilityContext(terminalStatus, 0m, false, false, false, false))
                .CanEditOrDeletePayment.Allowed,
            $"La politica deberia bloquear editar/borrar en {terminalStatus}.");

        // --- Camino CANONICO (PaymentService /api/payments/{id}) ---
        await using (var context = CreateContext())
        {
            var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
            var paymentService = BuildPaymentService(context);
            var p = await paymentService.CreatePaymentAsync(
                new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
                CancellationToken.None);
            await MoveReservaToStatusAsync(context, reserva.Id, terminalStatus);

            // Tanda de saneo (2026-07-22): el camino CANONICO (PaymentService) ya tira PaymentValidationException,
            // no InvalidOperationException "a secas" — el camino LEGACY de abajo todavia no se convirtio (queda
            // fuera de esta obra), por eso sigue esperando el tipo base.
            await Assert.ThrowsAsync<PaymentValidationException>(() =>
                paymentService.UpdatePaymentAsync(
                    p.PublicId.ToString(),
                    new UpdatePaymentRequest { Amount = 400m, Method = "Cash" },
                    CancellationToken.None));
            await Assert.ThrowsAsync<PaymentValidationException>(() =>
                paymentService.DeletePaymentAsync(p.PublicId.ToString(), CancellationToken.None));
        }

        // --- Camino LEGACY anidado (ReservaService int-overloads, /api/reservas/{id}/payments/{paymentId}) ---
        await using (var context = CreateContext())
        {
            var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
            var reservaService = BuildReservaService(context);
            var p = await reservaService.AddPaymentAsync(reserva.Id, new Payment { Amount = 300m, Method = "Cash" });
            var paymentId = await context.Payments.Where(x => x.PublicId == p.PublicId).Select(x => x.Id).FirstAsync();
            await MoveReservaToStatusAsync(context, reserva.Id, terminalStatus);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reservaService.UpdatePaymentAsync(
                    reserva.Id, paymentId, new Payment { Amount = 400m, Method = "Transfer" }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                reservaService.DeletePaymentAsync(reserva.Id, paymentId));
        }
    }

    [Fact]
    public async Task EditOrDeletePayment_OnFirmState_BothPathsAllowLikeThePolicy_Adr035()
    {
        // Contracara: en un estado firme NO-terminal (Confirmed) la politica habilita editar/borrar y AMBOS
        // caminos efectivamente lo permiten (sin atadura fiscal). Asi el cruce verifica los dos sentidos.
        Assert.True(
            ReservaCapabilityPolicy
                .For(new ReservaCapabilityContext(EstadoReserva.Confirmed, 300m, false, false, false, false))
                .CanEditOrDeletePayment.Allowed);

        // --- Camino CANONICO ---
        await using (var context = CreateContext())
        {
            var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
            var paymentService = BuildPaymentService(context);
            var p = await paymentService.CreatePaymentAsync(
                new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
                CancellationToken.None);

            await paymentService.UpdatePaymentAsync(
                p.PublicId.ToString(),
                new UpdatePaymentRequest { Amount = 400m, Method = "Cash" },
                CancellationToken.None);
            await paymentService.DeletePaymentAsync(p.PublicId.ToString(), CancellationToken.None);

            var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
            Assert.True(payment.IsDeleted);
        }

        // --- Camino LEGACY anidado ---
        await using (var context = CreateContext())
        {
            var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
            var reservaService = BuildReservaService(context);
            var p = await reservaService.AddPaymentAsync(reserva.Id, new Payment { Amount = 300m, Method = "Cash" });
            var paymentId = await context.Payments.Where(x => x.PublicId == p.PublicId).Select(x => x.Id).FirstAsync();

            await reservaService.UpdatePaymentAsync(
                reserva.Id, paymentId, new Payment { Amount = 400m, Method = "Transfer" });
            await reservaService.DeletePaymentAsync(reserva.Id, paymentId);

            var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.Id == paymentId);
            Assert.True(payment.IsDeleted);
        }
    }

    [Fact]
    public async Task DeletePayment_OnTerminal_WithIssuedReceipt_StillBlockedByFiscalGuard()
    {
        // ADR-033: el DELETE libre se libero del ESTADO, pero el guard FISCAL queda. Un cobro con recibo
        // emitido NO se borra (esta donde este la reserva); la salida es la anulacion fiscal/annul.
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        await paymentService.IssueReceiptAsync(p.PublicId.ToString(), CancellationToken.None);
        await MoveReservaToStatusAsync(context, reserva.Id, EstadoReserva.Closed);

        // Tanda de saneo (2026-07-22): el guard fiscal de DeletePaymentCoreAsync tira PaymentValidationException.
        await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.DeletePaymentAsync(p.PublicId.ToString(), CancellationToken.None));

        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
        Assert.False(payment.IsDeleted); // el guard fiscal corto el borrado
    }

    [Fact]
    public async Task AnnulPayment_OnTerminal_SoftDeletes_AndReversesLedger()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        // El alta creo un asiento de caja vigente.
        var liveBefore = await context.CashLedgerEntries.AsNoTracking()
            .CountAsync(e => !e.IsReversal && !e.IsReversed);
        Assert.Equal(1, liveBefore);

        await MoveReservaToStatusAsync(context, reserva.Id, EstadoReserva.Cancelled);

        // Anular SI funciona en terminal (a diferencia de DELETE/PUT).
        await paymentService.AnnulPaymentAsync(p.PublicId.ToString(), reason: "cobro mal cargado", CancellationToken.None);

        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
        Assert.True(payment.IsDeleted);
        Assert.NotNull(payment.DeletedAt);

        // El asiento original quedo revertido (rastro) y se inserto su contra-asiento.
        var liveAfter = await context.CashLedgerEntries.AsNoTracking()
            .CountAsync(e => !e.IsReversal && !e.IsReversed);
        Assert.Equal(0, liveAfter);
        var reversal = await context.CashLedgerEntries.AsNoTracking().CountAsync(e => e.IsReversal);
        Assert.Equal(1, reversal);
    }

    [Fact]
    public async Task AnnulPayment_OnCollectable_AlsoWorks()
    {
        // Anular tambien debe poder usarse en estados cobrables (es la operacion de reversa).
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        await paymentService.AnnulPaymentAsync(p.PublicId.ToString(), reason: null, CancellationToken.None);

        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
        Assert.True(payment.IsDeleted);
    }

    // =====================================================================================================
    // ADR-032 (review) — el ANNUL NO exime el guard FISCAL, solo el de estado.
    // =====================================================================================================

    [Fact]
    public async Task AnnulPayment_WithIssuedReceipt_StillBlockedByFiscalGuard_EvenOnTerminal()
    {
        // Un cobro con recibo emitido (Issued) sigue bloqueado al anular, AUNQUE la reserva sea terminal.
        // El annul exime el gate de ESTADO, pero NO el guard fiscal: el camino correcto es /receipt/void.
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        // Emitir el recibo y dejar la reserva en terminal.
        await paymentService.IssueReceiptAsync(p.PublicId.ToString(), CancellationToken.None);
        await MoveReservaToStatusAsync(context, reserva.Id, EstadoReserva.Cancelled);

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; el mensaje de negocio se
        // mantiene identico (sigue siendo el guard fiscal de DeletePaymentCoreAsync).
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.AnnulPaymentAsync(p.PublicId.ToString(), reason: "intento", CancellationToken.None));
        Assert.Contains("comprobante vigente", ex.Message);

        // El cobro sigue vivo: el guard fiscal corto la anulacion.
        var payment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
        Assert.False(payment.IsDeleted);
    }

    [Fact]
    public async Task AnnulPayment_LinkedToLiveInvoice_StillBlockedByFiscalGuard()
    {
        // Un cobro vinculado a una factura (RelatedInvoiceId) sigue bloqueado al anular: hay que generar una NC.
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        // Vincular el cobro a una factura viva (RelatedInvoiceId es el FK que mira el guard fiscal).
        var invoice = new Invoice
        {
            ReservaId = reserva.Id,
            TipoComprobante = 11,
            ImporteTotal = 300m,
            NumeroComprobante = 1,
            CreatedAt = DateTime.UtcNow,
        };
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        var paymentEntity = await context.Payments.IgnoreQueryFilters().FirstAsync(x => x.PublicId == p.PublicId);
        paymentEntity.RelatedInvoiceId = invoice.Id;
        await context.SaveChangesAsync();

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje de negocio identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.AnnulPaymentAsync(p.PublicId.ToString(), reason: "intento", CancellationToken.None));
        Assert.Contains("vinculado a una factura", ex.Message);

        var afterPayment = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == p.PublicId);
        Assert.False(afterPayment.IsDeleted);
    }

    // =====================================================================================================
    // ADR-032 (review) — anular un PUENTE (sobrepago / FC4) se rechaza incluso por la ruta annul.
    // =====================================================================================================

    [Fact]
    public async Task AnnulPayment_OnOverpaymentBridge_RejectsByEnsureNotBridge()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        // Firma exacta del puente de sobrepago: Method=SaldoAFavor + AffectsCash=false + OriginalPaymentId != null.
        var bridge = new Payment
        {
            ReservaId = reserva.Id,
            Amount = -200m,
            Method = "SaldoAFavor",
            AffectsCash = false,
            OriginalPaymentId = 12345,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            PaidAt = DateTime.UtcNow,
        };
        context.Payments.Add(bridge);
        await context.SaveChangesAsync();

        var paymentService = BuildPaymentService(context);

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje de negocio identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.AnnulPaymentAsync(bridge.PublicId.ToString(), reason: null, CancellationToken.None));
        Assert.Equal(OverpaymentCreditCleanup.DirectBridgeMutationBlockReason, ex.Message);

        var after = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == bridge.PublicId);
        Assert.False(after.IsDeleted);
    }

    [Fact]
    public async Task AnnulPayment_OnAppliedCreditBridge_RejectsByEnsureNotBridge()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        // Firma exacta del puente FC4: Method=SaldoAFavorAplicado + AffectsCash=false + AppliedFromCreditWithdrawalId != null.
        var bridge = new Payment
        {
            ReservaId = reserva.Id,
            Amount = 200m,
            Method = "SaldoAFavorAplicado",
            AffectsCash = false,
            AppliedFromCreditWithdrawalId = 54321,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            PaidAt = DateTime.UtcNow,
        };
        context.Payments.Add(bridge);
        await context.SaveChangesAsync();

        var paymentService = BuildPaymentService(context);

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje de negocio identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.AnnulPaymentAsync(bridge.PublicId.ToString(), reason: null, CancellationToken.None));
        Assert.Equal(AppliedCreditBridge.DirectBridgeMutationBlockReason, ex.Message);

        var after = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == bridge.PublicId);
        Assert.False(after.IsDeleted);
    }

    // =====================================================================================================
    // ADR-044 "Deshacer una multa ya emitida" (2026-07-14) — el puente "MultaDeshecha" no se borra ni edita.
    // =====================================================================================================

    /// <summary>Firma exacta del puente de multa deshecha: Method=MultaDeshecha + AffectsCash=false + monto negativo.</summary>
    private static Payment NewDebitNoteUndoBridge(int reservaId) => new()
    {
        ReservaId = reservaId,
        Amount = -20_000m,
        Method = TravelApi.Infrastructure.Services.ClientCreditService.DebitNoteUndoBridgeMethod,
        AffectsCash = false,
        Status = "Paid",
        EntryType = PaymentEntryTypes.Payment,
        PaidAt = DateTime.UtcNow,
    };

    [Fact]
    public void IsDebitNoteUndoBridge_RecognizesTheBridge_AndRejectsRealPayments()
    {
        // Positivo: la firma exacta.
        Assert.True(TravelApi.Infrastructure.Services.ClientCreditService.IsDebitNoteUndoBridge(
            new Payment { Method = "MultaDeshecha", AffectsCash = false }));
        // Negativos: un cobro real (AffectsCash=true) o cualquier otro Method NO es el puente.
        Assert.False(TravelApi.Infrastructure.Services.ClientCreditService.IsDebitNoteUndoBridge(
            new Payment { Method = "MultaDeshecha", AffectsCash = true }));
        Assert.False(TravelApi.Infrastructure.Services.ClientCreditService.IsDebitNoteUndoBridge(
            new Payment { Method = "Transfer", AffectsCash = false }));
    }

    [Fact]
    public async Task AnnulPayment_OnDebitNoteUndoBridge_RejectsByEnsureNotBridge()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Cancelled);
        var bridge = NewDebitNoteUndoBridge(reserva.Id);
        context.Payments.Add(bridge);
        await context.SaveChangesAsync();

        var paymentService = BuildPaymentService(context);

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje de negocio identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.AnnulPaymentAsync(bridge.PublicId.ToString(), reason: null, CancellationToken.None));
        Assert.Equal(TravelApi.Infrastructure.Services.ClientCreditService.DirectBridgeMutationBlockReason, ex.Message);

        var after = await context.Payments.IgnoreQueryFilters().AsNoTracking().FirstAsync(x => x.PublicId == bridge.PublicId);
        Assert.False(after.IsDeleted);
    }

    [Fact]
    public async Task UpdatePayment_OnDebitNoteUndoBridge_RejectsByBridgeGuard()
    {
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Cancelled);
        var bridge = NewDebitNoteUndoBridge(reserva.Id);
        context.Payments.Add(bridge);
        await context.SaveChangesAsync();

        var paymentService = BuildPaymentService(context);

        // Tanda de saneo (2026-07-22): tipo exacto PaymentValidationException; mensaje de negocio identico.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.UpdatePaymentAsync(
                bridge.PublicId.ToString(),
                new UpdatePaymentRequest { Amount = -5_000m, Method = "MultaDeshecha" },
                CancellationToken.None));
        Assert.Equal(TravelApi.Infrastructure.Services.ClientCreditService.DirectBridgeMutationBlockReason, ex.Message);
    }

    // =====================================================================================================
    // ADR-032 (review) — anti doble-anulacion. El guard EnsureNotAlreadyAnnulled corta sobre un cobro YA
    // soft-deleted (FindAsync ignora el filtro global !IsDeleted, por eso el guard existe).
    //
    // NOTA (verificada en codigo): por la RUTA de public-id, el resolver de ids
    // (EntityReferenceResolver.ResolveRequiredIdAsync) consulta Set<Payment>() SIN IgnoreQueryFilters, asi
    // que el filtro global !IsDeleted ya esconde el cobro anulado -> la segunda anulacion NO llega al guard,
    // sale antes como KeyNotFoundException. El efecto de negocio buscado igual se cumple: la segunda
    // anulacion NO duplica contra-asiento ni evento de auditoria. El guard queda como defensa en profundidad
    // para cualquier caller que cargue el cobro saltando el filtro (p.ej. overloads por id interno).
    // =====================================================================================================

    [Fact]
    public async Task AnnulPayment_AlreadyAnnulled_DoesNotDuplicateLedgerOrAudit()
    {
        await using var context = CreateContext();
        var auditMock = new Mock<IAuditService>();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentServiceWithAudit(context, auditMock.Object);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        // Primera anulacion: OK. Deja un contra-asiento y un evento de auditoria.
        await paymentService.AnnulPaymentAsync(p.PublicId.ToString(), reason: "primera", CancellationToken.None);

        var reversalsAfterFirst = await context.CashLedgerEntries.AsNoTracking().CountAsync(e => e.IsReversal);
        Assert.Equal(1, reversalsAfterFirst);
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "PaymentAnnulled", "Payment", It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);

        // Segunda anulacion sobre el MISMO cobro (ya soft-deleted): el cobro ya no es resoluble por public-id
        // (filtro global) -> KeyNotFoundException. Lo importante: NO vuelve a procesar.
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            paymentService.AnnulPaymentAsync(p.PublicId.ToString(), reason: "segunda", CancellationToken.None));

        // No se duplico el contra-asiento ni el evento de auditoria.
        var reversalsAfterSecond = await context.CashLedgerEntries.AsNoTracking().CountAsync(e => e.IsReversal);
        Assert.Equal(1, reversalsAfterSecond);
        auditMock.Verify(a => a.LogBusinessEventAsync(
            "PaymentAnnulled", "Payment", It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureNotAlreadyAnnulled_GuardCutsSoftDeletedPayment_WithStableMessage()
    {
        // Test directo del guard: cuando un caller carga un cobro ya soft-deleted (saltando el filtro, como
        // hace FindAsync), el DELETE/ANNUL deben cortar con "El cobro ya está anulado." en vez de re-procesar.
        // Se invoca el helper privado por reflexion para no depender de un caller que ya esconde el cobro.
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentService(context);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer" },
            CancellationToken.None);

        var payment = await context.Payments.IgnoreQueryFilters().FirstAsync(x => x.PublicId == p.PublicId);
        payment.IsDeleted = true; // simula un cobro ya anulado

        var guard = typeof(PaymentService).GetMethod(
            "EnsureNotAlreadyAnnulled",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;

        var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(
            () => guard.Invoke(null, new object[] { payment }));
        // Tanda de saneo (2026-07-22): EnsureNotAlreadyAnnulled ahora tira PaymentValidationException (sigue
        // heredando de InvalidOperationException, pero el tipo EXACTO cambio); mensaje de negocio identico.
        var inner = Assert.IsType<PaymentValidationException>(thrown.InnerException);
        Assert.Equal("El cobro ya está anulado.", inner.Message);
    }

    [Fact]
    public async Task AnnulPayment_AuditDetails_IncludeAmountAndCurrency()
    {
        // El rastro de PaymentAnnulled lleva el delta economico (monto + moneda), ademas del motivo.
        await using var context = CreateContext();
        var auditMock = new Mock<IAuditService>();
        string? capturedDetails = null;
        auditMock
            .Setup(a => a.LogBusinessEventAsync(
                "PaymentAnnulled", "Payment", It.IsAny<string>(), It.IsAny<string?>(),
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, string, string?, CancellationToken>(
                (_, _, _, details, _, _, _) => capturedDetails = details)
            .Returns(Task.CompletedTask);

        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var paymentService = BuildPaymentServiceWithAudit(context, auditMock.Object);
        var p = await paymentService.CreatePaymentAsync(
            new CreatePaymentRequest { ReservaId = reserva.PublicId.ToString(), Amount = 300m, Method = "Transfer", Currency = Monedas.ARS },
            CancellationToken.None);

        await paymentService.AnnulPaymentAsync(p.PublicId.ToString(), reason: "cobro mal cargado", CancellationToken.None);

        Assert.NotNull(capturedDetails);
        Assert.Contains("300", capturedDetails!);
        Assert.Contains(Monedas.ARS, capturedDetails!);
        Assert.Contains("cobro mal cargado", capturedDetails!);
    }

    // =====================================================================================================
    // ADR-032 (review) — el controller AnnulPayment mapea InvalidOperationException->409 y KeyNotFound->404.
    // =====================================================================================================

    [Fact]
    public async Task AnnulPaymentController_OnBridge_MapsInvalidOperationTo409()
    {
        // El controller mapea InvalidOperationException (aca: intentar anular un puente) -> 409 Conflict.
        await using var context = CreateContext();
        var reserva = await SeedReservaAsync(context, EstadoReserva.Confirmed);
        var bridge = new Payment
        {
            ReservaId = reserva.Id,
            Amount = -200m,
            Method = "SaldoAFavor",
            AffectsCash = false,
            OriginalPaymentId = 12345,
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            PaidAt = DateTime.UtcNow,
        };
        context.Payments.Add(bridge);
        await context.SaveChangesAsync();

        var paymentService = BuildPaymentService(context);
        var controller = new PaymentsController(paymentService);

        var result = await controller.AnnulPayment(
            bridge.PublicId.ToString(), new AnnulPaymentRequest("intento"), CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

    [Fact]
    public async Task AnnulPaymentController_OnUnknownPayment_Returns404()
    {
        // Tanto un cobro inexistente como uno YA anulado (escondido por el filtro global) salen como 404.
        await using var context = CreateContext();
        var paymentService = BuildPaymentService(context);
        var controller = new PaymentsController(paymentService);

        var result = await controller.AnnulPayment(
            Guid.NewGuid().ToString(), new AnnulPaymentRequest(null), CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    // =====================================================================================================
    // Helpers
    // =====================================================================================================

    private PaymentService BuildPaymentServiceWithAudit(AppDbContext context, IAuditService auditService)
        => new(
            context,
            new EntityReferenceResolver(context),
            _mapper,
            _settingsMock.Object,
            NullLogger<PaymentService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null,
            approvalService: null,
            approvalPolicyService: null,
            auditService: auditService);

    private ClientCreditService BuildClientCreditService(AppDbContext context)
        => new(
            context,
            Mock.Of<IBookingCancellationService>(),
            Mock.Of<IApprovalRequestService>(),
            Mock.Of<IAuditService>(),
            _settingsMock.Object,
            NullLogger<ClientCreditService>.Instance);

    /// <summary>
    /// Cambia el Status de la reserva directamente en BD (sin pasar por el motor de transiciones), para
    /// poner a la reserva en un estado terminal y probar los gates de editar/borrar/anular sobre un cobro
    /// que ya existia. Refresca el contexto rastreado para que los servicios lean el estado nuevo.
    /// </summary>
    private static async Task MoveReservaToStatusAsync(AppDbContext context, int reservaId, string status)
    {
        var reserva = await context.Reservas.FirstAsync(r => r.Id == reservaId);
        reserva.Status = status;
        await context.SaveChangesAsync();
    }
}
