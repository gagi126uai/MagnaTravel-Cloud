using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TravelApi.Domain.Helpers;

/// <summary>
/// ADR-024 (datos reales del receptor en la emision ARCA, 2026-06-12): fuente de verdad UNICA de
/// como se resuelven los datos fiscales del RECEPTOR de un comprobante (DocTipo + DocNro +
/// CondicionIVAReceptorId) a partir del snapshot del cliente.
///
/// <para><b>Por que existe esta clase</b>: antes de ADR-024 el armado de DocTipo/DocNro/condicion
/// IVA vivia inline dentro de <c>AfipService.ProcessInvoiceJob</c>, que POSTea a ARCA y no se puede
/// testear con tests unitarios. Eso dejaba la logica fiscal del receptor SIN blindaje: dos bugs reales
/// (extranjero con pasaporte emitido como DNI argentino; condicion IVA del receptor siempre fijada en
/// Consumidor Final porque nunca se leia <c>Customer.TaxConditionId</c>). Extraer la decision a metodos
/// puros y estaticos permite blindarla con tests que ejercitan EXACTAMENTE el codigo de produccion
/// (mismo patron que <see cref="ArcaCurrencyMapper"/> / BuildMonedaSoapFragment).</para>
///
/// <para><b>Lo que NO hace</b>: no arma el XML SOAP, no toca BD, no llama a ARCA. Solo decide los tres
/// numeros fiscales del receptor. El caller (el job) los inserta en el envelope.</para>
///
/// <para><b>Tablas de codigos</b>: verificadas en ADR-024 §3.1 (tipos de documento, FEParamGetTiposDoc)
/// y §4.1 (CondicionIVAReceptorId, RG 5616/2024). Los codigos pueden cambiar por norma; antes de prender
/// emision real hay que homologar contra el ambiente de testing de ARCA (ADR-024 §10 punto 8).</para>
/// </summary>
public static class ArcaReceptorResolver
{
    // ADR-024 §3.1: codigos ARCA de tipo de documento usados por una agencia retail.
    public const int DocTipoCuit = 80;
    public const int DocTipoCuil = 86;
    public const int DocTipoCdi = 87;
    public const int DocTipoLe = 89;
    public const int DocTipoLc = 90;
    public const int DocTipoCiExtranjera = 91;
    public const int DocTipoPasaporte = 94;
    public const int DocTipoDni = 96;
    public const int DocTipoConsumidorFinalSinIdentificar = 99;

    // ADR-024 §4.1: codigos de CondicionIVAReceptorId (RG 5616).
    public const int CondicionIvaResponsableInscripto = 1;
    public const int CondicionIvaExento = 4;
    public const int CondicionIvaConsumidorFinal = 5;
    public const int CondicionIvaMonotributo = 6;
    public const int CondicionIvaMonotributistaSocial = 13;
    public const int CondicionIvaMonotributoTrabajadorIndependientePromovido = 16;
    public const int CondicionIvaSujetoNoCategorizado = 7;
    public const int CondicionIvaProveedorDelExterior = 8;
    public const int CondicionIvaClienteDelExterior = 9;
    public const int CondicionIvaLiberado19640 = 10;
    public const int CondicionIvaNoAlcanzado = 15;

    /// <summary>
    /// Resultado de resolver el documento del receptor: tipo ARCA + numero.
    /// <paramref name="RequiresFiscalData"/> es true cuando NO se pudo identificar al receptor con un
    /// documento valido y se cayo al fallback "consumidor final sin identificar" (DocTipo=99). El caller
    /// puede usarlo para decidir si bloquea la emision por el tope de monto (ADR-024 §3.4 regla F), tema
    /// que queda fuera de este resolver (necesita el ImpTotal y el tope vigente, a confirmar con contador).
    /// </summary>
    public readonly record struct ReceptorDocument(int DocTipo, long DocNro, bool RequiresFiscalData);

