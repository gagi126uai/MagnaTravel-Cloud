using System.Text.RegularExpressions;

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
    /// que <paramref name="name"/> YA tenga esa palabra en cualquier parte del texto (no solo al
    /// principio).
    ///
    /// <para><b>Hallazgo H11 (barrido E2E 2026-07-25)</b>: el guard viejo solo miraba si el nombre
    /// EMPEZABA con el prefijo (<c>StartsWith</c>). Un hotel cargado como "PI0724 Hotel B" (el codigo del
    /// paquete de prueba adelante del nombre real) no arranca con "Hotel", asi que el guard viejo
    /// igual anteponia el prefijo y el resultado quedaba "Hotel PI0724 Hotel B" — la palabra repetida en
    /// el medio del texto, mismo problema de fondo que el hallazgo #8 pero en otra posicion. Ahora se
    /// busca la palabra COMPLETA en cualquier lugar del nombre, con limite de palabra (<c>\b</c>) para no
    /// confundir "Hotel" con un prefijo de otra palabra como "Hoteleria" (que NO deberia frenar el
    /// prefijo: "Hoteleria Especial" no es lo mismo que ya decir "Hotel").</para>
    ///
    /// <para>Comparacion case-insensitive: "hotel Sheraton" tambien cuenta como que ya lo tiene, no
    /// importa si el nombre viene con mayuscula distinta a la del prefijo.</para>
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

        // \b...\b = limite de palabra de verdad (no un simple Contains): "Hotel" matchea dentro de
        // "PI0724 Hotel B" pero NO dentro de "Hoteleria Especial" (ahi "Hotel" es solo el prefijo de
        // otra palabra, no la palabra completa).
        var prefixAppearsAsWholeWord = Regex.IsMatch(
            effectiveName,
            $@"\b{Regex.Escape(prefix)}\b",
            RegexOptions.IgnoreCase);

        if (prefixAppearsAsWholeWord)
            return effectiveName;

        return $"{prefix} {effectiveName}";
    }
}
