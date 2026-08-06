namespace TravelApi.Domain.Entities;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): libreta historica de cotizaciones.
/// Un registro por moneda + fecha + fuente + entorno. La llena el job diario
/// <c>ExchangeRateSyncJob</c> preguntandole a ARCA (<c>FEParamGetCotizacion</c>), con
/// respaldo del scraper de Banco Nacion cuando ARCA falla.
///
/// <para><b>Fila INMUTABLE una vez escrita (regla F-6, "nada se borra, se tacha")</b>: el upsert
/// del job es <c>ON CONFLICT DO NOTHING</c>, nunca <c>DO UPDATE</c>. Si algun dia hay que corregir
/// un valor mal traido, el procedimiento es insertar una fila NUEVA con el valor correcto y setear
/// <see cref="SupersededByQuoteId"/> de la fila vieja apuntando a ella — jamas un <c>UPDATE</c>
/// sobre <see cref="Rate"/> ni un <c>DELETE</c>. Asi un comprobante que cito la fila vieja siempre
/// puede explicar de donde salio su numero, aunque ese numero ya este corregido.</para>
/// </summary>
public class ExchangeRateQuote
{
    public int Id { get; set; }

    /// <summary>Codigo ISO 4217 ("USD"). NO es el codigo de moneda de ARCA ("DOL") — ese mapeo
    /// vive en <see cref="TravelApi.Domain.Helpers.ArcaCurrencyMapper"/> y se aplica al leer/escribir
    /// esta tabla, no al guardarla.</summary>
    public string Currency { get; set; } = string.Empty;

    /// <summary>
    /// La fecha que PEDIMOS (la que arma el job o la que pide un usuario al resolver). Puede diferir
    /// de <see cref="ArcaFchCotiz"/>: un domingo pedimos "domingo" pero ARCA contesta con el valor
    /// vigente del viernes. El unico indice de la tabla va sobre esta columna (no sobre ArcaFchCotiz),
    /// asi sabado/domingo/lunes pueden ser 3 filas distintas apuntando al mismo dato real de ARCA —
    /// es correcto y mantiene el upsert del job idempotente dia a dia.
    /// </summary>
    public DateOnly QuoteDate { get; set; }

    public ExchangeRateSource Source { get; set; }

    public decimal Rate { get; set; }

    /// <summary>
    /// Origen TECNICO real del dato (no confundir con <see cref="Source"/>, que es la clasificacion
    /// fiscal del enum): <c>"ARCA_WSFEv1"</c> cuando lo trajo <c>FEParamGetCotizacion</c>, o
    /// <c>"BNA_Scraper"</c> cuando el job cayo al respaldo de Banco Nacion.
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    /// <summary>Momento (UTC) en que el job efectivamente consulto la fuente y grabo esta fila.</summary>
    public DateTime FetchedAt { get; set; }

    /// <summary>
    /// La fecha que CONTESTO ARCA (<c>FchCotiz</c> de la respuesta de <c>FEParamGetCotizacion</c>).
    /// NULL para filas que no vienen de ARCA (ej. el respaldo BNA_Scraper, que no tiene ese campo).
    /// Es el dato que defiende el numero ante una inspeccion: el que pedimos puede no coincidir con
    /// el que ARCA realmente publico para ese dia.
    /// </summary>
    public DateOnly? ArcaFchCotiz { get; set; }

    /// <summary>
    /// Entorno de ARCA (<c>AfipSettings.IsProduction</c>) del que salio el dato. El sistema hoy factura
    /// contra homologacion; sin esta columna, o la libreta de homologacion queda siempre vacia, o un
    /// numero de juguete de homologacion termina citado en un comprobante real.
    ///
    /// <para><b>Solo tiene sentido fiscal para <see cref="ExchangeRateSource.AfipOficial"/></b> (ADR-011,
    /// enmienda 2026-08-05): para esa fuente, el resolver que usa la pantalla de FACTURAR sigue
    /// exigiendo que coincida con el <c>IsProduction</c> vigente (facturar en homologacion necesita el
    /// numero de juguete que ARCA va a validar — RG error 10240 si no coincide). Para las demas fuentes
    /// (<c>BNA_*</c>, <see cref="ExchangeRateSource.OficialPorApi"/>) esta columna es vestigial: son
    /// datos reales que no dependen de contra que ambiente de ARCA esta corriendo el sistema, y el
    /// modo "solo datos reales" del resolver (lo usa el dashboard) las sirve sin importar su valor.</para>
    /// </summary>
    public bool IsProductionSource { get; set; }

    /// <summary>
    /// Correccion por reemplazo (regla F-6): si esta fila resulto estar mal, apunta a la fila NUEVA que
    /// la corrige. NULL = fila vigente. El resolver ignora toda fila con este campo poblado.
    /// </summary>
    public int? SupersededByQuoteId { get; set; }
}
