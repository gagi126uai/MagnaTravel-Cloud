using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TravelApi.Domain.Entities;

/// <summary>
/// Campo de un servicio que cambio y dejo la reserva "confirmada con cambios". Hoy solo se trackean los dos
/// que mueven plata (precio de venta y costo); el enum existe para que el front sepa que columna mostrar y
/// para poder enmascarar el costo a quien no lo puede ver.
/// </summary>
public static class PendingChangeFields
{
    /// <summary>El operador confirmo con OTRO precio de venta al cliente.</summary>
    public const string SalePrice = "SalePrice";

    /// <summary>Cambio el costo del proveedor (dato sensible: se enmascara sin cobranzas.see_cost).</summary>
    public const string NetCost = "NetCost";
}

/// <summary>
/// ADR-027 (auditoria ERP, hallazgo #10): DETALLE de UN cambio de precio/costo que dejo la reserva
/// "confirmada con cambios". El flag <see cref="Reserva.HasUnacknowledgedChanges"/> dice "hay algo para
/// revisar"; estas filas dicen QUE cambio: que servicio, que campo, de cuanto a cuanto, en que moneda y
/// quien/cuando.
///
/// <para><b>Por que una tabla hija y no un JSON en la reserva</b>: se acumulan varios cambios (varios
/// servicios pueden cambiar antes del OK), se consultan/enmascaran por fila y se limpian de una al dar el OK.
/// Una tabla con columnas tipadas es mas clara para el equipo y mas facil de enmascarar (el costo es
/// sensible) que parsear un JSON. Se borra en cascada con la reserva.</para>
///
/// <para><b>Ciclo de vida</b>: una fila por cada edicion de precio/costo en estado vivo. El endpoint
/// <c>acknowledge-changes</c> borra TODAS las filas de la reserva al dar el OK (junto con bajar el flag).
/// No se actualiza una fila existente: cada edicion deja su propio rastro (auditoria de cada cambio).</para>
///
/// <para><b>Dato sensible</b>: cuando <see cref="Field"/> es <see cref="PendingChangeFields.NetCost"/>, los
/// montos <see cref="OldValue"/>/<see cref="NewValue"/> son COSTO. El read-model los enmascara a quien no
/// tiene <c>cobranzas.see_cost</c>, igual que el resto de los costos del sistema.</para>
/// </summary>
public class ReservaPendingChange
{
    public int Id { get; set; }

    /// <summary>FK a <see cref="Reserva"/> (tabla "TravelFiles"). Cascade: al borrar la reserva se borran sus cambios.</summary>
    public int ReservaId { get; set; }
    public Reserva? Reserva { get; set; }

    /// <summary>
    /// Tipo de servicio que cambio, en terminos de negocio ("Hotel", "Aereo", "Traslado", "Paquete",
    /// "Asistencia" o el tipo del servicio generico). Es para mostrar, no para resolver reglas.
    /// </summary>
    [MaxLength(50)]
    public string ServiceType { get; set; } = string.Empty;

    /// <summary>Nombre/descripcion del servicio que cambio (ej. "Hotel NH Centro"), para que el dueño lo reconozca.</summary>
    [MaxLength(300)]
    public string ServiceDescription { get; set; } = string.Empty;

    /// <summary>
    /// PublicId del servicio que cambio (el del servicio tipado o generico). Permite al front linkear el
    /// cambio con la fila del servicio. Nullable por robustez (un servicio sin PublicId no rompe el tracking).
    /// </summary>
    public Guid? ServicePublicId { get; set; }

    /// <summary>Campo que cambio (ver <see cref="PendingChangeFields"/>): precio de venta o costo.</summary>
    [MaxLength(20)]
    public string Field { get; set; } = string.Empty;

    /// <summary>Valor ANTERIOR del campo (en <see cref="Currency"/>).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal OldValue { get; set; }

    /// <summary>Valor NUEVO del campo (en <see cref="Currency"/>).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal NewValue { get; set; }

    /// <summary>Moneda del servicio que cambio ("ARS"/"USD"). Para no mezclar montos de distinta moneda en la franja.</summary>
    [MaxLength(3)]
    public string Currency { get; set; } = Monedas.ARS;

    /// <summary>Usuario que hizo el cambio (auditoria). Puede ser null si el cambio entro por un camino sin usuario (job/test).</summary>
    [MaxLength(200)]
    public string? ChangedByUserId { get; set; }

    /// <summary>Snapshot del nombre de quien hizo el cambio (par de <see cref="ChangedByUserId"/>).</summary>
    [MaxLength(200)]
    public string? ChangedByUserName { get; set; }

    /// <summary>Cuando se registro el cambio.</summary>
    public DateTime ChangedAt { get; set; } = DateTime.UtcNow;
}
