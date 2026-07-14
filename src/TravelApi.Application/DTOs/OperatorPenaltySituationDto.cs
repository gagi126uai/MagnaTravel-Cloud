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
    /// "ConfirmedNoDebitNote" | "Waived" | "Done" | "DebitNoteAnnulling" | "DebitNoteAnnulmentFailed". El front
    /// lo traduce a su cartel/boton en castellano. Los dos ultimos son ADR-044 "Deshacer una multa ya emitida"
    /// (2026-07-14): un front que no los conozca DEBE degradar a un estado informativo neutro (nunca romper).
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

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): true si se puede DESHACER la Nota de Debito ya
    /// emitida con CAE (estado "Done" + permiso). El endpoint <c>POST .../undo-debit-note</c> revalida todo
    /// server-side; este booleano solo decide si la ficha OFRECE el boton.
    /// </summary>
    public bool CanUndoDebitNote { get; set; }

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

    /// <summary>
    /// ADR-044 T3b/T4 (2026-07-10): mensaje LIMPIO y FIJO al usuario cuando la Nota de Debito quedo en revision
    /// manual por el caso DERIVABLE "falta elegir a qué factura corresponde el cargo" (cancelacion con 2+ facturas
    /// de venta activas + algun cargo trasladable sin factura destino resuelta). Null en cualquier OTRO motivo de
    /// revision manual: ahi el front muestra su propia copy fija ("falta confirmar el monto y la moneda").
    ///
    /// <para><b>SEGURIDAD (data-exposure, 2026-07-10)</b>: este campo NUNCA porta el <c>DebitNoteArcaErrorMessage</c>
    /// crudo del backend, que puede contener texto tecnico en español (ej. "OriginatingInvoice no cargada.",
    /// "...(M2)."). El service reconstruye la condicion "falta elegir factura" EN VIVO (2+ facturas activas + cargo
    /// con <see cref="OperatorChargeDto.TargetInvoicePublicId"/> null) y, solo si se cumple, expone un mensaje
    /// fijo controlado; para el resto viaja null. Asi el texto al usuario nunca depende de un string interno.</para>
    ///
    /// <para><b>NO se agrego un enum de estado nuevo para "falta elegir la factura" vs. "moneda no coincide"</b>
    /// (ambos comparten el token "DebitNoteNeedsAmountCurrency"): el FRONT los distingue por este campo (si viene
    /// no-null, el paso trabado es "falta elegir factura" y muestra el desplegable de la Pantalla 2 con
    /// <c>BookingCancellationDto.SaleInvoices</c> — <see cref="CancellationSaleInvoiceDto.PublicId"/>; si viene
    /// null, es "falta confirmar monto/moneda" y muestra el formulario de <c>correct-penalty</c>). GAP CONOCIDO:
    /// para operadores SECUNDARIOS este campo queda null (su desglose por operador es una tanda futura).</para>
    /// </summary>
    public string? ManualReviewReason { get; set; }

    /// <summary>
    /// ADR-044 Fix B (2026-07-13): moneda de la factura original a la que se emite la Nota de Débito, en ISO
    /// ("ARS"/"USD"). El modal de "corregir monto y moneda" la usa para saber CUÁNDO pedir el tipo de cambio: si
    /// el usuario elige una moneda distinta a ésta, hay que convertir (y aparece el campo de TC). Aditivo y
    /// nullable: un front viejo que lo ignore no se rompe. Null cuando no hay cancelación en juego.
    /// </summary>
    public string? InvoiceCurrency { get; set; }

    /// <summary>
    /// ADR-044 Fix B (2026-07-13): fecha SUGERIDA para el tipo de cambio (el día en que el operador confirmó/
    /// cobró la multa, <c>OperatorPenaltyConfirmedDate</c>). El modal la propone como default editable del campo
    /// de fecha; si viene null (BC legacy sin esa fecha), el campo arranca vacío y es obligatorio (el sistema
    /// nunca inventa una fecha). Aditivo y nullable.
    /// </summary>
    public DateTime? SuggestedExchangeRateDate { get; set; }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14): fecha en que ARCA aprobó el CAE de la Nota de
    /// Débito ya emitida (<c>Invoice.IssuedAt</c>). Solo tiene valor en "Done" (la ND vigente que se podría
    /// deshacer). El front la usa para el aviso informativo de RG 4540 (15 días corridos desde la emisión):
    /// AVISAR si pasó el plazo, NUNCA bloquear el botón de deshacer — el backend tampoco bloquea por esto.
    /// </summary>
    public DateTime? DebitNoteIssuedAt { get; set; }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14, M4): monto que le quedaría de saldo a favor al
    /// cliente si se deshiciera la multa AHORA — la porción EFECTIVAMENTE COBRADA de la multa, en la moneda de la
    /// Nota de Débito. El modal muestra la línea "le va a quedar $ X a favor" con este número. Es la MISMA fuente
    /// de verdad que acuña <c>ClientCreditService.CreateEntryFromDebitNoteUndoAsync</c> al consumarse el deshacer
    /// (<c>max(0, PenaltyAmountAtEvent − pendiente)</c>, neteado contra el saldo por moneda de la reserva).
    ///
    /// <para>Solo trae valor cuando <see cref="CanUndoDebitNote"/> es true (Done o deshacer-fallido). <c>0</c> si
    /// la multa está impaga (no se acuñaría nada); <c>null</c> si no se puede calcular (sin monto congelado /
    /// moneda no resoluble): el front OMITE la línea si viene null.</para>
    /// </summary>
    public decimal? CollectedPenaltyAmount { get; set; }

    /// <summary>
    /// ADR-044 "Deshacer una multa ya emitida" (2026-07-14, M4): rastro del ÚLTIMO deshacer CONSUMADO de este
    /// paso, para el cartel re-abierto ("Se deshizo el comprobante anterior el {fecha} — motivo: {motivo}").
    /// <c>null</c> si nunca se deshizo una Nota de Débito de esta cancelación. Sale de la tabla hija
    /// (<c>BookingCancellationDebitNoteAnnulment</c>), última fila en estado Succeeded.
    /// </summary>
    public LastDebitNoteUndoDto? LastDebitNoteUndo { get; set; }
}

