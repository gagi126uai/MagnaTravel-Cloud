using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// Lee un numero que el modelo puede haber escrito como numero (<c>48</c>) o como texto
/// (<c>"48"</c>, <c>"48,50"</c>, <c>"1.250"</c>).
///
/// <para><b>Por que existe</b>: la deserializacion de las respuestas del modelo es ESTRICTA a
/// proposito, y eso esta bien — asi una respuesta inventada se descarta en vez de colarse. Pero los
/// modelos mandan numeros entre comillas todo el tiempo, y con el lector de siempre eso hacia fallar
/// el objeto ENTERO: se perdian tambien el producto, el operador y las fechas por culpa de dos
/// comillas. Este lector arregla solo ese caso puntual; el resto del contrato sigue igual de estricto.</para>
///
/// <para><b>Nunca lanza</b>: si el texto no es un numero entendible, devuelve <c>null</c> (= "ese dato
/// no vino"). Lanzar seria volver al problema que este lector viene a resolver.</para>
/// </summary>
public sealed class FlexibleDecimalJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var number) ? number : null;

            case JsonTokenType.String:
                return ParseText(reader.GetString());

            default:
                // Cualquier otra cosa (un objeto, una lista) no es un numero: se trata como "no vino".
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        // Nosotros nunca le mandamos este objeto al modelo, pero el contrato del convertidor lo pide.
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }

    /// <summary>
    /// Interpreta el numero escrito como texto. Se prueba primero con punto decimal (formato de la
    /// mayoria de los modelos) y despues con coma decimal (como se escribe en Argentina).
    /// </summary>
    private static decimal? ParseText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var cleaned = raw.Trim().Replace(" ", string.Empty, StringComparison.Ordinal);
        if (cleaned.Length == 0) return null;

        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var withDot))
        {
            return withDot;
        }

        var esAr = CultureInfo.GetCultureInfo("es-AR");
        if (decimal.TryParse(cleaned, NumberStyles.Number, esAr, out var withComma))
        {
            return withComma;
        }

        return null;
    }
}
