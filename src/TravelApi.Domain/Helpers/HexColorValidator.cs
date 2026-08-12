using System.Text.RegularExpressions;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "PDF de presupuesto" (2026-08-11/12), TANDA 1: valida el color de la banda del PDF que carga
/// el admin en Configuración de la agencia. Mismo criterio "cada campo acepta solo lo que va en ese
/// campo" que <see cref="CommissionPercentValidator"/> y <see cref="PhoneValidator"/>.
/// </summary>
public static class HexColorValidator
{
    public const string InvalidHexColorMessage =
        "El color tiene que ser un código hexadecimal válido, por ejemplo #1E40AF.";

    // #RRGGBB, exactamente 6 dígitos hexadecimales. NO soportamos la forma corta #RGB ni el canal
    // alfa (#RRGGBBAA): el PDF no usa transparencia, y limitar el formato evita que lleguen valores
    // que el generador de PDF no sepa interpretar.
    private static readonly Regex HexColorPattern = new(@"^#[0-9A-Fa-f]{6}$", RegexOptions.Compiled);

    /// <summary>
    /// Vacío/null es VALIDO a propósito: "sin color cargado" es una configuración real (el PDF usa el
    /// color por defecto de la plantilla). Mismo criterio "vacío pasa" que el resto de los validadores
    /// de esta pantalla.
    /// </summary>
    public static bool IsValidOrEmpty(string? hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor))
        {
            return true;
        }

        return HexColorPattern.IsMatch(hexColor.Trim());
    }
}
