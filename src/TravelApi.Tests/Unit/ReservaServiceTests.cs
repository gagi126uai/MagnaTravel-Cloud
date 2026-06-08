using System;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Application.Contracts.Reservations;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class ReservaServiceTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IMapper> _mapperMock;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceTests()
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
        // UserManager mock minimo: el unico camino que lo usa en estos tests es
        // CreateReservaAsync (lookup de FullName del responsable). Configuramos
        // FindByIdAsync para devolver null y dejar ResponsibleUserName en null,
        // lo cual es valido para los asserts actuales.
        var store = new Mock<IUserStore<ApplicationUser>>();
        store
            .Setup(s => s.FindByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ApplicationUser?)null);
        return new UserManager<ApplicationUser>(
            store.Object,
            null!,
            null!,
            Array.Empty<IUserValidator<ApplicationUser>>(),
            Array.Empty<IPasswordValidator<ApplicationUser>>(),
            null!,
            null!,
            null!,
            null!);
    }

    [Fact]
    public async Task CreateReservaAsync_ShouldCreateReserva_WithCorrectInitialValues()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Customers.Add(new Customer { Id = 1, PublicId = Guid.Parse("00000000-0000-0000-0000-000000000001"), FullName = "Test" });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);
        var request = new CreateReservaRequest
        {
            Name = "Test Trip",
            PayerId = "00000000-0000-0000-0000-000000000001",
            StartDate = DateTime.UtcNow.AddDays(10),
            Description = "Testing reserve creation"
        };

        var result = await service.CreateReservaAsync(request, "user-1");

        Assert.NotNull(result);
        Assert.Equal("Test Trip", result.Name);
        // ADR-020: toda reserva nace en Cotizacion (antes nacia en Presupuesto).
        Assert.Equal(EstadoReserva.Quotation, result.Status);
        Assert.StartsWith($"F-{DateTime.Now.Year}-", result.NumeroReserva);
        Assert.Equal(1, await context.Reservas.CountAsync());
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldUpdateStatus_WhenValid()
    {
        using var context = new AppDbContext(_dbOptions);
        // 1 pasajero DECLARADO + su nominal cargado: el conteo esperado se basa en la cantidad
        // declarada de la reserva (no en los servicios), asi que readiness exige al menos 1 pax.
        context.Reservas.Add(new Reserva
        {
            Id = 1, Name = "Test", Status = EstadoReserva.Budget,
            AdultCount = 1, ChildCount = 0, InfantCount = 0
        });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1,
            ReservaId = 1,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Servicio test",
            ConfirmationNumber = "ABC123",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = 150m,
            NetCost = 100m,
            Commission = 50m,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

        // ADR-020: Budget -> InManagement es la transicion manual valida (Confirmed solo lo alcanza
        // el motor automatico). Con 1 pax declarado y 1 nominal cargado, readiness pasa.
        var result = await service.UpdateStatusAsync(1, EstadoReserva.InManagement);

        Assert.Equal(EstadoReserva.InManagement, result.Status);
        var dbReserva = await context.Reservas.FindAsync(1);
        Assert.NotNull(dbReserva);
        Assert.Equal(EstadoReserva.InManagement, dbReserva.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldThrowException_WhenReturningToBudgetWithPayments()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Confirmed });
        context.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 100, Status = "Paid" });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Budget));
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldBlockOperational_WhenReservationHasDebt()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Confirmed });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1,
            ReservaId = 1,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Servicio test",
            ConfirmationNumber = "ABC123",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = 150m,
            NetCost = 100m,
            Commission = 50m,
            CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        _settingsServiceMock
            .Setup(s => s.GetEntityAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OperationalFinanceSettings
            {
                RequireFullPaymentForOperativeStatus = true
            });

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Traveling));
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldAllowOperational_WhenReservationIsFullyPaid()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Confirmed });
        context.Servicios.Add(new ServicioReserva
        {
            Id = 1,
            ReservaId = 1,
            ServiceType = "Hotel",
            ProductType = "Hotel",
            Description = "Servicio test",
            ConfirmationNumber = "ABC123",
            Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(15),
            SalePrice = 150m,
            NetCost = 100m,
            Commission = 50m,
            CreatedAt = DateTime.UtcNow
        });
        context.Payments.Add(new Payment
        {
            Id = 1,
            ReservaId = 1,
            Amount = 150m,
            Method = "Transfer",
            Status = "Paid",
            EntryType = PaymentEntryTypes.Payment,
            AffectsCash = true
        });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Traveling);

        Assert.Equal(EstadoReserva.Traveling, result.Status);
    }

    // Coherencia pasajeros declarados vs nominales: si ya hay N pasajeros nominales cargados,
    // bajar la cantidad DECLARADA por debajo de N dejaria pasajeros "huerfanos" que podrian
    // colarse en vouchers/facturas y haria pasar el gate de readiness de forma enganosa.
    // El servicio debe RECHAZAR (no borra pasajeros automaticamente) con un mensaje claro.
    [Fact]
    public async Task UpdatePassengerCountsAsync_ShouldReject_WhenLoweringDeclaredBelowLoadedPassengers()
    {
        using var context = new AppDbContext(_dbOptions);
        // 3 nominales ya cargados; la reserva esta en Cotizacion (etapa donde se editan cantidades).
        context.Reservas.Add(new Reserva
        {
            Id = 1, Name = "Test", Status = EstadoReserva.Quotation,
            AdultCount = 3, ChildCount = 0, InfantCount = 0
        });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Pasajero Dos" });
        context.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "Pasajero Tres" });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

        // Intentar bajar la cantidad declarada total a 1 (< 3 cargados) debe rechazar.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpdatePassengerCountsAsync("1", new PassengerCountsRequest(AdultCount: 1, ChildCount: 0, InfantCount: 0)));
        Assert.Equal(
            "Hay 3 pasajeros cargados en la reserva; quitá los que sobren antes de bajar la cantidad a 1.",
            ex.Message);

        // No debe persistir: las cantidades declaradas siguen en 3/0/0.
        var dbReserva = await context.Reservas.FindAsync(1);
        Assert.NotNull(dbReserva);
        Assert.Equal(3, dbReserva!.AdultCount);
        Assert.Equal(0, dbReserva.ChildCount);
        Assert.Equal(0, dbReserva.InfantCount);
    }

    [Fact]
    public async Task UpdatePassengerCountsAsync_ShouldPersist_WhenDeclaredEqualsLoadedPassengers()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1, Name = "Test", Status = EstadoReserva.Quotation,
            AdultCount = 5, ChildCount = 0, InfantCount = 0
        });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Pasajero Dos" });
        context.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "Pasajero Tres" });
        await context.SaveChangesAsync();

        // El camino feliz devuelve GetReservaByIdAsync, que mapea la entidad a DTO.
        // Configuramos el mapper para devolver el estado actual y poder afirmar sobre el resultado.
        _mapperMock
            .Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
            .Returns((Reserva r) => new ReservaDto { Status = r.Status });

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

        // Bajar a un total igual a la cantidad cargada (3) esta permitido.
        var result = await service.UpdatePassengerCountsAsync("1", new PassengerCountsRequest(AdultCount: 2, ChildCount: 1, InfantCount: 0));

        Assert.Equal(EstadoReserva.Quotation, result.Status);
        var dbReserva = await context.Reservas.FindAsync(1);
        Assert.NotNull(dbReserva);
        Assert.Equal(2, dbReserva!.AdultCount);
        Assert.Equal(1, dbReserva.ChildCount);
        Assert.Equal(0, dbReserva.InfantCount);
    }

    [Fact]
    public async Task UpdatePassengerCountsAsync_ShouldPersist_WhenRaisingDeclaredAboveLoadedPassengers()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva
        {
            Id = 1, Name = "Test", Status = EstadoReserva.Budget,
            AdultCount = 3, ChildCount = 0, InfantCount = 0
        });
        context.Passengers.Add(new Passenger { Id = 1, ReservaId = 1, FullName = "Pasajero Uno" });
        context.Passengers.Add(new Passenger { Id = 2, ReservaId = 1, FullName = "Pasajero Dos" });
        context.Passengers.Add(new Passenger { Id = 3, ReservaId = 1, FullName = "Pasajero Tres" });
        await context.SaveChangesAsync();

        _mapperMock
            .Setup(m => m.Map<ReservaDto>(It.IsAny<Reserva>()))
            .Returns((Reserva r) => new ReservaDto { Status = r.Status });

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

        // Subir la cantidad declarada (4 > 3 cargados) siempre esta permitido.
        var result = await service.UpdatePassengerCountsAsync("1", new PassengerCountsRequest(AdultCount: 3, ChildCount: 1, InfantCount: 0));

        Assert.Equal(EstadoReserva.Budget, result.Status);
        var dbReserva = await context.Reservas.FindAsync(1);
        Assert.NotNull(dbReserva);
        Assert.Equal(3, dbReserva!.AdultCount);
        Assert.Equal(1, dbReserva.ChildCount);
        Assert.Equal(0, dbReserva.InfantCount);
    }
}
