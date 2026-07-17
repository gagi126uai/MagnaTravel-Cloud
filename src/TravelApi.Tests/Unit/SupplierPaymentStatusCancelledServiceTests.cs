using System;
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
/// ADR-048 T2 (modelo de estados derivados, 2026-07-17, reglas 6 y 7 del borrador aprobado): "un servicio
/// anulado no genera avisos de cobro/pago al operador, salvo multa/cargo real, y sus importes son historicos".
/// Antes de esta tanda, <c>GetReservaSupplierPaymentStatusAsync</c> reportaba "Operador impago" con el costo
/// PLENO de un servicio anulado, incluso sin ninguna multa real (el bug exacto de la reserva F-2026-1046: un
/// servicio anulado aparecia como "Operador impago US$385"). Estos tests fijan el contrato correcto:
///
/// <list type="bullet">
/// <item>Un servicio anulado SIN cargo real del operador queda AFUERA del listado (no genera fila).</item>
/// <item>Un servicio anulado CON una multa que el operador RETUVO del reembolso aparece, pero por el monto de
/// la multa (nunca el costo pleno) y SIEMPRE como pagado (la retencion ya lo salda, no hay nada pendiente).</item>
/// <item>Un servicio anulado con un cargo que el operador FACTURA APARTE aparece por ese monto, impago hasta
/// que se le registre un pago real (mismo circuito de pagos de siempre).</item>
/// <item>Un servicio vivo (no anulado) sigue reportando su costo pleno, sin cambios.</item>
/// </list>
/// </summary>
public class SupplierPaymentStatusCancelledServiceTests
{
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
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId) };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };

        var grantedPermissions = new System.Collections.Generic.HashSet<string> { Permissions.TesoreriaSupplierPayments };
        if (canSeeCost) grantedPermissions.Add(Permissions.CobranzasSeeCost);

        var permissions = new Mock<IUserPermissionResolver>();
        permissions
            .Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((System.Collections.Generic.IReadOnlySet<string>)grantedPermissions);

        return new SupplierService(context, auditService: null, httpContextAccessor: accessor, logger: null, permissionResolver: permissions.Object);
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

    private static async Task<HotelBooking> AddHotelAsync(
        AppDbContext context, int supplierId, int reservaId, decimal netCost, string status, string? currency = null)
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
            Status = status,
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
            Currency = currency,
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();
        return hotel;
    }

    private static async Task<FlightSegment> AddFlightAsync(
        AppDbContext context, int supplierId, int reservaId, decimal netCost, string iataStatus)
    {
        var flight = new FlightSegment
        {
            ReservaId = reservaId,
            SupplierId = supplierId,
            Origin = "AEP",
            Destination = "COR",
            DepartureTime = DateTime.UtcNow.AddDays(15),
            Status = iataStatus,
            NetCost = netCost,
            SalePrice = netCost * 1.5m,
        };
        context.FlightSegments.Add(flight);
        await context.SaveChangesAsync();
        return flight;
    }

    /// <summary>
    /// Cancelacion "de laboratorio": UNA linea que cancela el servicio indicado (por defecto un hotel, pero
    /// admite cualquier <see cref="CancellableServiceTable"/> — hace falta para probar un vuelo anulado), con
    /// la penalidad/cargo que le pase el test. No pasa por <c>BookingCancellationService</c> (no hace falta
    /// ejercer el flujo completo de cancelacion para probar el filtro de
    /// <c>GetReservaSupplierPaymentStatusAsync</c>, que solo LEE estas columnas fisicas).
    /// </summary>
    private static async Task SeedCancellationLineAsync(
        AppDbContext context,
        Reserva reserva,
        Supplier supplier,
        int serviceId,
        decimal retainedDeductionAmount = 0m,
        decimal? facturadaAparteAmount = null,
        BookingCancellationStatus bcStatus = BookingCancellationStatus.Closed,
        CancellableServiceTable serviceTable = CancellableServiceTable.Hotel,
        string lineCurrency = "ARS")
    {
        var customer = new Customer { FullName = "Cliente de prueba", IsActive = true };
        context.Customers.Add(customer);
        var invoice = new Invoice { TipoComprobante = 11, Resultado = "A" };
        context.Invoices.Add(invoice);
        await context.SaveChangesAsync();

        var bc = new BookingCancellation
        {
            ReservaId = reserva.Id,
            CustomerId = customer.Id,
            SupplierId = supplier.Id,
            OriginatingInvoiceId = invoice.Id,
            Status = bcStatus,
            Reason = "cancelacion de prueba",
            DraftedByUserId = "tester",
        };
        context.BookingCancellations.Add(bc);
        await context.SaveChangesAsync();

        var line = new BookingCancellationLine
        {
            BookingCancellationId = bc.Id,
            SupplierId = supplier.Id,
            ServiceTable = serviceTable,
            ServiceId = serviceId,
            Scope = BookingCancellationLineScope.Full,
            Currency = lineCurrency,
            RetainedDeductionAmount = retainedDeductionAmount,
        };
        context.Set<BookingCancellationLine>().Add(line);
        await context.SaveChangesAsync();

        if (facturadaAparteAmount is decimal amount)
        {
            context.Set<BookingCancellationLineOperatorCharge>().Add(new BookingCancellationLineOperatorCharge
            {
                BookingCancellationLineId = line.Id,
                Kind = OperatorChargeKind.AdministrativeFee,
                CollectionMode = PenaltyCollectionMode.FacturadaAparte,
                Amount = amount,
                Currency = lineCurrency,
                DocumentRef = "OP-DOC-1",
                ConfirmedByUserId = "tester",
            });
            await context.SaveChangesAsync();
        }
    }

    private static ServiceSupplierPaymentStatusDto? FindLine(ReservaSupplierPaymentStatusDto dto, Guid servicePublicId)
        => dto.Services.SingleOrDefault(s => s.ServicePublicId == servicePublicId);

    // ===================== regla 7: anulado SIN cargo real -> no genera fila =====================

    [Fact]
    public async Task LiveAndCancelledSiblingWithoutCharge_OnlyLiveServiceIsReported()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-001");
        var live = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, status: "Confirmado");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        Assert.NotNull(FindLine(dto, live.PublicId));
        Assert.Null(FindLine(dto, cancelled.PublicId));
    }

    [Fact]
    public async Task AllServicesCancelledWithoutCharge_ReportsNoServices()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-002");
        await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 1000m, status: "Cancelado");
        await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 500m, status: "Cancelado");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        Assert.Empty(dto.Services);
    }

    // ===================== regla 7: anulado CON multa retenida -> aparece por la multa, siempre "paid" =====================

    [Fact]
    public async Task CancelledServiceWithRetainedPenalty_ReportsOnlyPenaltyAmount_AlwaysPaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-003");
        // Costo pleno del servicio anulado: 800. El operador retuvo 150 de multa al devolver el reembolso.
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");
        await SeedCancellationLineAsync(context, reserva, supplier, cancelled.Id, retainedDeductionAmount: 150m);

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, cancelled.PublicId);
        Assert.NotNull(line);
        // La fila refleja SOLO la multa, nunca el costo pleno (800) del servicio anulado.
        Assert.Equal(150m, line!.NetCost);
        // La retencion ya salda la multa por construccion: nunca queda "impago".
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
        Assert.Equal(150m, line.PaidToOperator);
        Assert.Equal(0m, line.OutstandingToOperator);
    }

    // ===================== regla 7: anulado CON cargo facturado aparte -> impago hasta que se pague =====================

    [Fact]
    public async Task CancelledServiceWithInvoicedApartCharge_ReportsUnpaidUntilSettled()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-004");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");
        // El operador no retuvo nada del reembolso, pero factura APARTE un cargo administrativo de 200.
        await SeedCancellationLineAsync(context, reserva, supplier, cancelled.Id, facturadaAparteAmount: 200m);

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, cancelled.PublicId);
        Assert.NotNull(line);
        Assert.Equal(200m, line!.NetCost);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
        Assert.Equal(0m, line.PaidToOperator);
        Assert.Equal(200m, line.OutstandingToOperator);
    }

    [Fact]
    public async Task CancelledServiceWithInvoicedApartCharge_PaidInFull_ReportsAsPaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-005");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");
        await SeedCancellationLineAsync(context, reserva, supplier, cancelled.Id, facturadaAparteAmount: 200m);

        // El cargo facturado aparte se liquida como cualquier pago al operador imputado al servicio.
        var payment = new SupplierPayment
        {
            SupplierId = supplier.Id,
            ReservaId = reserva.Id,
            ServiceRecordKind = ServicePaymentRecordKinds.Hotel,
            ServicePublicId = cancelled.PublicId,
            Amount = 200m,
            Currency = "ARS",
            Method = "Transfer",
            PaidAt = DateTime.UtcNow,
        };
        context.SupplierPayments.Add(payment);
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, cancelled.PublicId);
        Assert.NotNull(line);
        Assert.Equal(200m, line!.NetCost);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
        Assert.Equal(200m, line.PaidToOperator);
        Assert.Equal(0m, line.OutstandingToOperator);
    }

    // ===================== regla 7: la cancelacion Aborted no cuenta (nunca tomo efecto) =====================

    [Fact]
    public async Task AbortedCancellation_DoesNotCountAsRealCharge()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-006");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");
        // La cancelacion se abandono (Aborted): su multa NUNCA tomo efecto, no debe generar deuda.
        await SeedCancellationLineAsync(
            context, reserva, supplier, cancelled.Id,
            retainedDeductionAmount: 150m, bcStatus: BookingCancellationStatus.Aborted);

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        Assert.Null(FindLine(dto, cancelled.PublicId));
    }

    // ===================== item (b): el credito FIFO no se desperdicia en un anulado sin cargo =====================

    [Fact]
    public async Task SupplierCreditIsNotWastedOnCancelledServiceWithoutCharge()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-007");
        // El anulado (sin cargo) es MAS VIEJO que el vivo, asi que en el FIFO por CreatedAt le tocaria primero
        // si no se excluyera: si el filtro no funcionara, "robaria" el credito antes de llegar al servicio vivo.
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 900m, status: "Cancelado");
        var live = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 500m, status: "Confirmado");

        var creditEntry = new SupplierCreditEntry
        {
            SupplierId = supplier.Id,
            Currency = "ARS",
            CreditedAmount = 500m,
            RemainingBalance = 0m, // ya esta totalmente aplicado (el test verifica el REPARTO, no el pool)
        };
        context.Set<SupplierCreditEntry>().Add(creditEntry);
        await context.SaveChangesAsync();

        context.Set<SupplierCreditApplication>().Add(new SupplierCreditApplication
        {
            SupplierCreditEntryId = creditEntry.Id,
            TargetReservaId = reserva.Id,
            Kind = SupplierCreditApplicationKind.Applied,
            Amount = 500m,
            CreatedByUserId = "tester",
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        // El servicio anulado sin cargo ni siquiera aparece; todo el saldo a favor fue al servicio vivo.
        Assert.Null(FindLine(dto, cancelled.PublicId));
        var liveLine = FindLine(dto, live.PublicId);
        Assert.NotNull(liveLine);
        Assert.Equal(500m, liveLine!.CreditAppliedToOperator);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, liveLine.Status);
    }

    // ===================== control: servicio vivo sigue igual que siempre =====================

    [Fact]
    public async Task LiveServiceWithoutPayments_StillReportsFullCostAsUnpaid()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-008");
        var live = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 1200m, status: "Confirmado");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, live.PublicId);
        Assert.NotNull(line);
        Assert.Equal(1200m, line!.NetCost);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
    }

    // ===================== tanda de endurecimientos (reviews backend+seguridad, 2026-07-17) =====================

    // (a) multimoneda: un cargo facturado aparte en USD sobre un servicio anulado en USD se reporta en USD,
    // nunca "convertido" ni pisado a ARS por default.
    [Fact]
    public async Task CancelledServiceWithChargeInUsd_ReportsInUsd()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista USD");
        var reserva = await AddReservaAsync(context, "F-T2-USD");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 300m, status: "Cancelado", currency: "USD");
        await SeedCancellationLineAsync(
            context, reserva, supplier, cancelled.Id,
            facturadaAparteAmount: 80m, lineCurrency: "USD");

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, cancelled.PublicId);
        Assert.NotNull(line);
        Assert.Equal("USD", line!.Currency);
        Assert.Equal(80m, line.NetCost);
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line.Status);
    }

    // (b) un vuelo anulado POR CODIGO IATA (UN/HX, no texto libre) sin cargo real tambien queda excluido:
    // ejercita el mapeo especifico de vuelo (WorkflowStatusHelper.MapFlightStatus), no el generico.
    [Theory]
    [InlineData("UN")]
    [InlineData("HX")]
    public async Task CancelledFlightByIataCode_WithoutCharge_IsExcluded(string cancelledIataCode)
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Aerolinea");
        var reserva = await AddReservaAsync(context, "F-T2-IATA-" + cancelledIataCode);
        var liveHotel = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 500m, status: "Confirmado");
        var cancelledFlight = await AddFlightAsync(context, supplier.Id, reserva.Id, netCost: 900m, iataStatus: cancelledIataCode);

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        Assert.NotNull(FindLine(dto, liveHotel.PublicId));
        Assert.Null(FindLine(dto, cancelledFlight.PublicId));
    }

    // (c) DOS lineas de cancelacion distintas sobre el MISMO servicio (ej. una primera cancelacion quedo con
    // multa retenida y una revision posterior sumo un cargo facturado aparte): los cargos reales se ACUMULAN,
    // no se pisan entre si.
    [Fact]
    public async Task TwoCancellationLinesOnSameService_AccumulateRealCharges()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-ACUM");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");

        // Dos eventos de cancelacion distintos (dos BC) apuntando al MISMO servicio interno.
        await SeedCancellationLineAsync(context, reserva, supplier, cancelled.Id, retainedDeductionAmount: 100m);
        await SeedCancellationLineAsync(context, reserva, supplier, cancelled.Id, facturadaAparteAmount: 50m);

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, cancelled.PublicId);
        Assert.NotNull(line);
        // 100 (retenida) + 50 (facturada aparte) = 150, nunca el costo pleno (800) ni una sola de las dos.
        Assert.Equal(150m, line!.NetCost);
        // La retencion (100) ya esta saldada; el facturado aparte (50) sigue pendiente de pago real.
        Assert.Equal(100m, line.PaidToOperator);
        Assert.Equal(50m, line.OutstandingToOperator);
        Assert.Equal(ServiceSupplierPaymentStatuses.Partial, line.Status);
    }

    // (d) una cancelacion en BORRADOR (Drafted, todavia no confirmada con el cliente) con un cargo real YA
    // confirmado por el operador SI cuenta -- el candado que excluye es "Aborted" (nunca tomo efecto), no
    // "no esta cerrada". Fija la semantica que ya usa el extracto del operador (SupplierCancellationCircuitReader).
    [Fact]
    public async Task DraftedCancellation_WithRealCharge_StillCounts()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-DRAFT");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");
        await SeedCancellationLineAsync(
            context, reserva, supplier, cancelled.Id,
            retainedDeductionAmount: 120m, bcStatus: BookingCancellationStatus.Drafted);

        var service = CreateService(context);
        var dto = await service.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, cancelled.PublicId);
        Assert.NotNull(line);
        Assert.Equal(120m, line!.NetCost);
        Assert.Equal(ServiceSupplierPaymentStatuses.Paid, line.Status);
    }

    // (e) ruta enmascarada (sin cobranzas.see_cost): un anulado CON cargo real sigue mostrando su ESTADO
    // (paid/partial/unpaid), pero los montos se anulan a 0 -- mismo contrato P4=B que el resto del endpoint.
    [Fact]
    public async Task CancelledServiceWithCharge_WithoutSeeCost_StatusVisible_AmountsMasked()
    {
        await using var context = CreateContext();
        var supplier = await AddSupplierAsync(context, "Mayorista");
        var reserva = await AddReservaAsync(context, "F-T2-MASK");
        var cancelled = await AddHotelAsync(context, supplier.Id, reserva.Id, netCost: 800m, status: "Cancelado");
        await SeedCancellationLineAsync(context, reserva, supplier, cancelled.Id, facturadaAparteAmount: 200m);

        var reader = CreateService(context, canSeeCost: false);
        var dto = await reader.GetReservaSupplierPaymentStatusAsync(reserva.Id, CancellationToken.None);

        var line = FindLine(dto, cancelled.PublicId);
        Assert.NotNull(line);
        Assert.False(dto.AmountsVisible);
        // El estado (impago, todavia sin pagar el cargo) se ve igual sin permiso de costos.
        Assert.Equal(ServiceSupplierPaymentStatuses.Unpaid, line!.Status);
        // Los montos se anulan.
        Assert.Equal(0m, line.NetCost);
        Assert.Equal(0m, line.PaidToOperator);
        Assert.Equal(0m, line.OutstandingToOperator);
    }
}
