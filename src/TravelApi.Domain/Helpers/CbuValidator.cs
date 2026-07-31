namespace TravelApi.Domain.Helpers;

/// <summary>
/// Obra "cada campo acepta solo lo que va en ese campo" (firmada por el dueño, 2026-07-31), TANDA 1.
///
/// <para><b>Por que existe</b>: el CBU es a donde VA LA PLATA. Antes solo se chequeaba que fueran 22
/// digitos, asi que un numero con un digito mal tipeado se guardaba igual y quedaba impreso en el
/// recibo/instructivo de transferencia que recibe el cliente. Una transferencia a un CBU mal tipeado o
/// no existe (rebota dias despues) o —el caso feo— cae en la cuenta de otra persona.</para>
///
/// <para><b>Como se valida (algoritmo del BCRA, explicado)</b>: el CBU tiene 22 digitos partidos en dos
/// bloques, y cada bloque termina en un digito verificador que se CALCULA a partir de los anteriores.
/// Es la misma idea que el digito verificador del CUIT: no prueba que la cuenta exista, pero atrapa
/// practicamente cualquier error de tipeo (un digito cambiado, dos digitos permutados).</para>
///
/// <list type="number">
///   <item><b>Bloque 1 — digitos 1 a 8</b>: los primeros 3 son el codigo del banco, los 4 siguientes la
///     sucursal, y el 8vo es el verificador. Se multiplica cada uno de los 7 primeros por los
///     ponderadores <c>7, 1, 3, 9, 7, 1, 3</c>, se suman los resultados, se toma el ULTIMO digito de esa
///     suma y el verificador esperado es <c>(10 - ultimoDigito) % 10</c>.</item>
///   <item><b>Bloque 2 — digitos 9 a 22</b>: los primeros 13 son el numero de cuenta y el 22do es el
///     verificador. Misma cuenta, con los ponderadores
///     <c>3, 9, 7, 1, 3, 9, 7, 1, 3, 9, 7, 1, 3</c>.</item>
/// </list>
///
/// <para><b>Por que el <c>% 10</c> final</b>: si la suma termina en 0, "10 - 0" daria 10, que no es un
/// digito. El resto de dividir por 10 lo devuelve a 0, que es el verificador correcto en ese caso.</para>
///
/// <para><b>Ejemplo para seguir a mano</b>: en el CBU <c>2850590940090418135201</c>, el bloque 1 es
/// <c>28505909</c>: 2×7 + 8×1 + 5×3 + 0×9 + 5×7 + 9×1 + 0×3 = 81 → ultimo digito 1 → verificador
/// (10-1)%10 = 9, que es justo el 8vo digito. El bloque 2 cierra igual con su propia cuenta.</para>
/// </summary>
public static class CbuValidator
{
    /// <summary>
    /// Mensaje criollo unico para el usuario final cuando el CBU no cierra. Centralizado aca para que el
    /// texto sea el mismo desde cualquier pantalla que cargue una cuenta bancaria.
    /// </summary>
    public const string InvalidCbuMessage = "Ese CBU no es válido. Revisá los números.";

    /// <summary>Largo fijo del CBU: 8 del primer bloque + 14 del segundo.</summary>
    private const int CbuLength = 22;

    /// <summary>Ponderadores del bloque 1 (banco + sucursal), aplicados a los digitos 1 a 7.</summary>
    private static readonly int[] FirstBlockWeights = { 7, 1, 3, 9, 7, 1, 3 };

    /// <summary>Ponderadores del bloque 2 (numero de cuenta), aplicados a los digitos 9 a 21.</summary>
    private static readonly int[] SecondBlockWeights = { 3, 9, 7, 1, 3, 9, 7, 1, 3, 9, 7, 1, 3 };

    /// <summary>
    /// True si <paramref name="rawValue"/> es un CBU de 22 digitos con los DOS digitos verificadores
    /// correctos. Un valor vacio o nulo se considera VALIDO a proposito: el CBU es opcional (una cuenta
    /// puede cargarse solo con alias) — este helper solo bloquea un CBU MAL TIPEADO.
    ///
    /// <para>Tolera guiones, puntos y espacios (la gente pega el CBU del homebanking con formato): se
    /// limpian antes de contar los digitos, igual que hace <see cref="CuitValidator"/> con el CUIT.</para>
    /// </summary>
    public static bool IsValidOrEmpty(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        // Se reusa el mismo limpiador que el CUIT para no tener dos formas distintas de "sacarle el
        // formato" a un numero cargado a mano.
        var clean = ArcaReceptorResolver.CleanNumericString(rawValue);

        if (clean.Length != CbuLength)
        {
            return false;
        }

        foreach (var character in clean)
        {
            if (!char.IsDigit(character))
            {
                return false;
            }
        }

        bool firstBlockIsValid = CheckDigitMatches(
            block: clean.Substring(0, 8),
            weights: FirstBlockWeights);

        bool secondBlockIsValid = CheckDigitMatches(
            block: clean.Substring(8, 14),
            weights: SecondBlockWeights);

        return firstBlockIsValid && secondBlockIsValid;
    }

    /// <summary>
    /// Calcula el digito verificador esperado de un bloque (todos sus digitos MENOS el ultimo, ponderados)
    /// y lo compara con el ultimo digito que trae el bloque.
    /// </summary>
    private static bool CheckDigitMatches(string block, int[] weights)
    {
        int weightedSum = 0;
        for (int position = 0; position < weights.Length; position++)
        {
            int digit = block[position] - '0';
            weightedSum += digit * weights[position];
        }

        int expectedCheckDigit = (10 - (weightedSum % 10)) % 10;
        int actualCheckDigit = block[^1] - '0';

        return expectedCheckDigit == actualCheckDigit;
    }
}
