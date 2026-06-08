using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-020 (BLOQUEANTE 5, INV-020-04): el borrado fisico de una reserva en Cotizacion/Presupuesto
/// se bloquea si algun servicio ya fue confirmado por el operador (ConfirmedAt sellado = compromiso
/// o deuda con el proveedor). Hay que cancelar esos servicios primero.
/// </summary>
public class Adr020ReservaDeleteGuardTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static Reserva Reserva(int id, string status) => new()
    {
        Id = id, NumeroReserva = $"F-{id}", Name = $"Reserva {id}", Status = status
    };

    [Fact]
    public async Task ReservaDelete_QuotationWithOperatorConfirmedHotel_IsBlocked()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Quotation));
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Confirmado", ConfirmedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var reason = await DeleteGuards.GetReservaDeleteBlockReasonAsync(ctx, 1);

        Assert.NotNull(reason);
        Assert.Contains("confirmados con el operador", reason);
    }

    [Fact]
    public async Task ReservaDelete_BudgetWithConfirmedFlight_IsBlocked()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Budget));
        // Aereo confirmado por operador (ConfirmedAt sellado) aunque no este emitido: ya hay compromiso.
        ctx.FlightSegments.Add(new FlightSegment { Id = 20, ReservaId = 1, Status = "HK", ConfirmedAt = DateTime.UtcNow });
        await ctx.SaveChangesAsync();

        var reason = await DeleteGuards.GetReservaDeleteBlockReasonAsync(ctx, 1);

        Assert.NotNull(reason);
    }

    [Fact]
    public async Task ReservaDelete_QuotationWithNoConfirmedServices_IsAllowed()
    {
        await using var ctx = NewContext();
        ctx.Reservas.Add(Reserva(1, EstadoReserva.Quotation));
        // Servicio solo solicitado (sin ConfirmedAt): es un borrador sin compromiso -> se puede borrar.
        ctx.HotelBookings.Add(new HotelBooking { Id = 10, ReservaId = 1, HotelName = "H", Status = "Solicitado", ConfirmedAt = null });
        await ctx.SaveChangesAsync();

        var reason = await DeleteGuards.GetReservaDeleteBlockReasonAsync(ctx, 1);

        Assert.Null(reason);
    }
}
