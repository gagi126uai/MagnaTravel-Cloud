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
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real" + "el dolar nunca falta"; ampliada 2026-08-06
/// a EUR/BRL): job que le pregunta a ARCA (<c>FEParamGetCotizacion</c>) cuanto vale el dolar y lo
/// anota en la libreta (<c>ExchangeRateQuotes</c>). Es el UNICO camino del sistema que le pega a ARCA
/// para esto — el resolver que usan las pantallas SOLO lee lo que este job ya escribio (§5.1).
///
/// <para><b>Cuando corre</b>: Hangfire lo dispara CADA HORA (antes era 1 vez/dia a las 15:00 UTC) —
/// asi un dia con las APIs caidas a la mañana se auto-sana en la corrida siguiente, sin esperar hasta
/// el dia siguiente. Un guard barato al inicio de cada moneda la corta sin llamar a nadie cuando ya
/// esta resuelta (ver <see cref="IsTodayAlreadyFullyCoveredAsync"/> para dolar y el chequeo
/// equivalente al principio de <see cref="RunPublicOnlyCurrencySyncAsync"/> para euro/real). Tambien
/// se puede disparar FUERA de horario: el resolver lo encola on-demand (fire-and-forget) cuando
/// alguien pide una cotizacion y no hay fila de hoy (ver <see cref="ExchangeRateResolver"/>).</para>
///
/// <para><b>Tres monedas, DOS caminos distintos</b> (ampliacion 2026-08-06, "el euro y el real
/// tampoco tienen que faltar"): ARCA solo cotiza dolar (<c>MonId="DOL"</c>) — no hay equivalente para
/// euro ni real, y esta obra NO agrega ninguno (la factura sigue siendo solo-USD hoy). Por eso:</para>
/// <list type="bullet">
///   <item><b>USD</b> (<see cref="RunUsdSyncAsync"/>): ARCA primero, despues la escalera de CINCO
///   APIs publicas, despues el scraper de Banco Nacion como ULTIMO respaldo — sin cambios de
///   comportamiento respecto de antes de esta ampliacion.</item>
///   <item><b>EUR/BRL</b> (<see cref="RunPublicOnlyCurrencySyncAsync"/>): SOLO la escalera de APIs
///   publicas (sin ARCA, sin scraper BNA — ese dato se compara en el dashboard contra estas mismas
///   filas, ver <c>ReportService.GetDashboardBnaRateAsync</c>). La cobertura de proveedores no es
///   pareja entre las tres monedas: criptoya solo tiene dolar, bluelytics solo dolar+euro (verificado
///   con curl el 2026-08-06, ver el doc de clase de <see cref="IOfficialDollarPublicApiService"/>).</item>
/// </list>
///
/// <para><b>Que hace la parte de USD</b> (§7.2, en este orden):
/// <list type="number">
///   <item>Cotizacion de HOY (hora argentina) desde ARCA. Si ARCA falla, prueba la escalera de
///   CINCO APIs publicas de respaldo REAL (<see cref="TryEachPublicProviderForTodayAsync"/>: dolarapi
///   -&gt; monedapi -&gt; criptoya -&gt; argentinadatos -&gt; bluelytics, corta en la primera que
///   conteste). Si las cinco fallan, cae al scraper de Banco Nacion
///   (<see cref="IBnaExchangeRateService"/>) — SOLO para hoy, ese scraper es el ULTIMO respaldo de
///   toda la escalera.</item>
///   <item>Backfill de los ultimos <see cref="BackfillDays"/> dias ANTERIORES a hoy: para cada
///   fecha sin fila oficial todavia, vuelve a preguntarle a ARCA; si falla, prueba SOLO
///   argentinadatos.com con su variante "por fecha" (el UNICO de los cinco proveedores con historial
///   real; el scraper BNA tampoco se usa aca — solo sabe dar el dato de "ahora"). Es la reconciliacion
///   que pide T-12 — si el job no corrio un dia, o ARCA estuvo caido, la corrida siguiente se
///   auto-repara sola sin intervencion manual.</item>
/// </list></para>
///
/// <para><b>Nunca tira una excepcion hacia afuera</b> (§7.2 punto 4): un job que explota deja de
/// correr (Hangfire lo marca fallido y depende de que alguien lo note) y nadie se entera. Cualquier
/// fallo puntual se loguea como <c>Warning</c> con moneda/fecha/proveedor, y la corrida sigue con lo
/// que falte.</para>
/// </summary>
public class ExchangeRateSyncJob
{
    private const string UsdCurrency = "USD";
    private const string EurCurrency = "EUR";
    private const string BrlCurrency = "BRL";
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
    ///
    /// <para><b>Fuente unica de la clave (regla T-6, 2026-08-06)</b>: apunta a la const de
    /// <see cref="ExchangeRateResolver.IsProductionCacheKey"/> en vez de repetir el literal. Que dos
    /// archivos escriban a mano el nombre de la MISMA entrada de cache es como se rompe silenciosamente
    /// una invalidacion: alguien la renombra en un lado y el otro sigue leyendo un valor viejo que ya
    /// nadie borra.</para>
    /// </summary>
    private const string IsProductionCacheKey = ExchangeRateResolver.IsProductionCacheKey;
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

