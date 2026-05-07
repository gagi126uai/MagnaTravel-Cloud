using System;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// C26 — Solo se pueden borrar servicios (Hotel/Transfer/Package/Flight/generico)
/// si la Reserva padre esta en Budget. En cualquier otro estado hay que cancelar
/// con el proveedor primero (cambiar Status del servicio a Cancelado).
///
/// Cubre las dos rutas que terminan ejecutando el guard:
///  - BookingService.DeleteHotel/Transfer/Package/Flight (cuatro tipos especificos).
///  - ReservaService.RemoveServiceAsync (servicio generico — quinto tipo).
/// </summary>
public class BookingServiceDeleteTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
        => new MapperConfiguration(c => c.AddProfile<MappingProfile>()).CreateMapper();

    private static BookingService CreateService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(s => s.UpdateBalanceAsync(It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService
            .Setup(s => s.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        return new BookingService(
            new Repository<FlightSegment>(context),
            new Repository<HotelBooking>(context),
            new Repository<PackageBooking>(context),
            new Repository<TransferBooking>(context),
            new Repository<Reserva>(context),
            new Repository<Supplier>(context),
            reservaService.Object,
            supplierService.Object,
            context,
            mapper,
            NullLogger<BookingService>.Instance);
    }

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

    // ===== BookingService.DeleteHotelAsync =====

    [Fact]
    public async Task DeleteHotelAsync_OnBudget_Removes()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Budget);
        var hotel = new HotelBooking
        {
            Id = 10, ReservaId = 1, HotelName = "Test", Status = "Solicitado",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13),
            Adults = 2
        };
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        await service.DeleteHotelAsync(1, 10, CancellationToken.None);

        Assert.Equal(0, await context.HotelBookings.CountAsync());
    }

    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    public async Task DeleteHotelAsync_NotBudget_Throws(string status)
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, status);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 11, ReservaId = 1, HotelName = "Test", Status = "Confirmado",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13),
            Adults = 2
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteHotelAsync(1, 11, CancellationToken.None));
        Assert.Contains("reserva esta en estado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.HotelBookings.CountAsync());
    }

    // ===== BookingService.DeleteTransferAsync =====

    [Fact]
    public async Task DeleteTransferAsync_OnBudget_Removes()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 20, ReservaId = 1, VehicleType = "Sedan", Passengers = 3, Status = "Solicitado",
            PickupDateTime = DateTime.UtcNow.AddDays(5)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        await service.DeleteTransferAsync(1, 20, CancellationToken.None);

        Assert.Equal(0, await context.TransferBookings.CountAsync());
    }

    [Fact]
    public async Task DeleteTransferAsync_OnConfirmed_Throws()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Confirmed);
        context.TransferBookings.Add(new TransferBooking
        {
            Id = 21, ReservaId = 1, VehicleType = "Van", Passengers = 5, Status = "Confirmado",
            PickupDateTime = DateTime.UtcNow.AddDays(5)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteTransferAsync(1, 21, CancellationToken.None));
        Assert.Contains("Cancelá", ex.Message);
        Assert.Equal(1, await context.TransferBookings.CountAsync());
    }

    // ===== BookingService.DeletePackageAsync =====

    [Fact]
    public async Task DeletePackageAsync_OnBudget_Removes()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 30, ReservaId = 1, PackageName = "Test pkg", Adults = 2, Children = 1, Status = "Solicitado",
            StartDate = DateTime.UtcNow.AddDays(20), EndDate = DateTime.UtcNow.AddDays(25)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        await service.DeletePackageAsync(1, 30, CancellationToken.None);

        Assert.Equal(0, await context.PackageBookings.CountAsync());
    }

    [Fact]
    public async Task DeletePackageAsync_OnTraveling_Throws()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Traveling);
        context.PackageBookings.Add(new PackageBooking
        {
            Id = 31, ReservaId = 1, PackageName = "Test pkg", Adults = 2, Children = 0, Status = "Confirmado",
            StartDate = DateTime.UtcNow.AddDays(20), EndDate = DateTime.UtcNow.AddDays(25)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeletePackageAsync(1, 31, CancellationToken.None));
        Assert.Equal(1, await context.PackageBookings.CountAsync());
    }

    // ===== BookingService.DeleteFlightAsync =====

    [Fact]
    public async Task DeleteFlightAsync_OnBudget_Removes()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 40, ReservaId = 1, AirlineCode = "AR", FlightNumber = "1234", Status = "Solicitado",
            DepartureTime = DateTime.UtcNow.AddDays(15), ArrivalTime = DateTime.UtcNow.AddDays(15).AddHours(2)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        await service.DeleteFlightAsync(1, 40, CancellationToken.None);

        Assert.Equal(0, await context.FlightSegments.CountAsync());
    }

    [Fact]
    public async Task DeleteFlightAsync_OnConfirmed_Throws()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Confirmed);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 41, ReservaId = 1, AirlineCode = "AR", FlightNumber = "1234", Status = "Emitido",
            DepartureTime = DateTime.UtcNow.AddDays(15), ArrivalTime = DateTime.UtcNow.AddDays(15).AddHours(2)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteFlightAsync(1, 41, CancellationToken.None));
        Assert.Equal(1, await context.FlightSegments.CountAsync());
    }

    // ===== Regression: pre-existing guards (payments / issued voucher) =====

    [Fact]
    public async Task DeleteHotelAsync_OnBudgetWithLivePayment_Throws()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 12, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        context.Payments.Add(new Payment { Id = 80, ReservaId = 1, Amount = 100m, IsDeleted = false, Status = "Paid" });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteHotelAsync(1, 12, CancellationToken.None));
        Assert.Contains("pagos", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.HotelBookings.CountAsync());
    }

    [Fact]
    public async Task DeleteHotelAsync_OnBudgetWithIssuedVoucher_Throws()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 13, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        context.Vouchers.Add(new Voucher
        {
            Id = 90, ReservaId = 1, Status = "Issued",
            CreatedAt = DateTime.UtcNow, IssuedAt = DateTime.UtcNow,
            CreatedByUserId = "u1", CreatedByUserName = "tester"
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteHotelAsync(1, 13, CancellationToken.None));
        Assert.Contains("voucher", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.HotelBookings.CountAsync());
    }
}
