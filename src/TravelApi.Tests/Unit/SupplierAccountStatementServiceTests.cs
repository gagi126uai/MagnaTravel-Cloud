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
/// TANDA 1 (cuenta corriente del proveedor) — cobertura del EXTRACTO de la Cuenta por Pagar y del fix de
/// monedas mezcladas en el overview, ATRAVESANDO el servicio (in-memory DB), no solo el builder puro.
///
/// <para><b>INVARIANTE RUNTIME</b>: el saldo de cierre del extracto por moneda DEBE igualar el
/// <c>SupplierBalanceByCurrency.Balance</c> persistido de esa moneda. Aca se prueba end-to-end: se siembran
/// servicios + pagos, se recalcula la proyeccion (<c>UpdateBalanceAsync</c>) y se compara el cierre del
/// extracto contra la fila persistida. Tambien se verifica la exclusion de CommissionOnly / soft-deleted y el
/// masking see_cost, que son los puntos donde el extracto podria crear una "segunda verdad".</para>
/// </summary>
public class SupplierAccountStatementServiceTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private const string SupplierPaymentsPermission = Permissions.TesoreriaSupplierPayments;

    private static SupplierService CreateServiceForUser(
        AppDbContext context, bool canSeeCost, bool canSeePaymentDetails = false)
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
        var granted = new HashSet<string>();
        if (canSeeCost) granted.Add(SeeCostPermission);
        if (canSeePaymentDetails) granted.Add(SupplierPaymentsPermission);

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = granted;
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

        return new SupplierService(
            context,
            auditService: null,
            httpContextAccessor: accessor,
            logger: null,
            permissionResolver: resolver.Object);
    }

    private static async Task<Supplier> AddSupplierAsync(AppDbContext context, SupplierInvoicingMode mode = SupplierInvoicingMode.TotalToCustomer)
    {
        var supplier = new Supplier { Name = "Operador", InvoicingMode = mode, IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        return supplier;
    }

    private static async Task<Reserva> AddReservaAsync(AppDbContext context, string numero, string status = "Confirmed")
    {
        var reserva = new Reserva { NumeroReserva = numero, Name = "Reserva " + numero, Status = status };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    private static void AddConfirmedHotel(AppDbContext context, int supplierId, int reservaId, decimal netCost, string? currency = null)
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

    // Vuelo CONFIRMADO: el estado se mapea por codigo IATA (HK = confirmado), NO por texto. La query del
    // proveedor etiqueta estos como Type "Vuelo", que dispara MapFlightStatus en CountsForSupplierDebtByType.
    private static void AddConfirmedFlight(AppDbContext context, int supplierId, int reservaId, decimal netCost, string currency)
    {
        context.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            AirlineCode = "AR",
            FlightNumber = "1234",
            Origin = "EZE",
            Destination = "MIA",
            DepartureTime = DateTime.UtcNow.AddDays(10),
            ArrivalTime = DateTime.UtcNow.AddDays(10).AddHours(9),
            Status = "HK", // codigo IATA de confirmado
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency
        });
    }

    private static void AddConfirmedTransfer(AppDbContext context, int supplierId, int reservaId, decimal netCost, string currency)
    {
        context.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            PickupLocation = "Aeropuerto EZE",
            DropoffLocation = "Hotel Centro",
            PickupDateTime = DateTime.UtcNow.AddDays(10),
            Status = "Confirmado",
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency
        });
    }

    private static void AddConfirmedPackage(AppDbContext context, int supplierId, int reservaId, decimal netCost, string currency)
    {
        context.PackageBookings.Add(new PackageBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            PackageName = "Paquete Caribe",
            Destination = "Cancun",
            StartDate = DateTime.UtcNow.Date.AddDays(10),
            EndDate = DateTime.UtcNow.Date.AddDays(17),
            Nights = 7,
            Status = "Confirmado",
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency
        });
    }

    private static void AddConfirmedAssistance(AppDbContext context, int supplierId, int reservaId, decimal netCost, string currency)
    {
        context.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            PlanType = "Plan 60",
            ValidFrom = DateTime.UtcNow.Date.AddDays(10),
            ValidTo = DateTime.UtcNow.Date.AddDays(17),
            Status = "Confirmado",
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency
        });
    }

    // Servicio GENERICO confirmado. ServiceType NO debe ser "Vuelo" (eso dispararia el mapeo de codigo IATA).
    private static void AddConfirmedGeneric(AppDbContext context, int supplierId, int reservaId, decimal netCost, string currency)
    {
        context.Servicios.Add(new ServicioReserva
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            ServiceType = "Excursion",
            Description = "Excursion confirmada",
            Status = "Confirmado",
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency
        });
    }

    // Saldo unico (2026-06-30): el invariante caja<->proyeccion vive ahora en CashClosingBalance (el saldo de
    // SOLO caja), NO en ClosingBalance (que paso a ser el saldo economico = caja + circuito). En estos tests no
    // hay cancelaciones (circuito vacio), asi que ambos coinciden, pero comparamos contra el campo correcto.
    private static decimal CashClosing(SupplierAccountStatementDto dto, string currency)
        => dto.Currencies.Single(b => b.Currency == currency).CashClosingBalance;

    private static decimal PersistedBalance(AppDbContext context, int supplierId, string currency)
        => context.SupplierBalanceByCurrency.Single(r => r.SupplierId == supplierId && r.Currency == currency).Balance;

    // ===================== INVARIANTE RUNTIME: cierre extracto == Balance proyeccion =====================

    [Fact]
    public async Task Statement_ClosingPerCurrency_EqualsPersistedProjectionBalance()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);

        var reservaA = await AddReservaAsync(context, "F-A", EstadoReserva.Confirmed);
        var reservaB = await AddReservaAsync(context, "F-B", EstadoReserva.Traveling);

        // MT-1: sembramos los 6 tipos de servicio (no solo hotel), para que una divergencia entre walkers en
        // cualquiera de los 5 restantes NO pase verde. Repartidos en 2 reservas y 2 monedas.
        //
        // Compras ARS: hotel 1000 + traslado 500 + generico 300 = 1800.
        // Compras USD: vuelo 200 + paquete 700 + asistencia 100 = 1000.
        AddConfirmedHotel(context, supplier.Id, reservaA.Id, netCost: 1000m, currency: "ARS");
        AddConfirmedTransfer(context, supplier.Id, reservaA.Id, netCost: 500m, currency: "ARS");
        AddConfirmedGeneric(context, supplier.Id, reservaB.Id, netCost: 300m, currency: "ARS");
        AddConfirmedFlight(context, supplier.Id, reservaA.Id, netCost: 200m, currency: "USD");
        AddConfirmedPackage(context, supplier.Id, reservaB.Id, netCost: 700m, currency: "USD");
        AddConfirmedAssistance(context, supplier.Id, reservaA.Id, netCost: 100m, currency: "USD");

        context.SupplierPayments.AddRange(
            // Pago normal ARS imputado a A.
            new SupplierPayment { SupplierId = supplier.Id, ReservaId = reservaA.Id, Amount = 400m, Currency = "ARS", Method = "Transferencia" },
            // Pago normal USD imputado a A.
            new SupplierPayment { SupplierId = supplier.Id, ReservaId = reservaA.Id, Amount = 50m, Currency = "USD", Method = "Transferencia" },
            // Pago CRUZADO: salieron 60 USD de caja, imputados a la deuda ARS como 600 (no crea linea USD).
            new SupplierPayment
            {
                SupplierId = supplier.Id, ReservaId = reservaB.Id,
                Amount = 60m, Currency = "USD",
                ImputedCurrency = "ARS", ImputedAmount = 600m, Method = "Transferencia"
            },
            // Anticipo a cuenta SIN reserva (saldo a favor en ARS).
            new SupplierPayment { SupplierId = supplier.Id, ReservaId = null, Amount = 80m, Currency = "ARS", Method = "Transferencia" });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);

        // Materializamos la proyeccion (escalar + tabla hija) igual que un write path real.
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        // ARS: 1800 comprado - (400 + 600 cruzado + 80 anticipo) = 1800 - 1080 = 720.
        // USD: 1000 comprado - 50 pagado = 950 (el pago cruzado NO descuenta USD: imputa a ARS).
        Assert.True(statement.AmountsVisible);
        Assert.Equal(720m, CashClosing(statement, "ARS"));
        Assert.Equal(950m, CashClosing(statement, "USD"));

        // El nucleo del invariante: cierre de CAJA del extracto == Balance persistido, por moneda, con los 6 tipos.
        Assert.Equal(PersistedBalance(context, supplier.Id, "ARS"), CashClosing(statement, "ARS"));
        Assert.Equal(PersistedBalance(context, supplier.Id, "USD"), CashClosing(statement, "USD"));

        // Sin cancelaciones (circuito vacio), el saldo MOSTRADO (economico) coincide con el de caja.
        var arsBlock = statement.Currencies.Single(b => b.Currency == "ARS");
        var usdBlock = statement.Currencies.Single(b => b.Currency == "USD");
        Assert.Equal(720m, arsBlock.ClosingBalance);
        Assert.Equal(950m, usdBlock.ClosingBalance);
    }

    // ===================== CommissionOnly: las compras NO entran como cargo =====================

    [Fact]
    public async Task Statement_CommissionOnlySupplier_PurchasesDoNotCount_OnlyPaymentsAsCredit()
    {
        await using var context = CreateContext();
        // Intermediacion: el operador factura directo al cliente, la agencia NO compra -> sin deuda de compra.
        var supplier = await AddSupplierAsync(context, SupplierInvoicingMode.CommissionOnly);
        var reserva = await AddReservaAsync(context, "F-COMM", EstadoReserva.Confirmed);

        // Servicio confirmado con costo: NO debe aparecer como cargo (intermediacion).
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        // Pero si hubiera un pago, queda como saldo a favor (no se oculta).
        context.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 100m, Currency = "ARS", Method = "T" });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var block = Assert.Single(statement.Currencies);

        // Ningun cargo (compra), solo el abono (pago) -> cierre -100 (saldo a favor), igual a la proyeccion.
        Assert.DoesNotContain(block.Lines, l => l.Kind == SupplierAccountStatementLineKinds.Purchase);
        Assert.Equal(-100m, block.CashClosingBalance);
        Assert.Equal(PersistedBalance(context, supplier.Id, "ARS"), block.CashClosingBalance);
        // Sin circuito, el saldo mostrado coincide con el de caja.
        Assert.Equal(-100m, block.ClosingBalance);
    }

    // ===================== Pago anulado (soft-deleted) no abona =====================

    [Fact]
    public async Task Statement_SoftDeletedPayment_DoesNotCredit()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F-DEL", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 400m, Currency = "ARS", Method = "T",
            IsDeleted = true, DeletedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var block = Assert.Single(statement.Currencies);

        // El pago anulado no aparece y el cierre es la compra completa (1000), igual a la proyeccion.
        Assert.DoesNotContain(block.Lines, l => l.Kind == SupplierAccountStatementLineKinds.Payment);
        Assert.Equal(1000m, block.CashClosingBalance);
        Assert.Equal(PersistedBalance(context, supplier.Id, "ARS"), block.CashClosingBalance);
        // Sin circuito, el saldo mostrado coincide con el de caja.
        Assert.Equal(1000m, block.ClosingBalance);
    }

    // ===================== Masking: sin see_cost, montos en 0 y AmountsVisible false =====================

    [Fact]
    public async Task Statement_WithoutSeeCost_MasksAmounts_KeepsStructure()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F-MASK", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        context.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 300m, Currency = "ARS", Method = "T" });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: false);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);

        Assert.False(statement.AmountsVisible);
        var block = Assert.Single(statement.Currencies);
        // Estructura visible: dos movimientos (compra + pago), montos en 0.
        Assert.Equal(2, block.Lines.Count);
        Assert.All(block.Lines, l =>
        {
            Assert.Equal(0m, l.Charge);
            Assert.Equal(0m, l.Credit);
            Assert.Equal(0m, l.RunningBalance);
        });
        Assert.Equal(0m, block.ClosingBalance);
        Assert.Equal(0m, block.CashClosingBalance);
    }

    [Fact]
    public async Task Statement_SupplierNotFound_Throws()
    {
        await using var context = CreateContext();
        var service = CreateServiceForUser(context, canSeeCost: true);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetSupplierAccountStatementAsync(9999, CancellationToken.None));
    }

    // ===================== SEC-1 / MT-2: datos de tesoreria del pago segun permiso =====================

    [Fact]
    public async Task Statement_WithoutSupplierPaymentsPermission_PaymentLineHidesMethodAndReference()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F-SEC", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id,
            Amount = 300m, Currency = "ARS",
            Method = "Transferencia bancaria", Reference = "OP-12345"
        });
        await context.SaveChangesAsync();

        // Caller con proveedores.view (implicito) + ve costos, pero SIN tesoreria.supplier_payments.
        var service = CreateServiceForUser(context, canSeeCost: true, canSeePaymentDetails: false);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var block = Assert.Single(statement.Currencies);

        var paymentLine = Assert.Single(block.Lines.Where(l => l.Kind == SupplierAccountStatementLineKinds.Payment));
        // Datos de tesoreria suprimidos: descripcion generica y sin referencia.
        Assert.Equal("Pago al operador", paymentLine.Description);
        Assert.Null(paymentLine.DocumentRef);
        // Pero el movimiento sigue: fecha/moneda intactas y monto visible (porque SI ve costos).
        Assert.Equal("ARS", paymentLine.Currency);
        Assert.Equal(300m, paymentLine.Credit);
    }

    [Fact]
    public async Task Statement_WithSupplierPaymentsPermission_PaymentLineShowsMethodAndReference()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F-SEC2", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, ReservaId = reserva.Id,
            Amount = 300m, Currency = "ARS",
            Method = "Transferencia bancaria", Reference = "OP-12345"
        });
        await context.SaveChangesAsync();

        // Caller CON tesoreria.supplier_payments: ve metodo (descripcion) y referencia (documento).
        var service = CreateServiceForUser(context, canSeeCost: true, canSeePaymentDetails: true);
        var statement = await service.GetSupplierAccountStatementAsync(supplier.Id, CancellationToken.None);
        var block = Assert.Single(statement.Currencies);

        var paymentLine = Assert.Single(block.Lines.Where(l => l.Kind == SupplierAccountStatementLineKinds.Payment));
        Assert.Equal("Transferencia bancaria", paymentLine.Description);
        Assert.Equal("OP-12345", paymentLine.DocumentRef);
        Assert.Equal(300m, paymentLine.Credit);
    }

    // ===================== Overview: BalancesByCurrency lee la proyeccion y enmascara =====================

    [Fact]
    public async Task Overview_BalancesByCurrency_ReadsProjection_PerCurrency()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F-OV", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 200m, currency: "USD");
        context.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = reserva.Id, Amount = 50m, Currency = "USD", Method = "T" });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        var overview = await service.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);

        // Dos lineas por moneda, leidas de la proyeccion (NO el escalar mezclado).
        Assert.Equal(2, overview.BalancesByCurrency.Count);
        var ars = overview.BalancesByCurrency.Single(b => b.Currency == "ARS");
        var usd = overview.BalancesByCurrency.Single(b => b.Currency == "USD");
        Assert.Equal(1000m, ars.Balance);
        Assert.Equal(150m, usd.Balance); // 200 - 50
    }

    [Fact]
    public async Task Overview_BalancesByCurrency_WithoutSeeCost_AmountsMasked_StructureKept()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context);
        var reserva = await AddReservaAsync(context, "F-OVM", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);

        // Releemos con un caller SIN see_cost.
        var maskedService = CreateServiceForUser(context, canSeeCost: false);
        var overview = await maskedService.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);

        // La moneda sigue visible; los montos en 0.
        var ars = Assert.Single(overview.BalancesByCurrency);
        Assert.Equal("ARS", ars.Currency);
        Assert.Equal(0m, ars.ConfirmedPurchases);
        Assert.Equal(0m, ars.TotalPaid);
        Assert.Equal(0m, ars.Balance);
    }
}
