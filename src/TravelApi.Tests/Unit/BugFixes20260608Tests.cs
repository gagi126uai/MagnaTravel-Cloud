using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Errors;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// 4 bugs reportados por el dueño el 2026-06-08:
///  - BUG 1: editar un servicio sin mandar Status rompia con "The Status field is required"
///    (los DTO de update exigian Status). Ahora Status es OPCIONAL y el update funciona sin el.
///  - BUG 2: un vuelo solo de ida era imposible porque ArrivalTime era obligatorio. Ahora es nullable.
///  - BUG 3: volver de Perdido (Lost) y avanzar a "En gestion" con los pasajeros YA completos no debe
///    ser un error que bloquee — la transicion Budget -> InManagement debe pasar.
///  - BUG 4: un error de constraint/dato al guardar un pasajero se rotulaba como "base no disponible"
///    (503). El clasificador ahora distingue conectividad real de un error de datos del request.
/// </summary>
public class BugFixes20260608Tests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IMapper NewMapper()
        => new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();

    // ===================== Harness BookingService (BUG 1 + BUG 2) =====================

    private static BookingService NewBookingService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        // ADR-027: overload nuevo que pasan los paths de edicion (marca "confirmada con cambios").
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        // Caller CON ver-costos para que "request manda" y el test no dependa del masking.
        const string userId = "vendedor-test";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
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
            settings.Object);
    }

    private static async Task<(Reserva reserva, Supplier supplier)> SeedReservaAndSupplierAsync(AppDbContext context, string status = "Budget")
    {
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-BUG", Name = "Reserva bug", Status = status };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    // ---------- BUG 1: Status opcional en update ----------

    [Fact]
    public async Task UpdateHotel_WithoutStatus_DoesNotThrow_AndKeepsServiceSolicitadoInBudget()
    {
        // El form de edicion NO manda Status. En Presupuesto el servicio debe seguir "Solicitado".
        await using var context = NewContext();
        var mapper = NewMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 50, ReservaId = reserva.Id, SupplierId = supplier.Id, HotelName = "Hotel X",
            City = "Bariloche", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12),
            Adults = 2, Children = 0, Rooms = 1, Status = "Solicitado", SalePrice = 150m
        });
        await context.SaveChangesAsync();
        var service = NewBookingService(context, mapper);

        // Status = null (no enviado por el form). Cambiamos solo pax/fecha.
        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel X", StarRating: null,
            City: "Bariloche", Country: null,
            CheckIn: DateTime.UtcNow.Date.AddDays(11), CheckOut: DateTime.UtcNow.Date.AddDays(13),
            RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 3, Children: 1, Rooms: 1, ConfirmationNumber: null,
            NetCost: 100m, SalePrice: 180m, Commission: 80m);

        var dto = await service.UpdateHotelAsync(reserva.Id, 50, request, CancellationToken.None);

        Assert.NotNull(dto);
        var stored = await context.HotelBookings.SingleAsync(h => h.Id == 50);
        Assert.Equal("Solicitado", stored.Status); // En Presupuesto se fuerza a Solicitado.
        Assert.Equal(3, stored.Adults);
    }

    [Fact]
    public async Task UpdateFlight_WithoutStatus_DoesNotThrow()
    {
        await using var context = NewContext();
        var mapper = NewMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 60, ReservaId = reserva.Id, SupplierId = supplier.Id,
            AirlineCode = "AR", FlightNumber = "1234", Origin = "EZE", Destination = "BRC",
            DepartureTime = DateTime.UtcNow.AddDays(10), ArrivalTime = DateTime.UtcNow.AddDays(10).AddHours(2),
            Status = "NN", SalePrice = 500m
        });
        await context.SaveChangesAsync();
        var service = NewBookingService(context, mapper);

        // Status = null por defecto (el form no lo manda). Antes esto tiraba 400 "Status field required".
        var request = new UpdateFlightRequest(
            SupplierId: supplier.PublicId.ToString(), AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: DateTime.UtcNow.AddDays(11), ArrivalTime: DateTime.UtcNow.AddDays(11).AddHours(2),
            CabinClass: null, Baggage: null, TicketNumber: null, PNR: null,
            NetCost: 300m, SalePrice: 550m, Commission: 250m, Tax: 0m);

        var dto = await service.UpdateFlightAsync(reserva.Id, 60, request, CancellationToken.None);

        Assert.NotNull(dto);
    }

    // ---------- BUG 2: vuelo solo de ida (ArrivalTime null) ----------

    [Fact]
    public async Task CreateFlight_OneWay_NullArrivalTime_PersistsNull()
    {
        await using var context = NewContext();
        var mapper = NewMapper();
        var (reserva, supplier) = await SeedReservaAndSupplierAsync(context);
        var service = NewBookingService(context, mapper);

        var request = new CreateFlightRequest(
            SupplierId: supplier.PublicId.ToString(),
            AirlineCode: "AR", AirlineName: "Aerolineas", FlightNumber: "1234",
            Origin: "EZE", OriginCity: "Buenos Aires", Destination: "BRC", DestinationCity: "Bariloche",
            DepartureTime: new DateTime(2026, 8, 12, 14, 30, 0, DateTimeKind.Unspecified),
            ArrivalTime: null, // vuelo solo de ida: sin hora de llegada
            CabinClass: null, Baggage: null, PNR: null,
            NetCost: 300m, SalePrice: 500m, Commission: 200m, Tax: 0m, Notes: null);

        var dto = await service.CreateFlightAsync(reserva.Id, request, CancellationToken.None);

        Assert.Null(dto.ArrivalTime);
        var stored = await context.FlightSegments.SingleAsync();
        Assert.Null(stored.ArrivalTime);
        // La salida SI se persiste (obligatoria) con Kind=Utc.
        Assert.Equal(DateTimeKind.Utc, stored.DepartureTime.Kind);
    }

    [Fact]
    public async Task ScheduleCalculator_OneWayFlight_UsesDepartureAsEnd()
    {
        // Un vuelo solo de ida (sin llegada) no debe dejar la reserva sin EndDate:
        // el "fin" del segmento es su salida.
        await using var context = NewContext();
        var departure = DateTime.SpecifyKind(new DateTime(2026, 8, 12, 14, 30, 0), DateTimeKind.Utc);
        context.Reservas.Add(new Reserva { Id = 1, NumeroReserva = "F-2026-OW", Name = "Solo ida", Status = "Budget" });
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 70, ReservaId = 1, SupplierId = 1, Status = "NN",
            DepartureTime = departure, ArrivalTime = null, SalePrice = 100m
        });
        await context.SaveChangesAsync();

        var (start, end) = await ReservaScheduleCalculator.ComputeAsync(context, 1, CancellationToken.None);

        Assert.Equal(departure, start);
        Assert.Equal(departure, end);
    }

    // ===================== Harness ReservaService (BUG 3) =====================

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store.Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private static ReservaService NewReservaService(AppDbContext context)
    {
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var mapper = new Mock<IMapper>();
        mapper.Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
              .Returns((Reserva r) => new ReservaDto
              {
                  PublicId = r.PublicId, NumeroReserva = r.NumeroReserva, Name = r.Name, Status = r.Status
              });
        // AddPassengerAsync mapea el pasajero creado a PassengerDto; sin este setup el mock
        // devuelve null y el camino feliz (carga exitosa) no se puede asertar.
        mapper.Setup(m => m.Map<PassengerDto>(It.IsAny<Passenger>()))
              .Returns((Passenger p) => new PassengerDto { FullName = p.FullName });
        return new ReservaService(context, mapper.Object, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance);
    }

    // ---------- BUG 3: volver de Perdido con pasajeros completos y avanzar ----------

    [Fact]
    public async Task ReturnFromLost_WithCompletePassengers_CanAdvanceBudgetToInManagement()
    {
        await using var context = NewContext();
        // Reserva Perdida que venia de Presupuesto, con 2 pasajeros DECLARADOS y los 2 nominales YA
        // cargados. El conteo esperado se basa en la cantidad DECLARADA de la reserva, no en el hotel.
        var reserva = new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-LOST", Name = "Reserva perdida", Status = EstadoReserva.Lost,
            AdultCount = 2, ChildCount = 0, InfantCount = 0
        };
        context.Reservas.Add(reserva);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 80, ReservaId = 1, HotelName = "Hotel Y", City = "Mendoza",
            RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = DateTime.UtcNow.Date.AddDays(20), CheckOut = DateTime.UtcNow.Date.AddDays(22),
            Adults = 2, Children = 0, Rooms = 1, Status = "Solicitado", SalePrice = 300m
        });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Pasajero Dos" });
        context.ReservaStatusChangeLogs.Add(new ReservaStatusChangeLog
        {
            ReservaId = 1, FromStatus = EstadoReserva.Budget, ToStatus = EstadoReserva.Lost,
            Direction = "Forward", OccurredAt = DateTime.UtcNow.AddMinutes(-10)
        });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        // 1) Volver de Perdido a Presupuesto (target deterministico = Budget).
        var reverted = await service.RevertStatusAsync(
            "1",
            new RevertStatusRequest(EstadoReserva.Budget, null, "el cliente retomo"),
            actorUserId: "admin-1", actorUserName: "Admin", actorIsAdmin: true, CancellationToken.None);
        Assert.Equal(EstadoReserva.Budget, reverted.Status);

        // 2) Avanzar a En gestion. Los pasajeros ya estan completos (2/2) -> NO debe lanzar
        //    ningun error de "pasajeros ya cargados". Antes esto bloqueaba el avance.
        var advanced = await service.UpdateStatusAsync(1, EstadoReserva.InManagement);
        Assert.Equal(EstadoReserva.InManagement, advanced.Status);
    }

    [Fact]
    public async Task AddPassenger_BeyondCapacity_StillGuards_ButNotAsDbFailure()
    {
        // El guard de capacidad sigue protegiendo contra crear un pasajero de mas: es una regla de
        // negocio (InvalidOperationException -> 409), NO un fallo de base. Esto confirma que NO
        // ablandamos la integridad al arreglar el BUG 3. El tope es la cantidad DECLARADA (1 adulto).
        await using var context = NewContext();
        context.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-2026-CAP", Name = "Cap", Status = EstadoReserva.Budget,
            AdultCount = 1, ChildCount = 0, InfantCount = 0
        });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Unico Pasajero" });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddPassengerAsync("1",
                new PassengerUpsertRequest("Pasajero Extra", null, null, null, null, null, null, null, null),
                CancellationToken.None));
        Assert.Contains("pasajeros", ex.Message);
    }

    // ===================== Conteo de pasajeros: fuente unica = cantidad DECLARADA =====================
    // Bug 2026-06-08: el "esperado" se inferia de la capacidad de los servicios de forma
    // inconsistente (Hotel/Package con Sum, Transfer con Max, sin Flight) -> daba 0 a veces y 3
    // otras, dejaba avanzar con 0 pasajeros, y bloqueaba la carga sin coherencia. Ahora el conteo
    // esperado/tope = Reserva.AdultCount + ChildCount + InfantCount (lo que el usuario declara).

    private static Reserva BudgetReservaWithDeclaredPax(int adults, int children, int infants) => new()
    {
        Id = 1, NumeroReserva = "F-2026-PAX", Name = "Pax", Status = EstadoReserva.Budget,
        AdultCount = adults, ChildCount = children, InfantCount = infants
    };

    private static HotelBooking SolicitadoHotel(int reservaId, int adults) => new()
    {
        Id = 100, ReservaId = reservaId, HotelName = "Hotel Pax", City = "Cordoba",
        RoomType = "Doble", MealPlan = "Desayuno",
        CheckIn = DateTime.UtcNow.Date.AddDays(5), CheckOut = DateTime.UtcNow.Date.AddDays(7),
        Adults = adults, Children = 0, Rooms = 1, Status = "Solicitado", SalePrice = 100m
    };

    // (1) No se puede avanzar a En gestion con 0 pasajeros declarados.
    [Fact]
    public async Task AdvanceToInManagement_WithZeroDeclaredPassengers_IsRejected()
    {
        await using var context = NewContext();
        // Hay servicio (requisito previo) pero 0 pasajeros declarados. Antes esto avanzaba en silencio.
        context.Reservas.Add(BudgetReservaWithDeclaredPax(adults: 0, children: 0, infants: 0));
        context.HotelBookings.Add(SolicitadoHotel(reservaId: 1, adults: 2));
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateStatusAsync(1, EstadoReserva.InManagement));
        // G4 (2026-06-24): el mensaje se reescribio sin jerga ("Declará al menos 1 pasajero antes de marcar que
        // el cliente aceptó."). Pineamos la palabra "pasajero" para no acoplarnos a la frase exacta.
        Assert.Contains("pasajero", ex.Message);
        // No transiciono.
        Assert.Equal(EstadoReserva.Budget, (await context.Reservas.FindAsync(1))!.Status);
    }

    // (2) ADR-031: Budget -> InManagement ya NO exige los pasajeros NOMINALES (solo la cantidad
    // declarada > 0). Con 3 declarados y solo 2 nominales cargados, antes esto bloqueaba; ahora el
    // avance es PERMITIDO (los nombres se exigen recien al resolver/emitir cada servicio).
    [Fact]
    public async Task AdvanceToInManagement_WithDeclaredPaxButIncompleteNominals_IsAllowed()
    {
        await using var context = NewContext();
        // Declarados: 3 (2 adultos + 1 menor). Solo 2 nominales cargados de los 3 declarados.
        context.Reservas.Add(BudgetReservaWithDeclaredPax(adults: 2, children: 1, infants: 0));
        context.HotelBookings.Add(SolicitadoHotel(reservaId: 1, adults: 2));
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Pasajero Dos" });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        // ADR-031: el avance NO debe lanzar por nominales incompletos.
        var advanced = await service.UpdateStatusAsync(1, EstadoReserva.InManagement);
        Assert.Equal(EstadoReserva.InManagement, advanced.Status);
    }

    // (3a) Cargar exactamente la cantidad declarada funciona.
    [Fact]
    public async Task AddPassenger_UpToDeclaredCount_Succeeds()
    {
        await using var context = NewContext();
        context.Reservas.Add(BudgetReservaWithDeclaredPax(adults: 2, children: 0, infants: 0));
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        // Cargar el segundo (de 2 declarados) debe funcionar.
        var dto = await service.AddPassengerAsync("1",
            new PassengerUpsertRequest("Pasajero Dos", null, null, null, null, null, null, null, null),
            CancellationToken.None);

        Assert.NotNull(dto);
        Assert.Equal(2, await context.Passengers.CountAsync(p => p.ReservaId == 1));
    }

    // (3b) Uno mas que la cantidad declarada se rechaza con mensaje claro.
    [Fact]
    public async Task AddPassenger_BeyondDeclaredCount_IsRejectedWithClearMessage()
    {
        await using var context = NewContext();
        context.Reservas.Add(BudgetReservaWithDeclaredPax(adults: 2, children: 0, infants: 0));
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Pasajero Dos" });
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddPassengerAsync("1",
                new PassengerUpsertRequest("Pasajero Extra", null, null, null, null, null, null, null, null),
                CancellationToken.None));
        Assert.Contains("declara 2", ex.Message);
        // No se creo el tercero.
        Assert.Equal(2, await context.Passengers.CountAsync(p => p.ReservaId == 1));
    }

    // (4) Declarar 0 da mensaje claro al intentar cargar (guia a declarar primero, no al guard confuso).
    [Fact]
    public async Task AddPassenger_WithZeroDeclared_GuidesToDeclareCountFirst()
    {
        await using var context = NewContext();
        context.Reservas.Add(BudgetReservaWithDeclaredPax(adults: 0, children: 0, infants: 0));
        await context.SaveChangesAsync();

        var service = NewReservaService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.AddPassengerAsync("1",
                new PassengerUpsertRequest("Pasajero Uno", null, null, null, null, null, null, null, null),
                CancellationToken.None));
        Assert.Contains("declará la cantidad de pasajeros", ex.Message);
        Assert.Equal(0, await context.Passengers.CountAsync(p => p.ReservaId == 1));
    }

    // ===================== BUG 4: clasificador de excepciones de base =====================

    [Fact]
    public void Classifier_ConstraintViolation_IsNotDatabaseUnavailable()
    {
        // 23505 (unique_violation) = error de DATOS, NO conectividad. No debe rotularse como 503.
        var postgresEx = new PostgresException(
            messageText: "duplicate key value violates unique constraint",
            severity: "ERROR", invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation);
        var dbUpdateEx = new DbUpdateException("Error al guardar.", postgresEx);

        Assert.False(DatabaseExceptionClassifier.IsDatabaseUnavailable(dbUpdateEx));
    }

    [Fact]
    public void Classifier_NotNullViolation_IsNotDatabaseUnavailable()
    {
        // 23502 (not_null_violation) = columna NOT NULL sin valor = error de datos.
        var postgresEx = new PostgresException(
            messageText: "null value in column violates not-null constraint",
            severity: "ERROR", invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.NotNullViolation);
        var dbUpdateEx = new DbUpdateException("Error al guardar.", postgresEx);

        Assert.False(DatabaseExceptionClassifier.IsDatabaseUnavailable(dbUpdateEx));
    }

    [Fact]
    public void Classifier_StringDataRightTruncation_IsNotDatabaseUnavailable()
    {
        // 22001 (string_data_right_truncation) = valor mas largo que el length de la columna.
        var postgresEx = new PostgresException(
            messageText: "value too long for type character varying(20)",
            severity: "ERROR", invariantSeverity: "ERROR",
            sqlState: "22001");
        var dbUpdateEx = new DbUpdateException("Error al guardar.", postgresEx);

        Assert.False(DatabaseExceptionClassifier.IsDatabaseUnavailable(dbUpdateEx));
    }

    [Fact]
    public void Classifier_ConnectionException_IsDatabaseUnavailable()
    {
        // Clase 08 (connection_exception) = la base SI esta caida/inaccesible -> 503 correcto.
        var postgresEx = new PostgresException(
            messageText: "connection failure",
            severity: "FATAL", invariantSeverity: "FATAL",
            sqlState: "08006"); // connection_failure
        var dbUpdateEx = new DbUpdateException("Error de conexion.", postgresEx);

        Assert.True(DatabaseExceptionClassifier.IsDatabaseUnavailable(dbUpdateEx));
    }

    [Fact]
    public void Classifier_AdminShutdown_IsDatabaseUnavailable()
    {
        // 57P01 (admin_shutdown) = el servidor se esta apagando -> base no disponible.
        var postgresEx = new PostgresException(
            messageText: "terminating connection due to administrator command",
            severity: "FATAL", invariantSeverity: "FATAL",
            sqlState: "57P01");

        Assert.True(DatabaseExceptionClassifier.IsDatabaseUnavailable(postgresEx));
    }

    [Fact]
    public void Classifier_TimeoutException_IsDatabaseUnavailable()
    {
        Assert.True(DatabaseExceptionClassifier.IsDatabaseUnavailable(new TimeoutException()));
    }
}
