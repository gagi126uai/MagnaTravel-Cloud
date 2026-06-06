namespace TravelApi.Application.DTOs;

public class PackageBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }
    public string PackageName { get; set; } = string.Empty;
    // ADR-018: Destination pasa a nullable (la ficha "producto-primero" identifica con PackageName).
    public string? Destination { get; set; }
    public DateTime StartDate { get; set; }
    // ADR-018: EndDate nullable. Null = la ficha no cargo fecha de fin (evita pintar 0001-01-01 en el front).
    public DateTime? EndDate { get; set; }
    public int Nights { get; set; }
    // Pasajeros del paquete, separados por categoria (editables).
    public int Adults { get; set; } = 2;
    public int Children { get; set; } = 0;
    // Ficha F2: base de ocupacion ("double"/"triple"/etc). Se expone para que la ficha de edicion lo
    // recupere y lo reenvie sin pisarlo (round-trip). Null en filas legacy.
    public string? OccupancyBase { get; set; }
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
    /// <summary>
    /// ADR-017 F1.4 (§2.5): fecha limite de seña/pago al operador (date-only, null = sin fecha). Se expone
    /// para que la fila/ficha pinte la etiqueta de vencimiento en F2. NO es un dato de costo.
    /// </summary>
    public DateTime? OperatorPaymentDeadline { get; set; }
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
    public string SourceKind { get; set; } = "Package";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
