namespace TravelApi.Application.DTOs;

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

    public string? CustomerName { get; set; } // Flattened
    public CustomerDto? Payer { get; set; } // Nested for frontend convenience
    public decimal TotalCost { get; set; }
    public decimal TotalSale { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Balance { get; set; }

    /// <summary>
    /// ADR-020 (decision #6): venta CONFIRMADA (solo servicios resueltos). Es la base del saldo
    /// (Balance = ConfirmedSale - TotalPaid). Se diferencia de TotalSale (valor comercial cotizado).
    /// </summary>
    public decimal ConfirmedSale { get; set; }

    /// <summary>
    /// ADR-020 (decision #6): si la reserva volvio SOLA de Confirmada a En gestion, este es el motivo
    /// (null si nunca regreso o si ya se re-confirmo). El frontend muestra una franja naranja con este
    /// texto. Se limpia automaticamente cuando la reserva se vuelve a confirmar.
    /// </summary>
    public string? LastRegressionReason { get; set; }

    /// <summary>Cuando ocurrio la ultima regresion automatica (par de <see cref="LastRegressionReason"/>).</summary>
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
    /// moneda &lt; 0), "Saldado" (todas en 0). "ConDeuda" gana sobre "SaldoAFavor" cuando hay ambas en
    /// monedas distintas (una reserva que debe USD y tiene saldo a favor ARS esta, antes que nada, con deuda).
    /// Se calcula desde <see cref="PorMoneda"/>. Eje independiente del estado operativo y de la facturacion.
    /// </summary>
    public string CollectionStatus { get; set; } = ReservaCollectionStatus.Settled;
}
