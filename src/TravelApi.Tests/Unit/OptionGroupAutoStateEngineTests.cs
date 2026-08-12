using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Fix B1 "cinturón" (review de seguridad "PDF de presupuesto", 2026-08-12): aunque el candado de
/// escritura (<c>BookingService.EnsureOptionGroupOnlySetDuringPresupuesto</c>) es la defensa PRINCIPAL
/// contra grupos de opciones A/B/C ambiguos, estos tests fijan la RED DE SEGURIDAD: el motor automático
/// (<see cref="ReservaAutoStateService"/>) nunca confirma una reserva En gestión que todavía tiene un
/// grupo ambiguo, aunque todos sus servicios estén individualmente resueltos. Los datos se siembran
/// DIRECTO en la base (sin pasar por BookingService) — simula el escenario "el candado de escritura
/// tuviera un agujero", que es justo lo que esta red de seguridad tiene que cubrir.
/// </summary>
public class OptionGroupAutoStateEngineTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    [Fact]
    public async Task EvaluateAndApplyAsync_AllResolvedButAmbiguousGroup_StaysInManagement()
    {
        await using var context = NewContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.InManagement });
        // Las DOS opciones estan "resueltas" individualmente (Status = Confirmado), pero comparten
        // OptionGroup: el grupo sigue ambiguo, nadie eligio todavia cual quedo.
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Hotel A", City = "Bariloche",
            Status = "Confirmado", SalePrice = 1000m, OptionGroup = "hoteles", OptionLabel = "A"
        });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 2, ReservaId = 1, HotelName = "Hotel B", City = "Bariloche",
            Status = "Confirmado", SalePrice = 1500m, OptionGroup = "hoteles", OptionLabel = "B"
        });
        await context.SaveChangesAsync();

        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        // OJO: EvaluateAndApplyAsync devuelve true tambien cuando solo estampa ConfirmedAt en los
        // servicios (StampConfirmedAt), asi que el valor de retorno NO alcanza para verificar "no
        // confirmo la reserva" — lo que importa acá es el Status persistido, que es lo que chequeamos.
        await engine.EvaluateAndApplyAsync(1);

        var reserva = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.InManagement, reserva.Status);
    }

    [Fact]
    public async Task EvaluateAndApplyAsync_AllResolvedNoAmbiguousGroup_ConfirmsNormally()
    {
        // Regresion: sin grupos de opciones (el 100% de las reservas hoy), el auto-confirm sigue
        // funcionando exactamente igual que siempre.
        await using var context = NewContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.InManagement });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Hotel Unico", City = "Bariloche",
            Status = "Confirmado", SalePrice = 1000m
        });
        await context.SaveChangesAsync();

        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        var changed = await engine.EvaluateAndApplyAsync(1);

        Assert.True(changed);
        var reserva = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
    }

    [Fact]
    public async Task EvaluateAndApplyAsync_ResolvedGroupWithSoleSurvivor_ConfirmsNormally()
    {
        // Estado DESPUES de resolver el grupo (BookingService.ResolveOptionGroupAsync borro al
        // perdedor): solo queda una opcion viva con OptionGroup cargado -> ya no es ambiguo, confirma.
        await using var context = NewContext();
        context.Reservas.Add(new Reserva { Id = 1, Name = "Test", Status = EstadoReserva.InManagement });
        context.HotelBookings.Add(new HotelBooking
        {
            Id = 1, ReservaId = 1, HotelName = "Hotel A", City = "Bariloche",
            Status = "Confirmado", SalePrice = 1000m, OptionGroup = "hoteles", OptionLabel = "A"
        });
        await context.SaveChangesAsync();

        var engine = new ReservaAutoStateService(context, NullLogger<ReservaAutoStateService>.Instance);
        var changed = await engine.EvaluateAndApplyAsync(1);

        Assert.True(changed);
        var reserva = await context.Reservas.AsNoTracking().SingleAsync(r => r.Id == 1);
        Assert.Equal(EstadoReserva.Confirmed, reserva.Status);
    }
}