/// <summary>
/// ADR-044 "Deshacer una multa ya emitida" (2026-07-14, M4): datos del último deshacer consumado, listos para
/// el cartel (sin IDs ni jerga interna). Ver <see cref="OperatorPenaltySituationDto.LastDebitNoteUndo"/>.
/// </summary>
public class LastDebitNoteUndoDto
{
    /// <summary>
    /// Cuándo se solicitó el deshacer (<c>RequestedAt</c> de la fila hija). Es la fecha del acto en el sistema;
    /// el CAE de la Nota de Crédito que lo consumó llega unos minutos después (no se persiste un timestamp
    /// aparte de esa aprobación, así que esta es la fecha razonable disponible para el cartel).
    /// </summary>
    public DateTime UndoneAt { get; set; }

    /// <summary>Nombre de quien deshizo (para el cartel). Puede venir null si la fila hija no tiene nombre legible.</summary>
    public string? UndoneByName { get; set; }

    /// <summary>Motivo con que se deshizo (texto libre que cargó el usuario, obligatorio al deshacer).</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// ADR-044 T2 Addendum (2026-07-10): UN cargo tipificado del operador, listo para mostrar (moneda ya en ISO,
/// Kind/CollectionMode como token del contrato que el front traduce a texto en castellano).
/// </summary>
public class OperatorChargeDto
{
    /// <summary>
    /// ADR-044 T4 (2026-07-10): identificador PUBLICO de este cargo. Lo necesita el front para pegarle al PATCH
    /// <c>/api/cancellations/{publicId}/operator-charges/{chargePublicId}/target-invoice</c> (elegir/corregir la
    /// factura destino de ESTE cargo puntual). Antes de este campo, la lista de cargos era solo informativa: no
    /// se podia direccionar un cargo individual desde el front.
    /// </summary>
    public Guid PublicId { get; set; }

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

    // ====================================================================================
    // ADR-044 T3b Decision 1 y 2 (2026-07-10): a que factura se traslada este cargo, y el TC
    // ESTIMADO usado para convertirlo cuando su moneda difiere de la de esa factura. Ver el
    // XML-doc de los campos espejo en BookingCancellationLineOperatorCharge (Domain).
    // ====================================================================================

    /// <summary>
    /// PublicId de la factura de venta a la que se traslada este cargo. Null cuando la reserva tiene 2+ facturas
    /// activas y todavia NADIE eligio (o la eleccion previa quedo invalida porque esa factura se anulo): el front
    /// usa este null, combinado con <see cref="OperatorPenaltySituationDto.State"/> == "DebitNoteNeedsAmountCurrency"
    /// (o el equivalente por operador), para saber que el desplegable de "elegir factura" es lo que hay que
    /// mostrar (en vez del formulario de "corregir monto y moneda"). Ver la nota de derivacion en
    /// <see cref="OperatorPenaltySituationDto"/>.
    /// </summary>
    public Guid? TargetInvoicePublicId { get; set; }

    /// <summary>TC ESTIMADO (preview, no fiscal) para convertir <see cref="Amount"/> a la moneda de la factura destino. Null si no hubo conversion (cargo en la misma moneda que su factura).</summary>
    public decimal? EstimatedExchangeRateToClientInvoiceCurrency { get; set; }

    /// <summary>Token: "Manual" | "Bcra" | "Blue" | etc. Ver <see cref="Domain.Entities.ExchangeRateSource"/>. Null si no hubo TC estimado.</summary>
    public string? EstimatedExchangeRateSource { get; set; }

    /// <summary>Fecha del TC estimado (el dia en que el operador cobro la multa). Null si no hubo TC estimado.</summary>
    public DateTime? EstimatedExchangeRateAt { get; set; }

    /// <summary>Justificacion del TC estimado, solo presente cuando su origen es "Manual".</summary>
    public string? EstimatedExchangeRateJustification { get; set; }
}
