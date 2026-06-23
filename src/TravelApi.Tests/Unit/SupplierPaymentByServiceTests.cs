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
/// ADR-036 punto 4c (2026-06-23): "pagado al operador POR SERVICIO". Contrato verificado:
///   - registrar un pago imputado a un servicio concreto deja a ESE servicio cubierto (status "paid") y
///     baja la deuda al operador de ese servicio;
///   - un pago parcial deja el servicio en "partial"; sin pagos queda "unpaid";
///   - editar el pago a un monto menor / eliminarlo revierte el estado del servicio (self-healing);
///   - sin cobranzas.see_cost los MONTOS se anulan pero el ESTADO (paid/partial/unpaid) sigue visible;
///   - validacion: no se puede imputar a un servicio de OTRO proveedor ni de OTRA reserva;
///   - multimoneda: un servicio en USD se compara contra el equivalente imputado en USD.
/// </summary>
public class SupplierPaymentByServiceTests
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
        var accessor = BuildHttpContextAccessor(userId);
        var permissions = canSeeCost
            ? new[] { SeeCostPermission, SupplierPaymentsPermission }
            : new[] { SupplierPaymentsPermission };
        var resolver = BuildResolver(userId, permissions);

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

    private static async Task<Supplier> AddSupplierAsync(AppDbContext context, string name)
    {
        var supplier = new Supplier { Name = name };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        return supplier;
    }

    private static async Task<Reserva> AddReservaAsync(AppDbContext context, string numero, string status = EstadoReserva.Confirmed)
    {
        var reserva = new Reserva { NumeroReserva = numero, Name = "Reserva " + numero, Status = status };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    // Hotel "Confirmado" (cuenta como deuda viva) con costo/moneda dados. Devuelve la entidad para tomar PublicId.
    private static async Task<HotelBooking> AddConfirmedHotelAsync(
        AppDbContext context, int supplierId, int reservaId, decimal netCost, string? currency = null)
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
            Currency = currency
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();
        return hotel;
    }

    private static SupplierPaymentRequest PaymentToService(
        decimal amount, Reserva reserva, string recordKind, Guid servicePublicId, string? currency = null)
        => new(
            Amount: amount,
            Method: "Transfer",
            Reference: null,
            Notes: null,
            ReservaId: reserva.PublicId.ToString(),
            ServicioReservaId: null,
            IsAdvanceToAccount: false,
            ServiceRecordKind: recordKind,
            ServicePublicId: servicePublicId.ToString(),
            Currency: currency);

    private static ServiceSupplierPaymentStatusDto ServiceLine(ReservaSupplierPaymentStatusDto dto, Guid servicePublicId)
        => Assert.Single(dto.Services.Where(s => s.ServicePublicId == servicePublicId));

    // ============================= registrar pago por servicio =============================

    [Fact]
    public async Task FullPaymentToService_MarksServicePaid_AndPersistsServiceLink()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-001");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        var paymentPublicId = await service.AddSupplierPaymentAsync(
            supplier.Id, PaymentToService(1000m, reserva, ServicePaymentRecordKinds.Hotel, hotel.PublicId), CancellationToken.None);

        // El pago quedo imputado al servicio concreto (recordKind + publicId).
        var payment = await context.SupplierPayments.SingleAsync(p => p.PublicId == paymentPublicId);
        Assert.Equal(ServicePaymentRecordKinds.Hotel, payment.ServiceRecordKind);
        Assert.Equal(hotel.PublicId, payment.ServicePublicId);
        Assert.Equal(reserva.Id, payment.ReservaId);

        // El estado del servicio quedo "paid" y el saldo al operador en 0.
        var statusDto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
        Assert.Equal(1000m, line.NetCost);
        Assert.Equal(1000m, line.PaidToOperator);
        Assert.Equal(0m, line.OutstandingToOperator);
    }

    [Fact]
    public async Task PartialPaymentToService_MarksServicePartial_AndReducesOperatorDebt()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-002");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        await service.AddSupplierPaymentAsync(
            supplier.Id, PaymentToService(400m, reserva, ServicePaymentRecordKinds.Hotel, hotel.PublicId), CancellationToken.None);

        var statusDto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Partial, line.Status);
        Assert.Equal(400m, line.PaidToOperator);
        Assert.Equal(600m, line.OutstandingToOperator);
    }

    [Fact]
    public async Task NoPaymentToService_ServiceIsUnpaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-003");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        var statusDto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
        Assert.Equal(0m, line.PaidToOperator);
        Assert.Equal(1000m, line.OutstandingToOperator);
    }

    // ============================= editar / eliminar revierte =============================

    [Fact]
    public async Task EditPaymentDownToPartial_RevertsServiceToPartial()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-004");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        var paymentPublicId = await service.AddSupplierPaymentAsync(
            supplier.Id, PaymentToService(1000m, reserva, ServicePaymentRecordKinds.Hotel, hotel.PublicId), CancellationToken.None);
        var paymentId = (await context.SupplierPayments.SingleAsync(p => p.PublicId == paymentPublicId)).Id;

        // Bajar el pago a 300: el servicio vuelve a "partial".
        await service.UpdateSupplierPaymentAsync(
            supplier.Id, paymentId, PaymentToService(300m, reserva, ServicePaymentRecordKinds.Hotel, hotel.PublicId), CancellationToken.None);

        var statusDto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Partial, line.Status);
        Assert.Equal(300m, line.PaidToOperator);
    }

    [Fact]
    public async Task DeletePayment_RevertsServiceToUnpaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-005");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        var paymentPublicId = await service.AddSupplierPaymentAsync(
            supplier.Id, PaymentToService(1000m, reserva, ServicePaymentRecordKinds.Hotel, hotel.PublicId), CancellationToken.None);
        var paymentId = (await context.SupplierPayments.SingleAsync(p => p.PublicId == paymentPublicId)).Id;

        await service.DeleteSupplierPaymentAsync(supplier.Id, paymentId, CancellationToken.None);

        var statusDto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
        Assert.Equal(0m, line.PaidToOperator);
        Assert.Equal(1000m, line.OutstandingToOperator);
    }

    // ============================= masking see_cost =============================

    [Fact]
    public async Task WithoutSeeCost_StatusVisible_AmountsMasked()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-006");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        // El pago lo registra alguien que SI ve costos (para que el seed exista), pero la consulta del
        // estado la hace alguien SIN see_cost.
        var writer = CreateService(context, canSeeCost: true);
        await writer.AddSupplierPaymentAsync(
            supplier.Id, PaymentToService(1000m, reserva, ServicePaymentRecordKinds.Hotel, hotel.PublicId), CancellationToken.None);

        var reader = CreateService(context, canSeeCost: false);
        var statusDto = await reader.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);

        Assert.False(statusDto.AmountsVisible);
        // El estado se ve igual (decision ADR-036 P4=B).
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
        // Los montos se anulan.
        Assert.Equal(0m, line.NetCost);
        Assert.Equal(0m, line.PaidToOperator);
        Assert.Equal(0m, line.OutstandingToOperator);
    }

    // ============================= validaciones de pertenencia =============================

    [Fact]
    public async Task PaymentToServiceOfAnotherSupplier_IsRejected()
    {
        await using var context = CreateContext();
        var supplierA = await AddSupplierAsync(context, "Mayorista A");
        var supplierB = await AddSupplierAsync(context, "Mayorista B");
        var reserva = await AddReservaAsync(context, "F-007");
        // El hotel es del proveedor B; intentamos pagarlo como si fuera del A.
        var hotelOfB = await AddConfirmedHotelAsync(context, supplierB.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddSupplierPaymentAsync(
                supplierA.Id, PaymentToService(500m, reserva, ServicePaymentRecordKinds.Hotel, hotelOfB.PublicId), CancellationToken.None));
    }

    [Fact]
    public async Task PaymentToServiceOfAnotherReserva_IsRejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reservaA = await AddReservaAsync(context, "F-008A");
        var reservaB = await AddReservaAsync(context, "F-008B");
        var hotelInB = await AddConfirmedHotelAsync(context, supplier.Id, reservaB.Id, netCost: 1000m);
        // Para que reservaA tenga deuda del proveedor (y no falle por "reserva sin servicios del proveedor")
        await AddConfirmedHotelAsync(context, supplier.Id, reservaA.Id, netCost: 500m);

        var service = CreateService(context);
        // Imputamos a reservaA pero apuntando a un servicio que vive en reservaB.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AddSupplierPaymentAsync(
                supplier.Id, PaymentToService(300m, reservaA, ServicePaymentRecordKinds.Hotel, hotelInB.PublicId), CancellationToken.None));
    }

    [Fact]
    public async Task ServiceWithoutReserva_IsRejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-009");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        // Pago con ServicePublicId pero SIN ReservaId -> ArgumentException (un servicio vive dentro de una reserva).
        var badRequest = new SupplierPaymentRequest(
            Amount: 500m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: null, ServicioReservaId: null, IsAdvanceToAccount: false,
            ServiceRecordKind: ServicePaymentRecordKinds.Hotel, ServicePublicId: hotel.PublicId.ToString());

        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddSupplierPaymentAsync(supplier.Id, badRequest, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidRecordKind_IsRejected()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-010");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var badRequest = new SupplierPaymentRequest(
            Amount: 500m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null, IsAdvanceToAccount: false,
            ServiceRecordKind: "no-existe", ServicePublicId: hotel.PublicId.ToString());

        var service = CreateService(context);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.AddSupplierPaymentAsync(supplier.Id, badRequest, CancellationToken.None));
    }

    // ============================= multimoneda =============================

    [Fact]
    public async Task UsdServicePaidInUsd_MarksServicePaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista USD");
        var reserva = await AddReservaAsync(context, "F-USD");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 300m, currency: "USD");

        var service = CreateService(context);
        await service.AddSupplierPaymentAsync(
            supplier.Id,
            PaymentToService(300m, reserva, ServicePaymentRecordKinds.Hotel, hotel.PublicId, currency: "USD"),
            CancellationToken.None);

        var statusDto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);
        Assert.Equal("USD", line.Currency);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
        Assert.Equal(300m, line.PaidToOperator);
    }

    // ============================= no rompe el camino sin servicio =============================

    [Fact]
    public async Task PaymentToReservaWithoutService_LeavesServiceLinkNull()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-011");
        var hotel = await AddConfirmedHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m);

        var service = CreateService(context);
        // Pago a la reserva SIN imputar a un servicio puntual (comportamiento previo).
        var request = new SupplierPaymentRequest(
            Amount: 400m, Method: "Transfer", Reference: null, Notes: null,
            ReservaId: reserva.PublicId.ToString(), ServicioReservaId: null);
        var paymentPublicId = await service.AddSupplierPaymentAsync(supplier.Id, request, CancellationToken.None);

        var payment = await context.SupplierPayments.SingleAsync(p => p.PublicId == paymentPublicId);
        Assert.Null(payment.ServiceRecordKind);
        Assert.Null(payment.ServicePublicId);

        // El servicio NO queda imputado: sigue "unpaid" en la vista por servicio (el pago fue a nivel reserva).
        var statusDto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);
        var line = ServiceLine(statusDto, hotel.PublicId);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
    }
}
