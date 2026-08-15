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

/// <summary>
/// Semaforo de DNI vencido para cabotaje (decision firmada del dueño, 2026-08-03): ambito geografico de
/// un servicio generico. Solo importa si HAY AL MENOS UN servicio <see cref="Domestic"/> en la reserva:
/// ese es el dato que dispara <see cref="TravelApi.Domain.Helpers.DniExpiryRules"/> (volar dentro del
/// pais exige documento vigente). Se persiste como entero (EF default enum-&gt;int); el orden de los
/// valores NO debe cambiarse porque ya queda escrito en la columna.
/// </summary>
public enum ServiceGeographicScope
{
    /// <summary>Default para TODO lo existente y para un servicio nuevo sin cargar: el vendedor no lo definio.</summary>
    Undefined = 0,

    /// <summary>Vuelo/servicio DENTRO del pais (cabotaje). Dispara el semaforo de DNI si el pasajero tiene uno vencido.</summary>
    Domestic = 1,

    /// <summary>Vuelo/servicio fuera del pais. NO dispara el semaforo de DNI (ahi lo que importa es el pasaporte).</summary>
    International = 2,
}

/// <summary>
/// Traduce <see cref="ServiceGeographicScope"/> a/desde el texto legible que viaja en los DTOs
/// ("Nacional"/"Internacional"). El entero del enum NUNCA sale de este proceso (gate de exposicion:
/// un numero de enum crudo es informacion tecnica que un usuario no-programador no entiende).
/// </summary>
public static class ServiceGeographicScopeText
{
    public const string Domestic = "Nacional";
    public const string International = "Internacional";

    /// <summary>
    /// Token explicito que manda el front cuando el vendedor VUELVE a elegir "Sin definir" a proposito
    /// (por ejemplo, corrigio un vuelo que habia marcado "Nacional" por error). Es distinto de mandar
    /// vacio/nulo: eso significa "no toque este campo" (ver <see cref="ParseOrNull"/>). Sin este token,
    /// un vuelo mal marcado quedaba avisando PARA SIEMPRE porque el update nunca sabia que el vendedor
    /// queria borrar el ambito, no simplemente omitirlo.
    /// </summary>
    public const string Cleared = "SinDefinir";

    /// <summary>Undefined se expone como <c>null</c>: "no definido" no necesita chip ni texto en pantalla.</summary>
    public static string? ToDisplayText(ServiceGeographicScope scope) => scope switch
    {
        ServiceGeographicScope.Domestic => Domestic,
        ServiceGeographicScope.International => International,
        _ => null,
    };

    /// <summary>
    /// Interpreta el texto que manda el alta/edicion del servicio. Vacio (nada mandado) devuelve
    /// <c>null</c> (validacion SUAVE: el caller simplemente no toca el ambito ya guardado). El token
    /// <see cref="Cleared"/> es la unica forma de pedir explicitamente "Sin definir" y SI devuelve un
    /// valor (<see cref="ServiceGeographicScope.Undefined"/>), para que el caller lo asigne como
    /// cualquier otro valor reconocido.
    /// </summary>
    public static ServiceGeographicScope? ParseOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = text.Trim();

        if (string.Equals(normalized, Domestic, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceGeographicScope.Domestic;
        }

        if (string.Equals(normalized, International, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceGeographicScope.International;
        }

        if (string.Equals(normalized, Cleared, StringComparison.OrdinalIgnoreCase))
        {
            return ServiceGeographicScope.Undefined;
        }

        return null;
    }
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
    /// Semaforo de DNI vencido para cabotaje (decision firmada del dueño, 2026-08-03): ambito geografico
    /// de ESTE servicio (ver <see cref="ServiceGeographicScope"/>). Opcional, default
    /// <see cref="ServiceGeographicScope.Undefined"/> para TODO lo existente (no hay forma de inferir
    /// retroactivamente si un vuelo viejo era nacional o internacional). El vendedor lo carga si quiere
    /// activar el aviso; nunca es obligatorio para guardar el servicio.
    /// </summary>
    public ServiceGeographicScope GeographicScope { get; set; } = ServiceGeographicScope.Undefined;

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

    /// <summary>ADR-050 (2026-07-24): <c>Status</c> justo antes de cancelarse, para que "Volver atrás" lo
    /// restaure exacto. Ver el XML-doc gemelo en <see cref="HotelBooking.StatusBeforeCancellation"/>.</summary>
    [MaxLength(50)]
    public string? StatusBeforeCancellation { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Obra "PDF ronda 2" (2026-08-14, spec §6): plan de cuotas informativo del servicio genérico, mismo
    /// criterio que <see cref="HotelBooking.InstallmentsCount"/> (no toca <see cref="SalePrice"/> ni el
    /// saldo; el PDF solo imprime la línea si AMBOS campos están cargados).
    /// </summary>
    public int? InstallmentsCount { get; set; }

    /// <summary>Monto de cada cuota del servicio genérico. Ver <see cref="InstallmentsCount"/>.</summary>
    [Column(TypeName = "decimal(12,2)")]
    public decimal? InstallmentAmount { get; set; }

    public ICollection<Payment> Payments { get; set; } = new List<Payment>();
    public string? ServiceDetailsJson { get; set; } 
    public ICollection<FlightSegment> Segments { get; set; } = new List<FlightSegment>();
}
