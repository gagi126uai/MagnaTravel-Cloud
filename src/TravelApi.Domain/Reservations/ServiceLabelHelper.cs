namespace TravelApi.Domain.Reservations;

/// <summary>
/// Fuente UNICA para armar etiquetas del tipo "Hotel {nombre}" / "Asistencia {plan}" que se usan en
/// mensajes de error, auditoria y sugerencias de factura en toda la app (BookingService, ReservaService,
/// InvoiceSuggestedItemsBuilder). Existe porque un hotel real suele llamarse "Hotel Sheraton": anteponer
/// el prefijo sin mirar el nombre daba renglones duplicados como "Hotel Hotel Sheraton" en la factura y
/// en la ficha (hallazgo #8, barrido T5, 2026-07-24). Antes esta misma logica estaba copiada a mano en 7
/// lugares distintos; ahora es UNA sola funcion pura, asi que si el criterio cambia se corrige en un
/// solo lugar.
/// </summary>
public static class ServiceLabelHelper
{
    /// <summary>
    /// Antepone <paramref name="prefix"/> (ej. "Hotel", "Asistencia") a <paramref name="name"/>, salvo
    /// que <paramref name="name"/> YA arranque con ese prefijo (comparacion case-insensitive: "hotel
    /// Sheraton" tambien cuenta como que ya lo tiene, no importa si el nombre viene con mayuscula
    /// distinta a la del prefijo).
    ///
    /// <para>Si <paramref name="name"/> viene vacio/null, se usa <paramref name="fallbackWhenEmpty"/> en
    /// su lugar ANTES de aplicar la misma regla del prefijo — cada caller decide su propio texto para
    /// "no tengo nombre cargado" (ej. "sin nombre", "seguro", o directamente vacio para que el resultado
    /// quede en solo el prefijo).</para>
    /// </summary>
    public static string WithPrefix(string prefix, string? name, string fallbackWhenEmpty)
    {
        var trimmedName = name?.Trim() ?? string.Empty;
        var effectiveName = trimmedName.Length > 0 ? trimmedName : (fallbackWhenEmpty?.Trim() ?? string.Empty);

        if (effectiveName.Length == 0)
            return prefix;

        if (effectiveName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return effectiveName;

        return $"{prefix} {effectiveName}";
    }
}
