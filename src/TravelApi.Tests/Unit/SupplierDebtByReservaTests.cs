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
/// Auditoria ERP hallazgo #4 (2026-06-12): deuda al proveedor DESGLOSADA POR EXPEDIENTE (reserva).
/// El dueño concilia con los mayoristas por expediente, no solo por el total global. Contrato verificado:
///   - deuda por reserva = compras CONFIRMADAS de ESE proveedor en ESA reserva menos pagos imputados;
///   - servicio cancelado / reserva no viva NO suma a la deuda;
///   - pago sin reserva (anticipo) va al bucket "a cuenta", no a una reserva;
///   - INVARIANTE: suma(saldos por reserva) + suma(anticipos) == total global por moneda (la misma cuenta
///     corriente global que ya calculaba el sistema). Es la prueba de no-divergencia (una sola fuente);
///   - multimoneda: un proveedor con costos en ARS y USD da lineas separadas por moneda;
///   - masking see_cost: sin permiso la estructura queda visible pero los montos en 0; con permiso, reales.
/// </summary>
public class SupplierDebtByReservaTests
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

    private static SupplierService CreateServiceForUser(AppDbContext context, bool canSeeCost)
    {
        const string userId = "vendedor-test";
        var accessor = BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId);

        return new SupplierService(
            context,
            auditService: null,
            httpContextAccessor: accessor,
            logger: null,
            permissionResolver: resolver);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };
    }

    private static IUserPermissionResolver BuildResolver(string userId, params string[] permissions)
    {
        var mock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        mock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);
        return mock.Object;
    }

    private static async Task<Reserva> AddReservaAsync(AppDbContext context, string numero, string status)
    {
        var reserva = new Reserva
        {
            NumeroReserva = numero,
            Name = "Reserva " + numero,
            Status = status
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    // Hotel "Confirmado" (cuenta para la deuda por la regla oficial) en una reserva, con costo y moneda dados.
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

    private static SupplierDebtCurrencyLineDto CurrencyLine(SupplierDebtReservaLineDto line, string currency)
        => Assert.Single(line.Currencies.Where(c => c.Currency == currency));

    private static decimal GlobalAmount(SupplierDebtByReservaDto dto, string currency)
        => dto.GlobalTotals.Single(t => t.Currency == currency).Amount;

    // ============================= deuda por reserva basica =============================

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_ConfirmedServiceMinusImputedPayment_PerReservaBalance()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var reserva = await AddReservaAsync(context, "F-001", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m);
        // Pago de 300 imputado a esa reserva.
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            ReservaId = reserva.Id,
            Amount = 300m,
            Method = "Transfer"
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        var reservaLine = Assert.Single(dto.Reservas);
        Assert.Equal("F-001", reservaLine.NumeroReserva);
        var ars = CurrencyLine(reservaLine, "ARS");
        Assert.Equal(1000m, ars.ConfirmedPurchases);
        Assert.Equal(300m, ars.TotalPaid);
        Assert.Equal(700m, ars.Balance); // 1000 - 300

        Assert.Empty(dto.AdvancesToAccount);
        Assert.Equal(700m, GlobalAmount(dto, "ARS"));
    }

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_CancelledService_DoesNotCountAsDebt()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var reserva = await AddReservaAsync(context, "F-002", EstadoReserva.Confirmed);
        // Servicio confirmado (suma) + servicio cancelado (NO suma).
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 500m);
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel cancelado",
            City = "Ciudad",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2,
            Status = "Cancelado",
            NetCost = 9999m
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        var reservaLine = Assert.Single(dto.Reservas);
        var ars = CurrencyLine(reservaLine, "ARS");
        Assert.Equal(500m, ars.ConfirmedPurchases); // el cancelado de 9999 no entra
        Assert.Equal(500m, ars.Balance);
    }

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_PaymentWithoutReserva_GoesToAdvancesBucket()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var reserva = await AddReservaAsync(context, "F-003", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m);
        // Anticipo a cuenta: pago SIN reserva.
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            ReservaId = null,
            Amount = 250m,
            Method = "Transfer"
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        // La reserva muestra su deuda SIN descontar el anticipo (el anticipo no esta imputado a ella).
        var reservaLine = Assert.Single(dto.Reservas);
        var arsReserva = CurrencyLine(reservaLine, "ARS");
        Assert.Equal(1000m, arsReserva.ConfirmedPurchases);
        Assert.Equal(0m, arsReserva.TotalPaid);
        Assert.Equal(1000m, arsReserva.Balance);

        // El anticipo aparece en el bucket a cuenta.
        var advance = Assert.Single(dto.AdvancesToAccount);
        Assert.Equal("ARS", advance.Currency);
        Assert.Equal(250m, advance.Amount);

        // Total global = 1000 (deuda) - 250 (anticipo a favor) = 750.
        Assert.Equal(750m, GlobalAmount(dto, "ARS"));
    }

    // ============================= INVARIANTE DE RECONCILIACION =============================

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_PerReservaPlusAdvances_ReconcilesWithGlobalDebt()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista conciliacion" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        // Escenario realista: 2 reservas vivas con servicios en ARS y USD, pagos imputados y un anticipo.
        var reservaA = await AddReservaAsync(context, "F-A", EstadoReserva.Confirmed);
        var reservaB = await AddReservaAsync(context, "F-B", EstadoReserva.Traveling);

        AddConfirmedHotel(context, supplier.Id, reservaA.Id, netCost: 1000m, currency: "ARS");
        AddConfirmedHotel(context, supplier.Id, reservaA.Id, netCost: 200m, currency: "USD");
        AddConfirmedHotel(context, supplier.Id, reservaB.Id, netCost: 500m, currency: "ARS");

        // Pagos imputados: 300 ARS a A, 50 USD a A, 100 ARS a B.
        context.SupplierPayments.AddRange(
            new SupplierPayment { SupplierId = supplier.Id, ReservaId = reservaA.Id, Amount = 300m, Currency = "ARS", Method = "T" },
            new SupplierPayment { SupplierId = supplier.Id, ReservaId = reservaA.Id, Amount = 50m, Currency = "USD", Method = "T" },
            new SupplierPayment { SupplierId = supplier.Id, ReservaId = reservaB.Id, Amount = 100m, Currency = "ARS", Method = "T" });
        // Anticipo a cuenta: 80 ARS sin reserva.
        context.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = null, Amount = 80m, Currency = "ARS", Method = "T" });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        // Calculamos la suma de saldos por reserva + anticipos, por moneda, y la comparamos con el total
        // global del DTO Y con el calculo global real del sistema.
        var reconciledByCurrency = new Dictionary<string, decimal>();
        foreach (var reserva in dto.Reservas)
        {
            foreach (var line in reserva.Currencies)
            {
                reconciledByCurrency.TryGetValue(line.Currency, out var acc);
                reconciledByCurrency[line.Currency] = acc + line.Balance;
            }
        }
        foreach (var advance in dto.AdvancesToAccount)
        {
            // El anticipo es saldo a favor de la agencia: resta a la deuda.
            reconciledByCurrency.TryGetValue(advance.Currency, out var acc);
            reconciledByCurrency[advance.Currency] = acc - advance.Amount;
        }

        // 1) suma por reserva + anticipos == GlobalTotals del propio DTO.
        foreach (var total in dto.GlobalTotals)
        {
            Assert.True(reconciledByCurrency.TryGetValue(total.Currency, out var reconciled));
            Assert.Equal(total.Amount, reconciled);
        }

        // 2) Y los GlobalTotals coinciden con el calculo global REAL (la cuenta corriente global del sistema),
        //    que es la fuente unica. ARS: (1000+500) - (300+100) = 1100; menos anticipo 80 = 1020.
        //    USD: 200 - 50 = 150.
        Assert.Equal(1020m, GlobalAmount(dto, "ARS"));
        Assert.Equal(150m, GlobalAmount(dto, "USD"));

        // 3) Reconciliacion contra SupplierDebtCalculator (la fuente unica del total global por moneda) via
        //    la tabla hija que persiste el mismo calculo. Recalculamos el balance global del sistema.
        await service.UpdateBalanceAsync(supplier.Id, CancellationToken.None);
        var persisted = await context.SupplierBalanceByCurrency
            .Where(r => r.SupplierId == supplier.Id)
            .ToListAsync();
        // El balance global persistido por moneda debe igualar el GlobalTotals del desglose.
        Assert.Equal(1020m, persisted.Single(r => r.Currency == "ARS").Balance);
        Assert.Equal(150m, persisted.Single(r => r.Currency == "USD").Balance);
    }

    // ============================= multimoneda en una sola reserva =============================

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_MultiCurrencyInOneReserva_SeparateLines()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista multimoneda" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var reserva = await AddReservaAsync(context, "F-MC", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS");
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 300m, currency: "USD");
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        var reservaLine = Assert.Single(dto.Reservas);
        Assert.Equal(2, reservaLine.Currencies.Count);
        Assert.Equal(1000m, CurrencyLine(reservaLine, "ARS").Balance);
        Assert.Equal(300m, CurrencyLine(reservaLine, "USD").Balance);
    }

    // ============================= masking see_cost =============================

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_WithoutSeeCost_MasksAmounts_KeepsStructure()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var reserva = await AddReservaAsync(context, "F-MASK", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m);
        context.SupplierPayments.Add(new SupplierPayment { SupplierId = supplier.Id, ReservaId = null, Amount = 100m, Method = "T" });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: false);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        // Estructura visible: la reserva, su numero, las monedas, el bucket de anticipos.
        var reservaLine = Assert.Single(dto.Reservas);
        Assert.Equal("F-MASK", reservaLine.NumeroReserva);
        var ars = CurrencyLine(reservaLine, "ARS");
        // Montos en 0.
        Assert.Equal(0m, ars.ConfirmedPurchases);
        Assert.Equal(0m, ars.TotalPaid);
        Assert.Equal(0m, ars.Balance);
        Assert.Equal(0m, Assert.Single(dto.AdvancesToAccount).Amount);
        Assert.All(dto.GlobalTotals, t => Assert.Equal(0m, t.Amount));
    }

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_WithSeeCost_RealAmounts()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var reserva = await AddReservaAsync(context, "F-REAL", EstadoReserva.Confirmed);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 1000m);
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        var reservaLine = Assert.Single(dto.Reservas);
        Assert.Equal(1000m, CurrencyLine(reservaLine, "ARS").ConfirmedPurchases);
    }

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_SupplierNotFound_Throws()
    {
        await using var context = CreateContext();
        var service = CreateServiceForUser(context, canSeeCost: true);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetSupplierDebtByReservaAsync(9999, CancellationToken.None));
    }

    // ============================= pago a reserva no-viva sigue conciliando =============================

    [Fact]
    public async Task GetSupplierDebtByReservaAsync_PaymentToReservaWithoutLiveServices_StillReconciles()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Mayorista" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        // Reserva Cancelled: sus servicios NO cuentan como deuda viva, pero hay un pago imputado a ella.
        // El desglose debe mostrar esa reserva con saldo NEGATIVO (sobrepago) para que reconcilie.
        var reserva = await AddReservaAsync(context, "F-DEAD", EstadoReserva.Cancelled);
        AddConfirmedHotel(context, supplier.Id, reserva.Id, netCost: 400m); // en reserva cancelada => no cuenta
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            ReservaId = reserva.Id,
            Amount = 120m,
            Method = "T"
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var dto = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        var reservaLine = Assert.Single(dto.Reservas);
        var ars = CurrencyLine(reservaLine, "ARS");
        Assert.Equal(0m, ars.ConfirmedPurchases); // servicio en reserva cancelada no suma
        Assert.Equal(120m, ars.TotalPaid);
        Assert.Equal(-120m, ars.Balance);          // sobrepago visible

        // Global = -120 (la deuda viva es 0, el pago imputado deja saldo a favor en esa reserva).
        Assert.Equal(-120m, GlobalAmount(dto, "ARS"));
    }
}
