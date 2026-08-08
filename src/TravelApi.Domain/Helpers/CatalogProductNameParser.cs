namespace TravelApi.Domain.Helpers;

/// <summary>
/// Separa el nombre de un producto del tarifario en "nombre real" + "habitacion que quedo metida adentro"
/// (spec firmada 2026-08-07, M-16).
///
/// <para><b>De donde viene el problema</b>: nuestro PROPIO formulario largo armaba el nombre del producto
/// pegandole la habitacion atras con un guion — "Sheraton Iguazú - Doble Superior". Resultado: el mismo
/// hotel aparece tres veces en el tarifario (una por habitacion) y ninguna de las tres recuerda el precio
/// de las otras. Este helper detecta ese caso para poder devolver el hotel a UN solo producto y convertir
/// el pedazo del nombre en la habitacion que siempre debio ser.</para>
///
/// <para><b>Es conservador a proposito</b>: solo separa cuando lo que quedo despues del guion se PARECE a
/// una habitacion de verdad (empieza por una capacidad conocida: simple, doble, triple...). Un hotel que se
/// llame "Costa - Playa Grande" NO se toca: en la duda, no se rompe nada.</para>
/// </summary>
public static class CatalogProductNameParser
{
    /// <summary>Separadores que uso el formulario viejo entre el nombre y la habitacion.</summary>
    private static readonly string[] Separators = { " - ", " – ", " — " };

    /// <summary>
    /// Resultado de mirar un nombre: si tiene una habitacion escondida, devuelve el nombre limpio y la
    /// variante; si no, devuelve el nombre tal cual y variante vacia.
    /// </summary>
    public readonly record struct ParsedName(
        string CleanName,
        string VariantKey,
        string VariantLabel,
        bool HadVariantInsideTheName);

    /// <summary>
    /// Mira el nombre de un producto de HOTEL y separa la habitacion si la tiene metida adentro.
    /// El <paramref name="mealPlan"/> del producto (si lo tiene cargado) entra en la variante rescatada,
    /// para no perder el regimen.
    /// </summary>
    public static ParsedName ParseHotelName(string? productName, string? mealPlan = null)
    {
        var name = (productName ?? string.Empty).Trim();
        if (name.Length == 0) return new ParsedName(string.Empty, string.Empty, string.Empty, false);

        foreach (var separator in Separators)
        {
            var cut = name.LastIndexOf(separator, StringComparison.Ordinal);
            if (cut <= 0) continue;

            var head = name[..cut].Trim();
            var tail = name[(cut + separator.Length)..].Trim();
            if (head.Length == 0 || tail.Length == 0) continue;

            if (!LooksLikeARoom(tail, out var roomWord, out var fineName)) continue;

            var variant = CatalogVariant.ForHotel(roomWord, mealPlan, fineName);
            if (variant.Key.Length == 0) continue;

            return new ParsedName(head, variant.Key, variant.Label, true);
        }

        return new ParsedName(name, string.Empty, string.Empty, false);
    }

    /// <summary>
    /// ¿Ese pedazo de texto parece una habitacion? Lo es cuando su PRIMERA palabra es una capacidad
    /// conocida ("Doble", "Triple", "Suite"...). Lo que sigue es el nombre fino ("Superior").
    /// </summary>
    private static bool LooksLikeARoom(string text, out string roomWord, out string? fineName)
    {
        roomWord = string.Empty;
        fineName = null;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return false;

        var firstWordKey = CatalogVariant.NormalizeRoomType(words[0]);
        if (!KnownRoomWords.Contains(firstWordKey)) return false;

        roomWord = words[0];
        if (words.Length > 1) fineName = string.Join(' ', words[1..]);
        return true;
    }

    /// <summary>Capacidades que reconocemos como "esto es una habitacion, no parte del nombre del hotel".</summary>
    private static readonly HashSet<string> KnownRoomWords = new(StringComparer.Ordinal)
    {
        "simple", "doble", "twin", "triple", "cuadruple", "familiar", "suite"
    };
}
