using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Domain.Reservations;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// TANDA 1 (cuenta corriente del proveedor): cobertura PURA del builder que arma el EXTRACTO de la Cuenta por
/// Pagar como libro mayor estilo banco. El builder recibe lineas planas YA clasificadas (compra confirmada =
/// cargo, pago al operador = abono) y calcula el saldo corriente por moneda. No toca EF: sin Postgres.
///
/// <para><b>INVARIANTE CLAVE</b> (observacion del reviewer): el saldo de cierre del extracto por moneda DEBE
/// ser EXACTAMENTE igual al <c>SupplierBalanceByCurrency.Balance</c> de esa moneda. Como la proyeccion
/// materializa el Balance corriendo <see cref="SupplierDebtCalculator.Calculate"/> sobre los MISMOS insumos
/// (las mismas compras confirmadas y los mismos pagos), probar "builder == Calculate" prueba "builder ==
/// proyeccion". Los tests construyen ambos lados desde los MISMOS arrays para que no haya forma de divergir.</para>
/// </summary>
public class SupplierAccountStatementBuilderTests
{
    private static readonly DateTime PurchaseDate = new(2026, 1, 1);
    private static readonly DateTime PaymentDate = new(2026, 1, 2);

    private static SupplierDebtCalculator.ConfirmedPurchase Purchase(string? currency, decimal netCost)
        => new(currency, netCost);

    private static SupplierDebtCalculator.SupplierPaymentInput Payment(
        decimal amount, string? currency = null, string? imputedCurrency = null, decimal? imputedAmount = null)
        => new(amount, currency, imputedCurrency, imputedAmount);

    /// <summary>
    /// Construye el extracto a partir de los MISMOS insumos canonicos (compras + pagos) que consume el
    /// calculador de deuda, usando las factories del builder (que derivan moneda/monto del abono con la misma
    /// primitiva de imputacion). Asi el extracto y el calculo de deuda parten del mismo lugar.
    /// </summary>
    private static SupplierAccountStatement BuildStatement(
        IEnumerable<SupplierDebtCalculator.ConfirmedPurchase> purchases,
        IEnumerable<SupplierDebtCalculator.SupplierPaymentInput> payments)
    {
        var lines = new List<SupplierAccountStatementInputLine>();

        foreach (var purchase in purchases)
        {
            lines.Add(SupplierAccountStatementBuilder.PurchaseLine(
                date: PurchaseDate, description: "compra", documentRef: null,
                currency: purchase.Currency, netCost: purchase.NetCost, sourcePublicId: null));
        }

        foreach (var payment in payments)
        {
            lines.Add(SupplierAccountStatementBuilder.PaymentLine(
                date: PaymentDate, description: "pago", documentRef: null,
                payment: payment, sourcePublicId: null));
        }

        return SupplierAccountStatementBuilder.Build(lines);
    }

    /// <summary>
    /// Afirma el invariante para un escenario: por CADA moneda del calculo de deuda existe un bloque del
    /// extracto con el mismo saldo de cierre, y no hay bloques de mas. Esto es lo que garantiza que el
    /// extracto cierre exactamente con SupplierBalanceByCurrency.
    /// </summary>
    private static void AssertStatementClosesToDebt(
        IEnumerable<SupplierDebtCalculator.ConfirmedPurchase> purchases,
        IEnumerable<SupplierDebtCalculator.SupplierPaymentInput> payments)
    {
        var purchaseList = purchases.ToList();
        var paymentList = payments.ToList();

        var debtByCurrency = SupplierDebtCalculator.Calculate(purchaseList, paymentList);
        var statement = BuildStatement(purchaseList, paymentList);

        // Mismo conjunto de monedas: ni de mas ni de menos.
        Assert.Equal(debtByCurrency.Count, statement.Currencies.Count);

        foreach (var (currency, debtLine) in debtByCurrency)
        {
            var block = statement.Currencies.Single(b => b.Currency == currency);
            // El nucleo del invariante: cierre del extracto == Balance de la proyeccion, por moneda.
            Assert.Equal(debtLine.Balance, block.ClosingBalance);
        }
    }

    // ===================== Solo ARS =====================

    [Fact]
    public void Invariante_SoloArs_CierreIgualaBalance()
    {
        var purchases = new[] { Purchase("ARS", 100000m), Purchase("ARS", 50000m) };
        var payments = new[] { Payment(70000m, "ARS") };

        AssertStatementClosesToDebt(purchases, payments);

        // Control explicito: 150k comprado - 70k pagado = 80k de deuda.
        var statement = BuildStatement(purchases, payments);
        Assert.Equal(80000m, Assert.Single(statement.Currencies).ClosingBalance);
    }

