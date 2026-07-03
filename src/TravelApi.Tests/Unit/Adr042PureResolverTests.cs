using System.Collections.Generic;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-042 §3.3.1 / §3.3.2 (2026-07-01): tests puros de los resolvers de moneda del saldo a favor y de
/// CanMisMonExt. Sin base ni servicios — es plata y datos fiscales, se blindan como funciones puras.
/// </summary>
public class Adr042PureResolverTests
{
    // ===== CanMisMonExtResolver =====

    [Theory]
    [InlineData("PES")]
    [InlineData("pes")]
    [InlineData(null)]
    [InlineData("")]
    public void CanMisMonExt_Pesos_o_vacio_devuelve_null(string? monId)
    {
        // Pesos / no informado: el nodo no aplica -> null (envelope byte-identico al historico).
        Assert.Null(CanMisMonExtResolver.Resolve(monId));
    }

    [Theory]
    [InlineData("DOL")]
    [InlineData("dol")]
    [InlineData("USD")]
    public void CanMisMonExt_Divisa_devuelve_N(string monId)
    {
        // Criterio firme para esta agencia: factura en divisa, cobra en pesos -> "N".
        Assert.Equal("N", CanMisMonExtResolver.Resolve(monId));
    }

    // ===== CreditAllocationCurrencyResolver.ResolveCreditCurrency =====

    [Fact]
    public void CreditCurrency_reembolso_coincide_con_obligacion_mintea_1a1_sin_revision()
    {
        // Caso normal (mono-factura misma moneda): USD reembolsado, obligacion USD -> consistente, 1:1.
        var decision = CreditAllocationCurrencyResolver.ResolveCreditCurrency(
            "USD", new List<string> { "USD" });

        Assert.False(decision.RequiresManualReview);
        Assert.Equal("USD", decision.Currency);
    }

    [Fact]
    public void CreditCurrency_reembolso_diverge_de_obligacion_va_a_revision_manual()
    {
        // Divergencia: operador reembolsa ARS pero la obligacion del cliente es USD -> NO mintear en la
        // moneda equivocada ni inventar TC -> revision manual.
        var decision = CreditAllocationCurrencyResolver.ResolveCreditCurrency(
            "ARS", new List<string> { "USD" });

        Assert.True(decision.RequiresManualReview);
    }

    [Fact]
    public void CreditCurrency_sin_obligaciones_imputadas_es_moneda_de_pago_sin_revision()
    {
        // Solo pagos a cuenta (sin obligacion imputada): a cuenta = moneda de pago, se mintea sin bloquear.
        var decision = CreditAllocationCurrencyResolver.ResolveCreditCurrency(
            "USD", new List<string>());

        Assert.False(decision.RequiresManualReview);
        Assert.Equal("USD", decision.Currency);
    }

    [Fact]
    public void CreditCurrency_multimoneda_coincide_con_alguna_obligacion_no_revisa()
    {
        // La reserva tiene obligaciones en USD y ARS; el reembolso ARS coincide con una -> OK.
        var decision = CreditAllocationCurrencyResolver.ResolveCreditCurrency(
            "ARS", new List<string> { "USD", "ARS" });

        Assert.False(decision.RequiresManualReview);
        Assert.Equal("ARS", decision.Currency);
    }
}
