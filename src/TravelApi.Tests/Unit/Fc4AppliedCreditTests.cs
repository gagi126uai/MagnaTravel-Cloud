using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC4 (saldo a favor aplicado a otra reserva, 2026-06-14): cubre la nueva logica de
/// <c>ClientCreditService.HandleAppliedToNewBookingAsync</c> a nivel UNIT (EF InMemory).
///
/// <para><b>Que cubre</b>: same-currency baja la deuda destino + crea el Payment puente; cruce de monedas
/// rechaza; sobre-aplicacion rechaza; estado destino invalido rechaza; INV-093 (otro cliente) sigue; el
/// puente NO aparece en las listas de pagos visibles; emitir recibo sobre el puente rechaza.</para>
///
/// <para><b>Nota InMemory</b>: el provider InMemory no soporta transacciones ni CHECK constraints. El
/// service ramifica por <c>IsRelational()</c> y aca corre SIN transaccion envolvente (mismo cuerpo). La
/// atomicidad real, la FK topologica y la concurrencia se prueban en integracion Postgres (anotado para QA).</para>
/// </summary>
public class Fc4AppliedCreditTests
{
    private readonly IMapper _mapper;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsMock;

    public Fc4AppliedCreditTests()
    {
        _mapper = new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();
        _settingsMock = new Mock<IOperationalFinanceSettingsService>();
        // El modulo de cancelacion/refund debe estar habilitado para que WithdrawAsync opere.
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

    private ClientCreditService CreateService(AppDbContext context)
        => new(
            context,
            Mock.Of<IBookingCancellationService>(),
            Mock.Of<IApprovalRequestService>(),
            new FakeAuditService(),
            _settingsMock.Object,
            NullLogger<ClientCreditService>.Instance);

    /// <summary>
    /// Arma un escenario base: un cliente con un bolsillo (ClientCreditEntry de SOBREPAGO, sin BC detras) en
    /// la moneda pedida + una reserva destino con UN servicio confirmado (la fuente de la deuda exigible).
    /// </summary>
    private static async Task<(Guid entryPublicId, int customerId, Guid targetReservaPublicId, int targetReservaId)>
        SeedAsync(AppDbContext context, string creditCurrency, decimal creditAmount,
                  string targetCurrency, decimal targetSalePrice, string targetStatus = EstadoReserva.Confirmed)
    {
        var customer = new Customer { FullName = "Cliente FC4", TaxCondition = "Consumidor Final", IsActive = true };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var entry = new ClientCreditEntry
        {
            CustomerId = customer.Id,
            Currency = creditCurrency,
            CreditedAmount = creditAmount,
            RemainingBalance = creditAmount,
            CreatedAt = DateTime.UtcNow,
            // SourcePaymentId marca el origen sobrepago (sin BookingCancellationId -> no dispara cierre de BC).
            SourcePaymentId = 999,
        };
        context.ClientCreditEntries.Add(entry);

        var target = new Reserva
        {
            NumeroReserva = "R-DEST",
            Name = "Reserva destino",
            Status = targetStatus,
            PayerId = customer.Id,
        };
        context.Reservas.Add(target);
        await context.SaveChangesAsync();

        // Un servicio confirmado en la moneda destino crea la deuda exigible (ConfirmedSale) en esa moneda.
        context.Servicios.Add(new ServicioReserva
        {
            ReservaId = target.Id,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Hotel destino",
            ConfirmationNumber = "OK-1",
            Status = "Confirmado",
            Currency = targetCurrency,
            DepartureDate = DateTime.UtcNow.AddDays(20),
            SalePrice = targetSalePrice,
            NetCost = 0m,
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        return (entry.PublicId, customer.Id, target.PublicId, target.Id);
    }

    private static WithdrawClientCreditRequest ApplyRequest(decimal amount, Guid targetReservaPublicId)
        => new(
            Kind: WithdrawalKind.AppliedToNewBooking,
            Amount: amount,
            PaymentMethodOverride: null,
            AppliedToReservaPublicId: targetReservaPublicId,
            ApprovalRequestPublicId: null,
            Reference: null);

    // =====================================================================================================
    // (a) same-currency: baja la deuda destino, decrementa el bolsillo, crea el puente bien formado.
    // =====================================================================================================

    [Fact]
    public async Task AppliedSameCurrency_LowersTargetDebt_DecrementsPocket_CreatesBridge()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        await CreateService(context).WithdrawAsync(
            seed.entryPublicId, ApplyRequest(200m, seed.targetReservaPublicId),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        // Bolsillo decremento 500 -> 300.
        var entry = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.PublicId == seed.entryPublicId);
        Assert.Equal(300m, entry.RemainingBalance);

        // Puente creado: positivo, AffectsCash=false, Method propio, atado al withdrawal.
        var withdrawal = await context.ClientCreditWithdrawals.AsNoTracking().FirstAsync();
        var bridge = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(p => p.AppliedFromCreditWithdrawalId == withdrawal.Id);
        Assert.Equal(200m, bridge.Amount);
        Assert.Equal("ARS", bridge.Currency);
        Assert.False(bridge.AffectsCash);
        Assert.Equal(AppliedCreditBridge.BridgeMethod, bridge.Method);
        Assert.Equal(seed.targetReservaId, bridge.ReservaId);

        // Deuda destino persistida: ConfirmedSale 1000 - 200 pagado = 800 (linea ARS).
        var line = await context.ReservaMoneyByCurrency.AsNoTracking()
            .FirstAsync(r => r.ReservaId == seed.targetReservaId && r.Currency == "ARS");
        Assert.Equal(800m, line.Balance);
    }

    // =====================================================================================================
    // (b) cruce de monedas: bolsillo USD, destino sin deuda USD -> rechaza INV-095.
    // =====================================================================================================

    [Fact]
    public async Task AppliedCrossCurrency_TargetHasNoDebtInPocketCurrency_RejectsInv095()
    {
        await using var context = CreateContext();
        // Bolsillo USD, pero la reserva destino solo debe ARS.
        var seed = await SeedAsync(context, creditCurrency: "USD", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            CreateService(context).WithdrawAsync(
                seed.entryPublicId, ApplyRequest(100m, seed.targetReservaPublicId),
                userId: "user1", userName: null, ct: CancellationToken.None));

        Assert.Equal("INV-095", ex.InvariantCode);

        // No se decremento el bolsillo ni se creo puente.
        var entry = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.PublicId == seed.entryPublicId);
        Assert.Equal(500m, entry.RemainingBalance);
        Assert.Equal(0, await context.Payments.IgnoreQueryFilters().CountAsync(p => p.AppliedFromCreditWithdrawalId != null));
    }

    // =====================================================================================================
    // (c) sobre-aplicacion: monto > deuda destino -> rechaza INV-097.
    // =====================================================================================================

    [Fact]
    public async Task AppliedOverDebt_RejectsInv097()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 5000m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        // Deuda destino = 1000; intentamos aplicar 2000 (mas que la deuda).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            CreateService(context).WithdrawAsync(
                seed.entryPublicId, ApplyRequest(2000m, seed.targetReservaPublicId),
                userId: "user1", userName: null, ct: CancellationToken.None));

        Assert.Equal("INV-097", ex.InvariantCode);
        var entry = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.PublicId == seed.entryPublicId);
        Assert.Equal(5000m, entry.RemainingBalance);
    }

    // =====================================================================================================
    // (d) estado destino invalido (Budget) -> rechaza INV-096.
    // =====================================================================================================

    [Fact]
    public async Task AppliedToNonCollectibleReserva_RejectsInv096()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m, targetStatus: EstadoReserva.Budget);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            CreateService(context).WithdrawAsync(
                seed.entryPublicId, ApplyRequest(100m, seed.targetReservaPublicId),
                userId: "user1", userName: null, ct: CancellationToken.None));

        Assert.Equal("INV-096", ex.InvariantCode);
    }

    // =====================================================================================================
    // (e) INV-093 sigue: reserva destino de OTRO cliente -> rechaza.
    // =====================================================================================================

    [Fact]
    public async Task AppliedToOtherCustomerReserva_RejectsInv093()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        // Reserva de OTRO cliente, en estado cobrable y con deuda (para aislar que lo que rechaza es el dueño).
        var otherCustomer = new Customer { FullName = "Otro", TaxCondition = "Consumidor Final", IsActive = true };
        context.Customers.Add(otherCustomer);
        await context.SaveChangesAsync();
        var otherReserva = new Reserva { NumeroReserva = "R-OTRO", Name = "Otro", Status = EstadoReserva.Confirmed, PayerId = otherCustomer.Id };
        context.Reservas.Add(otherReserva);
        await context.SaveChangesAsync();
        context.Servicios.Add(new ServicioReserva
        {
            ReservaId = otherReserva.Id, ServiceType = "Hotel", ProductType = "Hotel", Description = "H",
            ConfirmationNumber = "OK", Status = "Confirmado", Currency = "ARS",
            DepartureDate = DateTime.UtcNow.AddDays(10), SalePrice = 1000m, NetCost = 0m, CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            CreateService(context).WithdrawAsync(
                seed.entryPublicId, ApplyRequest(100m, otherReserva.PublicId),
                userId: "user1", userName: null, ct: CancellationToken.None));

        Assert.Equal("INV-093", ex.InvariantCode);
    }

    // =====================================================================================================
    // (f) el puente NO aparece en las listas de pagos visibles (reserva + cuenta del cliente).
    // =====================================================================================================

    [Fact]
    public async Task Bridge_IsHidden_FromReservaPayments_AndCustomerAccount()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        await CreateService(context).WithdrawAsync(
            seed.entryPublicId, ApplyRequest(200m, seed.targetReservaPublicId),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        // Lista de pagos de la reserva destino (PaymentService.GetPaymentsForReservaAsync): el puente NO aparece.
        var paymentService = BuildPaymentService(context);
        var payments = (await paymentService.GetPaymentsForReservaAsync(seed.targetReservaId, CancellationToken.None)).ToList();
        Assert.Empty(payments);

        // Cuenta del cliente: tampoco aparece en la pestaña Pagos.
        var customerService = new CustomerService(context, new FinancePositionService(context));
        var account = await customerService.GetCustomerAccountPaymentsAsync(
            seed.customerId, new PagedQuery(), CancellationToken.None);
        Assert.Empty(account.Items);
    }

    // =====================================================================================================
    // (g) emitir recibo sobre el puente rechaza.
    // =====================================================================================================

    [Fact]
    public async Task IssueReceipt_OnBridge_Rejects()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        await CreateService(context).WithdrawAsync(
            seed.entryPublicId, ApplyRequest(200m, seed.targetReservaPublicId),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        var bridge = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(p => p.AppliedFromCreditWithdrawalId != null);

        var paymentService = BuildPaymentService(context);
        // Tanda de saneo (2026-07-22): PaymentService.IssueReceiptAsync ahora rechaza el puente FC4 con
        // PaymentValidationException (mensaje de negocio), no con InvalidOperationException "a secas". xUnit
        // exige tipo EXACTO en Assert.ThrowsAsync<T>, asi que el test se actualiza al tipo nuevo.
        var ex = await Assert.ThrowsAsync<PaymentValidationException>(() =>
            paymentService.IssueReceiptAsync(bridge.Id, CancellationToken.None));
        Assert.Contains("saldo a favor aplicado", ex.Message);
    }

    // =====================================================================================================
    // FC4 REVERSA (2026-06-18): deshacer una aplicacion de saldo a favor a otra reserva.
    // =====================================================================================================

    /// <summary>
    /// (r1) Camino feliz: aplicar saldo y luego revertir -> el bolsillo recupera el monto, el puente queda
    /// soft-deleted y la deuda de la reserva destino vuelve a su nivel previo.
    /// </summary>
    [Fact]
    public async Task ReverseApplied_ReturnsPocket_SoftDeletesBridge_RestoresTargetDebt()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        // Aplicar 200: bolsillo 500 -> 300, deuda destino 1000 -> 800.
        var applied = await CreateService(context).WithdrawAsync(
            seed.entryPublicId, ApplyRequest(200m, seed.targetReservaPublicId),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        // Revertir esa aplicacion.
        var reversed = await CreateService(context).ReverseAppliedCreditAsync(
            applied.PublicId, userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        Assert.Equal(applied.PublicId, reversed.PublicId);

        // Bolsillo recupero los 200 -> vuelve a 500.
        var entry = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.PublicId == seed.entryPublicId);
        Assert.Equal(500m, entry.RemainingBalance);
        Assert.False(entry.IsFullyConsumed);

        // Puente quedo soft-deleted (sigue existiendo, pero IsDeleted=true).
        var withdrawal = await context.ClientCreditWithdrawals.AsNoTracking().FirstAsync();
        var bridge = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .FirstAsync(p => p.AppliedFromCreditWithdrawalId == withdrawal.Id);
        Assert.True(bridge.IsDeleted);
        Assert.NotNull(bridge.DeletedAt);

        // Deuda destino volvio a 1000 (el puente ya no la baja).
        var line = await context.ReservaMoneyByCurrency.AsNoTracking()
            .FirstAsync(r => r.ReservaId == seed.targetReservaId && r.Currency == "ARS");
        Assert.Equal(1000m, line.Balance);
    }

    /// <summary>
    /// (r2) Anti doble-reversa: revertir dos veces el mismo withdrawal -> la segunda rechaza INV-098 y no
    /// vuelve a tocar el bolsillo (no infla el saldo por encima de lo acreditado).
    /// </summary>
    [Fact]
    public async Task ReverseApplied_Twice_RejectsInv098_AndDoesNotInflatePocket()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        var applied = await CreateService(context).WithdrawAsync(
            seed.entryPublicId, ApplyRequest(200m, seed.targetReservaPublicId),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        // Primera reversa OK.
        await CreateService(context).ReverseAppliedCreditAsync(
            applied.PublicId, userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        // Segunda reversa: rechaza.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            CreateService(context).ReverseAppliedCreditAsync(
                applied.PublicId, userId: "user1", userName: "Cajero", ct: CancellationToken.None));
        Assert.Equal("INV-098", ex.InvariantCode);

        // El bolsillo quedo en 500 (acreditado original), no en 700.
        var entry = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.PublicId == seed.entryPublicId);
        Assert.Equal(500m, entry.RemainingBalance);
    }

    /// <summary>
    /// (r3) Solo se revierte AppliedToNewBooking: intentar revertir un KeptAsCredit (u otro kind) -> 400 via
    /// ValidationException. Verifica que la reversa no se cuela en flujos que tienen su propia anulacion.
    /// </summary>
    [Fact]
    public async Task ReverseApplied_OnWrongKind_RejectsValidation()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        // KeptAsCredit (Amount=0): no consume, no toca otra reserva.
        var kept = await CreateService(context).WithdrawAsync(
            seed.entryPublicId,
            new WithdrawClientCreditRequest(
                Kind: WithdrawalKind.KeptAsCredit, Amount: 0m, PaymentMethodOverride: null,
                AppliedToReservaPublicId: null, ApprovalRequestPublicId: null, Reference: null),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            CreateService(context).ReverseAppliedCreditAsync(
                kept.PublicId, userId: "user1", userName: "Cajero", ct: CancellationToken.None));
    }

    /// <summary>
    /// (r4) Idempotencia / not found: revertir un withdrawal inexistente -> KeyNotFoundException (404).
    /// </summary>
    [Fact]
    public async Task ReverseApplied_UnknownWithdrawal_NotFound()
    {
        await using var context = CreateContext();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            CreateService(context).ReverseAppliedCreditAsync(
                Guid.NewGuid(), userId: "user1", userName: "Cajero", ct: CancellationToken.None));
    }

    /// <summary>
    /// (r5) Reversa parcial-luego-reaplica: aplicar, revertir, y volver a aplicar el mismo monto -> el bolsillo
    /// y la deuda destino quedan consistentes. Verifica que la reversa deja el saldo en un estado reusable.
    /// </summary>
    [Fact]
    public async Task ReverseApplied_ThenReapply_LeavesConsistentState()
    {
        await using var context = CreateContext();
        var seed = await SeedAsync(context, creditCurrency: "ARS", creditAmount: 500m,
            targetCurrency: "ARS", targetSalePrice: 1000m);

        var applied = await CreateService(context).WithdrawAsync(
            seed.entryPublicId, ApplyRequest(200m, seed.targetReservaPublicId),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        await CreateService(context).ReverseAppliedCreditAsync(
            applied.PublicId, userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        // Re-aplicar 200 de nuevo: el bolsillo (de vuelta en 500) lo permite.
        await CreateService(context).WithdrawAsync(
            seed.entryPublicId, ApplyRequest(200m, seed.targetReservaPublicId),
            userId: "user1", userName: "Cajero", ct: CancellationToken.None);

        // Bolsillo 500 -> 300 otra vez.
        var entry = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.PublicId == seed.entryPublicId);
        Assert.Equal(300m, entry.RemainingBalance);

        // Deuda destino baja a 800 con el puente NUEVO (vivo); el viejo quedo soft-deleted.
        var line = await context.ReservaMoneyByCurrency.AsNoTracking()
            .FirstAsync(r => r.ReservaId == seed.targetReservaId && r.Currency == "ARS");
        Assert.Equal(800m, line.Balance);

        var liveBridges = await context.Payments.IgnoreQueryFilters().AsNoTracking()
            .CountAsync(p => p.AppliedFromCreditWithdrawalId != null && !p.IsDeleted);
        Assert.Equal(1, liveBridges);
    }

    // ----- builders -----

    private PaymentService BuildPaymentService(AppDbContext context)
        => new(context, new EntityReferenceResolver(context), _mapper, _settingsMock.Object, NullLogger<PaymentService>.Instance);

    /// <summary>Audit fake que no persiste nada (los tests de FC4 no verifican el audit, solo el efecto economico).</summary>
    private sealed class FakeAuditService : IAuditService
    {
        public Task<IEnumerable<AuditLog>> GetAuditLogsAsync(string? entityName, string? entityId, string? alternateEntityId, DateTime? dateFrom, DateTime? dateTo, string? userId, CancellationToken ct)
            => Task.FromResult<IEnumerable<AuditLog>>(Array.Empty<AuditLog>());

        public Task<PagedResult<AuditLog>> GetGlobalAuditLogsAsync(string? entityName, string? action, string? userId, DateTime? dateFrom, DateTime? dateTo, string? searchTerm, string? category, int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new PagedResult<AuditLog>(Array.Empty<AuditLog>(), 0, page, pageSize));

        public Task LogBusinessEventAsync(string action, string entityName, string entityId, string? details, string userId, string? userName, CancellationToken ct)
            => Task.CompletedTask;

        public void StageBusinessEvent(string action, string entityName, string entityId, string? details, string userId, string? userName)
        {
            // No-op: los tests de FC4 no verifican el audit, solo el efecto economico.
        }
    }
}