    // ===================== Solo USD =====================

    [Fact]
    public void Invariante_SoloUsd_CierreIgualaBalance()
    {
        var purchases = new[] { Purchase("USD", 1200m) };
        var payments = new[] { Payment(500m, "USD"), Payment(300m, "USD") };

        AssertStatementClosesToDebt(purchases, payments);

        var statement = BuildStatement(purchases, payments);
        var block = Assert.Single(statement.Currencies);
        Assert.Equal("USD", block.Currency);
        Assert.Equal(400m, block.ClosingBalance); // 1200 - 800
    }

    // ===================== ARS y USD en simultaneo: cada moneda cierra por separado =====================

    [Fact]
    public void Invariante_ArsYUsd_CadaMonedaCierraPorSeparado()
    {
        var purchases = new[]
        {
            Purchase("ARS", 100000m),
            Purchase("USD", 1000m),
        };
        var payments = new[]
        {
            Payment(40000m, "ARS"),
            Payment(600m, "USD"),
        };

        AssertStatementClosesToDebt(purchases, payments);

        var statement = BuildStatement(purchases, payments);
        Assert.Equal(2, statement.Currencies.Count);
        // Orden alfabetico estable: ARS, luego USD.
        Assert.Equal("ARS", statement.Currencies[0].Currency);
        Assert.Equal(60000m, statement.Currencies[0].ClosingBalance);  // 100k - 40k
        Assert.Equal("USD", statement.Currencies[1].Currency);
        Assert.Equal(400m, statement.Currencies[1].ClosingBalance);    // 1000 - 600

        // La plata NO cruza: cada bloque solo tiene lineas de su propia moneda.
        Assert.All(statement.Currencies[0].Lines, line => Assert.Equal("ARS", line.Currency));
        Assert.All(statement.Currencies[1].Lines, line => Assert.Equal("USD", line.Currency));
    }

    // ===================== Sobrepago = saldo a favor (cierre negativo) =====================

    [Fact]
    public void Invariante_Sobrepago_CierreNegativoIgualaBalance()
    {
        // Le pagamos de mas al operador en USD: compra 1000, pago 1500 -> saldo a favor 500 (cierre -500).
        var purchases = new[] { Purchase("USD", 1000m) };
        var payments = new[] { Payment(1500m, "USD") };

        AssertStatementClosesToDebt(purchases, payments);

        var statement = BuildStatement(purchases, payments);
        var block = Assert.Single(statement.Currencies);
        Assert.Equal(-500m, block.ClosingBalance); // negativo = saldo a favor de la agencia
    }

    // ===================== Pago cruzado USD->ARS cae en el bloque imputado =====================

    [Fact]
    public void Invariante_PagoCruzado_CaeEnMonedaImputada_CierreIgualaBalance()
    {
        // Deuda en ARS (compra 100k). Pago CRUZADO: salieron 50 USD de caja, imputados a ARS como 50000.
        var purchases = new[] { Purchase("ARS", 100000m) };
        var payments = new[]
        {
            Payment(amount: 50m, currency: "USD", imputedCurrency: "ARS", imputedAmount: 50000m),
        };

        AssertStatementClosesToDebt(purchases, payments);

        var statement = BuildStatement(purchases, payments);
        // El pago cruzado NO crea un bloque USD: abona la deuda ARS por su equivalente imputado.
        var block = Assert.Single(statement.Currencies);
        Assert.Equal("ARS", block.Currency);
        Assert.Equal(50000m, block.ClosingBalance); // 100k - 50k imputado
    }

    // ===================== Anticipo a cuenta sin compra: solo abono, cierre negativo =====================

    [Fact]
    public void Invariante_AnticipoSinCompra_CierreNegativoIgualaBalance()
    {
        // Anticipo/seña al operador sin compra confirmada todavia: solo hay un abono -> saldo a favor.
        var purchases = Array.Empty<SupplierDebtCalculator.ConfirmedPurchase>();
        var payments = new[] { Payment(80000m, "ARS") };

        AssertStatementClosesToDebt(purchases, payments);

        var statement = BuildStatement(purchases, payments);
        var block = Assert.Single(statement.Currencies);
        Assert.Equal(-80000m, block.ClosingBalance);
    }

    // ===================== Mezcla completa: ARS con sobrepago + USD con deuda + cruzado =====================

