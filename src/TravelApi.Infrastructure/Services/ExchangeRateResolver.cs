using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): implementacion del resolver. SOLO lee
/// la libreta de cotizaciones (<c>ExchangeRateQuotes</c>) — nunca le pega a ARCA ni a ninguna red
/// externa. Eso lo hace unicamente el job diario (<see cref="ExchangeRateSyncJob"/>).
/// </summary>
public class ExchangeRateResolver : IExchangeRateResolver
{
    /// <summary>
    /// Ventana de walk-back: cuantos dias hacia atras aceptamos como respaldo cuando no hay fila
    /// exacta para la fecha pedida. Reusa el MISMO numero que <c>BnaExchangeRateService</c> ya
    /// usaba para esto (5 dias cubre findes largos/feriados) — no se inventa un numero nuevo.
    /// </summary>
    private const int WalkBackWindowDays = 5;

    private static readonly TimeSpan TodayCacheTtl = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan PastDateCacheTtl = TimeSpan.FromHours(12);
    private static readonly TimeSpan MissCacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// TTL del entorno ARCA (<c>AfipSettings.IsProduction</c>) cacheado. Se lee en CADA llamada a
    /// <see cref="GetSuggestionAsync"/> (potencialmente muchas por minuto, una por tecla del
    /// casillero de TC), pero cambia rarísima vez (solo cuando un admin pasa la agencia de
    /// homologación a producción). 5 minutos es suficiente para no pegarle a la base en cada
    /// consulta sin arriesgar que un cambio de entorno tarde en reflejarse.
    /// </summary>
    private static readonly TimeSpan IsProductionCacheTtl = TimeSpan.FromMinutes(5);
    private const string IsProductionCacheKey = "afip-settings:is-production";

    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExchangeRateResolver> _logger;

