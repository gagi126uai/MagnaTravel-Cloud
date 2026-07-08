namespace TravelApi.Application.DTOs;

/// <summary>
/// Spec "el paso de multa vive en la ficha" (2026-07-08): read-model que le dice a la ficha (ReservaDetailPage) EN
/// QUE PASO esta la multa del operador de la cancelacion vigente y QUE puede hacer el usuario, sin tener que pedir
/// aparte el detalle de la cancelacion.
///
/// <para>Todos los datos ya vienen "listos para mostrar": la moneda en ISO ("ARS"/"USD", nunca el codigo ARCA
/// interno), el estado como token del contrato (el front lo mapea a su cartel; el usuario final NUNCA ve el token),
/// y los booleanos de accion ya combinan estado + permiso del usuario.</para>
/// </summary>
public class OperatorPenaltySituationDto
{
    /// <summary>
    /// Token del paso (contrato con el front, NO texto para el usuario). Valores:
    /// "None" | "PendingDecision" | "DebitNoteQueued" | "DebitNoteFailed" | "DebitNoteNeedsAmountCurrency" |
    /// "ConfirmedNoDebitNote" | "Waived" | "Done". El front lo traduce a su cartel/boton en castellano.
    /// </summary>
    public string State { get; set; } = "None";

    /// <summary>Monto de la multa (congelado al confirmar). Null cuando no aplica (None / pendiente sin monto / Waived).</summary>
    public decimal? Amount { get; set; }

    /// <summary>Moneda ISO 4217 ("ARS"/"USD") del <see cref="Amount"/>, ya normalizada para mostrar. Null si no aplica.</summary>
    public string? Currency { get; set; }

    /// <summary>
    /// Momento del ultimo cambio relevante del paso (para "hace X dias"). Se usa el timestamp mas razonable
    /// disponible: la confirmacion/cierre de la penalidad si ya se resolvio, o la fecha de la anulacion si esta
    /// pendiente. Ver el comentario del builder en el service.
    /// </summary>
    public DateTime? Since { get; set; }

    /// <summary>true si el usuario puede CONFIRMAR la multa ahora (estado PendingDecision + permiso). El endpoint revalida.</summary>
    public bool CanConfirm { get; set; }

    /// <summary>true si se puede REINTENTAR la emision de la Nota de Debito (fallida / confirmada-sin-encolar + permiso).</summary>
    public bool CanRetryDebitNote { get; set; }

    /// <summary>true si se puede CORREGIR monto + moneda de una multa con la ND trabada (revision manual / fallida + permiso).</summary>
    public bool CanCorrectAmountCurrency { get; set; }

    /// <summary>
    /// true si se puede CERRAR SIN MULTA una penalidad ya confirmada cuya Nota de Debito no llego a existir
    /// (fix "multa fantasma": estados DebitNoteFailed sin ND vinculada / DebitNoteNeedsAmountCurrency /
    /// ConfirmedNoDebitNote + permiso). Misma condicion que valida <c>WaiveOperatorPenaltyAsync</c> antes de
    /// aplicar el cierre; si esto da true, el endpoint de waive lo va a aceptar.
    /// </summary>
    public bool CanWaive { get; set; }

    /// <summary>Cuando se cerro sin multa (solo con valor en estado "Waived"). Persistido en el confirm del waive.</summary>
    public DateTime? WaivedAt { get; set; }

    /// <summary>Quien cerro sin multa (solo con valor en estado "Waived").</summary>
    public string? WaivedByName { get; set; }

    /// <summary>
    /// Cuando se reabrio un cierre sin multa. GAP CONOCIDO (2026-07-08): el revert-waive NO persiste este rastro en
    /// la entidad (solo queda en el audit log), asi que hoy SIEMPRE viaja null. Se expone el campo para no romper el
    /// contrato del front cuando en el futuro se agregue la columna; agregarla requiere migracion (fuera de alcance).
    /// </summary>
    public DateTime? RevertedAt { get; set; }

    /// <summary>Quien reabrio el cierre sin multa. Mismo GAP que <see cref="RevertedAt"/>: hoy siempre null.</summary>
    public string? RevertedByName { get; set; }
}
