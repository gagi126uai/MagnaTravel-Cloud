using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Estados del ciclo de vida de una Reserva. Los strings se persisten asi en BD
/// y los nombres de los miembros (ingles) reflejan la semantica funcional:
///  - Budget: presupuesto entregado al cliente, sin compromisos con proveedor.
///  - Confirmed: confirmada con proveedor, en gestion (cobro, vouchers, etc).
///  - Traveling: el cliente ya esta viajando (StartDate <= hoy <= EndDate).
///  - Closed: viaje terminado y cierre administrativo completo (Balance == 0).
///  - Cancelled: cancelada antes de viajar.
///
/// "Archived" es un estado adicional usado para soft-delete de reservas viejas
/// y se referencia como literal "Archived" (legacy) — no esta en este enum.
///
/// La UI muestra los labels en espanol (ver ReservaStatusBadge.statusConfig
/// en el frontend): Presupuesto / Confirmada / En viaje / Finalizada / Cancelada.
/// </summary>
public static class EstadoReserva
{
    public const string Budget = "Budget";
    public const string Confirmed = "Confirmed";
    public const string Traveling = "Traveling";
    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";
}

public class Reserva : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    [Required]
    [MaxLength(50)]
    public string NumeroReserva { get; set; } = string.Empty;
    
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }
    
    [Required]
    [MaxLength(50)]
    public string Status { get; set; } = EstadoReserva.Budget;
    
    // Dates
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
    public DateTime? ClosedAt { get; set; }

    // Payer/Main Client
    public int? PayerId { get; set; }
    public Customer? Payer { get; set; }

    // Commercial traceability
    public int? SourceQuoteId { get; set; }
    public Quote? SourceQuote { get; set; }

    public int? SourceLeadId { get; set; }
    public Lead? SourceLead { get; set; }

    public string? ResponsibleUserId { get; set; }
    public ApplicationUser? ResponsibleUser { get; set; }

    [MaxLength(50)]
    public string? WhatsAppPhoneOverride { get; set; }

    // Financials
    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalCost { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalSale { get; set; } = 0;
    
    [Column(TypeName = "decimal(18,2)")]
    public decimal Balance { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalPaid { get; set; } = 0;

    // Passenger counts (only used while Status = Budget; replaced by individual Passengers when promoted)
    public int AdultCount { get; set; } = 0;
    public int ChildCount { get; set; } = 0;
    public int InfantCount { get; set; } = 0;

    // Navigation
    public ICollection<ServicioReserva> Servicios { get; set; } = new List<ServicioReserva>();
    public ICollection<Passenger> Passengers { get; set; } = new List<Passenger>();
    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    
    // Servicios específicos
    public ICollection<HotelBooking> HotelBookings { get; set; } = new List<HotelBooking>();
    public ICollection<TransferBooking> TransferBookings { get; set; } = new List<TransferBooking>();
    public ICollection<PackageBooking> PackageBookings { get; set; } = new List<PackageBooking>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<FlightSegment> FlightSegments { get; set; } = new List<FlightSegment>();
    public ICollection<ReservaAttachment> Attachments { get; set; } = new List<ReservaAttachment>();
    public ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
    public ICollection<WhatsAppDelivery> WhatsAppDeliveries { get; set; } = new List<WhatsAppDelivery>();
    public ICollection<MessageDelivery> MessageDeliveries { get; set; } = new List<MessageDelivery>();
    public ICollection<ManualCashMovement> ManualCashMovements { get; set; } = new List<ManualCashMovement>();
}
