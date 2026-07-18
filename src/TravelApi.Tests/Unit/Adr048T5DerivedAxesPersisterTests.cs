using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using TravelApi.Application.DTOs;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-048 T5 (2026-07-17, hardening — materializacion de los ejes secundarios): prueba que
/// <see cref="ReservaMoneyPersister.PersistAsync"/> — el UNICO escritor — realmente ESCRIBE
/// <c>Reserva.DerivedCollectionStatus</c> / <c>DerivedInvoicingStatus</c> en la MISMA pasada que el
/// resto de la plata (regla 9). Usa el provider InMemory (mismo patron que
/// <c>Adr021MultiCurrencyPersisterTests</c>): no necesita Postgres porque no hay SQL crudo involucrado
/// en este camino — el SQL crudo del backfill se prueba aparte, contra Postgres, en la migracion misma
/// (documentado en el reporte de la tanda, no como test automatizado — ver la nota de la clase de
/// backfill).
/// </summary>
public class Adr048T5DerivedAxesPersisterTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    [Fact]
    public async Task Persist_ReservaNueva_EscribeSinMovimientosYNotInvoiced()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R1", Status = EstadoReserva.InManagement };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var reloaded = await db.Reservas.FindAsync(reserva.Id);
        Assert.Equal(ReservaCollectionStatus.NoCharges, reloaded!.DerivedCollectionStatus);
        Assert.Equal(ReservaInvoicingStatus.NotInvoiced, reloaded.DerivedInvoicingStatus);
    }

    [Fact]
    public async Task Persist_ServicioResueltoPagadoEntero_EscribeSaldado()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R2", Status = EstadoReserva.InManagement };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m });
        reserva.Payments.Add(new Payment { Status = "Paid", IsDeleted = false, Amount = 1000m, Currency = "ARS" });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var reloaded = await db.Reservas.FindAsync(reserva.Id);
        Assert.Equal(ReservaCollectionStatus.Settled, reloaded!.DerivedCollectionStatus);
    }

    /// <summary>
    /// ADR-048 T3 replicado por el chokepoint del persister: factura con CAE + Nota de Credito TOTAL debe
    /// dejar el eje de facturacion en "Facturada y devuelta", NUNCA "Sin facturar" — la MISMA mentira #2
    /// que T3 cerro para el detalle/listado EN VIVO, ahora tambien para la columna MATERIALIZADA.
    /// </summary>
    [Fact]
    public async Task Persist_FacturaConCaeMasNotaDeCreditoTotal_EscribeFullyReturned()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R3", Status = EstadoReserva.Confirmed, TotalSale = 1000m };
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        db.Invoices.Add(new Invoice
        {
            ReservaId = reserva.Id, TipoComprobante = 1, ImporteTotal = 1000m, Resultado = "A"
        });
        db.Invoices.Add(new Invoice
        {
            ReservaId = reserva.Id, TipoComprobante = 3, ImporteTotal = 1000m, Resultado = "A"
        });
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);

        var reloaded = await db.Reservas.FindAsync(reserva.Id);
        Assert.Equal(ReservaInvoicingStatus.FullyReturned, reloaded!.DerivedInvoicingStatus);
    }

    /// <summary>
    /// B3 (2026-07-17, review de arquitectura): el eje materializado NUNCA puede depender de si
    /// <c>Reserva.Status</c> es <c>Cancelled</c> o <c>PendingOperatorRefund</c> — los DOS estados del par
    /// "sin efecto" tienen que quedar con el MISMO eje para el MISMO dato de plata subyacente. Si alguien
    /// agregara algun dia una rama "if (Status == Cancelled) ... else no-op" en el proyector, este test la
    /// agarra: fuerza el persister sobre la MISMA reserva primero en <c>PendingOperatorRefund</c> y despues
    /// en <c>Cancelled</c>, sin tocar la plata entre medio, y exige el mismo resultado.
    /// </summary>
    [Fact]
    public async Task Persist_ConReservaEnCualquierEstadoDelParTerminal_EscribeElMismoEje()
    {
        await using var db = NewContext();
        var reserva = new Reserva { Name = "R4", Status = EstadoReserva.PendingOperatorRefund };
        reserva.HotelBookings.Add(new HotelBooking { Status = "Confirmado", Currency = "ARS", SalePrice = 1000m, NetCost = 700m });
        db.Reservas.Add(reserva);
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);
        var pendingRefundSnapshot = (await db.Reservas.FindAsync(reserva.Id))!.DerivedCollectionStatus;

        // Se salda la deuda del operador (mismo dato de plata del CLIENTE, sin tocar) y la reserva pasa al
        // otro estado del par.
        var tracked = await db.Reservas.FindAsync(reserva.Id);
        tracked!.Status = EstadoReserva.Cancelled;
        await db.SaveChangesAsync();

        await ReservaMoneyPersister.PersistAsync(db, reserva.Id, CancellationToken.None);
        var cancelledSnapshot = (await db.Reservas.FindAsync(reserva.Id))!.DerivedCollectionStatus;

        Assert.Equal(pendingRefundSnapshot, cancelledSnapshot);
        Assert.Equal(ReservaCollectionStatus.WithDebt, cancelledSnapshot);
    }
}
