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

    // ===== C26: RemoveServiceAsync (rama generico) state guard =====

    [Fact]
    public async Task RemoveServiceAsync_GenericOnBudget_Removes()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Servicios.Add(new ServicioReserva
        {
            Id = 11, ReservaId = 1, ServiceType = "Otros", ProductType = "Generico",
            Description = "Servicio extra", Status = "Solicitado",
            DepartureDate = DateTime.UtcNow.AddDays(5), CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await service.RemoveServiceAsync(11, CancellationToken.None);

        Assert.Equal(0, await context.Servicios.CountAsync());
    }

    [Fact]
    public async Task RemoveServiceAsync_GenericOnConfirmed_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Confirmed);
        context.Servicios.Add(new ServicioReserva
        {
            Id = 12, ReservaId = 1, ServiceType = "Otros", ProductType = "Generico",
            Description = "Servicio extra", Status = "Confirmado",
            DepartureDate = DateTime.UtcNow.AddDays(5), CreatedAt = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RemoveServiceAsync(12, CancellationToken.None));
        Assert.Contains("reserva esta en estado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.Servicios.CountAsync());
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

    // ===== C27: Passenger delete bloqueado por factura emitida (CAE) =====

    [Fact]
    public async Task RemovePassengerAsync_OnBudgetWithoutInvoice_Removes()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Passengers.Add(new Passenger { Id = 200, ReservaId = 1, FullName = "Pax sin factura" });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await service.RemovePassengerAsync(200);

        Assert.Equal(0, await context.Passengers.CountAsync());
    }

    [Fact]
    public async Task RemovePassengerAsync_WithIssuedInvoiceWithCae_Throws()
    {
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Confirmed);
        context.Passengers.Add(new Passenger { Id = 201, ReservaId = 1, FullName = "Pax con factura" });
        context.Invoices.Add(new Invoice
        {
            Id = 71, ReservaId = 1,
            CAE = "01234567890123",
            ImporteTotal = 100m, ImporteNeto = 82.64m, ImporteIva = 17.36m
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemovePassengerAsync(201));
        Assert.Contains("factura", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Mensaje accionable C27: menciona nota de credito.
        Assert.Contains("nota de credito", ex.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(1, await context.Passengers.CountAsync());
    }

    [Fact]
    public async Task RemovePassengerAsync_WithInvoiceWithoutCae_DoesNotBlock()
    {
        // Una factura sin CAE significa que no fue emitida con AFIP (rechazada o pendiente);
        // no debe bloquear el borrado del pasajero. Granularidad reserva-level — confirmado
        // con ARCA + Contable 2026-05-06.
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Passengers.Add(new Passenger { Id = 202, ReservaId = 1, FullName = "Pax con factura sin CAE" });
        context.Invoices.Add(new Invoice
        {
            Id = 72, ReservaId = 1, CAE = null,
            ImporteTotal = 100m, ImporteNeto = 82.64m, ImporteIva = 17.36m
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        await service.RemovePassengerAsync(202);

        Assert.Equal(0, await context.Passengers.CountAsync());
    }

    [Fact]
    public async Task RemovePassengerAsync_OnTraveling_StillBlockedByStateGuard()
    {
        // Regression: el guard pre-existente de estado Operativo/Cerrado sigue activo
        // y prevalece sobre cualquier check fiscal.
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Traveling);
        context.Passengers.Add(new Passenger { Id = 203, ReservaId = 1, FullName = "Pax en viaje" });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemovePassengerAsync(203));
        Assert.Contains("Operativo", ex.Message);
    }

    [Fact]
    public async Task RemovePassengerAsync_WithVoucherAssignment_StillBlocked()
    {
        // Regression: el guard pre-existente "asignado a un voucher" se preserva.
        await using var context = new AppDbContext(_dbOptions);
        await SeedReservaAsync(context, EstadoReserva.Confirmed);
        context.Passengers.Add(new Passenger { Id = 204, ReservaId = 1, FullName = "Pax con voucher" });
        var voucher = new Voucher
        {
            Id = 91, ReservaId = 1, Status = "Cancelled",
            CreatedAt = DateTime.UtcNow, IssuedAt = DateTime.UtcNow,
            CreatedByUserId = "u1", CreatedByUserName = "tester"
        };
        context.Vouchers.Add(voucher);
        await context.SaveChangesAsync();

        context.VoucherPassengerAssignments.Add(new VoucherPassengerAssignment
        {
            VoucherId = 91, PassengerId = 204
        });
        await context.SaveChangesAsync();

        var service = BuildService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemovePassengerAsync(204));
        Assert.Contains("voucher", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
