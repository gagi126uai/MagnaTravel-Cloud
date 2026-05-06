using System;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Contracts.Files;
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

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager());
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
        Assert.Equal(EstadoReserva.Budget, result.Status);
        Assert.StartsWith($"F-{DateTime.Now.Year}-", result.NumeroReserva);
        Assert.Equal(1, await context.Reservas.CountAsync());
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldUpdateStatus_WhenValid()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
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

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager());

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Confirmed);

        Assert.Equal(EstadoReserva.Confirmed, result.Status);
        var dbReserva = await context.Reservas.FindAsync(1);
        Assert.NotNull(dbReserva);
        Assert.Equal(EstadoReserva.Confirmed, dbReserva.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldThrowException_WhenReturningToBudgetWithPayments()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Confirmed });
        context.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 100, Status = "Paid" });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager());

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

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager());

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

        var service = new ReservaService(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager());

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Traveling);

        Assert.Equal(EstadoReserva.Traveling, result.Status);
    }
}
