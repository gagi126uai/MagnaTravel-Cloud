using System;
using System.Linq.Expressions;
using TravelApi.Domain.Entities;

namespace TravelApi.Infrastructure.Reservations;

/// <summary>
/// Reglas ÚNICAS y compartidas para decidir, mirando una <see cref="BookingCancellation"/>, si la MULTA por
/// anulación es "por cobrar de verdad" o si quedó "en revisión" (fallida / derivada a administración).
///
/// <para><b>Por qué existe</b> (bug "multa fantasma", 2026-07-05): el cartel "Multa por anulación pendiente de
/// cobro" se pintaba con un predicado demasiado amplio que contaba como multa cobrable a estados donde NO existe
/// ningún comprobante fiscal válido (la Nota de Débito quedó <see cref="DebitNoteStatus.Failed"/> o
/// <see cref="DebitNoteStatus.ManualReview"/>). Resultado: en una reserva anulada el cartel quedaba pegado aunque
/// el usuario ya hubiera decidido no cobrar la multa. Acá separamos dos conceptos distintos:</para>
///
/// <list type="bullet">
///   <item><b>Multa VIVA</b> (<see cref="LiveDebitNotePredicate"/>): hay respaldo fiscal real (una ND emitida y no
///     anulada) O una multa confirmada con monto en la ventana de emisión diferida (ADR-014). El cartel "por cobrar"
///     corresponde.</item>
///   <item><b>Multa EN REVISIÓN</b> (<see cref="PenaltyUnderReviewPredicate"/>): la multa se confirmó pero su ND
///     falló o quedó para resolución manual. NO es una cuenta por cobrar firme; la vigila la bandeja de back-office
///     (GET /cancellations/debit-notes/pending), no el cartel de la ficha.</item>
/// </list>
///
/// <para><b>Por qué son <see cref="Expression{TDelegate}"/> y no funciones</b>: se reusan idénticos dentro de
/// consultas EF (<c>.Where(...)</c>) en el detalle de una reserva, en el listado batcheado y en el vigía de
/// coherencia (W5). Al ser una sola definición, los tres no pueden divergir. Traducen bien tanto en Postgres como en
/// el proveedor InMemory de los tests: la única navegación que usan (<c>bc.DebitNoteInvoice</c>) es opcional y se
/// accede con guarda de null explícita (equivale a un LEFT JOIN), patrón ya usado en el proyecto.</para>
/// </summary>
public static class CancellationPenaltyRules
{
    /// <summary>
    /// "Multa por cobrar de verdad": el saldo positivo de una reserva anulada está respaldado por una multa VIVA.
    /// Se cumple si al menos una de las dos ramas da true:
    ///
    /// <list type="number">
    ///   <item><b>ND emitida y no anulada</b>: la Nota de Débito ya salió (<see cref="DebitNoteStatus.Issued"/>),
    ///     tiene su factura vinculada (<c>DebitNoteInvoiceId != null</c>) y esa factura NO fue anulada después
    ///     (<c>AnnulmentStatus != Succeeded</c>). Es el respaldo fiscal firme.</item>
    ///   <item><b>Ventana de emisión diferida (ADR-014)</b>: la multa está confirmada
    ///     (<see cref="PenaltyStatus.Confirmed"/>) con un monto positivo y su ND todavía se está emitiendo. Los ÚNICOS
    ///     estados de ND que cuentan acá son <see cref="DebitNoteStatus.NotApplicable"/> (aún no se encoló) o
    ///     <see cref="DebitNoteStatus.Pending"/> (encolada, esperando CAE). Se listan EXPLÍCITAMENTE (whitelist) en vez
    ///     de "distinto de Failed/ManualReview": si dijéramos "distinto de X" esta rama también admitiría
    ///     <see cref="DebitNoteStatus.Issued"/>, y una ND Issued cuya factura fue ANULADA después
    ///     (<c>AnnulmentStatus == Succeeded</c>) — que la rama 1 excluye a propósito — se colaría por acá, socavando el
    ///     guard fiscal de la rama 1. Con la whitelist, Failed / ManualReview quedan afuera (son "en revisión", ver
    ///     <see cref="PenaltyUnderReviewPredicate"/>) e Issued queda gobernado SOLO por la rama 1. Así el cartel no
    ///     parpadea mientras la ND se emite, pero tampoco queda pegado cuando la emisión fracasó o su comprobante se
    ///     anuló.</item>
    /// </list>
    /// </summary>
    public static readonly Expression<Func<BookingCancellation, bool>> LiveDebitNotePredicate =
        bc =>
            (bc.DebitNoteStatus == DebitNoteStatus.Issued
                && bc.DebitNoteInvoiceId != null
                && bc.DebitNoteInvoice != null
                && bc.DebitNoteInvoice.AnnulmentStatus != AnnulmentStatus.Succeeded)
            || (bc.PenaltyStatus == PenaltyStatus.Confirmed
                && bc.PenaltyAmountAtEvent > 0m
                && (bc.DebitNoteStatus == DebitNoteStatus.NotApplicable
                    || bc.DebitNoteStatus == DebitNoteStatus.Pending));

    /// <summary>
    /// "Multa en revisión": la multa se confirmó pero su Nota de Débito falló su emisión
    /// (<see cref="DebitNoteStatus.Failed"/>) o quedó derivada a resolución manual
    /// (<see cref="DebitNoteStatus.ManualReview"/>). NO es una cuenta por cobrar firme (no hay comprobante fiscal
    /// válido), así que NO pinta el cartel "por cobrar" ni la reporta el vigía como dato roto: ya la vigila la
    /// bandeja de back-office de Notas de Débito pendientes, que es quien la puede destrabar.
    /// </summary>
    public static readonly Expression<Func<BookingCancellation, bool>> PenaltyUnderReviewPredicate =
        bc =>
            bc.PenaltyStatus == PenaltyStatus.Confirmed
            && (bc.DebitNoteStatus == DebitNoteStatus.Failed
                || bc.DebitNoteStatus == DebitNoteStatus.ManualReview);
}
