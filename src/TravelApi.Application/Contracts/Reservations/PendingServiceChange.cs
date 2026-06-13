namespace TravelApi.Application.Contracts.Reservations;

/// <summary>
/// ADR-027 (detalle "confirmada con cambios", 2026-06-13): descriptor de UN cambio de precio/costo de un
/// servicio que viaja desde el path de edicion (BookingService de los 5 tipos + servicio generico) hasta el
/// trigger que marca la reserva "confirmada con cambios" y persiste el detalle.
///
/// <para>El path de edicion es el unico que conoce los valores ANTERIORES (los capturo antes de pisar la
/// entidad) y la identidad del servicio. En vez de threadear cada dato suelto por la firma de
/// <c>UpdateBalanceAsync</c>, se arma este objeto en el call-site y el trigger decide (estado vivo) si lo
/// registra. Si no hubo cambio significativo, el call-site pasa <c>null</c> y el trigger no hace nada.</para>
/// </summary>
public sealed class PendingServiceChange
{
    /// <summary>Tipo de servicio en terminos de negocio ("Hotel", "Aereo", "Traslado", "Paquete", "Asistencia", o el del generico).</summary>
    public required string ServiceType { get; init; }

    /// <summary>Nombre/descripcion del servicio (ej. "Hotel NH Centro"), para que el dueño lo reconozca en la franja.</summary>
    public required string ServiceDescription { get; init; }

    /// <summary>PublicId del servicio que cambio. Null si no se conocia (el front no lo podra linkear, pero el cambio se registra igual).</summary>
    public Guid? ServicePublicId { get; init; }

    /// <summary>Moneda del servicio ("ARS"/"USD"). Sin moneda explicita el trigger normaliza a ARS.</summary>
    public string? Currency { get; init; }

    /// <summary>Precio de venta ANTERIOR (null si el campo no cambio en esta edicion).</summary>
    public decimal? OldSalePrice { get; init; }

    /// <summary>Precio de venta NUEVO (par de <see cref="OldSalePrice"/>).</summary>
    public decimal? NewSalePrice { get; init; }

    /// <summary>Costo ANTERIOR (null si el campo no cambio en esta edicion).</summary>
    public decimal? OldNetCost { get; init; }

    /// <summary>Costo NUEVO (par de <see cref="OldNetCost"/>).</summary>
    public decimal? NewNetCost { get; init; }

    /// <summary>True si cambio el precio de venta (los dos valores estan presentes y son distintos).</summary>
    public bool SalePriceChanged => OldSalePrice.HasValue && NewSalePrice.HasValue && OldSalePrice.Value != NewSalePrice.Value;

    /// <summary>True si cambio el costo (los dos valores estan presentes y son distintos).</summary>
    public bool NetCostChanged => OldNetCost.HasValue && NewNetCost.HasValue && OldNetCost.Value != NewNetCost.Value;

    /// <summary>True si hubo algun cambio significativo (precio o costo). Es lo que decide si se registra detalle.</summary>
    public bool HasMeaningfulChange => SalePriceChanged || NetCostChanged;

    /// <summary>
    /// Descriptor "marcar sin detalle": lo usa la sobrecarga legacy <c>UpdateBalanceAsync(int, bool)</c> con
    /// <c>true</c>, cuando un caller sabe que hubo cambio significativo pero NO trae el que/cuanto. El trigger
    /// levanta la bandera pero no inserta una fila de detalle. Se reconoce por no tener servicio ni montos.
    /// </summary>
    public static PendingServiceChange MarkOnly { get; } = new()
    {
        ServiceType = string.Empty,
        ServiceDescription = string.Empty,
    };

    /// <summary>True si es el descriptor <see cref="MarkOnly"/> (marcar la bandera, sin registrar detalle).</summary>
    public bool IsMarkOnly => !HasMeaningfulChange && string.IsNullOrEmpty(ServiceType);
}
