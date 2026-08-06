using System.Text.RegularExpressions;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Data-exposure (2026-07-03): saneador COMPARTIDO de mensajes de error que podrian llegar al usuario final
/// (un agente de viajes NO tecnico). Reune la deteccion de "ruido tecnico" que antes vivia inline en
/// <c>BookingCancellationService</c>, para que la use tambien <c>InvoiceService</c> (notificaciones) y los
/// controllers (bodies de error) sin duplicar el criterio.
///
/// <para><b>Filosofia: BLOCKLIST, no allowlist.</b> Un allowlist mataria los motivos de AFIP en texto plano
/// que SI son utiles para el vendedor (aprobados en H2, ej. "CUIT del emisor sin habilitacion"). En cambio
/// detectamos el ruido tecnico (XML/SOAP de ARCA, excepciones/stack de .NET, mensajes de EF/Npgsql por
/// carreras) y solo eso se reemplaza por un copy generico. El texto de negocio en espanol limpio pasa igual.</para>
/// </summary>
public static class ArcaErrorSanitizer
{
    /// <summary>Copy generico neutro (sirve para NC y ND) cuando el motivo del organismo es ruido tecnico.</summary>
    public const string GenericArcaMessage =
        "ARCA rechazó la factura. Revisá los datos fiscales o volvé a intentar.";

    // Ruido tecnico que NUNCA debe llegar al usuario. Case-insensitive. Tokens en ingles/simbolos que el texto
    // de negocio en espanol no contiene, para no dar falsos positivos:
    //  - ARCA/SOAP:      XML (<...>), "SOAP", URLs, JSON ({}).
    //  - .NET/stack:     "Exception", "System.", "Traceback", "stack trace", " at TravelApi", ".cs:",
    //                    "Object reference not set", "Value cannot be null", "Parameter '", "Error tecnico".
    //  - EF/Npgsql/DB:   "Sequence contains", "cannot be tracked", "entity type", "The instance of",
    //                    "An error occurred while", "inner exception", "IExecutionStrategy", "DbUpdate",
    //                    "ExecuteSql", "Npgsql", "duplicate key value", "violates".
    //  - Jerga del comprobante (barrido "ayuda invisible", spec firmada 2026-08-06, Parte C):
    //    los NOMBRES DE CAMPO del comprobante electronico (MonCotiz, CbteFch, AlicIva...) y los
    //    nombres de los servicios del organismo (WSFEv1, WSAA, FECAE). El organismo los devuelve tal
    //    cual en sus rechazos ("El campo MonCotiz debe ser..."), y para el que opera el sistema son
    //    tan jerigonza como un stack trace. Ademas la palabra "homologacion", que nombra un modo
    //    interno que el usuario no tiene por que conocer. Ninguna de estas cadenas aparece en un
    //    texto de negocio en castellano, asi que no generan falsos positivos.
    private static readonly Regex TechnicalNoise = new(
        @"(<[^>]+>|Exception|System\.|SOAP|Traceback|stack\s*trace|https?://|[{}]" +
        @"|Error t[eé]cnico|Object reference not set|Value cannot be null|duplicate key value|violates" +
        @"| at TravelApi|\.cs:|Parameter '" +
        @"|Sequence contains|cannot be tracked|entity type|The instance of|An error occurred while" +
        @"|inner exception|IExecutionStrategy|DbUpdate|ExecuteSql|Npgsql" +
        @"|MonCotiz|MonId|FchCotiz|CbteFch|CbteTipo|CbtesAsoc|CbteDesde|CbteHasta|PtoVta" +
        @"|ImpTotal|ImpNeto|ImpIVA|ImpTrib|AlicIva|CanMisMonExt|DocTipo|DocNro|FchServ" +
        @"|WSFEv1|WSAA|FECAE|homologa)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// <c>true</c> si el mensaje parece ruido tecnico (ARCA XML/SOAP, excepcion/stack .NET, error EF/Npgsql).
    /// Un mensaje de negocio en espanol limpio devuelve <c>false</c>. Null/vacio -&gt; <c>false</c>.
    /// </summary>
    public static bool IsLikelyTechnical(string? message)
        => !string.IsNullOrWhiteSpace(message) && TechnicalNoise.IsMatch(message);

    /// <summary>
    /// Sanea el motivo de error del organismo para mostrarlo al vendedor: un rechazo en texto plano y
    /// legible se muestra tal cual (acotado a 300); el ruido tecnico se reemplaza por
    /// <see cref="GenericArcaMessage"/>. Null/vacio -&gt; <c>null</c> (no hay motivo que mostrar).
    /// </summary>
    public static string? SanitizeArcaError(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();
        if (IsLikelyTechnical(trimmed)) return GenericArcaMessage;
        return trimmed.Length > 300 ? trimmed[..300] : trimmed;
    }
}
