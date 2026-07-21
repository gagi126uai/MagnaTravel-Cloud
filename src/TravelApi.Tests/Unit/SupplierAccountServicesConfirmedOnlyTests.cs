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
/// P1 "circuito proveedor" (2026-07-21, D3 firmada por Gaston): el parametro opt-in
/// <see cref="SupplierAccountServicesQuery.ConfirmedOnly"/> de <c>GetSupplierAccountServicesAsync</c>.
/// "Nueva factura del proveedor" solo debe ofrecer servicios que YA generan deuda con el operador (la
/// regla oficial <c>WorkflowStatusHelper.CountsForSupplierDebtByType</c>); "Servicios comprados" y el
/// detalle de la grilla de pago deben seguir viendo TODO (default false, sin el parametro no cambia nada).
/// </summary>
public class SupplierAccountServicesConfirmedOnlyTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Un proveedor con un hotel CONFIRMADO (genera deuda) y otro SOLICITADO (todavia no).</summary>
    private static async Task<(AppDbContext Context, Supplier Supplier)> SeedSupplierWithMixedServicesAsync()
    {
        var context = CreateContext();
        var supplier = new Supplier { Name = "Operador mixto", IsActive = true };
        var reserva = new Reserva
        {
            NumeroReserva = "F-CONFONLY-1", Name = "Reserva confirmedOnly", Status = EstadoReserva.Confirmed,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel confirmado", NetCost = 40_000m, SalePrice = 60_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12),
        });
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Solicitado",
            HotelName = "Hotel solicitado", NetCost = 25_000m, SalePrice = 35_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(20), CheckOut = DateTime.UtcNow.Date.AddDays(22),
        });
        await context.SaveChangesAsync();

        return (context, supplier);
    }

    [Fact]
    public async Task Default_SinConfirmedOnly_DevuelveTodosLosServicios_ComoHoy()
    {
        var (context, supplier) = await SeedSupplierWithMixedServicesAsync();
        var service = new SupplierService(context);

        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Contains(page.Items, i => i.Status == "Confirmado");
        Assert.Contains(page.Items, i => i.Status == "Solicitado");
    }

    [Fact]
    public async Task ConfirmedOnly_DevuelveSoloElServicioConfirmado()
    {
        var (context, supplier) = await SeedSupplierWithMixedServicesAsync();
        var service = new SupplierService(context);

        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery { ConfirmedOnly = true }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("Confirmado", item.Status);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task ConfirmedOnly_ProveedorSinServiciosConfirmados_DevuelvePaginaVacia()
    {
        var context = CreateContext();
        var supplier = new Supplier { Name = "Operador sin confirmados", IsActive = true };
        var reserva = new Reserva
        {
            NumeroReserva = "F-CONFONLY-2", Name = "Reserva sin confirmados", Status = EstadoReserva.Confirmed,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Solicitado",
            HotelName = "Hotel solicitado", NetCost = 10_000m, SalePrice = 15_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(5), CheckOut = DateTime.UtcNow.Date.AddDays(7),
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery { ConfirmedOnly = true }, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }
}
