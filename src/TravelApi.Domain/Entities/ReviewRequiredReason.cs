namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 (ADR-009 §2.3.1, 2026-05-21): bitflag con los motivos por los cuales una
/// cancelacion FC1.3 cae a revision manual. Es bitflag porque una sola cancelacion
/// puede disparar varios (ej. Factura A + items no reintegrables + monto alto).
///
/// La persistencia es un int. Se consulta con <c>flags.HasFlag(ReviewRequiredReason.X)</c>.
/// El default <see cref="None"/> (=0) significa que el clasificador NO encontro
/// motivos y la cancelacion puede auto-aprobarse (cuando el caso lo permite).
///
/// Se persiste para queries de auditoria/reporting: "cuantas cancelaciones del mes
/// dispararon manual review por items no reintegrables" se contesta con una sola
/// query sin tocar el JSON del approval.
/// </summary>
[Flags]
public enum ReviewRequiredReason
{
    /// <summary>Sin motivos — el calculator permite auto-emision.</summary>
    None = 0,

    /// <summary>Cliente RI con Factura A: el contador exige revision manual siempre (caso 8).</summary>
    CustomerIsRiOrFacturaA = 1 << 0,

    /// <summary>La factura tiene items con IsRefundable=false: el monto fiscal no coincide con lo cobrado.</summary>
    HasNonRefundableItems = 1 << 1,

    /// <summary>Monto fiscal &gt;= <c>PartialNcAdminReviewThreshold</c> (default 2M ARS).</summary>
    AmountAboveAdminThreshold = 1 << 2,

    /// <summary>Monto fiscal &gt;= <c>PartialNcAccountingReviewThreshold</c> (G5).</summary>
    AmountAboveAccountingThreshold = 1 << 3,

    /// <summary>Caso 7: la retencion cambia la naturaleza fiscal. Fase 1 rechaza Confirm (GR-001).</summary>
    RetentionChangesNature = 1 << 4,

    /// <summary>Caso 4: factura con descripcion ambigua. Fase 1 rechaza Confirm (GR-001).</summary>
    OriginalInvoiceUnclear = 1 << 5,

    /// <summary>Factura en moneda distinta de ARS — formula de prorrateo difiere (Fase 2 implementa).</summary>
    MultiCurrency = 1 << 6,

    /// <summary>Factura emitida antes de <c>Fc13DeployDate</c> y heuristica activada (OFF por default, RH-008).</summary>
    LegacyInvoice = 1 << 7,

    /// <summary>Operador en modo CommissionOnly: Fase 1 deriva a manual (GR-003).</summary>
    InvoicingModeCommissionOnly = 1 << 8,

    /// <summary>Caso 3 con penalidad en modo TotalToCustomer — contradiccion plan funcional, espera F4 (GR-006).</summary>
    PenaltyResetUncertainInResellerMode = 1 << 9,

    /// <summary>Catch-all para casos raros (NC en cadena, etc.).</summary>
    Other = 1 << 10,

    /// <summary>
    /// FC1.3 Fase 2 (G-F2-C, 2026-05-22): la factura origen tiene tributos
    /// provinciales (IIBB, percepciones de Capital, percepciones de provincia,
    /// otros). Detectado por <c>Invoice.Tributes.Any() == true</c>.
    ///
    /// <para>El sistema FRENA la cancelacion auto y dispara manual review
    /// obligatorio porque el prorrateo de tributos provinciales NO esta modelado
    /// en Fase 2 (ver plan tactico Fase 2 §T8 OQ-3). El admin debe revisar caso
    /// a caso y decidir si la NC parcial se emite manualmente con tributos
    /// prorrateados (fuera del sistema) o se reformula la operacion.</para>
    ///
    /// <para>Por que es aditivo y no rompe lo de Fase 1: las facturas Hotel B/C
    /// estandar que cubre FC1.3 Fase 1 no llevan tributos provinciales (los
    /// tributos aparecen tipicamente en Factura A o operaciones con retenciones).
    /// Asi que en el flujo feliz vigente, este flag no se activa nunca y todos
    /// los tests previos siguen pasando.</para>
    /// </summary>
    HasProvincialTributes = 1 << 11,
}