    /// <summary>
    /// ADR-024 §3.4: resuelve (DocTipo, DocNro) del receptor desde los datos del snapshot del cliente.
    /// La precedencia prioriza la identificacion fiscal fuerte (CUIT) por sobre el texto libre de
    /// <paramref name="documentType"/>, porque un CUIT presente es el dato mas confiable para ARCA.
    /// </summary>
    /// <param name="taxId">Customer.TaxId del snapshot (CUIT/CUIL). Puede venir con guiones/puntos.</param>
    /// <param name="documentType">Customer.DocumentType del snapshot (texto libre: "DNI", "Pasaporte", ...).</param>
    /// <param name="documentNumber">Customer.DocumentNumber del snapshot.</param>
    public static ReceptorDocument ResolveDocument(string? taxId, string? documentType, string? documentNumber)
    {
        // Regla A (ADR-024 §3.4): CUIT siempre gana. Si hay TaxId y limpia a 11 digitos con DV valido,
        // el receptor se identifica con DocTipo=80. Un CUIT presente es el identificador fiscal fuerte;
        // ignoramos otro documento que pudiera estar cargado (ASUNCION confirmar con contador, §10.3).
        var cleanTaxId = CleanNumericString(taxId);
        if (!string.IsNullOrEmpty(cleanTaxId) && IsValidCuit(cleanTaxId))
        {
            return new ReceptorDocument(DocTipoCuit, long.Parse(cleanTaxId, CultureInfo.InvariantCulture), RequiresFiscalData: false);
        }

        // Regla B (ADR-024 §3.4): si el DocumentType normaliza a un tipo conocido, mapeamos por tabla §3.2.
        var mappedType = MapDocumentTypeToArca(documentType);
        if (mappedType.HasValue)
        {
            return ResolveForKnownType(mappedType.Value, documentNumber);
        }

        // Regla C (ADR-024 §3.4): DocumentType vacio/desconocido PERO hay numero numerico. NO asumimos
        // DNI ciegamente como hacia el codigo viejo a menos que el numero sea numerico y entre como long.
        // (ASUNCION confirmar con contador, §10.1: default DNI para numero suelto sin tipo.)
        if (string.IsNullOrWhiteSpace(documentType) && TryParseDocNumber(documentNumber, out long looseNumber))
        {
            return new ReceptorDocument(DocTipoDni, looseNumber, RequiresFiscalData: false);
        }

        // Regla D (ADR-024 §3.4): ni TaxId ni DocumentNumber -> consumidor final sin identificar.
        if (string.IsNullOrWhiteSpace(taxId) && string.IsNullOrWhiteSpace(documentNumber))
        {
            return new ReceptorDocument(DocTipoConsumidorFinalSinIdentificar, 0, RequiresFiscalData: false);
        }

        // Regla F (ADR-024 §3.4): tipo desconocido / numero invalido / DV de CUIT malo -> fallback seguro.
        // Emitir como consumidor final sin identificar es el fallback que NO miente sobre la identidad.
        // RequiresFiscalData=true para que el caller pueda evaluar el tope de monto antes de POSTear.
        return new ReceptorDocument(DocTipoConsumidorFinalSinIdentificar, 0, RequiresFiscalData: true);
    }

    /// <summary>
    /// ADR-024 §4.2: resuelve el CondicionIVAReceptorId del receptor. Orden de preferencia:
    /// (1) <paramref name="taxConditionId"/> explicito del snapshot si es un codigo valido de la tabla §4.1;
    /// (2) parseo del texto <paramref name="taxConditionText"/> si el Id viene null (snapshot viejo);
    /// (3) derivacion conservadora desde el <paramref name="docTipo"/> ya resuelto.
    /// Nunca devuelve un codigo fuera de la tabla §4.1: el fallback final es 5 (Consumidor Final), valido
    /// en clase C/B (ADR-024 §4.4). El caller que emita clase A debe verificar coherencia clase-condicion.
    /// </summary>
    /// <param name="taxConditionId">Customer.TaxConditionId del snapshot. null en snapshots viejos.</param>
    /// <param name="taxConditionText">Customer.TaxCondition (texto) del snapshot.</param>
    /// <param name="docTipo">DocTipo ya resuelto por <see cref="ResolveDocument"/>.</param>
    public static int ResolveCondicionIva(int? taxConditionId, string? taxConditionText, int docTipo)
    {
        // Paso 1 (ADR-024 §4.2): dato explicito del cliente, maxima confianza. Solo si es un codigo valido.
        if (taxConditionId.HasValue && IsValidCondicionIvaCode(taxConditionId.Value))
        {
            return taxConditionId.Value;
        }

        // Paso 2 (ADR-024 §4.2): snapshot viejo sin Id. Parseamos el texto si matchea inequivocamente.
        var fromText = TryParseCondicionIvaText(taxConditionText);
        if (fromText.HasValue)
        {
            return fromText.Value;
        }

        // Paso 3/4 (ADR-024 §4.2): derivacion conservadora desde el DocTipo.
        // Persona fisica (DNI/Pasaporte/CI/LE/LC/CUIL) o sin identificar -> Consumidor Final.
        // CUIT sin condicion conocida -> default 5 (tener CUIT NO implica RI ni Monotributo; defaultear a
        // RI/Mono afirmaria una condicion fiscal falsa, que es PEOR; ASUNCION confirmar con contador §10.4).
        return CondicionIvaConsumidorFinal;
    }

