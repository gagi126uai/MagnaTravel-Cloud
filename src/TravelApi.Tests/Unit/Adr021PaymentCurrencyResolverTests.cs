using System;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-021 Capa 4 (multimoneda + cobro cruzado, 2026-06-10): tests PUROS del resolver/validador del
/// bloque de moneda de un pago (§8.4/§8.5/§8.6 + formula §2.2bis). No usan EF.
///
/// <para>Cubre: (a) request sin datos de moneda = ARS no cruzado (regresion byte-identica); (b) pago
/// cruzado ARS->saldo USD calcula ImputedAmount = round2(Amount/TC); (c) caso simetrico USD->ARS;
/// (d) cruzado sin TC/fuente/fecha -> rechazado; (e) no cruzado con TC -> rechazado; (f) moneda no
/// soportada -> rechazada; (g) fuente Unset -> rechazada.</para>
/// </summary>
public class Adr021PaymentCurrencyResolverTests
{
    // Redondeo real del sistema (mismo que usa el registro de pago).
    private static decimal Round(decimal amount) => ReservationEconomicPolicy.RoundCurrency(amount);

    private static PaymentCurrencyResolver.Resolved Resolve(
        decimal amount,
        string? currency = null,
        string? imputedCurrency = null,
        decimal? exchangeRate = null,
        int? exchangeRateSource = null,
        DateTime? exchangeRateAt = null,
        decimal? imputedAmount = null)
        => PaymentCurrencyResolver.Resolve(
            amount, currency, imputedCurrency, exchangeRate, exchangeRateSource, exchangeRateAt, imputedAmount, Round);

    // ===================== (a) regresion: sin datos de moneda = ARS no cruzado =====================

    [Fact]
    public void NoCurrencyData_DefaultsToArsNonCross_WithNullExchangeBlock()
    {
        var result = Resolve(amount: 1000m);

        Assert.Equal(Monedas.ARS, result.Currency);
        Assert.Null(result.ImputedCurrency);
        Assert.Null(result.ExchangeRate);
        Assert.Null(result.ExchangeRateSource);
        Assert.Null(result.ExchangeRateAt);
        Assert.Null(result.ImputedAmount);
    }

    [Fact]
    public void SameCurrencyImputation_IsNotCross_ExchangeBlockStaysNull()
    {
        // Imputa USD a saldo USD: misma moneda = NO cruzado, bloque TC null.
        var result = Resolve(amount: 100m, currency: "USD", imputedCurrency: "USD");

        Assert.Equal(Monedas.USD, result.Currency);
        Assert.Null(result.ImputedCurrency);
        Assert.Null(result.ExchangeRate);
        Assert.Null(result.ImputedAmount);
    }

    // ===================== (b)(c) pago cruzado: formula §2.2bis =====================

    [Fact]
    public void CrossPayment_ArsToUsd_ImputedAmountIsAmountDividedByRate()
    {
        // Cliente paga ARS 100.000, imputa a saldo USD, TC 1000 ARS/USD -> 100 USD.
        var at = DateTime.UtcNow;
        var result = Resolve(
            amount: 100000m, currency: "ARS", imputedCurrency: "USD",
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Manual, exchangeRateAt: at);

        Assert.Equal(Monedas.ARS, result.Currency);
        Assert.Equal(Monedas.USD, result.ImputedCurrency);
        Assert.Equal(1000m, result.ExchangeRate);
        Assert.Equal(ExchangeRateSource.Manual, result.ExchangeRateSource);
        Assert.Equal(at, result.ExchangeRateAt);
        Assert.Equal(100m, result.ImputedAmount); // 100000 / 1000
    }

    [Fact]
    public void CrossPayment_UsdToArs_ImputedAmountIsAmountTimesRate()
    {
        var at = DateTime.UtcNow;
        var result = Resolve(
            amount: 100m, currency: "USD", imputedCurrency: "ARS",
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Manual, exchangeRateAt: at);

        Assert.Equal(100000m, result.ImputedAmount); // 100 * 1000
    }

    [Fact]
    public void CrossPayment_IgnoresFrontImputedAmount_AndRecomputes()
    {
        // El front manda un ImputedAmount mentiroso; el backend lo recalcula (no se confia en el front).
        var result = Resolve(
            amount: 100000m, currency: "ARS", imputedCurrency: "USD",
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Manual, exchangeRateAt: DateTime.UtcNow,
            imputedAmount: 999999m);

        Assert.Equal(100m, result.ImputedAmount);
    }

    // ===================== (d) cruzado sin datos de TC -> rechazado =====================

    [Fact]
    public void CrossPayment_MissingRate_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Resolve(
            amount: 100000m, currency: "ARS", imputedCurrency: "USD",
            exchangeRateSource: (int)ExchangeRateSource.Manual, exchangeRateAt: DateTime.UtcNow));
        Assert.Contains("tipo de cambio", ex.Message);
    }

    [Fact]
    public void CrossPayment_MissingSource_Throws()
    {
        Assert.Throws<ArgumentException>(() => Resolve(
            amount: 100000m, currency: "ARS", imputedCurrency: "USD",
            exchangeRate: 1000m, exchangeRateAt: DateTime.UtcNow));
    }

    [Fact]
    public void CrossPayment_MissingDate_Throws()
    {
        Assert.Throws<ArgumentException>(() => Resolve(
            amount: 100000m, currency: "ARS", imputedCurrency: "USD",
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Manual));
    }

    [Fact]
    public void CrossPayment_UnsetSource_Throws()
    {
        Assert.Throws<ArgumentException>(() => Resolve(
            amount: 100000m, currency: "ARS", imputedCurrency: "USD",
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Unset, exchangeRateAt: DateTime.UtcNow));
    }

    [Fact]
    public void CrossPayment_NonPositiveRate_Throws()
    {
        Assert.Throws<ArgumentException>(() => Resolve(
            amount: 100000m, currency: "ARS", imputedCurrency: "USD",
            exchangeRate: 0m, exchangeRateSource: (int)ExchangeRateSource.Manual, exchangeRateAt: DateTime.UtcNow));
    }

    // ===================== (e) no cruzado con TC -> rechazado =====================

    [Fact]
    public void NonCrossPayment_WithExchangeData_Throws()
    {
        var ex = Assert.Throws<ArgumentException>(() => Resolve(
            amount: 1000m, currency: "ARS", imputedCurrency: "ARS",
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Manual, exchangeRateAt: DateTime.UtcNow));
        Assert.Contains("misma moneda", ex.Message);
    }

    // ===================== (f) moneda no soportada -> rechazada =====================

    [Fact]
    public void UnsupportedPaymentCurrency_Throws()
    {
        Assert.Throws<ArgumentException>(() => Resolve(amount: 100m, currency: "EUR"));
    }

    [Fact]
    public void UnsupportedImputedCurrency_Throws()
    {
        Assert.Throws<ArgumentException>(() => Resolve(
            amount: 100m, currency: "ARS", imputedCurrency: "EUR",
            exchangeRate: 1000m, exchangeRateSource: (int)ExchangeRateSource.Manual, exchangeRateAt: DateTime.UtcNow));
    }
}
