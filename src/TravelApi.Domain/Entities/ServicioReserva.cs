using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

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

public class ServicioReserva : IHasPublicId
{
    public int Id { get; set; }
    public Guid PublicId { get; set; } = Guid.NewGuid();
    
    // Core Links
    public int? ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    public int? CustomerId { get; set; } 
    public Customer? Customer { get; set; }
    
    public int? SupplierId { get; set; }
    public Supplier? Supplier { get; set; }
    
    public int? RateId { get; set; }
    public Rate? Rate { get; set; }
    
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

    /// <summary>
    /// ADR-021 (multimoneda, 2026-06-08): moneda en que va ESTE servicio generico (costo y
    /// venta SIEMPRE en la misma moneda, decision del dueno). Espejo de los 5 servicios tipados
    /// que ya tienen <c>Currency</c> desde AddBookingCurrencyTraceability.
    ///
    /// <para><c>null</c> = legacy / no informado = se lee como ARS (<c>Monedas.Normalizar</c>).
    /// Se mantiene nullable a proposito: evita una migracion NOT NULL sobre una columna con
    /// datos. Valores soportados: <c>Monedas.Soportadas</c> (ARS/USD).</para>
    ///
    /// <para>El calculo de saldo agrupa por esta moneda (Capa 2). null = legacy = ARS.</para>
    /// </summary>
    [MaxLength(3)]
    public string? Currency { get; set; }

    public string? SupplierName { get; set; }

    /// <summary>
    /// Auditoria ERP 2026-06-12 (item 5): fecha limite de pago al operador del servicio generico.
    /// Mismo criterio que <see cref="HotelBooking.OperatorPaymentDeadline"/>. Opcional (null = no
    /// informada). Date-only "de pared" Kind=Utc.
    /// </summary>
    public DateTime? OperatorPaymentDeadline { get; set; }

    // === ADR-020 (2026-06-07): trazabilidad de confirmacion del operador y de cancelacion del servicio ===

    /// <summary>
    /// ADR-020: fecha en que el operador CONFIRMO este servicio (la estampa el motor de estados).
    /// Null = nunca confirmado. NO se borra al des-confirmar. Gobierna borrar-vs-cancelar y penalidades.
    /// </summary>
    public DateTime? ConfirmedAt { get; set; }

    /// <summary>ADR-020: cuando se cancelo el servicio (Status -> Cancelado). Null = no cancelado.</summary>
    public DateTime? CancelledAt { get; set; }

    public string? CancelledByUserId { get; set; }

    public string? CancelledByUserName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public string? ServiceDetailsJson { get; set; } 
    public ICollection<FlightSegment> Segments { get; set; } = new List<FlightSegment>();
}
