using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// ADR-025 (DT.1.3 / M1, 2026-06-13): backfill IDEMPOTENTE de las lineas de cancelacion
/// (<see cref="BookingCancellationLine"/>) para los BC historicos. Espejo del patron de
/// <see cref="CashLedgerBackfillService"/> (ADR-022) / <see cref="MultiCurrencyBackfillService"/> (ADR-021).
///
/// <para><b>Por que existe</b>: la migracion Adr028_M1 crea la tabla VACIA. El modelo nuevo asume que TODO
/// BC tiene al menos una linea (el path mono-operador es "1 BC = 1 linea"). Sin backfill, los BC viejos
/// quedarian sin lineas y el camino de refund reformulado (INV-126 a nivel linea) no encontraria su linea.</para>
///
/// <para><b>Que crea</b>: UNA linea sintetica por cada BC historico que no tenga lineas, con:
/// <list type="bullet">
///   <item><c>SupplierId = bc.SupplierId</c> (el operador "principal" denormalizado del BC mono-operador).</item>
///   <item><c>Scope = Full</c>, <c>ServiceTable = Generic</c>, <c>ServiceId = 0</c> (centinela "linea de
///   backfill, sin servicio puntual": el BC viejo cancelaba la reserva entera, no apunta a un servicio).</item>
///   <item><c>LineSaleAmount = 0</c>: NO se reconstruye el monto (el efecto economico del evento historico
///   ya vive en el padre y ya se consumio cuando se cancelo en su dia). M1: una linea ServiceId=0 es
///   HISTORICA, no participa de recalculos de saldo/deuda futuros.</item>
///   <item>copia de <c>ConceptKind</c>, <c>PenaltyStatus</c>, <c>ReceivedRefundAmount</c>,
///   <c>DebitNoteInvoiceId</c>, <c>DebitNoteStatus</c> del padre (para que el caso mono-operador sea
///   byte-equivalente: la fuente de verdad por operador pasa a la linea sin perder lo que el padre sabia).</item>
/// </list></para>
///
/// <para><b>Idempotencia</b>: solo crea linea para los BC que NO tienen ninguna. Correrlo dos veces no
/// duplica. <see cref="NeedsBackfillAsync"/> es el chequeo barato para saltar todo cuando ya esta cubierto.</para>
/// </summary>
public sealed class BookingCancellationLineBackfillService
{
    private readonly AppDbContext _db;
    private readonly ILogger<BookingCancellationLineBackfillService>? _logger;

    public BookingCancellationLineBackfillService(
        AppDbContext db,
        ILogger<BookingCancellationLineBackfillService>? logger = null)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Hay backfill pendiente si existe algun BC sin ninguna linea hija.</summary>
    public async Task<bool> NeedsBackfillAsync(CancellationToken ct = default)
    {
        return await _db.BookingCancellations
            .Where(bc => !_db.BookingCancellationLines.Any(l => l.BookingCancellationId == bc.Id))
            .AnyAsync(ct);
    }

    /// <summary>
    /// Ejecuta el backfill. Devuelve cuantas lineas sinteticas se crearon. Seguro de re-llamar
    /// (solo toca los BC que no tienen lineas).
    /// </summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        // Solo los BC sin lineas. Se trae el padre completo para copiar su clasificacion fiscal a la linea.
        var bcsWithoutLines = await _db.BookingCancellations
            .Where(bc => !_db.BookingCancellationLines.Any(l => l.BookingCancellationId == bc.Id))
            .ToListAsync(ct);

        int created = 0;
        foreach (var bc in bcsWithoutLines)
        {
            // Moneda del evento desde el snapshot fiscal; ARS si el snapshot no la tiene (legacy).
            string currency = bc.FiscalSnapshot?.CurrencyAtEvent ?? Monedas.ARS;

            var line = new BookingCancellationLine
            {
                BookingCancellationId = bc.Id,
                SupplierId = bc.SupplierId,
                ServiceTable = CancellableServiceTable.Generic,
                ServiceId = 0,                       // centinela: linea de backfill, sin servicio puntual
                Scope = BookingCancellationLineScope.Full,
                Currency = currency,
                LineSaleAmount = 0m,                 // no se reconstruye; el monto del evento vive en el padre
                ConceptKind = bc.ConceptKind,
                PenaltyStatus = bc.PenaltyStatus,
                PenaltyAmount = bc.PenaltyAmountAtEvent,
                ReceivedRefundAmount = bc.ReceivedRefundAmount,
                RefundCap = bc.ReceivedRefundAmount, // approx historica: el cap no se reconstruye; igualar lo recibido evita un cap menor que lo ya imputado
                RefundStatus = bc.ReceivedRefundAmount > 0m
                    ? BookingCancellationLineRefundStatus.Settled
                    : BookingCancellationLineRefundStatus.None,
                DebitNoteInvoiceId = bc.DebitNoteInvoiceId,
                DebitNoteStatus = bc.DebitNoteStatus,
                CreatedAt = bc.DraftedAt,            // la linea historica "nacio" cuando nacio su BC
            };

            _db.BookingCancellationLines.Add(line);
            created++;
        }

        if (created > 0) await _db.SaveChangesAsync(ct);

        _logger?.LogInformation("ADR-025 booking cancellation line backfill done. Lineas creadas={Created}.", created);
        return created;
    }
}
