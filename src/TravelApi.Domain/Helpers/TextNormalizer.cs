using System.Globalization;
using System.Text;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Pieza C del "tarifario que se llena solo" (2026-05-30): normaliza texto libre
/// para poder COMPARARLO sin que detalles cosmeticos cuenten como diferencia.
///
/// **Por que existe**: cuando el motor de deteccion de duplicados compara dos
/// tarifas, "Sheratón Buenos Aires" y "sheraton  buenos aires" tienen que dar
/// IGUAL. Si comparamos los strings crudos, el acento, las mayusculas y los
/// espacios de mas los harian parecer hoteles distintos y cargariamos el mismo
/// precio dos veces.
///
/// La regla de oro: aplicar <see cref="NormalizeForMatch"/> a AMBOS lados de la
/// comparacion (al texto guardado y al texto que viene del usuario). Si solo se
/// normaliza un lado, la comparacion sigue fallando.
///
/// **Importante**: esto NO se usa para mostrarle texto al usuario ni para
/// guardar en la base. Es solo para comparar. El texto original (con acentos y
/// mayusculas) se conserva tal cual en la entidad Rate.
/// </summary>
public static class TextNormalizer
{
    /// <summary>
    /// Devuelve una version "lavada" del texto, lista para comparar:
    ///   1. recorta espacios al principio y al final (<c>Trim</c>);
    ///   2. pasa todo a minuscula (<c>ToLowerInvariant</c>);
    ///   3. saca acentos/tildes ("á" -> "a", "ñ" queda "n");
    ///   4. colapsa cualquier secuencia de espacios/tabs en un solo espacio.
    ///
    /// Null, vacio o solo-espacios devuelven "" (string vacio), nunca null,
    /// para que el caller no tenga que chequear null antes de comparar.
    /// </summary>
    public static string NormalizeForMatch(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        // Orden de pasos:
        //  - Trim primero saca el ruido de los bordes.
        //  - ToLowerInvariant nos da case-insensitive sin depender de la cultura
        //    del servidor (Invariant evita sorpresas con el "turkish i", etc.).
        //  - RemoveDiacritics saca tildes; lo hacemos despues del lower para
        //    trabajar siempre sobre el mismo caso.
        var lowered = raw.Trim().ToLowerInvariant();
        var withoutAccents = RemoveDiacritics(lowered);

        return CollapseWhitespace(withoutAccents);
    }

    /// <summary>
    /// ADR-017 F1.1 (catalogo find-or-create, 2026-06-05): version del normalizador para el
    /// buscador del CATALOGO de productos (el campo <c>Rate.SearchName</c>).
    ///
    /// <para><b>Por que existe y no reusamos <see cref="NormalizeForMatch"/> a secas</b>: el catalogo
    /// agrupa "el producto" (ej. un hotel) escrito de mil formas distintas por vendedores apurados.
    /// Ademas de lo que ya hace <c>NormalizeForMatch</c> (minuscula, sin tildes, sin espacios de mas),
    /// colapsamos la puntuacion repetida ("hotel--maitei!!" -> "hotel-maitei!") para que esos detalles
    /// no partan el mismo producto en dos. Se agrega como metodo NUEVO (no se toca
    /// <c>NormalizeForMatch</c>) para no cambiarle la semantica al detector de duplicados del tarifario,
    /// que ya esta testeado y en uso.</para>
    ///
    /// <para><b>Regla de oro (igual que NormalizeForMatch)</b>: es la funcion AUTORITATIVA que usan
    /// TANTO el backfill de la migracion COMO la escritura de la app. El backfill SQL des-acentua
    /// best-effort solo el set español; cualquier residuo (alfabetos raros) se corrige solo en la
    /// primera escritura de la app sobre esa fila y el matching es difuso, asi que tolera residuos.</para>
    ///
    /// Null, vacio o solo-espacios devuelven "" (nunca null), igual que <see cref="NormalizeForMatch"/>.
    /// </summary>
    public static string NormalizeForCatalog(string? raw)
    {
        // Partimos de la base ya probada: minuscula + trim + sin tildes + espacios colapsados.
        var baseNormalized = NormalizeForMatch(raw);

        // Encima colapsamos corridas del MISMO signo de puntuacion ("a--b" -> "a-b", "x!!" -> "x!").
        return CollapseRepeatedPunctuation(baseNormalized);
    }

    /// <summary>
    /// Colapsa secuencias del mismo caracter de puntuacion/simbolo en una sola aparicion. Las letras,
    /// los digitos y los espacios NO se tocan (los espacios ya los colapso <see cref="NormalizeForMatch"/>).
    /// Ejemplo: "san---martin..." -> "san-martin."
    /// </summary>
    private static string CollapseRepeatedPunctuation(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousChar = '\0';

        foreach (var character in text)
        {
            // Es "puntuacion" todo lo que no sea letra, digito ni espacio. Si este caracter es
            // puntuacion Y es igual al que acabamos de escribir, lo salteamos (colapso).
            var isPunctuation = !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character);
            if (isPunctuation && character == previousChar)
            {
                continue;
            }

            builder.Append(character);
            previousChar = character;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Saca tildes/acentos preservando la letra base ("sheratón" -> "sheraton").
    ///
    /// **Como funciona** (didactico): Unicode permite escribir "á" de dos formas:
    /// como un caracter unico (NFC) o como "a" + un acento combinado (NFD).
    /// <c>Normalize(FormD)</c> separa la letra del acento; despues filtramos los
    /// <see cref="UnicodeCategory.NonSpacingMark"/> (los acentos que se dibujan
    /// encima de otra letra) y queda solo la letra base. Volvemos a FormC para
    /// dejar el string en su forma "normal" precompuesta.
    /// </summary>
    private static string RemoveDiacritics(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Reemplaza cualquier corrida de espacios en blanco (espacios, tabs, saltos
    /// de linea) por un unico espacio. Asi "buenos   aires" y "buenos aires"
    /// terminan iguales.
    /// </summary>
    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var previousWasWhitespace = false;

        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                // Solo escribimos un espacio si el anterior NO era espacio.
                // Asi varios espacios seguidos se vuelven uno solo.
                if (!previousWasWhitespace)
                {
                    builder.Append(' ');
                }
                previousWasWhitespace = true;
            }
            else
            {
                builder.Append(character);
                previousWasWhitespace = false;
            }
        }

        // El Trim original ya saco bordes, pero por las dudas el colapso pudo
        // dejar un espacio al final si el texto terminaba en whitespace interno.
        return builder.ToString().Trim();
    }
}
