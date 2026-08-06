using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;
using TravelApi.Application.Interfaces;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): job diario (Hangfire, 15:00 UTC ≈ 12:00
/// ART) que le pregunta a ARCA (<c>FEParamGetCotizacion</c>) cuanto vale el dolar y lo anota en la
/// libreta (<c>ExchangeRateQuotes</c>). Es el UNICO camino del sistema que le pega a ARCA para
/// esto — el resolver que usan las pantallas SOLO lee lo que este job ya escribio (§5.1).
///
/// <para><b>Que hace cada corrida</b> (§7.2, en este orden):
/// <list type="number">
///   <item>Cotizacion de HOY (hora argentina) desde ARCA. Si ARCA falla, prueba el respaldo REAL de
///   una API publica (<see cref="IOfficialDollarPublicApiService.GetTodayRateAsync"/>, ADR-011
///   enmienda 2026-08-05). Si esa tambien falla, cae al scraper de Banco Nacion
///   (<see cref="IBnaExchangeRateService"/>) — SOLO para hoy, en ese orden.</item>
///   <item>Backfill de los ultimos <see cref="BackfillDays"/> dias ANTERIORES a hoy: para cada
///   fecha sin fila oficial todavia, vuelve a preguntarle a ARCA; si falla, prueba la MISMA API
///   publica de respaldo pero con su variante "por fecha" (el scraper BNA no se usa aca: solo sabe
///   dar el dato de "ahora", no tiene historial). Es la reconciliacion que pide T-12 — si el job no
///   corrio un dia, o ARCA estuvo caido, la corrida siguiente se auto-repara sola sin intervencion
///   manual.</item>
/// </list></para>
///
/// <para><b>Nunca tira una excepcion hacia afuera</b> (§7.2 punto 4): un job que explota deja de
/// correr (Hangfire lo marca fallido y depende de que alguien lo note) y nadie se entera. Cualquier
/// fallo puntual se loguea como <c>Warning</c> con moneda/fecha/proveedor, y la corrida sigue con lo
/// que falte.</para>
/// </summary>
public class ExchangeRateSyncJob
{
    // MVP: solo se sincroniza dolar. Ampliar a otras monedas es agregar esta constante a una lista
    // y recorrerla — no hay nada mas atado a "USD" en el resto del job.
    private const string Currency = "USD";
    private const string ArcaMonId = "DOL";
    private const string ArcaProviderName = "ARCA_WSFEv1";
    private const string BnaProviderName = "BNA_Scraper";

    /// <summary>
    /// Cuantos dias hacia atras (sin contar hoy, que se maneja aparte) intenta rellenar el
    /// backfill en cada corrida. 7 dias es margen de sobra para que un fin de semana largo, un
    /// feriado, o una corrida que no paso por algun motivo, se termine auto-reparando solo.
    /// </summary>
    private const int BackfillDays = 7;

    /// <summary>
    /// MISMA clave y MISMO TTL que <see cref="ExchangeRateResolver"/> (fix detalle #7, revision
    /// post-implementacion 2026-08-05): antes cada llamada a <c>GetIsProductionAsync</c> hacia un
    /// SELECT a <c>AfipSettings</c> — barato individualmente, pero innecesario cuando el resolver
    /// (que corre en un proceso HTTP separado del job) ya puede tener el valor fresco en la MISMA
    /// cache compartida (<see cref="IMemoryCache"/> es un singleton del proceso). Cachear aca
    /// TAMBIEN evita que, si el job alguna vez llama a <c>GetIsProductionAsync</c> mas de una vez
    /// por corrida, cada llamada vuelva a pegarle a la base.
    /// </summary>
    private const string IsProductionCacheKey = "afip-settings:is-production";
    private static readonly TimeSpan IsProductionCacheTtl = TimeSpan.FromMinutes(5);

    private readonly AppDbContext _context;
    private readonly IAfipService _afipService;
    private readonly IBnaExchangeRateService _bnaExchangeRateService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ExchangeRateSyncJob> _logger;
    // ADR-011 (enmienda 2026-08-05): opcional para no romper los tests existentes que instancian el
    // job con los 5 args de siempre (mismo patron que los opcionales de ReportService). Sin este
    // servicio inyectado, el job simplemente NO intenta el respaldo de API publica y se comporta
    // EXACTO como antes de esta obra (ARCA -> scraper BNA para hoy; solo ARCA en el backfill).
    private readonly IOfficialDollarPublicApiService? _officialDollarPublicApiService;

    public ExchangeRateSyncJob(
        AppDbContext context,
        IAfipService afipService,
        IBnaExchangeRateService bnaExchangeRateService,
        IMemoryCache cache,
        ILogger<ExchangeRateSyncJob> logger,
        IOfficialDollarPublicApiService? officialDollarPublicApiService = null)
    {
        _context = context;
        _afipService = afipService;
        _bnaExchangeRateService = bnaExchangeRateService;
        _cache = cache;
        _logger = logger;
        _officialDollarPublicApiService = officialDollarPublicApiService;
    }

    /// <summary>Ejecuta una pasada del job. Hangfire la invoca con la cron registrada en Program.cs;
    /// tambien es invocable a mano desde el panel de Hangfire o desde tests.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        bool isProduction = await GetIsProductionAsync(ct);

        // "Hoy" SIEMPRE en hora argentina (regla obligatoria §5.3): el job corre a las 15:00 UTC
        // (12:00 ART), asi que ambos dias coinciden casi siempre, pero usar ArgentinaTime en vez de
        // el dia UTC crudo (sin ArgentinaTime) es la misma regla en los 4 lugares que la exigen, sin excepciones.
        var today = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());

        bool todaySyncedFromArca = await TrySyncDateFromArcaAsync(today, isProduction, ct);
        if (!todaySyncedFromArca)
        {
            bool todaySyncedFromPublicApi = await TrySyncTodayFromPublicApiAsync(today, ct);
            if (!todaySyncedFromPublicApi)
            {
                await TrySyncTodayFromBnaFallbackAsync(today, isProduction, ct);
            }
        }

        for (int daysBack = 1; daysBack <= BackfillDays; daysBack++)
        {
            var date = today.AddDays(-daysBack);
            bool syncedFromArca = await TrySyncDateFromArcaAsync(date, isProduction, ct);
            if (!syncedFromArca)
            {
                // El scraper BNA NO participa del backfill (§7.2 punto 1 de arriba): solo sabe dar el
                // dato de "ahora mismo", no tiene forma de contestar "cuanto valia el dolar hace 3 dias".
                await TrySyncDateFromPublicApiAsync(date, ct);
            }
        }
    }

    /// <summary>
    /// Intenta cubrir <paramref name="date"/> con la cotizacion OFICIAL de ARCA. Si ya hay una fila
    /// <see cref="ExchangeRateSource.AfipOficial"/> para esa fecha (idempotencia: no es un hueco),
    /// no hace nada y devuelve <c>true</c>. Devuelve <c>false</c> si quedo sin cubrir (ARCA no
    /// respondio, o la respuesta no sirvio) — solo lo usa el caller de "hoy" para decidir si cae al
    /// respaldo BNA; el backfill lo ignora (el proximo dia lo vuelve a intentar).
    /// </summary>
    private async Task<bool> TrySyncDateFromArcaAsync(DateOnly date, bool isProduction, CancellationToken ct)
    {
        bool alreadyCovered = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q =>
                q.Currency == Currency
                && q.QuoteDate == date
                && q.Source == ExchangeRateSource.AfipOficial
                && q.IsProductionSource == isProduction, ct);
        if (alreadyCovered)
        {
            return true;
        }

        ArcaExchangeRate? official;
        try
        {
            official = await _afipService.GetOfficialExchangeRateAsync(ArcaMonId, date, ct);
        }
        catch (Exception ex)
        {
            // AfipService.GetOfficialExchangeRateAsync ya devuelve null en sus propios fallos
            // controlados; este catch es la ultima red por si algo IMPREVISTO (ej. certificado
            // corrupto) tira una excepcion cruda. El job no puede caerse por una fecha puntual.
            _logger.LogWarning(ex,
                "ExchangeRateSyncJob: fallo inesperado consultando ARCA para {Currency}/{Date}.",
                Currency, date);
            return false;
        }

        if (official is null)
        {
            return false;
        }

        await EnsureRowExistsAsync(
            quoteDate: date,
            source: ExchangeRateSource.AfipOficial,
            providerName: ArcaProviderName,
            rate: official.MonCotiz,
            arcaFchCotiz: official.FchCotiz,
            isProduction: isProduction,
            ct: ct);

        return true;
    }

    /// <summary>
    /// Respaldo (§7.2 punto 3): ARCA no contesto para HOY, probamos con el scraper de Banco
    /// Nacion que ya existe. Solo se persiste si el dato es FRESCO (recien scrapeado en esta misma
    /// corrida) — un snapshot viejo resucitado por el fallback interno de
    /// <see cref="IBnaExchangeRateService"/> (<c>IsStale=true</c>) no es honesto etiquetarlo como
    /// "cotizacion de hoy".
    /// </summary>
    private async Task TrySyncTodayFromBnaFallbackAsync(DateOnly today, bool isProduction, CancellationToken ct)
    {
        BnaUsdSellerRateDto? bnaSnapshot;
        try
        {
            bnaSnapshot = await _bnaExchangeRateService.GetUsdSellerRateAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExchangeRateSyncJob: ARCA fallo para hoy ({Date}) y el respaldo BNA tambien fallo.", today);
            return;
        }

        if (bnaSnapshot is null || bnaSnapshot.IsStale || bnaSnapshot.Value <= 0m)
        {
            _logger.LogWarning(
                "ExchangeRateSyncJob: ARCA fallo para hoy ({Date}) y no hay respaldo BNA fresco disponible.",
                today);
            return;
        }

        await EnsureRowExistsAsync(
            quoteDate: today,
            source: ExchangeRateSource.BNA_Minorista,
            providerName: BnaProviderName,
            rate: bnaSnapshot.Value,
            arcaFchCotiz: null,
            isProduction: isProduction,
            ct: ct);
    }

    /// <summary>
    /// Respaldo REAL (ADR-011, enmienda 2026-08-05): ARCA no contesto para HOY, probamos la API
    /// publica (dolarapi.com) ANTES de caer al scraper BNA. Se salta la llamada de red si ya hay una
    /// fila <see cref="ExchangeRateSource.OficialPorApi"/> para hoy (idempotencia, misma logica que
    /// <see cref="TrySyncDateFromArcaAsync"/>). Devuelve <c>true</c> si quedo cubierto (para que el
    /// caller no intente TAMBIEN el scraper BNA de forma redundante).
    /// </summary>
    private async Task<bool> TrySyncTodayFromPublicApiAsync(DateOnly today, CancellationToken ct)
    {
        if (_officialDollarPublicApiService is null)
        {
            return false;
        }

        bool alreadyCovered = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q => q.Currency == Currency && q.QuoteDate == today && q.Source == ExchangeRateSource.OficialPorApi, ct);
        if (alreadyCovered)
        {
            return true;
        }

        PublicDollarRateReading? reading;
        try
        {
            reading = await _officialDollarPublicApiService.GetTodayRateAsync(ct);
        }
        catch (Exception ex)
        {
            // IOfficialDollarPublicApiService ya atrapa sus propios fallos y devuelve null; esta
            // ultima red es por si algo IMPREVISTO se escapa (mismo criterio que TrySyncDateFromArcaAsync).
            _logger.LogWarning(ex,
                "ExchangeRateSyncJob: fallo inesperado consultando la API publica de respaldo para {Currency}/{Date}.",
                Currency, today);
            return false;
        }

        if (reading is null)
        {
            return false;
        }

        await EnsureRowExistsAsync(
            quoteDate: today,
            source: ExchangeRateSource.OficialPorApi,
            providerName: reading.ProviderName,
            rate: reading.Rate,
            arcaFchCotiz: null,
            // Dato REAL (no depende de en que ambiente de ARCA esta corriendo el sistema): se marca
            // IsProductionSource=true SIEMPRE, literal, no la variable "isProduction" de la corrida
            // (que es el ambiente de ARCA, un concepto que a esta fuente no le aplica).
            isProduction: true,
            ct: ct);

        return true;
    }

    /// <summary>
    /// Backfill (ADR-011, enmienda 2026-08-05): ARCA no contesto para <paramref name="date"/>, probamos
    /// la variante "por fecha" de la API publica (argentinadatos.com). No hay caida a BNA aca: el
    /// scraper no tiene historial, solo el dato de "ahora". Si tampoco hay dato, la fecha queda como
    /// hueco para que una corrida futura lo reintente (mismo criterio que el backfill de ARCA).
    /// </summary>
    private async Task TrySyncDateFromPublicApiAsync(DateOnly date, CancellationToken ct)
    {
        if (_officialDollarPublicApiService is null)
        {
            return;
        }

        bool alreadyCovered = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q => q.Currency == Currency && q.QuoteDate == date && q.Source == ExchangeRateSource.OficialPorApi, ct);
        if (alreadyCovered)
        {
            return;
        }

        PublicDollarRateReading? reading;
        try
        {
            reading = await _officialDollarPublicApiService.GetRateForDateAsync(date, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExchangeRateSyncJob: fallo inesperado consultando la API publica de respaldo (backfill) para {Currency}/{Date}.",
                Currency, date);
            return;
        }

        if (reading is null)
        {
            return;
        }

        await EnsureRowExistsAsync(
            quoteDate: date,
            source: ExchangeRateSource.OficialPorApi,
            providerName: reading.ProviderName,
            rate: reading.Rate,
            arcaFchCotiz: null,
            isProduction: true,
            ct: ct);
    }

    /// <summary>
    /// Upsert idempotente equivalente al <c>ON CONFLICT ... DO NOTHING</c> de la spec (§4.2): si ya
    /// existe una fila para esta combinacion moneda+fecha+fuente+entorno, NO la toca (la fila es
    /// inmutable una vez escrita, regla F-6). Usa EF en vez de SQL crudo a proposito para que el
    /// job sea testeable con el proveedor InMemory (regla del repo: unit tests InMemory,
    /// integracion contra Postgres real queda para CI).
    /// </summary>
    private async Task EnsureRowExistsAsync(
        DateOnly quoteDate,
        ExchangeRateSource source,
        string providerName,
        decimal rate,
        DateOnly? arcaFchCotiz,
        bool isProduction,
        CancellationToken ct)
    {
        // Guard de valor invalido (§7.5), defensa en profundidad: AfipService.GetOfficialExchangeRateAsync
        // ya descarta un Rate<=0/==1 antes de devolverlo, y el fallback BNA chequea Value>0 antes de
        // llamar aca — pero un dolar no vale 0 ni 1 peso es la regla que protege esta tabla, no una
        // casualidad de quien la llama hoy. Repetirla aca cierra el caso de un caller futuro que se
        // olvide de chequear antes.
        if (rate <= 0m || rate == 1m)
        {
            _logger.LogWarning(
                "ExchangeRateSyncJob: se descarto una cotizacion invalida ({Rate}) para {Currency}/{QuoteDate}.",
                rate, Currency, quoteDate);
            return;
        }

        bool alreadyExists = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q =>
                q.Currency == Currency
                && q.QuoteDate == quoteDate
                && q.Source == source
                && q.IsProductionSource == isProduction, ct);
        if (alreadyExists)
        {
            return;
        }

        var newRow = new ExchangeRateQuote
        {
            Currency = Currency,
            QuoteDate = quoteDate,
            Source = source,
            Rate = rate,
            ProviderName = providerName,
            FetchedAt = DateTime.UtcNow,
            ArcaFchCotiz = arcaFchCotiz,
            IsProductionSource = isProduction,
        };
        _context.ExchangeRateQuotes.Add(newRow);

        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // Carrera real contra otra corrida del job en Postgres (dos disparos manuales
            // simultaneos desde el panel de Hangfire, por ejemplo): alguien mas ya escribio esta
            // fila entre el chequeo AsNoTracking de arriba y este INSERT. Mismo criterio "DO
            // NOTHING": no es un error, es el caso feliz de la idempotencia — no hace falta
            // reintentar, la fila que quedo escrita es correcta la haya puesto quien la haya puesto.
            //
            // FIX (revision post-implementacion 2026-08-05): el INSERT que fallo deja a "newRow"
            // en estado Added dentro del ChangeTracker. Sin desengancharlo, el PROXIMO
            // SaveChangesAsync de esta misma corrida del job (backfill de otra fecha) vuelve a
            // intentar insertar esta fila ya rota -> rebota TODA la corrida restante con el mismo
            // error, en vez de seguir con las fechas que si estaban libres. Detached limpia el
            // tracker sin tocar la fila que ya quedo escrita en la base (la puso quien la haya
            // puesto, esta fila local ya no importa).
            _context.Entry(newRow).State = EntityState.Detached;

            _logger.LogInformation(
                "ExchangeRateSyncJob: {Currency}/{QuoteDate}/{Source} ya fue escrita por otra corrida concurrente.",
                Currency, quoteDate, source);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

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
}
