using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit.Domain;

/// <summary>
/// FC1.3.F2.5 (multimoneda, 2026-05-28): tests del helper <see cref="ArcaCurrencyMapper"/>,
/// la fuente de verdad UNICA del catalogo de monedas soportadas para emitir NC parcial al ARCA.
///
/// <para>Estos tests son la red de seguridad del catalogo: si manana alguien suma EUR a
/// <see cref="ArcaCurrencyMapper.TryMap"/> sin homologar, o renombra un codigo ARCA, un test rojo
/// aca lo atrapa antes de que la NC rebote en produccion. Corren sin DB (helper puro estatico).</para>
/// </summary>
public class ArcaCurrencyMapperTests
{
    // ============== TryMap: monedas soportadas ==============

    [Theory]
    // ARS -> PES (peso argentino en el catalogo ARCA).
    [InlineData("ARS", "PES")]
    [InlineData("ars", "PES")]
    [InlineData("Ars", "PES")]
    // USD -> DOL (dolar en el catalogo ARCA, NO "USD").
    [InlineData("USD", "DOL")]
    [InlineData("usd", "DOL")]
    [InlineData("Usd", "DOL")]
    public void TryMap_SupportedCurrency_ReturnsArcaCode(string isoCurrency, string expectedArcaCode)
    {
        var result = ArcaCurrencyMapper.TryMap(isoCurrency);
        Assert.Equal(expectedArcaCode, result);
    }

    // ============== TryMap: monedas NO soportadas / entradas invalidas ==============

    [Theory]
    [InlineData("EUR")]   // euro: todavia no homologado contra ARCA.
    [InlineData("BRL")]   // real brasileno: idem.
    [InlineData("GBP")]
    [InlineData("DOL")]   // codigo ARCA, NO ISO — no debe mapear (el input es ISO).
    [InlineData("PES")]   // idem: el input esperado es ISO ("ARS"), no el codigo ARCA.
    [InlineData("XYZ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryMap_UnsupportedOrInvalid_ReturnsNull(string? isoCurrency)
    {
        var result = ArcaCurrencyMapper.TryMap(isoCurrency);
        Assert.Null(result);
    }

    // ============== IsSupported: azucar de lectura para los guards ==============

    [Theory]
    [InlineData("ARS", true)]
    [InlineData("USD", true)]
    [InlineData("usd", true)]
    [InlineData("EUR", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported_MatchesTryMapResult(string? isoCurrency, bool expected)
    {
        Assert.Equal(expected, ArcaCurrencyMapper.IsSupported(isoCurrency));
    }
}
