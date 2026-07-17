using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// Consultas BATCHEADAS y COMPARTIDAS para saber "cuanto le queda pendiente de cobro a una Nota de Debito (ND)
/// de multa". Trae, para un lote de NDs (por Id), su importe total + moneda, lo acreditado por Notas de Credito
/// asociadas y lo ya cobrado por pagos vivos vinculados. Estos tres datos, combinados con la regla PURA
/// <see cref="TravelApi.Domain.Reservations.DebitNoteOutstandingRules.ComputeOutstanding"/>, son la MISMA
/// formula que usa <c>PaymentService</c> (guard al registrar un cobro nuevo), <c>CustomerService</c> (bandeja
/// de multas del cliente) y <c>ReservaService</c> / <c>BookingCancellationService</c> (cartel de la ficha y del
/// listado). Centralizar las consultas aca evita que las cuatro lecturas repitan el mismo JOIN con una coma de
/// diferencia y terminen divergiendo (bug que motivo esta tanda, 2026-07-16: el cartel de la ficha usaba una
/// cuenta DISTINTA, basada en el saldo de la reserva, que dejo de ver los cobros reales de multa).
/// </summary>
internal static class DebitNoteOutstandingLookup
{
    /// <summary>Importe total + moneda (codigo ARCA crudo, ej. "PES"/"DOL") de cada ND pedida, por Id.</summary>
    public static async Task<Dictionary<int, (decimal ImporteTotal, string? MonId)>> LoadDebitNoteTotalsAsync(
        AppDbContext db, IReadOnlyList<int> debitNoteIds, CancellationToken cancellationToken)
    {
        if (debitNoteIds.Count == 0)
            return new Dictionary<int, (decimal, string?)>();

        var rows = await db.Invoices
            .AsNoTracking()
            .Where(invoice => debitNoteIds.Contains(invoice.Id))
            .Select(invoice => new { invoice.Id, invoice.ImporteTotal, invoice.MonId })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => (row.ImporteTotal, row.MonId));
    }

    /// <summary>
    /// Suma de Notas de Credito VIVAS (comprobante aprobado, no anulado despues) asociadas a cada ND, por Id de
    /// la ND. Mismo filtro de tipos de comprobante (3/8/13/53 = NC A/B/C/M) que usa el resto del modulo para
    /// distinguir una Nota de Credito de cualquier otro comprobante asociado.
    /// </summary>
    public static async Task<Dictionary<int, decimal>> LoadCreditedAmountsAsync(
        AppDbContext db, IReadOnlyList<int> debitNoteIds, CancellationToken cancellationToken)
    {
        if (debitNoteIds.Count == 0)
            return new Dictionary<int, decimal>();

        var rows = await db.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.OriginalInvoiceId.HasValue
                && debitNoteIds.Contains(invoice.OriginalInvoiceId.Value)
                && invoice.Resultado == "A"
                && invoice.AnnulmentStatus != AnnulmentStatus.Succeeded
                && (invoice.TipoComprobante == 3 || invoice.TipoComprobante == 8
                    || invoice.TipoComprobante == 13 || invoice.TipoComprobante == 53))
            .GroupBy(invoice => invoice.OriginalInvoiceId!.Value)
            .Select(group => new { DebitNoteId = group.Key, Amount = group.Sum(invoice => invoice.ImporteTotal) })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.DebitNoteId, row => row.Amount);
    }

    /// <summary>
    /// Suma de pagos VIVOS (no cancelados, no eliminados) imputados a cada ND, por Id de la ND. Usa el monto
    /// IMPUTADO cuando existe (cobro en moneda distinta de la de la ND, convertido) y si no el monto real del
    /// pago — mismo criterio que <c>PaymentService.EnsureCancelledDebitNoteCollectableAsync</c>.
    /// </summary>
    public static async Task<Dictionary<int, decimal>> LoadCollectedAmountsAsync(
        AppDbContext db, IReadOnlyList<int> debitNoteIds, CancellationToken cancellationToken)
    {
        if (debitNoteIds.Count == 0)
            return new Dictionary<int, decimal>();

        var rows = await db.Payments
            .AsNoTracking()
            .Where(payment => payment.LinkedInvoiceId.HasValue
                && debitNoteIds.Contains(payment.LinkedInvoiceId.Value)
                && payment.Status != "Cancelled"
                && !payment.IsDeleted)
            .GroupBy(payment => payment.LinkedInvoiceId!.Value)
            .Select(group => new
            {
                DebitNoteId = group.Key,
                Amount = group.Sum(payment => payment.ImputedAmount ?? payment.Amount)
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.DebitNoteId, row => row.Amount);
    }

    /// <summary>
    /// Fecha de alta (<c>CreatedAt</c>) del pago vivo MAS RECIENTE imputado a esta ND — "cuando quedo cerrada la
    /// multa" para el cartel. <c>null</c> si nunca se registro un cobro vivo contra ella.
    /// </summary>
    public static async Task<DateTime?> LoadLastCollectionDateAsync(
        AppDbContext db, int debitNoteId, CancellationToken cancellationToken)
    {
        return await db.Payments
            .AsNoTracking()
            .Where(payment => payment.LinkedInvoiceId == debitNoteId
                && payment.Status != "Cancelled"
                && !payment.IsDeleted)
            .OrderByDescending(payment => payment.CreatedAt)
            .Select(payment => (DateTime?)payment.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
