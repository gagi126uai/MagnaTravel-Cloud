namespace TravelApi.Domain.Helpers;

/// <summary>
/// Contrato UNICO de "nombre visible del servicio" por tipo (ADR-018 §4-ter).
///
/// POR QUE existe: la ficha "producto-primero" (ADR-018) identifica vuelos/traslados/paquetes con
/// UN solo texto (<c>ProductName</c> / <c>PackageName</c>) y deja los campos estructurados
/// (AirlineCode, Origin, Destination, Pickup, Dropoff...) en null. Antes cada consumidor (voucher,
/// alerta, reporte) armaba la etiqueta a su manera; con campos null eso producia basura como
/// "Aereo -" o " -> ". Para que NINGUN consumidor reintroduzca ese hueco, todos derivan la identidad
/// del servicio desde ACA.
///
/// Recibe campos PRIMITIVOS (no la entidad) a proposito: asi sirve tanto sobre entidades en memoria
/// (VoucherService) como sobre proyecciones materializadas de EF (AlertService). Los reportes que
/// agregan en SQL (ReportService) no pueden llamar metodos C# dentro de la query; ahi se replica la
/// MISMA regla con un COALESCE traducible, comentado en el call site.
/// </summary>
public static class ServiceDisplayName
{
    /// <summary>
    /// Devuelve el primer candidato con texto real (no null, no espacios); cadena vacia si todos
    /// estan vacios. Inocuo para datos viejos: con los campos estructurados presentes toma el primero,
    /// igual que antes.
    /// </summary>
    public static string FirstNonBlank(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate.Trim();
        }
        return string.Empty;
    }

    /// <summary>
    /// Arma "A -> B" SOLO si ambos extremos tienen texto. Si falta alguno (servicio de catalogo sin
    /// ruta cargada) devuelve cadena vacia, para que el caller caiga al fallback (ProductName) en vez
    /// de mostrar " -> " o "A -> ".
    /// </summary>
    public static string RouteOrEmpty(string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
            return string.Empty;
        return $"{from.Trim()} -> {to.Trim()}";
    }

    /// <summary>
    /// Identidad del vuelo: manda el nombre que vio el vendedor; si no hay, se arma con el codigo de
    /// aerolinea + numero de vuelo. Usado por alertas/reportes y por el titulo del voucher.
    /// </summary>
    public static string ForFlight(string? productName, string? airlineCode, string? flightNumber)
        => FirstNonBlank(productName, $"{airlineCode}{flightNumber}".Trim());

    /// <summary>
    /// Identidad del traslado: nombre del producto; si no, la ruta pickup -> dropoff; si no, el tipo
    /// de vehiculo. ADR-018 Ronda 7: el tipo de vehiculo tambien puede ser null (opcional), asi que
    /// si TODO esta vacio devuelve cadena vacia y el caller muestra solo "Transfer"/"Traslado".
    /// </summary>
    public static string ForTransfer(string? productName, string? pickup, string? dropoff, string? vehicleType)
        => FirstNonBlank(productName, RouteOrEmpty(pickup, dropoff), vehicleType);

    /// <summary>
    /// Identidad del paquete: PackageName es la identidad (sigue NOT NULL); el destino es secundario.
    /// </summary>
    public static string ForPackage(string? packageName, string? destination)
        => FirstNonBlank(packageName, destination);
}
