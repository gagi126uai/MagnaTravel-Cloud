namespace TravelApi.Application.DTOs;

/// <summary>
/// (2026-06-25) Valores del discriminador <see cref="ReservaDto.CancellationCase"/>: en cual de los cuatro
/// caminos de "Anular reserva" cae la reserva. El backend lo calcula; el front solo lo lee para mostrar el
/// cartel de confirmacion correcto. No son estados de la reserva (los estados viven en <c>EstadoReserva</c>).
/// </summary>
public static class ReservaCancellationCases
{
    /// <summary>Pre-venta (Cotizacion/Presupuesto): se descarta / marca Perdida. No hay plata que conservar.</summary>
    public const string PreSale = "PreSale";

    /// <summary>En firme, SIN factura y SIN cobros: baja directa a Cancelada.</summary>
    public const string DirectCancel = "DirectCancel";

    /// <summary>En firme, SIN factura pero CON cobros: Cancelada + la plata cobrada queda como saldo a favor.</summary>
    public const string PaymentsToCredit = "PaymentsToCredit";

    /// <summary>Con factura con CAE vivo: anulacion formal con Nota de Credito.</summary>
    public const string CreditNote = "CreditNote";

    /// <summary>La reserva no se puede anular en su estado actual (terminal o En viaje).</summary>
    public const string NotApplicable = "NotApplicable";
}

/// <summary>
/// (2026-06-25) Monto que quedaria como saldo a favor del cliente en UNA moneda si se anula una reserva del
/// caso <see cref="ReservaCancellationCases.PaymentsToCredit"/>. Es venta/cobro del cliente, no costo: no se
/// enmascara.
/// </summary>
public class ReservaCancellationCreditLineDto
{
    public string Currency { get; set; } = "ARS";
    public decimal Amount { get; set; }
}

/// <summary>
/// ADR-021 Capa 5 (multimoneda, 2026-06-10): una linea de plata de la reserva separada por moneda.
/// Espejo del value object de dominio <c>ReservaMoneyLine</c>. El front la usa para mostrar columnas
/// ARS/USD sin mezclar. <see cref="TotalCost"/> es COSTO/inversion -> se enmascara igual que el escalar
/// <c>TotalCost</c> para usuarios sin <c>cobranzas.see_cost</c> (ver ReservaService).
/// </summary>
public class ReservaMoneyLineDto
{
    public string Currency { get; set; } = "ARS";
    public decimal TotalSale { get; set; }
    public decimal ConfirmedSale { get; set; }
    /// <summary>Costo/inversion de esta moneda. Dato sensible: se enmascara a 0 sin ver-costos.</summary>
    public decimal TotalCost { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }

    /// <summary>
    /// ADR-037 / cuadre de facturacion POR MONEDA (2026-06-22): cuanto se le facturo NETO al cliente en
    /// ESTA moneda = facturas + notas de debito - notas de credito, solo comprobantes con CAE vivo
    /// (Resultado "A" y no anulados). El escalar <c>ReservaDto.FacturadoNeto</c> mezcla monedas en
    /// multimoneda; este es el numero correcto por moneda. NO es dato de costo (es venta/facturacion):
    /// no se enmascara por ver-costos. Las facturas se agrupan por su moneda ISO (Invoice.MonId via
    /// ArcaCurrencyMapper.ToIso; sin MonId -> ARS, regla legacy). Una moneda con venta y sin facturas
    /// queda en 0; una factura en una moneda sin venta vendida produce su propia linea (facturado &gt; 0,
    /// venta 0). Fuente unica: ReservaInvoicingCuadreCalculator.CalculatePerCurrency.
    /// </summary>
    public decimal FacturadoNeto { get; set; }

