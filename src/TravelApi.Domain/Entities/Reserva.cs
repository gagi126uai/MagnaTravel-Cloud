using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Estados del ciclo de vida de una Reserva. Los strings se persisten asi en BD
/// y los nombres de los miembros (ingles) reflejan la semantica funcional.
///
/// <para>CICLO CLASICO (cuando el flag <c>EnableSoldToSettleStates</c> esta APAGADO,
/// que es el comportamiento historico y el default en prod):</para>
///  - Budget: presupuesto entregado al cliente, sin compromisos con proveedor.
///  - Confirmed: vendida y activa, en gestion (cobro, vouchers, etc).
///  - Traveling: el cliente ya esta viajando (StartDate &lt;= hoy &lt;= EndDate).
///  - Closed: viaje terminado y cierre administrativo completo (Balance == 0).
///  - Cancelled: cancelada antes de viajar.
///
/// <para>CICLO NUEVO (cuando el flag <c>EnableSoldToSettleStates</c> esta PRENDIDO,
/// rediseño Fase A+B 2026-05-30). Se agregan dos paradas intermedias para reflejar
/// mejor el flujo real de una agencia: primero se vende, despues el operador confirma;
/// y al volver del viaje hay un periodo de liquidacion con el operador antes del cierre:</para>
///  - Budget: igual que antes (presupuesto).
///  - Sold (NUEVO): vendida al cliente, esperando que el operador confirme los servicios.
///    Aca ya se exige al menos un servicio + pasajeros nominales (gate que antes vivia
///    en Budget-&gt;Confirmed).
///  - Confirmed: ahora significa SOLO "el operador confirmo los servicios".
///  - Traveling: el cliente ya esta viajando.
///  - ToSettle (NUEVO): el viaje termino y falta liquidar con el operador. Reemplaza
///    el salto directo Traveling-&gt;Closed: primero pasa por "a liquidar".
///  - Closed: cierre administrativo completo (Balance == 0).
///
/// <para>Estados laterales (no cambian con el flag): Cancelled, PendingOperatorRefund,
/// y el legacy "Archived".</para>
///
/// "Archived" es un estado adicional usado para soft-delete de reservas viejas
/// y se referencia como literal "Archived" (legacy) — no esta en este enum.
///
/// La UI muestra los labels en espanol (ver ReservaStatusBadge.statusConfig
/// en el frontend): Presupuesto / Vendida / Confirmada / En viaje / A liquidar /
/// Finalizada / Cancelada.
/// </summary>
public static class EstadoReserva
{
    public const string Budget = "Budget";

    /// <summary>
    /// Rediseño Fase A+B (2026-05-30, flag <c>EnableSoldToSettleStates</c>): vendida al
    /// cliente pero todavia esperando que el operador confirme los servicios. Es el primer
    /// paso despues de Budget en el ciclo nuevo. Solo se usa con el flag PRENDIDO.
    /// </summary>
    public const string Sold = "Sold";

    public const string Confirmed = "Confirmed";
    public const string Traveling = "Traveling";

    /// <summary>
    /// Rediseño Fase A+B (2026-05-30, flag <c>EnableSoldToSettleStates</c>): el viaje
    /// termino y falta liquidar con el operador (pagar saldos, cerrar cuentas). Es el paso
    /// previo a Closed en el ciclo nuevo. Solo se usa con el flag PRENDIDO.
    /// </summary>
    public const string ToSettle = "ToSettle";

    public const string Closed = "Closed";
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// ADR-002 FC1 (2026-05-13): cancelacion con el cliente completada pero
    /// el operador aun no devolvio el dinero. La reserva ya no es operativa,
    /// pero queda visible en alertas de deuda y excluida de revenue queries
    /// hasta que llegue el refund (T2 del flujo) o se marque AbandonedByOperator.
    /// </summary>
    public const string PendingOperatorRefund = "PendingOperatorRefund";
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

    /// <summary>
    /// Snapshot denormalizado del FullName del usuario responsable al momento de
    /// asignacion. Se mantiene aca para evitar que Domain dependa de
    /// ASP.NET Identity (ApplicationUser vive en Infrastructure). Patron consistente
    /// con Voucher.CreatedByUserName.
    /// </summary>
    [MaxLength(200)]
    public string? ResponsibleUserName { get; set; }

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
    // Asistencias al viajero (seguros). Tipo de servicio propio, espejo de los otros 4.
    public ICollection<AssistanceBooking> AssistanceBookings { get; set; } = new List<AssistanceBooking>();
    public ICollection<Invoice> Invoices { get; set; } = new List<Invoice>();
    public ICollection<FlightSegment> FlightSegments { get; set; } = new List<FlightSegment>();
    public ICollection<ReservaAttachment> Attachments { get; set; } = new List<ReservaAttachment>();
    public ICollection<Voucher> Vouchers { get; set; } = new List<Voucher>();
    public ICollection<WhatsAppDelivery> WhatsAppDeliveries { get; set; } = new List<WhatsAppDelivery>();
    public ICollection<MessageDelivery> MessageDeliveries { get; set; } = new List<MessageDelivery>();
    public ICollection<ManualCashMovement> ManualCashMovements { get; set; } = new List<ManualCashMovement>();
}
