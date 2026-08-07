namespace TravelApi.Domain.Helpers;

/// <summary>
/// Como se ESCRIBEN, para una persona, los codigos internos del catalogo (tipo de servicio y unidad de
/// precio). Lo arma el motor (T-13) para que ninguna pantalla tenga que traducir codigos ni mostrar
/// jerga interna como "noche_habitacion".
/// </summary>
public static class CatalogDisplayLabels
{
    /// <summary>"Aereo" -> "Aéreo". Si el tipo no esta en la lista, se devuelve tal cual vino.</summary>
    public static string ServiceType(string? serviceType)
    {
        var key = TextNormalizer.NormalizeForMatch(serviceType);
        return key switch
        {
            "hotel" => "Hotel",
            "aereo" => "Aéreo",
            "traslado" => "Traslado",
            "paquete" => "Paquete",
            "asistencia" => "Asistencia",
            "excursion" => "Excursión",
            _ => string.IsNullOrWhiteSpace(serviceType) ? string.Empty : serviceType.Trim()
        };
    }

    /// <summary>
    /// Unidad del precio en criollo: "por noche", "por pasajero", "por pasajero por día".
    /// Devuelve "" cuando la unidad es el servicio entero (no se aclara nada) o no vino.
    /// Cubre los codigos nuevos (<c>CatalogPriceUnits</c>) y los del tarifario viejo ("noche", "trayecto").
    /// </summary>
    public static string PriceUnit(string? priceUnit)
    {
        var key = TextNormalizer.NormalizeForMatch(priceUnit);
        return key switch
        {
            "noche_habitacion" or "noche" => "por noche",
            "pasajero" => "por pasajero",
            "pasajero_dia" => "por pasajero por día",
            "trayecto" => "por trayecto",
            _ => string.Empty
        };
    }
}