    /// <summary>
    /// ADR-037 / cuadre de facturacion POR MONEDA (2026-06-22): cuanto QUEDA por facturar en ESTA moneda
    /// respecto de lo vendido = <see cref="TotalSale"/> de esta moneda - <see cref="FacturadoNeto"/> de
    /// esta moneda. Mismo criterio que el escalar <c>ReservaDto.DisponibleParaFacturar</c> (usa TotalSale,
    /// no ConfirmedSale), para no divergir. Puede ser negativo si en esta moneda se facturo de mas.
    /// </summary>
    public decimal DisponibleParaFacturar { get; set; }

    /// <summary>
    /// Margen/ganancia de esta moneda = ConfirmedSale - TotalCost (venta confirmada menos costo).
    /// DATO SENSIBLE: contiene el costo por resta (costo = venta - margen). Se enmascara a 0 con el MISMO
    /// criterio y en el MISMO lugar que <see cref="TotalCost"/> (sin <c>cobranzas.see_cost</c> ni Admin).
    /// </summary>
    public decimal Margin { get; set; }
}

/// <summary>
/// ADR-027 (detalle "confirmada con cambios", 2026-06-13): UN cambio de precio/costo pendiente de revisar.
/// El front lo muestra en la franja "confirmada con cambios" (que servicio, que campo, de cuanto a cuanto).
/// Cuando <see cref="Field"/> es costo y el usuario no ve costos, <see cref="OldValue"/>/<see cref="NewValue"/>
/// llegan en 0 (enmascarado) y <see cref="ValuesMasked"/> en true para que el front muestre "—" en vez de "0".
/// </summary>
public class ReservaPendingChangeDto
{
    /// <summary>Tipo de servicio que cambio ("Hotel", "Aereo", etc.), para mostrar.</summary>
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>Nombre/descripcion del servicio que cambio.</summary>
    public string ServiceDescription { get; set; } = string.Empty;

    /// <summary>PublicId del servicio que cambio (para que el front lo linkee con su fila). Null si no se conocia.</summary>
    public Guid? ServicePublicId { get; set; }

    /// <summary>Campo que cambio: "SalePrice" (precio de venta) o "NetCost" (costo).</summary>
    public string Field { get; set; } = string.Empty;

    /// <summary>Valor anterior. 0 si es costo y el usuario no ve costos (ver <see cref="ValuesMasked"/>).</summary>
    public decimal OldValue { get; set; }

    /// <summary>Valor nuevo. 0 si es costo y el usuario no ve costos (ver <see cref="ValuesMasked"/>).</summary>
    public decimal NewValue { get; set; }

    /// <summary>Moneda del servicio ("ARS"/"USD").</summary>
    public string Currency { get; set; } = "ARS";

    /// <summary>Quien hizo el cambio (snapshot del nombre).</summary>
    public string? ChangedByUserName { get; set; }

    /// <summary>Cuando se hizo el cambio.</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>
    /// True si los montos vienen enmascarados (cambio de costo + usuario sin <c>cobranzas.see_cost</c>). El
    /// front debe mostrar "—" en vez de los ceros. El cambio de precio de venta NUNCA se enmascara.
    /// </summary>
    public bool ValuesMasked { get; set; }
}

/// <summary>
/// ADR-035 (2026-06-19): UNA capacidad expuesta al frontend. <see cref="Allowed"/> dice si la accion se
/// puede ahora; <see cref="Reason"/> es el texto legible (en español, sin montos ni costos) que el front
/// muestra como tooltip/cartel cuando el boton va apagado. El front NO vuelve a evaluar el estado por su
/// cuenta: lee esto. Espejo del <c>Cap</c> de dominio.
/// </summary>
public class CapabilityDto
{
    public bool Allowed { get; set; }
    public string? Reason { get; set; }
}

/// <summary>
/// ADR-035 (2026-06-19): bloque de capacidades de la reserva. El front renderiza cada boton de accion
/// SIEMPRE visible, deshabilitado cuando <c>Allowed=false</c>, con el motivo como tooltip. Es la fuente
/// unica de "que se puede hacer en este estado" (la calcula el backend con ReservaCapabilityPolicy). Las
/// listas de transiciones permiten al front armar el menu de cambio de estado sin replicar la matriz.
/// </summary>
public class ReservaCapabilitiesDto
{
    public CapabilityDto CanInvoiceSale { get; set; } = new();
    public CapabilityDto CanEmitCreditDebitNote { get; set; } = new();
    public CapabilityDto CanRegisterPayment { get; set; } = new();
    public CapabilityDto CanEditOrDeletePayment { get; set; } = new();
    public CapabilityDto CanEditServices { get; set; } = new();

