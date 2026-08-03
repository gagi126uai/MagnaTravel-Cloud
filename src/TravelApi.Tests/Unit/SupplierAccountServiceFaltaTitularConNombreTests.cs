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
/// F4 (plan 2026-07-31 tarde, hueco "candado pre-emptivo en cuenta del operador"): el listado de
/// servicios de la cuenta del proveedor (<c>GetSupplierAccountServicesAsync</c>) trae calculado
/// <see cref="SupplierAccountServiceListItemDto.FaltaTitularConNombre"/>, para que el front apague
/// "Marcar confirmado" ANTES de intentar (P-9). Usa la MISMA regla que el gate H7
/// (<c>PassengerNominalRules.HasNamedLeadPassenger</c>), no una copia.
/// </summary>
public class SupplierAccountServiceFaltaTitularConNombreTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    private static async Task<(AppDbContext Context, Supplier Supplier)> SeedSupplierWithOneHotelAsync(
        Action<Reserva> configureReserva)
    {
        var context = CreateContext();
        var supplier = new Supplier { Name = "Operador F4", IsActive = true };
        var reserva = new Reserva
        {
            NumeroReserva = "F-F4-1", Name = "Reserva F4", Status = EstadoReserva.Confirmed,
        };
        configureReserva(reserva);

        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel F4", NetCost = 10_000m, SalePrice = 15_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12),
        });
        await context.SaveChangesAsync();

        return (context, supplier);
    }

    [Fact]
    public async Task ReservaConTitularConNombre_FaltaTitularConNombre_EsFalse()
    {
        var (context, supplier) = await SeedSupplierWithOneHotelAsync(reserva => { });
        // El titular es el primer pasajero por Id ascendente (misma definicion que GetLeadPassenger).
        context.Passengers.Add(new Passenger { ReservaId = context.Reservas.Local.Single().Id, FullName = "Juan Perez" });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.False(item.FaltaTitularConNombre);
    }

    [Fact]
    public async Task ReservaSinPasajerosCargados_FaltaTitularConNombre_EsTrue()
    {
        var (context, supplier) = await SeedSupplierWithOneHotelAsync(reserva => { });
        // Sin ningun Passenger cargado: no hay titular.

        var service = new SupplierService(context);
        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.True(item.FaltaTitularConNombre);
    }

    [Fact]
    public async Task ReservaConTitularSinNombreCargado_FaltaTitularConNombre_EsTrue()
    {
        var (context, supplier) = await SeedSupplierWithOneHotelAsync(reserva => { });
        // El pasajero existe (fila cargada) pero el nombre esta vacio: sigue faltando el titular.
        context.Passengers.Add(new Passenger { ReservaId = context.Reservas.Local.Single().Id, FullName = "   " });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.True(item.FaltaTitularConNombre);
    }
}
