using System;
using System.Collections.Generic;
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
/// Rediseno "Registrar pago" (2026-07-20, backend Tanda unica aprobada por el dueño, punto 5.2.3 del analisis
/// <c>docs/architecture/2026-07-20-analisis-cuenta-proveedor-vs-erps.md</c>): el POST de alta de un pago al
/// proveedor ahora devuelve, ademas del PublicId, A DONDE IMPACTO la plata (<c>SupplierPaymentImpactDto</c>),
/// para que la pantalla pueda mostrar un cartel de exito como "Bajo la deuda de la reserva 1051 en $45.000,
/// quedan $12.000 pendientes" en vez de solo confirmar que se guardo.
///
/// <para><b>Los 3 caminos que exige la spec</b>: imputado a una reserva (sin servicio puntual), imputado a
/// una reserva + servicio concreto, y "a cuenta" (anticipo sin reserva). Mas un test de masking see_cost
/// (el saldo restante es COSTO, se enmascara; la identidad de la reserva/servicio NO).</para>
/// </summary>
public class SupplierPaymentImpactTests
{
    private const string SeeCostPermission = Permissions.CobranzasSeeCost;
    private const string SupplierPaymentsPermission = Permissions.TesoreriaSupplierPayments;

    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static SupplierService CreateService(AppDbContext context, bool canSeeCost = true)
    {
        const string userId = "tesorero-test";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
            }
        };

        var permissions = canSeeCost
            ? new[] { SeeCostPermission, SupplierPaymentsPermission }
            : new[] { SupplierPaymentsPermission };
        var resolverMock = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> set = new HashSet<string>(permissions);
        resolverMock.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(set);

        return new SupplierService(
            context,
            auditService: null,
            httpContextAccessor: accessor,
            logger: null,
            permissionResolver: resolverMock.Object);
    }

    private static async Task<Supplier> AddSupplierAsync(AppDbContext context, string name)
    {
        var supplier = new Supplier { Name = name };
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
        AppDbContext context, int supplierId, int reservaId, decimal netCost, string hotelName = "Hotel", string city = "Ciudad")
    {
        var hotel = new HotelBooking
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            HotelName = hotelName,
            City = city,
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2,
            Status = "Confirmado",
            NetCost = netCost,
            SalePrice = netCost * 1.5m
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();
        return hotel;
    }

    [Fact]
    public async Task PaymentImputedToReserva_WithoutService_ReturnsReservaAndRemainingBalance()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-100");
        await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        var request = new SupplierPaymentRequest(
            Amount: 400m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false);

        var paymentPublicId = await service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None);
        var impact = await service.GetSupplierPaymentImpactAsync(supplier.Id, paymentPublicId, CancellationToken.None);

        Assert.True(impact.WasImputedToReserva);
        Assert.Equal("F-100", impact.NumeroReserva);
        Assert.Equal(reserva.PublicId, impact.ReservaPublicId);
        Assert.Null(impact.ServicioDescripcion);
        Assert.Equal("ARS", impact.Currency);
        // Deuda 1000 - pago 400 = 600 pendientes. Recalculado post-persistencia, no restado a mano.
        Assert.Equal(600m, impact.RemainingBalance);
        Assert.True(impact.AmountsVisible);
    }

    [Fact]
    public async Task PaymentImputedToReserva_FullyPaid_RemainingBalanceIsZero()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-101");
        await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        var request = new SupplierPaymentRequest(
            Amount: 1000m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false);

        var paymentPublicId = await service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None);
        var impact = await service.GetSupplierPaymentImpactAsync(supplier.Id, paymentPublicId, CancellationToken.None);

        Assert.True(impact.WasImputedToReserva);
        Assert.Equal(0m, impact.RemainingBalance);
    }

    [Fact]
    public async Task PaymentImputedToReservaAndService_ReturnsServiceDescription()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-102");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, hotelName: "Hotel Bariloche", city: "Bariloche");

        var service = CreateService(context);
        var request = new SupplierPaymentRequest(
            Amount: 1000m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false,
            ServiceRecordKind: ServicePaymentRecordKinds.Hotel, ServicePublicId: hotel.PublicId.ToString());

        var paymentPublicId = await service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None);
        var impact = await service.GetSupplierPaymentImpactAsync(supplier.Id, paymentPublicId, CancellationToken.None);

        Assert.True(impact.WasImputedToReserva);
        Assert.Equal("F-102", impact.NumeroReserva);
        Assert.Equal("Hotel Bariloche (Bariloche)", impact.ServicioDescripcion);
        Assert.Equal(0m, impact.RemainingBalance);
    }

    [Fact]
    public async Task PaymentAsAdvanceToAccount_WithoutReserva_ReturnsWasImputedFalse()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");

        var service = CreateService(context);
        var request = new SupplierPaymentRequest(
            Amount: 2000m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true);

        var paymentPublicId = await service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None);
        var impact = await service.GetSupplierPaymentImpactAsync(supplier.Id, paymentPublicId, CancellationToken.None);

        Assert.False(impact.WasImputedToReserva);
        Assert.Null(impact.ReservaPublicId);
        Assert.Null(impact.NumeroReserva);
        Assert.Null(impact.ServicioDescripcion);
        Assert.Equal("ARS", impact.Currency);
        // Sin reserva no hay deuda-por-reserva que recalcular: el saldo restante queda en 0 por definicion
        // (no aplica), no porque este enmascarado.
        Assert.Equal(0m, impact.RemainingBalance);
    }

    [Fact]
    public async Task WithoutSeeCost_RemainingBalanceMasked_ButReservaIdentityStaysVisible()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-103");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, hotelName: "Hotel Iguazu", city: "Iguazu");

        // El pago lo registra un tesorero con permiso completo (para que el seed exista con datos reales).
        var writer = CreateService(context, canSeeCost: true);
        var request = new SupplierPaymentRequest(
            Amount: 400m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false,
            ServiceRecordKind: ServicePaymentRecordKinds.Hotel, ServicePublicId: hotel.PublicId.ToString());
        var paymentPublicId = await writer.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None);

        // El impacto lo lee alguien con permiso de tesoreria (puede registrar pagos) pero SIN cobranzas.see_cost.
        var reader = CreateService(context, canSeeCost: false);
        var impact = await reader.GetSupplierPaymentImpactAsync(supplier.Id, paymentPublicId, CancellationToken.None);

        Assert.False(impact.AmountsVisible);
        Assert.Equal(0m, impact.RemainingBalance);

        // La identidad de la reserva/servicio NO es un monto de costo: se sigue mostrando igual que el resto
        // de la cuenta del proveedor (mismo criterio que SupplierDebtReservaLineDto).
        Assert.True(impact.WasImputedToReserva);
        Assert.Equal("F-103", impact.NumeroReserva);
        Assert.Equal("Hotel Iguazu (Iguazu)", impact.ServicioDescripcion);
    }

    // B1 del review (2026-07-21): el impacto se lee DESPUES de que el pago quedo persistido.
    // Si esa lectura falla, el alta NO puede reportarse como error ("volve a intentar"
    // duplicaria la plata: no hay clave de idempotencia). El controller degrada a Impact=null.
    [Fact]
    public async Task AddSupplierPayment_WhenImpactReadFails_StillReturnsOkWithNullImpact()
    {
        var paymentPublicId = Guid.NewGuid();

        var supplierService = new Mock<ISupplierService>();
        supplierService
            .Setup(s => s.AddSupplierPaymentAsync(It.IsAny<int>(), It.IsAny<SupplierPaymentRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(paymentPublicId);
        supplierService
            .Setup(s => s.GetSupplierPaymentImpactAsync(It.IsAny<int>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("falla simulada del armado del cartel"));

        var resolver = new Mock<IEntityReferenceResolver>();
        resolver
            .Setup(r => r.ResolveRequiredIdAsync<Supplier>(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var controller = new TravelApi.Controllers.SuppliersController(
            supplierService.Object,
            resolver.Object,
            new Mock<ISupplierCreditService>().Object,
            new Mock<IOperatorRefundReadModelService>().Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TravelApi.Controllers.SuppliersController>.Instance);

        var request = new SupplierPaymentRequest(
            Amount: 100m, Method: "Transferencia", Reference: null, Notes: null,
            ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: true,
            ServiceRecordKind: null, ServicePublicId: null);

        var result = await controller.AddSupplierPayment("5", request, CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result);
        var body = ok.Value!;
        var impactProp = body.GetType().GetProperty("Impact");
        Assert.NotNull(impactProp);
        Assert.Null(impactProp!.GetValue(body));
        var idProp = body.GetType().GetProperty("PaymentPublicId");
        Assert.Equal(paymentPublicId, idProp!.GetValue(body));
    }
}
