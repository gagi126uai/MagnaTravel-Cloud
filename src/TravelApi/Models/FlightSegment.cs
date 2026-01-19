using System.ComponentModel.DataAnnotations;

namespace TravelApi.Models;

public class FlightSegment
{
    public int Id { get; set; }
    
    [Required]
    [MaxLength(3)]
    public string AirlineCode { get; set; } = string.Empty; // e.g. "AA"

    [Required]
    [MaxLength(10)]
    public string FlightNumber { get; set; } = string.Empty; // e.g. "900"

    [Required]
    [MaxLength(3)]
    public string Origin { get; set; } = string.Empty; // e.g. "MIA"

    [Required]
    [MaxLength(3)]
    public string Destination { get; set; } = string.Empty; // e.g. "EZE"

    public DateTime DepartureTime { get; set; }
    public DateTime ArrivalTime { get; set; }

    [MaxLength(2)]
    public string Status { get; set; } = "HK"; // HK = Holding Confirmed

    public int ReservationId { get; set; }
    public Reservation? Reservation { get; set; }
}
