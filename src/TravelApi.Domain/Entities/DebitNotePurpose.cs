namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-013 (2026-06-01, R3 contador matriculado): finalidad de la Nota de Debito.
/// Define A QUE comprobante se asocia la ND (RG 4540: comprobante asociado).
///
/// <para><b>Por que no es un string libre</b>: la finalidad cambia el tratamiento
/// fiscal (a que se asocia, si va a revision contable, si es automatizable). Un
/// string libre dejaria entrar combinaciones invalidas que ARCA rebotaria.</para>
///
/// <para>El MVP solo automatiza <see cref="PenaltyOrCancellationCharge"/> (la
/// penalidad/cargo propio de la agencia, asociado a la factura original). Los
/// demas valores existen para que el modelo sea completo, pero hoy ruteán a
/// revision manual.</para>
/// </summary>
public enum DebitNotePurpose
{
    /// <summary>
    /// Penalidad o cargo de cancelacion propio de la agencia. MVP: se asocia a la
    /// FACTURA ORIGINAL + leyenda mencionando la NC. Es el unico caso que el MVP
    /// emite automatico.
    /// </summary>
    PenaltyOrCancellationCharge = 0,

    /// <summary>
    /// Correccion de una Nota de Credito previa. Se asocia a la NC, NO a la factura.
    /// Requiere revision contable. FUTURO — el MVP lo rutea a revision manual.
    /// </summary>
    CorrectCreditNote = 1,

    /// <summary>
    /// Factura de Credito Electronica MiPyME. NUNCA automatico (validaciones ARCA
    /// estrictas). El MVP lo rutea a revision manual.
    /// </summary>
    FceMiPyme = 2,
}
