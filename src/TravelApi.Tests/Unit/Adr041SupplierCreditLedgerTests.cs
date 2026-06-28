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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-041 TANDA 3 (lado proveedor, 2026-06-27): cobertura del saldo a favor CONSUMIBLE con un operador.
/// Cubre: sobrepago crea entry; aplicar drena el pool y baja la deuda destino SIN mover el Balance agregado;
/// reversa repone; multimoneda no se cruza; y el INVARIANTE autoritativo
/// (<c>Σ RemainingBalance == max(0,-Balance) - Σ aplicaciones netas</c>, por proveedor+moneda).
///
/// <para>NOTA InMemory: el CHECK SQL del saldo no-negativo y la atomicidad real (transaccion/xmin) viven en
/// integracion Postgres. Aca se valida la LOGICA del servicio (validaciones, drenaje, reposicion, invariante).</para>
/// </summary>
public class Adr041SupplierCreditLedgerTests
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

    private static HttpContextAccessor SeeCostAccessor(out Mock<IUserPermissionResolver> resolver)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);
        return accessor;
    }

    private static SupplierService CreateSupplierService(AppDbContext context)
    {
        var accessor = SeeCostAccessor(out var resolver);
        return new SupplierService(context, auditService: null, httpContextAccessor: accessor,
            logger: null, permissionResolver: resolver.Object);
    }

    private static SupplierCreditService CreateCreditService(AppDbContext context)
    {
        var accessor = SeeCostAccessor(out var resolver);
        return new SupplierCreditService(
            context,
            auditService: new Mock<IAuditService>().Object,
            logger: NullLogger<SupplierCreditService>.Instance,
            httpContextAccessor: accessor,
            permissionResolver: resolver.Object);
    }

    // Mismo usuario pero SIN cobranzas.see_cost: el overview enmascara los montos (incl. los de ActiveApplications).
    private static SupplierCreditService CreateMaskedCreditService(AppDbContext context)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, UserId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> perms = new HashSet<string>(); // sin permisos de costo
        resolver.Setup(r => r.GetPermissionsAsync(UserId, It.IsAny<CancellationToken>())).ReturnsAsync(perms);

        return new SupplierCreditService(
            context,
            auditService: new Mock<IAuditService>().Object,
            logger: NullLogger<SupplierCreditService>.Instance,
            httpContextAccessor: accessor,
            permissionResolver: resolver.Object);
    }

    private static async Task<Supplier> AddSupplierAsync(AppDbContext context)
    {
        var supplier = new Supplier { Name = "Operador", InvoicingMode = SupplierInvoicingMode.TotalToCustomer, IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        return supplier;
    }

    private static async Task<Reserva> AddReservaAsync(AppDbContext context, string numero)
    {
        var reserva = new Reserva { NumeroReserva = numero, Name = "Reserva " + numero, Status = EstadoReserva.Confirmed };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    private static async Task AddConfirmedHotelAsync(
        AppDbContext context, int supplierId, int reservaId, decimal netCost, string? currency = null)
    {
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            HotelName = "Hotel",
            City = "Ciudad",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2,
            Status = "Confirmado",
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency
        });
        await context.SaveChangesAsync();
    }

    private static SupplierPaymentRequest Advance(decimal amount, string? currency = null) =>
        new(Amount: amount, Method: "Transfer", Reference: null, Notes: null, ReservaId: null,
            ServicioReservaId: null, IsAdvanceToAccount: true, Currency: currency);

    /// <summary>Balance derivado del operador en una moneda (compras - pagos). 0 si no hay fila.</summary>
    private static async Task<decimal> SupplierBalanceAsync(AppDbContext context, int supplierId, string currency)
    {
        var row = await context.SupplierBalanceByCurrency.AsNoTracking()
            .FirstOrDefaultAsync(r => r.SupplierId == supplierId && r.Currency == currency);
        return row?.Balance ?? 0m;
    }

    /// <summary>Pool disponible (Σ RemainingBalance) del operador en una moneda.</summary>
    private static async Task<decimal> PoolAsync(AppDbContext context, int supplierId, string currency)
    {
        var rows = await context.SupplierCreditEntries.AsNoTracking()
            .Where(e => e.SupplierId == supplierId).Select(e => new { e.Currency, e.RemainingBalance }).ToListAsync();
        return rows.Where(r => Monedas.Normalizar(r.Currency) == currency).Sum(r => r.RemainingBalance);
    }

    /// <summary>Aplicaciones netas (Applied - Reversed) del operador en una moneda.</summary>
    private static async Task<decimal> NetApplicationsAsync(AppDbContext context, int supplierId, string currency)
    {
        var rows = await context.SupplierCreditApplications.AsNoTracking()
            .Where(a => a.Entry.SupplierId == supplierId)
            .Select(a => new { a.Kind, a.Amount, a.Entry.Currency })
            .ToListAsync();
        return rows.Where(r => Monedas.Normalizar(r.Currency) == currency)
            .Sum(r => r.Kind == SupplierCreditApplicationKind.Applied ? r.Amount : -r.Amount);
    }

    /// <summary>
    /// INVARIANTE autoritativo por proveedor+moneda: el pool refleja el sobrepago global MENOS lo aplicado.
    /// <c>Σ RemainingBalance == max(0, -Balance) - Σ aplicaciones netas</c>.
    /// </summary>
    private static async Task AssertInvariantAsync(AppDbContext context, int supplierId, string currency)
    {
        decimal balance = await SupplierBalanceAsync(context, supplierId, currency);
        decimal overpayment = balance < 0m ? -balance : 0m;
        decimal pool = await PoolAsync(context, supplierId, currency);
        decimal netApplications = await NetApplicationsAsync(context, supplierId, currency);

        Assert.Equal(overpayment - netApplications, pool);
    }

    // ============================================================
    // 1) Sobrepago crea entry (caso degenerado: pool == -Balance)
    // ============================================================

    [Fact]
    public async Task Overpayment_creates_credit_entry_equal_to_negative_balance()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F1");
        await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 5000m);

        var supplierService = CreateSupplierService(context);
        // Anticipo de 8000 ARS contra una deuda confirmada de 5000 -> sobrepago global 3000.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(8000m), CancellationToken.None);

        Assert.Equal(-3000m, await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS));

        // El pool se materializo con el sobrepago: un entry de 3000.
        var entries = await context.SupplierCreditEntries.AsNoTracking()
            .Where(e => e.SupplierId == supplier.Id).ToListAsync();
        Assert.Single(entries);
        Assert.Equal(3000m, entries[0].CreditedAmount);
        Assert.Equal(3000m, entries[0].RemainingBalance);
        Assert.Equal(Monedas.ARS, entries[0].Currency);

        // Caso degenerado del invariante: sin aplicaciones, pool == -Balance.
        await AssertInvariantAsync(context, supplier.Id, Monedas.ARS);
    }

    [Fact]
    public async Task No_overpayment_creates_no_entry()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F1");
        await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 5000m);

        var supplierService = CreateSupplierService(context);
        // Pago de 4000 contra deuda 5000: queda deuda 1000, NO hay sobrepago.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(4000m), CancellationToken.None);

        Assert.Equal(1000m, await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS));
        Assert.Empty(await context.SupplierCreditEntries.AsNoTracking().Where(e => e.SupplierId == supplier.Id).ToListAsync());
        await AssertInvariantAsync(context, supplier.Id, Monedas.ARS);
    }

    // ============================================================
    // 2) Aplicar: drena el pool, baja la deuda destino, NO mueve el Balance agregado
    // ============================================================

    [Fact]
    public async Task Apply_drains_pool_and_lowers_target_reserva_debt_without_moving_aggregate_balance()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);

        // Reserva destino con deuda de 1000 ARS de este operador.
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        // Anticipo de 5000 ARS: sobrepago global = 5000 - 1000 (deuda confirmada del destino) = 4000.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        decimal balanceBefore = await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS);
        Assert.Equal(-4000m, balanceBefore);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        var creditService = CreateCreditService(context);
        var result = await creditService.ApplyCreditAsync(
            supplier.Id,
            new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        Assert.False(result.IsReversal);
        Assert.Equal(1000m, result.Amount);
        Assert.Equal(3000m, result.AvailableBalanceAfter);

        // Pool drenado en 1000.
        Assert.Equal(3000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // El Balance agregado NO se movio (aplicar es neto-cero a nivel operador).
        Assert.Equal(balanceBefore, await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS));

        // La deuda-por-reserva del destino bajo 1000 (de 1000 a 0) y se refleja como credito aplicado.
        var byReserva = await supplierService.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);
        var targetLine = byReserva.Reservas.Single(r => r.ReservaPublicId == target.PublicId);
        var arsLine = targetLine.Currencies.Single(c => c.Currency == Monedas.ARS);
        Assert.Equal(1000m, arsLine.ConfirmedPurchases);
        Assert.Equal(1000m, arsLine.CreditApplied);
        Assert.Equal(0m, arsLine.Balance);

        await AssertInvariantAsync(context, supplier.Id, Monedas.ARS);
    }

    [Fact]
    public async Task Apply_more_than_available_is_rejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        var creditService = CreateCreditService(context);
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            creditService.ApplyCreditAsync(
                supplier.Id,
                new ApplySupplierCreditRequest(Monedas.ARS, 5000m, target.PublicId),
                UserId, "Tester", CancellationToken.None));

        // Nada se drena cuando se rechaza.
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));
    }

    [Fact]
    public async Task Apply_to_reserva_of_another_operator_is_rejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var other = await AddSupplierAsync(context);

        // El destino tiene servicios del OTRO operador, no del que tiene el saldo a favor.
        var target = await AddReservaAsync(context, "OTHER");
        await AddConfirmedHotelAsync(context, other.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            creditService.ApplyCreditAsync(
                supplier.Id,
                new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
    }

    // ============================================================
    // 3) Reversa: repone el pool y deshace la imputacion en destino
    // ============================================================

    [Fact]
    public async Task Reverse_restores_pool_and_target_debt()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        var apply = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(3000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        decimal balanceBefore = await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS);

        var reverse = await creditService.ReverseApplicationAsync(
            supplier.Id, apply.ApplicationPublicId,
            new ReverseSupplierCreditApplicationRequest("Imputacion equivocada, se revierte"),
            UserId, "Tester", CancellationToken.None);

        Assert.True(reverse.IsReversal);
        Assert.Equal(4000m, reverse.AvailableBalanceAfter);

        // Pool repuesto y Balance agregado intacto.
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));
        Assert.Equal(balanceBefore, await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS));

        // La deuda del destino volvio a 1000 (credito aplicado neto = 0).
        var byReserva = await supplierService.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);
        var arsLine = byReserva.Reservas.Single(r => r.ReservaPublicId == target.PublicId)
            .Currencies.Single(c => c.Currency == Monedas.ARS);
        Assert.Equal(0m, arsLine.CreditApplied);
        Assert.Equal(1000m, arsLine.Balance);

        await AssertInvariantAsync(context, supplier.Id, Monedas.ARS);
    }

    [Fact]
    public async Task Double_reverse_is_rejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        var apply = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        var reason = new ReverseSupplierCreditApplicationRequest("Imputacion equivocada, se revierte");
        await creditService.ReverseApplicationAsync(supplier.Id, apply.ApplicationPublicId, reason, UserId, "Tester", CancellationToken.None);

        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            creditService.ReverseApplicationAsync(supplier.Id, apply.ApplicationPublicId, reason, UserId, "Tester", CancellationToken.None));
    }

    // ============================================================
    // 4) Multimoneda: ARS y USD no se cruzan
    // ============================================================

    [Fact]
    public async Task Currencies_do_not_cross_pool_or_apply()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);

        // Deuda USD de 1000 en una reserva destino (servicio en USD).
        var target = await AddReservaAsync(context, "USD-DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m, currency: Monedas.USD);

        var supplierService = CreateSupplierService(context);
        // Anticipo en USD de 4000 -> sobrepago USD = 3000. NO toca ARS.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(4000m, Monedas.USD), CancellationToken.None);

        Assert.Equal(-3000m, await SupplierBalanceAsync(context, supplier.Id, Monedas.USD));
        Assert.Equal(3000m, await PoolAsync(context, supplier.Id, Monedas.USD));
        Assert.Equal(0m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        var creditService = CreateCreditService(context);

        // Intentar aplicar como si fuera ARS: no hay pool ARS -> rechazo (no cruza).
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            creditService.ApplyCreditAsync(
                supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 500m, target.PublicId),
                UserId, "Tester", CancellationToken.None));

        // Aplicar USD a la reserva USD: ok.
        var result = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.USD, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(2000m, result.AvailableBalanceAfter);

        // El pool ARS sigue intacto en 0; USD bajo.
        Assert.Equal(0m, await PoolAsync(context, supplier.Id, Monedas.ARS));
        Assert.Equal(2000m, await PoolAsync(context, supplier.Id, Monedas.USD));

        await AssertInvariantAsync(context, supplier.Id, Monedas.USD);
        await AssertInvariantAsync(context, supplier.Id, Monedas.ARS);
    }

    [Fact]
    public async Task Apply_in_currency_with_no_destination_debt_is_rejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);

        // El destino solo opera en ARS con el operador.
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        // Sobrepago en USD (anticipo USD), pero el destino no tiene deuda USD.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(2000m, Monedas.USD), CancellationToken.None);

        var creditService = CreateCreditService(context);
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            creditService.ApplyCreditAsync(
                supplier.Id, new ApplySupplierCreditRequest(Monedas.USD, 1000m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
    }

    // ============================================================
    // 5) Reduccion de sobrepago: drena el pool no aplicado; bloquea si ya se aplico
    // ============================================================

    [Fact]
    public async Task Reducing_overpayment_drains_unapplied_pool()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F1");
        await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        var paymentPublicId = await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // Editar el anticipo de 5000 a 2000: sobrepago baja de 4000 a 1000. El pool no aplicado se drena.
        var paymentId = await context.SupplierPayments.AsNoTracking()
            .Where(p => p.PublicId == paymentPublicId).Select(p => p.Id).FirstAsync();
        await supplierService.UpdateSupplierPaymentAsync(supplier.Id, paymentId, Advance(2000m), CancellationToken.None);

        Assert.Equal(-1000m, await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS));
        Assert.Equal(1000m, await PoolAsync(context, supplier.Id, Monedas.ARS));
        await AssertInvariantAsync(context, supplier.Id, Monedas.ARS);
    }

    [Fact]
    public async Task Reducing_overpayment_below_applied_credit_is_blocked()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        // Destino con deuda 4000 (asi se puede aplicar 3500 sin pasar el tope M1 de la deuda destino).
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 4000m);

        var supplierService = CreateSupplierService(context);
        // Anticipo 8000 contra deuda 4000 -> sobrepago 4000.
        var paymentPublicId = await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(8000m), CancellationToken.None);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        var creditService = CreateCreditService(context);
        // Aplicamos casi todo el saldo a favor (3500 de 4000) a la deuda del destino (4000).
        await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 3500m, target.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(500m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // Bajar el pago de 8000 a 5000 dejaria el sobrepago en 1000, pero ya se aplicaron 3500: se bloquea.
        var paymentId = await context.SupplierPayments.AsNoTracking()
            .Where(p => p.PublicId == paymentPublicId).Select(p => p.Id).FirstAsync();
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            supplierService.UpdateSupplierPaymentAsync(supplier.Id, paymentId, Advance(5000m), CancellationToken.None));
    }

    // ============================================================
    // B1 (review): el pool no puede gastar "credito fantasma" que el sobrepago derivado ya no respalda
    // ============================================================

    [Fact]
    public async Task Apply_is_capped_at_live_overpayment_even_if_pool_is_stale()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);

        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        // Anticipo 5000 -> sobrepago 4000, pool 4000.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // Aparece un servicio confirmado del operador en OTRA reserva (costo 4000) que consume el sobrepago.
        // Recalculamos el Balance derivado pero el reconciler NO corre (no es evento de pago): Balance vuelve a 0
        // (overpayment 0) y el pool queda OPTIMISTA en 4000 (fantasma).
        var filler = await AddReservaAsync(context, "FILLER");
        await AddConfirmedHotelAsync(context, supplier.Id, filler.Id, netCost: 4000m);
        await supplierService.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        Assert.Equal(0m, await SupplierBalanceAsync(context, supplier.Id, Monedas.ARS));
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // B1: aplicar se topea en el sobrepago REAL (0), aunque el pool muestre 4000.
        var creditService = CreateCreditService(context);
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            creditService.ApplyCreditAsync(
                supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 500m, target.PublicId),
                UserId, "Tester", CancellationToken.None));

        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS)); // no se drena al rechazar
    }

    // ============================================================
    // M1 (review): no sobre-aplicar mas que la deuda viva del destino
    // ============================================================

    [Fact]
    public async Task Apply_more_than_destination_debt_is_rejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);

        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        var creditService = CreateCreditService(context);
        // 2000 < pool (4000) y < sobrepago (4000), pero > deuda del destino (1000): se rechaza (M1).
        await Assert.ThrowsAsync<BusinessInvariantViolationException>(() =>
            creditService.ApplyCreditAsync(
                supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 2000m, target.PublicId),
                UserId, "Tester", CancellationToken.None));
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // Aplicar exactamente la deuda del destino (1000) si esta permitido.
        var result = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);
        Assert.Equal(1000m, result.Amount);
        Assert.Equal(3000m, await PoolAsync(context, supplier.Id, Monedas.ARS));
        await AssertInvariantAsync(context, supplier.Id, Monedas.ARS);
    }

    // ============================================================
    // M2 (review): el drenaje de saldo a favor se audita
    // ============================================================

    [Fact]
    public async Task Draining_pool_on_payment_edit_emits_audit_event()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F1");
        await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        // SupplierService CON auditoria mockeada para capturar el evento de drenaje.
        var accessor = SeeCostAccessor(out var resolver);
        var audit = new Mock<IAuditService>();
        var supplierService = new SupplierService(context, auditService: audit.Object,
            httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);

        var paymentPublicId = await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // Editar 5000 -> 3000: el sobrepago baja de 4000 a 2000; se drena 2000 del pool no aplicado.
        var paymentId = await context.SupplierPayments.AsNoTracking()
            .Where(p => p.PublicId == paymentPublicId).Select(p => p.Id).FirstAsync();
        await supplierService.UpdateSupplierPaymentAsync(supplier.Id, paymentId, Advance(3000m), CancellationToken.None);

        Assert.Equal(2000m, await PoolAsync(context, supplier.Id, Monedas.ARS));
        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.SupplierCreditDrained,
            AuditActions.SupplierCreditEntryEntityName,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.AtLeastOnce);
    }

    // ============================================================
    // 6) Motivo de reversa OPCIONAL (simetrico con el lado cliente)
    // ============================================================

    [Fact]
    public async Task Reverse_without_reason_proceeds()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        var apply = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Motivo null: la reversa procede igual y el pool se repone.
        var reverse = await creditService.ReverseApplicationAsync(
            supplier.Id, apply.ApplicationPublicId,
            new ReverseSupplierCreditApplicationRequest(Reason: null),
            UserId, "Tester", CancellationToken.None);

        Assert.True(reverse.IsReversal);
        Assert.Equal(4000m, await PoolAsync(context, supplier.Id, Monedas.ARS));

        // La contra-fila Reversed quedo sin motivo.
        var reversalRow = await context.SupplierCreditApplications.AsNoTracking()
            .Where(a => a.Kind == SupplierCreditApplicationKind.Reversed)
            .FirstAsync();
        Assert.Null(reversalRow.ReversalReason);
    }

    [Fact]
    public async Task Reverse_with_reason_records_it()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        var apply = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        await creditService.ReverseApplicationAsync(
            supplier.Id, apply.ApplicationPublicId,
            new ReverseSupplierCreditApplicationRequest("Se cargo en la reserva equivocada"),
            UserId, "Tester", CancellationToken.None);

        var reversalRow = await context.SupplierCreditApplications.AsNoTracking()
            .Where(a => a.Kind == SupplierCreditApplicationKind.Reversed)
            .FirstAsync();
        Assert.Equal("Se cargo en la reserva equivocada", reversalRow.ReversalReason);
    }

    // ============================================================
    // 7) ActiveApplications en el overview: aparece tras aplicar, desaparece tras revertir
    // ============================================================

    [Fact]
    public async Task Overview_active_applications_appear_after_apply_and_vanish_after_reverse()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var holder = new Customer { FullName = "Titular Destino" };
        context.Customers.Add(holder);
        await context.SaveChangesAsync();

        var target = await AddReservaAsync(context, "DEST");
        target.PayerId = holder.Id;
        await context.SaveChangesAsync();
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        var creditService = CreateCreditService(context);

        // Antes de aplicar: no hay aplicaciones vivas.
        var before = await creditService.GetSupplierCreditAsync(supplier.Id, CancellationToken.None);
        Assert.Empty(before.ActiveApplications);

        var apply = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Tras aplicar: aparece la fila viva con su numero y titular de reserva destino.
        var afterApply = await creditService.GetSupplierCreditAsync(supplier.Id, CancellationToken.None);
        var application = Assert.Single(afterApply.ActiveApplications);
        Assert.Equal(apply.ApplicationPublicId, application.ApplicationPublicId);
        Assert.Equal(1000m, application.Amount);
        Assert.Equal(Monedas.ARS, application.Currency);
        Assert.Equal(target.PublicId, application.TargetReservaPublicId);
        Assert.Equal("DEST", application.TargetReservaNumber);
        Assert.Equal("Titular Destino", application.TargetReservaHolderName);

        await creditService.ReverseApplicationAsync(
            supplier.Id, apply.ApplicationPublicId,
            new ReverseSupplierCreditApplicationRequest(Reason: null),
            UserId, "Tester", CancellationToken.None);

        // Tras revertir: la aplicacion deja de estar viva.
        var afterReverse = await creditService.GetSupplierCreditAsync(supplier.Id, CancellationToken.None);
        Assert.Empty(afterReverse.ActiveApplications);
    }

    [Fact]
    public async Task Overview_active_application_amount_is_masked_without_see_cost()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, Advance(5000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Overview SIN permiso cobranzas.see_cost: la estructura queda visible pero el monto en 0.
        var maskedService = CreateMaskedCreditService(context);
        var overview = await maskedService.GetSupplierCreditAsync(supplier.Id, CancellationToken.None);
        var application = Assert.Single(overview.ActiveApplications);
        Assert.Equal(0m, application.Amount);
        Assert.Equal(target.PublicId, application.TargetReservaPublicId); // los identificadores NO se enmascaran
    }
}
