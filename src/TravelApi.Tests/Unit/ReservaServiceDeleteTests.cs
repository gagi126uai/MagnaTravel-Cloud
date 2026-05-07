using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Controllers;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Identity;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// C25 — DeleteReservaAsync solo permite borrar reservas en estado Budget.
/// Resto de validaciones (pagos vivos, vouchers emitidos, facturas con CAE) se
/// delegan a DeleteGuards. Tambien fija el contrato HTTP 409 (antes era 400).
///
/// C27 (pasajero bloqueado por factura emitida) se anade en commit posterior;
/// los tests asociados van junto al wiring de RemovePassengerAsync.
/// </summary>
public class ReservaServiceDeleteTests
{
    private readonly DbContextOptions<AppDbContext> _dbOptions;
    private readonly Mock<IMapper> _mapperMock;
    private readonly Mock<IOperationalFinanceSettingsService> _settingsServiceMock;

    public ReservaServiceDeleteTests()
    {
        _dbOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
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

    private ReservaService BuildService(AppDbContext context)
        => new(context, _mapperMock.Object, _settingsServiceMock.Object, BuildUserManager(), NullLogger<ReservaService>.Instance);

    private static async Task<Reserva> SeedReservaAsync(AppDbContext context, string status)
    {
        var reserva = new Reserva
        {
            Id = 1,
            NumeroReserva = "F-2026-0001",
            Name = "Reserva test",
            Status = status
        };
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return reserva;
    }

    // ===== C25: Reserva delete state guard =====

    [Fact]
    public async Task DeleteReservaAsync_OnBudget_DeletesReservaAndChildren()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Servicios.Add(new ServicioReserva
        {
            Id = 10, ReservaId = 1, ServiceType = "Hotel", ProductType = "Hotel",
            Description = "S", Status = "Solicitado", DepartureDate = DateTime.UtcNow.AddDays(5),
            CreatedAt = DateTime.UtcNow
        });
        context.Passengers.Add(new Passenger { Id = 100, ReservaId = 1, FullName = "Pax 1" });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await service.DeleteReservaAsync(1);

        Assert.Equal(0, await context.Reservas.CountAsync());
        Assert.Equal(0, await context.Servicios.CountAsync());
        Assert.Equal(0, await context.Passengers.CountAsync());
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    public async Task DeleteReservaAsync_NotBudget_Throws(string status)
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, status);

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteReservaAsync(1));
        Assert.Contains("Presupuesto", ex.Message);

        // Pin de transaccion: nada se borra cuando se rechaza el delete.
        Assert.Equal(1, await context.Reservas.CountAsync());
    }

    [Fact]
    public async Task DeleteReservaAsync_BudgetWithLivePayment_Throws_AndPreservesData()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Payments.Add(new Payment { Id = 50, ReservaId = 1, Amount = 100m, IsDeleted = false, Status = "Paid" });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteReservaAsync(1));
        Assert.Contains("pagos", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await context.Reservas.CountAsync());
        Assert.Equal(1, await context.Payments.CountAsync());
    }

    [Fact]
    public async Task DeleteReservaAsync_BudgetWithSoftDeletedPayment_IsAllowed()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Payments.Add(new Payment { Id = 51, ReservaId = 1, Amount = 100m, IsDeleted = true, DeletedAt = DateTime.UtcNow, Status = "Paid" });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        // Soft-deleted payments NO bloquean el delete (Payments.AnyAsync filtra por !IsDeleted).
        await service.DeleteReservaAsync(1);

        Assert.Equal(0, await context.Reservas.CountAsync());
    }

    [Fact]
    public async Task DeleteReservaAsync_BudgetWithIssuedVoucher_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Vouchers.Add(new Voucher
        {
            Id = 60, ReservaId = 1, Status = "Issued",
            CreatedAt = DateTime.UtcNow, IssuedAt = DateTime.UtcNow,
            CreatedByUserId = "u1", CreatedByUserName = "tester"
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteReservaAsync(1));
        Assert.Contains("voucher", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await context.Reservas.CountAsync());
        Assert.Equal(1, await context.Vouchers.CountAsync());
    }

    [Fact]
    public async Task DeleteReservaAsync_BudgetWithInvoiceWithCae_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Invoices.Add(new Invoice
        {
            Id = 70, ReservaId = 1,
            CAE = "01234567890123",
            ImporteTotal = 100m, ImporteNeto = 82.64m, ImporteIva = 17.36m
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteReservaAsync(1));
        Assert.Contains("CAE", ex.Message);

        Assert.Equal(1, await context.Reservas.CountAsync());
    }

    // ===== C25: Pin de contrato HTTP 409 (antes 400) =====

    [Fact]
    public async Task DeleteReserva_ControllerMaps_InvalidOperationException_To409Conflict()
    {
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(s => s.DeleteReservaAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Solo se pueden eliminar reservas en estado Presupuesto."));

        var controller = new ReservasController(
            reservaService.Object,
            Mock.Of<IVoucherService>(),
            Mock.Of<ITimelineService>(),
            NullLogger<ReservasController>.Instance);

        var result = await controller.DeleteReserva("reserva-1", CancellationToken.None);

        // Pin del contrato: rechazos por estado/contenido devuelven 409, no 400.
        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, conflict.StatusCode);
    }

}
