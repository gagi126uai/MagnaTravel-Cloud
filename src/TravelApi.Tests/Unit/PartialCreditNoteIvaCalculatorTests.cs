using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.F2.2 (plan tactico Fase 2 §FC1.3.F2.2 punto 5, 2026-05-27 + fix de semantica BRUTO
/// 2026-05-28): tests unit del prorrateo de IVA de la NC parcial.
///
/// <para><b>SEMANTICA DE <c>line.Total</c> que asumen estos tests (decision Gaston
/// 2026-05-28)</b>: el <c>Total</c> es BRUTO (incluye IVA por dentro). El calculator EXTRAE
/// el IVA: <c>BaseImp = round(bruto / (1+tasa), 2)</c>, <c>IVA = bruto - BaseImp</c>.</para>
///
/// <para><b>Sin DB, sin async, sin TestContainers</b>: <see cref="PartialCreditNoteIvaCalculator"/>
/// es un helper puro. Cada test arma el input en memoria y verifica numeros. Corren en
/// milisegundos sin Docker — por eso son unit y no integration.</para>
///
/// <para>Cubre los casos del plan §FC1.3.F2.2 punto 5:
/// <list type="bullet">
///   <item>ProportionalToNet con 1 sola alicuota (21%, 10.5%, 0%).</item>
///   <item>ProportionalToNet con 2 alicuotas mezcladas (10.5% + 21%).</item>
///   <item>ProportionalToNet con 2 lineas misma alicuota.</item>
///   <item>PerItem con la misma config.</item>
///   <item>Invariante de extraccion (BaseImp + IVA == bruto a centavo exacto).</item>
///   <item>Caso que excede tolerancia -&gt; throw.</item>
///   <item>Guards de input invalido (null, sin lineas, tolerancia negativa).</item>
/// </list>
/// </para>
/// </summary>
public class PartialCreditNoteIvaCalculatorTests
{
    // ============================================================
    // Codigos de alicuota ARCA usados en los tests (ver InvoiceItem.AlicuotaIvaId):
    //   3 = 0%, 4 = 10.5%, 5 = 21%.
    // Constantes nombradas para que cada test se lea sin tener que recordar la tabla.
    // ============================================================
    private const int AlicuotaCero = 3;       // 0%
    private const int AlicuotaDiezYMedio = 4;  // 10.5%
    private const int AlicuotaVeintiuno = 5;   // 21%

    private const decimal DefaultTolerance = 0.01m;

    /// <summary>
    /// Helper: arma un <see cref="PartialCreditNoteEmissionInput"/> con las lineas dadas y
    /// un <c>FiscalAmountToCredit</c> explicito. Los montos originales de la factura no los
    /// usa el calculator (solo los valida el job upstream), asi que aca van con valores
    /// coherentes pero no relevantes al test.
    /// </summary>
    private static PartialCreditNoteEmissionInput InputWith(
        decimal fiscalAmountToCredit,
        params PartialCreditNoteLineDto[] lines)
    {
        // El campo OriginalTotalAmount lo seteamos a la suma de los brutos para mantener un
        // input coherente (la suma de las lineas brutas = monto bruto a acreditar).
        decimal sumBruto = lines.Sum(line => line.Total);
        return new PartialCreditNoteEmissionInput(
            OriginalNetAmount: sumBruto, // no lo usa el calculator
            OriginalVatAmount: 0m,
            OriginalTotalAmount: sumBruto,
            FiscalAmountToCredit: fiscalAmountToCredit,
            Currency: "ARS",
            ExchangeRateAtOriginalInvoice: 1m,
            Lines: lines);
    }

    private static PartialCreditNoteLineDto Line(
        decimal total,
        int alicuotaIvaId,
        string description = "Item")
        => new(
            Description: description,
            Quantity: 1m,
            UnitPrice: total,
            Total: total,
            AlicuotaIvaId: alicuotaIvaId);

    // ============================================================
    // ProportionalToNet — 1 sola alicuota
    // ============================================================

    [Fact]
    public void Calculate_ProportionalToNet_SingleAlicuota21_ExtractsVatFromGross()
    {
        // Una sola linea BRUTA $121.000 a 21%.
        //   BaseImp = round(121000 / 1.21, 2) = 100.000.
        //   IVA = 121000 - 100000 = 21.000.
        //   CreditedTotal = bruto = 121.000.
        var input = InputWith(
            fiscalAmountToCredit: 121_000m,
            Line(total: 121_000m, alicuotaIvaId: AlicuotaVeintiuno));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(100_000m, result.CreditedNetAmount);
        Assert.Equal(21_000m, result.CreditedVatAmount);
        Assert.Equal(121_000m, result.CreditedTotalAmount);

        var group = Assert.Single(result.VatGroups);
        Assert.Equal(AlicuotaVeintiuno, group.AlicuotaIvaId);
        Assert.Equal(100_000m, group.BaseImponible);
        Assert.Equal(21_000m, group.ImporteIva);
        // Invariante de extraccion: BaseImp + IVA == bruto del grupo, gap 0.
        Assert.Equal(121_000m, group.BaseImponible + group.ImporteIva);
    }

    [Fact]
    public void Calculate_ProportionalToNet_SingleAlicuota105_ExtractsVatFromGross()
    {
        // Bruto $55.250 a 10.5%.
        //   BaseImp = round(55250 / 1.105, 2) = round(50000, 2) = 50.000.
        //   IVA = 55250 - 50000 = 5.250.
        var input = InputWith(
            fiscalAmountToCredit: 55_250m,
            Line(total: 55_250m, alicuotaIvaId: AlicuotaDiezYMedio));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(50_000m, result.CreditedNetAmount);
        Assert.Equal(5_250m, result.CreditedVatAmount);
        Assert.Equal(55_250m, result.CreditedTotalAmount);
    }

    [Fact]
    public void Calculate_ProportionalToNet_AlicuotaCero_ProducesNoVat()
    {
        // Alicuota 0% (codigo 3): la extraccion no toca el bruto.
        //   BaseImp = round(80000 / (1+0), 2) = 80.000. IVA = 80000 - 80000 = 0.
        var input = InputWith(
            fiscalAmountToCredit: 80_000m,
            Line(total: 80_000m, alicuotaIvaId: AlicuotaCero));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(80_000m, result.CreditedNetAmount);
        Assert.Equal(0m, result.CreditedVatAmount);
        Assert.Equal(80_000m, result.CreditedTotalAmount);
    }

    [Fact]
    public void Calculate_ProportionalToNet_SameAlicuotaTwoLines_GroupsAndExtractsOnTotal()
    {
        // Dos lineas BRUTAS a 21% colapsan en UN solo grupo de alicuota.
        //   Linea A 12.100 + Linea B 24.200 = 36.300 bruto del grupo.
        //   BaseImp = round(36300 / 1.21, 2) = 30.000.
        //   IVA = 36300 - 30000 = 6.300.
        var input = InputWith(
            fiscalAmountToCredit: 36_300m,
            Line(total: 12_100m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea A"),
            Line(total: 24_200m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea B"));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        var group = Assert.Single(result.VatGroups);
        Assert.Equal(30_000m, group.BaseImponible);
        Assert.Equal(6_300m, group.ImporteIva);
        Assert.Equal(36_300m, result.CreditedTotalAmount);
    }

    // ============================================================
    // ProportionalToNet — 2 alicuotas mezcladas (10.5% + 21%)
    // ============================================================

    [Fact]
    public void Calculate_ProportionalToNet_TwoAlicuotas_ExtractsPerGroupAndSums()
    {
        // Linea 21%: BRUTO $121.000 -> BaseImp 100.000, IVA 21.000.
        // Linea 10.5%: BRUTO $44.200 -> BaseImp round(44200/1.105, 2) = 40.000, IVA 4.200.
        // Bruto total $165.200 = neto $140.000 + IVA $25.200.
        var input = InputWith(
            fiscalAmountToCredit: 165_200m,
            Line(total: 121_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Aereo 21%"),
            Line(total: 44_200m, alicuotaIvaId: AlicuotaDiezYMedio, description: "Hotel 10.5%"));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(140_000m, result.CreditedNetAmount);
        Assert.Equal(25_200m, result.CreditedVatAmount);
        Assert.Equal(165_200m, result.CreditedTotalAmount);

        Assert.Equal(2, result.VatGroups.Count);

        var grupo21 = result.VatGroups.Single(g => g.AlicuotaIvaId == AlicuotaVeintiuno);
        Assert.Equal(100_000m, grupo21.BaseImponible);
        Assert.Equal(21_000m, grupo21.ImporteIva);
        // Invariante: BaseImp + IVA == bruto del grupo.
        Assert.Equal(121_000m, grupo21.BaseImponible + grupo21.ImporteIva);

        var grupo105 = result.VatGroups.Single(g => g.AlicuotaIvaId == AlicuotaDiezYMedio);
        Assert.Equal(40_000m, grupo105.BaseImponible);
        Assert.Equal(4_200m, grupo105.ImporteIva);
        Assert.Equal(44_200m, grupo105.BaseImponible + grupo105.ImporteIva);
    }

    // ============================================================
    // PerItem — misma config
    // ============================================================

    [Fact]
    public void Calculate_PerItem_TwoAlicuotas_MatchesProportionalWhenNoRoundingDrift()
    {
        // Mismos numeros redondos que el test proporcional de 2 alicuotas: como no hay
        // centavos partidos en la division por (1+tasa), PerItem y ProportionalToNet deben
        // dar identico.
        var lines = new[]
        {
            Line(total: 121_000m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 44_200m, alicuotaIvaId: AlicuotaDiezYMedio),
        };
        var input = InputWith(fiscalAmountToCredit: 165_200m, lines);

        var perItem = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.PerItem, DefaultTolerance);
        var proportional = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(proportional.CreditedNetAmount, perItem.CreditedNetAmount);
        Assert.Equal(proportional.CreditedVatAmount, perItem.CreditedVatAmount);
        Assert.Equal(proportional.CreditedTotalAmount, perItem.CreditedTotalAmount);
    }

    [Fact]
    public void Calculate_PerItem_ExtractsAndRoundsEachLineSeparately()
    {
        // Dos lineas BRUTAS a 21% donde la extraccion por linea produce un redondeo per item
        // que difiere del que daria sumar y extraer al grupo. Numeros:
        //   Bruto por linea: 121.01.
        //   Por linea: BaseImp = round(121.01 / 1.21, 2) = round(100.008264, 2) = 100.01.
        //              IVA = 121.01 - 100.01 = 21.00.
        //   Dos lineas: BaseImp acumulado 200.02, IVA acumulado 42.00.
        //   Bruto total = 242.02.
        // (En el modo proporcional al grupo: BaseImp = round(242.02/1.21, 2) = round(200.0165..., 2) = 200.02,
        //  IVA = 242.02 - 200.02 = 42.00. Coinciden en este caso "limpio"; el modo PerItem
        //  igual REDONDEA por linea, que es lo que importa documentar.)
        var input = InputWith(
            fiscalAmountToCredit: 242.02m,
            Line(total: 121.01m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea 1"),
            Line(total: 121.01m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea 2"));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.PerItem, DefaultTolerance);

        Assert.Equal(200.02m, result.CreditedNetAmount);
        Assert.Equal(42.00m, result.CreditedVatAmount);
        Assert.Equal(242.02m, result.CreditedTotalAmount);
    }

    // ============================================================
    // Invariante de extraccion (BaseImp + IVA == bruto, gap 0)
    // ============================================================

    [Fact]
    public void Calculate_ProportionalToNet_ExtractionIsExactPerGroup()
    {
        // Caso donde el cociente no es redondo: bruto 0.121 a 21%.
        //   0.121 / 1.21 = 0.1 exacto (sin midpoint).
        //   BaseImp = round(0.1, 2) = 0.10. IVA = 0.121 - 0.10 = 0.021. Pero el bruto solo
        //   tiene 3 decimales, asi que el caso tipico ARCA (2 decimales) no se da aca.
        //
        // Lo importante de la EXTRACCION es la invariante: BaseImp + IVA == bruto a centavo
        // exacto por grupo, sin importar como redondee Math.Round la base. Lo verificamos en
        // un escenario con 3 lineas mixtas que generan residuos no triviales.
        var input = InputWith(
            fiscalAmountToCredit: 100_000.00m + 50_000.00m + 80_000.00m,
            Line(total: 100_000.00m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 50_000.00m, alicuotaIvaId: AlicuotaDiezYMedio),
            Line(total: 80_000.00m, alicuotaIvaId: AlicuotaCero));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        // INVARIANTE: por grupo, BaseImp + IVA == bruto del grupo a centavo exacto.
        // Esto es la propiedad estructural que el caller (BuildInvoiceItemsFromOverride)
        // depende para repartir el IVA sin descuadres.
        foreach (var group in result.VatGroups)
        {
            decimal brutoGrupo = input.Lines
                .Where(l => l.AlicuotaIvaId == group.AlicuotaIvaId)
                .Sum(l => l.Total);
            Assert.Equal(brutoGrupo, group.BaseImponible + group.ImporteIva);
        }

        // Y el total acreditado es la suma de brutos (= FiscalAmountToCredit del input).
        Assert.Equal(input.FiscalAmountToCredit, result.CreditedTotalAmount);
    }

    [Fact]
    public void Calculate_RoundingDriftWithinTolerance_DoesNotThrow()
    {
        // Bruto $121.000,21 a 21%. La extraccion:
        //   BaseImp = round(121000.21 / 1.21, 2) = round(100000.173554..., 2) = 100.000,17.
        //   IVA = 121000.21 - 100000.17 = 21.000,04.
        //   creditedTotal = 121.000,21 (= suma de brutos).
        // Si FiscalAmountToCredit viene como 121.000,22 (1 centavo de gap), entra dentro de
        // la tolerancia default 0.01 y NO debe tirar.
        var input = InputWith(
            fiscalAmountToCredit: 121_000.22m,
            Line(total: 121_000.21m, alicuotaIvaId: AlicuotaVeintiuno));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(121_000.21m, result.CreditedTotalAmount);
    }

    // ============================================================
    // Excede tolerancia -> throw
    // ============================================================

    [Fact]
    public void Calculate_FiscalAmountDoesNotMatchSumOfLines_ThrowsInvalidOperation()
    {
        // Bruto suma de lineas = 121.000. Pero FiscalAmountToCredit dice 121.050 (gap $50,
        // muy por encima de la tolerancia 0.01). El caller le paso al calculator dos numeros
        // contradictorios -> guard debe rebotar.
        var input = InputWith(
            fiscalAmountToCredit: 121_050m,
            Line(total: 121_000m, alicuotaIvaId: AlicuotaVeintiuno));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PartialCreditNoteIvaCalculator.Calculate(
                input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance));

        // El mensaje incluye los numeros para diagnostico (sin datos sensibles).
        Assert.Contains("no cierra", ex.Message);
    }

    [Fact]
    public void Calculate_PerItemFiscalAmountMismatch_Throws()
    {
        // Bruto suma = 0.99 (3 lineas de 0.33). Si FiscalAmountToCredit = 1.18 (gap 0.19),
        // mucho mas que 0.01 de tolerancia -> debe rebotar.
        var input = InputWith(
            fiscalAmountToCredit: 1.18m,
            Line(total: 0.33m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 0.33m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 0.33m, alicuotaIvaId: AlicuotaVeintiuno));

        Assert.Throws<InvalidOperationException>(() =>
            PartialCreditNoteIvaCalculator.Calculate(
                input, IvaProrrateoMode.PerItem, DefaultTolerance));
    }

    // ============================================================
    // Guards de input invalido
    // ============================================================

    [Fact]
    public void Calculate_NullInput_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PartialCreditNoteIvaCalculator.Calculate(
                null!, IvaProrrateoMode.ProportionalToNet, DefaultTolerance));
    }

    [Fact]
    public void Calculate_NoLines_ThrowsArgument()
    {
        var input = new PartialCreditNoteEmissionInput(
            OriginalNetAmount: 0m,
            OriginalVatAmount: 0m,
            OriginalTotalAmount: 0m,
            FiscalAmountToCredit: 0m,
            Currency: "ARS",
            ExchangeRateAtOriginalInvoice: 1m,
            Lines: new List<PartialCreditNoteLineDto>());

        Assert.Throws<ArgumentException>(() =>
            PartialCreditNoteIvaCalculator.Calculate(
                input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance));
    }

    [Fact]
    public void Calculate_NegativeTolerance_ThrowsArgument()
    {
        var input = InputWith(
            fiscalAmountToCredit: 121_000m,
            Line(total: 121_000m, alicuotaIvaId: AlicuotaVeintiuno));

        Assert.Throws<ArgumentException>(() =>
            PartialCreditNoteIvaCalculator.Calculate(
                input, IvaProrrateoMode.ProportionalToNet, roundingTolerance: -0.01m));
    }
}
