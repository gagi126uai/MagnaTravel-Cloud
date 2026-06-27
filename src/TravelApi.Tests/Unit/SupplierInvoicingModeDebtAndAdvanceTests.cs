using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// (2026-06-26) Auditoria pata de proveedores.
///
/// <para><b>AGUJERO 1</b> — la deuda con el operador (Cuenta por Pagar) ahora respeta
/// <see cref="Supplier.InvoicingMode"/>: un proveedor <see cref="SupplierInvoicingMode.CommissionOnly"/>
/// (intermediacion) NO genera deuda de compra por el costo (el operador factura directo al cliente, la agencia
/// no compra). Un <see cref="SupplierInvoicingMode.TotalToCustomer"/> (reseller) se comporta como antes
/// (deuda = NetCost). Reserva mixta: solo cuenta el reseller.</para>
///
/// <para><b>AGUJERO 3</b> — el anticipo "a cuenta" (sin reserva) se valida POR MONEDA contra la deuda viva del
/// proveedor en esa moneda, no contra el surrogate mezclado: un anticipo en USD no puede "comerse" deuda en ARS
/// ni pagar de mas en su moneda.</para>
/// </summary>
public class SupplierInvoicingModeDebtAndAdvanceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static SupplierService CreateService(AppDbContext context)
    {
        const string userId = "tester";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

        return new SupplierService(
            context,
            auditService: null,
            httpContextAccessor: accessor,
            logger: null,
            permissionResolver: resolver.Object);
    }

    private static async Task<Supplier> AddSupplierAsync(AppDbContext context, SupplierInvoicingMode mode)
    {
        var supplier = new Supplier { Name = "Operador " + mode, InvoicingMode = mode, IsActive = true };
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

    private static void AddConfirmedHotel(
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
    }

    // ============================================================ AGUJERO 1 ============================================================

    [Fact]
    public async Task CommissionOnlySupplier_ConfirmedService_GeneratesZeroDebt()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.CommissionOnly);
        var reserva = await AddReservaAsync(context, "F-CO");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 5000m);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        // Escalar surrogate: 0 (no hay compra que la agencia le deba al operador en intermediacion).
        var refreshed = await context.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id);
        Assert.Equal(0m, refreshed.CurrentBalance);

        // Tabla hija por moneda: sin filas (no hay compras ni pagos).
        var byCurrency = await context.SupplierBalanceByCurrency.AsNoTracking()
            .Where(r => r.SupplierId == supplier.Id).ToListAsync();
        Assert.Empty(byCurrency);

        // Resumen de cuenta: compras y saldo en 0.
        var overview = await service.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(0m, overview.Summary.TotalPurchases);
        Assert.Equal(0m, overview.Summary.Balance);

        // Deuda por reserva: ninguna reserva acumula deuda.
        var byReserva = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);
        Assert.Empty(byReserva.Reservas);
    }

    [Theory]
    [InlineData(SupplierInvoicingMode.TotalToCustomer)]
    public async Task ResellerSupplier_ConfirmedService_GeneratesDebtEqualToNetCost(SupplierInvoicingMode mode)
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, mode);
        var reserva = await AddReservaAsync(context, "F-RS");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 5000m);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var refreshed = await context.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id);
        Assert.Equal(5000m, refreshed.CurrentBalance); // comportamiento historico intacto

        var arsRow = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.ARS);
        Assert.Equal(5000m, arsRow.ConfirmedPurchases);
        Assert.Equal(5000m, arsRow.Balance);

        var overview = await service.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);
        Assert.Equal(5000m, overview.Summary.TotalPurchases);
    }

    [Fact]
    public async Task DefaultSupplier_BehavesAsReseller_RegressionGuard()
    {
        // Un Supplier nuevo nace TotalToCustomer (default). El fix NO debe cambiar el comportamiento legacy.
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Default", IsActive = true }; // sin setear InvoicingMode
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        var reserva = await AddReservaAsync(context, "F-DEF");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1234m);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var refreshed = await context.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id);
        Assert.Equal(1234m, refreshed.CurrentBalance);
    }

    [Fact]
    public async Task MixedReserva_OnlyResellerSupplierCounts()
    {
        // Una reserva con un servicio de cada modo: solo el reseller genera deuda; el intermediado, cero.
        await using var context = CreateContext();
        var reseller = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var intermediary = await AddSupplierAsync(context, SupplierInvoicingMode.CommissionOnly);
        var reserva = await AddReservaAsync(context, "F-MIX");
        AddConfirmedHotel(context, reseller.Id, reserva.Id, netCost: 3000m);
        AddConfirmedHotel(context, intermediary.Id, reserva.Id, netCost: 8000m);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateBalanceAsync(reseller.Id, CancellationToken.None);
        await service.UpdateBalanceAsync(intermediary.Id, CancellationToken.None);

        var resellerRow = await context.Suppliers.AsNoTracking().FirstAsync(s => s.Id == reseller.Id);
        var intermediaryRow = await context.Suppliers.AsNoTracking().FirstAsync(s => s.Id == intermediary.Id);

        Assert.Equal(3000m, resellerRow.CurrentBalance); // solo el reseller
        Assert.Equal(0m, intermediaryRow.CurrentBalance); // el intermediado no debe nada por su costo
    }

    [Fact]
    public async Task CommissionOnlySupplier_WithPreexistingPayment_ShowsCreditBalance_PaymentNotLost()
    {
        // Un proveedor CommissionOnly no genera deuda de compra, pero si TIENE pagos (ej. una seña historica) el
        // pago NO se pierde: queda como SALDO A FAVOR (balance negativo) en su moneda, no como "deuda negativa rara".
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.CommissionOnly);
        var reserva = await AddReservaAsync(context, "F-CO-PAY");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 5000m); // ARS, NO cuenta (intermediacion)
        // Pago preexistente directo en DB (sin reserva = a cuenta; Currency null -> ARS).
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            Amount = 700m,
            Method = "Transfer"
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        // Compras = 0 (CommissionOnly), pago 700 -> saldo a favor -700 en ARS. El escalar mono-moneda es la deuda
        // cruda (puede ser negativa).
        var refreshed = await context.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplier.Id);
        Assert.Equal(-700m, refreshed.CurrentBalance);

        var ars = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.ARS);
        Assert.Equal(0m, ars.ConfirmedPurchases); // el costo NO se cuenta
        Assert.Equal(700m, ars.TotalPaid);        // el pago se preserva (no se pierde)
        Assert.Equal(-700m, ars.Balance);         // saldo a favor coherente
    }

    [Fact]
    public async Task ReservaSupplierPaymentStatus_OmitsCommissionOnlyServices_KeepsResellerService()
    {
        // AGUJERO 1 (cierre del gap): la vista "pagado al operador por servicio" no debe reportar estado de pago
        // para servicios de proveedores CommissionOnly (intermediacion: no hay deuda con el operador, no es
        // "impago"). El servicio reseller en la misma reserva sigue mostrando su estado normal.
        await using var context = CreateContext();
        var reseller = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var intermediary = await AddSupplierAsync(context, SupplierInvoicingMode.CommissionOnly);
        var reserva = await AddReservaAsync(context, "F-PAYSTATUS");
        AddConfirmedHotel(context, reseller.Id, reserva.Id, netCost: 1000m);
        AddConfirmedHotel(context, intermediary.Id, reserva.Id, netCost: 2000m);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        // Solo el reseller aparece; el intermediado NO reporta estado de pago al operador.
        var line = Assert.Single(dto.Services);
        Assert.Equal(reseller.PublicId, line.SupplierPublicId);
        Assert.DoesNotContain(dto.Services, s => s.SupplierPublicId == intermediary.PublicId);
        // El reseller sigue con su estado normal (sin pagos -> impago, con su costo visible).
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
        Assert.Equal(1000m, line.NetCost);
    }

    // ============================================================ AGUJERO 3 ============================================================

    [Fact]
    public async Task AdvanceToAccount_WithinSameCurrencyDebt_IsAccepted()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var reserva = await AddReservaAsync(context, "F-ADV-OK");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.ARS);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        // Anticipo ARS 400 contra deuda ARS 1000 -> OK.
        var paymentId = await service.AddSupplierPaymentAsync(
            supplier.Id,
            new SupplierPaymentRequest(Amount: 400m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true, Currency: Monedas.ARS),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, paymentId);
    }

    // CRITERIO NUEVO (decision del dueño 2026-06-26): el anticipo "a cuenta" es un PREPAGO/SEÑA genuino, SIN tope
    // superior. Lo unico que se garantiza es la AISLACION POR MONEDA: un anticipo en una moneda nunca afecta otra.

    [Fact]
    public async Task AdvanceToAccount_InForeignCurrency_BecomesCreditInThatCurrency_DoesNotTouchOtherDebt()
    {
        // Deuda SOLO en ARS; un anticipo en USD NO la toca: se acepta y queda como saldo a favor (negativo) en USD.
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var reserva = await AddReservaAsync(context, "F-ADV-FX");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.ARS);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var paymentId = await service.AddSupplierPaymentAsync(
            supplier.Id,
            new SupplierPaymentRequest(Amount: 500m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true, Currency: Monedas.USD),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, paymentId); // NO se rechaza: prepago genuino

        var ars = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.ARS);
        Assert.Equal(1000m, ars.Balance); // ARS intacta: el anticipo USD NO la compenso

        var usd = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.USD);
        Assert.Equal(-500m, usd.Balance); // saldo a favor en USD (prepago)
    }

    [Fact]
    public async Task AdvanceToAccount_AboveDebtInSameCurrency_IsAccepted_AsCredit()
    {
        // Anticipo POR ENCIMA de la deuda de su moneda: ahora es VALIDO (antes se rechazaba). Deja saldo a favor.
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var reserva = await AddReservaAsync(context, "F-ADV-OVER");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.USD);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var paymentId = await service.AddSupplierPaymentAsync(
            supplier.Id,
            new SupplierPaymentRequest(Amount: 1500m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true, Currency: Monedas.USD),
            CancellationToken.None);

        Assert.NotEqual(Guid.Empty, paymentId);

        var usd = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.USD);
        Assert.Equal(-500m, usd.Balance); // 1000 - 1500 = -500 (saldo a favor / seña por adelantado)
    }

    [Fact]
    public async Task AdvanceToAccount_OnlyAffectsItsOwnCurrency_NeverAnother()
    {
        // Proveedor con deuda ARS 1000 Y USD 1000. Un anticipo USD 1500 SOLO mueve la posicion USD; la ARS no cambia.
        // (Antes el surrogate mezclado 2000 dejaba que el USD "tocara" la ARS — ese era el bug.)
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var reserva = await AddReservaAsync(context, "F-ADV-ISO");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.ARS);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.USD);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        await service.AddSupplierPaymentAsync(
            supplier.Id,
            new SupplierPaymentRequest(Amount: 1500m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true, Currency: Monedas.USD),
            CancellationToken.None);

        var ars = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.ARS);
        var usd = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.USD);

        Assert.Equal(1000m, ars.Balance);  // ARS intacta
        Assert.Equal(-500m, usd.Balance);  // USD: 1000 - 1500 (la unica posicion afectada)
    }

    [Fact]
    public async Task EditReservaImputedPayment_ToHigherAmountWithinReservaDebt_Succeeds_NoDoubleCount()
    {
        // (4d) La edicion de un pago imputado a una reserva usa excludePaymentId: el monto VIEJO del propio pago no
        // se cuenta como "ya pagado". Reserva con deuda ARS 1000, pago de 300 imputado; editarlo a 800 (<= 1000)
        // debe pasar. Sin excludePaymentId, veria 300 + 800 = 1100 > 1000 y rechazaria por error.
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var reserva = await AddReservaAsync(context, "F-EDIT");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.ARS);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        // Alta: 300 imputado a la reserva.
        var paymentPublicId = await service.AddSupplierPaymentAsync(
            supplier.Id,
            new SupplierPaymentRequest(Amount: 300m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false,
                Currency: Monedas.ARS),
            CancellationToken.None);
        var paymentId = await context.SupplierPayments.AsNoTracking()
            .Where(p => p.PublicId == paymentPublicId).Select(p => p.Id).SingleAsync();

        // Edicion a 800 (dentro de la deuda 1000 de la reserva) -> NO debe lanzar.
        await service.UpdateSupplierPaymentAsync(
            supplier.Id, paymentId,
            new SupplierPaymentRequest(Amount: 800m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false,
                Currency: Monedas.ARS),
            CancellationToken.None);

        var updated = await context.SupplierPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
        Assert.Equal(800m, updated.Amount);
    }

    [Fact]
    public async Task ReservaImputedPayment_OverDebt_OnlyAffectsItsCurrency_ExcessIsCredit()
    {
        // (2026-06-26) Un pago al operador imputado a una RESERVA por encima de su deuda en esa moneda se acepta;
        // el excedente queda como saldo a favor en ESA moneda y NUNCA toca la deuda de otra moneda.
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var reserva = await AddReservaAsync(context, "F-IMP-ISO");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.ARS);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.USD);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        // USD 1500 imputado a la reserva (sin servicio puntual): excede la deuda USD (1000) -> aceptado.
        var paymentId = await service.AddSupplierPaymentAsync(
            supplier.Id,
            new SupplierPaymentRequest(Amount: 1500m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, Currency: Monedas.USD),
            CancellationToken.None);
        Assert.NotEqual(Guid.Empty, paymentId);

        var ars = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.ARS);
        var usd = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.USD);
        Assert.Equal(1000m, ars.Balance);  // ARS intacta: el pago USD no la toco
        Assert.Equal(-500m, usd.Balance);  // USD: 1000 - 1500 = -500 (saldo a favor)
    }

    [Fact]
    public async Task EditReservaImputedPayment_ToAmountOverReservaDebt_Accepted_ExcessIsCredit()
    {
        // (2026-06-26, decision del dueño) Simetria del alta: EDITAR un pago imputado a una reserva para que pase
        // a SUPERAR la deuda de esa reserva tambien se acepta; el excedente queda como saldo a favor en esa moneda.
        // Antes la edicion rechazaba por el tope por reserva; ahora la unica validacion que queda es la de moneda.
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.TotalToCustomer);
        var reserva = await AddReservaAsync(context, "F-EDIT-OVER");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: Monedas.ARS);
        await context.SaveChangesAsync();

        var service = CreateService(context);

        // Alta: 300 imputado a la reserva (dentro de la deuda).
        var paymentPublicId = await service.AddSupplierPaymentAsync(
            supplier.Id,
            new SupplierPaymentRequest(Amount: 300m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false,
                Currency: Monedas.ARS),
            CancellationToken.None);
        var paymentId = await context.SupplierPayments.AsNoTracking()
            .Where(p => p.PublicId == paymentPublicId).Select(p => p.Id).SingleAsync();

        // Edicion a 1500 (> deuda 1000 de la reserva) -> ya NO rechaza; el excedente es saldo a favor.
        await service.UpdateSupplierPaymentAsync(
            supplier.Id, paymentId,
            new SupplierPaymentRequest(Amount: 1500m, Method: "Transfer", Reference: null, Notes: null,
                ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false,
                Currency: Monedas.ARS),
            CancellationToken.None);

        var updated = await context.SupplierPayments.AsNoTracking().SingleAsync(p => p.Id == paymentId);
        Assert.Equal(1500m, updated.Amount);

        var ars = await context.SupplierBalanceByCurrency.AsNoTracking()
            .SingleAsync(r => r.SupplierId == supplier.Id && r.Currency == Monedas.ARS);
        Assert.Equal(-500m, ars.Balance); // 1000 - 1500 = -500 saldo a favor (sin doble conteo del monto viejo)
    }
}
