using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// C24: cobertura de la regla de negocio "no se puede borrar un Supplier que
/// tiene bookings tipados asociados". Antes la FK era Cascade y un DELETE en
/// /suppliers/{id}/force arrastraba HotelBookings/TransferBookings/PackageBookings/
/// FlightSegments perdiendo datos historicos. Ahora SupplierService.DeleteSupplierAsync
/// chequea explicitamente cada tabla y la BD queda como red de seguridad con Restrict.
///
/// Nota sobre InMemoryDatabase: no enforza las FK declarativas, asi que los tests
/// validan el flujo de la regla de negocio en el servicio. El comportamiento real
/// del Restrict a nivel BD se verifica en VPS al aplicar la migracion (ver pre-check
/// SQL del playbook).
/// </summary>
public class SupplierServiceTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(Supplier supplier, Reserva reserva)> SeedSupplierAndReservaAsync(AppDbContext context)
    {
        var supplier = new Supplier { Name = "Proveedor C24" };
        var reserva = new Reserva
        {
            NumeroReserva = "F-2026-C24",
            Name = "Reserva C24",
            Status = EstadoReserva.Confirmed
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (supplier, reserva);
    }

    [Fact]
    public async Task DeleteSupplierAsync_WithoutAnyReferences_Succeeds()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Proveedor sin referencias" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        await service.DeleteSupplierAsync(supplier.Id, CancellationToken.None);

        Assert.Equal(0, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task DeleteSupplierAsync_WithHotelBooking_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel test",
            City = "Bariloche",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteSupplierAsync(supplier.Id, CancellationToken.None));
        Assert.Contains("hotel", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task DeleteSupplierAsync_WithTransferBooking_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PickupLocation = "EZE",
            DropoffLocation = "Hotel",
            PickupDateTime = DateTime.UtcNow.AddDays(10)
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteSupplierAsync(supplier.Id, CancellationToken.None));
        Assert.Contains("transfer", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task DeleteSupplierAsync_WithPackageBooking_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.PackageBookings.Add(new PackageBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PackageName = "Paquete C24",
            Destination = "Bariloche",
            StartDate = DateTime.UtcNow.AddDays(10),
            EndDate = DateTime.UtcNow.AddDays(15),
            Nights = 5
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteSupplierAsync(supplier.Id, CancellationToken.None));
        Assert.Contains("paquete", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task DeleteSupplierAsync_WithFlightSegment_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            AirlineCode = "AR",
            FlightNumber = "1234",
            Origin = "EZE",
            Destination = "BRC",
            DepartureTime = DateTime.UtcNow.AddDays(10),
            ArrivalTime = DateTime.UtcNow.AddDays(10).AddHours(2)
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteSupplierAsync(supplier.Id, CancellationToken.None));
        Assert.Contains("vuelo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await context.Suppliers.CountAsync());
    }
}
