using System;
using System.Globalization;
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
/// "Ayuda invisible del tipo de cambio" (spec firmada 2026-08-06, A5.7): el TECHO del dia.
///
/// <para>Que se protege aca, en criollo: cuando facturas en dolares no podes declarar cualquier
/// numero. Como maximo entra la cotizacion oficial del dia mas un peso; arriba de eso el comprobante
/// rebota. El motor calcula ese maximo y se lo pasa hecho a la pantalla — la pantalla JAMAS le suma un
/// peso a nada por su cuenta (regla T-13).</para>
/// </summary>
public class TcAyudaInvisibleCeilingTests
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
        DateOnly quoteDate, ExchangeRateSource source, decimal rate, bool isProduction) => new()
    {
        Currency = "USD",
        QuoteDate = quoteDate,
        Source = source,
        Rate = rate,
        ProviderName = source == ExchangeRateSource.AfipOficial ? "ARCA_WSFEv1" : "dolarapi",
        FetchedAt = DateTime.UtcNow,
        ArcaFchCotiz = source == ExchangeRateSource.AfipOficial ? quoteDate : null,
        IsProductionSource = isProduction,
    };

    // ============================================================
    // La regla pura: techo = cotizacion oficial + $1
    // ============================================================

    [Theory]
    [InlineData(1234.50, 1235.50)]
    [InlineData(1152.202, 1153.202)]
    [InlineData(1000, 1001)]
    public void ElTechoEsLaCotizacionOficialMasUnPeso(decimal oficial, decimal techoEsperado)
    {
        Assert.Equal(techoEsperado, ArcaInvoicingRateCeiling.FromOfficialRate(oficial));
    }

    /// <summary>
    /// El borde EXACTO importa: el techo entra (no lo supera), un centavo por encima ya no.
    /// </summary>
    [Fact]
    public void ElTechoJusto_Entra_YUnCentavoMas_NoEntra()
    {
        var techo = ArcaInvoicingRateCeiling.FromOfficialRate(1234.50m); // 1235.50

        Assert.False(ArcaInvoicingRateCeiling.ExceedsCeiling(1235.50m, techo));
        Assert.False(ArcaInvoicingRateCeiling.ExceedsCeiling(1234.50m, techo));
        Assert.True(ArcaInvoicingRateCeiling.ExceedsCeiling(1235.51m, techo));
    }

    // ============================================================
    // El techo que sirve el resolver
    // ============================================================

    [Fact]
    public async Task ConCotizacionOficialDelDia_DevuelveEsaCotizacionMasUnPeso()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 06);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1234.50m, isProduction: false));
        await ctx.SaveChangesAsync();

        var techo = await NewResolver(ctx).GetInvoicingCeilingAsync("USD", fecha, CancellationToken.None);

        Assert.Equal(1235.50m, techo);
    }

    /// <summary>
    /// El techo es una regla del organismo contra SU propio numero. Un dato del Banco Nacion o de una
    /// API publica NO sirve para calcularlo: si lo usaramos, podriamos bajarle el valor a una factura
    /// legitima. Sin cotizacion oficial conocida, no hay techo y nadie acomoda nada.
    /// </summary>
    [Fact]
    public async Task ConSoloDatosDeRespaldo_NoHayTecho()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 06);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.OficialPorApi, 1496.50m, isProduction: true));
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.BNA_Minorista, 1490m, isProduction: true));
        await ctx.SaveChangesAsync();

        var techo = await NewResolver(ctx).GetInvoicingCeilingAsync("USD", fecha, CancellationToken.None);

        Assert.Null(techo);
    }

    /// <summary>
    /// Sin cotización propia del día se acepta la del DÍA HÁBIL ANTERIOR, y nada más viejo.
    /// 2026-08-10 es lunes: el día hábil anterior es el viernes 2026-08-07.
    /// </summary>
    [Fact]
    public async Task SinCotizacionDelDia_AceptaLaDelDiaHabilAnterior()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        ctx.ExchangeRateQuotes.Add(
            BuildQuote(new DateOnly(2026, 08, 07), ExchangeRateSource.AfipOficial, 1200m, isProduction: false));
        await ctx.SaveChangesAsync();

        var techo = await NewResolver(ctx).GetInvoicingCeilingAsync(
            "USD", new DateOnly(2026, 08, 10), CancellationToken.None);

        Assert.Equal(1201m, techo);
    }

    /// <summary>
    /// HALLAZGO DE SEGURIDAD B2 (2026-08-06): el techo BAJA el número que el usuario declaró, en un
    /// comprobante que después no se puede deshacer. Con una cotización rancia (la libreta viene
    /// desactualizada porque el organismo no contestó hace días), acomodar contra ese dólar viejo
    /// le bajaría el valor a una factura legítima. Preferimos quedarnos SIN techo: el sistema no
    /// acomoda nada y se emite como siempre.
    ///
    /// <para>Antes acá se aceptaba hasta 5 días para atrás; eso ya no vale.</para>
    /// </summary>
    [Theory]
    [InlineData("2026-08-04")] // martes: el jueves anterior ya es demasiado viejo
    [InlineData("2026-08-03")]
    [InlineData("2026-07-01")]
    public async Task ConCotizacionRancia_NoHayTecho(string fechaDeLaCotizacion)
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        ctx.ExchangeRateQuotes.Add(BuildQuote(
            DateOnly.Parse(fechaDeLaCotizacion), ExchangeRateSource.AfipOficial, 1200m, isProduction: false));
        await ctx.SaveChangesAsync();

        // Jueves 2026-08-06: solo aceptaría una cotización del 6 o del 5.
        var techo = await NewResolver(ctx).GetInvoicingCeilingAsync(
            "USD", new DateOnly(2026, 08, 06), CancellationToken.None);

        Assert.Null(techo);
    }

    /// <summary>
    /// Piso de cordura (hallazgo N1, 2026-08-06): una cotización oficial guardada en 1 o menos es un
    /// dato corrupto, no una cotización. Sin este piso, el techo daría 2 y el motor le bajaría el tipo
    /// de cambio de una factura legítima a 2 pesos por dólar.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0.5)]
    public async Task ConCotizacionOficialAbsurda_NoHayTecho(decimal cotizacionCorrupta)
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);
        var fecha = new DateOnly(2026, 08, 06);
        ctx.ExchangeRateQuotes.Add(
            BuildQuote(fecha, ExchangeRateSource.AfipOficial, cotizacionCorrupta, isProduction: false));
        await ctx.SaveChangesAsync();

        var techo = await NewResolver(ctx).GetInvoicingCeilingAsync("USD", fecha, CancellationToken.None);

        Assert.Null(techo);
    }

    /// <summary>La ventana, sin base de por medio: lunes mira al viernes, el resto mira al día anterior.</summary>
    [Theory]
    [InlineData("2026-08-10", "2026-08-07")] // lunes  -> viernes
    [InlineData("2026-08-09", "2026-08-07")] // domingo -> viernes
    [InlineData("2026-08-08", "2026-08-07")] // sábado  -> viernes
    [InlineData("2026-08-06", "2026-08-05")] // jueves  -> miércoles
    public void LaVentanaDelTechoLlegaHastaElDiaHabilAnterior(string fechaFactura, string fechaMasVieja)
    {
        Assert.Equal(
            DateOnly.Parse(fechaMasVieja),
            ArcaInvoicingRateCeiling.EarliestAcceptableQuoteDate(DateOnly.Parse(fechaFactura)));
    }

    /// <summary>
    /// El numero de ensayo y el numero real no se mezclan: si el sistema esta emitiendo comprobantes
    /// reales, el techo NO puede salir de una cotizacion de ensayo (y al reves tampoco).
    /// </summary>
    [Fact]
    public async Task ElTechoRespetaDeDondeSaleElComprobante()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: true);
        var fecha = new DateOnly(2026, 08, 06);
        ctx.ExchangeRateQuotes.Add(BuildQuote(fecha, ExchangeRateSource.AfipOficial, 1152.202m, isProduction: false));
        await ctx.SaveChangesAsync();

        var techo = await NewResolver(ctx).GetInvoicingCeilingAsync("USD", fecha, CancellationToken.None);

        Assert.Null(techo);
    }

    [Fact]
    public async Task EnPesosNoHayTecho()
    {
        await using var ctx = NewContext();
        await SeedAfipSettingsAsync(ctx, isProduction: false);

        var techo = await NewResolver(ctx).GetInvoicingCeilingAsync(
            "ARS", new DateOnly(2026, 08, 06), CancellationToken.None);

        Assert.Null(techo);
    }
}
