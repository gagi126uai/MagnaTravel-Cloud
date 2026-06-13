namespace TravelApi.Domain.Helpers;

/// <summary>
/// Normalizador UNICO de telefonos para comparar/deduplicar (CRM de leads, 2026-06-12).
///
/// <para><b>Por que existe</b>: antes habia DOS reglas distintas dando vueltas. El webhook de
/// WhatsApp solo sacaba '+' y espacios; la conversion a cliente sacaba ademas guiones y parentesis.
/// El mismo telefono escrito de dos formas ("+54 9 11 1234-5678" vs "5491112345678") podia NO
/// matchear en un lado y SI en el otro, generando clientes y leads duplicados. Centralizar la
/// regla en un solo lugar evita ese descalce.</para>
///
/// <para><b>Regla de oro</b>: normalizar SIEMPRE los dos lados de la comparacion (el telefono
/// guardado y el que llega). Si solo se normaliza uno, la comparacion sigue fallando.</para>
///
/// La normalizacion es solo para COMPARAR/deduplicar; el telefono original (con su formato
/// elegido por el usuario) se sigue guardando tal cual en la entidad.
/// </summary>
public static class PhoneNormalizer
{
    /// <summary>
    /// Deja SOLO los digitos del telefono: saca '+', espacios, guiones, parentesis y cualquier
    /// otro caracter que no sea un numero. "+54 9 11 1234-5678" -> "5491112345678".
    ///
    /// Null, vacio o solo-espacios devuelven "" (string vacio), nunca null, para que el caller
    /// no tenga que chequear null antes de comparar.
    /// </summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        // Recorremos caracter por caracter y nos quedamos solo con los digitos. Es deliberadamente
        // simple (sin regex) para que cualquiera del equipo lea exactamente que entra y que sale.
        var digitsOnly = new System.Text.StringBuilder(raw.Length);
        foreach (var character in raw)
        {
            if (char.IsDigit(character))
            {
                digitsOnly.Append(character);
            }
        }

        return digitsOnly.ToString();
    }
}
