using TravelApi.Domain.Entities;

namespace TravelApi.Application.DTOs;

public class ServicioReservaDto
{
    public Guid PublicId { get; set; }
    public string? ServiceType { get; set; }
    public string? ProductType { get; set; }
    public string? Description { get; set; }
    public string? ConfirmationNumber { get; set; }
    public string Status { get; set; } = ReservationStatuses.Draft;
    
    public DateTime DepartureDate { get; set; }
    public DateTime? ReturnDate { get; set; }

    // Auditoria ERP 2026-06-12 (item 5): fecha limite de pago al operador del servicio generico.
    // Aditivo, null = no informada. Ver ServicioReserva.OperatorPaymentDeadline.
    public DateTime? OperatorPaymentDeadline { get; set; }

    public decimal NetCost { get; set; }
    public decimal SalePrice { get; set; }
    public decimal Commission { get; set; }
    public decimal Tax { get; set; }
    
    public string? SupplierName { get; set; }
    public Guid? SupplierPublicId { get; set; }
    
    public Guid? RatePublicId { get; set; }
    public Guid? ReservaPublicId { get; set; }
    
    public string SourceKind { get; set; } = "Generic";
    public string WorkflowStatus { get; set; } = "Solicitado";

    /// <summary>
    /// ADR-021 Capa 7: moneda del servicio (costo y venta van en la misma moneda). El front la lee como
    /// <c>svc.currency</c> para el badge de moneda y para totalizar la lista de servicios POR moneda.
    /// Default "ARS"; el mapeo normaliza null/legacy a ARS (<c>Monedas.Normalizar</c>). NO es dato de
    /// costo, no se enmascara con see_cost (es solo el rotulo de moneda).
    /// </summary>
    public string Currency { get; set; } = "ARS";

    // Auditoria de cancelacion (ADR-020): cuando se cancelo el servicio generico y quien lo cancelo. El
    // front los muestra como "Cancelado por X el DD/MM/YYYY". Null = no cancelado. NO son datos de costo
    // ni fiscales: no se enmascaran. Mapean por convencion (mismo nombre que la entidad ServicioReserva).
    public DateTime? CancelledAt { get; set; }
    public string? CancelledByUserName { get; set; }
}