    /// <summary>
    /// ADR-035 (2026-06-19): si se pueden tocar los PASAJEROS (agregar/completar/cambiar/borrar) en el estado
    /// actual. El front apaga los botones de pasajeros cuando es false. En estados terminales = false (solo
    /// lectura dura, ni completar datos). El candado de autorizacion en estados firmes se aplica aparte.
    /// </summary>
    public CapabilityDto CanEditPassengers { get; set; } = new();

    /// <summary>
    /// ADR-035 (2026-06-19): si se pueden editar las FECHAS / datos de cabecera de la reserva en el estado
    /// actual. El front apaga el boton "Editar fechas" cuando es false. En estados terminales = false.
    /// </summary>
    public CapabilityDto CanEditReservaData { get; set; } = new();

    public CapabilityDto CanCancel { get; set; } = new();

    /// <summary>
    /// (2026-06-24): si la reserva se puede ANULAR FORMALMENTE (deshacerla con plata viva emitiendo Nota de
    /// Crédito). Es el complemento de <see cref="CanCancel"/>: cuando hay factura/cobros, canCancel da false
    /// ("hay que anularla") y ESTA da true. El front muestra el botón "Anular reserva" si
    /// <c>CanCancel.Allowed || CanAnnul.Allowed</c>. El backend revalida la anulación real aparte.
    /// </summary>
    public CapabilityDto CanAnnul { get; set; } = new();

    /// <summary>
    /// (2026-06-26): si la reserva se puede ELIMINAR FISICAMENTE. <c>Allowed=true</c> solo en pre-venta
    /// (Cotización/Presupuesto) y SIN plata viva (sin cobros ni factura con CAE). En cualquier otro caso
    /// <c>Allowed=false</c> con el motivo. El front muestra "Eliminar" solo si <c>Allowed=true</c>; antes el
    /// backend NO mandaba esta capacidad y el front la asumía permitida por default (mostraba "Eliminar" en
    /// presupuestos con cobros). El borrado real revalida con DeleteGuards (incluye servicios confirmados por
    /// el operador, que esta capacidad no mira).
    /// </summary>
    public CapabilityDto CanDelete { get; set; } = new();

    /// <summary>
    /// G3 (2026-06-24): si se puede CANCELAR un servicio en el estado actual. true solo en {En gestión,
    /// Confirmada}. En pre-venta (Cotización/Presupuesto) un servicio se BORRA, no se cancela; el front usa
    /// esto para mostrar "Cancelar servicio" vs "Borrar servicio". En viaje/terminales = false.
    /// </summary>
    public CapabilityDto CanCancelServices { get; set; } = new();

    /// <summary>
    /// G5 (2026-06-24): si se puede REPROGRAMAR el viaje (mover la fecha de salida del itinerario) en el estado
    /// actual. true solo desde Confirmada en adelante {Confirmada, En viaje}. El front apaga el botón
    /// "Reprogramar viaje" cuando es false.
    /// </summary>
    public CapabilityDto CanReschedule { get; set; } = new();

    /// <summary>
    /// B3 (2026-06-24): si se pueden AGREGAR/MODIFICAR documentos adjuntos en el estado actual. false en
    /// estados terminales (Finalizada/Anulada/Perdida/Esperando reembolso): ahí los documentos son solo
    /// lectura. Ver/descargar lo ya cargado no depende de esta capacidad.
    /// </summary>
    public CapabilityDto CanUploadDocument { get; set; } = new();

    public CapabilityDto CanAdvance { get; set; } = new();
    public CapabilityDto CanEmitVoucher { get; set; } = new();

