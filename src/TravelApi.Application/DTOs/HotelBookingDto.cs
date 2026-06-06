namespace TravelApi.Application.DTOs;

public class HotelBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    public string HotelName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public DateTime CheckIn { get; set; }
    public DateTime CheckOut { get; set; }
    public int Nights { get; set; }
    public int Rooms { get; set; } = 1;
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    public string RoomType { get; set; } = "Standard";
    public string? MealPlan { get; set; }
    public string Status { get; set; } = "Pendiente";
    public string? ConfirmationNumber { get; set; }
    // Direccion del hotel (campo de "Mas detalles"). Se expone para que la ficha de edicion
    // la recupere y no la pise al guardar (round-trip). No es dato de costo.
    public string? Address { get; set; }
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
    /// <summary>
    /// ADR-017 F1.4 (§2.5): fecha limite de seña/pago al operador (date-only, null = sin fecha). Se expone
    /// para que la fila/ficha pinte la etiqueta de vencimiento en F2; el dato debe viajar aunque todavia no
    /// haya UI. NO es un dato de costo.
    /// </summary>
    public DateTime? OperatorPaymentDeadline { get; set; }
    /// <summary>"TariffAtBookingTime" if a Rate was applied; "Manual" otherwise.</summary>
    public string SnapshotSource { get; set; } = "Manual";
    public string SourceKind { get; set; } = "Hotel";
    public string WorkflowStatus { get; set; } = "Solicitado";
    public string? RoomingAssignments { get; set; }
}

