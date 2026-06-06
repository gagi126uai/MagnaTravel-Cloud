namespace TravelApi.Application.DTOs;

public class TransferBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    /// <summary>
    /// ADR-018: identidad visible del traslado cargado por la ficha "producto-primero". Null en filas
    /// viejas/modal viejo: ahi la fila se muestra con la ruta Pickup -> Dropoff o el tipo de vehiculo.
    /// </summary>
    public string? ProductName { get; set; }
    // ADR-018: estructurados pasan a nullable (la ficha puede omitirlos). Null = no informado.
    public string? PickupLocation { get; set; }
    public string? DropoffLocation { get; set; }
    public DateTime PickupDateTime { get; set; }
    public string VehicleType { get; set; } = "Private";
    // Ficha F2: sentido del traslado ("in" = llegada / "out" = salida). Se expone para que la ficha
    // de edicion lo recupere y lo reenvie sin pisarlo (round-trip). Null en filas legacy.
    public string? Direction { get; set; }
    // Ficha F2: modalidad ("private" = privado / "shared" = compartido). Round-trip igual que Direction.
    public string? ServiceMode { get; set; }
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
    // Impuestos incluidos en el costo (mismo criterio que FlightSegmentDto.Tax). No suma al precio
    // que paga el cliente; se expone para el detalle. Default 0 en filas legacy.
    public decimal Tax { get; set; }
    /// <summary>
    /// Moneda en que se cotizo el servicio (trazabilidad, copiada del tarifario).
    /// Null = legacy / no informado. NO se usa todavia en calculos de saldo.
    /// </summary>
    public string? Currency { get; set; }
    public bool IsPriceSynced { get; set; } = true;
    public string SourceKind { get; set; } = "Transfer";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
