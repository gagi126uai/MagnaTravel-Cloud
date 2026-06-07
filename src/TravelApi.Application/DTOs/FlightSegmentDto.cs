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
    public DateTime ArrivalTime { get; set; }
    // ADR-018 Ronda 7 (2026-06-06): la cabina es opcional. Null = "Sin especificar" (el front
    // muestra esa opcion en el desplegable; ya no se rellena con "Economy" del lado del server).
    public string? CabinClass { get; set; }
    public string Status { get; set; } = "HK";
    // PNR / localizador para gestionar la reserva en la aerolinea.
    public string? PNR { get; set; }
    // Numero de confirmacion que entrega el proveedor (distinto del PNR).
    public string? ConfirmationNumber { get; set; }
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
}
