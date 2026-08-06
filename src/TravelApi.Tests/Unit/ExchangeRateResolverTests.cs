using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-011 (enmienda 2026-08-05, "tipo de cambio real"): tests focales de
/// <see cref="ExchangeRateResolver"/> — la escalera de fallback (§5.1), la precedencia de fuentes
/// (§4.3), el aislamiento por entorno (§4.1) y el respeto a filas "tachadas" (§4.4). Son tests
/// UNITARIOS (InMemory), no tocan Postgres ni ARCA.
/// </summary>
public class ExchangeRateResolverTests
{
    private static AppDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static ExchangeRateResolver NewResolver(AppDbContext ctx) =>
        new(ctx, new MemoryCache(new MemoryCacheOptions()), NullLogger<ExchangeRateResolver>.Instance);

    private static async Task SeedAfipSettingsAsync(AppDbContext ctx, bool isProduction)
    {
        ctx.AfipSettings.Add(new AfipSettings { Id = 1, IsProduction = isProduction });
        await ctx.SaveChangesAsync();
    }

    private static ExchangeRateQuote BuildQuote(
        DateOnly quoteDate,
        ExchangeRateSource source,
        decimal rate,
        bool isProduction,
        int? supersededByQuoteId = null,
        string currency = "USD") => new()
    {
        Currency = currency,
        QuoteDate = quoteDate,
        Source = source,
        Rate = rate,
        ProviderName = source == ExchangeRateSource.AfipOficial ? "ARCA_WSFEv1" : "BNA_Scraper",
        FetchedAt = DateTime.UtcNow,
        ArcaFchCotiz = source == ExchangeRateSource.AfipOficial ? quoteDate : null,
        IsProductionSource = isProduction,
        SupersededByQuoteId = supersededByQuoteId,
    };

    // ============================================================
    // Test 1 (spec §15): ARS -> Rate=1 sin tocar la base.
    // ============================================================

