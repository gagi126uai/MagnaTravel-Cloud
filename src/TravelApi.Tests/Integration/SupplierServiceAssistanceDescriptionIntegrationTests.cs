using System;
using System.Threading;
using System.Threading.Tasks;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using TravelApi.Tests.Fixtures;
using Xunit;

namespace TravelApi.Tests.Integration;

/// <summary>
/// Hallazgo #47 (barrido T5, 2026-07-24): en la cuenta del operador, la descripcion de una Asistencia
/// sin <see cref="AssistanceBooking.CoverageZone"/> mostraba parentesis vacios "()" (ej. "Seguro ()").
/// El fix en <c>SupplierService.BuildSupplierServicesQuery</c> usa <c>string.IsNullOrWhiteSpace</c>
/// DENTRO de un <c>Select()</c> de EF Core (LINQ-to-SQL, no LINQ-to-Objects).
///
/// <para><b>Por que este test necesita Postgres real (no InMemory)</b>: el proveedor InMemory de EF Core
/// no traduce la expresion a SQL — la ejecuta como delegado C# directo, asi que un <c>Select</c> roto
/// que InMemory acepta puede tirar <c>InvalidOperationException</c> ("could not be translated") recien
/// contra un motor SQL real como Postgres (igual que produccion). Los tests InMemory de
/// <c>SupplierServiceAssistanceDescriptionTests</c> ya prueban el RESULTADO (que arma bien el texto);
/// este prueba que la EXPRESION es traducible por Npgsql, la red que falta.</para>
///
/// <para><b>Hallazgo H9 (barrido E2E 2026-07-25)</b>: el mismo bug de fondo del #47 (parentesis vacios
/// cuando falta el dato de detalle) tambien estaba en Hotel ("Hotel Palace ()"), Traslado ("Sedan
/// ( -> )") y Vuelo ("AA 1234 (-)"). El fix en <c>SupplierService.BuildSupplierServicesQuery</c> replica
/// la MISMA regla (ternarios traducibles con <c>string.IsNullOrWhiteSpace</c>) para los 3 tipos; estos
/// tests amplian esta clase para blindar tambien su traduccion a SQL, ademas del caso de Asistencia.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SupplierServiceAssistanceDescriptionIntegrationTests
    : IClassFixture<PostgresIntegrationFixture>, IAsyncLifetime
{
    private readonly PostgresIntegrationFixture _fixture;

    public SupplierServiceAssistanceDescriptionIntegrationTests(PostgresIntegrationFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync() => _fixture.ResetDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetSupplierAccountServicesAsync_AsistenciaSinCoverageZone_TraduceASqlSinParentesisVacios()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Assist Integration SA" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-ASIST-{Guid.NewGuid():N}"[..14],
            Name = "Reserva asistencia sin zona",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PlanType = "Full Cobertura",
            CoverageZone = null, // sin dato: antes esto generaba "Full Cobertura ()"
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        // ACT: si string.IsNullOrWhiteSpace(assistance.CoverageZone) dentro del Select() no fuera
        // traducible por Npgsql, esta linea tiraria InvalidOperationException ANTES de llegar al
        // assert de contenido — esa es la red que un test InMemory no puede tender.
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Full Cobertura", item.Description);
        Assert.DoesNotContain("(", item.Description);
        Assert.DoesNotContain(")", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_AsistenciaConCoverageZone_TraduceASqlConElParentesisYElDato()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Assist Integration SA 2" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-ASIST-{Guid.NewGuid():N}"[..14],
            Name = "Reserva asistencia con zona",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PlanType = "Full Cobertura",
            CoverageZone = "Mundial",
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Full Cobertura (Mundial)", item.Description);
    }

    // ============================================================
    // H9: Hotel — antes "Hotel Palace ()" cuando faltaba la ciudad.
    // ============================================================

    [Fact]
    public async Task GetSupplierAccountServicesAsync_HotelSinCiudad_TraduceASqlSinParentesisVacios()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Hotel Integration SA" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-HOTEL-{Guid.NewGuid():N}"[..14],
            Name = "Reserva hotel sin ciudad",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Palace",
            City = string.Empty, // sin dato: antes generaba "Hotel Palace ()"
            Status = "Solicitado",
            CheckIn = DateTime.UtcNow,
            CheckOut = DateTime.UtcNow.AddDays(3),
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Hotel Palace", item.Description);
        Assert.DoesNotContain("(", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_HotelConCiudad_TraduceASqlConElParentesisYElDato()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Hotel Integration SA 2" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-HOTEL-{Guid.NewGuid():N}"[..14],
            Name = "Reserva hotel con ciudad",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Palace",
            City = "Bariloche",
            Status = "Solicitado",
            CheckIn = DateTime.UtcNow,
            CheckOut = DateTime.UtcNow.AddDays(3),
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Hotel Palace (Bariloche)", item.Description);
    }

    // ============================================================
    // H9: Traslado — antes "Sedan ( -> )" cuando faltaban pickup Y dropoff.
    // ============================================================

    [Fact]
    public async Task GetSupplierAccountServicesAsync_TrasladoSinRuta_TraduceASqlSinParentesisVacios()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Traslado Integration SA" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-TRASL-{Guid.NewGuid():N}"[..14],
            Name = "Reserva traslado sin ruta",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            VehicleType = "Sedan",
            PickupLocation = null, // sin dato: antes generaba "Sedan ( -> )"
            DropoffLocation = null,
            Status = "Solicitado",
            PickupDateTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Sedan", item.Description);
        Assert.DoesNotContain("(", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_TrasladoConRuta_TraduceASqlConElParentesisYLaRuta()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Traslado Integration SA 2" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-TRASL-{Guid.NewGuid():N}"[..14],
            Name = "Reserva traslado con ruta",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            VehicleType = "Sedan",
            PickupLocation = "Aeropuerto",
            DropoffLocation = "Hotel",
            Status = "Solicitado",
            PickupDateTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Sedan (Aeropuerto -> Hotel)", item.Description);
    }

    // ============================================================
    // H9: Vuelo — antes "AA 1234 (-)" cuando faltaban origen Y destino.
    // ============================================================

    [Fact]
    public async Task GetSupplierAccountServicesAsync_VueloSinOrigenNiDestino_TraduceASqlSinParentesisVacios()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Vuelo Integration SA" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-VUELO-{Guid.NewGuid():N}"[..14],
            Name = "Reserva vuelo sin ruta",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            AirlineName = "AA",
            FlightNumber = "1234",
            Origin = null, // sin dato: antes generaba "AA 1234 (-)"
            Destination = null,
            Status = "NN",
            DepartureTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("AA 1234", item.Description);
        Assert.DoesNotContain("(", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_VueloConOrigenYDestino_TraduceASqlConElParentesisYElTramo()
    {
        await using var ctx = _fixture.CreateDbContext();

        var supplier = new Supplier { Name = "Vuelo Integration SA 2" };
        ctx.Suppliers.Add(supplier);
        var reserva = new Reserva
        {
            NumeroReserva = $"F-VUELO-{Guid.NewGuid():N}"[..14],
            Name = "Reserva vuelo con ruta",
            Status = EstadoReserva.Confirmed,
        };
        ctx.Reservas.Add(reserva);
        await ctx.SaveChangesAsync();

        ctx.FlightSegments.Add(new FlightSegment
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            AirlineName = "AA",
            FlightNumber = "1234",
            Origin = "EZE",
            Destination = "MIA",
            Status = "NN",
            DepartureTime = DateTime.UtcNow,
        });
        await ctx.SaveChangesAsync();

        var service = new SupplierService(ctx);

        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("AA 1234 (EZE-MIA)", item.Description);
    }
}
