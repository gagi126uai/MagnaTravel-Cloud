using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC4 (saldo a favor del cliente aplicado a otra reserva — flujo a nivel cliente, espejo del lado operador):
/// cobertura de <c>GetCustomerCreditAsync</c> / <c>ApplyCustomerCreditAsync</c> /
/// <c>ReverseCustomerCreditApplicationAsync</c>. Cubre: aplicar drena el pool (FIFO) y baja la deuda del
/// destino via el Payment puente; revertir repone; topes (pool, deuda destino, otro cliente, moneda cruzada);
/// anti doble-reversa; y auditoria staged.
///
/// <para>NOTA InMemory: el CHECK SQL del saldo no-negativo, la atomicidad real (transaccion) y el retry xmin
/// viven en integracion Postgres. Aca se valida la LOGICA del servicio (validaciones, drenaje FIFO,
/// reposicion, puentes).</para>
/// </summary>
public class Fc4ClientCreditApplicationTests
{
    private const string UserId = "tester";

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    // Service con dependencias mockeadas. La feature flag (EnableNewCancellationFlow) viene true por default en
    // OperationalFinanceSettings. accessor/resolver null -> sin filtro de ownership (comportamiento legacy/test).
    private static ClientCreditService CreateService(AppDbContext context, Mock<IAuditService>? audit = null)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        return new ClientCreditService(
            context,
            bcService: new Mock<IBookingCancellationService>().Object,
            approvalService: new Mock<IApprovalRequestService>().Object,
            auditService: (audit ?? new Mock<IAuditService>()).Object,
            settings: settings.Object,
            logger: NullLogger<ClientCreditService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null);
    }

    // Service con HttpContext + resolver mockeados para ejercitar el filtro de ownership de la reserva DESTINO.
    // userId = vendedor actual (NO Admin). seesAllCobranzas controla si tiene cobranzas.view_all (ve todo, no se
    // filtra) o un scope acotado (solo sus reservas).
    private static ClientCreditService CreateScopedService(
        AppDbContext context, string userId, bool seesAllCobranzas)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = seesAllCobranzas
            ? new HashSet<string> { Permissions.CobranzasViewAll }
            : new HashSet<string>();
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);

        return new ClientCreditService(
            context,
            bcService: new Mock<IBookingCancellationService>().Object,
            approvalService: new Mock<IApprovalRequestService>().Object,
            auditService: new Mock<IAuditService>().Object,
            settings: settings.Object,
            logger: NullLogger<ClientCreditService>.Instance,
            permissionResolver: resolver.Object,
            httpContextAccessor: accessor);
    }

    private static async Task<Customer> AddCustomerAsync(AppDbContext context, string name = "Cliente")
    {
        var customer = new Customer { FullName = name };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task<ClientCreditEntry> AddCreditEntryAsync(
        AppDbContext context, int customerId, decimal amount, string? currency = null, DateTime? createdAt = null)
    {
        var entry = new ClientCreditEntry
        {
            CustomerId = customerId,
            Currency = currency ?? Monedas.ARS,
            CreditedAmount = amount,
            RemainingBalance = amount,
            IsFullyConsumed = false,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        context.ClientCreditEntries.Add(entry);
        await context.SaveChangesAsync();
        return entry;
    }

    // Reserva destino del cliente con deuda exigible: un hotel CONFIRMADO con SalePrice en la moneda y sin pagos
    // -> Balance = SalePrice en esa moneda.
    private static async Task<Reserva> AddTargetReservaWithDebtAsync(
        AppDbContext context, int payerId, string numero, decimal salePrice, string? currency = null)
    {
        var reserva = new Reserva
        {
            NumeroReserva = numero,
            Name = "Reserva " + numero,
            Status = EstadoReserva.Confirmed,
            PayerId = payerId,
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            HotelName = "Hotel",
            City = "Ciudad",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2,
            Status = "Confirmado",
            SalePrice = salePrice,
            NetCost = salePrice * 0.7m,
            Currency = currency,
        });
        await context.SaveChangesAsync();
        return reserva;
    }

    /// <summary>Pool disponible (Σ RemainingBalance) del cliente en una moneda.</summary>
    private static async Task<decimal> PoolAsync(AppDbContext context, int customerId, string currency)
    {
        var rows = await context.ClientCreditEntries.AsNoTracking()
            .Where(e => e.CustomerId == customerId)
            .Select(e => new { e.Currency, e.RemainingBalance }).ToListAsync();
        return rows.Where(r => Monedas.Normalizar(r.Currency) == currency).Sum(r => r.RemainingBalance);
    }

    /// <summary>Deuda exigible (Balance) de la reserva en una moneda, recalculada desde el grafo economico.</summary>
    private static async Task<decimal> TargetBalanceAsync(AppDbContext context, int reservaId, string currency)
    {
        var reserva = await context.Reservas.AsNoTracking()
            .Include(r => r.Payments)
            .Include(r => r.HotelBookings)
            .Include(r => r.FlightSegments)
            .Include(r => r.TransferBookings)
            .Include(r => r.PackageBookings)
            .Include(r => r.AssistanceBookings)
            .Include(r => r.Servicios)
            .FirstAsync(r => r.Id == reservaId);
        var summary = ReservaMoneyCalculator.Calculate(reserva);
        return summary.PorMoneda.TryGetValue(currency, out var line) ? line.Balance : 0m;
    }

    // ============================================================
    // 1) Aplicar: drena el pool, baja la deuda del destino
    // ============================================================

    [Fact]
    public async Task Apply_drains_pool_and_lowers_target_reserva_debt()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.ARS));
        Assert.Equal(1000m, await TargetBalanceAsync(context, target.Id, Monedas.ARS));

        var service = CreateService(context);
        var result = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        Assert.False(result.IsReversal);
        Assert.Equal(1000m, result.Amount);
        Assert.Equal(2000m, result.AvailableBalanceAfter);
        Assert.Equal(target.PublicId, result.TargetReservaPublicId);

        // Pool drenado en 1000; deuda del destino a 0.
        Assert.Equal(2000m, await PoolAsync(context, customer.Id, Monedas.ARS));
        Assert.Equal(0m, await TargetBalanceAsync(context, target.Id, Monedas.ARS));

        // Se creo UN Payment puente positivo, sin caja.
        var bridge = await context.Payments.AsNoTracking()
            .SingleAsync(p => p.ReservaId == target.Id && p.Method == AppliedCreditBridge.BridgeMethod);
        Assert.Equal(1000m, bridge.Amount);
        Assert.False(bridge.AffectsCash);
        Assert.NotNull(bridge.AppliedFromCreditWithdrawalId);
    }

    [Fact]
    public async Task Apply_drains_multiple_pockets_fifo()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        var older = await AddCreditEntryAsync(context, customer.Id, 600m, createdAt: DateTime.UtcNow.AddDays(-2));
        var newer = await AddCreditEntryAsync(context, customer.Id, 800m, createdAt: DateTime.UtcNow.AddDays(-1));
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);
        var result = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        Assert.Equal(1000m, result.Amount);
        Assert.Equal(400m, result.AvailableBalanceAfter);

        // FIFO: el bolsillo viejo se consumio entero (600), el nuevo perdio 400.
        var olderAfter = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.Id == older.Id);
        var newerAfter = await context.ClientCreditEntries.AsNoTracking().FirstAsync(e => e.Id == newer.Id);
        Assert.Equal(0m, olderAfter.RemainingBalance);
        Assert.True(olderAfter.IsFullyConsumed);
        Assert.Equal(400m, newerAfter.RemainingBalance);
        Assert.False(newerAfter.IsFullyConsumed);

        // Dos retiros AppliedToNewBooking + dos puentes; deuda del destino a 0.
        var withdrawals = await context.ClientCreditWithdrawals.AsNoTracking()
            .Where(w => w.Kind == WithdrawalKind.AppliedToNewBooking).ToListAsync();
        Assert.Equal(2, withdrawals.Count);
        Assert.Equal(0m, await TargetBalanceAsync(context, target.Id, Monedas.ARS));

        // El ApplicationPublicId devuelto es el del PRIMER retiro (bolsillo mas viejo).
        Assert.Equal(older.PublicId, result.EntryPublicId);
    }

    // ============================================================
    // 2) Topes
    // ============================================================

    [Fact]
    public async Task Apply_more_than_available_pool_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 500m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditAsync(
                customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 800m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-085", ex.InvariantCode);

        // Nada se drena al rechazar; no se mienta la cifra disponible.
        Assert.Equal(500m, await PoolAsync(context, customer.Id, Monedas.ARS));
        Assert.DoesNotContain("500", ex.Message);
    }

    [Fact]
    public async Task Apply_more_than_destination_debt_is_rejected_then_exact_debt_ok()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);

        // 2000 < pool (3000) pero > deuda destino (1000): se rechaza (INV-097).
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditAsync(
                customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 2000m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-097", ex.InvariantCode);
        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.ARS));

        // Aplicar exactamente la deuda (1000) si esta permitido.
        var result = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(1000m, result.Amount);
        Assert.Equal(2000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Apply_to_reserva_of_another_customer_is_rejected()
    {
        await using var context = CreateContext();
        var owner = await AddCustomerAsync(context, "Dueño del saldo");
        var other = await AddCustomerAsync(context, "Otro cliente");
        await AddCreditEntryAsync(context, owner.Id, amount: 3000m);

        // La reserva destino es de OTRO cliente.
        var target = await AddTargetReservaWithDebtAsync(context, other.Id, "OTHER", salePrice: 1000m);

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditAsync(
                owner.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-093", ex.InvariantCode);
        Assert.Equal(3000m, await PoolAsync(context, owner.Id, Monedas.ARS));
    }

    // ============================================================
    // 3) Multimoneda: ARS y USD no se cruzan
    // ============================================================

    [Fact]
    public async Task Currencies_do_not_cross()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        // Saldo a favor SOLO en USD.
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m, currency: Monedas.USD);
        // Reserva destino con deuda SOLO en ARS.
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "ARS-DEST", salePrice: 1000m);

        var service = CreateService(context);

        // Aplicar en ARS: no hay pool ARS -> rechazo (INV-085).
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditAsync(
                customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 500m, target.PublicId),
                UserId, "Tester", CancellationToken.None));

        // Aplicar en USD: el destino no tiene deuda USD -> rechazo (INV-095), no cruza.
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ApplyCustomerCreditAsync(
                customer.Id, new ApplyClientCreditRequest(Monedas.USD, 500m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-095", ex.InvariantCode);

        // Nada se movio en ninguna moneda.
        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.USD));
        Assert.Equal(0m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Apply_usd_credit_to_usd_debt_ok()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m, currency: Monedas.USD);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "USD-DEST", salePrice: 1000m, currency: Monedas.USD);

        var service = CreateService(context);
        var result = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.USD, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        Assert.Equal(Monedas.USD, result.Currency);
        Assert.Equal(2000m, result.AvailableBalanceAfter);
        Assert.Equal(0m, await TargetBalanceAsync(context, target.Id, Monedas.USD));
        Assert.Equal(2000m, await PoolAsync(context, customer.Id, Monedas.USD));
    }

    // ============================================================
    // 4) Reversa
    // ============================================================

    [Fact]
    public async Task Reverse_restores_pool_and_target_debt()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);
        var apply = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(2000m, await PoolAsync(context, customer.Id, Monedas.ARS));

        var reverse = await service.ReverseCustomerCreditApplicationAsync(
            customer.Id, apply.ApplicationPublicId,
            new ReverseClientCreditApplicationRequest("Aplicacion equivocada, se revierte"),
            UserId, "Tester", CancellationToken.None);

        Assert.True(reverse.IsReversal);
        Assert.Equal(3000m, reverse.AvailableBalanceAfter);
        Assert.Equal(target.PublicId, reverse.TargetReservaPublicId);

        // Pool repuesto, deuda del destino de vuelta a 1000, puente soft-deleted.
        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.ARS));
        Assert.Equal(1000m, await TargetBalanceAsync(context, target.Id, Monedas.ARS));

        var liveBridges = await context.Payments.AsNoTracking()
            .Where(p => p.ReservaId == target.Id && p.Method == AppliedCreditBridge.BridgeMethod && !p.IsDeleted)
            .ToListAsync();
        Assert.Empty(liveBridges);
    }

    [Fact]
    public async Task Double_reverse_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);
        var apply = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        var reason = new ReverseClientCreditApplicationRequest("Aplicacion equivocada, se revierte");
        await service.ReverseCustomerCreditApplicationAsync(customer.Id, apply.ApplicationPublicId, reason, UserId, "Tester", CancellationToken.None);

        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ReverseCustomerCreditApplicationAsync(customer.Id, apply.ApplicationPublicId, reason, UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-098", ex.InvariantCode);

        // El pool no se reincrementa dos veces.
        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Reverse_of_non_application_withdrawal_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        var entry = await AddCreditEntryAsync(context, customer.Id, amount: 1000m);

        // Un retiro que NO es AppliedToNewBooking (decision "lo dejo como credito").
        var kept = new ClientCreditWithdrawal
        {
            ClientCreditEntryId = entry.Id,
            Kind = WithdrawalKind.KeptAsCredit,
            Amount = 0m,
            ExecutedByUserId = UserId,
            ExecutedByUserName = "Tester",
        };
        context.ClientCreditWithdrawals.Add(kept);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var ex = await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            service.ReverseCustomerCreditApplicationAsync(
                customer.Id, kept.PublicId,
                new ReverseClientCreditApplicationRequest("No es una aplicacion, deberia rechazar"),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal("INV-CLICREDIT-005", ex.InvariantCode);
    }

    [Fact]
    public async Task Reverse_without_reason_proceeds()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);
        var apply = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Motivo OPCIONAL: la reversa procede aunque el motivo venga null. El pool se repone igual.
        var reverse = await service.ReverseCustomerCreditApplicationAsync(
            customer.Id, apply.ApplicationPublicId,
            new ReverseClientCreditApplicationRequest(Reason: null),
            UserId, "Tester", CancellationToken.None);

        Assert.True(reverse.IsReversal);
        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Reverse_of_application_of_another_customer_is_not_found()
    {
        await using var context = CreateContext();
        var owner = await AddCustomerAsync(context, "Dueño");
        var stranger = await AddCustomerAsync(context, "Ajeno");
        await AddCreditEntryAsync(context, owner.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, owner.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);
        var apply = await service.ApplyCustomerCreditAsync(
            owner.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Otro cliente intenta revertir la aplicacion del dueño -> 404 (no la encuentra en su scope).
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            service.ReverseCustomerCreditApplicationAsync(
                stranger.Id, apply.ApplicationPublicId,
                new ReverseClientCreditApplicationRequest("Intento revertir algo ajeno"),
                UserId, "Tester", CancellationToken.None));
    }

    // ============================================================
    // 4b) Ownership de la reserva DESTINO (B1 review seguridad)
    // ============================================================

    [Fact]
    public async Task Apply_to_reserva_of_another_seller_is_denied_for_scoped_user()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        // Reserva del MISMO cliente pero a cargo de otro vendedor (ResponsibleUserId queda null != "seller-1").
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        // Vendedor con scope acotado (sin cobranzas.view_all) -> no puede aplicar a una reserva ajena.
        var service = CreateScopedService(context, userId: "seller-1", seesAllCobranzas: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ApplyCustomerCreditAsync(
                customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
                "seller-1", "Vendedor", CancellationToken.None));

        // No se drena nada al rechazar.
        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Apply_to_reserva_is_allowed_for_user_who_sees_all_cobranzas()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        // Con cobranzas.view_all el ownership NO se filtra: la aplicacion procede.
        var service = CreateScopedService(context, userId: "supervisor", seesAllCobranzas: true);
        var apply = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            "supervisor", "Supervisor", CancellationToken.None);

        Assert.Equal(1000m, apply.Amount);
        Assert.Equal(2000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Reverse_of_application_on_another_sellers_reserva_is_denied_for_scoped_user()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        // El apply se hace sin filtro (setup); lo que se prueba es que la REVERSA respeta el ownership del destino.
        var setupService = CreateService(context);
        var apply = await setupService.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Vendedor con scope acotado intenta revertir sobre una reserva que no esta a su cargo -> denegado.
        var scopedService = CreateScopedService(context, userId: "seller-1", seesAllCobranzas: false);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            scopedService.ReverseCustomerCreditApplicationAsync(
                customer.Id, apply.ApplicationPublicId,
                new ReverseClientCreditApplicationRequest(Reason: null),
                "seller-1", "Vendedor", CancellationToken.None));

        // El puente sigue vivo y el pool no se repuso (la reversa no procedio).
        Assert.Equal(2000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Reverse_is_allowed_for_user_who_sees_all_cobranzas()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var setupService = CreateService(context);
        var apply = await setupService.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Con cobranzas.view_all la reversa procede aunque la reserva no este nominalmente a su cargo.
        var service = CreateScopedService(context, userId: "supervisor", seesAllCobranzas: true);
        var reverse = await service.ReverseCustomerCreditApplicationAsync(
            customer.Id, apply.ApplicationPublicId,
            new ReverseClientCreditApplicationRequest(Reason: null),
            "supervisor", "Supervisor", CancellationToken.None);

        Assert.True(reverse.IsReversal);
        Assert.Equal(3000m, await PoolAsync(context, customer.Id, Monedas.ARS));
    }

    // ============================================================
    // 5) Auditoria staged
    // ============================================================

    [Fact]
    public async Task Apply_and_reverse_emit_staged_audit_events()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 3000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var audit = new Mock<IAuditService>();
        var service = CreateService(context, audit);

        var apply = await service.ApplyCustomerCreditAsync(
            customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.ClientCreditApplied,
            AuditActions.ClientCreditWithdrawalEntityName,
            It.IsAny<string>(), It.IsAny<string>(), UserId, It.IsAny<string>()),
            Times.Once);

        await service.ReverseCustomerCreditApplicationAsync(
            customer.Id, apply.ApplicationPublicId,
            new ReverseClientCreditApplicationRequest("Aplicacion equivocada, se revierte"),
            UserId, "Tester", CancellationToken.None);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.ClientCreditApplicationReversed,
            AuditActions.ClientCreditWithdrawalEntityName,
            It.IsAny<string>(), It.IsAny<string>(), UserId, It.IsAny<string>()),
            Times.Once);
    }

    // ============================================================
    // 6) Overview por moneda
    // ============================================================

    [Fact]
    public async Task GetCustomerCredit_groups_active_pockets_by_currency()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context, "Cliente con saldo");
        await AddCreditEntryAsync(context, customer.Id, amount: 1000m, currency: Monedas.ARS);
        await AddCreditEntryAsync(context, customer.Id, amount: 500m, currency: Monedas.ARS);
        await AddCreditEntryAsync(context, customer.Id, amount: 300m, currency: Monedas.USD);
        // Bolsillo agotado: no debe aparecer.
        var consumed = await AddCreditEntryAsync(context, customer.Id, amount: 0m, currency: Monedas.ARS);
        consumed.IsFullyConsumed = true;
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var overview = await service.GetCustomerCreditAsync(customer.Id, CancellationToken.None);

        Assert.Equal(customer.PublicId, overview.CustomerPublicId);
        Assert.Equal("Cliente con saldo", overview.CustomerName);
        Assert.Equal(2, overview.Currencies.Count);

        var ars = overview.Currencies.Single(c => c.Currency == Monedas.ARS);
        Assert.Equal(1500m, ars.AvailableBalance);
        Assert.Equal(2, ars.Entries.Count);

        var usd = overview.Currencies.Single(c => c.Currency == Monedas.USD);
        Assert.Equal(300m, usd.AvailableBalance);
    }

    [Fact]
    public async Task Apply_with_non_positive_amount_or_unsupported_currency_is_rejected()
    {
        await using var context = CreateContext();
        var customer = await AddCustomerAsync(context);
        await AddCreditEntryAsync(context, customer.Id, amount: 1000m);
        var target = await AddTargetReservaWithDebtAsync(context, customer.Id, "DEST", salePrice: 1000m);

        var service = CreateService(context);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyCustomerCreditAsync(
                customer.Id, new ApplyClientCreditRequest(Monedas.ARS, 0m, target.PublicId),
                UserId, "Tester", CancellationToken.None));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ApplyCustomerCreditAsync(
                customer.Id, new ApplyClientCreditRequest("EUR", 100m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
    }
}
