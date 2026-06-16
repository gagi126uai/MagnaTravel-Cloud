using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-031: gate de pasajeros nominales POR SERVICIO al resolver/emitir, y el aflojamiento del gate de
/// Presupuesto (Budget -> InManagement ya no exige nombres). Estos tests usan un ReservaService REAL con
/// el motor de estados enchufado, asi se verifica el efecto end-to-end mas importante: si falta cobertura
/// nominal, la operacion se RECHAZA y la reserva NO auto-confirma (cierra el bypass B1 del ADR §2.4).
/// </summary>
public class Adr031PassengerNominalGateTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static IMapper NewMapper()
        => new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

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

    // ReservaService real con motor de estados -> al resolver un servicio, la reserva puede auto-confirmar.
    private static ReservaService NewReservaService(AppDbContext context, IAuditService? auditService = null)
    {
        var mapper = NewMapper();
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        return new ReservaService(
            context, mapper, settings.Object,
            BuildUserManager(), NullLogger<ReservaService>.Instance,
            permissionResolver: null, httpContextAccessor: null, autoStateService: engine,
            auditService: auditService);
    }

    private static BookingService NewBookingService(AppDbContext context, ReservaService reservaService, IAuditService? auditService = null)
    {
        var mapper = NewMapper();
        var settings = new Mock<IOperationalFinanceSettingsService>();
        settings.Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OperationalFinanceSettings());
        var supplierService = new Mock<ISupplierService>();
        supplierService
            .Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<AssistanceBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaService,
            supplierService.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance,
            permissionResolver: null,
            httpContextAccessor: null,
            settingsService: settings.Object,
            auditService: auditService);
    }

    private static Reserva InManagementReserva(int adults = 1)
        => new()
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test",
            Status = EstadoReserva.InManagement,
            AdultCount = adults, ChildCount = 0, InfantCount = 0
        };

    // ===================== BYPASS B1: crear servicio ya resuelto sin nombres -> rechazado =====================

    [Fact]
    public async Task CreateHotel_AlreadyConfirmed_InManagement_WithoutNames_IsRejected_AndReservaStaysInManagement()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva()); // 1 declarado, sin pasajeros nominales
        var supplier = new Supplier { Id = 1, Name = "Prov" };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var req = new CreateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel", StarRating: 3,
            City: "Bariloche", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 1, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 100m, Notes: null,
            WorkflowStatus: "Confirmado");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => booking.CreateHotelAsync(1, req, CancellationToken.None));

        // No se persistio el hotel y la reserva NO auto-confirmo.
        Assert.Equal(0, await ctx.HotelBookings.CountAsync());
        Assert.Equal(EstadoReserva.InManagement, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public async Task CreateHotel_AlreadyConfirmed_WithLeadNamed_Succeeds_AndReservaAutoConfirms()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular" });
        var supplier = new Supplier { Id = 1, Name = "Prov" };
        ctx.Suppliers.Add(supplier);
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var req = new CreateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel", StarRating: 3,
            City: "Bariloche", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 1, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 100m, Notes: null,
            WorkflowStatus: "Confirmado");

        var dto = await booking.CreateHotelAsync(1, req, CancellationToken.None);

        Assert.Equal("Confirmado", dto.Status);
        // Unico servicio resuelto + nombres ok -> el motor auto-confirma la reserva.
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    // ===================== Emitir aereo: exige nombre + documento de todos =====================

    [Fact]
    public async Task MarkFlightTicketIssued_WithoutDocument_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva());
        // Titular con nombre pero SIN documento -> el aereo exige documento.
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 50, ReservaId = 1, Status = "HK", SalePrice = 500m, AirlineCode = "AR", FlightNumber = "1234"
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => booking.MarkFlightTicketIssuedAsync("1", "50", ticketNumber: null, CancellationToken.None));

        // El gate lanza ANTES de SaveChanges, asi que el store NO persiste la emision ni la
        // auto-confirmacion. Limpiamos el ChangeTracker para releer del store (en el mismo contexto la
        // entidad trackeada quedaria sucia en memoria, artefacto de InMemory; el contrato real es "no se
        // persiste").
        ctx.ChangeTracker.Clear();
        Assert.Null((await ctx.FlightSegments.FindAsync(50))!.TicketIssuedAt);
        Assert.Equal(EstadoReserva.InManagement, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public async Task MarkFlightTicketIssued_WithNameAndDocument_Succeeds_AndAutoConfirms()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva());
        ctx.Passengers.Add(new Passenger
        {
            Id = 1, ReservaId = 1, FullName = "Uno", DocumentType = "DNI", DocumentNumber = "11111111"
        });
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 50, ReservaId = 1, Status = "HK", SalePrice = 500m, AirlineCode = "AR", FlightNumber = "1234"
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var dto = await booking.MarkFlightTicketIssuedAsync("1", "50", ticketNumber: "TK-1", CancellationToken.None);

        Assert.NotNull(dto);
        Assert.NotNull((await ctx.FlightSegments.FindAsync(50))!.TicketIssuedAt);
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    // ===================== Asistencia: exige fecha de nacimiento =====================

    [Fact]
    public async Task ConfirmAssistance_WithoutBirthDate_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva());
        // Nombre + documento pero SIN fecha de nacimiento -> la asistencia lo exige.
        ctx.Passengers.Add(new Passenger
        {
            Id = 1, ReservaId = 1, FullName = "Uno", DocumentType = "DNI", DocumentNumber = "11111111", BirthDate = null
        });
        ctx.AssistanceBookings.Add(new AssistanceBooking
        {
            Id = 70, ReservaId = 1, Status = "Solicitado", SalePrice = 80m, PlanType = "Plan",
            ValidFrom = DateTime.UtcNow.Date.AddDays(10), ValidTo = DateTime.UtcNow.Date.AddDays(20),
            Adults = 1, Children = 0
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => booking.UpdateAssistanceStatusAsync("70", "Confirmado", confirmationNumber: null, CancellationToken.None));

        // Releer del store (ver nota en MarkFlightTicketIssued_WithoutDocument_IsRejected).
        ctx.ChangeTracker.Clear();
        Assert.Equal("Solicitado", (await ctx.AssistanceBookings.FindAsync(70))!.Status);
        Assert.Equal(EstadoReserva.InManagement, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    // ===================== Hotel/Traslado: confirman con solo titular (nombre) =====================

    [Fact]
    public async Task ConfirmHotelStatus_WithOnlyLeadNamed_Succeeds()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 2)); // 2 declarados
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular" }); // solo el titular
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var dto = await booking.UpdateHotelStatusAsync("10", "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.Equal("Confirmado", dto.Status);
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    // ===================== Bug 2026-06-15: confirmar "desde proveedor" con 1 pasajero cargado =====================
    // Reporte del dueno: reserva con 1 servicio y 1 pasajero con NOMBRE; al confirmar "desde proveedor" el
    // sistema rechazaba diciendo que "el pasajero no esta cargado". Estos tests fijan el comportamiento real
    // por tipo de servicio y verifican que el mensaje nombre EXACTAMENTE lo que falta.

    [Fact]
    public async Task ConfirmFlightFromProvider_OnePassengerWithNameOnly_RejectedWithClearDocumentMessage()
    {
        // El aereo exige nombre + documento. El dueno cargo solo el nombre -> rechazo. El mensaje NO debe
        // decir "el pasajero no esta cargado" ni "falta nombre": debe decir que falta el DOCUMENTO.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Cargado" }); // sin documento
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 50, ReservaId = 1, Status = "HK", SalePrice = 500m, AirlineCode = "AR", FlightNumber = "1234"
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => booking.MarkFlightTicketIssuedAsync("1", "50", ticketNumber: null, CancellationToken.None));

        Assert.Contains("documento", ex.Message);
        Assert.DoesNotContain("nombre", ex.Message); // el nombre SI estaba cargado
        Assert.DoesNotContain("sin pasajeros", ex.Message); // no es un set vacio
    }

    [Fact]
    public async Task ConfirmHotelFromProvider_OnePassengerWithNameOnly_Succeeds()
    {
        // Control positivo del mismo escenario: para HOTEL alcanza el titular con nombre, asi que confirmar
        // "desde proveedor" con 1 pasajero cargado (solo nombre) DEBE pasar. Esto es lo que el dueno esperaba.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva());
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Cargado" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var dto = await booking.UpdateHotelStatusAsync("10", "Confirmado", confirmationNumber: null, CancellationToken.None);

        Assert.Equal("Confirmado", dto.Status);
        Assert.Equal(EstadoReserva.Confirmed, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    // ===================== Editar a confirmado sin nombres -> rechazado =====================

    [Fact]
    public async Task EditHotelToConfirmed_WithoutNames_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva()); // 1 declarado, sin pasajeros
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        // Editar dejando el hotel "Confirmado" sin titular cargado -> rechazado.
        var req = new UpdateHotelRequest(
            SupplierId: null!, HotelName: "Hotel", StarRating: 3, City: "Bariloche", Country: "Argentina",
            CheckIn: DateTime.UtcNow.Date.AddDays(10), CheckOut: DateTime.UtcNow.Date.AddDays(12),
            RoomType: "Doble", MealPlan: "Desayuno", Adults: 1, Children: 0, Rooms: 1,
            ConfirmationNumber: null, NetCost: 100m, SalePrice: 200m, Commission: 100m,
            WorkflowStatus: "Confirmado");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => booking.UpdateHotelAsync(1, 10, req, CancellationToken.None));

        // Releer del store (ver nota en MarkFlightTicketIssued_WithoutDocument_IsRejected).
        ctx.ChangeTracker.Clear();
        Assert.Equal("Solicitado", (await ctx.HotelBookings.FindAsync(10))!.Status);
        Assert.Equal(EstadoReserva.InManagement, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    // ===================== Budget -> InManagement: avanza con cantidad SIN nombres =====================

    [Fact]
    public async Task BudgetToInManagement_AdvancesWithDeclaredCount_WithoutNominals()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test", Status = EstadoReserva.Budget,
            AdultCount = 2, ChildCount = 0, InfantCount = 0 // 2 declarados, 0 nominales
        });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);

        var advanced = await reservaService.UpdateStatusAsync(1, EstadoReserva.InManagement);

        Assert.Equal(EstadoReserva.InManagement, advanced.Status);
    }

    [Fact]
    public async Task BudgetToInManagement_WithZeroDeclared_IsRejected()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Test", Status = EstadoReserva.Budget,
            AdultCount = 0, ChildCount = 0, InfantCount = 0
        });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reservaService.UpdateStatusAsync(1, EstadoReserva.InManagement));
        Assert.Equal(EstadoReserva.Budget, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    // ===================== v2.1: defaults (cantidad propia NO achica el set) =====================

    [Fact]
    public async Task ConfirmPackage_NoAssignments_RequiresAllReservaNames_NotPackageAdults()
    {
        // Reserva de 3. Paquete con Adults=2 (default-ish) y SIN asignaciones. El set debe ser TODA la
        // reserva (3), no "los 2 del paquete": faltan nombres -> RECHAZO. Cierra M3.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 3));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Dos" });
        // El tercero existe pero SIN nombre -> el paquete (nombre de todos) debe rechazar.
        ctx.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "   " });
        ctx.PackageBookings.Add(new PackageBooking
        {
            Id = 20, ReservaId = 1, PackageName = "Paq", Status = "Solicitado", SalePrice = 300m,
            Adults = 2, Children = 0,
            StartDate = DateTime.UtcNow.Date.AddDays(10), EndDate = DateTime.UtcNow.Date.AddDays(15)
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => booking.UpdatePackageStatusAsync("20", "Confirmado", confirmationNumber: null, CancellationToken.None));

        ctx.ChangeTracker.Clear();
        Assert.Equal("Solicitado", (await ctx.PackageBookings.FindAsync(20))!.Status);
        Assert.Equal(EstadoReserva.InManagement, (await ctx.Reservas.FindAsync(1))!.Status);
    }

    [Fact]
    public async Task ConfirmHotel_NoAssignments_Adults2InReservaOfThree_RequiresReservaLead_NotTreatedAsForTwo()
    {
        // Hotel con Adults=2 (default) en reserva de 3, SIN asignaciones. El gate de hotel pide el TITULAR
        // de la reserva (no trata al hotel como "para 2"). Con el titular nombrado, pasa.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 3));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            Adults = 2, Children = 0,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var dto = await booking.UpdateHotelStatusAsync("10", "Confirmado", confirmationNumber: null, CancellationToken.None);
        Assert.Equal("Confirmado", dto.Status);
    }

    // ===================== v2.1: excursion 2 de 3 (set explicito chico) =====================

    [Fact]
    public async Task ConfirmPackage_AssignedToTwoAdults_RequiresOnlyThem_NotTheMinor()
    {
        // Reserva 2A+1C. Paquete (excursion) ASIGNADO a los 2 adultos (nombrados). El menor NO tiene nombre
        // pero NO esta en el set -> el gate del paquete pasa pidiendo solo a los 2 adultos.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 3));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Adulto1" });
        ctx.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Adulto2" });
        ctx.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "   " }); // menor sin nombre
        ctx.PackageBookings.Add(new PackageBooking
        {
            Id = 20, ReservaId = 1, PackageName = "Excursion", Status = "Solicitado", SalePrice = 150m,
            Adults = 2, Children = 0,
            StartDate = DateTime.UtcNow.Date.AddDays(10), EndDate = DateTime.UtcNow.Date.AddDays(11)
        });
        // Asignacion explicita del subconjunto: los 2 adultos.
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment { PassengerId = 1, ServiceType = AssignmentServiceType.Package, ServiceId = 20 });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment { PassengerId = 2, ServiceType = AssignmentServiceType.Package, ServiceId = 20 });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var dto = await booking.UpdatePackageStatusAsync("20", "Confirmado", confirmationNumber: null, CancellationToken.None);
        Assert.Equal("Confirmado", dto.Status);
    }

    [Fact]
    public async Task EmitFlight_AssignedToOneOfThree_EmitsForThatSetOnly()
    {
        // Bypass B1 (accion explicita del agente): aereo asignado a 1 de 3 pasajeros. Solo ese tiene
        // nombre+documento; los otros 2 estan vacios. Como el set = ese 1, la emision PROCEDE.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 3));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Viajero", DocumentType = "DNI", DocumentNumber = "11111111" });
        ctx.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "   " });
        ctx.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "   " });
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 50, ReservaId = 1, Status = "HK", SalePrice = 500m, AirlineCode = "AR", FlightNumber = "1234"
        });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment { PassengerId = 1, ServiceType = AssignmentServiceType.Flight, ServiceId = 50 });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        var dto = await booking.MarkFlightTicketIssuedAsync("1", "50", ticketNumber: "TK-1", CancellationToken.None);
        Assert.NotNull(dto);
        Assert.NotNull((await ctx.FlightSegments.FindAsync(50))!.TicketIssuedAt);
    }

    [Fact]
    public async Task EmitFlight_AssignedToOneIncompletePassenger_IsRejected()
    {
        // Aereo asignado a 1 pasajero que NO tiene documento -> el set (ese 1) esta incompleto -> RECHAZO.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 2));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "SinDoc" }); // sin documento
        ctx.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Otro", DocumentType = "DNI", DocumentNumber = "22222222" });
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 50, ReservaId = 1, Status = "HK", SalePrice = 500m, AirlineCode = "AR", FlightNumber = "1234"
        });
        // Asignado SOLO al pasajero sin documento.
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment { PassengerId = 1, ServiceType = AssignmentServiceType.Flight, ServiceId = 50 });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => booking.MarkFlightTicketIssuedAsync("1", "50", ticketNumber: null, CancellationToken.None));
        ctx.ChangeTracker.Clear();
        Assert.Null((await ctx.FlightSegments.FindAsync(50))!.TicketIssuedAt);
    }

    // ===================== v2.1: M1 — limpieza de asignaciones huerfanas + reuso de Id =====================

    [Fact]
    public async Task DeleteHotel_RemovesItsAssignments_NoOrphans()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 2));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment { PassengerId = 1, ServiceType = AssignmentServiceType.Hotel, ServiceId = 10 });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        await booking.DeleteHotelAsync(1, 10, CancellationToken.None);

        ctx.ChangeTracker.Clear();
        Assert.Equal(0, await ctx.HotelBookings.CountAsync());
        // No quedan asignaciones huerfanas con ese (ServiceType, ServiceId).
        Assert.Equal(0, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10));
    }

    [Fact]
    public async Task DeleteHotelThenReuseId_NewServiceDoesNotInheritDeadSet()
    {
        // Borramos un hotel (con asignacion). Creamos otro hotel REUSANDO el mismo ServiceId 10. El nuevo
        // NO debe heredar la asignacion del muerto: su set = default (toda la reserva). Como el gate del
        // hotel pide solo el titular y la reserva NO tiene titular nombrado, intentar confirmarlo RECHAZA
        // pidiendo a TODA la reserva (no al pasajero del hotel muerto).
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 2));
        // Pasajero 1 (estaba asignado al hotel muerto) tiene nombre; el resto de la reserva no importa para
        // este escenario: lo clave es que la asignacion NO sobreviva.
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "ViejoTitular" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "HotelMuerto", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment { PassengerId = 1, ServiceType = AssignmentServiceType.Hotel, ServiceId = 10 });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);
        var booking = NewBookingService(ctx, reservaService);

        await booking.DeleteHotelAsync(1, 10, CancellationToken.None);
        ctx.ChangeTracker.Clear();

        // Crear un hotel nuevo que reusa el Id 10 (lo forzamos para reproducir el reuso de Id).
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "HotelNuevo", Status = "Solicitado", SalePrice = 250m,
            CheckIn = DateTime.UtcNow.Date.AddDays(20), CheckOut = DateTime.UtcNow.Date.AddDays(22)
        });
        await ctx.SaveChangesAsync();
        ctx.ChangeTracker.Clear();

        // No hay asignaciones para el (Hotel, 10) reusado -> set por defecto = toda la reserva.
        var orphanCount = await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10);
        Assert.Equal(0, orphanCount);
    }

    // ===================== v2.1: auditoria de asignaciones =====================

    [Fact]
    public async Task CreateAssignment_LogsPassengerAssignedToService_WithoutDocumentNumber()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 2));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno", DocumentType = "DNI", DocumentNumber = "11111111" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();
        var passengerPublicId = (await ctx.Passengers.FindAsync(1))!.PublicId;
        var hotelPublicId = (await ctx.HotelBookings.FindAsync(10))!.PublicId;

        var audit = new Mock<IAuditService>();
        var reservaService = NewReservaService(ctx, audit.Object);

        var reservaPublicId = (await ctx.Reservas.FindAsync(1))!.PublicId.ToString();
        await reservaService.CreateAssignmentAsync(reservaPublicId, new TravelApi.Application.DTOs.CreatePassengerAssignmentRequest(
            PassengerPublicIdOrLegacyId: passengerPublicId.ToString(),
            ServiceType: AssignmentServiceType.Hotel,
            ServicePublicIdOrLegacyId: hotelPublicId.ToString(),
            RoomNumber: null, SeatNumber: null, Notes: null), CancellationToken.None);

        audit.Verify(a => a.LogBusinessEventAsync(
            TravelApi.Application.Constants.AuditActions.PassengerAssignedToService,
            TravelApi.Application.Constants.AuditActions.PassengerServiceAssignmentEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => details != null && !details.Contains("11111111")),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task DeleteServiceWithAssignments_LogsCascadeUnassign()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 1));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment { PassengerId = 1, ServiceType = AssignmentServiceType.Hotel, ServiceId = 10 });
        await ctx.SaveChangesAsync();

        var audit = new Mock<IAuditService>();
        var reservaService = NewReservaService(ctx, audit.Object);
        var booking = NewBookingService(ctx, reservaService, audit.Object);

        await booking.DeleteHotelAsync(1, 10, CancellationToken.None);

        // I-ATOM: la cascada audita via StageBusinessEvent (sin SaveChanges propio), NO LogBusinessEventAsync.
        // Asi el alta del audit entra en el mismo SaveChanges que el borrado del servicio = atomico.
        audit.Verify(a => a.StageBusinessEvent(
            TravelApi.Application.Constants.AuditActions.PassengerUnassignedFromServiceByDelete,
            TravelApi.Application.Constants.AuditActions.PassengerServiceAssignmentEntityName,
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
        // Y NUNCA por el camino que guarda en transaccion separada.
        audit.Verify(a => a.LogBusinessEventAsync(
            TravelApi.Application.Constants.AuditActions.PassengerUnassignedFromServiceByDelete,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ===================== T2: segundo path de borrado (ReservaService.RemoveServiceAsync) =====================

    [Fact]
    public async Task RemoveServiceAsync_GenericService_CleansUpAssignments()
    {
        // Path dual del borrado: ReservaService.RemoveServiceAsync (no el tipado de BookingService).
        // El generico tambien debe limpiar sus PassengerServiceAssignment al borrarse.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 1));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.Servicios.Add(new ServicioReserva
        {
            Id = 50, ReservaId = 1, Description = "Excursion", Status = "Solicitado", SalePrice = 100m
        });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment
        {
            PassengerId = 1, ServiceType = AssignmentServiceType.Generic, ServiceId = 50
        });
        await ctx.SaveChangesAsync();

        var reservaService = NewReservaService(ctx);

        await reservaService.RemoveServiceAsync(50, CancellationToken.None);

        ctx.ChangeTracker.Clear();
        Assert.Equal(0, await ctx.Servicios.CountAsync());
        Assert.Equal(0, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Generic && a.ServiceId == 50));
    }

    [Fact]
    public async Task RemoveServiceAsync_TypedFlight_CleansUpAssignments_AndAuditsViaStage()
    {
        // El mismo path dual, pero para un servicio TIPADO (aereo). Verifica ademas que la auditoria de la
        // cascada va por StageBusinessEvent (atomicidad), igual que en el path de BookingService.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 1));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 60, ReservaId = 1, AirlineCode = "AR", FlightNumber = "1234",
            Status = "Solicitado", SalePrice = 300m
        });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment
        {
            PassengerId = 1, ServiceType = AssignmentServiceType.Flight, ServiceId = 60
        });
        await ctx.SaveChangesAsync();

        var audit = new Mock<IAuditService>();
        var reservaService = NewReservaService(ctx, audit.Object);

        await reservaService.RemoveServiceAsync(60, CancellationToken.None);

        ctx.ChangeTracker.Clear();
        Assert.Equal(0, await ctx.FlightSegments.CountAsync());
        Assert.Equal(0, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Flight && a.ServiceId == 60));

        audit.Verify(a => a.StageBusinessEvent(
            TravelApi.Application.Constants.AuditActions.PassengerUnassignedFromServiceByDelete,
            TravelApi.Application.Constants.AuditActions.PassengerServiceAssignmentEntityName,
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveServiceAsync_TypedFlight_AtomicCleanup_WithRealAuditOnSameContext()
    {
        // Sella el punto I-ATOM con un AuditService REAL sobre el MISMO contexto: el alta del AuditLog de la
        // cascada se STAGEA y solo se materializa en el SaveChanges del borrado. Tras un borrado exitoso,
        // tanto la baja de la asignacion como el AuditLog quedan persistidos en la misma operacion.
        await using var ctx = NewContext();
        ctx.Reservas.Add(InManagementReserva(adults: 1));
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.FlightSegments.Add(new FlightSegment
        {
            Id = 70, ReservaId = 1, AirlineCode = "AR", FlightNumber = "9999",
            Status = "Solicitado", SalePrice = 300m
        });
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment
        {
            PassengerId = 1, ServiceType = AssignmentServiceType.Flight, ServiceId = 70
        });
        await ctx.SaveChangesAsync();

        // AuditService REAL (no mock) sobre el mismo AppDbContext via Repository<AuditLog>.
        var realAudit = new AuditService(
            new Repository<AuditLog>(ctx),
            NullLogger<AuditService>.Instance);
        var reservaService = NewReservaService(ctx, realAudit);

        await reservaService.RemoveServiceAsync(70, CancellationToken.None);

        ctx.ChangeTracker.Clear();
        // Asignacion borrada Y AuditLog de la cascada persistido (mismo SaveChanges) = consistente.
        Assert.Equal(0, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Flight && a.ServiceId == 70));
        Assert.Equal(1, await ctx.Set<AuditLog>()
            .CountAsync(l => l.Action == TravelApi.Application.Constants.AuditActions.PassengerUnassignedFromServiceByDelete));
    }

    // ===================== T3: ownership de la cobertura nominal por servicio =====================

    [Fact]
    public async Task GetServiceNominalCoverage_ServiceFromAnotherReserva_IsRejected()
    {
        // Cross-reserva: el hotel pertenece a la reserva 1, pero se pide su cobertura nominal bajo la
        // reserva 2. La validacion serviceBelongsToReserva debe rechazar (no se puede mirar la composicion
        // de pasajeros de un servicio ajeno a la reserva del request).
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "ReservaDuena",
            Status = EstadoReserva.InManagement, AdultCount = 1
        });
        ctx.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "F-2", Name = "OtraReserva",
            Status = EstadoReserva.InManagement, AdultCount = 1
        });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "HotelDeReserva1", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var otherReservaPublicId = (await ctx.Reservas.FindAsync(2))!.PublicId.ToString();
        var hotelPublicId = (await ctx.HotelBookings.FindAsync(10))!.PublicId.ToString();

        var reservaService = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reservaService.GetServiceNominalCoverageAsync(
                otherReservaPublicId, AssignmentServiceType.Hotel, hotelPublicId, CancellationToken.None));
        Assert.Contains("no pertenece a esta reserva", ex.Message);
    }

    [Fact]
    public async Task GetServiceNominalCoverage_ServiceFromSameReserva_Succeeds()
    {
        // Control positivo del mismo gate: pedir la cobertura del hotel bajo SU reserva si funciona,
        // para asegurar que el rechazo cross-reserva no es un falso positivo del setup.
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "ReservaDuena",
            Status = EstadoReserva.InManagement, AdultCount = 1
        });
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Titular" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "HotelDeReserva1", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaPublicId = (await ctx.Reservas.FindAsync(1))!.PublicId.ToString();
        var hotelPublicId = (await ctx.HotelBookings.FindAsync(10))!.PublicId.ToString();

        var reservaService = NewReservaService(ctx);

        var dto = await reservaService.GetServiceNominalCoverageAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId, CancellationToken.None);

        Assert.Equal(AssignmentServiceType.Hotel, dto.ServiceType);
        Assert.Equal(10, dto.ServiceId);
        Assert.Equal(1, dto.ReservaPassengerCount);
    }

    // ===================== T4: reemplazo total atomico del set (PUT .../assignments) =====================

    /// <summary>
    /// Setup comun de las pruebas de reemplazo: una reserva InManagement con 3 pasajeros + 1 hotel.
    /// Devuelve los publicIds (reserva + hotel + los 3 pasajeros por Id) para armar los requests.
    /// </summary>
    private static async Task<(string reservaPublicId, string hotelPublicId, Guid p1, Guid p2, Guid p3)>
        SeedReservaWith3PaxAndHotelAsync(AppDbContext ctx)
    {
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Reserva",
            Status = EstadoReserva.InManagement, AdultCount = 3
        });
        ctx.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Uno" });
        ctx.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Dos" });
        ctx.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "Tres" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();

        var reservaPublicId = (await ctx.Reservas.FindAsync(1))!.PublicId.ToString();
        var hotelPublicId = (await ctx.HotelBookings.FindAsync(10))!.PublicId.ToString();
        var p1 = (await ctx.Passengers.FindAsync(1))!.PublicId;
        var p2 = (await ctx.Passengers.FindAsync(2))!.PublicId;
        var p3 = (await ctx.Passengers.FindAsync(3))!.PublicId;
        return (reservaPublicId, hotelPublicId, p1, p2, p3);
    }

    [Fact]
    public async Task ReplaceAssignments_ToSubset_CreatesOnlyThose()
    {
        // (a) Reemplazo a un subconjunto estricto (2 de 3) -> crea exactamente esas 2 asignaciones.
        await using var ctx = NewContext();
        var (reservaPublicId, hotelPublicId, p1, p2, _) = await SeedReservaWith3PaxAndHotelAsync(ctx);
        var reservaService = NewReservaService(ctx);

        var dto = await reservaService.ReplaceServiceAssignmentsAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId,
            new ReplaceServiceAssignmentsRequest(new[] { p1.ToString(), p2.ToString() }),
            CancellationToken.None);

        ctx.ChangeTracker.Clear();
        var persisted = await ctx.PassengerServiceAssignments
            .Where(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10)
            .Select(a => a.PassengerId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { 1, 2 }, persisted);

        // El DTO devuelto refleja el subconjunto (set = 2 de 3, con asignaciones explicitas).
        Assert.True(dto.HasExplicitAssignments);
        Assert.Equal(2, dto.ServiceSetCount);
        Assert.Equal(3, dto.ReservaPassengerCount);
    }

    [Fact]
    public async Task ReplaceAssignments_ToAll_LeavesZeroAssignments_Normalization()
    {
        // (b) Reemplazo a "todos" (los 3 pasajeros) -> NO crea asignaciones (invariante "todos = sin
        // asignaciones"). El set sigue siendo toda la reserva, pero implicito (HasExplicitAssignments=false).
        await using var ctx = NewContext();
        var (reservaPublicId, hotelPublicId, p1, p2, p3) = await SeedReservaWith3PaxAndHotelAsync(ctx);
        var reservaService = NewReservaService(ctx);

        var dto = await reservaService.ReplaceServiceAssignmentsAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId,
            new ReplaceServiceAssignmentsRequest(new[] { p1.ToString(), p2.ToString(), p3.ToString() }),
            CancellationToken.None);

        ctx.ChangeTracker.Clear();
        Assert.Equal(0, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10));

        Assert.False(dto.HasExplicitAssignments);
        Assert.Equal(3, dto.ServiceSetCount); // set implicito = toda la reserva
        Assert.Equal(3, dto.ReservaPassengerCount);
    }

    [Fact]
    public async Task ReplaceAssignments_EmptyList_LeavesZeroAssignments_Normalization()
    {
        // (b-bis) Lista vacia tambien normaliza a "todos = sin asignaciones": parte de un set previo de 1
        // y, tras reemplazar por lista vacia, no queda ninguna asignacion.
        await using var ctx = NewContext();
        var (reservaPublicId, hotelPublicId, p1, _, _) = await SeedReservaWith3PaxAndHotelAsync(ctx);
        ctx.PassengerServiceAssignments.Add(new PassengerServiceAssignment
        {
            PassengerId = 1, ServiceType = AssignmentServiceType.Hotel, ServiceId = 10
        });
        await ctx.SaveChangesAsync();
        var reservaService = NewReservaService(ctx);

        var dto = await reservaService.ReplaceServiceAssignmentsAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId,
            new ReplaceServiceAssignmentsRequest(Array.Empty<string>()),
            CancellationToken.None);

        ctx.ChangeTracker.Clear();
        Assert.Equal(0, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10));
        Assert.False(dto.HasExplicitAssignments);
    }

    [Fact]
    public async Task ReplaceAssignments_CalledTwiceWithSameSet_IsIdempotent()
    {
        // (c) Idempotencia: llamar dos veces con el mismo subconjunto deja el mismo estado final (mismas 2
        // asignaciones, no se duplican). El reemplazo total borra y recrea, asi que el conteo se mantiene.
        await using var ctx = NewContext();
        var (reservaPublicId, hotelPublicId, p1, p2, _) = await SeedReservaWith3PaxAndHotelAsync(ctx);
        var reservaService = NewReservaService(ctx);
        var request = new ReplaceServiceAssignmentsRequest(new[] { p1.ToString(), p2.ToString() });

        await reservaService.ReplaceServiceAssignmentsAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId, request, CancellationToken.None);
        var dto = await reservaService.ReplaceServiceAssignmentsAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId, request, CancellationToken.None);

        ctx.ChangeTracker.Clear();
        var persisted = await ctx.PassengerServiceAssignments
            .Where(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10)
            .Select(a => a.PassengerId)
            .OrderBy(id => id)
            .ToListAsync();
        Assert.Equal(new[] { 1, 2 }, persisted);
        Assert.Equal(2, dto.ServiceSetCount);
    }

    [Fact]
    public async Task ReplaceAssignments_AtomicWithRealAuditOnSameContext()
    {
        // (d) Atomicidad con AuditService REAL sobre el MISMO contexto: tras un reemplazo exitoso, las nuevas
        // asignaciones Y el AuditLog del reemplazo quedan persistidos en la misma operacion (un SaveChanges).
        await using var ctx = NewContext();
        var (reservaPublicId, hotelPublicId, p1, p2, _) = await SeedReservaWith3PaxAndHotelAsync(ctx);

        var realAudit = new AuditService(
            new Repository<AuditLog>(ctx),
            NullLogger<AuditService>.Instance);
        var reservaService = NewReservaService(ctx, realAudit);

        await reservaService.ReplaceServiceAssignmentsAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId,
            new ReplaceServiceAssignmentsRequest(new[] { p1.ToString(), p2.ToString() }),
            CancellationToken.None);

        ctx.ChangeTracker.Clear();
        Assert.Equal(2, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10));
        Assert.Equal(1, await ctx.Set<AuditLog>()
            .CountAsync(l => l.Action == TravelApi.Application.Constants.AuditActions.PassengerAssignmentsReplaced));
    }

    [Fact]
    public async Task ReplaceAssignments_PassengerFromAnotherReserva_IsRejected()
    {
        // (e1) Ownership: un pasajero de OTRA reserva en el body -> rechazo (no se mezclan sets entre reservas).
        await using var ctx = NewContext();
        var (reservaPublicId, hotelPublicId, p1, _, _) = await SeedReservaWith3PaxAndHotelAsync(ctx);
        ctx.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "F-2", Name = "Otra", Status = EstadoReserva.InManagement, AdultCount = 1
        });
        ctx.Passengers.Add(new Passenger { Id = 99, ReservaId = 2, FullName = "Ajeno" });
        await ctx.SaveChangesAsync();
        var foreignPassenger = (await ctx.Passengers.FindAsync(99))!.PublicId;
        var reservaService = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reservaService.ReplaceServiceAssignmentsAsync(
                reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId,
                new ReplaceServiceAssignmentsRequest(new[] { p1.ToString(), foreignPassenger.ToString() }),
                CancellationToken.None));
        Assert.Contains("no pertenece a esta reserva", ex.Message);

        // Y no toco nada (el rechazo es ANTES de cualquier escritura).
        ctx.ChangeTracker.Clear();
        Assert.Equal(0, await ctx.PassengerServiceAssignments
            .CountAsync(a => a.ServiceType == AssignmentServiceType.Hotel && a.ServiceId == 10));
    }

    [Fact]
    public async Task ReplaceAssignments_ServiceFromAnotherReserva_IsRejected()
    {
        // (e2) Ownership: el servicio (hotel) pertenece a la reserva 1, pero se pide reemplazar su set bajo
        // la reserva 2 -> rechazo (mismo gate serviceBelongsToReserva que el GET de nominal-coverage).
        await using var ctx = NewContext();
        var (_, hotelPublicId, _, _, _) = await SeedReservaWith3PaxAndHotelAsync(ctx);
        ctx.Reservas.Add(new Reserva
        {
            Id = 2, NumeroReserva = "F-2", Name = "Otra", Status = EstadoReserva.InManagement, AdultCount = 1
        });
        ctx.Passengers.Add(new Passenger { Id = 99, ReservaId = 2, FullName = "PaxR2" });
        await ctx.SaveChangesAsync();
        var otherReservaPublicId = (await ctx.Reservas.FindAsync(2))!.PublicId.ToString();
        var paxR2 = (await ctx.Passengers.FindAsync(99))!.PublicId;
        var reservaService = NewReservaService(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => reservaService.ReplaceServiceAssignmentsAsync(
                otherReservaPublicId, AssignmentServiceType.Hotel, hotelPublicId,
                new ReplaceServiceAssignmentsRequest(new[] { paxR2.ToString() }),
                CancellationToken.None));
        Assert.Contains("no pertenece a esta reserva", ex.Message);
    }

    [Fact]
    public async Task ReplaceAssignments_AuditDetail_HasNoDocumentNumber()
    {
        // (f) El detalle de auditoria del reemplazo NO contiene el numero de documento del pasajero.
        await using var ctx = NewContext();
        ctx.Reservas.Add(new Reserva
        {
            Id = 1, NumeroReserva = "F-1", Name = "Reserva",
            Status = EstadoReserva.InManagement, AdultCount = 2
        });
        ctx.Passengers.Add(new Passenger
        {
            Id = 1, ReservaId = 1, FullName = "Uno", DocumentType = "DNI", DocumentNumber = "11111111"
        });
        ctx.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Dos" });
        ctx.HotelBookings.Add(new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado", SalePrice = 200m,
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12)
        });
        await ctx.SaveChangesAsync();
        var reservaPublicId = (await ctx.Reservas.FindAsync(1))!.PublicId.ToString();
        var hotelPublicId = (await ctx.HotelBookings.FindAsync(10))!.PublicId.ToString();
        var p1 = (await ctx.Passengers.FindAsync(1))!.PublicId;

        var audit = new Mock<IAuditService>();
        var reservaService = NewReservaService(ctx, audit.Object);

        await reservaService.ReplaceServiceAssignmentsAsync(
            reservaPublicId, AssignmentServiceType.Hotel, hotelPublicId,
            new ReplaceServiceAssignmentsRequest(new[] { p1.ToString() }), // subconjunto -> hay asignacion + audit
            CancellationToken.None);

        // STAGE (no Log): entra en el mismo SaveChanges = atomico. Y el detail no lleva el documento.
        audit.Verify(a => a.StageBusinessEvent(
            TravelApi.Application.Constants.AuditActions.PassengerAssignmentsReplaced,
            TravelApi.Application.Constants.AuditActions.PassengerServiceAssignmentEntityName,
            It.IsAny<string>(),
            It.Is<string>(details => details != null && !details.Contains("11111111")),
            It.IsAny<string>(), It.IsAny<string?>()),
            Times.Once);
    }
}
