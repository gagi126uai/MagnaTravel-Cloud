using System;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Contracts.Files;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class ReservaServiceTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IMapper> _mapperMock;

    public ReservaServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _mapperMock = new Mock<IMapper>();
    }

    [Fact]
    public async Task CreateReservaAsync_ShouldCreateReserva_WithCorrectInitialValues()
    {
        using var context = new AppDbContext(_dbOptions);
        var service = new ReservaService(context, _mapperMock.Object);
        var request = new CreateReservaRequest
        {
            Name = "Test Trip",
            PayerId = 1,
            StartDate = DateTime.UtcNow.AddDays(10),
            Description = "Testing reserve creation"
        };

        var result = await service.CreateReservaAsync(request);

        Assert.NotNull(result);
        Assert.Equal("Test Trip", result.Name);
        Assert.Equal(EstadoReserva.Reserved, result.Status);
        Assert.StartsWith($"F-{DateTime.Now.Year}-", result.NumeroReserva);
        Assert.Equal(1, await context.Reservas.CountAsync());
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldUpdateStatus_WhenValid()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Budget });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object);

        var result = await service.UpdateStatusAsync(1, EstadoReserva.Reserved);

        Assert.Equal(EstadoReserva.Reserved, result.Status);
        var dbReserva = await context.Reservas.FindAsync(1);
        Assert.NotNull(dbReserva);
        Assert.Equal(EstadoReserva.Reserved, dbReserva.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_ShouldThrowException_WhenReturningToBudgetWithPayments()
    {
        using var context = new AppDbContext(_dbOptions);
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.Reserved });
        context.Payments.Add(new Payment { Id = 1, ReservaId = 1, Amount = 100, Status = "Paid" });
        await context.SaveChangesAsync();

        var service = new ReservaService(context, _mapperMock.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateStatusAsync(1, EstadoReserva.Budget));
    }
}
