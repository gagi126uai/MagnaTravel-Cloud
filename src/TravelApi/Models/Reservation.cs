using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Models;

public static class ServiceTypes
{
    public const string Flight = "Aereo";
    public const string Hotel = "Hotel";
    public const string Transfer = "Traslado";
    public const string Insurance = "Asistencia";
    public const string Excursion = "Excursion";
    public const string Package = "Paquete";
    public const string Other = "Otro";
}

public static class ReservationStatuses
{
    public const string Draft = "Borrador";
    public const string Requested = "Solicitado";
    public const string Confirmed = "Confirmado";
    public const string Issued = "Emitido";
    public const string Cancelled = "Cancelado";
}

public class Reservation
{
    public int Id { get; set; }
    
    // Core Links
    public int? TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }

    public int? CustomerId { get; set; } 
    public Customer? Customer { get; set; }
    
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    public string? ConfirmationNumber { get; set; }
    public string Status { get; set; } = ReservationStatuses.Draft;
    public string? ServiceType { get; set; } = ServiceTypes.Flight;
    public string? ProductType { get; set; } = ServiceTypes.Flight;
    
    public string? Description { get; set; }

    // Dates
    public DateTime DepartureDate { get; set; }
    public DateTime? ReturnDate { get; set; }

    // Financials
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; } = 0;

    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; } = 0;

    public string? SupplierName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public string? ServiceDetailsJson { get; set; } 
    public ICollection<FlightSegment> Segments { get; set; } = new List<FlightSegment>();
}
