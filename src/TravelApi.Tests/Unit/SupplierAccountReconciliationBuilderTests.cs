using System.Collections.Generic;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// Pasos B/C "cuenta del operador" (2026-06-29): cobertura del calculador PURO <see cref="SupplierAccountReconciliationBuilder"/>.
/// Prueba (a) la invariante estructural (X - Prepago == Econ + Y; X,Prepago >= 0; nunca ambos > 0) y (c) la matriz
/// de escenarios M-A..M-F + casos borde §2.4, con los esperados de X/Y/Prepago razonados a mano por negocio, NO por
/// la formula. Un bug de sourcing/gate/scope desviaria el resultado del esperado.
/// </summary>
public class SupplierAccountReconciliationBuilderTests
{
    private const string ARS = "ARS";
    private const string USD = "USD";

    private static SupplierCircuitLine Penalty(string currency, decimal amount) =>
        new(System.DateTime.UtcNow, SupplierAccountStatementLineKinds.PenaltyRetained,
            "Multa retenida por el operador", null, currency, amount, null);

    private static SupplierCircuitLine Refund(string currency, decimal amount) =>
        new(System.DateTime.UtcNow, SupplierAccountStatementLineKinds.RefundReceived,
            "Reembolso recibido del operador", null, currency, amount, null);

    private static SupplierAccountReconciliationCurrencyBlock Block(
        decimal cashClosing,
        IEnumerable<SupplierCircuitLine> circuit,
        decimal receivableY,
        string currency = ARS)
    {
        var result = SupplierAccountReconciliationBuilder.Build(
            new Dictionary<string, decimal> { [currency] = cashClosing },
            circuit,
            new Dictionary<string, decimal> { [currency] = receivableY });
        return Assert.Single(result.Currencies);
    }

    /// <summary>Invariante estructural (a): vale para CUALQUIER bloque producido por el calculador.</summary>
    private static void AssertStructuralInvariant(SupplierAccountReconciliationCurrencyBlock b)
    {
        Assert.True(b.ITheyOwe >= 0m, "X (le debo) nunca negativo");
        Assert.True(b.Prepayment >= 0m, "Prepago nunca negativo");
        Assert.True(b.ITheyOwe == 0m || b.Prepayment == 0m, "X y Prepago nunca ambos positivos");
        // X - Prepago == Econ + Y (definicion).
        Assert.Equal(b.EconomicClosingBalance + b.TheyOweMe, b.ITheyOwe - b.Prepayment);
    }

    // ============================================================
    // M-A: anulado pagado 1000, multa 300 confirmada, sin reembolso
    // ============================================================
    [Fact]
    public void M_A_paid1000_penalty300_noRefund()
    {
        // Caja = -1000 (pago vivo), multa retenida +300, Y = 1000-300 = 700.
        var b = Block(cashClosing: -1000m, circuit: new[] { Penalty(ARS, 300m) }, receivableY: 700m);

        Assert.Equal(-700m, b.EconomicClosingBalance); // -1000 + 300
        Assert.Equal(700m, b.TheyOweMe);
        Assert.Equal(0m, b.ITheyOwe);
        Assert.Equal(0m, b.Prepayment);
        AssertStructuralInvariant(b);
    }

    // ============================================================
    // M-B: lo anterior + reembolso 700 recibido -> cuenta en CERO
    // ============================================================
    [Fact]
    public void M_B_paid1000_penalty300_refund700_closesToZero()
    {
        var b = Block(cashClosing: -1000m, circuit: new[] { Penalty(ARS, 300m), Refund(ARS, 700m) }, receivableY: 0m);

        Assert.Equal(0m, b.EconomicClosingBalance); // -1000 + 300 + 700
        Assert.Equal(0m, b.TheyOweMe);
        Assert.Equal(0m, b.ITheyOwe);
        Assert.Equal(0m, b.Prepayment);
        AssertStructuralInvariant(b);
    }

    // ============================================================
    // M-C: coexisten deuda viva 500 + anulado pagado 1000 con multa 300 sin reembolsar
    // -> "Le debo 500" Y "Me tiene que devolver 700" a la vez, SIN netearse, Prepago 0 (no se mintea la fuga)
    // ============================================================
    [Fact]
    public void M_C_liveDebt500_andAnnulledPaid1000_penalty300_noNet_noPhantomCredit()
    {
        // Caja = +500 (compra viva) -1000 (pago) = -500; circuito multa +300; Y = 700.
        var b = Block(cashClosing: -500m, circuit: new[] { Penalty(ARS, 300m) }, receivableY: 700m);

        Assert.Equal(-200m, b.EconomicClosingBalance); // -500 + 300
        Assert.Equal(700m, b.TheyOweMe);
        Assert.Equal(500m, b.ITheyOwe);   // max(0, -200 + 700)
        Assert.Equal(0m, b.Prepayment);   // NO se mintea saldo a favor: el -500 de caja es "me tiene que devolver", no prepago
        AssertStructuralInvariant(b);
    }

    // ============================================================
    // M-D: sobrepago genuino sin anulacion (pague 1500 a un servicio vivo de 1000) -> Prepago 500
    // ============================================================
    [Fact]
    public void M_D_genuineOverpayment_noCancellation_isPrepayment()
    {
        var b = Block(cashClosing: -500m, circuit: new SupplierCircuitLine[0], receivableY: 0m);

        Assert.Equal(-500m, b.EconomicClosingBalance);
        Assert.Equal(0m, b.TheyOweMe);
        Assert.Equal(0m, b.ITheyOwe);
        Assert.Equal(500m, b.Prepayment); // saldo a favor consumible legitimo
        AssertStructuralInvariant(b);
    }

