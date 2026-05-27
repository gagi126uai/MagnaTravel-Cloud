using System;
using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.F2.2 (plan tactico Fase 2 §FC1.3.F2.2 punto 5, 2026-05-27): tests unit del
/// prorrateo de IVA de la NC parcial.
///
/// <para><b>Sin DB, sin async, sin TestContainers</b>: <see cref="PartialCreditNoteIvaCalculator"/>
/// es un helper puro. Cada test arma el input en memoria y verifica numeros. Corren en
/// milisegundos sin Docker — por eso son unit y no integration.</para>
///
/// <para>Cubre los casos del plan §FC1.3.F2.2 punto 5:
/// <list type="bullet">
///   <item>ProportionalToNet con 1 sola alicuota.</item>
///   <item>ProportionalToNet con 2 alicuotas mezcladas (10.5% + 21%).</item>
///   <item>PerItem con la misma config.</item>
///   <item>Borde de redondeo (.005 / redondeo bancario de Math.Round).</item>
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
        decimal net = lines.Sum(line => line.Total);
        return new PartialCreditNoteEmissionInput(
            OriginalNetAmount: net,
            OriginalVatAmount: 0m,
            OriginalTotalAmount: net,
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
    public void Calculate_ProportionalToNet_SingleAlicuota21_ComputesVatAndTotal()
    {
        // Una sola linea neto $100.000 a 21% -> IVA $21.000 -> total $121.000.
        var input = InputWith(
            fiscalAmountToCredit: 121_000m,
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(100_000m, result.CreditedNetAmount);
        Assert.Equal(21_000m, result.CreditedVatAmount);
        Assert.Equal(121_000m, result.CreditedTotalAmount);

        var group = Assert.Single(result.VatGroups);
        Assert.Equal(AlicuotaVeintiuno, group.AlicuotaIvaId);
        Assert.Equal(100_000m, group.BaseImponible);
        Assert.Equal(21_000m, group.ImporteIva);
    }

    [Fact]
    public void Calculate_ProportionalToNet_SingleAlicuota105_ComputesVatAndTotal()
    {
        // Neto $50.000 a 10.5% -> IVA $5.250 -> total $55.250.
        var input = InputWith(
            fiscalAmountToCredit: 55_250m,
            Line(total: 50_000m, alicuotaIvaId: AlicuotaDiezYMedio));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(50_000m, result.CreditedNetAmount);
        Assert.Equal(5_250m, result.CreditedVatAmount);
        Assert.Equal(55_250m, result.CreditedTotalAmount);
    }

    [Fact]
    public void Calculate_ProportionalToNet_AlicuotaCero_ProducesNoVat()
    {
        // Alicuota 0% (codigo 3): el IVA tiene que ser 0 y el total = neto.
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
    public void Calculate_ProportionalToNet_SameAlicuotaTwoLines_GroupsIntoOneGroup()
    {
        // Dos lineas a 21% deben colapsar en UN solo grupo de alicuota.
        // Neto total $30.000 -> IVA $6.300 -> total $36.300.
        var input = InputWith(
            fiscalAmountToCredit: 36_300m,
            Line(total: 10_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea A"),
            Line(total: 20_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea B"));

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
    public void Calculate_ProportionalToNet_TwoAlicuotas_ComputesPerGroupAndSums()
    {
        // Linea 21%: neto $100.000 -> IVA $21.000.
        // Linea 10.5%: neto $40.000 -> IVA $4.200.
        // Neto total $140.000 + IVA $25.200 = total $165.200.
        var input = InputWith(
            fiscalAmountToCredit: 165_200m,
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Aereo 21%"),
            Line(total: 40_000m, alicuotaIvaId: AlicuotaDiezYMedio, description: "Hotel 10.5%"));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(140_000m, result.CreditedNetAmount);
        Assert.Equal(25_200m, result.CreditedVatAmount);
        Assert.Equal(165_200m, result.CreditedTotalAmount);

        Assert.Equal(2, result.VatGroups.Count);

        var grupo21 = result.VatGroups.Single(g => g.AlicuotaIvaId == AlicuotaVeintiuno);
        Assert.Equal(100_000m, grupo21.BaseImponible);
        Assert.Equal(21_000m, grupo21.ImporteIva);

        var grupo105 = result.VatGroups.Single(g => g.AlicuotaIvaId == AlicuotaDiezYMedio);
        Assert.Equal(40_000m, grupo105.BaseImponible);
        Assert.Equal(4_200m, grupo105.ImporteIva);
    }

    // ============================================================
    // PerItem — misma config
    // ============================================================

    [Fact]
    public void Calculate_PerItem_TwoAlicuotas_MatchesProportionalWhenNoRoundingDrift()
    {
        // Mismos numeros redondos que el test proporcional de 2 alicuotas: como no hay
        // centavos partidos, PerItem y ProportionalToNet deben dar identico.
        var lines = new[]
        {
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 40_000m, alicuotaIvaId: AlicuotaDiezYMedio),
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
    public void Calculate_PerItem_RoundsEachLineSeparately()
    {
        // Dos lineas a 21% con base que produce centavos partidos por linea:
        //   Linea $100,01 * 0.21 = 21,0021 -> redondea a 21,00.
        //   Linea $100,01 * 0.21 = 21,0021 -> redondea a 21,00.
        // PerItem suma los IVA ya redondeados por linea: 21,00 + 21,00 = 42,00.
        // Neto $200,02 + IVA $42,00 = total $242,02.
        var input = InputWith(
            fiscalAmountToCredit: 242.02m,
            Line(total: 100.01m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea 1"),
            Line(total: 100.01m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea 2"));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.PerItem, DefaultTolerance);

        Assert.Equal(200.02m, result.CreditedNetAmount);
        Assert.Equal(42.00m, result.CreditedVatAmount);
        Assert.Equal(242.02m, result.CreditedTotalAmount);
    }

    // ============================================================
    // Borde de redondeo (.005 / banker's rounding de Math.Round)
    // ============================================================

    [Fact]
    public void Calculate_ProportionalToNet_HalfCent_UsesBankersRounding()
    {
        // Base elegida para que el IVA caiga exactamente en .005:
        //   neto $0,50 * 0.21 = 0,105 -> Math.Round(0,105, 2) = 0,10 (banker's: 0->par).
        // Documentamos el comportamiento real de Math.Round (MidpointRounding.ToEven),
        // que es el default de .NET y el que usa AfipService.
        var input = InputWith(
            fiscalAmountToCredit: 0.60m,
            Line(total: 0.50m, alicuotaIvaId: AlicuotaVeintiuno));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        // 0,105 redondea a 0,10 (no 0,11) por banker's rounding hacia el par.
        Assert.Equal(0.10m, result.CreditedVatAmount);
        Assert.Equal(0.60m, result.CreditedTotalAmount);
    }

    [Fact]
    public void Calculate_RoundingDriftWithinTolerance_DoesNotThrow()
    {
        // El IVA calculado da 21.000,21 (100.001 * 0.21) -> total 121.001,21.
        // Si FiscalAmountToCredit viene como 121.001,22 (1 centavo de gap), entra dentro
        // de la tolerancia default 0.01 y NO debe tirar.
        var input = InputWith(
            fiscalAmountToCredit: 121_001.22m,
            Line(total: 100_001m, alicuotaIvaId: AlicuotaVeintiuno));

        var result = PartialCreditNoteIvaCalculator.Calculate(
            input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance);

        Assert.Equal(121_001.21m, result.CreditedTotalAmount);
    }

    // ============================================================
    // Excede tolerancia -> throw
    // ============================================================

    [Fact]
    public void Calculate_SumExceedsTolerance_ThrowsInvalidOperation()
    {
        // Neto $100.000 a 21% -> total calculado $121.000. Pero FiscalAmountToCredit dice
        // $121.050 (gap $50, muy por encima de la tolerancia 0.01). Debe rebotar.
        var input = InputWith(
            fiscalAmountToCredit: 121_050m,
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            PartialCreditNoteIvaCalculator.Calculate(
                input, IvaProrrateoMode.ProportionalToNet, DefaultTolerance));

        // El mensaje incluye los numeros para diagnostico (sin datos sensibles).
        Assert.Contains("no cierra", ex.Message);
        Assert.Contains("121050", ex.Message.Replace(".", string.Empty).Replace(",", string.Empty));
    }

    [Fact]
    public void Calculate_PerItemRoundingPushesOverTolerance_Throws()
    {
        // Caso donde el redondeo por item se aleja mas que la tolerancia del fiscal esperado.
        // 3 lineas $0,33 a 21%: por item 0,33*0,21 = 0,0693 -> 0,07 cada una -> IVA $0,21.
        // Neto $0,99 + IVA $0,21 = $1,20. Si el fiscal esperado fuera $1,18 (gap 0,02 > 0,01),
        // debe rebotar.
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
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno));

        Assert.Throws<ArgumentException>(() =>
            PartialCreditNoteIvaCalculator.Calculate(
                input, IvaProrrateoMode.ProportionalToNet, roundingTolerance: -0.01m));
    }
}
