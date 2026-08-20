using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Obra "la ficha del operador no borra la historia" (2026-08-20, punto 3): <c>GET
/// /suppliers/{id}/timeline</c>. Cobertura de los eventos del historial del operador (compra, anulacion de
/// reserva, multa, reembolso, pago, factura), el orden (mas nuevo arriba) y el masking F-14
/// (<c>cobranzas.see_cost</c>).
/// </summary>
public class SupplierTimelineServiceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static SupplierService CreateServiceForUser(
        AppDbContext context, bool canSeeCost, bool canSeePaymentDetails = false)
    {
        const string userId = "tester";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        var granted = new HashSet<string>();
        if (canSeeCost) granted.Add(Permissions.CobranzasSeeCost);
        if (canSeePaymentDetails) granted.Add(Permissions.TesoreriaSupplierPayments);

        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = granted;
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

        return new SupplierService(context, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: resolver.Object);
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_SupplierNotFound_Throws()
    {
        await using var context = CreateContext();
        var service = CreateServiceForUser(context, canSeeCost: true);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.GetSupplierTimelineAsync(9999, CancellationToken.None));
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_PurchaseConfirmed_ShowsAmountAndReserva()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador Hoteles", IsActive = true };
        var reserva = new Reserva { NumeroReserva = "F-TL-1", Name = "Reserva", Status = EstadoReserva.Confirmed };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var purchaseDate = new DateTime(2026, 8, 7, 11, 15, 0, DateTimeKind.Utc);
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel Bariloche 3 noches", NetCost = 90000m, SalePrice = 130000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12),
            CreatedAt = purchaseDate,
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        Assert.True(timeline.AmountsVisible);
        var purchaseEvent = Assert.Single(timeline.Events, e => e.EventType == "SupplierPurchaseConfirmed");
        Assert.Equal(purchaseDate, purchaseEvent.Timestamp);
        Assert.Equal("Se compró Hotel Bariloche 3 noches: $ 90.000.", purchaseEvent.Title);
        Assert.Equal(90000m, purchaseEvent.Amount);
        Assert.Contains("F-TL-1", purchaseEvent.Details);
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_WithoutSeeCost_PurchaseEventHasNoAmount_ButStillAppears()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador Hoteles", IsActive = true };
        var reserva = new Reserva { NumeroReserva = "F-TL-2", Name = "Reserva", Status = EstadoReserva.Confirmed };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel Bariloche", NetCost = 90000m, SalePrice = 130000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12),
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: false);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        Assert.False(timeline.AmountsVisible);
        var purchaseEvent = Assert.Single(timeline.Events, e => e.EventType == "SupplierPurchaseConfirmed");
        // El evento SIGUE existiendo (F-14: se esconde el numero, no el hecho de que la compra paso).
        Assert.Equal("Se compró Hotel Bariloche.", purchaseEvent.Title);
        Assert.Null(purchaseEvent.Amount);
        Assert.Null(purchaseEvent.Currency);
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_ReservaAnnulled_ShowsActorAndMotivo()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        var reserva = new Reserva { NumeroReserva = "F-TL-3", Name = "Reserva", Status = EstadoReserva.Cancelled };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            Status = WorkflowStatuses.Cancelado, StatusBeforeCancellation = "Confirmado",
            HotelName = "Hotel", NetCost = 10000m, SalePrice = 15000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12),
        });
        var annulledAt = new DateTime(2026, 8, 19, 19, 40, 0, DateTimeKind.Utc);
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = reserva.Id, FromStatus = EstadoReserva.Confirmed, ToStatus = EstadoReserva.Cancelled,
            Direction = "Forward", ByUserId = "user-1", ByUserName = "María",
            Reason = "El pasajero se bajó del viaje", OccurredAt = annulledAt,
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        var annulledEvent = Assert.Single(timeline.Events, e => e.EventType == "ReservaAnnulled");
        Assert.Equal("María anuló la reserva.", annulledEvent.Title);
        Assert.Equal(annulledAt, annulledEvent.Timestamp);
        Assert.Contains("Motivo: El pasajero se bajó del viaje", annulledEvent.Details);
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_LiveToLiveStatusChange_IsNotTreatedAsAnnulment()
    {
        // Un cambio de estado que NO entra a Cancelled/PendingOperatorRefund (ej. InManagement -> Confirmed)
        // no es una anulacion: no debe generar el evento "anuló la reserva".
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        var reserva = new Reserva { NumeroReserva = "F-TL-4", Name = "Reserva", Status = EstadoReserva.Confirmed };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel", NetCost = 10000m, SalePrice = 15000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(12),
        });
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = reserva.Id, FromStatus = EstadoReserva.InManagement, ToStatus = EstadoReserva.Confirmed,
            Direction = "Forward", ByUserId = "user-1", ByUserName = "María", OccurredAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        Assert.DoesNotContain(timeline.Events, e => e.EventType == "ReservaAnnulled");
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_RefundReceivedAndUndone_ShowsBothEvents()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var refund = new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 30000m, Currency = "ARS", Method = "Transfer",
            ReceivedAt = new DateTime(2026, 8, 18, 17, 50, 0, DateTimeKind.Utc),
            ReceivedByUserId = "user-1", ReceivedByUserName = "María",
        };
        context.OperatorRefundReceived.Add(refund);
        await context.SaveChangesAsync();

        var voidedByUser = new ApplicationUser { Id = "user-2", UserName = "gaston", FullName = "Gaston Admin" };
        context.Users.Add(voidedByUser);

        // Una allocation soft-voided contra ESE refund (deshecho).
        var bc = new BookingCancellation { Id = 200, ReservaId = 1, SupplierId = supplier.Id };
        var voidedAt = new DateTime(2026, 8, 19, 9, 0, 0, DateTimeKind.Utc);
        context.OperatorRefundAllocations.Add(new OperatorRefundAllocation
        {
            OperatorRefundReceivedId = refund.Id, BookingCancellationId = bc.Id,
            GrossAmount = 30000m, NetAmount = 30000m, CreatedByUserId = "user-1",
            IsVoided = true, VoidedAt = voidedAt, VoidedByUserId = voidedByUser.Id,
            VoidedReason = "Reasignacion de plata a la reserva correcta.",
        });
        context.BookingCancellations.Add(bc);
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        var registeredEvent = Assert.Single(timeline.Events, e => e.EventType == "OperatorRefundRegistered");
        Assert.Equal("Se registró un reembolso del operador: $ 30.000.", registeredEvent.Title);

        var undoneEvent = Assert.Single(timeline.Events, e => e.EventType == "OperatorRefundUndone");
        Assert.Equal("Gaston Admin deshizo el reembolso del operador.", undoneEvent.Title);
        Assert.Equal(voidedAt, undoneEvent.Timestamp);
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_PaymentRegistered_HasNoActor_MatchesSpecText()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, Amount = 15000m, Currency = "ARS", Method = "Transfer",
            PaidAt = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true, canSeePaymentDetails: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        var paymentEvent = Assert.Single(timeline.Events, e => e.EventType == "SupplierPaymentRegistered");
        Assert.Equal("Se registró un pago al operador: $ 15.000.", paymentEvent.Title);
    }

    // ===================================================================================================
    // Fix BLOQUEANTE (review seguridad B1 + data-exposure, 2026-08-20): el METODO de un pago al operador
    // es dato de TESORERIA (SEC-1) — sin tesoreria.supplier_payments viene en null (fail-closed); con el
    // permiso, se traduce al castellano en el servidor y NUNCA el token crudo ("Transfer").
    // ===================================================================================================

    [Fact]
    public async Task GetSupplierTimelineAsync_PaymentEvent_WithoutTreasuryPermission_HidesMethod_FailClosed()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, Amount = 15000m, Currency = "ARS", Method = "Transfer",
            PaidAt = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        // Caller con proveedores.view (implicito) + ve costos, pero SIN tesoreria.supplier_payments —
        // exactamente el escenario del hallazgo: un vendedor mirando la ficha del operador.
        var service = CreateServiceForUser(context, canSeeCost: true, canSeePaymentDetails: false);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        var paymentEvent = Assert.Single(timeline.Events, e => e.EventType == "SupplierPaymentRegistered");
        // El evento SIGUE existiendo (con el monto, que es cobranzas.see_cost, no tesoreria) pero SIN el metodo.
        Assert.Null(paymentEvent.Details);
        Assert.Null(paymentEvent.PaymentMethod);
        Assert.Equal(15000m, paymentEvent.Amount);
    }

    [Theory]
    [InlineData("Transfer", "Transferencia")]
    [InlineData("Cash", "Efectivo")]
    [InlineData("Card", "Tarjeta")]
    [InlineData("Check", "Cheque")]
    [InlineData("transfer", "Transferencia")] // case-insensitive
    [InlineData("MercadoPago", "Otro medio")] // desconocido -> generico, NUNCA el crudo
    public async Task GetSupplierTimelineAsync_PaymentEvent_WithTreasuryPermission_TranslatesMethodToSpanish(
        string rawMethod, string expectedSpanishLabel)
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, Amount = 15000m, Currency = "ARS", Method = rawMethod,
            PaidAt = new DateTime(2026, 8, 19, 10, 0, 0, DateTimeKind.Utc),
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true, canSeePaymentDetails: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        var paymentEvent = Assert.Single(timeline.Events, e => e.EventType == "SupplierPaymentRegistered");
        // Igualdad EXACTA (no solo "no contiene el crudo"): "Transfer" -> "Transferencia" naturalmente
        // CONTIENE la palabra en ingles como prefijo, asi que un DoesNotContain daria falso negativo ahi;
        // la igualdad exacta es la asercion correcta y mas fuerte para fijar "nunca el token crudo".
        Assert.Equal(expectedSpanishLabel, paymentEvent.Details);
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_InvoiceCreatedAndVoided_ShowsBothEvents()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var createdAt = new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc);
        var voidedAt = new DateTime(2026, 8, 15, 9, 0, 0, DateTimeKind.Utc);
        context.SupplierInvoices.Add(new SupplierInvoice
        {
            SupplierId = supplier.Id, Number = "0001-00004521", Currency = "ARS",
            IssuedAt = createdAt, DueDate = createdAt.AddDays(30),
            Status = SupplierInvoiceStatus.Void,
            CreatedAt = createdAt, CreatedByUserId = "user-1", CreatedByUserName = "María",
            VoidedAt = voidedAt, VoidReason = "Factura cargada con el monto equivocado.",
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        var createdEvent = Assert.Single(timeline.Events, e => e.EventType == "SupplierInvoiceCreated");
        Assert.Equal("Se cargó la factura 0001-00004521 del operador: $ 0.", createdEvent.Title); // sin lineas cargadas -> total 0
        Assert.Equal("María", createdEvent.Actor);

        var voidedEvent = Assert.Single(timeline.Events, e => e.EventType == "SupplierInvoiceVoided");
        Assert.Equal("Se anuló la factura 0001-00004521 del operador.", voidedEvent.Title);
        Assert.Contains("Motivo: Factura cargada con el monto equivocado.", voidedEvent.Details);
    }

    [Fact]
    public async Task GetSupplierTimelineAsync_OrdersEventsNewestFirst()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador", IsActive = true };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        context.SupplierPayments.Add(new SupplierPayment
        {
            SupplierId = supplier.Id, Amount = 1000m, Currency = "ARS", Method = "Transfer",
            PaidAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        context.OperatorRefundReceived.Add(new OperatorRefundReceived
        {
            SupplierId = supplier.Id, ReceivedAmount = 2000m, Currency = "ARS", Method = "Transfer",
            ReceivedAt = new DateTime(2026, 8, 15, 0, 0, 0, DateTimeKind.Utc),
            ReceivedByUserId = "u1", ReceivedByUserName = "María",
        });
        await context.SaveChangesAsync();

        var service = CreateServiceForUser(context, canSeeCost: true);
        var timeline = await service.GetSupplierTimelineAsync(supplier.Id, CancellationToken.None);

        Assert.True(timeline.Events.Count >= 2);
        for (int i = 1; i < timeline.Events.Count; i++)
        {
            Assert.True(timeline.Events[i - 1].Timestamp >= timeline.Events[i].Timestamp);
        }
    }
}
