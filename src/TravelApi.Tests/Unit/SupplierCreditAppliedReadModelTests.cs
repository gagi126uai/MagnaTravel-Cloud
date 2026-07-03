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
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Bug del dueño (dogfood, 2026-07-03): al usar el saldo a favor en ARS de un operador (ApplyCreditAsync) para
/// cubrir deudas, (1) el recuadro "Saldo a favor" del header NO bajaba y (2) las reservas cubiertas seguian
/// mostrando "Operador impago". Raiz: los LECTORES (extracto del operador + estado de pago por servicio) no
/// descontaban las aplicaciones vivas de saldo a favor; el pool si bajaba.
///
/// <para>Estos tests reproducen el escenario y verifican el FIX de los lectores (una sola verdad: lo que se VE
/// == lo que se puede GASTAR == el pool). NO se toca la mecanica del pool (drenaje/reconciler/CHECK).</para>
///
/// <para>El pool se mintea por el camino real: un anticipo (AddSupplierPaymentAsync) dispara el reconciler.</para>
/// </summary>
public class SupplierCreditAppliedReadModelTests
{
    private const string UserId = "tester";

    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

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
        IReadOnlySet<string> perms = new HashSet<string>
        {
            Permissions.CobranzasSeeCost, Permissions.TesoreriaSupplierPayments
        };
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

    private static async Task<HotelBooking> AddConfirmedHotelAsync(
        AppDbContext context, int supplierId, int reservaId, decimal netCost,
        string? currency = null, DateTime? createdAt = null)
    {
        var hotel = new HotelBooking
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
            Currency = currency,
            CreatedAt = createdAt ?? DateTime.UtcNow
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();
        return hotel;
    }

    // Anticipo a cuenta (no imputado a reserva): dispara el reconciler que mintea el pool con el sobrepago.
    private static SupplierPaymentRequest AccountAdvance(decimal amount, string? currency = null) =>
        new(Amount: amount, Method: "Transfer", Reference: null, Notes: null, ReservaId: null,
            ServicioReservaId: null, IsAdvanceToAccount: true, Currency: currency);

    private static SupplierAccountStatementCurrencyBlockDto Block(SupplierAccountStatementDto dto, string currency)
        => dto.Currencies.Single(c => c.Currency == currency);

    private static ServiceSupplierPaymentStatusDto ServiceLine(ReservaSupplierPaymentStatusDto dto, Guid servicePublicId)
        => Assert.Single(dto.Services.Where(s => s.ServicePublicId == servicePublicId));

    // ============================================================
    // BUG 1: el recuadro "Saldo a favor" baja al aplicar (== pool) y el extracto cierra ahi.
    // Escenario: sobrepago 1000 (anticipo 2000, deuda destino 1000) -> aplicar los 1000 a la reserva destino
    // deja el saldo a favor mostrado en 0 (antes seguia mostrando 1000).
    // ============================================================
    [Fact]
    public async Task ApplyingAllCredit_dropsHeaderPrepaymentToZero_andExtractCloses()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        // Anticipo a cuenta de 2000: balance = 1000 (compra) - 2000 (pago) = -1000. Pool minteado = 1000.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, AccountAdvance(2000m), CancellationToken.None);

