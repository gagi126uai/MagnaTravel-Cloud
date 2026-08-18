using System.Text.RegularExpressions;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Data-exposure (2026-08-18, bloqueante de <c>data-exposure-reviewer</c> sobre la tanda que expuso
/// <c>Invoice.AnnulmentReason</c> al frontend): algunos flujos INTERNOS arman ese motivo automaticamente
/// pegandole un prefijo tecnico al texto que el usuario tipeo, para que quede trazable en el log/auditoria
/// de quien disparo la anulacion. Ejemplos reales guardados hoy en la base:
/// <list type="bullet">
/// <item><c>"BC override 3f8a1c22-...: cliente pidio cambio de fecha"</c> (anulacion disparada desde una
/// cancelacion de reserva, con el GUID interno del <c>ApprovalRequest</c> que la autorizo)</item>
/// <item><c>"BC admin self-authorized override: se vencio el plazo del cliente"</c></item>
/// <item><c>"BC cancellation: motivo cargado en la reserva"</c></item>
/// <item><c>"BC retry-credit-notes: motivo cargado en la reserva"</c></item>
/// <item><c>"FC1.3 manual review approved: aprobado por el supervisor"</c></item>
/// <item><c>"FC1.3 F2 partial NC: aprobado por el supervisor"</c></item>
/// </list>
/// Esos prefijos (GUID interno, "BC", "FC1.3") son jerga de programador: un agente de viajes no sabe que es
/// un "BC" ni le sirve el identificador tecnico. Este helper LIMPIA la parte visible para pantalla sin tocar
/// como se guarda (la fila real en <c>Invoice.AnnulmentReason</c> sigue con el prefijo completo, que es util
/// para debug/soporte) — solo cambia lo que la API devuelve.
///
/// <para><b>Filosofia: allowlist de prefijos conocidos, no blocklist de jerga.</b> Solo reconocemos los
/// prefijos que ESTOS flujos internos realmente generan (<c>BC...</c> / <c>FC1.3...</c> seguido de ": ").
/// Un motivo tipeado a mano por un usuario nunca empieza asi en español normal, asi que no hay falsos
/// positivos: el texto de negocio comun pasa intacto.</para>
/// </summary>
public static class AnnulmentReasonUiSanitizer
{
    // Prefijo tecnico conocido + ": " + el texto real que importa. Case-sensitive a proposito: "BC" y
    // "FC1.3" son siglas internas del proyecto (Booking Cancellation / Fase-Cancelacion 1.3) que SIEMPRE
    // se generan en mayusculas desde el codigo — un motivo tipeado por un usuario que arranque distinto
    // (minuscula, otra palabra) nunca cae en este patron por accidente.
    private static readonly Regex TechnicalPrefix = new(
        @"^(BC|FC1\.3)\b[^:]*:\s*(.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Devuelve el motivo de anulacion listo para mostrar en pantalla: si <paramref name="raw"/> tiene uno
    /// de los prefijos tecnicos conocidos, devuelve SOLO la parte que escribio el usuario (sin el prefijo).
    /// Si esa parte queda vacia (el flujo interno no tenia texto de usuario para pegar), devuelve
    /// <c>null</c> — mejor no mostrar nada que mostrar jerga de programador. Un motivo sin prefijo tecnico
    /// (el caso normal: el usuario tipeo el motivo directo desde la pantalla de anular factura) se devuelve
    /// tal cual, sin tocar.
    /// </summary>
    public static string? ForDisplay(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var match = TechnicalPrefix.Match(raw);
        if (!match.Success) return raw;

        var userText = match.Groups[2].Value.Trim();
        return userText.Length > 0 ? userText : null;
    }
}
