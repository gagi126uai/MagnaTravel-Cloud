namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-036 punto 4c (2026-06-23): vocabulario UNICO del "tipo de registro" de un servicio de la
/// reserva, usado para imputar un pago al operador a UN servicio concreto.
///
/// <para><b>Por que existe</b>: un servicio de la reserva puede vivir en 6 tablas distintas (vuelo,
/// hotel, traslado, paquete, asistencia y el generico). No hay una sola FK que las cubra a todas, asi
/// que la referencia es polimorfica: (recordKind, servicePublicId). Estos strings son EXACTAMENTE los
/// que ya usa el front (SERVICE_RECORD_KIND en reservationServiceModel.js), para que el estado
/// "pagado al operador" por servicio se pueda unir sin traducciones.</para>
/// </summary>
public static class ServicePaymentRecordKinds
{
    public const string Flight = "flight";
    public const string Hotel = "hotel";
    public const string Transfer = "transfer";
    public const string Package = "package";
    public const string Assistance = "assistance";
    public const string Generic = "generic";

    /// <summary>Los 6 valores validos. Cualquier otro recordKind se rechaza al imputar un pago.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Flight, Hotel, Transfer, Package, Assistance, Generic
    };

    /// <summary>
    /// Normaliza un recordKind crudo (recortando y pasando a minusculas) y lo devuelve si es valido;
    /// si no, <c>null</c>. Tolera tambien los "labels de tipo" en espanol que usa la cuenta del
    /// proveedor (Vuelo/Hotel/Traslado/Paquete/Asistencia) por si el front manda ese formato.
    /// </summary>
    public static string? Normalize(string? rawRecordKind)
    {
        if (string.IsNullOrWhiteSpace(rawRecordKind)) return null;
        string lower = rawRecordKind.Trim().ToLowerInvariant();

        return lower switch
        {
            Flight or "vuelo" or "aereo" => Flight,
            Hotel => Hotel,
            Transfer or "traslado" => Transfer,
            Package or "paquete" => Package,
            Assistance or "asistencia" => Assistance,
            Generic or "generico" => Generic,
            _ => null
        };
    }
}
