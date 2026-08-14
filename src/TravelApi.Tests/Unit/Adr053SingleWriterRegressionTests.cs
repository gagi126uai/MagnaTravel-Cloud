using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-053 (2026-08-13) §1.3/D7: regresión explícita de los 4 "agujeros" de escritor único que la
/// investigación del ADR encontró (T-7 ya roto HOY, no una regla nueva de esta obra) — más un smoke del
/// camino de siempre (crear un servicio vía <see cref="BookingService"/>) para no perder la cobertura del
/// caso base al reemplazar el candado <c>DatesManuallySet</c>.
/// </summary>
public class Adr053SingleWriterRegressionTests
{
    private static AppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IMapper CreateMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private const string ActorUserId = "vendedor-test";

    private static IHttpContextAccessor BuildHttpContextAccessor()
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, ActorUserId) };
        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test")) }
        };
    }

    private static BookingService CreateBookingService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var accessor = BuildHttpContextAccessor();
        var resolver = new Mock<IUserPermissionResolver>();
        IReadOnlySet<string> permissions = new HashSet<string> { Permissions.CobranzasSeeCost };
        resolver.Setup(r => r.GetPermissionsAsync(ActorUserId, It.IsAny<CancellationToken>())).ReturnsAsync(permissions);

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
            accessor);
    }

    private static ReservaService CreateReservaService(AppDbContext context, IMapper mapper)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return new ReservaService(
            context, mapper, settings.Object, BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: BuildHttpContextAccessor());
    }

    private static QuoteService CreateQuoteService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
        return new QuoteService(
            context, Mock.Of<IEntityReferenceResolver>(), settings.Object,
            permissionResolver: null, httpContextAccessor: BuildHttpContextAccessor());
    }

    // ================================================================================================
    // SMOKE del camino de siempre: crear un hotel (BookingService, camino catálogo — único que existe
    // desde 2026-08-06) sigue recalculando la cabecera, ahora vía el escritor único.
    // ================================================================================================

    [Fact]
    public async Task CrearHotel_ViaBookingService_RecalculaCabecera()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Name = "Operador Test" };
        var reserva = new Reserva { NumeroReserva = "F-ADR053-BS1", Name = "Reserva smoke", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var checkIn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var checkOut = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
        var bookingService = CreateBookingService(context, mapper);
        var req = new CreateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Test", StarRating: 4, City: "Bariloche",
            Country: "Argentina", CheckIn: checkIn, CheckOut: checkOut, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null, NetCost: 500m, SalePrice: 800m,
            Commission: 300m, Notes: null, Currency: "ARS");

        await bookingService.CreateHotelAsync(reserva.Id, req, CancellationToken.None);

        var reloaded = await context.Reservas.SingleAsync();
        Assert.Equal(checkIn, reloaded.StartDate);
        Assert.Equal(checkOut, reloaded.EndDate);
        // Aviso suave: hay actor (ActorUserId), la ventana cambio de null a algo -> tiene que quedar el pendiente.
        Assert.NotNull(reloaded.PendingScheduleWarning);
        Assert.Equal(ActorUserId, reloaded.PendingScheduleWarningByUserId);
    }

    // ================================================================================================
    // AGUJERO #1/#2 — servicio GENÉRICO (ReservaService.AddServiceAsync/UpdateServiceAsync/RemoveServiceAsync):
    // hoy NUNCA recalculaban la cabecera.
    // ================================================================================================

    [Fact]
    public async Task AddServiceGenerico_RecalculaCabecera()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { NumeroReserva = "F-ADR053-G1", Name = "Reserva generico", Status = EstadoReserva.InManagement };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, CreateMapper());
        var departure = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var (reservation, _) = await service.AddServiceAsync(reserva.Id, new AddServiceRequest(
            ServiceType: "Excursion", SupplierId: null, Description: "Excursion de prueba",
            ConfirmationNumber: null, DepartureDate: departure, ReturnDate: null,
            SalePrice: 1000m, NetCost: 500m, RateId: null, OperatorPaymentDeadline: null,
            GeographicScope: null), CancellationToken.None);

        Assert.True(reservation.Id > 0);
        var reloaded = await context.Reservas.SingleAsync();
        Assert.Equal(departure, reloaded.StartDate);
        Assert.Equal(departure, reloaded.EndDate); // sin ReturnDate, coalesce a DepartureDate
    }

    [Fact]
    public async Task UpdateServiceGenerico_RecalculaCabecera()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { NumeroReserva = "F-ADR053-G2", Name = "Reserva generico edicion", Status = EstadoReserva.InManagement };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, CreateMapper());
        var originalDeparture = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var (reservation, _) = await service.AddServiceAsync(reserva.Id, new AddServiceRequest(
            ServiceType: "Excursion", SupplierId: null, Description: "Excursion de prueba",
            ConfirmationNumber: null, DepartureDate: originalDeparture, ReturnDate: null,
            SalePrice: 1000m, NetCost: 500m, RateId: null, OperatorPaymentDeadline: null,
            GeographicScope: null), CancellationToken.None);

        var newDeparture = new DateTime(2026, 11, 15, 0, 0, 0, DateTimeKind.Utc);
        await service.UpdateServiceAsync(reservation.Id, new AddServiceRequest(
            ServiceType: "Excursion", SupplierId: null, Description: "Excursion movida",
            ConfirmationNumber: null, DepartureDate: newDeparture, ReturnDate: null,
            SalePrice: 1000m, NetCost: 500m, RateId: null, OperatorPaymentDeadline: null,
            GeographicScope: null), CancellationToken.None);

        var reloaded = await context.Reservas.SingleAsync();
        Assert.Equal(newDeparture, reloaded.StartDate);
        Assert.Equal(newDeparture, reloaded.EndDate);
    }

    [Fact]
    public async Task RemoveServiceGenerico_RecalculaCabecera_SinAvisoSuave()
    {
        await using var context = CreateContext();
        var reserva = new Reserva { NumeroReserva = "F-ADR053-G3", Name = "Reserva generico borrado", Status = EstadoReserva.InManagement };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, CreateMapper());
        var departure = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var (reservation, _) = await service.AddServiceAsync(reserva.Id, new AddServiceRequest(
            ServiceType: "Excursion", SupplierId: null, Description: "Excursion de prueba",
            ConfirmationNumber: null, DepartureDate: departure, ReturnDate: null,
            SalePrice: 1000m, NetCost: 500m, RateId: null, OperatorPaymentDeadline: null,
            GeographicScope: null), CancellationToken.None);

        var afterAdd = await context.Reservas.AsNoTracking().SingleAsync();
        Assert.NotNull(afterAdd.PendingScheduleWarning); // el ALTA sí deja aviso

        await service.RemoveServiceAsync(reservation.PublicId.ToString(), CancellationToken.None);

        var reloaded = await context.Reservas.SingleAsync();
        Assert.Null(reloaded.StartDate); // se quedo sin servicios -> null/null
        Assert.Null(reloaded.EndDate);
        // D2: un borrado duro NO deja aviso suave (aunque la ventana cambió de verdad).
        Assert.Null(reloaded.PendingScheduleWarning);
    }

    // ================================================================================================
    // AGUJERO #2 — borrado UNIFICADO (DELETE /api/reservas/services/{id} -> ReservaService.RemoveServiceAsync(int),
    // distinto del BookingService.DeleteHotelAsync/DeleteFlightAsync que SÍ recalculaban): hoy NUNCA
    // recalculaba la cabecera para los 5 tipos tipados.
    // ================================================================================================

    [Fact]
    public async Task RemoveHotel_ViaBorradoUnificado_RecalculaCabecera_SinAvisoSuave()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador Test" };
        var reserva = new Reserva { NumeroReserva = "F-ADR053-U1", Name = "Reserva borrado unificado", Status = EstadoReserva.InManagement };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        var hotel = new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "Hotel a borrar",
            CheckIn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
            CheckOut = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc),
            Status = "Solicitado",
        };
        context.HotelBookings.Add(hotel);
        // Deja la cabecera YA seteada (como si un alta previa la hubiera calculado) para poder observar
        // el recalculo tras el borrado.
        reserva.StartDate = hotel.CheckIn;
        reserva.EndDate = hotel.CheckOut;
        await context.SaveChangesAsync();

        var service = CreateReservaService(context, CreateMapper());
        await service.RemoveServiceAsync(hotel.Id, CancellationToken.None);

        var reloaded = await context.Reservas.SingleAsync();
        Assert.Null(reloaded.StartDate); // el unico hotel se borro -> sin servicios -> null/null
        Assert.Null(reloaded.EndDate);
        Assert.Null(reloaded.PendingScheduleWarning); // borrado duro: sin aviso (D2)
    }

    // ================================================================================================
    // AGUJERO #4 — QuoteService.ConvertToFileCoreAsync copiaba StartDate/EndDate DIRECTO de la cabecera
    // del presupuesto. Caso que lo diferencia (B1 del review): la cabecera del presupuesto SIN fecha
    // (TravelStartDate/EndDate null) pero un item que SÍ termina generando un servicio con fecha real
    // (fallback interno del propio item) — con el código viejo, file.StartDate quedaba en null (mintiendo);
    // con el escritor único, refleja el servicio real recién creado.
    // ================================================================================================

    [Fact]
    public async Task ConvertToFile_CabeceraDelPresupuestoSinFecha_TomaLaFechaRealDelServicioCreado()
    {
        await using var context = CreateContext();
        var quote = new Quote
        {
            QuoteNumber = "COT-ADR053-1",
            Title = "Presupuesto sin fecha de cabecera",
            Status = QuoteStatus.Accepted,
            TravelStartDate = null,
            TravelEndDate = null,
            Adults = 2,
        };
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        context.QuoteItems.Add(new QuoteItem
        {
            QuoteId = quote.Id,
            ServiceType = "Hotel",
            Description = "Hotel sin tarifa ligada",
            Quantity = 1,
            SupplierId = null,
            RateId = null,
            UnitCost = 100m,
            UnitPrice = 150m,
        });
        await context.SaveChangesAsync();

        var service = CreateQuoteService(context);
        var reservaId = await service.ConvertToFileAsync(quote.Id, CancellationToken.None);

        var file = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == reservaId);
        // El item se creo con el fallback interno (CheckIn = quote.TravelStartDate ?? DateTime.UtcNow) —
        // NO null. La cabecera tiene que reflejar ESE valor real, no quedar en null como el codigo viejo.
        Assert.NotNull(file.StartDate);
        Assert.NotNull(file.EndDate);
    }
}
