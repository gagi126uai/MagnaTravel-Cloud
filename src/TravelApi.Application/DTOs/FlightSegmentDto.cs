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
    public string CabinClass { get; set; } = "Economy";
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
    /// ADR-017 F1.4 (§2.5): fecha limite de EMISION del ticket (date-only, null = sin fecha). Se expone para
    /// que la fila/ficha pinte la etiqueta de vencimiento en F2. NO es un dato de costo.
    /// </summary>
    public DateTime? TicketingDeadline { get; set; }
    public bool IsPriceSynced { get; set; } = true;
    public string SourceKind { get; set; } = "Flight";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