    /// <summary>Ejecuta una pasada del job. Hangfire la invoca con la cron registrada en Program.cs
    /// (cada hora desde la enmienda "el dolar nunca falta", 2026-08-05); tambien es invocable a mano
    /// desde el panel de Hangfire, desde el disparo on-demand del resolver, o desde tests.
    ///
    /// <para>Ampliacion 2026-08-06: una sola llamada sincroniza las TRES monedas (USD/EUR/BRL), cada
    /// una con su propio guard de cadencia — asi <c>POST /api/exchange-rates/refresh</c> (el boton
    /// "actualizar" de la tira) y el disparo on-demand del resolver siguen funcionando SIN cambios:
    /// encolan esta misma <see cref="RunAsync"/>, que ahora cubre todo.</para>
    ///
    /// <para><b>Una moneda no bloquea a las otras</b> (revision post-review 2026-08-06): cada llamada
    /// va en SU PROPIO try/catch. Antes de este fix, un fallo IMPREVISTO en el camino de USD (ej. una
    /// excepcion que se escape de <c>IAfipService</c> antes de llegar al catch interno, o un error de
    /// Postgres al leer <c>AfipSettings</c>) cortaba TODA la corrida y EUR/BRL se quedaban sin
    /// sincronizar esa hora — el guard de cadencia por moneda (§7.2) protege contra llamadas
    /// redundantes, pero no contra esto: una excepcion no pasa POR el guard, lo salta directo. Esto
    /// vuelve la garantia real en vez de accidental.</para>
    /// </summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        // "Hoy" SIEMPRE en hora argentina (regla obligatoria §5.3): el job corre cada hora en UTC,
        // asi que cerca de la medianoche ambos dias pueden no coincidir; usar ArgentinaTime en vez de
        // el dia UTC crudo (sin ArgentinaTime) es la misma regla en los 4 lugares que la exigen, sin excepciones.
        var today = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());

        await RunCurrencySyncSafelyAsync(UsdCurrency, () => RunUsdSyncAsync(today, ct));
        await RunCurrencySyncSafelyAsync(EurCurrency, () => RunPublicOnlyCurrencySyncAsync(EurCurrency, today, ct));
        await RunCurrencySyncSafelyAsync(BrlCurrency, () => RunPublicOnlyCurrencySyncAsync(BrlCurrency, today, ct));
    }

    /// <summary>
    /// Ultima red por moneda (revision post-review 2026-08-06): cada uno de los tres caminos de
    /// <see cref="RunAsync"/> YA se defiende de sus propios fallos esperados (ARCA caido, proveedor
    /// publico caido, etc. — todos vuelven <c>null</c>/<c>false</c>, nunca tiran). Este catch es para
    /// lo IMPREVISTO que se escape de todos modos (ej. Postgres cae justo al leer <c>AfipSettings</c>
    /// en <see cref="GetIsProductionAsync"/>, antes de que ningun try/catch interno pueda actuar): sin
    /// esto, esa excepcion cruda tumbaria <see cref="RunAsync"/> completo y las monedas que todavia no
    /// corrieron esa hora se quedarian sin intentar. Mismo criterio §7.2 punto 4 ("nunca tira hacia
    /// afuera"), aplicado ahora POR MONEDA en vez de a la corrida entera.
    /// </summary>
    private async Task RunCurrencySyncSafelyAsync(string currency, Func<Task> runCurrencySync)
    {
        try
        {
            await runCurrencySync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExchangeRateSyncJob: fallo inesperado sincronizando {Currency}; las demas monedas de esta corrida siguen su curso.",
                currency);
        }
    }

    /// <summary>
    /// Camino de USD (sin cambios de comportamiento respecto de antes de la ampliacion 2026-08-06):
    /// ARCA primero, escalera de APIs publicas despues, scraper BNA como ultimo respaldo. Es la UNICA
    /// moneda con fuente ARCA — ver el doc de clase para el porque.
    ///
    /// <para><c>internal virtual</c> (no <c>private</c>) a proposito, mismo patron que otros seams de
    /// test del repo (<c>InternalsVisibleTo("TravelApi.Tests")</c> ya configurado): permite que un test
    /// arme una subclase que lo sobreescriba para tirar una excepcion cruda, y verificar que
    /// <see cref="RunCurrencySyncSafelyAsync"/> la contiene sin frenar a EUR/BRL. No cambia nada del
    /// comportamiento en produccion (nadie mas hereda de esta clase).</para>
    /// </summary>
    internal virtual async Task RunUsdSyncAsync(DateOnly today, CancellationToken ct)
    {
        bool isProduction = await GetIsProductionAsync(ct);

        // Guard barato de cadencia (ADR-011, enmienda 2026-08-05 "el dolar nunca falta"): con el
        // recurring corriendo cada hora en vez de 1 vez/dia, la mayoria de las corridas se encuentran
        // el dia YA resuelto. Cortar aca evita pegarle a Postgres con el resto del metodo (7 fechas
        // de backfill x hasta 2 checks cada una) cuando no hace falta.
        if (await IsTodayAlreadyFullyCoveredAsync(today, isProduction, ct))
        {
            _logger.LogInformation(
                "ExchangeRateSyncJob: {Currency}/{Today} ya esta cubierto, se salta esta corrida sin llamar a nadie.",
                UsdCurrency, today);
            return;
        }

        bool todaySyncedFromArca = await TrySyncDateFromArcaAsync(today, isProduction, ct);
        if (!todaySyncedFromArca)
        {
            bool todaySyncedFromPublicApi = await TrySyncTodayFromPublicApiAsync(UsdCurrency, today, ct);
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
                await TrySyncDateFromPublicApiAsync(UsdCurrency, date, ct);
            }
        }
    }

    /// <summary>
    /// Camino de EUR/BRL (ampliacion 2026-08-06, "el euro y el real tampoco tienen que faltar"): SIN
    /// ARCA (no cotiza estas monedas) y SIN el scraper BNA como respaldo escrito aca — ese dato
    /// (<c>BnaExchangeRateService</c> ya trae Euro/Real de la misma pagina que trae el dolar) se
    /// compara DESPUES, en el dashboard, contra las filas que este metodo va dejando en la libreta
    /// (ver <c>ReportService.GetDashboardBnaRateAsync</c>, "el dato mas nuevo gana" extendido a estas
    /// dos monedas). Mezclar las dos fuentes ACA hubiera sido escribir dos veces el mismo dato.
    ///
    /// <para><b>Guard de cadencia "corta por moneda"</b> (mas simple que el de USD a proposito: sin
    /// ARCA no hace falta la segunda señal de <see cref="IsTodayAlreadyFullyCoveredAsync"/> — alcanza
    /// con tener la fila <see cref="ExchangeRateSource.OficialPorApi"/> de hoy para considerar el dia
    /// resuelto).</para>
    /// </summary>
    private async Task RunPublicOnlyCurrencySyncAsync(string currency, DateOnly today, CancellationToken ct)
    {
        if (_officialDollarPublicApiService is null)
        {
            return;
        }

        bool alreadyCoveredToday = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q => q.Currency == currency && q.QuoteDate == today && q.Source == ExchangeRateSource.OficialPorApi, ct);
        if (alreadyCoveredToday)
        {
            _logger.LogInformation(
                "ExchangeRateSyncJob: {Currency}/{Today} ya esta cubierto, se salta esta corrida sin llamar a nadie.",
                currency, today);
            return;
        }

        bool syncedToday = await TrySyncTodayFromPublicApiAsync(currency, today, ct);
        if (!syncedToday)
        {
            _logger.LogWarning(
                "ExchangeRateSyncJob: ningun proveedor publico tuvo {Currency} para hoy ({Date}).", currency, today);
        }

        for (int daysBack = 1; daysBack <= BackfillDays; daysBack++)
        {
            var date = today.AddDays(-daysBack);
            await TrySyncDateFromPublicApiAsync(currency, date, ct);
        }
    }

    /// <summary>
    /// Guard de cadencia (ADR-011, enmienda 2026-08-05 "el dolar nunca falta"): decide si el dia ya
    /// esta "resuelto" y la corrida puede cortar sin llamar a nadie. Exige DOS señales, no una sola:
    ///
    /// <list type="bullet">
    ///   <item>Una fila <see cref="ExchangeRateSource.OficialPorApi"/> de HOY: es la redundancia real
    ///   que agrega esta obra — se quiere que exista SIEMPRE, incluso los dias que ARCA responde
    ///   bien, para que el modo "solo datos reales" del dashboard (§ <see cref="ExchangeRateResolver"/>)
    ///   nunca dependa de un unico proveedor.</item>
    ///   <item>SOLO si el ambiente es productivo, TAMBIEN una fila <see cref="ExchangeRateSource.AfipOficial"/>
    ///   de hoy con <c>IsProductionSource=true</c>: en produccion ese numero es el que factura, asi
    ///   que el dia no cuenta como resuelto hasta tenerlo.</item>
    /// </list>
    ///
    /// <para><b>Decision deliberada sobre homologacion</b>: NO se exige la fila AfipOficial de
    /// practica para este guard. Esa cotizacion es de juguete (no sirve para nada fiscal en ese
    /// entorno) y volver a pedirsela a ARCA cada hora es barato — no vale la pena acoplar el guard a
    /// una fila fiscalmente irrelevante ahi. Es la version MAS SIMPLE que cumple el objetivo real
    /// del guard ("no spamear las APIs publicas cuando ya hay dato del dia") sin inventar una regla
    /// extra para un entorno donde no hace falta.</para>
    /// </summary>
    private async Task<bool> IsTodayAlreadyFullyCoveredAsync(DateOnly today, bool isProduction, CancellationToken ct)
    {
        bool hasOficialPorApiToday = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q => q.Currency == UsdCurrency && q.QuoteDate == today && q.Source == ExchangeRateSource.OficialPorApi, ct);
        if (!hasOficialPorApiToday)
        {
            return false;
        }

        if (!isProduction)
        {
            return true;
        }

        return await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q =>
                q.Currency == UsdCurrency
                && q.QuoteDate == today
                && q.Source == ExchangeRateSource.AfipOficial
                && q.IsProductionSource, ct);
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
                q.Currency == UsdCurrency
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
                UsdCurrency, date);
            return false;
        }

        if (official is null)
        {
            return false;
        }

        await EnsureRowExistsAsync(
            currency: UsdCurrency,
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
            currency: UsdCurrency,
            quoteDate: today,
            source: ExchangeRateSource.BNA_Minorista,
            providerName: BnaProviderName,
            rate: bnaSnapshot.Value,
            arcaFchCotiz: null,
            isProduction: isProduction,
            ct: ct);
    }

    /// <summary>
    /// Respaldo REAL via API publica, para <paramref name="currency"/> (ADR-011, enmienda 2026-08-05
    /// "el dolar nunca falta"; generalizada a EUR/BRL en la ampliacion 2026-08-06). Para USD, este es
    /// el paso DESPUES de que ARCA no contesto para HOY y ANTES de caer al scraper BNA; para EUR/BRL
    /// es el UNICO camino (no hay ARCA ni scraper para esas dos, ver el doc de clase). Se salta la
    /// llamada de red si ya hay una fila <see cref="ExchangeRateSource.OficialPorApi"/> para hoy
    /// (idempotencia, misma logica que <see cref="TrySyncDateFromArcaAsync"/>). Devuelve <c>true</c>
    /// si quedo cubierto (para que el caller de USD no intente TAMBIEN el scraper BNA de forma
    /// redundante).
    /// </summary>
    private async Task<bool> TrySyncTodayFromPublicApiAsync(string currency, DateOnly today, CancellationToken ct)
    {
        if (_officialDollarPublicApiService is null)
        {
            return false;
        }

        bool alreadyCovered = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q => q.Currency == currency && q.QuoteDate == today && q.Source == ExchangeRateSource.OficialPorApi, ct);
        if (alreadyCovered)
        {
            return true;
        }

        var reading = await TryEachPublicProviderForTodayAsync(currency, today, ct);
        if (reading is null)
        {
            return false;
        }

        await EnsureRowExistsAsync(
            currency: currency,
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
    /// Escalera de APIs publicas para el dato de HOY de <paramref name="currency"/> (ADR-011,
    /// enmienda 2026-08-05 "el dolar nunca falta"; generalizada a EUR/BRL 2026-08-06) — corta en la
    /// PRIMERA que conteste un valor util. La lista de proveedores (y su orden) depende de la moneda
    /// via <see cref="BuildTodayProvidersLadder"/>: USD tiene los cinco, EUR cuatro (sin criptoya),
    /// BRL tres (sin criptoya ni bluelytics) — ver el doc de clase de
    /// <see cref="IOfficialDollarPublicApiService"/> para el detalle de cobertura verificado con curl.
    ///
    /// <para>Cada metodo del servicio YA nunca tira (T-12, contrato de
    /// <see cref="IOfficialDollarPublicApiService"/>) — el try/catch de aca es una ultima red por si
    /// algo IMPREVISTO se escapa de una llamada puntual, para que NO tumbe el resto de la escalera.</para>
    /// </summary>
    private async Task<PublicDollarRateReading?> TryEachPublicProviderForTodayAsync(string currency, DateOnly today, CancellationToken ct)
    {
        var providersInOrder = BuildTodayProvidersLadder(currency, today);

        foreach (var (providerName, callProvider) in providersInOrder)
        {
            PublicDollarRateReading? reading;
            try
            {
                reading = await callProvider(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExchangeRateSyncJob: fallo inesperado consultando {Provider} para {Currency}/{Date}.",
                    providerName, currency, today);
                continue;
            }

            if (reading is not null)
            {
                return reading;
            }
        }

        return null;
    }

    /// <summary>
    /// Arma la escalera de proveedores de HOY para <paramref name="currency"/> (2026-08-06). Centraliza
    /// en UN solo lugar que metodo del <see cref="IOfficialDollarPublicApiService"/> corresponde a
    /// cada combinacion moneda+proveedor — el resto del job no sabe nada de nombres de metodos por
    /// moneda, solo recorre esta lista. <paramref name="today"/> viaja para la variante
    /// "por fecha" de argentinadatos.com, que en esta escalera se usa TAMBIEN para hoy (verificado
    /// con curl que esa ruta sirve el dia corriente igual que las otras).
    /// </summary>
    private (string Name, Func<CancellationToken, Task<PublicDollarRateReading?>> Call)[] BuildTodayProvidersLadder(
        string currency, DateOnly today)
    {
        var service = _officialDollarPublicApiService!;

        if (currency == UsdCurrency)
        {
            return new (string, Func<CancellationToken, Task<PublicDollarRateReading?>>)[]
            {
                ("dolarapi", service.GetTodayRateAsync),
                ("monedapi", service.GetTodayRateFromMonedApiAsync),
                ("criptoya", service.GetTodayRateFromCriptoYaAsync),
                ("argentinadatos", callCt => service.GetRateForDateAsync(today, callCt)),
                ("bluelytics", service.GetTodayRateFromBluelyticsAsync),
            };
        }

        if (currency == EurCurrency)
        {
            // Sin criptoya (verificado con curl: /api/bancostodos no tiene variante de euro).
            return new (string, Func<CancellationToken, Task<PublicDollarRateReading?>>)[]
            {
                ("dolarapi", service.GetTodayRateForEurAsync),
                ("monedapi", service.GetTodayRateForEurFromMonedApiAsync),
                ("argentinadatos", callCt => service.GetEurRateForDateAsync(today, callCt)),
                ("bluelytics", service.GetTodayRateForEurFromBluelyticsAsync),
            };
        }

        if (currency == BrlCurrency)
        {
            // Sin criptoya NI bluelytics (verificado con curl: bluelytics no trae ninguna clave de real).
            return new (string, Func<CancellationToken, Task<PublicDollarRateReading?>>)[]
            {
                ("dolarapi", service.GetTodayRateForBrlAsync),
                ("monedapi", service.GetTodayRateForBrlFromMonedApiAsync),
                ("argentinadatos", callCt => service.GetBrlRateForDateAsync(today, callCt)),
            };
        }

        // Defensivo: este job solo conoce USD/EUR/BRL (constantes de arriba). Si algun dia se agrega
        // una cuarta moneda sin actualizar este metodo, mejor una lista vacia (el caller lo trata
        // como "ningun proveedor contesto") que una excepcion que tumbe el resto de la corrida.
        return Array.Empty<(string, Func<CancellationToken, Task<PublicDollarRateReading?>>)>();
    }

    /// <summary>
    /// Backfill (ADR-011, enmienda 2026-08-05; generalizado a EUR/BRL 2026-08-06): ARCA (solo para
    /// USD) o directamente la escalera de hoy (para EUR/BRL) no contesto para <paramref name="date"/>,
    /// probamos la variante "por fecha" de argentinadatos.com — el UNICO de los proveedores con
    /// historial real para CUALQUIERA de las tres monedas. No hay caida a BNA aca: el scraper no
    /// tiene historial, solo el dato de "ahora". Si tampoco hay dato, la fecha queda como hueco para
    /// que una corrida futura lo reintente (mismo criterio que el backfill de ARCA).
    /// </summary>
    private async Task TrySyncDateFromPublicApiAsync(string currency, DateOnly date, CancellationToken ct)
    {
        if (_officialDollarPublicApiService is null)
        {
            return;
        }

        bool alreadyCovered = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q => q.Currency == currency && q.QuoteDate == date && q.Source == ExchangeRateSource.OficialPorApi, ct);
        if (alreadyCovered)
        {
            return;
        }

        var callBackfillProvider = GetBackfillProviderCall(currency);
        if (callBackfillProvider is null)
        {
            return; // moneda sin proveedor con historial (defensivo, ver BuildTodayProvidersLadder).
        }

        PublicDollarRateReading? reading;
        try
        {
            reading = await callBackfillProvider(date, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExchangeRateSyncJob: fallo inesperado consultando la API publica de respaldo (backfill) para {Currency}/{Date}.",
                currency, date);
            return;
        }

        if (reading is null)
        {
            return;
        }

        await EnsureRowExistsAsync(
            currency: currency,
            quoteDate: date,
            source: ExchangeRateSource.OficialPorApi,
            providerName: reading.ProviderName,
            rate: reading.Rate,
            arcaFchCotiz: null,
            isProduction: true,
            ct: ct);
    }

    /// <summary>
    /// El UNICO proveedor con historial real (argentinadatos.com) tiene un metodo DISTINTO por moneda
    /// en la interfaz (rutas distintas, ver el doc de clase de <see cref="IOfficialDollarPublicApiService"/>)
    /// — este helper es el UNICO lugar del job que sabe cual metodo corresponde a cual moneda para el
    /// backfill.
    /// </summary>
    private Func<DateOnly, CancellationToken, Task<PublicDollarRateReading?>>? GetBackfillProviderCall(string currency)
    {
        var service = _officialDollarPublicApiService!;

        if (currency == UsdCurrency) return service.GetRateForDateAsync;
        if (currency == EurCurrency) return service.GetEurRateForDateAsync;
        if (currency == BrlCurrency) return service.GetBrlRateForDateAsync;

        return null;
    }

    /// <summary>
    /// Upsert idempotente equivalente al <c>ON CONFLICT ... DO NOTHING</c> de la spec (§4.2): si ya
    /// existe una fila para esta combinacion moneda+fecha+fuente+entorno, NO la toca (la fila es
    /// inmutable una vez escrita, regla F-6). Usa EF en vez de SQL crudo a proposito para que el
    /// job sea testeable con el proveedor InMemory (regla del repo: unit tests InMemory,
    /// integracion contra Postgres real queda para CI).
    /// </summary>
    private async Task EnsureRowExistsAsync(
        string currency,
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
        // llamar aca — pero un dolar (o un euro, o un real) no vale 0 ni 1 peso es la regla que
        // protege esta tabla, no una casualidad de quien la llama hoy. Repetirla aca cierra el caso
        // de un caller futuro que se olvide de chequear antes.
        if (rate <= 0m || rate == 1m)
        {
            _logger.LogWarning(
                "ExchangeRateSyncJob: se descarto una cotizacion invalida ({Rate}) para {Currency}/{QuoteDate}.",
                rate, currency, quoteDate);
            return;
        }

        bool alreadyExists = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .AnyAsync(q =>
                q.Currency == currency
                && q.QuoteDate == quoteDate
                && q.Source == source
                && q.IsProductionSource == isProduction, ct);
        if (alreadyExists)
        {
            return;
        }

        // Defensa de coherencia (§7.2 punto 5, "el dolar nunca falta"): solo RASTRO, jamas bloquea
        // (P-21/T-12) — si otra fuente ya escrita hoy para esta misma moneda dio un numero muy
        // distinto, alguien deberia poder verlo en los logs (una API con un bug puntual, un feriado
        // que una fuente todavia no actualizo, etc.).
        await WarnIfRateDivergesFromSameDayAsync(currency, quoteDate, source, providerName, rate, ct);

        var newRow = new ExchangeRateQuote
        {
            Currency = currency,
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
                currency, quoteDate, source);
        }
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

    /// <summary>
    /// Umbral de la defensa de coherencia (§7.2 punto 5): 5% de diferencia entre dos fuentes del
    /// MISMO dia es sospechoso (el dolar oficial no salta 5% de una fuente a otra en el mismo dia),
    /// pero no es motivo para rechazar el dato — el sistema sugiere, nunca decide por el usuario
    /// (P-21). Solo deja rastro en el log para que alguien lo pueda revisar.
    /// </summary>
    private const decimal CoherenceWarningThreshold = 0.05m;

    /// <summary>
    /// Compara <paramref name="rate"/> (la fila que estamos por escribir) contra la fila MAS
    /// RECIENTE que YA existe para el mismo <paramref name="quoteDate"/> y moneda, sin importar de
    /// que fuente/proveedor vino esa otra fila. Si difieren mas del <see cref="CoherenceWarningThreshold"/>,
    /// deja un <c>Warning</c> — nunca bloquea el guardado (T-12, "el sistema degrada, no se cae").
    /// </summary>
    private async Task WarnIfRateDivergesFromSameDayAsync(
        string currency, DateOnly quoteDate, ExchangeRateSource source, string providerName, decimal rate, CancellationToken ct)
    {
        var mostRecentOtherRow = await _context.ExchangeRateQuotes
            .AsNoTracking()
            .Where(q => q.Currency == currency
                && q.QuoteDate == quoteDate
                && (q.Source != source || q.ProviderName != providerName))
            .OrderByDescending(q => q.Id)
            .FirstOrDefaultAsync(ct);

        if (mostRecentOtherRow is null || mostRecentOtherRow.Rate <= 0m)
        {
            return;
        }

        var differenceRatio = Math.Abs(rate - mostRecentOtherRow.Rate) / mostRecentOtherRow.Rate;
        if (differenceRatio > CoherenceWarningThreshold)
        {
            _logger.LogWarning(
                "ExchangeRateSyncJob: {Currency}/{QuoteDate} - {NewProvider} dio {NewRate} pero {ExistingProvider} " +
                "ya tenia {ExistingRate} para el mismo dia (diferencia {DifferencePercent:P1}). Solo queda como rastro, no se bloquea el guardado.",
                currency, quoteDate, providerName, rate, mostRecentOtherRow.ProviderName, mostRecentOtherRow.Rate, differenceRatio);
        }
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
}
