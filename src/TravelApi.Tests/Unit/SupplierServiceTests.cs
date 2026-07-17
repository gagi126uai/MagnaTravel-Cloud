using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Moq;
using TravelApi.Application.Constants;
using TravelApi.Application.Interfaces;
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

    // ----------------------------------------------------------------------
    // Rediseño alta de operador (2026-06-28): moneda por defecto del operador.
    // El alta acepta una moneda por defecto (ARS/USD), la valida server-side y
    // permite guardar SIN datos fiscales (el escape "datos fiscales pendientes"
    // se enforza en la UI; el servidor NO exige CUIT/condicion fiscal).
    // ----------------------------------------------------------------------

    [Fact]
    public async Task CreateSupplierAsync_WithDefaultCurrencyUsd_PersistsCanonicalCurrency()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        // El front manda "usd" en minuscula: se acepta y se guarda canonico "USD".
        var incoming = new Supplier { Name = "Operador USD", DefaultCurrency = "usd" };

        var result = await service.CreateSupplierAsync(incoming, CancellationToken.None);

        Assert.Equal(Monedas.USD, result.DefaultCurrency);
        var stored = await context.Suppliers.FindAsync(result.Id);
        Assert.Equal(Monedas.USD, stored!.DefaultCurrency);
    }

    [Fact]
    public async Task CreateSupplierAsync_WithoutDefaultCurrency_DefaultsToArs()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        // Moneda vacia: el servidor la resuelve a ARS (moneda por defecto del sistema), no es un error.
        var incoming = new Supplier { Name = "Operador sin moneda", DefaultCurrency = "  " };

        var result = await service.CreateSupplierAsync(incoming, CancellationToken.None);

        Assert.Equal(Monedas.ARS, result.DefaultCurrency);
    }

    [Fact]
    public async Task CreateSupplierAsync_WithUnsupportedCurrency_ThrowsFriendlySpanishMessage()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        var incoming = new Supplier { Name = "Operador moneda mala", DefaultCurrency = "EUR" };

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CreateSupplierAsync(incoming, CancellationToken.None));

        // Mensaje de negocio en espanol que NO filtra el valor recibido ni el catalogo interno.
        Assert.Equal("La moneda por defecto del proveedor no es válida.", ex.Message);
        Assert.DoesNotContain("EUR", ex.Message);
        // No se persistio nada.
        Assert.Equal(0, await context.Suppliers.CountAsync());
    }

    [Fact]
    public async Task CreateSupplierAsync_WithoutTaxIdOrTaxCondition_Succeeds()
    {
        await using var context = CreateContext();
        var service = new SupplierService(context);

        // Escape "datos fiscales pendientes": el alta debe poder guardar sin CUIT ni condicion fiscal.
        var incoming = new Supplier
        {
            Name = "Operador fiscalmente incompleto",
            DefaultCurrency = Monedas.ARS,
            TaxId = null,
            TaxCondition = null
        };

        var result = await service.CreateSupplierAsync(incoming, CancellationToken.None);

        Assert.True(result.Id > 0);
        Assert.Null(result.TaxId);
        Assert.Null(result.TaxCondition);
    }

    [Fact]
    public async Task UpdateSupplierAsync_WithUnsupportedCurrency_ThrowsAndDoesNotPersist()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador edit moneda", DefaultCurrency = Monedas.ARS };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var incoming = BuildIncomingSupplier(supplier, isActive: true);
        incoming.DefaultCurrency = "BRL"; // no soportada

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));
        Assert.Equal("La moneda por defecto del proveedor no es válida.", ex.Message);

        // La moneda guardada sigue siendo la original (la edicion no se aplico).
        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.Equal(Monedas.ARS, stored!.DefaultCurrency);
    }

    [Fact]
    public async Task UpdateSupplierAsync_WithoutDefaultCurrency_PreservesExistingCurrency()
    {
        // Regresion (perdida de dato): los forms de edicion existentes NO mandan defaultCurrency. Editar
        // cualquier otro campo (ej. el telefono) NO debe resetear la moneda de un operador USD a ARS.
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador USD", DefaultCurrency = Monedas.USD, Phone = "111" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        // Simulamos el request del form de edicion: trae los otros campos pero NO la moneda (null).
        var incoming = BuildIncomingSupplier(supplier, isActive: true);
        incoming.Phone = "222";
        incoming.DefaultCurrency = null;

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.Equal("222", result.Phone);
        Assert.Equal(Monedas.USD, result.DefaultCurrency); // se preservo, NO se reseteo a ARS

        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.Equal(Monedas.USD, stored!.DefaultCurrency);
    }

    // ================================================================
    // Regla del dueño (2026-07-17): "el CUIT es una identidad; la condicion fiscal es un
    // dato de HOY". CODE-13 se separa en dos ejes: el eje TaxId sigue bloqueado con factura
    // viva de una reserva del proveedor (igual que antes); el eje TaxCondition se permite
    // editar SIEMPRE, con auditoria.
    // ================================================================

    private static async Task<(Supplier supplier, Reserva reserva)> SeedSupplierWithLiveInvoiceAsync(AppDbContext context, int supplierId)
    {
        var supplier = new Supplier { Id = supplierId, Name = "Operador con factura", TaxId = null, TaxCondition = null };
        var reserva = new Reserva
        {
            Id = supplierId,
            NumeroReserva = $"F-SUP-{supplierId:D4}",
            Name = "Reserva facturada",
            Status = EstadoReserva.Confirmed
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            Id = supplierId,
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel con factura",
            CheckIn = DateTime.UtcNow.AddDays(10),
            CheckOut = DateTime.UtcNow.AddDays(13),
            SalePrice = 500m,
            NetCost = 300m
        });
        context.Invoices.Add(new Invoice
        {
            Id = supplierId,
            ReservaId = reserva.Id,
            CAE = "012345",
            AnnulmentStatus = AnnulmentStatus.None,
            TipoComprobante = 6, // Factura B
            ImporteTotal = 100m,
            ImporteNeto = 82.64m,
            ImporteIva = 17.36m
        });
        await context.SaveChangesAsync();
        return (supplier, reserva);
    }

    /// <summary>
    /// Cambiar SOLO la condicion fiscal (null -> valor) NO pasa por el guard CODE-13 aunque el proveedor tenga
    /// una reserva con factura CAE viva referenciandolo: la condicion es un dato de HOY, no una identidad. Queda
    /// auditada (accion SupplierTaxConditionChanged) con el viejo -&gt; nuevo valor.
    /// </summary>
    [Fact]
    public async Task UpdateSupplierAsync_ChangingOnlyTaxCondition_WithLiveInvoice_AllowsAndAudits()
    {
        await using var context = CreateContext();
        var (supplier, _) = await SeedSupplierWithLiveInvoiceAsync(context, supplierId: 60);
        context.ChangeTracker.Clear();

        var audit = new Mock<IAuditService>();
        var service = new SupplierService(context, audit.Object);

        var incoming = BuildIncomingSupplier(supplier, isActive: true);
        incoming.TaxCondition = "IVA_RESP_INSCRIPTO"; // null -> RI

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.Equal("IVA_RESP_INSCRIPTO", result.TaxCondition);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.SupplierTaxConditionChanged,
            "Supplier",
            supplier.Id.ToString(),
            It.Is<string>(d => d.Contains("(vacio)") && d.Contains("IVA_RESP_INSCRIPTO")),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
    }

    /// <summary>Cambiar el CUIT con factura viva sigue bloqueado, con el mensaje nuevo (sin mencionar "condicion").</summary>
    [Fact]
    public async Task UpdateSupplierAsync_ChangingTaxId_WithLiveInvoice_Blocks()
    {
        await using var context = CreateContext();
        var (supplier, _) = await SeedSupplierWithLiveInvoiceAsync(context, supplierId: 61);
        context.ChangeTracker.Clear();

        var service = new SupplierService(context);

        var incoming = BuildIncomingSupplier(supplier, isActive: true);
        incoming.TaxId = "30-11111111-1"; // CAMBIA el CUIT

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));

        Assert.Contains("CUIT", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("condicion", ex.Message, StringComparison.OrdinalIgnoreCase);

        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.Null(stored!.TaxId);
    }

    /// <summary>Sin facturas vivas, cambiar el CUIT sigue permitido (comportamiento preexistente).</summary>
    [Fact]
    public async Task UpdateSupplierAsync_ChangingTaxId_WithoutInvoices_Allows()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador limpio", TaxId = null };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();

        var service = new SupplierService(context);

        var incoming = BuildIncomingSupplier(supplier, isActive: true);
        incoming.TaxId = "30-22222222-2";

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.Equal("30-22222222-2", result.TaxId);
    }

    /// <summary>
    /// N2(a) espejo del cliente: con FACTURA VIVA, si el PUT trae los DOS ejes a la vez (CUIT nuevo +
    /// condicion nueva), el bloqueo del CUIT es TOTAL — no se persiste ninguno de los dos, ni se audita el
    /// cambio de condicion.
    /// </summary>
    [Fact]
    public async Task UpdateSupplierAsync_ChangingTaxIdAndTaxCondition_WithLiveInvoice_BlocksBothAndDoesNotAudit()
    {
        await using var context = CreateContext();
        var (supplier, _) = await SeedSupplierWithLiveInvoiceAsync(context, supplierId: 62);
        context.ChangeTracker.Clear();

        var audit = new Mock<IAuditService>();
        var service = new SupplierService(context, audit.Object);

        var incoming = BuildIncomingSupplier(supplier, isActive: true);
        incoming.TaxId = "30-44444444-4"; // CAMBIA el CUIT
        incoming.TaxCondition = "IVA_RESP_INSCRIPTO"; // Y TAMBIEN cambia la condicion

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None));

        var stored = await context.Suppliers.FindAsync(supplier.Id);
        Assert.Null(stored!.TaxId);
        Assert.Null(stored.TaxCondition);

        audit.Verify(a => a.StageBusinessEvent(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    /// <summary>
    /// N2(b) espejo del cliente: cambiar SOLO el CUIT audita SupplierTaxIdChanged pero NUNCA
    /// SupplierTaxConditionChanged de rebote.
    /// </summary>
    [Fact]
    public async Task UpdateSupplierAsync_ChangingOnlyTaxId_DoesNotAuditTaxConditionChange()
    {
        await using var context = CreateContext();
        var supplier = new Supplier { Name = "Operador limpio", TaxId = "30-10000000-1", TaxCondition = "IVA_RESP_INSCRIPTO" };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var audit = new Mock<IAuditService>();
        var service = new SupplierService(context, audit.Object);

        var incoming = BuildIncomingSupplier(supplier, isActive: true);
        incoming.TaxId = "30-99999999-9"; // SOLO cambia el CUIT (TaxCondition round-tripeado sin cambios)

        await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.SupplierTaxIdChanged,
            "Supplier", supplier.Id.ToString(),
            It.Is<string>(d => d.Contains("30-10000000-1") && d.Contains("30-99999999-9")),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.SupplierTaxConditionChanged,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    /// <summary>
    /// N2(c) espejo del cliente: PUT que OMITE TaxCondition sobre un proveedor Responsable Inscripto CON
    /// factura viva: la condicion se PRESERVA y NO se audita.
    /// </summary>
    [Fact]
    public async Task UpdateSupplierAsync_OmittingTaxCondition_WithLiveInvoice_PreservesAndDoesNotAudit()
    {
        await using var context = CreateContext();
        var (supplier, _) = await SeedSupplierWithLiveInvoiceAsync(context, supplierId: 63);
        supplier.TaxCondition = "IVA_RESP_INSCRIPTO";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var audit = new Mock<IAuditService>();
        var service = new SupplierService(context, audit.Object);

        var incoming = BuildIncomingSupplier(supplier, isActive: true, overrideName: "Nombre editado");
        incoming.TaxCondition = null; // el request omite el campo

        var result = await service.UpdateSupplierAsync(supplier.Id, incoming, CancellationToken.None);

        Assert.Equal("IVA_RESP_INSCRIPTO", result.TaxCondition);

        audit.Verify(a => a.StageBusinessEvent(
            AuditActions.SupplierTaxConditionChanged,
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }
}
