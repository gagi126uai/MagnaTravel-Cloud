namespace TravelApi.Domain.Reservations;

/// <summary>
/// Spec "el paso de multa vive en la ficha" (2026-07-08): estado DETALLADO de la pata "multa del operador" de la
/// cancelacion vigente de una reserva, pensado para que la ficha (ReservaDetailPage) muestre EL PASO exacto en que
/// esta la multa y ofrezca la accion correcta, sin tener que pedir aparte el detalle de la cancelacion.
///
/// <para>Es una version mas fina de <see cref="OperatorPenaltyOutcome"/> (que solo distingue None/Pending/Confirmed/
/// Waived): desglosa el "Confirmed" segun donde quedo la Nota de Debito (encolada / fallida / trabada por moneda /
/// emitida), que es justo lo que decide que boton mostrar en la ficha.</para>
///
/// <para><b>Es un contrato para el front</b>: el token viaja como string en el DTO, pero el usuario final NUNCA ve
/// este nombre — el front lo mapea a su cartel/boton en castellano. Nada de exponer el enum crudo como texto.</para>
/// </summary>
public enum OperatorPenaltySituationState
{
    /// <summary>La reserva no tiene cancelacion vigente, o su pata de operador no esta en juego. La ficha no muestra el paso.</summary>
    None = 0,

    /// <summary>Anulada con la multa PENDIENTE de decidir (aun Estimated, esperando confirmar la multa o cerrar sin multa).</summary>
    PendingDecision = 1,

    /// <summary>Multa confirmada; su Nota de Debito quedo ENCOLADA (Pending, esperando el CAE de ARCA). No hay accion: esperar.</summary>
    DebitNoteQueued = 2,

    /// <summary>Multa confirmada; su Nota de Debito FALLO el CAE. Se puede reintentar la emision (ARCA pudo estar caido).</summary>
    DebitNoteFailed = 3,

    /// <summary>
    /// Multa confirmada; su Nota de Debito quedo TRABADA en revision manual — tipicamente porque la moneda declarada
    /// de la multa no coincide con la de la factura, o quedo sin moneda registrada (confirmaciones viejas). Se
    /// resuelve re-capturando monto + moneda (correct-penalty), no con un reintento a secas.
    /// </summary>
    DebitNoteNeedsAmountCurrency = 4,

    /// <summary>Multa confirmada pero su Nota de Debito nunca se llego a encolar (diferida / a medias). Se puede reintentar la emision.</summary>
    ConfirmedNoDebitNote = 5,

    /// <summary>Se cerro SIN multa (el operador no cobro penalidad; no hay Nota de Debito).</summary>
    Waived = 6,

    /// <summary>Multa confirmada y su Nota de Debito ya EMITIDA con CAE. La pata del operador quedo resuelta.</summary>
    Done = 7,

    /// <summary>
    /// ADR-044 T1 (2026-07-10): multa CONFIRMADA de un operador en una cancelacion con MAS DE UN operador
    /// confirmado a la vez. La Nota de Debito automatica queda BLOQUEADA a proposito (la ND por linea de
    /// operador recien se automatiza en una tanda futura — "T3" del rediseño): se resuelve manualmente por
    /// ahora, misma logica que ya usaba <c>TryEmitCancellationDebitNoteAsync</c> ("ARREGLO 2") para frenar la
    /// emision automatica cuando detecta multas confirmadas de mas de un operador.
    /// </summary>
    MultiOperatorNeedsManualReview = 8,

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): la multa esta <c>Done</c> (ND emitida con CAE) Y hay
    /// un "deshacer" en curso (la Nota de Credito que anula esa ND todavia espera su propio CAE). Familia
    /// PROCESANDO: el front hace polling, sin accion ofrecida (esperar).
    /// </summary>
    DebitNoteAnnulling = 9,

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): la multa sigue <c>Done</c> (la ND original SIGUE
    /// viva, Issued) porque el ultimo intento de deshacerla fallo (ARCA rechazo la Nota de Credito). Se ofrece
    /// reintentar el deshacer.
    /// </summary>
    DebitNoteAnnulmentFailed = 10,
}
