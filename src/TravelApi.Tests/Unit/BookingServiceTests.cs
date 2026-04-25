using AutoMapper;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.DTOs;
using TravelApi.Application.Interfaces;
using TravelApi.Application.Mappings;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Repositories;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

public class BookingServiceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        return new AppDbContext(options);
    }

    private static IMapper CreateMapper()
    {
        return new MapperConfiguration(config => config.AddProfile<MappingProfile>()).CreateMapper();
    }

    private static BookingService CreateService(AppDbContext context, IMapper mapper)
    {
        var reservaService = new Mock<IReservaService>();
        reservaService
            .Setup(service => service.UpdateBalanceAsync(It.IsAny<int>()))
            .Returns(Task.CompletedTask);

        var supplierService = new Mock<ISupplierService>();
        supplierService
            .Setup(service => service.UpdateBalanceAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
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
            mapper);
    }

    [Fact]
    public async Task CreateHotelAsync_WithRateId_UsesSubmittedManualPrices()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0001", Name = "Reserva test" };
        var rate = new Rate
        {
            Id = 1,
            SupplierId = supplier.Id,
            ServiceType = "Hotel",
            ProductName = "Hotel tarifario",
            HotelName = "Hotel tarifario",
            City = "Bariloche",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = 100m,
            SalePrice = 150m,
            Commission = 50m,
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.Rates.Add(rate);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new CreateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel elegido",
            4,
            "Bariloche",
            "Argentina",
            DateTime.UtcNow.Date.AddDays(10),
            DateTime.UtcNow.Date.AddDays(13),
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            300m,
            777m,
            477m,
            null,
            null,
            rate.PublicId.ToString(),
            "Solicitado");

        var created = await service.CreateHotelAsync(reserva.Id, request, CancellationToken.None);

        Assert.Equal(300m, created.NetCost);
        Assert.Equal(777m, created.SalePrice);
        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(rate.Id, storedHotel.RateId);
        Assert.Equal(777m, storedHotel.SalePrice);
    }

    [Fact]
    public async Task UpdateHotelAsync_WithExistingRateId_KeepsSubmittedManualSalePrice()
    {
        await using var context = CreateContext();
        var mapper = CreateMapper();
        var supplier = new Supplier { Id = 1, Name = "Hotel Supplier" };
        var reserva = new Reserva { Id = 1, NumeroReserva = "F-2026-0002", Name = "Reserva test" };
        var rate = new Rate
        {
            Id = 1,
            SupplierId = supplier.Id,
            ServiceType = "Hotel",
            ProductName = "Hotel tarifario",
            HotelName = "Hotel tarifario",
            City = "Mendoza",
            RoomType = "Doble",
            MealPlan = "Desayuno",
            NetCost = 100m,
            SalePrice = 150m,
            Commission = 50m,
        };
        var hotel = new HotelBooking
        {
            Id = 1,
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            RateId = rate.Id,
            HotelName = "Hotel tarifario",
            City = "Mendoza",
            CheckIn = DateTime.UtcNow.Date.AddDays(10),
            CheckOut = DateTime.UtcNow.Date.AddDays(12),
            Nights = 2,
            RoomType = "Doble",
            MealPlan = "Desayuno",
            Rooms = 1,
            Adults = 2,
            Children = 0,
            NetCost = 200m,
            SalePrice = 300m,
            Commission = 100m,
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        context.Rates.Add(rate);
        context.HotelBookings.Add(hotel);
        await context.SaveChangesAsync();

        var service = CreateService(context, mapper);
        var request = new UpdateHotelRequest(
            supplier.PublicId.ToString(),
            "Hotel tarifario",
            4,
            "Mendoza",
            "Argentina",
            hotel.CheckIn,
            hotel.CheckOut,
            "Doble",
            "Desayuno",
            2,
            0,
            1,
            null,
            240m,
            888m,
            648m,
            "Solicitado",
            null,
            null,
            rate.PublicId.ToString(),
            "Solicitado");

        var updated = await service.UpdateHotelAsync(reserva.Id, hotel.Id, request, CancellationToken.None);

        Assert.Equal(240m, updated.NetCost);
        Assert.Equal(888m, updated.SalePrice);
        var storedHotel = await context.HotelBookings.SingleAsync();
        Assert.Equal(rate.Id, storedHotel.RateId);
        Assert.Equal(888m, storedHotel.SalePrice);
    }

    [Fact]
    public void PassengerDtoMapping_IncludesEditableContactFields()
    {
        var mapper = CreateMapper();
        var passenger = new Passenger
        {
            PublicId = Guid.NewGuid(),
            FullName = "Ada Lovelace",
            DocumentType = "DNI",
            DocumentNumber = "123",
            Phone = "+5491112345678",
            Email = "ada@example.com",
            Gender = "F",
            Notes = "Vegetariana",
        };

        var dto = mapper.Map<PassengerDto>(passenger);

        Assert.Equal(passenger.Phone, dto.Phone);
        Assert.Equal(passenger.Email, dto.Email);
        Assert.Equal(passenger.Gender, dto.Gender);
        Assert.Equal(passenger.Notes, dto.Notes);
    }
}
