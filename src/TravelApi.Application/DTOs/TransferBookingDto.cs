namespace TravelApi.Application.DTOs;

public class TransferBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    public string PickupLocation { get; set; } = string.Empty;
    public string DropoffLocation { get; set; } = string.Empty;
    public DateTime PickupDateTime { get; set; }
    public string VehicleType { get; set; } = "Private";
    public int Passengers { get; set; } = 1;
    public bool IsRoundTrip { get; set; }
    public DateTime? ReturnDateTime { get; set; }
    // Vuelo que se recibe / asociado al traslado (ej. el avion del que se baja el pasajero).
    public string? FlightNumber { get; set; }
    // Info / notas operativas del traslado (texto libre).
    public string? Notes { get; set; }
    public string Status { get; set; } = "Pendiente";
    public string? ConfirmationNumber { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    /// <summary>
    /// Moneda en que se cotizo el servicio (trazabilidad, copiada del tarifario).
    /// Null = legacy / no informado. NO se usa todavia en calculos de saldo.
    /// </summary>
    public string? Currency { get; set; }
    public bool IsPriceSynced { get; set; } = true;
    public string SourceKind { get; set; } = "Transfer";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
