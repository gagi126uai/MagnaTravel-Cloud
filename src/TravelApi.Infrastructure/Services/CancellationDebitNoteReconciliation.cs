using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Regla UNICA de reconciliacion entre la Nota de Debito (ND) de una anulacion y el estado de esa
/// anulacion (<see cref="BookingCancellation.DebitNoteStatus"/>).
///
/// <para>POR QUE EXISTE (bug 2026-07-13): la ND se emite de forma asincrona. El motor de la
/// anulacion crea la ND y deja <c>DebitNoteStatus = Pending</c>; el CAE lo consigue despues
/// <c>AfipService.ProcessInvoiceJob</c> (setea <c>Invoice.Resultado</c> "A"+CAE o "R"+Observaciones).
/// Antes NADIE transicionaba el <c>DebitNoteStatus</c> de Pending a Issued/Failed cuando llegaba el
/// CAE: el unico que reconciliaba era la LECTURA de la bandeja "Comprobantes por resolver". Si nadie
/// abria la bandeja, la ficha mostraba "se esta emitiendo la multa" indefinidamente aunque ARCA ya
/// hubiera dado el CAE.</para>
///
/// <para>Esta clase concentra esa regla para que ARCA (via el job) reconcilie ni bien resuelve la ND,
/// SIN duplicar la logica que ya vivia en la bandeja. La bandeja sigue siendo la red de seguridad.</para>
///
/// <para>ALCANCE ACOTADO A PROPOSITO: solo escala el <c>DebitNoteStatus</c> + el mensaje de error.
/// NO cierra la reserva ni dispara transiciones de negocio (decision 2026-07-03: el cierre lo hacen
/// las acciones explicitas de resolucion y el barrido nocturno, nunca como efecto lateral de que
/// llegue el CAE async). Tampoco toca el <c>DebitNoteStatus</c> POR LINEA
/// (<c>BookingCancellationLine.DebitNoteStatus</c>, marcador de T3a), que es otra cosa.</para>
/// </summary>
public static class CancellationDebitNoteReconciliation
{
    /// <summary>Mensaje cuando ARCA rechaza la ND sin devolver texto. Mismo literal historico de la bandeja.</summary>
    public const string ArcaRejectedWithoutMessage = "ARCA rechazo la ND sin mensaje.";

    /// <summary>La columna <c>DebitNoteArcaErrorMessage</c> tolera hasta 1000 chars; truncamos a esa cota.</summary>
    public const int ArcaErrorMaxLength = 1000;

    /// <summary>
    /// Aplica la regla de reconciliacion a UNA anulacion cuya ND acaba de resolverse en ARCA.
    /// Muta el <see cref="BookingCancellation"/> en memoria (NO persiste: el caller decide el SaveChanges).
    ///
    /// <para>Solo actua si la anulacion esta esperando el CAE (<c>DebitNoteStatus == Pending</c>). Si ya
    /// esta Issued/Failed/ManualReview/NotApplicable no se pisa: otra via ya la resolvio.</para>
    /// </summary>
    /// <returns><c>true</c> si transiciono a un estado terminal (Issued o Failed); <c>false</c> si no toco nada.</returns>
    public static bool TryApplyResolvedDebitNote(BookingCancellation cancellation, Invoice debitNote)
    {
        // Guard: solo reconciliamos anulaciones cuya ND quedo en vuelo esperando CAE.
        if (cancellation.DebitNoteStatus != DebitNoteStatus.Pending)
        {
            return false;
        }

        // ARCA aprobo la ND (Resultado "A") Y devolvio CAE -> la multa quedo fiscalmente emitida.
        bool arcaApproved =
            string.Equals(debitNote.Resultado, "A", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(debitNote.CAE);
        if (arcaApproved)
        {
            cancellation.DebitNoteStatus = DebitNoteStatus.Issued;
            cancellation.DebitNoteArcaErrorMessage = null;
            return true;
        }

        // ARCA rechazo la ND (Resultado "R") -> quedo fallida; preservamos el motivo (truncado).
        bool arcaRejected = string.Equals(debitNote.Resultado, "R", StringComparison.OrdinalIgnoreCase);
        if (arcaRejected)
        {
            cancellation.DebitNoteStatus = DebitNoteStatus.Failed;
            cancellation.DebitNoteArcaErrorMessage = TruncateArcaError(debitNote.Observaciones);
            return true;
        }

        // Resultado "PENDING" o null: la ND sigue en vuelo. No tocamos la anulacion.
        return false;
    }

    /// <summary>
    /// Busca la(s) anulacion(es) cuya ND es <paramref name="resolvedDebitNote"/> y siguen en Pending, y
    /// les aplica la reconciliacion. Persiste con su propio SaveChanges solo si algo cambio.
    ///
    /// <para>Pensado para llamarse desde <c>ProcessInvoiceJob</c> ni bien la Invoice queda resuelta. El job
    /// corre para TODAS las invoices (facturas de venta, NC, ND): el lookup por <c>DebitNoteInvoiceId</c>
    /// da 0 filas para todo lo que no sea una ND vinculada, y ese caso barato no toca nada.</para>
    /// </summary>
    /// <returns>Cantidad de anulaciones transicionadas.</returns>
    public static async Task<int> ReconcileLinkedCancellationFromDebitNoteAsync(
        AppDbContext db,
        Invoice resolvedDebitNote,
        ILogger logger,
        CancellationToken ct)
    {
        var linkedCancellations = await db.BookingCancellations
            .Where(b => b.DebitNoteInvoiceId == resolvedDebitNote.Id
                     && b.DebitNoteStatus == DebitNoteStatus.Pending)
            .ToListAsync(ct);

        if (linkedCancellations.Count == 0)
        {
            return 0;
        }

        int changedCount = 0;
        foreach (var cancellation in linkedCancellations)
        {
            if (TryApplyResolvedDebitNote(cancellation, resolvedDebitNote))
            {
                changedCount++;
            }
        }

        if (changedCount > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Reconciliacion ND->anulacion: {Count} anulacion(es) actualizada(s) al resolver la ND " +
                "(Invoice {InvoiceId}, Resultado {Resultado}).",
                changedCount, resolvedDebitNote.Id, resolvedDebitNote.Resultado);
        }

        return changedCount;
    }

    /// <summary>Recorta el mensaje de ARCA a <see cref="ArcaErrorMaxLength"/> y cae al literal por defecto si viene vacio.</summary>
    private static string TruncateArcaError(string? observaciones)
    {
        string text = string.IsNullOrWhiteSpace(observaciones) ? ArcaRejectedWithoutMessage : observaciones;
        return text.Length > ArcaErrorMaxLength ? text[..ArcaErrorMaxLength] : text;
    }
}