    /// <summary>
    /// ADR-036 (2026-06-22): si el ESTADO permite "Sacar de viaje" (corregir una entrada erronea a "En viaje").
    /// allowed solo cuando la reserva esta En viaje, sin factura con CAE vivo y sin voucher emitido vivo. OJO:
    /// esto NO incluye el permiso — el front ademas debe chequear que el usuario sea Admin
    /// (<c>reservas.correct_traveling</c>); el backend revalida ambas cosas (estado en la capacidad, permiso en
    /// el controller). Es la base para mostrar/apagar el boton "Sacar de viaje" con su motivo.
    /// </summary>
    public CapabilityDto CanCorrectTravelingEntry { get; set; } = new();

    /// <summary>
    /// H3 (2026-06-24): si se puede CONFIRMAR LA MULTA DEL OPERADOR (paso diferido que emite la Nota de Débito
    /// pass-through). allowed SOLO cuando la reserva tiene una multa del operador pendiente de confirmar — es la
    /// verdad del dato (existe una cancelación con multa diferida sin confirmar), NO el estado. El front muestra el
    /// botón "Confirmar multa del operador" únicamente si <c>allowed=true</c>; si es false, no lo ofrece (con el
    /// motivo como tooltip). Solo se calcula en el DETALLE de la reserva (en el listado va false, default). El
    /// endpoint confirm-penalty revalida permiso/4-ojos/idempotencia server-side.
    /// </summary>
    public CapabilityDto CanConfirmOperatorPenalty { get; set; } = new();

    /// <summary>
    /// Fase A (2026-06-28): estado de RESOLUCION de la "multa del operador" de la cancelación vigente.
    /// Valores: "None" | "Pending" | "Confirmed" | "Waived". El front lo lee al cargar la ficha para mostrar
    /// "Cerrada sin multa del operador" cuando es "Waived" (sin tener que pedir aparte el detalle de la
    /// cancelación). Es informativo: la ACCIÓN de confirmar/cerrar la gobierna <see cref="CanConfirmOperatorPenalty"/>.
    /// Default "None" cuando la reserva no tiene cancelación o su pata de operador no está en juego.
    /// </summary>
    public string OperatorPenaltyOutcome { get; set; } = "None";

    /// <summary>Estados a los que se puede avanzar manualmente (matriz forward del dominio).</summary>
    public List<string> AllowedForward { get; set; } = new();

    /// <summary>Estados a los que se puede revertir manualmente (matriz revert del dominio).</summary>
    public List<string> AllowedRevert { get; set; } = new();
}

