namespace TravelApi.Application.DTOs;

/// <summary>
/// DTO de salida de una Asistencia al viajero (seguro). Espejo de <see cref="HotelBookingDto"/>:
/// expone NetCost (que se ENMASCARA a 0 si el caller no tiene cobranzas.see_cost) y NO expone
/// Commission — la comision de la agencia nunca viaja al frontend, igual que Hotel/Flight.
/// </summary>
public class AssistanceBookingDto
{
    public Guid PublicId { get; set; }
    public Guid SupplierPublicId { get; set; }
    public string SupplierName { get; set; } = string.Empty;
    public Guid? RatePublicId { get; set; }

    // Datos de negocio del seguro.
    public string? PolicyNumber { get; set; }
    public string? PlanType { get; set; }
    public string? CoverageLimit { get; set; }
    public string? CoverageZone { get; set; }

    // Vigencia (date-only, mismo manejo que Hotel CheckIn/CheckOut).
    public DateTime ValidFrom { get; set; }
    public DateTime ValidTo { get; set; }

    public int Adults { get; set; } = 1;
    public int Children { get; set; } = 0;

    public string Status { get; set; } = "Solicitado";
    public string? ConfirmationNumber { get; set; }
    public string? Notes { get; set; }

    public decimal SalePrice { get; set; }
    // NetCost se enmascara a 0 para usuarios sin cobranzas.see_cost (ver CostMasking.MaskAssistanceAsync).
    public decimal NetCost { get; set; }

    // Impuestos incluidos en el costo (mismo criterio que FlightSegmentDto.Tax). No suma al precio
    // que paga el cliente; se expone para el detalle. Default 0 en filas legacy.
    public decimal Tax { get; set; }

    /// <summary>
    /// Moneda en que se cotizo el servicio (trazabilidad, copiada del tarifario).
    /// Null = legacy / no informado. NO se usa todavia en calculos de saldo.
    /// </summary>
    public string? Currency { get; set; }

    /// <summary>"TariffAtBookingTime" si se aplico una tarifa; "Manual" en caso contrario.</summary>
    public string SnapshotSource { get; set; } = "Manual";

    public string SourceKind { get; set; } = "Assistance";
    public string WorkflowStatus { get; set; } = "Solicitado";
}
