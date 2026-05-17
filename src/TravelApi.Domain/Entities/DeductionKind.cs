namespace TravelApi.Domain.Entities;

/// <summary>
/// FC1 (ADR-002 §2.3 / §2.9 / arca-tax round 2, 2026-05-13): tipificacion de
/// deducciones que el operador retiene al devolver fondos en un refund.
///
/// Numeracion por bloques (decision arca-tax) para permitir filtros simples
/// en reportes contables y en la matriz cruzada agencia/operador:
///   - 1..9   = costos operativos (NO impuestos).
///   - 10..29 = retenciones impositivas nacionales (IVA, Ganancias).
///   - 30..39 = retenciones impositivas provinciales (IIBB).
///   - 40..49 = impuestos extranjeros (sin credito fiscal AR).
///   - 99     = "Other" (comentario obligatorio + revision contable).
///
/// Reglas de validacion (INV-103..115) viven en
/// <c>BookingCancellationDeductionValidator</c> (FC1.2/FC1.3, fuera de scope FC1.1):
///   - Retenciones AR -> certificado obligatorio.
///   - IIBB           -> jurisdiccion obligatoria.
///   - Foreign        -> country code + descripcion.
///   - Costos op.     -> documento de soporte O justificacion + flag.
///   - Other          -> comentario + flag de revision.
///   - Si <c>Agency.TaxCondition = Monotributo</c> O <c>Operator.TaxCondition = Monotributo</c>,
///     bloquear los kinds 10..39 (no aplica regimen retenciones AR).
/// </summary>
public enum DeductionKind
{
    /// <summary>Cargo administrativo del operador (gestion, papeleo).</summary>
    AdministrativeFee = 1,

    /// <summary>Costos bancarios (comisiones, transferencias).</summary>
    BankingCost = 2,

    /// <summary>Penalidad contractual por cancelacion (cuotas no reembolsables).</summary>
    CancellationPenalty = 3,

    /// <summary>Retencion IVA (RG 2854). Requiere certificado.</summary>
    IvaWithholding = 10,

    /// <summary>Percepcion IVA. Requiere comprobante.</summary>
    IvaPerception = 11,

    /// <summary>Retencion Impuesto a las Ganancias (RG 830). Requiere certificado.</summary>
    IncomeTaxWithholding = 20,

    /// <summary>Retencion Ingresos Brutos. Requiere certificado + jurisdiccion.</summary>
    IIBBWithholding = 30,

    /// <summary>Percepcion Ingresos Brutos. Requiere comprobante + jurisdiccion.</summary>
    IIBBPerception = 31,

    /// <summary>Impuesto del pais destino del operador. NO genera credito fiscal AR. Requiere country code + descripcion.</summary>
    ForeignTax = 40,

    /// <summary>Cajon de sastre: requiere comentario y se marca para revision contable.</summary>
    Other = 99,
}
