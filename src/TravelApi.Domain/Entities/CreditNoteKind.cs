namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 (ADR-009 §2.3.1, 2026-05-21): el calculator clasifica cada cancelacion en
/// uno de estos dos tipos de NC.
///
///  - <see cref="PartialOnOriginal"/>: una sola NC, vinculada a la factura original
///    via <c>Invoice.OriginalInvoiceId</c> + <c>&lt;CbtesAsoc&gt;</c>. La factura
///    sigue viva por el saldo. Casos 1, 2, 3, 5, 6, 8.
///  - <see cref="TotalPlusNewInvoice"/>: requiere NC por el total + factura nueva
///    por la parte que queda. Casos 4 y 7. <b>Fase 1 RECHAZA esto en Confirm
///    (GR-001)</b> porque el flujo no esta implementado todavia y avanzar dejaria
///    el sistema en estado inconsistente.
/// </summary>
public enum CreditNoteKind
{
    /// <summary>Default — no clasificado.</summary>
    Unset = 0,

    /// <summary>Una NC parcial vinculada a la factura original. Casos 1, 2, 3, 5, 6, 8.</summary>
    PartialOnOriginal = 1,

    /// <summary>NC total + factura nueva por el remanente. Casos 4 y 7. Fase 1 rechaza Confirm.</summary>
    TotalPlusNewInvoice = 2,
}
