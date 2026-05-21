namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1.3 (ADR-009 §2.3.1, 2026-05-21): los 8 casos de la matriz fiscal del contador
/// para NC parcial Hotel. Cada caso resulta de la combinacion de
/// (tipo de factura A/B/C) x (modo TotalToCustomer/CommissionOnly) x
/// (cancelacion total/parcial) x (con/sin penalidad/items no reintegrables).
///
/// El <see cref="IFiscalLiquidationCalculator"/> clasifica cada cancelacion en uno
/// de estos casos y decide si emite NC parcial automatica, va a revision manual,
/// o se rechaza directo (Fase 1).
///
/// Casos 4 y 7 quedan <b>fuera de Fase 1</b> (GR-001): el Confirm los rechaza con
/// error explicito porque requieren NC total + factura nueva (flujo no implementado
/// en Fase 1).
///
/// Casos 5 y 6 quedan en revision manual obligatoria en Fase 1 (GR-003): el modo
/// CommissionOnly no tiene formula auto-confirmada hasta que el contador responda
/// la pregunta F2 round 3.
/// </summary>
public enum PartialCreditNoteCase
{
    /// <summary>Caso 0: clasificador no corrio o caso no aplicable (default).</summary>
    Unset = 0,

    /// <summary>Caso 1: cancelacion PARCIAL sin retencion (Factura B/C, TotalToCustomer).</summary>
    Case1_PartialCancellationNoRetention = 1,

    /// <summary>Caso 2: cancelacion TOTAL sin retencion (Factura B/C, TotalToCustomer). Trivial: NC por total.</summary>
    Case2_FullCancellationNoRetention = 2,

    /// <summary>Caso 3: cancelacion TOTAL con penalidad operador (Factura B/C, TotalToCustomer). Fase 1 deriva a manual (GR-006).</summary>
    Case3_FullCancellationWithPenalty = 3,

    /// <summary>Caso 4: factura confusa (descripcion generica unica). Fase 1 RECHAZA Confirm (GR-001).</summary>
    Case4_OriginalInvoiceUnclear = 4,

    /// <summary>Caso 5: cancelacion PARCIAL en modo CommissionOnly. Fase 1 manual review (GR-003).</summary>
    Case5_CommissionOnlyPartial = 5,

    /// <summary>Caso 6: cancelacion TOTAL en modo CommissionOnly. Fase 1 manual review (GR-003).</summary>
    Case6_CommissionOnlyFull = 6,

    /// <summary>Caso 7: retencion cambia naturaleza fiscal del item. Fase 1 RECHAZA Confirm (GR-001).</summary>
    Case7_RetentionChangesNature = 7,

    /// <summary>Caso 8: Factura A (cliente RI). Manual review obligatorio (criterio contador).</summary>
    Case8_FacturaA = 8,
}
