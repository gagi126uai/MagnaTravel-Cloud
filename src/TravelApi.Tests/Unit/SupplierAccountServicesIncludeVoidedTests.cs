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
/// Obra "la ficha del operador no borra la historia" (2026-08-20, punto 2, F-6): "Servicios comprados"
/// (<c>GET /suppliers/{id}/account/services</c>) deja de excluir SIEMPRE los servicios de reservas
/// anuladas. Por default (<c>SupplierAccountServicesQuery.IncludeVoided = true</c>) se ven, con
/// <see cref="SupplierAccountServiceListItemDto.ReservaIsVoided"/> en true para que el front pinte el chip
/// "Anulada"; con <c>includeVoided=false</c> se ocultan (checkbox "Mostrar anuladas" destildado).
/// </summary>
public class SupplierAccountServicesIncludeVoidedTests
{
    private static AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new AppDbContext(options);
    }

    /// <summary>Un proveedor con UN servicio de una reserva VIVA y otro de una reserva ANULADA.</summary>
    private static async Task<(AppDbContext Context, Supplier Supplier)> SeedSupplierWithVoidedAndLiveReservaAsync()
    {
        var context = CreateContext();
        var supplier = new Supplier { Name = "Operador con anuladas", IsActive = true };
        var reservaViva = new Reserva
        {
            NumeroReserva = "F-VIVA-1", Name = "Reserva viva", Status = EstadoReserva.Confirmed,
        };
        var reservaAnulada = new Reserva
        {
            NumeroReserva = "F-ANU-1", Name = "Reserva anulada", Status = EstadoReserva.Cancelled,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.AddRange(reservaViva, reservaAnulada);
        await context.SaveChangesAsync();

        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaViva.Id, SupplierId = supplier.Id, Status = "Confirmado",
            HotelName = "Hotel vivo", NetCost = 40_000m, SalePrice = 60_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(10), CheckOut = DateTime.UtcNow.Date.AddDays(12),
        });
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reservaAnulada.Id, SupplierId = supplier.Id,
            Status = WorkflowStatuses.Cancelado, StatusBeforeCancellation = "Confirmado", CancelledAt = DateTime.UtcNow,
            HotelName = "Hotel anulado", NetCost = 25_000m, SalePrice = 35_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(20), CheckOut = DateTime.UtcNow.Date.AddDays(22),
        });
        await context.SaveChangesAsync();

        return (context, supplier);
    }

    [Fact]
    public async Task Default_SinPasarIncludeVoided_MuestraLaVivaYLaAnulada_ConElFlagCorrecto()
    {
        var (context, supplier) = await SeedSupplierWithVoidedAndLiveReservaAsync();
        var service = new SupplierService(context);

        // SupplierAccountServicesQuery() (default del ctor) ya trae IncludeVoided = true.
        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery(), CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        var viva = Assert.Single(page.Items, i => i.Description!.Contains("Hotel vivo"));
        var anulada = Assert.Single(page.Items, i => i.Description!.Contains("Hotel anulado"));
        Assert.False(viva.ReservaIsVoided);
        Assert.True(anulada.ReservaIsVoided);
    }

    [Fact]
    public async Task IncludeVoidedFalse_OcultaLosServiciosDeReservasAnuladas()
    {
        var (context, supplier) = await SeedSupplierWithVoidedAndLiveReservaAsync();
        var service = new SupplierService(context);

        var page = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery { IncludeVoided = false }, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Contains("Hotel vivo", item.Description);
        Assert.False(item.ReservaIsVoided);
    }

    [Fact]
    public async Task ReservaPendingOperatorRefund_TambienCuentaComoAnulada_ConIncludeVoided()
    {
        // isVoided cubre el PAR Cancelled + PendingOperatorRefund (EstadoReserva.VoidedStatuses) — mismo
        // criterio que ya usa el resto de la app (isReservaAnulada en el frontend, IsVoidedStatus en el
        // dominio). Se prueba el otro miembro del par para no dejarlo sin cobertura.
        var context = CreateContext();
        var supplier = new Supplier { Name = "Operador pendiente reembolso", IsActive = true };
        var reserva = new Reserva
        {
            NumeroReserva = "F-PEND-1", Name = "Reserva pendiente de reembolso",
            Status = EstadoReserva.PendingOperatorRefund,
        };
        context.Suppliers.Add(supplier);
        context.Reservas.Add(reserva);
        await context.SaveChangesAsync();
        context.HotelBookings.Add(new HotelBooking
        {
            ReservaId = reserva.Id, SupplierId = supplier.Id,
            Status = WorkflowStatuses.Cancelado, StatusBeforeCancellation = "Confirmado", CancelledAt = DateTime.UtcNow,
            HotelName = "Hotel esperando reembolso", NetCost = 10_000m, SalePrice = 15_000m, Currency = "ARS",
            CheckIn = DateTime.UtcNow.Date.AddDays(5), CheckOut = DateTime.UtcNow.Date.AddDays(7),
        });
        await context.SaveChangesAsync();

        var service = new SupplierService(context);
        var withVoided = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery { IncludeVoided = true }, CancellationToken.None);
        var withoutVoided = await service.GetSupplierAccountServicesAsync(
            supplier.Id, new SupplierAccountServicesQuery { IncludeVoided = false }, CancellationToken.None);

        Assert.Equal(1, withVoided.TotalCount);
        Assert.True(Assert.Single(withVoided.Items).ReservaIsVoided);
        Assert.Empty(withoutVoided.Items);
    }

    // ===================================================================================================
    // Regresion (F-6, "que NO cambia" de la spec): "Deuda por reserva" sigue mostrando SOLO deuda VIVA —
    // una reserva anulada no debe aparecer ahi ni sumar nada, sin importar el cambio de "Servicios comprados".
    // ===================================================================================================

    [Fact]
    public async Task DebtByReserva_NuncaMuestraUnaReservaAnulada_AunqueSuCompraHayaSidoConfirmada()
    {
        var (context, supplier) = await SeedSupplierWithVoidedAndLiveReservaAsync();
        var service = new SupplierService(context);

        var debt = await service.GetSupplierDebtByReservaAsync(supplier.Id, CancellationToken.None);

        var numerosDeReserva = debt.Reservas.Select(r => r.NumeroReserva).ToList();
        Assert.Contains("F-VIVA-1", numerosDeReserva);
        Assert.DoesNotContain("F-ANU-1", numerosDeReserva);
    }
}
