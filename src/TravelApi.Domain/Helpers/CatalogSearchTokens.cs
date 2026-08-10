using System.Text;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// Buscador del catálogo, parte "palabra por palabra" (2026-08-10).
///
/// <para><b>Por que existe</b>: el vendedor escribe pedazos sueltos del producto ("sheraton ola",
/// "aereo bue") y espera que el buscador entienda CADA palabra por separado. Buscar el parecido del
/// texto ENTERO no alcanza: "sheraton" contra "sheraton buenos aires hotel &amp; convention center" da
/// un parecido bajísimo (el texto largo diluye), aunque para una persona sea obviamente el mismo hotel.
/// Este helper parte la búsqueda en palabras y decide si CADA una aparece en algún lado del producto
/// (nombre, ciudad, operador, tipo).</para>
///
/// <para><b>Por que la similitud vive tambien en C#</b>: la primera pasada la hace Postgres con
/// pg_trgm (rápida, con índice), pero el afinado fino se hace en memoria sobre los pocos candidatos que
/// volvieron. Si C# midiera el parecido con otra fórmula, un producto podría entrar por SQL y quedar
/// descartado en memoria (o al revés) sin ninguna lógica visible. Por eso
/// <see cref="TrigramSimilarity"/> replica la fórmula de <c>similarity()</c> de pg_trgm: los dos lados
/// miden igual.</para>
///
/// <para>Todo lo que entra acá se asume YA normalizado con
/// <see cref="TextNormalizer.NormalizeForCatalog"/> (minúscula, sin tildes, sin espacios de más).</para>
/// </summary>
public static class CatalogSearchTokens
{
    /// <summary>
    /// Palabras de relleno del castellano que no aportan nada para encontrar un producto ("hotel DE
    /// las cataratas"). Si contaran como palabra a buscar, un producto que las tenga por casualidad
    /// parecería "más completo" que el correcto.
    /// </summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "de", "del", "la", "el", "los", "las", "en", "con", "al", "y", "o", "a", "un", "una", "para"
    };

    /// <summary>
    /// Tope de palabras que se toman de la búsqueda. Es un límite de SEGURIDAD, no de gusto: cada
    /// palabra se convierte en un pedazo más de la consulta SQL (con sus parámetros), así que un
    /// pegote de 200 palabras haría una consulta gigante. Con 6 alcanza de sobra para "sheraton
    /// buenos aires doble ola".
    /// </summary>
    public const int MaxTokens = 6;

    /// <summary>Una o dos letras sueltas no son una palabra buscable: matchearían con casi todo.</summary>
    private const int MinTokenLength = 2;

    /// <summary>
    /// Largo mínimo para tolerar errores de tipeo. Con palabras cortas ("bue", "eze") el parecido
    /// difuso confunde demasiado ("bue" se parece a "sue"), así que abajo de 4 letras se exige que la
    /// palabra aparezca tal cual.
    /// </summary>
    private const int MinTokenLengthForTypos = 4;

    /// <summary>
    /// Cuánto tiene que parecerse una palabra escrita a una palabra del producto para aceptarla como
    /// la misma pese al error de tipeo. 0.5 agarra "sheratom" -&gt; "sheraton" (0.63) sin llegar a
    /// juntar palabras distintas de largo parecido.
    /// </summary>
    private const double TypoSimilarityThreshold = 0.5;

    /// <summary>
    /// Parte el texto ya normalizado en las palabras que hay que buscar: saca las de relleno, las de
    /// una sola letra, y corta en <see cref="MaxTokens"/>. Devuelve lista vacía (nunca null) si no
    /// quedó nada buscable (ej. el vendedor escribió solo "de la").
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? normalizedQuery)
    {
        if (string.IsNullOrWhiteSpace(normalizedQuery))
        {
            return Array.Empty<string>();
        }

        var tokens = new List<string>();

        foreach (var word in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length < MinTokenLength) continue;
            if (Stopwords.Contains(word)) continue;
            // Repetir la misma palabra no la hace más importante, y gastaría un lugar del cupo.
            if (tokens.Contains(word, StringComparer.Ordinal)) continue;

            tokens.Add(word);
            if (tokens.Count == MaxTokens) break;
        }

        return tokens;
    }

    /// <summary>
    /// Cuánto se parecen dos textos, con la MISMA cuenta que hace <c>similarity()</c> de pg_trgm:
    /// 0 = nada que ver, 1 = idénticos.
    ///
    /// <para><b>Como funciona</b> (didáctico): cada texto se corta en "trigramas", pedacitos de 3
    /// letras. Antes de cortar, cada palabra se rodea de espacios (dos adelante, uno atrás) para que
    /// el principio y el final también cuenten: "sol" se vuelve "␣␣sol␣" y da los pedacitos
    /// "␣␣s", "␣so", "sol", "ol␣". Después se comparan los dos conjuntos de pedacitos: el parecido es
    /// cuántos comparten dividido cuántos hay en total entre los dos (sin repetir).</para>
    /// </summary>
    public static double TrigramSimilarity(string? left, string? right)
    {
        var leftTrigrams = BuildTrigrams(left);
        var rightTrigrams = BuildTrigrams(right);

        if (leftTrigrams.Count == 0 || rightTrigrams.Count == 0)
        {
            return 0d;
        }

        var shared = 0;
        foreach (var trigram in leftTrigrams)
        {
            if (rightTrigrams.Contains(trigram)) shared++;
        }

        var union = leftTrigrams.Count + rightTrigrams.Count - shared;
        return union == 0 ? 0d : (double)shared / union;
    }

    /// <summary>
    /// ¿Esta palabra buscada aparece en este texto del producto? Vale de dos formas:
    ///   1. tal cual, como pedazo del texto ("sherat" está adentro de "sheraton"); o
    ///   2. parecida a alguna palabra del texto, para tolerar un error de tipeo ("sheratom").
    /// </summary>
    public static bool TokenMatches(string? token, string? haystack)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(haystack))
        {
            return false;
        }

        if (haystack.Contains(token, StringComparison.Ordinal))
        {
            return true;
        }

        if (token.Length < MinTokenLengthForTypos)
        {
            return false;
        }

        foreach (var word in SplitWords(haystack))
        {
            if (TrigramSimilarity(token, word) >= TypoSimilarityThreshold)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Cuántas de las palabras buscadas aparecen en AL MENOS UNO de los textos del producto (nombre,
    /// subtítulo, operadores, tipo). Es la medida de "qué tan completo" es un resultado.
    /// </summary>
    public static int MatchedTokenCount(IReadOnlyList<string> tokens, IEnumerable<string?> haystacks)
        => CountMatchedTokens(tokens, haystacks, requireExactSubstring: false);

    /// <summary>
    /// Igual que <see cref="MatchedTokenCount"/> pero EXIGIENDO que la palabra aparezca tal cual (sin
    /// tolerar errores de tipeo). Se usa para separar "coincidencia impecable" de "coincidencia con
    /// ayuda": lo primero merece más puntaje que lo segundo.
    /// </summary>
    public static int ExactMatchedTokenCount(IReadOnlyList<string> tokens, IEnumerable<string?> haystacks)
        => CountMatchedTokens(tokens, haystacks, requireExactSubstring: true);

    private static int CountMatchedTokens(
        IReadOnlyList<string> tokens, IEnumerable<string?> haystacks, bool requireExactSubstring)
    {
        if (tokens.Count == 0)
        {
            return 0;
        }

        var usableHaystacks = new List<string>();
        foreach (var haystack in haystacks)
        {
            if (!string.IsNullOrWhiteSpace(haystack)) usableHaystacks.Add(haystack!);
        }

        var matched = 0;
        foreach (var token in tokens)
        {
            foreach (var haystack in usableHaystacks)
            {
                var hit = requireExactSubstring
                    ? haystack.Contains(token, StringComparison.Ordinal)
                    : TokenMatches(token, haystack);
                if (hit)
                {
                    matched++;
                    break;
                }
            }
        }

        return matched;
    }

    /// <summary>
    /// Los pedacitos de 3 letras del texto, sin repetir. Igual que pg_trgm: se corta por palabra
    /// (todo lo que no sea letra o número separa) y cada palabra va rodeada de espacios.
    /// </summary>
    private static HashSet<string> BuildTrigrams(string? text)
    {
        var trigrams = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return trigrams;
        }

        foreach (var word in SplitWords(text))
        {
            // "  sol " -> "  s", " so", "sol", "ol ". Los espacios del borde hacen que el principio
            // y el final de la palabra pesen, que es lo que hace a esta medida buena para nombres.
            var padded = new StringBuilder(word.Length + 3).Append("  ").Append(word).Append(' ').ToString();
            for (var start = 0; start + 3 <= padded.Length; start++)
            {
                trigrams.Add(padded.Substring(start, 3));
            }
        }

        return trigrams;
    }

    /// <summary>Corta el texto en palabras: todo lo que no sea letra ni número es separador.</summary>
    private static IReadOnlyList<string> SplitWords(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                current.Append(character);
                continue;
            }

            if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
