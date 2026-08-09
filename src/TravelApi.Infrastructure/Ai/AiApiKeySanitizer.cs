using System;

namespace TravelApi.Infrastructure.Ai;

/// <summary>
/// Limpia la clave del proveedor de IA antes de que se use en una cabecera HTTP.
///
/// <para><b>Por que existe como pieza aparte</b>: la misma limpieza hacia falta en tres lugares
/// (guardar la configuracion, probar la conexion y hablar con el modelo) y estaba escrita dos veces
/// con reglas apenas distintas. En el tercero — el que hace las llamadas de verdad — directamente no
/// estaba, asi que una clave con un salto de linea pegado (lo mas comun al copiar y pegar) explotaba
/// al armar la cabecera <c>Authorization</c>.</para>
///
/// <para><b>Que saca y por que</b>: saltos de linea, tabulaciones y espacios de los bordes. Una
/// cabecera HTTP con un salto de linea adentro es un pedido invalido — y, en el peor caso, la forma
/// clasica de colar cabeceras de mas (HTTP header injection). Ademas, una clave BUENA con un salto
/// pegado atras fallaria con un rechazo del proveedor imposible de entender para el dueño.</para>
/// </summary>
internal static class AiApiKeySanitizer
{
    /// <summary>
    /// Devuelve la clave lista para usar, o <c>null</c> si lo que quedo esta vacio (asi el llamador
    /// trata "clave en blanco" y "clave que era solo espacios" exactamente igual).
    /// </summary>
    public static string? Sanitize(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var cleaned = apiKey
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Replace("\t", string.Empty, StringComparison.Ordinal)
            .Trim();

        return string.IsNullOrEmpty(cleaned) ? null : cleaned;
    }
}
