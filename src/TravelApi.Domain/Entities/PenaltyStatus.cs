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

    /// <summary>
    /// 2026-06-28 (Fase A — cierre sin multa): el operador NO cobro ninguna multa (devuelve todo). Es una
    /// DECISION DE NEGOCIO explicita, distinta de "multa = 0 por error", por eso es un estado propio y NO se
    /// reusa <see cref="Confirmed"/> con monto 0.
    ///
    /// <para><b>Por que un valor propio y no Confirmed+0</b>: la bandeja de "NC sin su ND"
    /// (<c>GetCancellationsWithMissingDebitNoteAsync</c>) lista los BC con <c>PenaltyStatus == Confirmed</c> y sin
    /// ND vinculada para pedir que se emita la ND. Un cierre sin multa NUNCA debe emitir ND, asi que marcarlo como
    /// Confirmed lo meteria por error en esa bandeja. Con un estado terminal propio, esas consultas (que keyean por
    /// <c>== Confirmed</c>) lo excluyen sin tocarlas.</para>
    ///
    /// <para>Es TERMINAL como Confirmed: una vez resuelta la pata del operador (con o sin multa) no se vuelve atras.
    /// NO dispara ND y NO emite ningun comprobante fiscal. La NC total al cliente ya se emitio al anular.</para>
    /// </summary>
    Waived = 2,
}
