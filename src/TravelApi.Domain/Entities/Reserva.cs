using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Estados del ciclo de vida de una Reserva (ADR-020, 2026-06-07). Los strings se persisten
/// asi en BD y los nombres de los miembros (ingles) reflejan la semantica funcional. La UI
/// muestra los labels en espanol.
///
/// <para>CICLO UNICO (ya NO hay flag ni ciclo dual; el rediseño Fase A+B con
/// <c>EnableSoldToSettleStates</c> y el estado "Sold"/Vendida murieron en ADR-020):</para>
///  - Quotation (Cotizacion): estado INICIAL unico. Toda reserva nace aca. Borrador interno.
///  - Budget (Presupuesto): el documento que recibe el cliente. Antes era el estado inicial;
///    ahora es la etapa "presupuesto entregado".
///  - InManagement (En gestion): el cliente acepto; se gestionan los servicios con los operadores.
///    Reemplaza al viejo "Sold". El saldo del cliente nace POR SERVICIO CONFIRMADO en esta etapa.
///  - Confirmed (Confirmada): TODOS los servicios estan resueltos (aereo emitido, hotel confirmado,
///    etc.). Solo se ALCANZA y se ABANDONA-hacia-InManagement por el motor automatico
///    (ReservaAutoStateService) — NUNCA por transicion manual. La reserva queda bajo candado.
///  - Traveling (En viaje): el cliente ya esta viajando.
///  - ToSettle (A liquidar): DESVIO MANUAL OPCIONAL post-viaje para liquidar con el operador.
///  - Closed (Finalizada): cierre administrativo completo (saldo a favor o cero).
///  - Lost (Perdido): cotizacion/presupuesto que el cliente NO compro. Queda en historial.
///  - Cancelled (Cancelada): cancelada (flujo ADR-002 con factura viva, o transicion manual
///    sin factura viva desde {InManagement, Confirmed, Traveling, ToSettle}).
///  - PendingOperatorRefund: ver abajo.
///
/// <para>Estado lateral legacy: "Archived" (soft-delete de reservas viejas) se referencia
/// como literal "Archived" — no esta como constante en este enum.</para>
/// </summary>
public static class EstadoReserva
{
    /// <summary>
    /// ADR-020 (2026-06-07): estado INICIAL unico. Toda reserva nace en Cotizacion (borrador
    /// interno). <c>CreateReservaAsync</c> ignora cualquier Status del request y fuerza este valor.
    /// </summary>
    public const string Quotation = "Quotation";

    public const string Budget = "Budget";

    /// <summary>
    /// ADR-020 (2026-06-07): "En gestion". El cliente acepto el presupuesto y se gestionan los
    /// servicios con los operadores. Reemplaza al viejo "Sold" (Vendida). El saldo del cliente
    /// nace por servicio confirmado durante esta etapa. Edicion libre (sin candado).
    /// </summary>
    public const string InManagement = "InManagement";

    public const string Confirmed = "Confirmed";
    public const string Traveling = "Traveling";

    /// <summary>
    /// "A liquidar": DESVIO MANUAL OPCIONAL post-viaje para liquidar con el operador antes de
    /// finalizar. El job de lifecycle NUNCA mete ni saca a nadie de aca; entra/sale solo a mano.
    /// </summary>
    public const string ToSettle = "ToSettle";

    public const string Closed = "Closed";

    /// <summary>
    /// ADR-020 (2026-06-07): "Perdido". Una cotizacion o presupuesto que el cliente no compro.
    /// Queda en el historial. Solo se alcanza desde {Quotation, Budget} y solo si NO hay pagos
    /// vivos. El "el cliente volvio a interesarse" se resuelve con revert al estado de origen
    /// (el <c>FromStatus</c> de la ultima transicion a Lost en ReservaStatusChangeLog).
    /// </summary>
    public const string Lost = "Lost";

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
    public string Status { get; set; } = EstadoReserva.Quotation;
    
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

    /// <summary>
    /// ADR-020 (2026-06-07): venta CONFIRMADA = suma de SalePrice de los servicios RESUELTOS
    /// (<see cref="TravelApi.Domain.Reservations.ServiceResolutionRules"/>.IsResolved). Es la deuda
    /// EXIGIBLE al cliente: un servicio recien "Solicitado" NO suma. Se diferencia de
    /// <see cref="TotalSale"/> (valor comercial del presupuesto = todos los no cancelados, lo que
    /// el cliente ve cotizado). El saldo del cliente es <see cref="Balance"/> = ConfirmedSale - TotalPaid.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal ConfirmedSale { get; set; } = 0;

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

    // ADR-020 F4 (candado): autorizaciones de edicion bajo candado (estado Confirmada en adelante).
    public ICollection<ReservaEditAuthorization> EditAuthorizations { get; set; } = new List<ReservaEditAuthorization>();

    /// <summary>
    /// ADR-020 (decision #6): motivo de la ULTIMA regresion automatica Confirmed -> InManagement.
    /// Lo SETEA el motor (<see cref="TravelApi.Domain.Reservations.ServiceResolutionRules"/> via
    /// ReservaAutoStateService) cuando una reserva confirmada vuelve sola a En gestion (un servicio
    /// dejo de estar resuelto: el operador cancelo/reprogramo o se agrego un servicio nuevo). Lo
    /// LIMPIA (null) el mismo motor cuando la reserva se vuelve a auto-confirmar. El frontend lo usa
    /// para mostrar la franja naranja "volvio a En gestion porque ...". Es informativo, no afecta plata.
    /// </summary>
    [MaxLength(300)]
    public string? LastRegressionReason { get; set; }

    /// <summary>Cuando ocurrio la ultima regresion automatica (par de <see cref="LastRegressionReason"/>).</summary>
    public DateTime? LastRegressionAt { get; set; }
}
