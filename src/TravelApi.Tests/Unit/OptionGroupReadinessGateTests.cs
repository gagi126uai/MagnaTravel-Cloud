using System;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Opciones A/B/C (decisión #1 firmada del dueño, 2026-08-11/12): el gate que rechaza "el cliente
/// aceptó" (Presupuesto -&gt; En gestión, <c>ReservaService.UpdateStatusAsync</c>) cuando queda algún
/// grupo de opciones con 2+ alternativas vivas sin resolver. Mismo harness que
/// <c>ReservaServiceTests</c> (in-memory + mocks mínimos).
/// </summary>
public class OptionGroupReadinessGateTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IMapper> _mapperMock;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public OptionGroupReadinessGateTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _mapperMock = new Mock<IMapper>();
        _settingsServiceMock = new Mock<IOperationalFinanceSettingsService>();
        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings());
    }

    private static UserManager<ApplicationUser> BuildUserManager()
    {
        var store = new Mock<IUserStore<ApplicationUser>>();
        store
            .Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object, null!, null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!, null!, null!, null!);
    }

    private ReservaService CreateService(AppDbContext context)
        => new(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

    private static void SeedBaseReserva(AppDbContext context)
    {
        context.Reservas.Add(new Reserva
        {
            Id = 1, Name = "Test", Status = EstadoReserva.Budget,
            AdultCount = 1, ChildCount = 0, InfantCount = 0
        });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
    }

    [Fact]
    public async Task UpdateStatus_WithTwoLiveOptionsInSameGroup_IsRejected()
    {
        using var context = new AppDbContext(_dbOptions);
        SeedBaseReserva(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Hotel A", City = "Bariloche",
            Status = "Solicitado", SalePrice = 1000m, OptionGroup = "hoteles", OptionLabel = "A"
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 2, ReservaId = 1, HotelName = "Hotel B", City = "Bariloche",
            Status = "Solicitado", SalePrice = 1500m, OptionGroup = "hoteles", OptionLabel = "B"
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateStatusAsync(1, EstadoReserva.InManagement));

        Assert.Equal("Elegí qué opción quedó de hoteles antes de confirmar.", error.Message);
        // La reserva NO avanzo de estado: el rechazo corto ANTES de persistir el cambio.
        var dbReserva = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Budget, dbReserva.Status);
    }

    [Fact]
    public async Task UpdateStatus_WithOnlyOneLiveOptionInGroup_Succeeds()
    {
        // Simula el estado DESPUES de resolver el grupo: Hotel B fue borrado (o esta cancelado),
        // solo queda Hotel A vivo con OptionGroup cargado -> ya no es ambiguo.
        using var context = new AppDbContext(_dbOptions);
        SeedBaseReserva(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Hotel A", City = "Bariloche",
            Status = "Solicitado", SalePrice = 1000m, OptionGroup = "hoteles", OptionLabel = "A"
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 2, ReservaId = 1, HotelName = "Hotel B", City = "Bariloche",
            Status = "Cancelado", SalePrice = 1500m, OptionGroup = "hoteles", OptionLabel = "B"
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.InManagement);

        Assert.Equal(EstadoReserva.InManagement, result.Status);
    }

    [Fact]
    public async Task UpdateStatus_WithoutAnyOptionGroup_IsUnaffected()
    {
        // Regresion: una reserva SIN opciones A/B/C (el 100% de las reservas hoy) sigue avanzando igual.
        using var context = new AppDbContext(_dbOptions);
        SeedBaseReserva(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Hotel Unico", City = "Bariloche",
            Status = "Solicitado", SalePrice = 1000m
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.InManagement);

        Assert.Equal(EstadoReserva.InManagement, result.Status);
    }

    [Fact]
    public async Task UpdateStatus_WithAmbiguousGroupAcrossDifferentServiceTypes_IsRejected()
    {
        // El grupo puede mezclar tipos de servicio (ej. "Hotel A" vs "Paquete todo-incluido B").
        using var context = new AppDbContext(_dbOptions);
        SeedBaseReserva(context);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Hotel A", City = "Bariloche",
            Status = "Solicitado", SalePrice = 1000m, OptionGroup = "alojamiento", OptionLabel = "A"
        });
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 1, ReservaId = 1, PackageName = "Todo incluido B", StartDate = DateTime.UtcNow,
            Status = "Solicitado", SalePrice = 2000m, OptionGroup = "alojamiento", OptionLabel = "B"
        });
        await context.SaveChangesAsync();

        var service = CreateService(context);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateStatusAsync(1, EstadoReserva.InManagement));

        Assert.Equal("Elegí qué opción quedó de alojamiento antes de confirmar.", error.Message);
    }
}
