namespace TravelApi.Domain.Reservations;

/// <summary>
/// ADR-022 §4.13 (T3, Q3 — bloqueo duro): un movimiento manual de caja NO puede impersonar un hecho que
/// tiene su PROPIA puerta unica. Cobro de cliente (<c>Payment</c>) y pago a proveedor (<c>SupplierPayment</c>)
/// tienen su servicio dedicado; un manual con una categoria que nombre uno de esos hechos crearia una
/// SEGUNDA puerta para la misma plata (doble conteo en el Libro de Caja). Por eso esas categorias se
/// RECHAZAN en el alta/edicion manual. Cualquier otra categoria (gastos de oficina, ajustes de caja, etc.)
/// es libre.
///
/// <para><b>Por que una lista y no un enum</b>: el campo Category es texto libre en el front, asi que no hay
/// un conjunto cerrado. La regla fija es "si la categoria nombra un cobro de cliente o un pago a proveedor,
/// se rechaza"; esta lista materializa las etiquetas conocidas que lo nombran. El match es por texto
/// normalizado (sin distinguir mayusculas/acentos/espacios) para que no se escape por una variante de tipeo.</para>
/// </summary>
public static class ManualCashMovementCategoryRules
{
    /// <summary>
    /// Etiquetas de categoria que duplican una puerta propia y por eso se bloquean. Normalizadas (minuscula,
    /// sin acentos, sin espacios) para comparar contra la categoria del request tambien normalizada.
    /// </summary>
    private static readonly string[] BlockedCategoriesNormalized =
    {
        // Cobro de cliente -> puerta unica: Payment / PaymentService.
        "cobrocliente",
        "cobranza",
        "cobranzacliente",
        "cobro",
        "pagocliente",
        // Pago a proveedor -> puerta unica: SupplierPayment / SupplierService.
        "pagoproveedor",
        "pagoaproveedor",
        "pagooperador",
        "pagoaoperador",
    };

    /// <summary>
    /// True si la categoria nombra un cobro de cliente o un pago a proveedor (debe rechazarse). False si es
    /// una categoria libre (gasto/ajuste). null/vacio devuelve false (la validacion de "categoria obligatoria"
    /// vive aparte en el service).
    /// </summary>
    public static bool IsBlocked(string? category)
    {
        if (string.IsNullOrWhiteSpace(category)) return false;
        var normalized = Normalize(category);
        foreach (var blocked in BlockedCategoriesNormalized)
        {
            if (normalized == blocked) return true;
        }
        return false;
    }

    /// <summary>
    /// Baja a minuscula, saca acentos comunes (a/e/i/o/u con tilde y ñ) y quita espacios, para que
    /// "Pago Proveedor", "pago proveedor" y "Pago a Proveedor" caigan en la misma clave comparable.
    /// </summary>
    private static string Normalize(string value)
    {
        var lowered = value.Trim().ToLowerInvariant();
        var builder = new System.Text.StringBuilder(lowered.Length);
        foreach (var ch in lowered)
        {
            char replacement = ch switch
            {
                'á' => 'a',
                'é' => 'e',
                'í' => 'i',
                'ó' => 'o',
                'ú' => 'u',
                'ñ' => 'n',
                _ => ch,
            };
            // Sacamos espacios para que "pago a proveedor" == "pagoaproveedor".
            if (!char.IsWhiteSpace(replacement))
            {
                builder.Append(replacement);
            }
        }
        return builder.ToString();
    }
}
