namespace TravelApi.Application.DTOs;

public class PackageBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int Nights { get; set; }
    // Pasajeros del paquete, separados por categoria (editables).
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    // Que incluye el paquete: se exponen para que el detalle muestre los mismos
    // checkboxes que ya viajan en el request de alta/edicion.
    public bool IncludesHotel { get; set; } = true;
    public bool IncludesFlight { get; set; } = true;
    public bool IncludesTransfer { get; set; } = false;
    public bool IncludesExcursions { get; set; } = false;
    public bool IncludesMeals { get; set; } = false;
    // Descripcion / itinerario del paquete (texto libre largo).
    public string? Itinerary { get; set; }
    // Observaciones libres del paquete.
    public string? Notes { get; set; }
    public string Status { get; set; } = "Pendiente";
    public string? ConfirmationNumber { get; set; }
    public decimal SalePrice { get; set; }
    public decimal NetCost { get; set; }
    // Impuestos incluidos en el costo (mismo criterio que FlightSegmentDto.Tax). No suma al precio
    // que paga el cliente; se expone para el detalle. Default 0 en filas legacy.
    public decimal Tax { get; set; }
    /// <summary>
    /// Moneda en que se cotizo el servicio (trazabilidad, copiada del tarifario).
    /// Null = legacy / no informado. NO se usa todavia en calculos de saldo.
    /// </summary>
    public string? Currency { get; set; }
    public bool IsPriceSynced { get; set; } = true;
    public string SourceKind { get; set; } = "Package";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