    /// <summary>ADR-024 §4.1: true si el codigo pertenece a la tabla verificada de CondicionIVAReceptorId.</summary>
    public static bool IsValidCondicionIvaCode(int code)
    {
        return code == CondicionIvaResponsableInscripto
            || code == CondicionIvaExento
            || code == CondicionIvaConsumidorFinal
            || code == CondicionIvaMonotributo
            || code == CondicionIvaMonotributistaSocial
            || code == CondicionIvaMonotributoTrabajadorIndependientePromovido
            || code == CondicionIvaSujetoNoCategorizado
            || code == CondicionIvaProveedorDelExterior
            || code == CondicionIvaClienteDelExterior
            || code == CondicionIvaLiberado19640
            || code == CondicionIvaNoAlcanzado;
    }

    // ====================================================================================
    // Helpers privados
    // ====================================================================================

    /// <summary>
    /// ADR-024 §3.2: normaliza el texto libre de DocumentType y lo mapea al codigo ARCA. Devuelve null si
    /// el texto esta vacio o no matchea ningun tipo conocido (el caller cae al fallback §3.4).
    /// </summary>
    private static int? MapDocumentTypeToArca(string? documentType)
    {
        var normalized = NormalizeText(documentType);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized switch
        {
            "cuit" => DocTipoCuit,
            "cuil" => DocTipoCuil,
            "cdi" => DocTipoCdi,
            "le" or "libreta de enrolamiento" => DocTipoLe,
            "lc" or "libreta civica" => DocTipoLc,
            "ci extranjera" or "cedula extranjera" or "cedula de identidad" => DocTipoCiExtranjera,
            "pasaporte" or "passport" or "pas" => DocTipoPasaporte,
            "dni" => DocTipoDni,
            _ => (int?)null
        };
    }

    /// <summary>
    /// ADR-024 §3.4 regla B + §3.5: resuelve (DocTipo, DocNro) para un tipo ya mapeado desde DocumentType.
    /// Aplica las validaciones de formato §3.3 y el caso borde critico del pasaporte/CI alfanumericos §3.5.
    /// </summary>
    private static ReceptorDocument ResolveForKnownType(int docTipo, string? documentNumber)
    {
        // Tipos de 11 digitos con DV (CUIT/CUIL/CDI): el numero debe limpiar a 11 digitos. El DV solo se
        // valida estricto para CUIT/CUIL (mismo algoritmo); CDI no tiene DV publico documentado, exigimos
        // 11 digitos. Si no cumple -> fallback §3.4 regla F.
        if (docTipo == DocTipoCuit || docTipo == DocTipoCuil || docTipo == DocTipoCdi)
        {
            var clean = CleanNumericString(documentNumber);
            if (!string.IsNullOrEmpty(clean) && clean.Length == 11 && long.TryParse(clean, out long val))
            {
                bool dvOk = docTipo == DocTipoCdi || IsValidCuit(clean);
                if (dvOk)
                {
                    return new ReceptorDocument(docTipo, val, RequiresFiscalData: false);
                }
            }
            return new ReceptorDocument(DocTipoConsumidorFinalSinIdentificar, 0, RequiresFiscalData: true);
        }

        // ADR-024 §3.5 (RIESGO FISCAL alto): DocNro en WSFE es numerico (long). Un pasaporte/CI extranjera
        // alfanumerico ("AB123456") NO se puede representar como long. Opcion 1 (conservadora, aprobada
        // como default del MVP): tratar como consumidor final sin identificar (99/0). NUNCA mentir la
        // identidad emitiendo un numero inventado. ASUNCION confirmar con contador (§10.2) + homologar.
        if (docTipo == DocTipoPasaporte || docTipo == DocTipoCiExtranjera)
        {
            if (TryParseDocNumber(documentNumber, out long numericPassport))
            {
                // Pasaporte/CI con numero puramente numerico: lo dejamos con su DocTipo real.
                return new ReceptorDocument(docTipo, numericPassport, RequiresFiscalData: false);
            }
            // Alfanumerico -> consumidor final sin identificar.
            return new ReceptorDocument(DocTipoConsumidorFinalSinIdentificar, 0, RequiresFiscalData: true);
        }

        // Tipos numericos restantes (DNI/LE/LC): el numero debe parsear a long > 0.
        if (TryParseDocNumber(documentNumber, out long numeric))
        {
            return new ReceptorDocument(docTipo, numeric, RequiresFiscalData: false);
        }

        // Numero invalido para un tipo numerico -> fallback §3.4 regla F.
        return new ReceptorDocument(DocTipoConsumidorFinalSinIdentificar, 0, RequiresFiscalData: true);
    }

