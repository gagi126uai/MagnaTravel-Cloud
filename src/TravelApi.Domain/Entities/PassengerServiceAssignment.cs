using System.ComponentModel.DataAnnotations;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Constantes para el campo discriminator ServiceType.
/// No es enum porque el modelo soporta nuevos tipos de servicio sin migracion.
/// </summary>
public static class AssignmentServiceType
{
    public const string Hotel = "Hotel";
    public const string Transfer = "Transfer";
    public const string Package = "Package";
    public const string Flight = "Flight";
    public const string Generic = "Generic"; // ServicioReserva generico

    public static readonly string[] All = { Hotel, Transfer, Package, Flight, Generic };
}

/// <summary>
/// Asignacion N:M entre Passenger y un servicio especifico (HotelBooking, TransferBooking,
/// PackageBooking, FlightSegment o ServicioReserva generico).
/// El passenger puede existir en la Reserva sin asignaciones (asignacion no obligatoria).
/// Un passenger no puede estar asignado dos veces al mismo servicio (constraint unico).
///
/// ServiceId es soft FK: apunta al Id en la tabla del ServiceType correspondiente, pero
/// EF no maneja la cascada porque la tabla destino varia. La integridad la maneja el
/// service en el backend al crear/borrar asignaciones.
/// </summary>
public class PassengerServiceAssignment : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();

    public int PassengerId { get; set; }
    public Passenger? Passenger { get; set; }

    [Required]
    [MaxLength(20)]
    public string ServiceType { get; set; } = string.Empty;

    public int ServiceId { get; set; }

    // Metadata opcional especifica al tipo
    public int? RoomNumber { get; set; }    // Hotel rooming: 1, 2, 3...
    [MaxLength(20)]
    public string? SeatNumber { get; set; } // Flight seat: 12A, 14C...

    [MaxLength(500)]
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