    public ExchangeRateResolver(AppDbContext context, IMemoryCache cache, ILogger<ExchangeRateResolver> logger)
    {
        _context = context;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ExchangeRateSuggestion?> GetSuggestionAsync(
        string currency, DateOnly date, CancellationToken ct, bool excludePracticeOfficialData = false)
    {
        // Paso 0 (§5.1): moneda local, sin base ni red. Corta aca — nunca cachea ni consulta nada,
        // ni siquiera necesita saber el entorno de ARCA.
        if (IsPesos(currency))
        {
            return BuildPesosSuggestion(date);
        }

        // "Hoy" SIEMPRE en hora argentina (regla obligatoria §5.3): entre las 21:00 y las 24:00 ART,
        // UTC ya esta en el dia siguiente. Usar el dia UTC crudo (sin pasar por ArgentinaTime) aca
        // haria que a las 21:30 ART el sistema busque la cotizacion de MANANA (que todavia no
        // existe) y la pantalla quede sin sugerencia todas las noches.
        var todayArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
        bool isPastDate = date < todayArgentina;

        var isProduction = await GetIsProductionAsync(ct);
        // El modo "solo datos reales" (dashboard) es una dimension MAS de la cache: la misma
        // fecha puede tener respuestas distintas segun quien pregunta (facturar SI acepta un
        // AfipOficial de homologacion; el dashboard NO), asi que viaja en la clave.
        var cacheKey = BuildCacheKey(currency, isProduction, date, excludePracticeOfficialData);

        if (_cache.TryGetValue(cacheKey, out ExchangeRateSuggestion? cached))
        {
            return cached;
        }

        var suggestion = await ResolveFromDatabaseAsync(currency, date, isProduction, excludePracticeOfficialData, ct);

        // Fechas pasadas son inmutables (el dato no va a cambiar): TTL largo. "Hoy" puede recibir la
        // fila que el job recien escribio: TTL corto. Un miss se cachea aparte y mas corto todavia,
        // para que una fecha sin dato no golpee la base en cada tecla que el usuario tipea.
        var ttl = suggestion is null
            ? MissCacheTtl
            : isPastDate ? PastDateCacheTtl : TodayCacheTtl;
        _cache.Set(cacheKey, suggestion, ttl);

        return suggestion;
    }

    private async Task<bool> GetIsProductionAsync(CancellationToken ct)
    {
        if (_cache.TryGetValue(IsProductionCacheKey, out bool cachedIsProduction))
        {
            return cachedIsProduction;
        }

        var settings = await _context.AfipSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var isProduction = settings?.IsProduction ?? false;
        _cache.Set(IsProductionCacheKey, isProduction, IsProductionCacheTtl);
        return isProduction;
    }

    private async Task<ExchangeRateSuggestion?> ResolveFromDatabaseAsync(
        string currency, DateOnly date, bool isProduction, bool excludePracticeOfficialData, CancellationToken ct)
    {
        // Paso 1 (§5.1): match exacto de la fecha pedida.
        var exactMatch = await QueryBestRowAsync(
            currency, fromDateInclusive: date, toDateInclusive: date, isProduction, excludePracticeOfficialData, ct);
        if (exactMatch is not null)
        {
            return ToSuggestion(exactMatch, isStale: false);
        }

        // Paso 2: walk-back hasta 5 dias hacia atras. Sin fila propia (fin de semana/feriado), cae a
        // la mas reciente dentro de la ventana; la fecha REAL del dato viaja en RateDate.
        var earliestDate = date.AddDays(-WalkBackWindowDays);
        var fallbackMatch = await QueryBestRowAsync(
            currency, fromDateInclusive: earliestDate, toDateInclusive: date, isProduction, excludePracticeOfficialData, ct);
        if (fallbackMatch is not null)
        {
            return ToSuggestion(fallbackMatch, isStale: true);
        }

        // Paso 3: nada dentro de la ventana. El job todavia no cubrio esta fecha (o esta muy vieja):
        // sin sugerencia. El caller cae a carga manual (P-21: nunca se inventa un numero).
        return null;
    }

    /// <summary>
    /// Trae la MEJOR fila candidata dentro del rango de fechas pedido, aplicando la precedencia
    /// de fuentes (§4.3, ampliada por ADR-011 enmienda 2026-08-05 con <see cref="ExchangeRateSource.OficialPorApi"/>):
    /// AfipOficial primero, despues la API publica de respaldo, despues los respaldos BNA_*, despues
    /// el resto; a igualdad de fuente, la fecha mas reciente; a igualdad de fecha, la fila mas nueva.
    ///
    /// <para><b>FIX BLOQUEANTE (revision post-implementacion, 2026-08-05)</b>: la primera version de
    /// este metodo llamaba a un helper <c>static</c> con un <c>switch</c>
    /// (<c>.OrderBy(quote => SourcePrecedenceRank(quote.Source))</c>). Eso compila y pasa los tests
    /// InMemory (que evaluan LINQ en memoria, no lo traducen a SQL), pero contra Postgres real el
    /// traductor de EF Core NO sabe convertir una llamada a metodo arbitraria dentro de un
    /// <c>OrderBy</c> en un <c>ORDER BY CASE</c> de SQL — tira <c>InvalidOperationException</c> en
    /// CADA resolucion. El ternario INLINE de abajo SI lo traduce (EF Core reconoce el patron
    /// <c>condicion ? a : b</c> y lo arma como <c>CASE WHEN ... THEN ... ELSE ... END</c>). Blindado
    /// con un test de integracion contra Postgres real (Testcontainers) que ejecuta esta query tal
    /// cual corre en produccion — los tests InMemory NO alcanzan para esto.</para>
    ///
    /// <para><b>Los dos modos de <paramref name="excludePracticeOfficialData"/></b> (ver el doc de
    /// <see cref="IExchangeRateResolver.GetSuggestionAsync"/> para el POR QUE de cada uno):</para>
    /// <list type="bullet">
    ///   <item><c>false</c> (facturar, comportamiento de SIEMPRE, sin cambios): la fila tiene que ser
    ///   del MISMO entorno de ARCA que esta corriendo el sistema ahora mismo, sin importar la fuente.</item>
    ///   <item><c>true</c> (dashboard, "solo datos reales"): un <see cref="ExchangeRateSource.AfipOficial"/>
    ///   que no vino del entorno productivo de ARCA (dato de juguete) se descarta siempre; el resto de
    ///   fuentes valen en cualquier entorno (son datos reales que no dependen de contra que ambiente de
    ///   ARCA esta corriendo el sistema).</item>
    /// </list>
    /// </summary>
    private Task<ExchangeRateQuote?> QueryBestRowAsync(
        string currency, DateOnly fromDateInclusive, DateOnly toDateInclusive, bool isProduction,
        bool excludePracticeOfficialData, CancellationToken ct)
    {
        return _context.ExchangeRateQuotes
            .AsNoTracking()
            .Where(quote =>
                quote.Currency == currency
                && quote.SupersededByQuoteId == null
                && quote.QuoteDate >= fromDateInclusive
                && quote.QuoteDate <= toDateInclusive
                && (
                    (!excludePracticeOfficialData && quote.IsProductionSource == isProduction)
                    || (excludePracticeOfficialData
                        && (quote.Source != ExchangeRateSource.AfipOficial || quote.IsProductionSource))
                ))
            // El CASE de la precedencia (§4.3) INLINE, no en un metodo aparte: es lo unico que EF
            // Core 8 / Npgsql traducen de forma confiable a "ORDER BY CASE WHEN ... END" en SQL.
            .OrderBy(quote => quote.Source == ExchangeRateSource.AfipOficial
                ? 0
                : (quote.Source == ExchangeRateSource.OficialPorApi
                    ? 1
                    : (quote.Source == ExchangeRateSource.BNA_Minorista
                        || quote.Source == ExchangeRateSource.BNA_Mayorista
                        || quote.Source == ExchangeRateSource.BNA_VendedorDivisa
                            ? 2
                            : 3)))
            .ThenByDescending(quote => quote.QuoteDate)
            .ThenByDescending(quote => quote.Id)
            .FirstOrDefaultAsync(ct);
    }

    private static ExchangeRateSuggestion ToSuggestion(ExchangeRateQuote row, bool isStale) => new(
        Rate: row.Rate,
        RateDate: row.QuoteDate,
        Source: row.Source,
        ProviderName: row.ProviderName,
        ArcaFchCotiz: row.ArcaFchCotiz,
        IsStale: isStale,
        QuoteId: row.Id,
        FetchedAt: row.FetchedAt,
        IsProductionSource: row.IsProductionSource);

    /// <summary>
    /// Sugerencia trivial para pesos: Rate=1, sin fuente registrada (no hay fila real detras — no
    /// hace falta consultar nada). QuoteId=0 es un CENTINELA que nunca se persiste: ningun caller de
    /// negocio llama a este resolver para una factura en pesos (el guard "es pesos" corta antes en
    /// <c>InvoiceService.ValidateMultiCurrencyInvoicingAsync</c>).
    /// </summary>
    private static ExchangeRateSuggestion BuildPesosSuggestion(DateOnly date) => new(
        Rate: 1m,
        RateDate: date,
        Source: ExchangeRateSource.Unset,
        ProviderName: "PES",
        ArcaFchCotiz: null,
        IsStale: false,
        QuoteId: 0,
        FetchedAt: DateTime.UtcNow,
        // Pesos no es un dato de ARCA (Source=Unset, nunca AfipOficial): este flag no dispara ninguna
        // leyenda de "dolar de prueba" para este caso, el valor exacto es irrelevante fuera de eso.
        IsProductionSource: true);

    private static bool IsPesos(string currency) =>
        string.Equals(currency, "ARS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(currency, "PES", StringComparison.OrdinalIgnoreCase);

    private static string BuildCacheKey(string currency, bool isProduction, DateOnly date, bool excludePracticeOfficialData) =>
        $"fx:{currency}:{isProduction}:{date:yyyy-MM-dd}:{excludePracticeOfficialData}";
}