        var before = Block(await supplierService.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None), Monedas.ARS);
        Assert.Equal(1000m, before.Prepayment);          // saldo a favor bruto ANTES de aplicar
        Assert.Equal(-1000m, before.EconomicClosingBalance);

        // Aplicar los 1000 a la deuda de la reserva destino.
        var creditService = CreateCreditService(context);
        await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        var after = Block(await supplierService.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None), Monedas.ARS);
        // El recuadro "Saldo a favor" bajo a 0 (== pool restante) — antes seguia en 1000 (el bug).
        Assert.Equal(0m, after.Prepayment);
        Assert.Equal(0m, after.ITheyOwe);
        // El saldo unico del pie del extracto tambien cierra en 0 (incluye la linea de saldo a favor aplicado).
        Assert.Equal(0m, after.EconomicClosingBalance);
        Assert.Equal(0m, after.ClosingBalance);
        Assert.Equal(0m, after.Lines[^1].RunningBalance);
        // La CAJA no se movio (aplicar no es movimiento de efectivo): sigue en -1000 (invariante con la proyeccion).
        Assert.Equal(-1000m, after.CashClosingBalance);
        // Aparece la linea de saldo a favor aplicado (cargo +) referida a la reserva destino.
        var appliedLine = Assert.Single(after.Lines.Where(l => l.Kind == SupplierAccountStatementLineKinds.CreditApplied));
        Assert.Equal(1000m, appliedLine.Charge);
        Assert.Equal("DEST", appliedLine.DocumentRef);
        Assert.Contains("DEST", appliedLine.Description);

        // El "Saldo a favor" mostrado == pool disponible (una sola verdad).
        var overview = await creditService.GetSupplierCreditAsync(supplier.Id, CancellationToken.None);
        decimal poolAvailable = overview.Currencies
            .Where(c => c.Currency == Monedas.ARS).Sum(c => c.AvailableBalance);
        Assert.Equal(poolAvailable, after.Prepayment);
        Assert.Equal(0m, poolAvailable);
    }

    // ============================================================
    // BUG 2: la reserva cubierta con saldo a favor queda "paid" (antes seguia "impago" -> "operador impago").
    // ============================================================
    [Fact]
    public async Task ApplyingCredit_marksReservaServicePaid_withCreditAppliedAmount()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST2");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, AccountAdvance(2000m), CancellationToken.None);

        // Antes de aplicar: el servicio esta impago (sin pagos de caja).
        var beforeStatus = await supplierService.GetReservaSupplierPaymentStatusAsync(target.Id, CancellationToken.None);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, ServiceLine(beforeStatus, hotel.PublicId).Status);

        var creditService = CreateCreditService(context);
        await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        var afterStatus = await supplierService.GetReservaSupplierPaymentStatusAsync(target.Id, CancellationToken.None);
        var line = ServiceLine(afterStatus, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);   // ya NO "operador impago"
        Assert.Equal(1000m, line.NetCost);
        Assert.Equal(0m, line.PaidToOperator);            // no hubo pago de caja
        Assert.Equal(1000m, line.CreditAppliedToOperator);// se cubrio con saldo a favor
        Assert.Equal(0m, line.OutstandingToOperator);
    }

    // ============================================================
    // Enmascarado (gate data-exposure 2026-07-03): CreditAppliedToOperator es COSTO -> sin cobranzas.see_cost
    // viaja en 0, igual que NetCost/PaidToOperator/Outstanding. El STATUS (paid/unpaid) sigue visible (no es monto).
    // ============================================================
    [Fact]
    public async Task SinPermisoDeCostos_CreditAppliedToOperator_viajaEnCero()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DESTMASK");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, AccountAdvance(2000m), CancellationToken.None);
        var creditService = CreateCreditService(context);
        await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Lector SIN permisos (resolver que devuelve set vacio): fail-closed, montos enmascarados server-side.
        var noPermsResolver = new Mock<IUserPermissionResolver>();
        noPermsResolver.Setup(r => r.GetPermissionsAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)new HashSet<string>());
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    new List<Claim> { new(ClaimTypes.NameIdentifier, UserId) }, "Test"))
            }
        };
        var maskedReader = new SupplierService(context, auditService: null, httpContextAccessor: accessor,
            logger: null, permissionResolver: noPermsResolver.Object);

        var status = await maskedReader.GetReservaSupplierPaymentStatusAsync(target.Id, CancellationToken.None);
        var line = ServiceLine(status, hotel.PublicId);

        Assert.False(status.AmountsVisible);
        Assert.Equal(0m, line.CreditAppliedToOperator);   // el campo nuevo tambien va en 0
        Assert.Equal(0m, line.NetCost);
        Assert.Equal(0m, line.PaidToOperator);
        Assert.Equal(0m, line.OutstandingToOperator);
        // El estado (no-monto) sigue visible: cubierto con saldo a favor => paid.
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
    }

    // ============================================================
    // Aplicacion PARCIAL: el header muestra el pool restante y el credito se reparte FIFO (servicio mas antiguo
    // primero). Dos servicios de 1000 c/u; sobrepago 1500; se aplican 1500 -> el 1ro paga, el 2do parcial.
    // ============================================================
    [Fact]
    public async Task PartialCredit_headerEqualsPool_andCreditAttributedFifoByServiceAge()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST3");
        var older = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m,
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m,
            createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var supplierService = CreateSupplierService(context);
        // Deuda total 2000; anticipo 3500 -> balance -1500 -> pool 1500.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, AccountAdvance(3500m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1500m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Header: saldo a favor restante = 1500 (pool) - 1500 (aplicado)... pero el sobrepago bruto era 1500 y se
        // aplico todo -> 0. El pool restante es 0.
        var block = Block(await supplierService.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None), Monedas.ARS);
        var overview = await creditService.GetSupplierCreditAsync(supplier.Id, CancellationToken.None);
        decimal poolAvailable = overview.Currencies.Where(c => c.Currency == Monedas.ARS).Sum(c => c.AvailableBalance);
        Assert.Equal(poolAvailable, block.Prepayment);
        Assert.Equal(0m, block.Prepayment);

        // FIFO: el servicio MAS ANTIGUO se cubre primero (1000 -> paid), el mas nuevo recibe el resto (500 -> partial).
        var status = await supplierService.GetReservaSupplierPaymentStatusAsync(target.Id, CancellationToken.None);
        var olderLine = ServiceLine(status, older.PublicId);
        var newerLine = ServiceLine(status, newer.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, olderLine.Status);
        Assert.Equal(1000m, olderLine.CreditAppliedToOperator);
        Assert.Equal(ServiceSupplierPaymentStatuses.Partial, newerLine.Status);
        Assert.Equal(500m, newerLine.CreditAppliedToOperator);
        Assert.Equal(500m, newerLine.OutstandingToOperator);
    }

    // ============================================================
    // Reversa: deshace todo simetricamente en los lectores (header vuelve a mostrar el saldo a favor, el servicio
    // vuelve a impago, la linea del extracto desaparece).
    // ============================================================
    [Fact]
    public async Task ReversingApplication_restoresHeaderAndServiceStatus_symmetrically()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST4");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, AccountAdvance(2000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        var apply = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        // Revertir la aplicacion.
        await creditService.ReverseApplicationAsync(
            supplier.Id, apply.ApplicationPublicId, new ReverseSupplierCreditApplicationRequest(null),
            UserId, "Tester", CancellationToken.None);

        var block = Block(await supplierService.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None), Monedas.ARS);
        Assert.Equal(1000m, block.Prepayment);              // el saldo a favor volvio
        Assert.Equal(-1000m, block.EconomicClosingBalance);
        // La linea de saldo a favor aplicado desaparecio (la reversa netea la aplicacion a cero).
        Assert.DoesNotContain(block.Lines, l => l.Kind == SupplierAccountStatementLineKinds.CreditApplied);

        var status = await supplierService.GetReservaSupplierPaymentStatusAsync(target.Id, CancellationToken.None);
        var line = ServiceLine(status, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
        Assert.Equal(0m, line.CreditAppliedToOperator);
        Assert.Equal(1000m, line.OutstandingToOperator);
    }

    // ============================================================
    // No-regresion: el pool disponible baja al aplicar (esto ya andaba; queda como red de seguridad).
    // ============================================================
    [Fact]
    public async Task PoolAvailableBalance_dropsAfterApply()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST5");
        await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m);

        var supplierService = CreateSupplierService(context);
        await supplierService.AddSupplierPaymentAsync(supplier.Id, AccountAdvance(2000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        var result = await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        Assert.Equal(0m, result.AvailableBalanceAfter);
    }

    // ============================================================
    // Multimoneda dura: un saldo a favor ARS aplicado NO afecta el estado de un servicio en USD.
    // ============================================================
    [Fact]
    public async Task ArsCreditApplied_doesNotAffectUsdServiceStatus()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var target = await AddReservaAsync(context, "DEST6");
        var arsHotel = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 1000m, currency: "ARS");
        var usdHotel = await AddConfirmedHotelAsync(context, supplier.Id, target.Id, netCost: 300m, currency: "USD");

        var supplierService = CreateSupplierService(context);
        // Sobrepago SOLO en ARS (anticipo ARS 2000 contra deuda ARS 1000). El USD queda impago.
        await supplierService.AddSupplierPaymentAsync(supplier.Id, AccountAdvance(2000m), CancellationToken.None);

        var creditService = CreateCreditService(context);
        await creditService.ApplyCreditAsync(
            supplier.Id, new ApplySupplierCreditRequest(Monedas.ARS, 1000m, target.PublicId),
            UserId, "Tester", CancellationToken.None);

        var status = await supplierService.GetReservaSupplierPaymentStatusAsync(target.Id, CancellationToken.None);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, ServiceLine(status, arsHotel.PublicId).Status);
        // El servicio USD sigue impago: el saldo a favor ARS no cruza de moneda.
        var usdLine = ServiceLine(status, usdHotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, usdLine.Status);
        Assert.Equal(0m, usdLine.CreditAppliedToOperator);
        Assert.Equal(300m, usdLine.OutstandingToOperator);
    }
}
