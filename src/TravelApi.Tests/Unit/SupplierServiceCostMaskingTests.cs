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
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1b (cuenta corriente del proveedor — fix de seguridad SIN flag): regresion
/// del leak de costos/deuda. Antes GET /suppliers, /suppliers/{id}, /{id}/account,
/// /{id}/account/services y los endpoints de pagos devolvian CurrentBalance,
/// TotalPurchases/TotalPaid/Balance, NetCost por servicio y Amount por pago a cualquier
/// caller con proveedores.view (permiso que el Vendedor tiene seeded). Contrato fijado:
///   - usuario SIN cobranzas.see_cost -> montos de costo/deuda enmascarados a 0m;
///   - usuario CON cobranzas.see_cost (o Admin) -> valores reales, sin regresion;
///   - datos NO monetarios (nombres, servicios, fechas, contadores) visibles SIEMPRE
///     (decision del dueño 2026-06-05: el vendedor sigue viendo la lista y los servicios);
///   - SalePrice viaja SIEMPRE (D1: es venta, no costo);
///   - lo persistido en DB nunca se altera.
/// </summary>
public class SupplierServiceCostMaskingTests
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

    // Construye el servicio con un caller no-Admin. Si "canSeeCost" es true, el
    // resolver devuelve el permiso cobranzas.see_cost; si es false, devuelve vacio.
    private static SupplierService CreateServiceForUser(AppDbContext context, bool canSeeCost, bool isAdmin = false)
    {
        const string userId = "vendedor-test";
        var accessor = isAdmin
            ? BuildHttpContextAccessor(userId, "Admin")
            : BuildHttpContextAccessor(userId);
        var resolver = canSeeCost
            ? BuildResolver(userId, SeeCostPermission)
            : BuildResolver(userId); // sin permisos

        return new SupplierService(
            context,
            auditService: null,
            httpContextAccessor: accessor,
            logger: null,
            permissionResolver: resolver);
    }

    private static IHttpContextAccessor BuildHttpContextAccessor(string userId, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
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

    // Seed: proveedor con deuda conocida + 1 hotel CONFIRMADO (genera deuda segun la
    // regla oficial unica) + 1 pago. Valores elegidos para que el resumen real sea:
    // TotalPurchases=100, TotalPaid=40, Balance=60, ServiceCount=1, PaymentCount=1.
    private static async Task<Supplier> SeedSupplierAccountAsync(AppDbContext context)
    {
        var supplier = new Supplier { Name = "Mayorista cuenta", CurrentBalance = 500m };
        var reserva = new Reserva
        {
            NumeroReserva = "F-2026-MASK",
            Name = "Reserva masking",
            Status = EstadoReserva.Confirmed
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Maitei",
            City = "Posadas",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2,
            Status = "Confirmado", // confirmado por texto -> cuenta para la deuda
            NetCost = 100m,
            SalePrice = 160m
        });
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id,
            Amount = 40m,
            Method = "Transfer",
            Reference = "OP-123",
            PaidAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        return supplier;
    }

    // ============================= GET /api/suppliers (listado) =============================

    [Fact]
    public async Task GetSuppliersAsync_UserWithoutSeeCost_MasksCurrentBalance_KeepsIdentityData()
    {
        await using var context = CreateContext();
        await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var page = await service.GetSuppliersAsync(new SupplierListQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(0m, item.CurrentBalance);       // deuda con el proveedor: oculta
        Assert.Equal("Mayorista cuenta", item.Name); // identidad visible (decision del dueño)
        Assert.True(item.IsActive);

        // Lo persistido no se altera: solo se anula en el DTO de salida.
        var stored = await context.Suppliers.AsNoTracking().SingleAsync();
        Assert.Equal(500m, stored.CurrentBalance);
    }

    [Fact]
    public async Task GetSuppliersAsync_UserWithSeeCost_KeepsCurrentBalance()
    {
        await using var context = CreateContext();
        await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var page = await service.GetSuppliersAsync(new SupplierListQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(500m, item.CurrentBalance);
    }

    // ============================= GET /api/suppliers/{id} =============================

    [Fact]
    public async Task GetSupplierAsync_UserWithoutSeeCost_MasksCurrentBalance_DbIntact()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var result = await service.GetSupplierAsync(supplier.Id, CancellationToken.None);

        Assert.Equal(0m, result.CurrentBalance);
        Assert.Equal("Mayorista cuenta", result.Name);

        // El enmascarado muta la instancia devuelta: tiene que ser NO trackeada para
        // que un SaveChanges posterior no persista el 0. Verificamos la DB intacta.
        await context.SaveChangesAsync();
        var stored = await context.Suppliers.AsNoTracking().SingleAsync();
        Assert.Equal(500m, stored.CurrentBalance);
    }

    [Fact]
    public async Task GetSupplierAsync_UserWithSeeCost_KeepsCurrentBalance()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var result = await service.GetSupplierAsync(supplier.Id, CancellationToken.None);

        Assert.Equal(500m, result.CurrentBalance);
    }

    // ============================= GET /api/suppliers/{id}/account =============================

    [Fact]
    public async Task GetSupplierAccountOverviewAsync_UserWithoutSeeCost_MasksAllMoney_KeepsCounts()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var overview = await service.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);

        // Todos los montos del resumen son lado costo/deuda: ocultos.
        Assert.Equal(0m, overview.Supplier.CurrentBalance);
        Assert.Equal(0m, overview.Summary.TotalPurchases);
        Assert.Equal(0m, overview.Summary.TotalPaid);
        Assert.Equal(0m, overview.Summary.Balance);
        // Los contadores y la identidad NO son montos: visibles.
        Assert.Equal(1, overview.Summary.ServiceCount);
        Assert.Equal(1, overview.Summary.PaymentCount);
        Assert.Equal("Mayorista cuenta", overview.Supplier.Name);
    }

    [Fact]
    public async Task GetSupplierAccountOverviewAsync_UserWithSeeCost_KeepsMoney()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var overview = await service.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);

        Assert.Equal(500m, overview.Supplier.CurrentBalance);
        Assert.Equal(100m, overview.Summary.TotalPurchases);
        Assert.Equal(40m, overview.Summary.TotalPaid);
        Assert.Equal(60m, overview.Summary.Balance);
    }

    [Fact]
    public async Task GetSupplierAccountOverviewAsync_AdminWithoutExplicitPermission_KeepsMoney()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        // Admin sin el permiso en el resolver: el bypass por rol debe alcanzar.
        var service = CreateServiceForUser(context, canSeeCost: false, isAdmin: true);

        var overview = await service.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);

        Assert.Equal(500m, overview.Supplier.CurrentBalance);
        Assert.Equal(100m, overview.Summary.TotalPurchases);
    }

    // ============================= GET /api/suppliers/{id}/account/services =============================

    [Fact]
    public async Task GetSupplierAccountServicesAsync_UserWithoutSeeCost_MasksNetCost_KeepsSalePriceAndData()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id,
            new SupplierAccountServicesQuery(),
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(0m, item.NetCost);          // costo con el proveedor: oculto
        Assert.Equal(160m, item.SalePrice);      // la venta viaja SIEMPRE (D1)
        Assert.Equal("Hotel", item.Type);        // datos no monetarios visibles
        Assert.Contains("Hotel Maitei", item.Description);
        Assert.Equal("F-2026-MASK", item.NumeroReserva);

        // Lo persistido no se altera.
        var stored = await context.HotelBookings.AsNoTracking().SingleAsync();
        Assert.Equal(100m, stored.NetCost);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_UserWithSeeCost_KeepsNetCost()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id,
            new SupplierAccountServicesQuery(),
            CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(100m, item.NetCost);
        Assert.Equal(160m, item.SalePrice);
    }

    // ============================= GET /api/suppliers/{id}/account/payments =============================

    [Fact]
    public async Task GetSupplierAccountPaymentsAsync_UserWithoutSeeCost_MasksAmount_KeepsData()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var page = await service.GetSupplierAccountPaymentsAsync(
            supplier.Id,
            new SupplierAccountPaymentsQuery(),
            CancellationToken.None);

        var payment = Assert.Single(page.Items);
        Assert.Equal(0m, payment.Amount);         // pago al proveedor = lado costo/deuda
        Assert.Equal("Transfer", payment.Method); // datos no monetarios visibles
        Assert.Equal("OP-123", payment.Reference);

        var stored = await context.SupplierPayments.AsNoTracking().SingleAsync();
        Assert.Equal(40m, stored.Amount);
    }

    [Fact]
    public async Task GetSupplierAccountPaymentsAsync_UserWithSeeCost_KeepsAmount()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var page = await service.GetSupplierAccountPaymentsAsync(
            supplier.Id,
            new SupplierAccountPaymentsQuery(),
            CancellationToken.None);

        var payment = Assert.Single(page.Items);
        Assert.Equal(40m, payment.Amount);
    }

    [Fact]
    public async Task GetSupplierPaymentsHistoryAsync_UserWithoutSeeCost_MasksAmount()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var payments = await service.GetSupplierPaymentsHistoryAsync(supplier.Id, CancellationToken.None);

        var payment = Assert.Single(payments);
        Assert.Equal(0m, payment.Amount);
        Assert.Equal("Transfer", payment.Method);
    }

    // ============================= PUT /api/suppliers/{id} (respuesta) =============================

    [Fact]
    public async Task UpdateSupplierAsync_UserWithoutSeeCost_MasksCurrentBalanceInResponse_DbIntact()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        var incoming = new Supplier
        {
            Name = "Nombre nuevo",
            IsActive = true
        };

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        // La respuesta del PUT no debe ecoar la deuda al caller sin see_cost.
        Assert.Equal(0m, result.CurrentBalance);
        Assert.Equal("Nombre nuevo", result.Name);

        // El update real SI quedo persistido y la deuda NO se piso con el 0 enmascarado
        // (la entidad se detacha antes de anular el monto).
        await context.SaveChangesAsync();
        var stored = await context.Suppliers.AsNoTracking().SingleAsync();
        Assert.Equal("Nombre nuevo", stored.Name);
        Assert.Equal(500m, stored.CurrentBalance);
    }

    [Fact]
    public async Task UpdateSupplierAsync_UserWithSeeCost_KeepsCurrentBalanceInResponse()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var incoming = new Supplier
        {
            Name = "Nombre nuevo",
            IsActive = true
        };

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.Equal(500m, result.CurrentBalance);
    }

    // ============================= mensajes de error con deuda =============================
    // (2026-06-26) El viejo tope GLOBAL (surrogate mezclado) se ELIMINO por decision del dueño (un anticipo a
    // cuenta es prepago genuino, sin tope). El mensaje generico que NO filtra la deuda sigue vigente en el rechazo
    // que SI permanece: imputar un pago a una reserva por encima de la deuda de ese proveedor EN ESA RESERVA.
    // Estos tests fijan que ese mensaje es generico (sin montos) para todos, con y sin see_cost (ADR-017 F1b).

    [Fact]
    public async Task AddSupplierPaymentAsync_ExceedsReservaDebt_UserWithoutSeeCost_GenericMessageWithoutAmounts()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: false);

        // La reserva masking debe 100 (su hotel confirmado). Imputar 999 a ESA reserva supera su deuda -> rebota.
        var reservaPublicId = (await context.Reservas.AsNoTracking()
            .FirstAsync(r => r.NumeroReserva == "F-2026-MASK")).PublicId.ToString();
        var request = new SupplierPaymentRequest(999m, "Transfer", null, null, reservaPublicId, null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None));

        // El mensaje NO debe revelar la deuda exacta al caller sin see_cost.
        Assert.DoesNotContain("100", ex.Message);
        Assert.Contains("excede la deuda", ex.Message);
    }

    [Fact]
    public async Task AddSupplierPaymentAsync_ExceedsReservaDebt_UserWithSeeCost_StillGenericMessage()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        var service = CreateServiceForUser(context, canSeeCost: true);

        var reservaPublicId = (await context.Reservas.AsNoTracking()
            .FirstAsync(r => r.NumeroReserva == "F-2026-MASK")).PublicId.ToString();
        var request = new SupplierPaymentRequest(999m, "Transfer", null, null, reservaPublicId, null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None));

        // ADR-017 F1b: el mensaje de error es generico para TODOS (incluso con see_cost),
        // porque SuppliersController lo traduce a un mensaje HTTP generico — un mensaje con
        // la deuda exacta seria codigo muerto que nunca llega al cliente. El masking real de
        // montos vive en los DTOs de la cuenta del proveedor, no en los mensajes de error.
        Assert.DoesNotContain("100", ex.Message);
        Assert.Contains("excede la deuda", ex.Message);
    }

    // ============================= fail-closed =============================

    [Fact]
    public async Task GetSupplierAccountOverviewAsync_WithoutResolverNorAccessor_FailClosed_MasksMoney()
    {
        await using var context = CreateContext();
        var supplier = await SeedSupplierAccountAsync(context);
        // Instancia "legacy" sin resolver ni accessor (el ctor de 1 arg): no hay forma
        // de saber quien llama -> fail-closed, los montos se ocultan SIEMPRE.
        var service = new SupplierService(context);

        var overview = await service.GetSupplierAccountOverviewAsync(supplier.Id, CancellationToken.None);

        Assert.Equal(0m, overview.Supplier.CurrentBalance);
        Assert.Equal(0m, overview.Summary.TotalPurchases);
        Assert.Equal(0m, overview.Summary.TotalPaid);
        Assert.Equal(0m, overview.Summary.Balance);
        Assert.Equal(1, overview.Summary.ServiceCount); // los contadores viajan igual
    }
}
