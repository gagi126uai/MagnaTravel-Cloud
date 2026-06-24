using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Contracts;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// REPROGRAMAR VIAJE (2026-06-23): mover JUNTAS las fechas de todos los servicios de una reserva por un
/// desplazamiento de N dias. Estos tests pinean:
/// - +N / -N mueven TODAS las fechas de TODOS los tipos y recalculan StartDate/EndDate;
/// - los guards reusados (estado terminal/En viaje, candado de autorizacion en Confirmada, fiscal CAE/voucher);
/// - el criterio de cancelados (se mueven igual, coherente con ReservaScheduleCalculator);
/// - el modo "nueva fecha de salida" (deriva el shift);
/// - la auditoria (ReservaRescheduled queda registrada con shift + conteo, sin montos).
///
/// <para>InMemory NO valida el Kind de las fechas, pero igual nos deja assertar que AddDays preserva Kind=Utc
/// y mueve la fecha calendario sin correr la hora de pared.</para>
/// </summary>
public class BookingServiceRescheduleTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    /// <summary>Fake de IAuditService que captura los eventos stageados para poder assertarlos.</summary>
    private sealed class RecordingAuditService : IAuditService
    {
        public List<(string Action, string EntityName, string EntityId, string? Details)> Staged { get; } = new();

        public Task<IEnumerable<AuditLog>> GetAuditLogsAsync(string? entityName, string? entityId, string? alternateEntityId, DateTime? dateFrom, DateTime? dateTo, string? userId, CancellationToken ct)
            => Task.FromResult<IEnumerable<AuditLog>>(Array.Empty<AuditLog>());

        public Task<PagedResult<AuditLog>> GetGlobalAuditLogsAsync(string? entityName, string? action, string? userId, DateTime? dateFrom, DateTime? dateTo, string? searchTerm, string? category, int page, int pageSize, CancellationToken ct)
            => Task.FromResult(new PagedResult<AuditLog>(Array.Empty<AuditLog>(), 0, page, pageSize));

        public Task LogBusinessEventAsync(string action, string entityName, string entityId, string? details, string userId, string? userName, CancellationToken ct)
        {
            Staged.Add((action, entityName, entityId, details));
            return Task.CompletedTask;
        }

        public void StageBusinessEvent(string action, string entityName, string entityId, string? details, string userId, string? userName)
            => Staged.Add((action, entityName, entityId, details));
    }

    private static BookingService CreateService(AppDbContext context, IMapper mapper, IAuditService? audit = null)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        const string userId = "vendedor-test";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId), new(ClaimTypes.Name, "Vendedor Test") };
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(userId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings { EnableCatalogFindOrCreate = false });

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaService.Object,
            supplierService.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance,
            resolver.Object,
            accessor,
            settings.Object,
            audit);
    }

    // Fechas base. Vuelo/transfer = hora de pared; hotel/paquete/asistencia/generico = fecha calendario.
    private static readonly DateTime FlightDeparture = DateTime.SpecifyKind(new DateTime(2026, 8, 10, 14, 30, 0), DateTimeKind.Utc);
    private static readonly DateTime FlightArrival = DateTime.SpecifyKind(new DateTime(2026, 8, 10, 17, 45, 0), DateTimeKind.Utc);
    private static readonly DateTime HotelCheckIn = DateTime.SpecifyKind(new DateTime(2026, 8, 10), DateTimeKind.Utc);
    private static readonly DateTime HotelCheckOut = DateTime.SpecifyKind(new DateTime(2026, 8, 15), DateTimeKind.Utc);
    private static readonly DateTime TransferPickup = DateTime.SpecifyKind(new DateTime(2026, 8, 10, 12, 0, 0), DateTimeKind.Utc);

    /// <summary>
    /// Siembra una reserva con vuelo (ida+vuelta de un segmento), hotel y transfer (solo ida). Es la
    /// combinacion multi-tipo que pide el caso. status = estado de la reserva.
    ///
    /// <para>G5 (2026-06-24): el default es <see cref="EstadoReserva.Traveling"/> porque reprogramar ahora solo
    /// se permite desde Confirmada en adelante ({Confirmed, Traveling}). Se elige Traveling (no Confirmed) para
    /// los tests de MECANICA DE FECHAS porque Confirmed esta bajo el candado de autorizacion (ReservaLockGuard)
    /// y exigiria sembrar una autorizacion viva en cada test; Traveling es reprogramable y NO tiene candado, asi
    /// que aisla la mecanica del shift sin ruido. Antes el default era En gestion, que ya NO es reprogramable.</para>
    /// </summary>
    private static async Task<Reserva> SeedMultiTypeAsync(AppDbContext context, string status = EstadoReserva.Traveling)
    {
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-RESCH", Name = "Reserva reprogramable",
            Status = status, StartDate = HotelCheckIn, EndDate = HotelCheckOut
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);

        context.FlightSegments.Add(new FlightSegment
        {
            Id = 100, ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "EZE", Destination = "BRC",
            DepartureTime = FlightDeparture, ArrivalTime = FlightArrival,
            Status = "Solicitado", SalePrice = 500m
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 200, ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "Hotel Test", City = "Bariloche",
            CheckIn = HotelCheckIn, CheckOut = HotelCheckOut, RoomType = "Doble", MealPlan = "Desayuno",
            Status = "Solicitado", SalePrice = 300m
        });
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 300, ReservaId = reserva.Id, SupplierId = supplier.Id,
            PickupLocation = "Aeropuerto", DropoffLocation = "Hotel",
            PickupDateTime = TransferPickup, ReturnDateTime = null, Passengers = 2,
            Status = "Solicitado", SalePrice = 80m
        });

        await context.SaveChangesAsync();
        return reserva;
    }

    // ===================== +N dias mueve todo y recalcula cabecera =====================

    [Fact]
    public async Task Reschedule_ForwardShift_MovesAllServiceDatesAndRecalculatesHeader()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        var service = CreateService(context, CreateMapper());

        var result = await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None);

        Assert.Equal(7, result.DaysShift);
        Assert.Equal(3, result.ServicesMoved); // vuelo + hotel + transfer

        var flight = await context.FlightSegments.SingleAsync();
        Assert.Equal(FlightDeparture.AddDays(7), flight.DepartureTime);
        Assert.Equal(FlightArrival.AddDays(7), flight.ArrivalTime);
        Assert.Equal(DateTimeKind.Utc, flight.DepartureTime.Kind);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn.AddDays(7), hotel.CheckIn);
        Assert.Equal(HotelCheckOut.AddDays(7), hotel.CheckOut);

        var transfer = await context.TransferBookings.SingleAsync();
        Assert.Equal(TransferPickup.AddDays(7), transfer.PickupDateTime);
        Assert.Null(transfer.ReturnDateTime); // nullable sin valor queda null

        // Cabecera recalculada: nuevo min/max sobre TODAS las fechas. La salida mas temprana es el check-in
        // del hotel (medianoche del 17/08), antes que el vuelo (17/08 14:30); el fin mas tardio es el checkout.
        var reloaded = await context.Reservas.SingleAsync();
        Assert.Equal(HotelCheckIn.AddDays(7), reloaded.StartDate);
        Assert.Equal(HotelCheckOut.AddDays(7), reloaded.EndDate);
        Assert.Equal(HotelCheckIn.AddDays(7), result.NewStartDate);
        Assert.Equal(HotelCheckOut.AddDays(7), result.NewEndDate);
    }

    // ===================== -N dias idem =====================

    [Fact]
    public async Task Reschedule_BackwardShift_MovesAllDatesEarlier()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        var service = CreateService(context, CreateMapper());

        var result = await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: -3), CancellationToken.None);

        Assert.Equal(-3, result.DaysShift);
        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn.AddDays(-3), hotel.CheckIn);
        Assert.Equal(HotelCheckOut.AddDays(-3), hotel.CheckOut);
    }

    // ===================== Round-trip (+N luego -N) vuelve al origen =====================

    [Fact]
    public async Task Reschedule_ForwardThenBackward_ReturnsToOrigin()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        var service = CreateService(context, CreateMapper());

        await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 10), CancellationToken.None);
        await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: -10), CancellationToken.None);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn, hotel.CheckIn);
        Assert.Equal(HotelCheckOut, hotel.CheckOut);
        var flight = await context.FlightSegments.SingleAsync();
        Assert.Equal(FlightDeparture, flight.DepartureTime);
    }

    // ===================== Modo "nueva fecha de salida" deriva el shift =====================

    [Fact]
    public async Task Reschedule_NewStartDate_DerivesShiftFromReservaStart()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context); // StartDate = 10/08
        var service = CreateService(context, CreateMapper());

        // Nueva salida 20/08 = +10 dias respecto del StartDate de la reserva (10/08).
        var newStart = DateTime.SpecifyKind(new DateTime(2026, 8, 20), DateTimeKind.Utc);
        var result = await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(NewStartDate: newStart), CancellationToken.None);

        Assert.Equal(10, result.DaysShift);
        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn.AddDays(10), hotel.CheckIn);
    }

    // ===================== Cancelados se mueven igual (criterio coherente con el calculator) =====================

    [Fact]
    public async Task Reschedule_MovesCancelledServicesToo()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        // El transfer queda cancelado: debe moverse igual (coherencia fechas<->reserva, ADR-019 R8).
        var transfer = await context.TransferBookings.SingleAsync();
        transfer.Status = WorkflowStatuses.Cancelado;
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var result = await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 5), CancellationToken.None);

        Assert.Equal(3, result.ServicesMoved); // los 3, incluido el cancelado
        var movedTransfer = await context.TransferBookings.SingleAsync();
        Assert.Equal(TransferPickup.AddDays(5), movedTransfer.PickupDateTime);
    }

    // ===================== En viaje SI se puede reprogramar (G5) =====================

    [Fact]
    public async Task Reschedule_Traveling_IsAllowed()
    {
        // G5 (2026-06-24): reprogramar aplica "desde Confirmada en adelante" e INCLUYE En viaje. Es el caso de
        // uso central del feature: el vuelo se atraso con el viaje ya empezado y todo el itinerario se corre.
        // Antes este estado estaba bloqueado (reusaba CanEditServices, que trata Traveling como solo lectura);
        // ahora usa el gate dedicado CanReschedule, que SI permite Traveling.
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context, status: EstadoReserva.Traveling);
        var service = CreateService(context, CreateMapper());

        var result = await service.RescheduleAsync(
            reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None);

        Assert.Equal(7, result.DaysShift);
        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn.AddDays(7), hotel.CheckIn);
    }

    // ===================== Bloqueo por estado: pre-firme (En gestion) NO reprograma (G5) =====================

    [Fact]
    public async Task Reschedule_InManagement_IsBlockedByStateGuard()
    {
        // G5 (2026-06-24): En gestion es pre-firme. El itinerario todavia se esta armando: mover fechas ahi es
        // editar el servicio, no "reprogramar el viaje". Reprogramar solo aplica desde Confirmada en adelante.
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context, status: EstadoReserva.InManagement);
        var service = CreateService(context, CreateMapper());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None));

        // No se movio nada.
        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn, hotel.CheckIn);
    }

    // ===================== Bloqueo por estado: terminal (Cancelada) =====================

    [Fact]
    public async Task Reschedule_Cancelled_IsBlockedByStateGuard()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context, status: EstadoReserva.Cancelled);
        var service = CreateService(context, CreateMapper());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None));
    }

    // ===================== Bloqueo fiscal: factura con CAE vivo =====================

    [Fact]
    public async Task Reschedule_LiveCae_IsBlockedByFiscalGuard()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context, status: EstadoReserva.Confirmed);
        // Factura con CAE vivo (no NC, no anulada). TipoComprobante 1 = Factura A.
        context.Invoices.Add(new Invoice
        {
            Id = 5000, ReservaId = reserva.Id, TipoComprobante = 1,
            CAE = "70000000000001", AnnulmentStatus = AnnulmentStatus.None
        });
        // Autorizacion viva para que el candado de Confirmed no sea lo que bloquee (queremos aislar el fiscal).
        context.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 1, ReservaId = reserva.Id, ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None));
        Assert.Contains("factura emitida", ex.Message);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn, hotel.CheckIn); // nada se movio
    }

    // ===================== Bloqueo fiscal: voucher emitido =====================

    [Fact]
    public async Task Reschedule_IssuedVoucher_IsBlockedByFiscalGuard()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context, status: EstadoReserva.Confirmed);
        context.Vouchers.Add(new Voucher { Id = 6000, ReservaId = reserva.Id, Status = VoucherStatuses.Issued });
        context.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 1, ReservaId = reserva.Id, ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None));
        Assert.Contains("voucher", ex.Message);
    }

    // ===================== Candado: Confirmada SIN autorizacion viva =====================

    [Fact]
    public async Task Reschedule_Confirmed_NoAuthorization_IsBlockedByLock()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context, status: EstadoReserva.Confirmed);
        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None));
        Assert.Equal(ReservaLockGuard.LockedMessage, ex.Message);

        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn, hotel.CheckIn); // nada se movio
    }

    // ===================== Candado: Confirmada CON autorizacion viva = pasa =====================

    [Fact]
    public async Task Reschedule_Confirmed_WithAuthorization_Succeeds()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context, status: EstadoReserva.Confirmed);
        context.ReservaEditAuthorizations.Add(new ReservaEditAuthorization
        {
            Id = 1, ReservaId = reserva.Id, ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await context.SaveChangesAsync();
        var service = CreateService(context, CreateMapper());

        var result = await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 7), CancellationToken.None);

        Assert.Equal(3, result.ServicesMoved);
        // El candado registra la operacion bajo la autorizacion viva.
        var change = await context.ReservaEditAuthorizationChanges.SingleAsync();
        Assert.Equal(ReservaEditAuthorizationOperations.ReservaDataEdited, change.Operation);
    }

    // ===================== Validacion de request: ambos modos / ninguno =====================

    [Fact]
    public async Task Reschedule_BothModes_ThrowsArgumentException()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        var service = CreateService(context, CreateMapper());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RescheduleAsync(reserva.PublicId.ToString(),
                new RescheduleReservaRequest(DaysShift: 7, NewStartDate: DateTime.UtcNow), CancellationToken.None));
    }

    [Fact]
    public async Task Reschedule_NoMode_ThrowsArgumentException()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        var service = CreateService(context, CreateMapper());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(), CancellationToken.None));
    }

    // ===================== No-op (shift 0) no mueve nada ni audita =====================

    [Fact]
    public async Task Reschedule_ZeroShift_IsNoOp_NoAudit()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        var audit = new RecordingAuditService();
        var service = CreateService(context, CreateMapper(), audit);

        var result = await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 0), CancellationToken.None);

        Assert.Equal(0, result.DaysShift);
        Assert.Equal(0, result.ServicesMoved);
        Assert.Empty(audit.Staged); // no-op no genera evento
        var hotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(HotelCheckIn, hotel.CheckIn);
    }

    // ===================== Auditoria: shift real queda registrado sin montos =====================

    [Fact]
    public async Task Reschedule_RealShift_RecordsAuditWithoutAmounts()
    {
        await using var context = CreateContext();
        var reserva = await SeedMultiTypeAsync(context);
        var audit = new RecordingAuditService();
        var service = CreateService(context, CreateMapper(), audit);

        await service.RescheduleAsync(reserva.PublicId.ToString(), new RescheduleReservaRequest(DaysShift: 4), CancellationToken.None);

        var evt = Assert.Single(audit.Staged);
        Assert.Equal(AuditActions.ReservaRescheduled, evt.Action);
        Assert.Equal(AuditActions.ReservaEntityName, evt.EntityName);
        Assert.Equal(reserva.Id.ToString(), evt.EntityId);
        Assert.Contains("\"daysShift\":4", evt.Details);
        Assert.Contains("\"servicesMoved\":3", evt.Details);
    }
}