    [Fact]
    public void Invariante_EscenarioMixto_CadaMonedaCierraConSuBalance()
    {
        var purchases = new[]
        {
            Purchase("ARS", 100000m),
            Purchase("ARS", 20000m),
            Purchase("USD", 1000m),
        };
        var payments = new[]
        {
            Payment(150000m, "ARS"),                                             // ARS sobrepagado
            Payment(300m, "USD"),                                                // USD parcial
            Payment(amount: 200m, currency: "USD", imputedCurrency: "ARS", imputedAmount: 1000m), // cruzado a ARS
        };

        AssertStatementClosesToDebt(purchases, payments);

        // ARS: compras 120000, pagos 150000 + 1000 (cruzado) = 151000 -> cierre -31000 (saldo a favor).
        // USD: compras 1000, pagos 300 -> cierre 700.
        var statement = BuildStatement(purchases, payments);
        var ars = statement.Currencies.Single(b => b.Currency == "ARS");
        var usd = statement.Currencies.Single(b => b.Currency == "USD");
        Assert.Equal(-31000m, ars.ClosingBalance);
        Assert.Equal(700m, usd.ClosingBalance);
    }

    // ===================== Mecanica del libro mayor: orden, signos y saldo corriente =====================

    [Fact]
    public void LibroMayor_OrdenaPorFecha_SignosYSaldoCorriente()
    {
        // Llegan desordenadas a proposito: el builder debe ordenarlas por fecha.
        var d = new DateTime(2026, 3, 1);
        var lines = new[]
        {
            SupplierAccountStatementBuilder.PaymentLine(d.AddDays(3), "pago", null, Payment(30000m, "ARS"), null),
            SupplierAccountStatementBuilder.PurchaseLine(d.AddDays(1), "compra", null, "ARS", 100000m, null),
            SupplierAccountStatementBuilder.PaymentLine(d.AddDays(2), "pago", null, Payment(50000m, "ARS"), null),
        };

        var statement = SupplierAccountStatementBuilder.Build(lines);
        var block = Assert.Single(statement.Currencies);

        Assert.Equal(3, block.Lines.Count);
        // Compra primero (cargo), luego los dos pagos (abonos).
        Assert.Equal(SupplierAccountStatementLineKinds.Purchase, block.Lines[0].Kind);
        Assert.Equal(100000m, block.Lines[0].RunningBalance);   // +100k
        Assert.Equal(50000m, block.Lines[1].RunningBalance);    // 100k - 50k
        Assert.Equal(20000m, block.Lines[2].RunningBalance);    // 50k - 30k
        Assert.Equal(20000m, block.ClosingBalance);
    }

    // ===================== Empate de fecha: la compra (entrada antes) queda primero =====================

    [Fact]
    public void MismaFecha_PreservaOrdenDeEntrada()
    {
        var d = new DateTime(2026, 4, 1);
        var statement = SupplierAccountStatementBuilder.Build(new[]
        {
            SupplierAccountStatementBuilder.PurchaseLine(d, "compra", null, "ARS", 100000m, null),
            SupplierAccountStatementBuilder.PaymentLine(d, "pago", null, Payment(40000m, "ARS"), null),
        });
        var block = Assert.Single(statement.Currencies);

        Assert.Equal(SupplierAccountStatementLineKinds.Purchase, block.Lines[0].Kind);
        Assert.Equal(100000m, block.Lines[0].RunningBalance);
        Assert.Equal(60000m, block.Lines[1].RunningBalance);
    }

    // ===================== Vacio: sin lineas, sin bloques =====================

    [Fact]
    public void EntradaVacia_SinBloques()
    {
        var statement = SupplierAccountStatementBuilder.Build(Array.Empty<SupplierAccountStatementInputLine>());
        Assert.Empty(statement.Currencies);
    }

    // ===================== SourcePublicId se arrastra hasta el resultado =====================

    [Fact]
    public void SourcePublicId_SeArrastra_PorLinea()
    {
        var d = new DateTime(2026, 5, 1);
        var serviceId = Guid.NewGuid();
        var paymentId = Guid.NewGuid();

        var statement = SupplierAccountStatementBuilder.Build(new[]
        {
            SupplierAccountStatementBuilder.PurchaseLine(d, "compra", null, "ARS", 100000m, serviceId),
            SupplierAccountStatementBuilder.PaymentLine(d.AddDays(1), "pago", null, Payment(40000m, "ARS"), paymentId),
        });
        var block = Assert.Single(statement.Currencies);

        Assert.Equal(serviceId, block.Lines[0].SourcePublicId);
        Assert.Equal(paymentId, block.Lines[1].SourcePublicId);
    }
}
