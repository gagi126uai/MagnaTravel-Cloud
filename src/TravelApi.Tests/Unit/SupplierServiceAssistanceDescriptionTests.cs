using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Hallazgo #47 (barrido T5, 2026-07-24): en la cuenta del operador, la fila de pago y el selector de
/// servicio mostraban la descripcion de una Asistencia con parentesis vacios "()" cuando el servicio no
/// tenia <see cref="AssistanceBooking.CoverageZone"/> cargada (ej. "Seguro ()"). La composicion vieja
/// concatenaba " (" + CoverageZone + ")" SIEMPRE, sin mirar si habia dato. Estos tests blindan
/// <see cref="SupplierService.GetSupplierAccountServicesAsync"/> (que arma la Description via
/// <c>BuildSupplierServicesQuery</c>), la solapa "Servicios comprados" / selector de imputacion de pagos.
///
/// <para><b>B2 (plan 2026-07-31 tarde) — Hotel/Traslado/Vuelo, hueco de QA (tanda Q)</b>: el mismo
/// barrido de "()" vacios (hallazgo H9, 2026-07-25) ya esta arreglado en <c>BuildSupplierServicesQuery</c>
/// para Hotel/Traslado/Vuelo, y ya tiene tests de INTEGRACION contra Postgres real
/// (<c>SupplierServiceAssistanceDescriptionIntegrationTests</c>, obligatorios porque el fix vive DENTRO
/// de un <c>Select()</c> LINQ-to-SQL que InMemory no valida). Lo que faltaba era la red RAPIDA de
/// contenido (sin Postgres, corre en cualquier `dotnet test --filter Unit`) — estos 6 tests de abajo
/// cierran ese hueco, mismo patron que los de Asistencia de arriba.</para>
/// </summary>
public class SupplierServiceAssistanceDescriptionTests
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
        var supplier = new Supplier { Name = "Assist SA" };
        var reserva = new Reserva
        {
            NumeroReserva = "F-2026-ASIST",
            Name = "Reserva asistencia",
            Status = EstadoReserva.Confirmed
        };

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        return (supplier, reserva);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_AsistenciaSinCoverageZone_NoMuestraParentesisVacios()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PlanType = "Full Cobertura",
            CoverageZone = null, // sin dato: antes esto generaba "Full Cobertura ()"
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Full Cobertura", item.Description);
        Assert.DoesNotContain("(", item.Description);
        Assert.DoesNotContain(")", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_AsistenciaConCoverageZone_MuestraElParentesisConDato()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PlanType = "Full Cobertura",
            CoverageZone = "Mundial",
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Full Cobertura (Mundial)", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_AsistenciaSinPlanTypeNiCoverageZone_UsaElFallbackSeguro()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.AssistanceBookings.Add(new AssistanceBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            PlanType = null,
            CoverageZone = null,
            Status = "Solicitado",
            ValidFrom = DateTime.UtcNow,
            ValidTo = DateTime.UtcNow.AddDays(10),
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Seguro", item.Description);
    }

    // ===================================================================================================
    // B2/H9 — Hotel, Traslado y Vuelo: mismo bug de fondo, red rapida sin Postgres (ver docstring de arriba).
    // ===================================================================================================

    [Fact]
    public async Task GetSupplierAccountServicesAsync_HotelSinCiudad_NoMuestraParentesisVacios()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Palace",
            City = string.Empty, // sin dato: antes generaba "Hotel Palace ()"
            Status = "Solicitado",
            CheckIn = DateTime.UtcNow,
            CheckOut = DateTime.UtcNow.AddDays(3),
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Hotel Palace", item.Description);
        Assert.DoesNotContain("(", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_HotelConCiudad_MuestraElParentesisConDato()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            HotelName = "Hotel Palace",
            City = "Bariloche",
            Status = "Solicitado",
            CheckIn = DateTime.UtcNow,
            CheckOut = DateTime.UtcNow.AddDays(3),
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Hotel Palace (Bariloche)", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_TrasladoSinRuta_NoMuestraParentesisVacios()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            VehicleType = "Sedan",
            PickupLocation = null, // sin dato: antes generaba "Sedan ( -> )"
            DropoffLocation = null,
            Status = "Solicitado",
            PickupDateTime = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Sedan", item.Description);
        Assert.DoesNotContain("(", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_TrasladoConRuta_MuestraElParentesisConLaRuta()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.TransferBookings.Add(new TransferBooking
        {
            ReservaId = reserva.Id,
            SupplierId = supplier.Id,
            VehicleType = "Sedan",
            PickupLocation = "Aeropuerto",
            DropoffLocation = "Hotel",
            Status = "Solicitado",
            PickupDateTime = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("Sedan (Aeropuerto -> Hotel)", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_VueloSinOrigenNiDestino_NoMuestraParentesisVacios()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.FlightSegments.Add(new FlightSegment
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
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("AA 1234", item.Description);
        Assert.DoesNotContain("(", item.Description);
    }

    [Fact]
    public async Task GetSupplierAccountServicesAsync_VueloConOrigenYDestino_MuestraElParentesisConElTramo()
    {
        await using var context = CreateContext();
        var (supplier, reserva) = await SeedSupplierAndReservaAsync(context);

        context.FlightSegments.Add(new FlightSegment
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
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var result = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("AA 1234 (EZE-MIA)", item.Description);
    }
}
