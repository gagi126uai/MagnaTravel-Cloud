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
}
