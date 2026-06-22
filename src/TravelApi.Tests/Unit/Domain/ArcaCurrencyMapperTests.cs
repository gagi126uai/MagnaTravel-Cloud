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

    // ============== ToIso: inversa ARCA -> ISO (la usa el extracto/Estado de Cuenta) ==============
    // Cubre el cruce que hace AddInvoiceLines: Invoice.MonId viene en codigo ARCA ("PES"/"DOL") y el
    // libro mayor agrupa por ISO ("ARS"/"USD"). Si manana se renombra un codigo o se suma una moneda,
    // este test rojo lo atrapa antes de que una factura caiga en el bloque de moneda equivocado.

    [Theory]
    // PES -> ARS (case-insensitive: el snapshot fiscal podria venir en minuscula).
    [InlineData("PES", "ARS")]
    [InlineData("pes", "ARS")]
    [InlineData("Pes", "ARS")]
    // DOL -> USD.
    [InlineData("DOL", "USD")]
    [InlineData("dol", "USD")]
    [InlineData("Dol", "USD")]
    public void ToIso_KnownArcaCode_ReturnsIso(string arcaCode, string expectedIso)
    {
        Assert.Equal(expectedIso, ArcaCurrencyMapper.ToIso(arcaCode));
    }

    [Theory]
    [InlineData(null)]    // sin MonId (factura legacy): el caller decide el fallback (ARS).
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ARS")]   // ISO, NO codigo ARCA: ToIso espera "PES", no "ARS" -> no reconoce.
    [InlineData("USD")]   // idem: ISO en vez del codigo ARCA "DOL".
    [InlineData("EUR")]   // moneda no soportada.
    [InlineData("XYZ")]   // basura.
    public void ToIso_UnknownOrInvalid_ReturnsNull(string? arcaCode)
    {
        Assert.Null(ArcaCurrencyMapper.ToIso(arcaCode));
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