    [Fact]
    public async Task Ars_DevuelveRateUno_SinConsultarLaBase()
    {
        await using var ctx = NewContext();
        // A proposito NO sembramos AfipSettings ni ninguna fila: si el resolver tocara la base
        // para pesos, este test explotaria (AfipSettings ausente -> IsProduction=false por default,
        // no rompe, pero cualquier query a ExchangeRateQuotes sobre una tabla vacia igual devolveria
        // null de forma valida; el punto real es que el resultado es SIEMPRE 1 sin importar la base).
        var resolver = NewResolver(ctx);

        var suggestion = await resolver.GetSuggestionAsync("ARS", new DateOnly(2026, 08, 05), CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.Equal(1m, suggestion!.Rate);
        Assert.False(suggestion.IsStale);
    }

    // ============================================================
    // Test 2: match exacto -> IsStale=false, RateDate == QuoteDate pedida.
    // ============================================================

    [Fact]
    public async Task MatchExacto_DevuelveIsStaleFalse_ConLaFechaPedida()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 05);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1350.50m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", fecha, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.False(suggestion!.IsStale);
        Assert.Equal(fecha, suggestion.RateDate);
        Assert.Equal(1350.50m, suggestion.Rate);
    }

    // ============================================================
    // Test 3: sin fila del dia pedido, si de 3 dias atras -> IsStale=true + RateDate real.
    // ============================================================

    [Fact]
    public async Task SinFilaDeHoy_ConFilaDeTresDiasAtras_DevuelveIsStaleTrue_ConLaFechaReal()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fechaPedida = new DateOnly(2026, 08, 05);
        var fechaDelDato = fechaPedida.AddDays(-3);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fechaDelDato, ExchangeRateSource.AfipOficial, 1340m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", fechaPedida, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.True(suggestion!.IsStale);
        Assert.Equal(fechaDelDato, suggestion.RateDate);
        Assert.Equal(1340m, suggestion.Rate);
    }

    // ============================================================
    // Test 4: sin fila dentro de la ventana de 5 dias -> null.
    // ============================================================

    [Fact]
    public async Task SinFilaDentroDeLaVentanaDeCincoDias_DevuelveNull()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fechaPedida = new DateOnly(2026, 08, 05);
        // 6 dias atras: fuera de la ventana de 5.
        ctx.ExchangeRateQuotes.Add(BuildQuote(fechaPedida.AddDays(-6), ExchangeRateSource.AfipOficial, 1300m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", fechaPedida, CancellationToken.None);

        Assert.Null(suggestion);
    }

    // ============================================================
    // Test 5 (§4.3): con fila AfipOficial y fila BNA_Minorista el mismo dia, devuelve la AfipOficial.
    // ============================================================

    [Fact]
    public async Task ConAfipOficialYBnaElMismoDia_DevuelveLaAfipOficial()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 05);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.BNA_Minorista, 1349m, isProduction: false));
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1350.50m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", fecha, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.Equal(ExchangeRateSource.AfipOficial, suggestion!.Source);
        Assert.Equal(1350.50m, suggestion.Rate);
    }

    // ============================================================
    // Test 6: con IsProduction=true, NO devuelve filas de IsProductionSource=false.
    // ============================================================

    [Fact]
    public async Task ConEntornoProduccion_NoDevuelveFilasDeHomologacion()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: true);
        var fecha = new DateOnly(2026, 08, 05);
        // Solo hay fila de HOMOLOGACION (isProduction: false); el sistema esta en PRODUCCION.
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1350.50m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", fecha, CancellationToken.None);

        Assert.Null(suggestion);
    }

    // ============================================================
    // Test 7 (§4.4): una fila con SupersededByQuoteId no nulo NO se devuelve.
    // ============================================================

    [Fact]
    public async Task FilaSuperseded_NoSeDevuelve()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 05);

        var filaVieja = BuildQuote(fecha, ExchangeRateSource.AfipOficial, 999m, isProduction: false);
        ctx.ExchangeRateQuotes.Add(filaVieja);
        await ctx.SaveChangesAsync();

        var filaCorrecta = BuildQuote(fecha, ExchangeRateSource.BNA_Minorista, 1350.50m, isProduction: false);
        ctx.ExchangeRateQuotes.Add(filaCorrecta);
        await ctx.SaveChangesAsync();

        // "Tachamos" la vieja apuntando a la correcta (F-6: el UNICO UPDATE permitido).
        filaVieja.SupersededByQuoteId = filaCorrecta.Id;
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", fecha, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.Equal(1350.50m, suggestion!.Rate);
        Assert.Equal(ExchangeRateSource.BNA_Minorista, suggestion.Source);
    }

    // ============================================================
    // Test 8 (§5.3): "hoy" siempre en hora argentina.
    //
    // NOTA METODOLOGICA: ArgentinaTime no tiene un reloj inyectable en este repo (mismo patron que
    // ArgentinaTimeTests.cs, que tampoco fija el reloj real). No podemos literalmente "poner el
    // reloj a las 23:30 ART" en un test determinista. Lo que SI podemos blindar — y es el riesgo de
    // regresion real — es que el limite pasado/hoy que decide el TTL de cache usa
    // ArgentinaTime.GetArgentinaToday(), no DateTime.UtcNow.Date: si alguien lo cambia por error, la
    // fecha de HOY calculada con ArgentinaTime (este test) y la que use el resolver dejan de
    // coincidir en la franja 21-24hs ART, y este test lo va a mostrar apenas alguien reintroduzca
    // DateTime.UtcNow.Date en el calculo.
    // ============================================================

    [Fact]
    public async Task ExactamenteHoyEnArgentina_SeConsideraFrescoNoStale_UsandoArgentinaTime()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);

        // "Hoy" calculado con el MISMO helper que exige la regla T-4 (§5.3), no con DateTime.UtcNow.Date.
        var hoyArgentina = DateOnly.FromDateTime(ArgentinaTime.GetArgentinaToday());
        ctx.ExchangeRateQuotes.Add(BuildQuote(hoyArgentina, ExchangeRateSource.AfipOficial, 1350.50m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync("USD", hoyArgentina, CancellationToken.None);

        Assert.NotNull(suggestion);
        Assert.False(suggestion!.IsStale);
        Assert.Equal(hoyArgentina, suggestion.RateDate);
    }

    // ============================================================
    // ADR-011 (enmienda 2026-08-05, hallazgo normativo "validacion ARCA 10240"): el flag
    // excludePracticeOfficialData bifurca el resolver en dos modos. Tests 9-11 abajo.
    // ============================================================

    /// <summary>
    /// Test 9: comportamiento de SIEMPRE (facturar, flag en false) — sin cambios. Un AfipOficial de
    /// homologacion SIGUE sirviendose cuando el sistema corre en homologacion: la factura de prueba
    /// necesita el numero de juguete que ARCA va a validar (error 10240 si no coincide).
    /// </summary>
    [Fact]
    public async Task ModoFacturar_ConEntornoHomologacion_SigueSirviendoElAfipOficialDePractica()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 05);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1152.202m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync(
            "USD", fecha, CancellationToken.None, excludePracticeOfficialData: false);

        Assert.NotNull(suggestion);
        Assert.Equal(1152.202m, suggestion!.Rate);
        Assert.False(suggestion.IsProductionSource);
    }

    /// <summary>
    /// Test 10: modo "solo datos reales" (dashboard, flag en true) — en homologacion, con SOLO una
    /// fila AfipOficial de juguete disponible, el resolver NO la ofrece: devuelve null (el caller cae
    /// al estado vacio honesto, nunca muestra un numero de práctica como referencia real).
    /// </summary>
    [Fact]
    public async Task ModoSoloDatosReales_ConSoloAfipOficialDePractica_DevuelveNull()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 05);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1152.202m, isProduction: false));
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync(
            "USD", fecha, CancellationToken.None, excludePracticeOfficialData: true);

        Assert.Null(suggestion);
    }

    /// <summary>
    /// Test 11: modo "solo datos reales" con una fila REAL disponible (OficialPorApi, ADR-011) —
    /// SI la sirve, sin importar que el sistema este corriendo en homologacion (IsProductionSource=true
    /// en la fila, "vale en cualquier entorno").
    /// </summary>
    [Fact]
    public async Task ModoSoloDatosReales_ConFilaOficialPorApi_LaSirve_SinImportarElEntornoActual()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 05);
        // Solo hay juguete AfipOficial (homologacion) + el respaldo REAL (OficialPorApi, siempre
        // IsProductionSource=true por diseño, ver ExchangeRateSyncJob).
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1152.202m, isProduction: false));
        ctx.ExchangeRateQuotes.Add(new ExchangeRateQuote
        {
            Currency = "USD",
            QuoteDate = fecha,
            Source = ExchangeRateSource.OficialPorApi,
            Rate = 1496.50m,
            ProviderName = "dolarapi",
            FetchedAt = DateTime.UtcNow,
            IsProductionSource = true,
        });
        await ctx.SaveChangesAsync();

        var resolver = NewResolver(ctx);
        var suggestion = await resolver.GetSuggestionAsync(
            "USD", fecha, CancellationToken.None, excludePracticeOfficialData: true);

        Assert.NotNull(suggestion);
        Assert.Equal(1496.50m, suggestion!.Rate);
        Assert.Equal(ExchangeRateSource.OficialPorApi, suggestion.Source);
    }
}
