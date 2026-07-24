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
/// FIX #16 (Tanda 3 del barrido de PROD, 2026-07-23): antes de este fix,
/// <c>GetReservaSupplierPaymentStatusAsync</c> solo miraba pagos imputados a UN servicio puntual
/// (<c>SupplierPayment.ServicePublicId != null</c>). Un pago registrado a NIVEL RESERVA (sin imputar a
/// ningun servicio) nunca entraba al calculo — el cartel decia "Operador impago" aunque el pago cubriera
/// el costo entero. Estos tests fijan el contrato nuevo: <c>AttributeReservaLevelPaymentsToServicesAsync</c>
/// reparte esos pagos FIFO por moneda+operador entre los servicios de la reserva.
/// </summary>
public class SupplierReservaLevelPaymentAttributionTests
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
        const string userId = "tesorero-test";
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var grantedPermissions = new HashSet<string>
        {
            Permissions.TesoreriaSupplierPayments,
            Permissions.CobranzasSeeCost
        };
        var permissions = new Mock<IUserPermissionResolver>();
        permissions
            .Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlySet<string>)grantedPermissions);

        return new SupplierService(context, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: permissions.Object);
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

    private static async Task<HotelBooking> AddHotelAsync(
        AppDbContext context, int supplierId, int reservaId, decimal netCost, string currency, DateTime createdAt)
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
            CreatedAt = createdAt,
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();
        return hotel;
    }

    /// <summary>Pago al operador sin imputar a ningun servicio puntual (ServicePublicId/ServiceRecordKind null).</summary>
    private static async Task AddReservaLevelPaymentAsync(
        AppDbContext context, int supplierId, int reservaId, decimal amount, string currency)
    {
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplierId,
            ReservaId = reservaId,
            ServiceRecordKind = null,
            ServicePublicId = null,
            Amount = amount,
            Currency = currency,
            Method = "Transfer",
            PaidAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
    }

    private static ServiceSupplierPaymentStatusDto FindLine(ReservaSupplierPaymentStatusDto dto, Guid servicePublicId)
        => dto.Services.Single(s => s.ServicePublicId == servicePublicId);

    [Fact]
    public async Task ReservaLevelPayment_CoveringServiceInFull_ReportsAsPaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-FIX16-001");
        var hotel = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS", createdAt: DateTime.UtcNow);
        await AddReservaLevelPaymentAsync(context, supplier.Id, reserva.Id, amount: 1000m, currency: "ARS");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
        Assert.Equal(1000m, line.PaidToOperator);
        Assert.Equal(0m, line.OutstandingToOperator);
    }

    [Fact]
    public async Task ReservaLevelPayment_CoveringServicePartially_ReportsAsPartial()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-FIX16-002");
        var hotel = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS", createdAt: DateTime.UtcNow);
        await AddReservaLevelPaymentAsync(context, supplier.Id, reserva.Id, amount: 400m, currency: "ARS");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Partial, line.Status);
        Assert.Equal(400m, line.PaidToOperator);
        Assert.Equal(600m, line.OutstandingToOperator);
    }

    /// <summary>
    /// Un pago a nivel reserva en USD NO debe cubrir un servicio en ARS del mismo operador: el reparto es
    /// SIEMPRE por (operador, moneda), nunca cruza monedas sin una conversion explicita.
    /// </summary>
    [Fact]
    public async Task ReservaLevelPayment_DoesNotCrossCurrencies()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-FIX16-003");
        var hotelArs = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS", createdAt: DateTime.UtcNow);
        await AddReservaLevelPaymentAsync(context, supplier.Id, reserva.Id, amount: 500m, currency: "USD");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, hotelArs.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
        Assert.Equal(0m, line.PaidToOperator);
        Assert.Equal(1000m, line.OutstandingToOperator);
    }

    /// <summary>
    /// Con dos servicios del mismo operador y moneda, el reparto FIFO cubre primero el mas antiguo
    /// (mismo criterio cronologico que ya usa el reparto de saldo a favor).
    /// </summary>
    [Fact]
    public async Task ReservaLevelPayment_WithTwoServices_CoversOldestFirst()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-FIX16-004");
        var older = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 300m, currency: "ARS",
            createdAt: DateTime.UtcNow.AddDays(-2));
        var newer = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 300m, currency: "ARS",
            createdAt: DateTime.UtcNow.AddDays(-1));
        await AddReservaLevelPaymentAsync(context, supplier.Id, reserva.Id, amount: 300m, currency: "ARS");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, FindLine(dto, older.PublicId).Status);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, FindLine(dto, newer.PublicId).Status);
    }

    /// <summary>Sin pagos a nivel reserva, el comportamiento es identico al de siempre (no rompe nada existente).</summary>
    [Fact]
    public async Task NoReservaLevelPayments_ServiceRemainsUnpaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-FIX16-005");
        var hotel = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, currency: "ARS", createdAt: DateTime.UtcNow);

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
        Assert.Equal(0m, line.PaidToOperator);
    }
}
