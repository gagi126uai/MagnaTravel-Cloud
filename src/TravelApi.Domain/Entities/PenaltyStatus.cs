namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-013 (2026-06-01, R5 contador matriculado): estado de la penalidad de
/// cancelacion. Existe para impedir que se emita un comprobante fiscal (la ND)
/// sobre un monto que el operador todavia NO confirmo.
///
/// <para><b>Por que importa fiscalmente</b>: el contador matriculado exige NO
/// emitir comprobante sobre una penalidad estimada. Si el monto real difiere del
/// estimado, corresponde una ND/NC complementaria por la diferencia — pero eso es
/// fase posterior. En el MVP, solo <see cref="Confirmed"/> dispara la emision
/// automatica de la ND.</para>
///
/// <para>Default <see cref="Estimated"/> a proposito: es el valor conservador
/// (= NO emitir ND). Una cancelacion recien confirmada con el cliente arranca con
/// la penalidad estimada hasta que el operador la confirme formalmente.</para>
/// </summary>
public enum PenaltyStatus
{
    /// <summary>La penalidad es una estimacion (pre-acuerdo con el operador). NO dispara ND.</summary>
    Estimated = 0,

    /// <summary>El operador confirmo el monto de la penalidad. Habilita la emision de la ND (R5).</summary>
    Confirmed = 1,
}