public class ReservaDto
{
    public Guid PublicId { get; set; }
    public string NumeroReserva { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = "Budget";
    public Guid? CustomerPublicId { get; set; }
    public Guid? SourceLeadPublicId { get; set; }
    public Guid? SourceQuotePublicId { get; set; }
    public string? ResponsibleUserId { get; set; }
    public string? ResponsibleUserName { get; set; }
    public string? WhatsAppPhoneOverride { get; set; }
    public bool IsEconomicallySettled { get; set; }
    public bool CanMoveToOperativo { get; set; }
    public bool CanEmitVoucher { get; set; }
    public bool CanEmitAfipInvoice { get; set; }
    public string? EconomicBlockReason { get; set; }
    public bool IsInProgress { get; set; }
    /// <summary>True si el cliente no debe nada (Balance == 0). Chip verde "Pagada".</summary>
    public bool IsFullyPaid { get; set; }
    /// <summary>True si el viaje termino y todavia hay deuda (EndDate &lt; hoy AND Balance &gt; 0). Chip rojo "Vencida con deuda".</summary>
    public bool HasOverdueDebt { get; set; }

    /// <summary>
    /// Contexto de PLATA REAL en una reserva anulada. Null salvo en estados de cancelacion (Cancelled /
    /// PendingOperatorRefund). Una reserva anulada NO muestra "deuda" generica: muestra solo plata con
    /// contexto. Tokens (castellano, consistente con collectionStatus; el front los traduce a la etiqueta
    /// final): "SaldoAFavorPendiente" (quedo saldo a favor del cliente sin devolver), "MultaPorCobrar" (la
    /// deuda es la multa por anulacion, respaldada por una Nota de Debito viva), "Inconsistente" (saldo
    /// positivo sin comprobante que lo justifique = dato roto). null = sin plata pendiente. Ver
    /// <c>ReservationDebtRules</c>.
    /// </summary>
    public string? CancelledMoneyContext { get; set; }

    public string? CustomerName { get; set; } // Flattened
    public CustomerDto? Payer { get; set; } // Nested for frontend convenience
    public decimal TotalCost { get; set; }
    public decimal TotalSale { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }

    /// <summary>
    /// Margen/ganancia escalar de la reserva = ConfirmedSale - TotalCost (sobre lo confirmado, coherente con
    /// Balance). En multimoneda mezcla monedas (igual que los demas escalares); el margen real por moneda esta
    /// en <see cref="PorMoneda"/> (cada line.Margin). Solo en el DETALLE (no en el listado), decision del diseño.
    ///
    /// <para>DATO SENSIBLE: contiene el costo por resta (costo = venta - margen). Se enmascara a 0 en el MISMO
    /// <c>if (!seeCost)</c> que <see cref="TotalCost"/> — NUNCA se devuelve TotalCost==0 con Margin con valor.</para>
    /// </summary>
    public decimal TotalMargin { get; set; }

    /// <summary>
    /// ADR-020 (decision #6): venta CONFIRMADA (solo servicios resueltos). Es la base del saldo
    /// (Balance = ConfirmedSale - TotalPaid). Se diferencia de TotalSale (valor comercial cotizado).
    /// </summary>
    public decimal ConfirmedSale { get; set; }

    /// <summary>
    /// Motivo por el que la reserva quedo "confirmada con cambios / revisar" (null si no hay nada para revisar
    /// por servicios). Lo setea el motor cuando una reserva confirmada deja de tener todos sus servicios
    /// resueltos o se queda sin servicios: la reserva NO regresa de estado (la regresion automatica se elimino
    /// el 2026-06-24), queda confirmada pero marcada. El frontend muestra una franja informativa con este texto.
    /// Se limpia cuando una persona da el OK (acknowledge-changes), junto con <see cref="HasUnacknowledgedChanges"/>.
    /// El nombre es historico (antes era el motivo de la regresion automatica).
    /// </summary>
    public string? LastRegressionReason { get; set; }

    /// <summary>Cuando se marco el ultimo motivo de revision (par de <see cref="LastRegressionReason"/>).</summary>
    public DateTime? LastRegressionAt { get; set; }

    /// <summary>
    /// ADR-027 (hallazgo #10): true si se edito el precio/costo de un servicio estando la reserva en estado
    /// vivo y todavia nadie dio el OK. El frontend muestra la marca "confirmada con cambios" y un boton para
    /// dar el OK (POST /api/reservas/{id}/acknowledge-changes). Se limpia al acusar.
    /// </summary>
    public bool HasUnacknowledgedChanges { get; set; }

    /// <summary>ADR-027: desde cuando hay cambios sin revisar (par de <see cref="HasUnacknowledgedChanges"/>). Null si no hay nada pendiente.</summary>
    public DateTime? ChangesPendingSince { get; set; }

    /// <summary>
    /// ADR-027 (detalle, 2026-06-13): DETALLE de los cambios pendientes de revisar (que servicio, que campo,
    /// antes/despues, moneda, quien/cuando). El front lo muestra en la franja "confirmada con cambios". Vacio
    /// si no hay nada pendiente. Los montos de COSTO se enmascaran a quien no tiene <c>cobranzas.see_cost</c>.
    /// </summary>
    public List<ReservaPendingChangeDto> PendingChanges { get; set; } = new();

    /// <summary>
    /// ADR-020 F4 (candado): true si la reserva esta bajo candado y tiene una autorizacion de edicion
    /// VIVA (ExpiresAt &gt; ahora). El frontend muestra "destrabada por unos minutos" en vez de "pedi
    /// autorizacion". Calculado (no es columna): derivado de ReservaEditAuthorizations.
    /// </summary>
    public bool HasLiveEditAuthorization { get; set; }

    /// <summary>Cuando vence la autorizacion de edicion viva (null si no hay ninguna viva).</summary>
    public DateTime? EditAuthorizationExpiresAt { get; set; }

    /// <summary>
    /// ADR-025 (read-model cancelacion parcial, 2026-06-13): motivo por el que NINGUN servicio de la
    /// reserva se puede cancelar (candado fiscal: factura con CAE viva o voucher emitido), o <c>null</c>
    /// si se puede cancelar. El front lo usa para PRE-BLOQUEAR los casilleros de "cancelar varios
    /// servicios" antes de intentar (y evitar el 409). El bloqueo es a NIVEL RESERVA: si esta seteado,
    /// todos los servicios estan trabados. Calculado (no es columna): misma fuente de verdad que el
    /// guard que enforza la cancelacion (MutationGuards), asi no divergen UI y backend.
    /// Es info OPERATIVA (no costo): no se enmascara por ver-costos.
    /// </summary>
    public string? ServiceCancellationBlockReason { get; set; }

    // P3 (cuadre de facturacion): cuanto se le facturo NETO al cliente por esta reserva
    // (facturas + ND - NC, solo comprobantes con CAE vivo) y cuanto QUEDA por facturar
    // respecto de lo vendido (TotalSale). La UI los usa para avisar si se factura de mas.
    // Se calculan en el backend (fuente unica) para no duplicar la regla en el frontend.
    public decimal FacturadoNeto { get; set; }
    public decimal DisponibleParaFacturar { get; set; }

    public int AdultCount { get; set; }
    public int ChildCount { get; set; }
    public int InfantCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    /// <summary>
    /// Fecha de salida computada del primer servicio cargado (min de fechas).
    /// Se expone para que la UI pueda sugerir un valor cuando StartDate este vacio.
    /// </summary>
    public DateTime? SuggestedStartDate { get; set; }
    /// <summary>
    /// Fecha de regreso computada del ultimo servicio cargado (max de fechas).
    /// Se expone para que la UI pueda sugerir un valor cuando EndDate este vacio.
    /// </summary>
    public DateTime? SuggestedEndDate { get; set; }
    public DateTime? ClosedAt { get; set; }
    
    // Collections
    public List<PassengerDto> Passengers { get; set; } = new();
    public List<FlightSegmentDto> FlightSegments { get; set; } = new();
    public List<HotelBookingDto> HotelBookings { get; set; } = new();
    public List<TransferBookingDto> TransferBookings { get; set; } = new();
    public List<PackageBookingDto> PackageBookings { get; set; } = new();
    public List<AssistanceBookingDto> AssistanceBookings { get; set; } = new();
    public List<ServicioReservaDto> Servicios { get; set; } = new();
    public List<PaymentDto> Payments { get; set; } = new();
    public List<InvoiceDto> Invoices { get; set; } = new();

    /// <summary>
    /// ADR-021 Capa 5: detalle de plata SEPARADO por moneda (una linea por moneda presente). Los
    /// escalares de arriba (TotalSale/Balance/...) se conservan para compat; este es el detalle real
    /// que el front usa para mostrar columnas ARS/USD. Vacio/una sola linea = reserva mono-moneda.
    /// </summary>
    public List<ReservaMoneyLineDto> PorMoneda { get; set; } = new();

    /// <summary>ADR-021: true si la reserva mueve mas de una moneda.</summary>
    public bool EsMultimoneda { get; set; }

    /// <summary>
    /// ADR-033 (E7/A5, 2026-06-16): ESTADO DE COBRO derivado del saldo POR MONEDA (no persistido, no es
    /// columna). Valores: "ConDeuda" (alguna moneda con Balance &gt; 0), "SaldoAFavor" (sin deuda y alguna
    /// moneda &lt; 0), "Saldado" (todo en 0 PERO hubo cargos/cobros), "SinMovimientos" (todo en 0 y sin
    /// cargos ni cobros — reserva nueva, nada cobrado). "ConDeuda" gana sobre "SaldoAFavor" cuando hay ambas
    /// en monedas distintas (una reserva que debe USD y tiene saldo a favor ARS esta, antes que nada, con deuda).
    /// Se calcula desde <see cref="PorMoneda"/>. Eje independiente del estado operativo y de la facturacion.
    /// H1 (2026-06-24): el default es "SinMovimientos" (no "Saldado") para que una reserva sin datos de plata
    /// nunca se muestre como "pagada".
    /// </summary>
    public string CollectionStatus { get; set; } = ReservaCollectionStatus.NoCharges;

    /// <summary>
    /// ADR-037 (2026-06-21): ESTADO DE FACTURACION derivado del cuadre VENDIDO vs FACTURADO NETO (no
    /// persistido, no es columna). Carril SEPARADO de <see cref="CollectionStatus"/> (cobro) y del estado
    /// operativo. Valores: "NotInvoiced" (sin facturar), "PartiallyInvoiced" (facturada en parte),
    /// "FullyInvoiced" (facturada total o de mas). Por MONTO (decision H1): "total" = facturadoNeto &gt;= vendido.
    /// Escalar v1 (decision H4): deriva de <see cref="FacturadoNeto"/>/<see cref="TotalSale"/> escalares.
    /// Lo calcula <c>ReservaInvoicingStatus.Derive</c> en el backend (fuente unica).
    /// </summary>
    public string InvoicingStatus { get; set; } = ReservaInvoicingStatus.NotInvoiced;

    /// <summary>
    /// (2026-06-24): true si la reserva tiene una factura EN PROCESO — encolada en AFIP/ARCA esperando el CAE
    /// (<c>Resultado == "PENDING"</c> y no anulada). Mientras esto es true, el <see cref="InvoicingStatus"/>
    /// todavia NO la cuenta (el cuadre solo suma comprobantes con CAE aprobado), asi que la UI mostraria
    /// "Sin facturar" y ofreceria "Emitir factura" otra vez. Este flag existe para que el front avise "factura
    /// en proceso" y NO ofrezca re-emitir: emitir una segunda mientras hay una PENDING rebota con 409 en el
    /// backend (mismo criterio: <c>Resultado=="PENDING" &amp;&amp; AnnulmentStatus != Succeeded</c>). Es un
    /// espejo de lectura de ese guard, para feedback temprano.
    /// </summary>
    public bool HasInvoiceInProgress { get; set; }

    /// <summary>
    /// ADR-037 (2026-06-21): true si la reserva entra en el aviso "Debe — no viaja" (ADR-036): el cliente
    /// tiene deuda (saldo pendiente &gt; 0) Y la fecha de salida (<see cref="StartDate"/>) cae dentro de la
    /// ventana configurada (<c>UpcomingUnpaidReservationAlertDays</c>) Y las notificaciones de este tipo
    /// estan habilitadas (<c>EnableUpcomingUnpaidReservationNotifications</c>). El front lo usa para mostrar
    /// el aviso sin recalcular la ventana. Calculado server-side (fuente unica con el job nocturno).
    /// </summary>
    public bool IsWithinUnpaidAlertWindow { get; set; }

    /// <summary>
    /// ADR-035 (2026-06-19): que se puede hacer con esta reserva en su estado actual, y por que no cuando no
    /// se puede. El front lo usa para apagar botones con motivo (siempre visibles, deshabilitados). Es la
    /// fuente unica de capacidades (no se replica la regla de estado en el cliente). Calculado por la politica
    /// de dominio ReservaCapabilityPolicy; aditivo (no rompe consumidores actuales).
    /// </summary>
    public ReservaCapabilitiesDto Capabilities { get; set; } = new();

    /// <summary>
    /// ADR-035 (2026-06-19): true si la reserva tiene una factura AFIP con CAE vivo, por lo que NO se puede
    /// cancelar directamente: primero hay que anular la factura con una Nota de Credito. El front lo usa para
    /// explicar por que el flujo de cancelacion pide pasar por la NC. Derivado de "tiene CAE vivo".
    /// </summary>
    public bool RequiresInvoiceAnnulmentToCancel { get; set; }

    /// <summary>
    /// (2026-06-25) Flujo unificado de "Anular reserva": discriminador que dice EN QUE CASO de anulacion esta la
    /// reserva, para que el front muestre el cartel de confirmacion correcto SIN decidir la logica de plata por su
    /// cuenta (la decide el backend). Valores en <see cref="ReservaCancellationCases"/>:
    /// <list type="bullet">
    ///   <item><b>PreSale</b>: pre-venta (Cotizacion/Presupuesto) -> se descarta / marca Perdida.</item>
    ///   <item><b>DirectCancel</b>: en firme SIN factura y SIN cobros -> baja directa a Cancelada.</item>
    ///   <item><b>PaymentsToCredit</b>: en firme SIN factura pero CON cobros -> Cancelada + la plata cobrada queda
    ///         como SALDO A FAVOR del cliente (ver <see cref="CancellationCreditByCurrency"/>).</item>
    ///   <item><b>CreditNote</b>: con factura con CAE vivo -> anulacion formal con Nota de Credito.</item>
    ///   <item><b>NotApplicable</b>: la reserva no se puede anular en su estado actual (terminal o En viaje).</item>
    /// </list>
    /// Es DERIVADO del estado + plata viva (mismo criterio que las capacidades canCancel/canAnnul); no hay estado
    /// ni columna nueva. El backend es la unica fuente de este caso.
    /// </summary>
    public string CancellationCase { get; set; } = ReservaCancellationCases.NotApplicable;

    /// <summary>
    /// (2026-06-25) Solo para <see cref="CancellationCase"/> == <c>PaymentsToCredit</c>: el monto de cobros vivos
    /// SEPARADO POR MONEDA que quedaria como saldo a favor del cliente si se anula. El front lo muestra en el
    /// cartel de confirmacion ("Quedará a tu favor: ARS 100.000, USD 50"). Es VENTA/COBRO del cliente, NO costo:
    /// no se enmascara. Vacio en los demas casos. Fuente: TotalPaid por moneda del ReservaMoneyCalculator.
    /// </summary>
    public List<ReservaCancellationCreditLineDto> CancellationCreditByCurrency { get; set; } = new();

    /// <summary>
    /// ADR-036 (2026-06-22): true si la reserva quedo "En corrección" tras un "Sacar de viaje" — esto es,
    /// volvio a Confirmada PERO con la fecha de salida borrada (StartDate == null), senal de que entro a "En
    /// viaje" por error y falta recargar la fecha del servicio. El front lo usa para mostrar el cartel/chip
    /// "En corrección — pendiente revisar fechas". Es DERIVADO (Status == Confirmed && StartDate == null), no
    /// hay estado ni columna nueva. Cuando se corrige la fecha del servicio, StartDate se recomputa y el flag
    /// se apaga solo.
    /// </summary>
    public bool IsUnderCorrection { get; set; }

    /// <summary>
    /// ADR-035 Decision 2 / C5 (2026-06-19): moneda PRINCIPAL de la reserva, la que el cobro ofrece
    /// PRESELECCIONADA. La decide el backend (ReservaMoneyCalculator / armado del DTO), NUNCA el front:
    /// criterio = la moneda con MAYOR saldo pendiente; si empatan o hay una sola, esa (desempate por el orden
    /// del DTO, alfabetico estable). Vacio si la reserva no tiene plata cargada. El front la usa como default
    /// del formulario de cobro (con link "pagar en otra moneda" para el caso menos comun).
    /// </summary>
    public string? MonedaPrincipal { get; set; }
}