    /// <summary>
    /// ADR-024 §4.2 paso 2: parsea el texto de condicion fiscal a un codigo. Devuelve null si no matchea
    /// inequivocamente (el caller deriva por DocTipo). El orden importa: "inscripto" sin "monotributo"
    /// es RI; si dice "monotributo" gana Monotributo aunque tambien diga "inscripto".
    /// </summary>
    private static int? TryParseCondicionIvaText(string? taxConditionText)
    {
        var normalized = NormalizeText(taxConditionText);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Contains("monotributo") || normalized.Contains("monotributista"))
        {
            return CondicionIvaMonotributo;
        }
        if (normalized.Contains("inscripto"))
        {
            return CondicionIvaResponsableInscripto;
        }
        if (normalized.Contains("exento"))
        {
            return CondicionIvaExento;
        }
        if (normalized.Contains("consumidor"))
        {
            return CondicionIvaConsumidorFinal;
        }

        return null;
    }

    /// <summary>
    /// ADR-024 §3.3: valida un CUIT/CUIL de 11 digitos con su digito verificador (modulo 11). Evita
    /// POSTear a ARCA un CUIT mal tipeado (rechazo 10074/10246). La cadena debe venir ya limpia (solo digitos).
    /// </summary>
    public static bool IsValidCuit(string cleanCuit)
    {
        if (string.IsNullOrEmpty(cleanCuit) || cleanCuit.Length != 11)
        {
            return false;
        }
        if (!cleanCuit.All(char.IsDigit))
        {
            return false;
        }

        // Pesos del algoritmo oficial de CUIT (modulo 11) para los primeros 10 digitos.
        int[] weights = { 5, 4, 3, 2, 7, 6, 5, 4, 3, 2 };
        int sum = 0;
        for (int i = 0; i < 10; i++)
        {
            sum += (cleanCuit[i] - '0') * weights[i];
        }

        int remainder = sum % 11;
        int checkDigit = 11 - remainder;
        if (checkDigit == 11) checkDigit = 0;
        else if (checkDigit == 10) checkDigit = 9; // caso especial del algoritmo

        int actualCheckDigit = cleanCuit[10] - '0';
        return checkDigit == actualCheckDigit;
    }

    /// <summary>Quita guiones, puntos y espacios de una cadena de identificacion (CUIT "20-12345678-9").</summary>
    private static string CleanNumericString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }
        return value.Replace("-", string.Empty).Replace(".", string.Empty).Replace(" ", string.Empty).Trim();
    }

    /// <summary>
    /// Parsea un numero de documento a long > 0. Limpia separadores antes de parsear. Devuelve false si
    /// queda vacio, no es numerico, o es 0/negativo (un DocNro 0 se reserva para consumidor final).
    /// </summary>
    private static bool TryParseDocNumber(string? documentNumber, out long result)
    {
        result = 0;
        var clean = CleanNumericString(documentNumber);
        if (string.IsNullOrEmpty(clean))
        {
            return false;
        }
        if (long.TryParse(clean, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed) && parsed > 0)
        {
            result = parsed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Normaliza texto libre: trim + minusculas + sin acentos + colapsa espacios (ADR-024 §3.2). Asi
    /// "PASAPORTE", "Pasaporte" y " pasaporte " mapean al mismo tipo.
    /// </summary>
    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lowered = value.Trim().ToLowerInvariant();

        // Quitar acentos: descomponer a forma D y descartar las marcas diacriticas.
        var decomposed = lowered.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }
        var withoutAccents = builder.ToString().Normalize(NormalizationForm.FormC);

        // Colapsar espacios multiples a uno solo.
        var parts = withoutAccents.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts);
    }
}
