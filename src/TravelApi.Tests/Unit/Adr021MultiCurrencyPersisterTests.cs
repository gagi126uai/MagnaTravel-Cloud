using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-021 Capa 2 (multimoneda, 2026-06-09): tests del PERSISTER consolidado y de la sincronizacion
/// de la tabla hija <c>ReservaMoneyByCurrency</c>.
///
/// <para>Cubre: el persister escribe escalar surrogate + filas hijas en la misma SaveChanges; una
/// reserva USD+ARS deja dos filas; al cambiar el detalle (cancelar el unico servicio de una moneda) la
/// fila de esa moneda se borra (upsert + delete); y el invariante <c>surrogate == sum(max(0, hija))</c>.
/// Usa el provider InMemory (no requiere Postgres); aplica los query filters del modelo.</para>
/// </summary>
public class Adr021MultiCurrencyPersisterTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    [Fact]
    public async Task Persist_MonoArs_WritesScalarAndSingleChildRow()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R1", Status = EstadoReserva.InManagement };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 400m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var reloaded = await db.Reservas.FindAsync(reserva.Id);
        Assert.Equal(600m, reloaded!.Balance); // escalar surrogate = saldo crudo mono-moneda

        var rows = await db.ReservaMoneyByCurrency.Where(x => x.ReservaId == reserva.Id).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("ARS", row.Currency);
        Assert.Equal(1000m, row.ConfirmedSale);
        Assert.Equal(400m, row.TotalPaid);
        Assert.Equal(600m, row.Balance);
    }

    [Fact]
    public async Task Persist_MultiCurrency_WritesTwoChildRows_AndSurrogateSumsPositives()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R2", Status = EstadoReserva.InManagement };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m });
        reserva.FlightSegments.Add(new FlightSegment
        {
            Status = "HK", Currency = "USD", TicketIssuedAt = DateTime.UtcNow, SalePrice = 300m, NetCost = 200m
        });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 400m, Currency = "ARS" });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 100m, Currency = "USD" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var rows = await db.ReservaMoneyByCurrency
            .Where(x => x.ReservaId == reserva.Id)
            .ToDictionaryAsync(x => x.Currency);

        Assert.Equal(2, rows.Count);
        Assert.Equal(600m, rows["ARS"].Balance);
        Assert.Equal(200m, rows["USD"].Balance);

        var reloaded = await db.Reservas.FindAsync(reserva.Id);
        // Surrogate = 600 + 200 (ambas deben). Invariante: surrogate == sum(max(0, hija)).
        Assert.Equal(800m, reloaded!.Balance);
        Assert.Equal(rows.Values.Where(r => r.Balance > 0).Sum(r => r.Balance), reloaded.Balance);
    }

    [Fact]
    public async Task Persist_WhenCurrencyDisappears_RemovesStaleChildRow()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R3", Status = EstadoReserva.InManagement };
        var usdFlight = new FlightSegment
        {
            Status = "HK", Currency = "USD", TicketIssuedAt = DateTime.UtcNow, SalePrice = 300m, NetCost = 200m
        };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m });
        reserva.FlightSegments.Add(usdFlight);
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        // Primer recalculo: dos filas (ARS y USD).
        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);
        Assert.Equal(2, await db.ReservaMoneyByCurrency.CountAsync(x => x.ReservaId == reserva.Id));

        // Cancelamos el unico servicio USD -> la moneda USD desaparece del detalle.
        var trackedFlight = await db.FlightSegments.FirstAsync(f => f.Id == usdFlight.Id);
        trackedFlight.Status = "UN"; // codigo cancelado
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var rows = await db.ReservaMoneyByCurrency.Where(x => x.ReservaId == reserva.Id).ToListAsync();
        var row = Assert.Single(rows); // la fila USD se borro
        Assert.Equal("ARS", row.Currency);
    }

    [Fact]
    public async Task Persist_ReservaNotFound_DoesNothing()
    {
        await using var db = NewContext();
        // No debe lanzar ni crear filas para un id inexistente (mismo contrato que las rutinas viejas).
        await ReservaMoneyPersister.PersistAsync(db, reservaId: 99999, CancellationToken.None);
        Assert.Empty(await db.ReservaMoneyByCurrency.ToListAsync());
    }

    // ===================== (e) Backfill idempotente =====================

    [Fact]
    public async Task Backfill_PopulatesChildRows_ForReservasWithNonZeroBalance_AndIsIdempotent()
    {
        await using var db = NewContext();

        // Reserva con saldo legacy (escalar Balance != 0) pero SIN filas hijas (estado pre-backfill).
        var reserva = new Reserva { Name = "Legacy", Status = EstadoReserva.Closed, Balance = 600m };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 400m, Currency = "ARS" });
        db.Reservas.Add(reserva);

        // Reserva saldada (Balance == 0): el backfill NO debe crear filas para ella.
        var settled = new Reserva { Name = "Settled", Status = EstadoReserva.Closed, Balance = 0m };
        db.Reservas.Add(settled);

        await db.SaveChangesAsync();

        var supplierService = new TravelApi.Infrastructure.Services.SupplierService(db);
        var backfill = new MultiCurrencyBackfillService(db, supplierService);

        Assert.True(await backfill.NeedsBackfillAsync());

        var (reservasDone, _) = await backfill.RunAsync();
        Assert.Equal(1, reservasDone); // solo la que tiene saldo != 0

        var rows = await db.ReservaMoneyByCurrency.Where(x => x.ReservaId == reserva.Id).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal(600m, row.Balance);
        Assert.Empty(await db.ReservaMoneyByCurrency.Where(x => x.ReservaId == settled.Id).ToListAsync());

        // Idempotente: ya no necesita backfill y un segundo run no duplica.
        Assert.False(await backfill.NeedsBackfillAsync());
        await backfill.RunAsync();
        Assert.Single(await db.ReservaMoneyByCurrency.Where(x => x.ReservaId == reserva.Id).ToListAsync());
    }
}
