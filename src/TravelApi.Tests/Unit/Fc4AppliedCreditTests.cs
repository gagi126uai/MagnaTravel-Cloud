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
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            paymentService.IssueReceiptAsync(bridge.Id, CancellationToken.None));
        Assert.Contains("saldo a favor aplicado", ex.Message);
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
    }
}
