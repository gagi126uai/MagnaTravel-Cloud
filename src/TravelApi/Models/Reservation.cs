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

public class Reservation
{
    public int Id { get; set; }
    
    // Core Links
    public int? TravelFileId { get; set; }
    public TravelFile? TravelFile { get; set; }

    public int? CustomerId { get; set; } // Legacy or Specific Passenger
    public Customer? Customer { get; set; }
    
    public int? SupplierId { get; set; } // The Provider
    public Supplier? Supplier { get; set; }

    // Erasable/Legacy fields should be deprecated or repurposed
    // public string ReferenceCode { get; set; } -> Use ConfirmationNumber
    
    public string ConfirmationNumber { get; set; } = string.Empty; // PNR / Booking ID
    public string Status { get; set; } = ReservationStatuses.Draft; // HK, OK, RQ, XX
    public string ServiceType { get; set; } = ServiceTypes.Flight;
    
    public string Description { get; set; } = string.Empty; // "Hotel Miami 3 Nights"

    // Dates
    public DateTime DepartureDate { get; set; } // CheckIn / Flight Date
    public DateTime? ReturnDate { get; set; }   // CheckOut

    // Financials
    [Column(TypeName = "decimal(18,2)")]
    public decimal NetCost { get; set; } = 0; // Costo Neto

    [Column(TypeName = "decimal(18,2)")]
    public decimal SalePrice { get; set; } = 0; // Venta Publico

    [Column(TypeName = "decimal(18,2)")]
    public decimal Commission { get; set; } = 0; // Ganancia o %

    [Column(TypeName = "decimal(18,2)")]
    public decimal Tax { get; set; } = 0;

    // Legacy Mappings (to avoid breaking simple builds immediately, but we should migrate)
    public string ReferenceCode { get { return ConfirmationNumber; } set { ConfirmationNumber = value; } }
    public decimal BasePrice { get { return NetCost; } set { NetCost = value; } }
    public decimal TotalAmount { get { return SalePrice; } set { SalePrice = value; } }
    public string ProductType { get { return ServiceType; } set { ServiceType = value; } }
    public string? SupplierName { get; set; } // Legacy string, prefer SupplierId

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();

    // Flexible Details (JSON) - Simple string storage for now
    public string? ServiceDetailsJson { get; set; } 
    
    // Navigation for flights
    public ICollection<FlightSegment> Segments { get; set; } = new List<FlightSegment>();
}