    // ============================================================
    // M-E: multi-operador = cuentas separadas (se prueba como dos bloques de moneda distintos no aplica;
    // aca se prueba que cada operador, modelado como su propio calculo, da su numero)
    // ============================================================
    [Fact]
    public void M_E_perOperator_separateAccounts()
    {
        // Operador P: pagado 1000, multa 300, sin reembolso.
        var p = Block(cashClosing: -1000m, circuit: new[] { Penalty(ARS, 300m) }, receivableY: 700m);
        Assert.Equal(0m, p.ITheyOwe);
        Assert.Equal(700m, p.TheyOweMe);

        // Operador Q: pagado 500, sin multa aplicada, sin reembolso.
        var q = Block(cashClosing: -500m, circuit: new SupplierCircuitLine[0], receivableY: 500m);
        Assert.Equal(0m, q.ITheyOwe);
        Assert.Equal(500m, q.TheyOweMe);
        Assert.Equal(0m, q.Prepayment);
    }

    // ============================================================
    // M-F: multimoneda (pago USD, reembolso en ARS) — los ledgers NO se netean entre monedas
    // ============================================================
    [Fact]
    public void M_F_multiCurrency_doesNotNetAcrossCurrencies()
    {
        // Pagado USD 1000, multa USD 300 -> Y_USD = 700. El operador manda ARS 700 (entra a su propio bloque ARS).
        var result = SupplierAccountReconciliationBuilder.Build(
            new Dictionary<string, decimal> { [USD] = -1000m },                // caja USD negativa por el pago
            new[] { Penalty(USD, 300m), Refund(ARS, 700m) },                    // multa USD + reembolso ARS
            new Dictionary<string, decimal> { [USD] = 700m });                  // receivable vive en USD (moneda del pago)

        var usd = Assert.Single(result.Currencies, c => c.Currency == USD);
        Assert.Equal(-700m, usd.EconomicClosingBalance); // -1000 + 300 (el reembolso ARS NO reduce el receivable USD)
        Assert.Equal(700m, usd.TheyOweMe);
        Assert.Equal(0m, usd.ITheyOwe);

        var ars = Assert.Single(result.Currencies, c => c.Currency == ARS);
        Assert.Equal(700m, ars.EconomicClosingBalance); // el reembolso ARS entra a su propio bloque
        Assert.Equal(0m, ars.TheyOweMe);
        Assert.Equal(700m, ars.ITheyOwe);               // "le debo 700 ARS" (el operador devolvio de mas en ARS)
        AssertStructuralInvariant(usd);
        AssertStructuralInvariant(ars);
    }

    // ============================================================
    // §2.4-a: sobrepague-y-anule (excedente costo-cap) -> parte Y=1000 (por cobrar) + Prepago=500 (consumible)
    // ============================================================
    [Fact]
    public void Edge_overpaidThenAnnulled_splitsReceivableAndPrepayment()
    {
        // Pague 1500 a un servicio de costo 1000, anulado, sin multa, sin reembolso. RefundCap topea en costo: Y=1000.
        var b = Block(cashClosing: -1500m, circuit: new SupplierCircuitLine[0], receivableY: 1000m);

        Assert.Equal(-1500m, b.EconomicClosingBalance);
        Assert.Equal(1000m, b.TheyOweMe);  // por cobrar (hasta el costo)
        Assert.Equal(0m, b.ITheyOwe);
        Assert.Equal(500m, b.Prepayment);  // el excedente costo-cap = saldo a favor consumible (decision del dueño)
        AssertStructuralInvariant(b);
    }

    // ============================================================
    // §2.4-b: sobre-reembolso (el operador devuelve mas que el cap) -> "Le debo 100"
    // ============================================================
    [Fact]
    public void Edge_overRefund_becomesITheyOwe()
    {
        // Pague 1000, multa 300 -> cap 700, pero el operador devuelve 800. Y se clampea a 0; reembolso recibido 800.
        var b = Block(cashClosing: -1000m, circuit: new[] { Penalty(ARS, 300m), Refund(ARS, 800m) }, receivableY: 0m);

        Assert.Equal(100m, b.EconomicClosingBalance); // -1000 + 300 + 800
        Assert.Equal(0m, b.TheyOweMe);
        Assert.Equal(100m, b.ITheyOwe);   // el operador devolvio 100 de mas: la agencia se los debe
        Assert.Equal(0m, b.Prepayment);
        AssertStructuralInvariant(b);
    }

    // ============================================================
    // Sin movimientos de circuito: colapsa al comportamiento de caja puro
    // ============================================================
    [Fact]
    public void NoCircuit_collapsesToCash()
    {
        var debt = Block(cashClosing: 800m, circuit: new SupplierCircuitLine[0], receivableY: 0m);
        Assert.Equal(800m, debt.ITheyOwe);
        Assert.Equal(0m, debt.Prepayment);
        Assert.Equal(0m, debt.TheyOweMe);
        AssertStructuralInvariant(debt);
    }

    [Fact]
    public void EmptyInputs_produceNoBlocks()
    {
        var result = SupplierAccountReconciliationBuilder.Build(
            new Dictionary<string, decimal>(), new SupplierCircuitLine[0], new Dictionary<string, decimal>());
        Assert.Empty(result.Currencies);
    }
}
