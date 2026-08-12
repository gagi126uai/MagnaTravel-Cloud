using System.Collections.Generic;
using System.Security.Claims;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Constants;
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
/// Obra "PDF de presupuesto" (decisión firmada del dueño, 2026-08-11/12), TANDA 1: guard de estrellas
/// del hotel (1..5) y "resolver grupo de opciones" (decisión #1, A/B/C). Mismo harness que
/// <c>ServiceQuantityGuardTests</c> (BookingService con InMemory + mocks mínimos).
/// </summary>
public class OptionGroupBookingServiceTests
{
    private static readonly DateTime Inicio = DateTime.SpecifyKind(new DateTime(2026, 9, 10, 10, 0, 0), DateTimeKind.Utc);
    private static readonly DateTime Fin = Inicio.AddDays(4);

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

    private static BookingService CreateBookingService(AppDbContext context, Mock<IAuditService>? auditServiceMock = null)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>())).Returns(Task.CompletedTask);
        reservaService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService.Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

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
            .ReturnsAsync(new OperationalFinanceSettings());

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
            CreateMapper(),
            NullLogger<BookingService>.Instance,
            resolver.Object,
            accessor,
            settings.Object,
            auditServiceMock?.Object);
    }

    private static async Task<(Reserva reserva, Supplier supplier)> SeedAsync(AppDbContext context)
    {
        var supplier = new Supplier { Id = 1, Name = "Operador Test" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "2026-OPC1", Name = "Reserva opciones", Status = EstadoReserva.Budget };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (reserva, supplier);
    }

    private static CreateHotelRequest BuildCreateHotel(string supplierPublicId, int? starRating, string? optionGroup = null, string? optionLabel = null)
        => new(
            SupplierId: supplierPublicId, HotelName: "Hotel Test", StarRating: starRating, City: "Bariloche", Country: "Argentina",
            CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Notes: null, Currency: "ARS",
            OptionGroup: optionGroup, OptionLabel: optionLabel);

    // =====================================================================
    // Estrellas del hotel: 1..5, null = no informado
    // =====================================================================

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public async Task CreateHotel_WithStarRatingOutOfRange_IsRejected(int starRating)
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), starRating), CancellationToken.None));

        Assert.Equal("Las estrellas van de 1 a 5.", error.Message);
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(null)]
    public async Task CreateHotel_WithValidStarRatingOrNull_IsAccepted(int? starRating)
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var dto = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), starRating), CancellationToken.None);

        Assert.Equal(starRating, dto.StarRating);
    }

    [Fact]
    public async Task UpdateHotel_WithStarRatingOutOfRange_IsRejected_AndNotSaved()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 80, ReservaId = reserva.Id, SupplierId = supplier.Id,
            HotelName = "Hotel Test", City = "Bariloche", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = Inicio, CheckOut = Fin, Adults = 2, Children = 0, Rooms = 1,
            Status = "Solicitado", SalePrice = 1000m, StarRating = 3
        });
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Test", StarRating: 9, City: "Bariloche",
            Country: "Argentina", CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Status: "Solicitado", Notes: null);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateHotelAsync(reserva.Id, 80, request, CancellationToken.None));

        Assert.Equal("Las estrellas van de 1 a 5.", error.Message);
        Assert.Equal(3, (await context.HotelBookings.AsNoTracking().SingleAsync()).StarRating);
    }

    // =====================================================================
    // Resolver grupo de opciones A/B/C
    // =====================================================================

    [Fact]
    public async Task ResolveOptionGroupAsync_DeletesLosers_KeepsWinner_AndAudits()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var auditMock = new Mock<IAuditService>();
        var service = CreateBookingService(context, auditMock);

        var winner = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, "hoteles", "A"), CancellationToken.None);
        var loser = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 3, "hoteles", "B"), CancellationToken.None);

        // Fix B2 (review de seguridad, 2026-08-12): capturamos el JSON real que se manda a auditar.
        string? capturedDetails = null;
        auditMock
            .Setup(a => a.LogBusinessEventAsync(
                AuditActions.OptionGroupResolved, AuditActions.ReservaEntityName, It.IsAny<string>(),
                It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, string, string?, CancellationToken>(
                (_, _, _, details, _, _, _) => capturedDetails = details)
            .Returns(Task.CompletedTask);

        var result = await service.ResolveOptionGroupAsync(
            reserva.Id.ToString(),
            new ResolveOptionGroupRequest("hoteles", AssignmentServiceType.Hotel, winner.PublicId.ToString()),
            CancellationToken.None);

        Assert.Equal(winner.PublicId, result.WinnerServicePublicId);
        Assert.Single(result.RemovedServices);
        // El borrado de un hotel es FISICO (BookingService.DeleteHotelAsync), no soft-delete: solo el
        // ganador queda en la tabla.
        Assert.Equal(1, await context.HotelBookings.CountAsync());

        var stillThere = await context.HotelBookings.AsNoTracking().SingleOrDefaultAsync(h => h.PublicId == winner.PublicId);
        Assert.NotNull(stillThere);
        Assert.Null(await context.HotelBookings.AsNoTracking().SingleOrDefaultAsync(h => h.PublicId == loser.PublicId));

        // Fix B2: el rastro auditable tiene que poder ubicar EXACTAMENTE que fila se borro (el borrado es
        // fisico) y CUANTO valia — sin esto, el audit log queda mudo sobre una venta borrada de verdad.
        Assert.NotNull(capturedDetails);
        Assert.Contains(loser.PublicId.ToString(), capturedDetails);
        Assert.Contains(winner.PublicId.ToString(), capturedDetails);
        Assert.Contains("1000", capturedDetails); // SalePrice del perdedor (BuildCreateHotel)
        Assert.Contains("600", capturedDetails); // NetCost del perdedor
        Assert.Contains("ARS", capturedDetails);
    }

    [Fact]
    public async Task ResolveOptionGroupAsync_AlreadyResolved_IsIdempotent_NoAudit()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var auditMock = new Mock<IAuditService>();
        var service = CreateBookingService(context, auditMock);

        // Solo queda una opcion viva en el grupo (como si ya se hubiera resuelto antes).
        var winner = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, "hoteles", "A"), CancellationToken.None);

        var result = await service.ResolveOptionGroupAsync(
            reserva.Id.ToString(),
            new ResolveOptionGroupRequest("hoteles", AssignmentServiceType.Hotel, winner.PublicId.ToString()),
            CancellationToken.None);

        Assert.Empty(result.RemovedServices);
        Assert.Equal(1, await context.HotelBookings.CountAsync());
        auditMock.Verify(a => a.LogBusinessEventAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveOptionGroupAsync_WinnerNotInGroup_IsRejected()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);

        var outsider = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4), CancellationToken.None); // sin grupo
        await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 3, "hoteles", "A"), CancellationToken.None);
        await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 3, "hoteles", "B"), CancellationToken.None);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveOptionGroupAsync(
                reserva.Id.ToString(),
                new ResolveOptionGroupRequest("hoteles", AssignmentServiceType.Hotel, outsider.PublicId.ToString()),
                CancellationToken.None));

        Assert.Contains("no pertenece a este grupo", error.Message);
        // Nada se borro: el grupo sigue con sus 2 alternativas vivas.
        Assert.Equal(3, await context.HotelBookings.CountAsync());
    }

    // =====================================================================
    // Fix B1(a) (review de seguridad, 2026-08-12): opciones A/B/C SOLO en Presupuesto
    // =====================================================================

    [Theory]
    [InlineData(EstadoReserva.InManagement)]
    [InlineData(EstadoReserva.Confirmed)]
    public async Task CreateHotel_WithOptionGroup_AfterBudget_IsRejected(string reservaStatusPastBudget)
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        reserva.Status = reservaStatusPastBudget;
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, "hoteles", "A"), CancellationToken.None));

        Assert.Equal(BookingService.OptionGroupOnlyDuringPresupuestoMessage, error.Message);
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Theory]
    [InlineData(EstadoReserva.Quotation)]
    [InlineData(EstadoReserva.Budget)]
    public async Task CreateHotel_WithOptionGroup_DuringPresupuesto_IsAccepted(string presupuestoStatus)
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        reserva.Status = presupuestoStatus;
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var dto = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, "hoteles", "A"), CancellationToken.None);

        Assert.Equal("hoteles", dto.OptionGroup);
    }

    [Fact]
    public async Task UpdateHotel_SettingOptionGroup_AfterBudget_IsRejected_AndNotSaved()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 90, ReservaId = reserva.Id, SupplierId = supplier.Id,
            HotelName = "Hotel Test", City = "Bariloche", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = Inicio, CheckOut = Fin, Adults = 2, Children = 0, Rooms = 1,
            Status = "Solicitado", SalePrice = 1000m
        });
        reserva.Status = EstadoReserva.InManagement; // ya paso Presupuesto
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Test", StarRating: 4, City: "Bariloche",
            Country: "Argentina", CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Status: "Solicitado", Notes: null,
            OptionGroup: "hoteles", OptionLabel: "A");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateHotelAsync(reserva.Id, 90, request, CancellationToken.None));

        Assert.Equal(BookingService.OptionGroupOnlyDuringPresupuestoMessage, error.Message);
        Assert.Null((await context.HotelBookings.AsNoTracking().SingleAsync()).OptionGroup);
    }

    [Fact]
    public async Task UpdateHotel_WithoutTouchingOptionGroup_AfterBudget_IsStillAllowed()
    {
        // Anti-sobre-freno: editar CUALQUIER OTRO campo de un hotel sin grupo, en una reserva que ya paso
        // Presupuesto, sigue funcionando igual que siempre — el candado nuevo SOLO se dispara cuando el
        // request efectivamente intenta SETEAR un OptionGroup (anti-clobber: null = no tocar).
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 91, ReservaId = reserva.Id, SupplierId = supplier.Id,
            HotelName = "Hotel Test", City = "Bariloche", RoomType = "Doble", MealPlan = "Desayuno",
            CheckIn = Inicio, CheckOut = Fin, Adults = 2, Children = 0, Rooms = 1,
            Status = "Solicitado", SalePrice = 1000m
        });
        reserva.Status = EstadoReserva.Confirmed;
        await context.SaveChangesAsync();
        var service = CreateBookingService(context);

        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Test Editado", StarRating: 4, City: "Bariloche",
            Country: "Argentina", CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Status: "Solicitado", Notes: null);

        var dto = await service.UpdateHotelAsync(reserva.Id, 91, request, CancellationToken.None);

        Assert.Equal("Hotel Test Editado", dto.HotelName);
    }

    // =====================================================================
    // Micro-ronda review (2026-08-12): "" limpia el grupo (via Normalize) + guard de longitud
    // =====================================================================

    [Fact]
    public async Task UpdateHotel_WithEmptyStringOptionGroup_ClearsTheGroup()
    {
        // Via de escape existente (OptionGroupRules.Normalize trata "" como "sin grupo"): mandar
        // OptionGroup="" (a diferencia de null, que es "no tocar" por el anti-clobber) es la forma de
        // sacar a un servicio de un grupo de opciones sin borrar el servicio.
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);
        var hotel = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, "hoteles", "A"), CancellationToken.None);
        var hotelId = await ResolveHotelIdAsync(context, hotel.PublicId);

        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Test", StarRating: 4, City: "Bariloche",
            Country: "Argentina", CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Status: "Solicitado", Notes: null,
            OptionGroup: "", OptionLabel: "");

        var dto = await service.UpdateHotelAsync(reserva.Id, hotelId, request, CancellationToken.None);

        Assert.Null(dto.OptionGroup);
        Assert.Null(dto.OptionLabel);
        Assert.Null((await context.HotelBookings.AsNoTracking().SingleAsync(h => h.Id == hotelId)).OptionGroup);
    }

    private static async Task<int> ResolveHotelIdAsync(AppDbContext context, Guid publicId)
        => (await context.HotelBookings.AsNoTracking().SingleAsync(h => h.PublicId == publicId)).Id;

    [Theory]
    [InlineData(61)] // MaxOptionGroupLength = 60
    public async Task CreateHotel_WithOptionGroupTooLong_IsRejected(int length)
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);
        var demasiadoLargo = new string('x', length);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, demasiadoLargo, "A"), CancellationToken.None));

        Assert.Equal(BookingService.OptionGroupTooLongMessage, error.Message);
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Theory]
    [InlineData(6)] // MaxOptionLabelLength = 5
    public async Task CreateHotel_WithOptionLabelTooLong_IsRejected(int length)
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);
        var etiquetaDemasiadoLarga = new string('A', length);

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, "hoteles", etiquetaDemasiadoLarga), CancellationToken.None));

        Assert.Equal(BookingService.OptionLabelTooLongMessage, error.Message);
        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Fact]
    public async Task CreateHotel_WithOptionGroupAtExactMaxLength_IsAccepted()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);
        var exactoAlLimite = new string('x', 60);

        var dto = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, exactoAlLimite, "A"), CancellationToken.None);

        Assert.Equal(exactoAlLimite, dto.OptionGroup);
    }

    [Fact]
    public async Task UpdateHotel_WithOptionGroupTooLong_IsRejected_AndNotSaved()
    {
        await using var context = CreateContext();
        var (reserva, supplier) = await SeedAsync(context);
        var service = CreateBookingService(context);
        var hotel = await service.CreateHotelAsync(reserva.Id, BuildCreateHotel(supplier.PublicId.ToString(), 4, "hoteles", "A"), CancellationToken.None);
        var hotelId = await ResolveHotelIdAsync(context, hotel.PublicId);
        var demasiadoLargo = new string('x', 61);

        var request = new UpdateHotelRequest(
            SupplierId: supplier.PublicId.ToString(), HotelName: "Hotel Test", StarRating: 4, City: "Bariloche",
            Country: "Argentina", CheckIn: Inicio, CheckOut: Fin, RoomType: "Doble", MealPlan: "Desayuno",
            Adults: 2, Children: 0, Rooms: 1, ConfirmationNumber: null,
            NetCost: 600m, SalePrice: 1000m, Commission: 400m, Status: "Solicitado", Notes: null,
            OptionGroup: demasiadoLargo, OptionLabel: "A");

        var error = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateHotelAsync(reserva.Id, hotelId, request, CancellationToken.None));

        Assert.Equal(BookingService.OptionGroupTooLongMessage, error.Message);
        Assert.Equal("hoteles", (await context.HotelBookings.AsNoTracking().SingleAsync(h => h.Id == hotelId)).OptionGroup);
    }
}
