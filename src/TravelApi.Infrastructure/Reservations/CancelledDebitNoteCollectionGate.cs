using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Exceptions;
using TravelApi.Domain.Helpers;
using TravelApi.Domain.Reservations;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// Tanda D1 (2026-07-16): gate UNICO de "se puede cobrar (o aplicar saldo a favor) contra esta Nota de Debito
/// de multa": exige que la ND este APROBADA (<c>Resultado == "A"</c>) y sea realmente una ND, que la moneda del
/// cobro/aplicacion coincida con la de la ND, y que el monto no supere el saldo pendiente (calculado con la
/// regla PURA compartida <see cref="TravelApi.Domain.Reservations.DebitNoteOutstandingRules.ComputeOutstanding"/>).
///
/// <para><b>Por que se extrajo aca</b>: antes esta validacion vivia SOLO dentro de
/// <c>PaymentService.EnsureCancelledDebitNoteCollectableAsync</c>, para el cobro REAL en efectivo/transferencia
/// de una multa de una reserva anulada. El saldo a favor del cliente TAMBIEN puede pagar una multa (un Payment
/// puente que no mueve caja) y necesita EXACTAMENTE la misma regla — mismo tope, mismo gate de comprobante
/// aprobado, misma coherencia de moneda. Centralizar el gate aca evita que las DOS puertas de entrada (cobro
/// real via <c>PaymentService</c>, aplicacion de saldo via <c>ClientCreditService</c>) terminen divergiendo.
/// <c>PaymentService.EnsureCancelledDebitNoteCollectableAsync</c> ahora delega en este helper.</para>
/// </summary>
public static class CancelledDebitNoteCollectionGate
{
    /// <summary>Datos de la ND ya validados, para que el caller no tenga que re-consultarlos.</summary>
    public sealed record Result(int DebitNoteId, string CurrencyIso, decimal Outstanding);

    /// <summary>
    /// Valida el gate completo y devuelve el <see cref="Result"/> con la moneda y el saldo pendiente de la ND.
    /// Tira <see cref="PaymentValidationException"/> (mensaje de negocio, pensado para el usuario) si algo no
    /// corresponde: sin ND vinculada, ND no aprobada, moneda distinta, sin saldo pendiente, o monto que supera
    /// lo pendiente.
    ///
    /// <para>Tanda de saneo (2026-07-22): antes tiraba <see cref="InvalidOperationException"/> "a secas". Como
    /// este gate lo llaman DOS callers (<c>PaymentService</c> para el cobro real de una multa, y
    /// <c>ClientCreditService</c> cuando el saldo a favor del cliente paga la multa), se eligio
    /// <c>PaymentValidationException</c> — sigue heredando de <see cref="InvalidOperationException"/>, asi que
    /// el catch ancho que todavia tiene <c>CustomersController</c> para el segundo caso lo sigue atrapando
    /// igual. El primer caso (<c>PaymentsController</c>) ya tiene el catch ESPECIFICO de esta tanda, que solo
    /// funciona si el gate tira este tipo (si siguiera tirando InvalidOperationException "a secas", el mensaje
    /// de negocio se perdia y el usuario veia el 500 generico).</para>
    /// </summary>
    public static async Task<Result> EnsureCollectableAsync(
        AppDbContext db,
        int reservaId,
        int? linkedInvoiceId,
        string rawImputedCurrency,
        decimal imputedAmount,
        CancellationToken cancellationToken)
    {
        if (!linkedInvoiceId.HasValue)
        {
            throw new PaymentValidationException(
                "Para cobrar una multa de una reserva anulada debe seleccionar su Nota de Debito aprobada.");
        }

        var debitNote = await db.Invoices
            .AsNoTracking()
            .Where(invoice => invoice.Id == linkedInvoiceId.Value && invoice.ReservaId == reservaId)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.TipoComprobante,
                invoice.ImporteTotal,
                invoice.MonId,
                invoice.Resultado
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (debitNote is null
            || debitNote.Resultado != "A"
            || !InvoiceComprobanteHelpers.IsDebitNote(debitNote.TipoComprobante))
        {
            throw new PaymentValidationException(
                "La multa debe estar documentada por una Nota de Debito aprobada de esta reserva.");
        }

        var debitNoteCurrency = Monedas.Normalizar(ArcaCurrencyMapper.ToIso(debitNote.MonId));
        var imputedCurrency = Monedas.Normalizar(rawImputedCurrency);
        if (!string.Equals(debitNoteCurrency, imputedCurrency, StringComparison.Ordinal))
        {
            throw new PaymentValidationException(
                $"La Nota de Debito esta expresada en {debitNoteCurrency}; el cobro debe imputarse a esa moneda.");
        }

        // TANDA C "la multa cobrada se ve cerrada" (2026-07-16): la cuenta de "cuanto le queda pendiente a la
        // ND" vive UNICA en DebitNoteOutstandingLookup + DebitNoteOutstandingRules (Dominio).
        var debitNoteIdAsList = new List<int> { debitNote.Id };
        var creditedByDebitNote = await DebitNoteOutstandingLookup.LoadCreditedAmountsAsync(
            db, debitNoteIdAsList, cancellationToken);
        var collectedByDebitNote = await DebitNoteOutstandingLookup.LoadCollectedAmountsAsync(
            db, debitNoteIdAsList, cancellationToken);

        var outstanding = DebitNoteOutstandingRules.ComputeOutstanding(
            debitNote.ImporteTotal,
            creditedByDebitNote.GetValueOrDefault(debitNote.Id),
            collectedByDebitNote.GetValueOrDefault(debitNote.Id));
        if (outstanding <= 0m)
        {
            throw new PaymentValidationException("La Nota de Debito seleccionada ya no tiene saldo pendiente.");
        }

        if (ReservationEconomicPolicy.RoundCurrency(imputedAmount) > outstanding)
        {
            throw new PaymentValidationException(
                $"El monto imputado supera el saldo pendiente de la Nota de Debito ({CurrencyDisplayFormat.Amount(outstanding)} {debitNoteCurrency}).");
        }

        return new Result(debitNote.Id, debitNoteCurrency, outstanding);
    }
}
