namespace TravelApi.Application.DTOs;

public class FlightSegmentDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    /// <summary>
    /// ADR-018: identidad visible del vuelo cargado por la ficha "producto-primero" (el texto que vio
    /// el vendedor). Null en filas viejas/modal viejo: ahi la fila se muestra con AirlineCode/FlightNumber.
    /// </summary>
    public string? ProductName { get; set; }
    // ADR-018: campos estructurados pasan a nullable (la ficha puede omitirlos). Null = no informado.
    public string? AirlineCode { get; set; }
    public string? AirlineName { get; set; }
    public string? FlightNumber { get; set; }
    public string? Origin { get; set; }
    // Ciudad legible del origen/destino (ej. "Miami") ademas del codigo IATA (ej. "MIA").
    public string? OriginCity { get; set; }
    public string? Destination { get; set; }
    public string? DestinationCity { get; set; }
    public DateTime DepartureTime { get; set; }
    // BUG 2 (2026-06-08): ArrivalTime nullable — vuelos solo de ida (segmento sin hora de llegada).
    // Null = no informado; el front muestra el segmento sin hora de llegada (no como 01/01/0001).
    public DateTime? ArrivalTime { get; set; }
    // ADR-018 Ronda 7 (2026-06-06): la cabina es opcional. Null = "Sin especificar" (el front
    // muestra esa opcion en el desplegable; ya no se rellena con "Economy" del lado del server).
    public string? CabinClass { get; set; }
    public string Status { get; set; } = "HK";
    // PNR / localizador para gestionar la reserva en la aerolinea.
    public string? PNR { get; set; }
    // Numero de confirmacion que entrega el proveedor (distinto del PNR).
    public string? ConfirmationNumber { get; set; }
    // Auditoria ERP 2026-06-12 (item 5): time-limit de emision del aereo. Se expone para la ficha
    // (round-trip) y la alarma de emision. Aditivo, null = no informada. Ver FlightSegment.TicketingDeadline.
    public DateTime? TicketingDeadline { get; set; }
    // Auditoria ERP 2026-06-12 (item 5): fecha limite de pago al operador del segmento (distinta del
    // time-limit). Aditivo, null = no informada. Ver FlightSegment.OperatorPaymentDeadline.
    public DateTime? OperatorPaymentDeadline { get; set; }
    // Numero de ticket emitido (si ya se emitio el billete).
    public string? TicketNumber { get; set; }
    // Equipaje incluido (ej. "23kg" o "2PC").
    public string? Baggage { get; set; }
    // Base tarifaria del segmento (ej. clase de reserva/fare basis).
    public string? FareBase { get; set; }
    // Cantidad de pasajeros de este segmento. Null = no informado (segmentos legacy).
    public int? PassengerCount { get; set; }
    // Impuestos del segmento (ya existian en la entidad; los exponemos para el detalle).
    public decimal Tax { get; set; }
    // Observaciones libres del segmento.
    public string? Notes { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    /// <summary>
    /// Moneda en que se cotizo el servicio (trazabilidad, copiada del tarifario).
    /// Null = legacy / no informado. NO se usa todavia en calculos de saldo.
    /// </summary>
    public string? Currency { get; set; }
    /// <summary>
    /// ADR-017 (pill ambar "costo a confirmar", D7). Marca de costo: se enmascara a false para callers
    /// sin cobranzas.see_cost (ver comentario completo en <see cref="HotelBookingDto.CostToConfirm"/>).
    /// </summary>
    public bool CostToConfirm { get; set; }
    /// <summary>
    /// ADR-017 (pill violeta "creado en esta venta"): Rate.CreatedInSale del producto vinculado.
    /// NO es dato de costo, lo ven todos (ver <see cref="HotelBookingDto.ProductCreatedInSale"/>).
    /// </summary>
    public bool ProductCreatedInSale { get; set; }
    public bool IsPriceSynced { get; set; } = true;
    public string SourceKind { get; set; } = "Flight";
    public string WorkflowStatus { get; set; } = "Solicitado";
    // Auditoria de cancelacion (ADR-020): cuando se cancelo el segmento (Status -> HX) y quien lo cancelo.
    // El front los muestra como "Cancelado por X el DD/MM/YYYY". Null = no cancelado. NO son datos de costo
    // ni fiscales: no se enmascaran. Mapean por convencion (mismo nombre que la entidad FlightSegment).
    public DateTime? CancelledAt { get; set; }
    public string? CancelledByUserName { get; set; }
    // ADR-048 T4 (2026-07-17): etiqueta "Con multa"/"Multa cobrada" por servicio. Ver el XML-doc completo
    // en HotelBookingDto.CancellationPenaltyState (mismo campo, mismo criterio, replicado en los 6 DTOs
    // de servicio).
    public string? CancellationPenaltyState { get; set; }
    /// <summary>
    /// Tanda 7 (2026-07-20): si ESTE servicio admite "Anular", y el motivo cuando no. Ver el XML-doc
    /// completo en <see cref="HotelBookingDto.CanCancel"/> (mismo campo, mismo criterio, replicado en los
    /// 6 DTOs de servicio).
    /// </summary>
    public CapabilityDto? CanCancel { get; set; }
}
