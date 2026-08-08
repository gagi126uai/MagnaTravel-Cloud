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

    /// <summary>Como se llama la solapa: "Hoteles", "Aéreos", "Asistencias" (spec 2026-08-07, V8=A).</summary>
    public static string ServiceTypePlural(string? serviceType)
        => TextNormalizer.NormalizeForMatch(serviceType) switch
        {
            "hotel" => "Hoteles",
            "aereo" => "Aéreos",
            "traslado" => "Traslados",
            "paquete" => "Paquetes",
            "asistencia" => "Asistencias",
            "excursion" => "Excursiones",
            _ => ServiceType(serviceType)
        };

    /// <summary>
    /// Como se nombra el producto DENTRO de una frase: "tocá <b>el hotel</b> para verlos". Incluye el
    /// articulo porque en castellano cambia con el genero ("la asistencia").
    /// </summary>
    public static string TheProduct(string? serviceType)
        => TextNormalizer.NormalizeForMatch(serviceType) switch
        {
            "hotel" => "el hotel",
            "aereo" => "el aéreo",
            "traslado" => "el traslado",
            "paquete" => "el paquete",
            "asistencia" => "la asistencia",
            _ => "el producto"
        };

    /// <summary>
    /// Como se llama la variante de cada tipo, para el encabezado de la columna del medio:
    /// "Habitación" en hotel, "Cabina" en aéreo, "Vehículo" en traslado. Vacio donde no hay variante.
    /// </summary>
    public static string VariantColumn(string? serviceType)
        => TextNormalizer.NormalizeForMatch(serviceType) switch
        {
            "hotel" => "Habitación",
            "aereo" => "Cabina",
            "traslado" => "Vehículo",
            _ => string.Empty
        };

    /// <summary>
    /// Como se nombra la variante DENTRO de una frase: "Elegí <b>la cabina</b>". Incluye el articulo
    /// porque en castellano cambia con el genero ("el vehículo"). Vacio donde no hay variante.
    /// </summary>
    public static string TheVariant(string? serviceType)
        => TextNormalizer.NormalizeForMatch(serviceType) switch
        {
            "hotel" => "la habitación",
            "aereo" => "la cabina",
            "traslado" => "el vehículo",
            _ => string.Empty
        };

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
