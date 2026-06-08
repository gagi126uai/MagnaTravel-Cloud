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
/// ADR-020 F5 — Borrar vs cancelar manda EL SERVICIO (el viejo guard reserva-level C26 murio).
/// Un servicio NUNCA confirmado por el operador se BORRA (en cualquier etapa de la reserva); uno
/// confirmado solo se CANCELA. Se conserva el bloqueo por voucher emitido y por pago soft-deleted
/// vinculado al servicio generico. El bloqueo generico "la reserva tiene pagos" YA NO aplica al
/// borrado de un servicio nunca-confirmado.
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
            new Repository<AssistanceBooking>(context),
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

    // ADR-020 F5: un servicio CONFIRMADO por el operador no se borra (manda el servicio, no la
    // reserva). El estado de la reserva es irrelevante para esta regla.
    [Theory]
    [InlineData(EstadoReserva.Confirmed)]
    [InlineData(EstadoReserva.Traveling)]
    [InlineData(EstadoReserva.Closed)]
    [InlineData(EstadoReserva.Cancelled)]
    public async Task DeleteHotelAsync_ServiceConfirmed_Throws(string status)
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
        Assert.Contains("confirmado con el operador", ex.Message, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("Cancelalo", ex.Message);
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

    // ADR-020 B4: un aereo con PNR confirmado (HK) NO se borra aunque NO tenga ticket emitido — el
    // compromiso con el consolidador ya existe. Solo se cancela.
    [Fact]
    public async Task DeleteFlightAsync_PnrConfirmedWithoutTicket_Throws()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Confirmed);
        context.FlightSegments.Add(new FlightSegment
        {
            Id = 41, ReservaId = 1, AirlineCode = "AR", FlightNumber = "1234", Status = "HK",
            TicketIssuedAt = null, // PNR confirmado pero SIN ticket emitido
            DepartureTime = DateTime.UtcNow.AddDays(15), ArrivalTime = DateTime.UtcNow.AddDays(15).AddHours(2)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteFlightAsync(1, 41, CancellationToken.None));
        Assert.Contains("confirmado con el operador", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.FlightSegments.CountAsync());
    }

    // ===== Regression: pre-existing guards (payments / issued voucher) =====

    // ADR-020 F5 (relajacion): el bloqueo generico "la reserva tiene pagos vivos" YA NO aplica al
    // borrado de un servicio NUNCA confirmado. Los pagos son de la reserva, no del servicio; el
    // recalculo de saldo absorbe el cambio. (Sigue aplicando al borrado de la RESERVA completa.)
    [Fact]
    public async Task DeleteHotelAsync_NeverConfirmedWithReservaPayment_Allowed()
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

        await service.DeleteHotelAsync(1, 12, CancellationToken.None);

        Assert.Equal(0, await context.HotelBookings.CountAsync());
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

    // ===== Cascade Servicio→Payments con soft-deleted (review hallazgo Security ALTO) =====

    // ADR-020 F5: el guard de "pago soft-deleted vinculado" es ahora POR SERVICIO. Un pago vinculado
    // a un servicio GENERICO (id 50) NO bloquea el borrado de un HOTEL distinto (que no tiene ese link):
    // borrar el hotel no cascadea sobre ese pago. El hotel nunca-confirmado se borra; el servicio
    // generico y su pago soft-deleted quedan intactos. (El guard sigue bloqueando el borrado del
    // PROPIO servicio generico vinculado — cubierto en ReservaLifecycleTests.)
    [Fact]
    public async Task DeleteHotelAsync_WithSoftDeletedPaymentLinkedToOtherGenericService_Allowed()
    {
        await using var context = CreateContext();
        await SeedReservaAsync(context, EstadoReserva.Budget);
        context.Servicios.Add(new ServicioReserva
        {
            Id = 50, ReservaId = 1, ServiceType = "Otros", ProductType = "Generico",
            Description = "Servicio asociado a pago borrado", Status = "Solicitado",
            DepartureDate = DateTime.UtcNow.AddDays(5), CreatedAt = DateTime.UtcNow
        });
        context.Payments.Add(new Payment
        {
            Id = 95, ReservaId = 1, ServicioReservaId = 50,
            Amount = 200m, IsDeleted = true, DeletedAt = DateTime.UtcNow.AddDays(-1),
            Status = "Paid", Method = "Transfer", PaidAt = DateTime.UtcNow.AddDays(-2),
            EntryType = PaymentEntryTypes.Payment
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 14, ReservaId = 1, HotelName = "Hotel", Status = "Solicitado",
            CheckIn = DateTime.UtcNow.AddDays(10), CheckOut = DateTime.UtcNow.AddDays(13)
        });
        await context.SaveChangesAsync();

        var service = CreateService(context, CreateMapper());

        await service.DeleteHotelAsync(1, 14, CancellationToken.None);

        Assert.Equal(0, await context.HotelBookings.CountAsync());
        // El servicio generico y su pago soft-deleted siguen intactos.
        Assert.Equal(1, await context.Servicios.CountAsync());
    }
}
