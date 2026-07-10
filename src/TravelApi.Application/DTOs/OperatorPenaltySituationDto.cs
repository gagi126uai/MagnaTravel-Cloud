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

    /// <summary>
    /// ADR-044 T1 (2026-07-10): identificador PUBLICO (no el id interno de base) del operador al que corresponde
    /// esta situacion. Solo tiene valor cuando este DTO es un elemento de la lista
    /// <c>ReservaDto.OperatorPenaltySituations</c> (una cancelacion puede tener servicios de mas de un operador,
    /// ADR-025); el uso singular legacy (<c>ReservaDto.OperatorPenaltySituation</c>) tambien lo trae para que la
    /// ficha pueda titular el cartel, pero nada rompe si un consumidor viejo lo ignora.
    /// </summary>
    public Guid? SupplierPublicId { get; set; }

    /// <summary>Nombre del operador (para titular el cartel "Multa del operador X"). Ver <see cref="SupplierPublicId"/>.</summary>
    public string? SupplierName { get; set; }

    /// <summary>
    /// ADR-044 T2 Addendum (2026-07-10): desglose de los cargos tipificados de ESTE operador (el cargo
    /// administrativo automatico que crea el confirm, mas cualquier cargo secundario agregado despues —
    /// retencion fiscal, impuesto, facturado aparte). Lista vacia si el operador todavia no tiene ningun cargo
    /// registrado (multa pendiente de decidir, o cerrada sin multa). El front usa esto para el detalle "ver
    /// desglose de la multa", NO reemplaza <see cref="Amount"/>/<see cref="Currency"/> (que siguen siendo el
    /// total client-facing de la ND, sin cambios en esta tanda — T3 es quien conecta el desglose con la ND).
    /// </summary>
    public IReadOnlyList<OperatorChargeDto> Charges { get; set; } = Array.Empty<OperatorChargeDto>();
}

/// <summary>
/// ADR-044 T2 Addendum (2026-07-10): UN cargo tipificado del operador, listo para mostrar (moneda ya en ISO,
/// Kind/CollectionMode como token del contrato que el front traduce a texto en castellano).
/// </summary>
public class OperatorChargeDto
{
    /// <summary>Token: "AdministrativeFee" | "Tax" | "Withholding" | "Other". Ver <see cref="Domain.Entities.OperatorChargeKind"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Token: "Retenida" | "FacturadaAparte". Ver <see cref="Domain.Entities.PenaltyCollectionMode"/>.</summary>
    public string CollectionMode { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    /// <summary>Moneda ISO 4217 ("ARS"/"USD"), ya normalizada.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>Referencia al documento del proveedor. Solo tiene valor cuando <see cref="CollectionMode"/> = "FacturadaAparte".</summary>
    public string? DocumentRef { get; set; }

    public DateTime ConfirmedAt { get; set; }

    /// <summary>
    /// ADR-044 T3a (2026-07-10): Token "AsIs" | "WithManagementFee" | "Absorbed". Ver
    /// <see cref="Domain.Entities.ClientTransferMode"/>. Como se traslada ESTE cargo al cliente en la ND.
    /// </summary>
    public string ClientTransferMode { get; set; } = string.Empty;

    /// <summary>Monto del fee de gestión agregado, solo presente cuando <see cref="ClientTransferMode"/> = "WithManagementFee".</summary>
    public decimal? ManagementFeeAmount { get; set; }
}
