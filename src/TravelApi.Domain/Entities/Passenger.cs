using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

public class Passenger : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    [Required]
    [MaxLength(200)]
    public string FullName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? DocumentType { get; set; } // DNI, Pasaporte, etc.

    [MaxLength(50)]
    public string? DocumentNumber { get; set; }

    public DateTime? BirthDate { get; set; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 8, decision del dueño): vencimiento del PASAPORTE del pasajero.
    /// Opcional (null = no informado). Alimenta la alarma de vigencia de pasaporte en AlertService:
    /// muchos destinos exigen que el pasaporte siga vigente 6 meses DESPUES de la fecha del viaje, asi
    /// que la alarma avisa cuando el pasaporte vence dentro de los 6 meses posteriores al inicio del
    /// viaje (regla tipica de vigencia). Date-only "de pared" Kind=Utc, igual que BirthDate.
    ///
    /// <para>FUERA DE ALCANCE (auditoria): que documento exige cada destino. Por ahora solo el
    /// vencimiento del pasaporte + la alarma de vigencia.</para>
    /// </summary>
    public DateTime? PassportExpiry { get; set; }

    [MaxLength(50)]
    public string? Nationality { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(200)]
    public string? Email { get; set; }

    // Gender for airline tickets
    [MaxLength(10)]
    public string? Gender { get; set; } // M, F

    // Additional notes
    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
