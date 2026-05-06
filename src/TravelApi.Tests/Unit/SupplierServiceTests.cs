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

    // ----------------------------------------------------------------------
    // C29: guard de desactivacion (IsActive: true -> false) cuando el supplier
    // tiene reservas activas (Status in {Budget, Confirmed, Traveling}) con al
    // menos un booking tipado referenciandolo.
    // ----------------------------------------------------------------------

    private static async Task<(Supplier supplier, Reserva reserva)> SeedSupplierAndReservaWithStatusAsync(
        AppDbContext context,
        string status,
        bool supplierIsActive = true)
    {
        var supplier = new Supplier { Name = "Proveedor C29", IsActive = supplierIsActive };
        var reserva = new Reserva
        {
            NumeroReserva = $"F-2026-C29-{Guid.NewGuid():N}".Substring(0, 20),
            Name = "Reserva C29",
            Status = status
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (supplier, reserva);
    }

    private static Supplier BuildIncomingSupplier(Supplier existing, bool isActive, string? overrideName = null) => new()
    {
        Name = overrideName ?? existing.Name,
        ContactName = existing.ContactName,
        Email = existing.Email,
        Phone = existing.Phone,
        TaxId = existing.TaxId,
        TaxCondition = existing.TaxCondition,
        Address = existing.Address,
        IsActive = isActive
    };

    [Fact]
    public async Task UpdateSupplierAsync_DeactivateWithActiveHotelBooking_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaWithStatusAsync(context, EstadoReserva.Budget);

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel C29",
            City = "Bariloche",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));
        Assert.Contains("1 reservas activas", ex.Message);
        Assert.Contains("no se puede desactivar", ex.Message);

        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.NotNull(stored);
        Assert.True(stored!.IsActive);
    }

    [Fact]
    public async Task UpdateSupplierAsync_DeactivateWithActiveTransferBooking_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaWithStatusAsync(context, EstadoReserva.Confirmed);

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
        var incoming = BuildIncomingSupplier(supplier, isActive: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));
        Assert.Contains("1 reservas activas", ex.Message);

        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.True(stored!.IsActive);
    }

    [Fact]
    public async Task UpdateSupplierAsync_DeactivateWithActivePackageBooking_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaWithStatusAsync(context, EstadoReserva.Traveling);

        context.PackageBookings.Add(new PackageBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PackageName = "Paquete C29",
            Destination = "Bariloche",
            StartDate = DateTime.UtcNow.AddDays(1),
            EndDate = DateTime.UtcNow.AddDays(5),
            Nights = 4
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));
        Assert.Contains("1 reservas activas", ex.Message);

        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.True(stored!.IsActive);
    }

    [Fact]
    public async Task UpdateSupplierAsync_DeactivateWithActiveFlightSegment_Throws()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaWithStatusAsync(context, EstadoReserva.Confirmed);

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
        var incoming = BuildIncomingSupplier(supplier, isActive: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));
        Assert.Contains("1 reservas activas", ex.Message);

        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.True(stored!.IsActive);
    }

    [Fact]
    public async Task UpdateSupplierAsync_DeactivateWithMultipleBookingsSameReserva_CountsOnce()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaWithStatusAsync(context, EstadoReserva.Confirmed);

        // 1 reserva con 2 hoteles + 1 transfer del mismo proveedor.
        context.HotelBookings.AddRange(
            new HotelBooking
            {
                ReservaId = reserva.Id,
                SupplierId = supplier.Id,
                HotelName = "Hotel A",
                City = "BRC",
                CheckIn = DateTime.UtcNow.AddDays(10),
                CheckOut = DateTime.UtcNow.AddDays(11),
                Nights = 1
            },
            new HotelBooking
            {
                ReservaId = reserva.Id,
                SupplierId = supplier.Id,
                HotelName = "Hotel B",
                City = "BRC",
                CheckIn = DateTime.UtcNow.AddDays(12),
                CheckOut = DateTime.UtcNow.AddDays(13),
                Nights = 1
            });
        context.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PickupLocation = "EZE",
            DropoffLocation = "Hotel A",
            PickupDateTime = DateTime.UtcNow.AddDays(10)
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));
        Assert.Contains("1 reservas activas", ex.Message);
        Assert.DoesNotContain("3 reservas", ex.Message);
        Assert.DoesNotContain("2 reservas", ex.Message);
    }

    [Fact]
    public async Task UpdateSupplierAsync_DeactivateWithBookingsInClosedReserva_Succeeds()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Proveedor C29 closed" };
        var closedReserva = new Reserva
        {
            NumeroReserva = "F-2026-C29-CL",
            Name = "Reserva cerrada",
            Status = EstadoReserva.Closed
        };
        var cancelledReserva = new Reserva
        {
            NumeroReserva = "F-2026-C29-CA",
            Name = "Reserva cancelada",
            Status = EstadoReserva.Cancelled
        };
        context.Suppliers.Add(supplier);
        context.Reservas.AddRange(closedReserva, cancelledReserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = closedReserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel cerrado",
            City = "BRC",
            CheckIn = DateTime.UtcNow.AddDays(-10),
            CheckOut = DateTime.UtcNow.AddDays(-8),
            Nights = 2
        });
        context.FlightSegments.Add(new FlightSegment
        {
            ReservaId = cancelledReserva.Id,
            SupplierId = supplier.Id,
            AirlineCode = "AR",
            FlightNumber = "9999",
            Origin = "EZE",
            Destination = "BRC",
            DepartureTime = DateTime.UtcNow.AddDays(-10),
            ArrivalTime = DateTime.UtcNow.AddDays(-10).AddHours(2)
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: false);

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.False(result.IsActive);
        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.False(stored!.IsActive);
    }

    [Fact]
    public async Task UpdateSupplierAsync_DeactivateWithoutBookings_Succeeds()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Proveedor sin bookings" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: false);

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task UpdateSupplierAsync_ReactivateWithActiveBookings_Succeeds()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaWithStatusAsync(
            context,
            EstadoReserva.Confirmed,
            supplierIsActive: false);

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel reactivar",
            City = "BRC",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: true);

        // false -> true es transicion permitida sin chequeo, aunque haya reservas activas.
        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.True(result.IsActive);
    }

    [Fact]
    public async Task UpdateSupplierAsync_NoChangeInIsActive_NoGuardCheck()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaWithStatusAsync(context, EstadoReserva.Confirmed);

        // Reserva activa con booking que normalmente bloquearia desactivar,
        // pero como NO se cambia IsActive el guard no debe dispararse.
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel sin cambio",
            City = "BRC",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(12),
            Nights = 2
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: true, overrideName: "Nombre nuevo");

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.Equal("Nombre nuevo", result.Name);
        Assert.True(result.IsActive);
    }
}
