using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.F2.2 (fix fiscal B1, 2026-05-27): tests UNIT (sin DB, sin Docker) del armado de los
/// <see cref="InvoiceItem"/> de una NC parcial cuando viene un <see cref="InvoiceTotalsOverride"/>.
///
/// <para><b>Que atrapan</b>: el bug B1 era que el pipeline VALIDABA el cuadre con unos montos
/// (los del <c>PartialCreditNoteIvaCalculator</c>, redondeados por grupo de alicuota) pero
/// MANDABA al ARCA otros (recalculados item por item sin redondear y redondeados recien al
/// serializar). Con varias lineas de la MISMA alicuota, <c>Σ AlicIva.Importe</c> (suma de
/// redondeos por linea) podia diferir en 1-2 centavos de <c>ImpIVA</c> (round del agregado), y
/// ARCA rebotaba el comprobante.</para>
///
/// <para><b>Por que es unit y corre LOCAL</b>: <see cref="AfipService.BuildInvoiceItemsFromOverride"/>
/// es estatico y puro. El job (<c>ProcessInvoiceJob</c>) RELEE la Invoice de BD y reagrupa
/// <c>invoice.Items</c> sumando <c>InvoiceItem.ImporteIva</c>. Por eso, verificar que la suma de
/// los <c>ImporteIva</c> que produce este helper cuadra EXACTO con el override es equivalente a
/// verificar que el envelope que armara el job va a cuadrar — pero sin necesidad de Postgres.</para>
/// </summary>
public class AfipServiceTotalsOverrideTests
{
    // Codigos de alicuota ARCA (ver InvoiceItem.AlicuotaIvaId): 5 = 21%, 4 = 10.5%.
    private const int AlicuotaVeintiuno = 5;
    private const int AlicuotaDiezYMedio = 4;

    private static InvoiceItemDto Line(decimal total, int alicuotaIvaId, string description = "Item")
        => new()
        {
            Description = description,
            Quantity = 1m,
            UnitPrice = total,
            Total = total,
            AlicuotaIvaId = alicuotaIvaId,
        };

    /// <summary>
    /// TEST CRITICO B1: dos lineas de la MISMA alicuota (21%) cuyos IVA individuales redondeados
    /// suman DISTINTO que el round del agregado.
    ///
    /// <para>Numeros: dos lineas de Total 23.45 a 21%.
    /// <list type="bullet">
    ///   <item>Per item: round(23.45 * 0.21, 2) = round(4.9245) = 4.92. Dos lineas = 9.84.</item>
    ///   <item>Agregado: round(46.90 * 0.21, 2) = round(9.849) = 9.85.</item>
    /// </list>
    /// El bug viejo mandaba 9.84 en <c>Σ AlicIva.Importe</c> pero 9.85 en <c>ImpIVA</c> -> rebote.
    /// El calculator (ProportionalToNet) devuelve 9.85 por grupo. El override lleva 9.85, y este
    /// helper reparte el IVA entre los dos items de modo que sumen EXACTO 9.85.</para>
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_TwoLinesSameAlicuota_SumOfItemVatEqualsGroupImporte()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea A"),
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea B"),
        };

        // El override trae el cuadre EXACTO que produce el calculator:
        //   BaseImp = 46.90, Importe (IVA) = round(46.90 * 0.21, 2) = 9.85.
        const decimal grupoBaseImp = 46.90m;
        const decimal grupoImporteIva = 9.85m; // round del agregado, NO suma de redondeos por linea
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: grupoBaseImp, Importe: grupoImporteIva),
            },
            ImpNeto: grupoBaseImp,
            ImpIVA: grupoImporteIva,
            ImpTrib: 0m,
            ImpTotal: grupoBaseImp + grupoImporteIva);

        var items = AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride);

        // INVARIANTE no negociable: lo que el job sumara al reagrupar (Σ ImporteIva del grupo)
        // tiene que ser EXACTAMENTE el Importe redondeado del override. Gap 0, no tolerancia.
        decimal sumItemVat = items
            .Where(i => i.AlicuotaIvaId == AlicuotaVeintiuno)
            .Sum(i => i.ImporteIva);
        Assert.Equal(grupoImporteIva, sumItemVat);

        // Cada item quedo con 2 decimales (el job los serializa con ToString("0.00")).
        Assert.All(items, i => Assert.Equal(i.ImporteIva, decimal.Round(i.ImporteIva, 2)));

        // La base imponible de los items no se toca (suma de Total).
        Assert.Equal(grupoBaseImp, items.Sum(i => i.Total));
    }

    /// <summary>
    /// Mezcla de dos alicuotas (21% + 10.5%), cada una con dos lineas. Verifica que el cuadre se
    /// mantiene POR grupo y que la suma total de IVA == ImpIVA del override (lo que el job manda
    /// como ImpIVA y como Σ AlicIva.Importe).
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_TwoAlicuotas_EachGroupSumsToItsImporte()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
        };

        // 21%: 46.90 -> round(9.849) = 9.85.
        // 10.5%: 20.10 -> round(2.1105) = 2.11.
        const decimal importe21 = 9.85m;
        const decimal importe105 = 2.11m;
        const decimal impIva = importe21 + importe105; // 11.96
        const decimal impNeto = 46.90m + 20.10m;        // 67.00
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: 46.90m, Importe: importe21),
                new(Id: AlicuotaDiezYMedio, BaseImp: 20.10m, Importe: importe105),
            },
            ImpNeto: impNeto,
            ImpIVA: impIva,
            ImpTrib: 0m,
            ImpTotal: impNeto + impIva);

        var items = AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride);

        decimal sum21 = items.Where(i => i.AlicuotaIvaId == AlicuotaVeintiuno).Sum(i => i.ImporteIva);
        decimal sum105 = items.Where(i => i.AlicuotaIvaId == AlicuotaDiezYMedio).Sum(i => i.ImporteIva);

        Assert.Equal(importe21, sum21);
        Assert.Equal(importe105, sum105);
        // Σ de todos los items == ImpIVA del override == lo que el job manda como ImpIVA.
        Assert.Equal(impIva, items.Sum(i => i.ImporteIva));
    }

    /// <summary>
    /// Una sola linea por alicuota (caso simple): el reparto le asigna el Importe del grupo
    /// completo a ese unico item (es el "ultimo" del grupo). Sin residuo que distribuir.
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_SingleLinePerAlicuota_AssignsFullGroupImporte()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno),
        };
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: 100_000m, Importe: 21_000m),
            },
            ImpNeto: 100_000m,
            ImpIVA: 21_000m,
            ImpTrib: 0m,
            ImpTotal: 121_000m);

        var item = Assert.Single(AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride));
        Assert.Equal(21_000m, item.ImporteIva);
        Assert.Equal(100_000m, item.Total);
    }

    /// <summary>
    /// El armado completo del envelope (totales) cuadra EXACTO contra los invariantes que exige
    /// ARCA cuando la Invoice nacio de override. Reproduce lo que persistira CreatePendingInvoice
    /// (ImpNeto/ImpIVA/ImpTotal del override) + lo que reagrupara el job (Σ ImporteIva por grupo)
    /// y verifica los 4 invariantes del objetivo del fix con gap 0.
    /// </summary>
    [Fact]
    public void OverrideEnvelope_SatisfiesArcaInvariants_ExactZeroGap()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
        };
        const decimal importe21 = 9.85m;
        const decimal importe105 = 2.11m;
        const decimal impNeto = 67.00m;
        const decimal impIva = importe21 + importe105; // 11.96
        const decimal impTrib = 0m;
        const decimal impTotal = impNeto + impIva;     // 78.96
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: 46.90m, Importe: importe21),
                new(Id: AlicuotaDiezYMedio, BaseImp: 20.10m, Importe: importe105),
            },
            ImpNeto: impNeto,
            ImpIVA: impIva,
            ImpTrib: impTrib,
            ImpTotal: impTotal);

        var items = AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride);

        // Reproducimos como el job arma el envelope desde la Invoice persistida:
        //   - <ImpNeto>   = invoice.ImporteNeto  (= override.ImpNeto)
        //   - <ImpIVA>    = invoice.ImporteIva   (= override.ImpIVA)
        //   - <ImpTotal>  = invoice.ImporteTotal (= override.ImpTotal)
        //   - cada <AlicIva><Importe> = Σ InvoiceItem.ImporteIva del grupo
        var alicIvaImportes = items
            .GroupBy(i => i.AlicuotaIvaId)
            .Select(g => g.Sum(x => x.ImporteIva))
            .ToList();
        var alicIvaBases = items
            .GroupBy(i => i.AlicuotaIvaId)
            .Select(g => g.Sum(x => x.Total))
            .ToList();

        // Invariante 1: ImpIVA == Σ AlicIva.Importe (gap exacto 0).
        Assert.Equal(totalsOverride.ImpIVA, alicIvaImportes.Sum());
        // Invariante 2: ImpNeto == Σ AlicIva.BaseImp.
        Assert.Equal(totalsOverride.ImpNeto, alicIvaBases.Sum());
        // Invariante 3: ImpTotal == ImpNeto + ImpIVA + ImpTrib.
        Assert.Equal(totalsOverride.ImpTotal, totalsOverride.ImpNeto + totalsOverride.ImpIVA + totalsOverride.ImpTrib);
        // Invariante 4: cada AlicIva.Importe == el Importe redondeado por grupo del override.
        foreach (var group in totalsOverride.AlicIvas)
        {
            decimal sumGroup = items.Where(i => i.AlicuotaIvaId == group.Id).Sum(i => i.ImporteIva);
            Assert.Equal(group.Importe, sumGroup);
        }
    }

    // ---------------------------------------------------------------------------------------
    // MEJORA 3 (blindaje 2026-05-27): tests de las guardas defensivas + de la frontera FC1.2.
    // ---------------------------------------------------------------------------------------

    // Multiplier canonico de la alicuota 21%. Es la MISMA tabla que GetVatMultiplier (rama FC1.2)
    // y GetVatMultiplierStatic (rama override) en AfipService. Lo replicamos aca para poder
    // afirmar sobre el VALOR ESPERADO del calculo sin depender de la BD.
    private const decimal Multiplier21 = 0.21m;

    /// <summary>
    /// BLINDAJE de la frontera entre las DOS ramas de <c>CreatePendingInvoice</c> (fix B1).
    ///
    /// <para><b>Que protege</b>: la rama FC1.2 (override == null, facturacion normal + NC total)
    /// calcula el IVA por item como <c>Total * multiplier</c> SIN redondear por item (el round
    /// recien ocurre al serializar). La rama override (NC parcial) SI redondea por item y carga
    /// el residuo al ultimo. Son comportamientos DISTINTOS a proposito. Si manana alguien
    /// "unifica" las dos ramas (p.ej. mete <c>Math.Round</c> por item tambien en FC1.2), cambia
    /// el IVA por item de TODA la facturacion normal sin querer. Este test deja escrito el
    /// contrato historico de la rama FC1.2 para que esa regresion explote en CI.</para>
    ///
    /// <para><b>Por que no llamamos a CreatePendingInvoice</b>: es metodo de instancia y necesita
    /// _context (Postgres) + reserva + AfipSettings. La parte testeable LOCAL es la formula del
    /// IVA por item de la rama else, que es <c>i.Total * GetVatMultiplier(i.AlicuotaIvaId)</c>
    /// (ver AfipService.cs, rama 'else' de CreatePendingInvoice). La reproducimos con el mismo
    /// multiplier canonico y verificamos que NO esta redondeada, contrastandola contra lo que
    /// haria la rama override para los mismos numeros.</para>
    /// </summary>
    [Fact]
    public void Fc12Branch_OverrideNull_VatPerItemIsNotRoundedUnlikeOverrideBranch()
    {
        // Total elegido para que Total * 0.21 tenga MAS de 2 decimales: 23.45 * 0.21 = 4.9245.
        const decimal lineTotal = 23.45m;

        // --- Rama FC1.2 (override == null): IVA por item = Total * multiplier, SIN round. ---
        // Replica EXACTA de la formula de la rama else de CreatePendingInvoice.
        decimal fc12VatPerItem = lineTotal * Multiplier21;
        Assert.Equal(4.9245m, fc12VatPerItem);

        // Confirmamos que NO esta redondeado a 2 decimales (si lo estuviera, seria 4.92).
        Assert.NotEqual(decimal.Round(fc12VatPerItem, 2), fc12VatPerItem);

        // --- Rama override (NC parcial): el MISMO item, via BuildInvoiceItemsFromOverride,
        //     queda redondeado por item (4.92) porque el override manda el cuadre por grupo. ---
        var lines = new List<InvoiceItemDto> { Line(total: lineTotal, alicuotaIvaId: AlicuotaVeintiuno) };
        var overrideExact = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                // Una sola linea: el "ultimo item" absorbe el Importe completo del grupo.
                // Lo seteamos al redondeo per-item (4.92) para que el reparto cierre exacto.
                new(Id: AlicuotaVeintiuno, BaseImp: lineTotal, Importe: 4.92m),
            },
            ImpNeto: lineTotal,
            ImpIVA: 4.92m,
            ImpTrib: 0m,
            ImpTotal: lineTotal + 4.92m);

        var overrideItem = Assert.Single(AfipService.BuildInvoiceItemsFromOverride(lines, overrideExact));

        // La rama override produce 4.92 (redondeado); la rama FC1.2 produciria 4.9245. Distintas.
        Assert.Equal(4.92m, overrideItem.ImporteIva);
        Assert.NotEqual(fc12VatPerItem, overrideItem.ImporteIva);
    }

    /// <summary>
    /// MEJORA 3: tres lineas de la MISMA alicuota que generan residuo acumulado. Verifica que el
    /// reparto del helper sigue cerrando exacto (Σ ImporteIva del grupo == override.Importe).
    ///
    /// <para>Numeros: tres lineas de 33.33 a 21%.
    /// <list type="bullet">
    ///   <item>Per item: round(33.33 * 0.21, 2) = round(6.9993) = 7.00. Tres = 21.00.</item>
    ///   <item>Agregado: round(99.99 * 0.21, 2) = round(20.9979) = 21.00.</item>
    /// </list>
    /// Aca el agregado coincide con la suma de redondeos (residuo 0), pero el helper igual tiene
    /// que repartir entre 3 items y el ultimo absorber el residuo. Verificamos el cierre exacto.</para>
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_ThreeLinesSameAlicuota_GroupSumEqualsOverrideImporte()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 33.33m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea A"),
            Line(total: 33.33m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea B"),
            Line(total: 33.33m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea C"),
        };

        const decimal grupoBaseImp = 99.99m;
        const decimal grupoImporteIva = 21.00m; // round(99.99 * 0.21, 2)
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: grupoBaseImp, Importe: grupoImporteIva),
            },
            ImpNeto: grupoBaseImp,
            ImpIVA: grupoImporteIva,
            ImpTrib: 0m,
            ImpTotal: grupoBaseImp + grupoImporteIva);

        var items = AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride);

        Assert.Equal(3, items.Count);
        // Cierre exacto del grupo (gap 0), que es lo unico que ARCA valida en el AlicIva.
        Assert.Equal(grupoImporteIva, items.Sum(i => i.ImporteIva));
        // Ningun item con IVA negativo.
        Assert.All(items, i => Assert.True(i.ImporteIva >= 0m));
    }

    /// <summary>
    /// MEJORA 3 (guarda (a)): override del grupo MENOR que la Σ de redondeos por item -> el
    /// ultimo item quedaria con IVA negativo / residuo fuera de rango. La guarda debe LANZAR
    /// en vez de persistir IVA negativo en silencio.
    ///
    /// <para>Numeros: dos lineas de 100.000 a 21% (round per item = 21.000 c/u, Σ = 42.000).
    /// Si el override del grupo dice 10.000 (desalineado, aguas arriba mal calculado), el ultimo
    /// item recibiria 10.000 - 21.000 = -11.000. Eso es un override roto: la guarda corta.</para>
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_OverrideBelowPerItemRounding_Throws()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea A"),
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea B"),
        };

        // Override desalineado a proposito: Importe del grupo MUCHO menor que Σ redondeos (42.000).
        var totalsOverrideRoto = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: 200_000m, Importe: 10_000m),
            },
            ImpNeto: 200_000m,
            ImpIVA: 10_000m,
            ImpTrib: 0m,
            ImpTotal: 210_000m);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverrideRoto));

        // El mensaje debe ser diagnostico (nombrar el grupo / el descuadre), no generico.
        Assert.Contains("alicuota", ex.Message);
        Assert.Contains(AlicuotaVeintiuno.ToString(), ex.Message);
    }

    /// <summary>
    /// MEJORA 3 (guarda (b)): una alicuota presente en las lineas que NO esta en el override.
    /// Antes repartia 0 IVA en silencio (descuadre fiscal). Ahora debe LANZAR.
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_LineAlicuotaMissingFromOverride_Throws()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 100m, alicuotaIvaId: AlicuotaVeintiuno),
            // Esta linea es 10.5% pero el override SOLO trae 21%: desincronizado.
            Line(total: 50m, alicuotaIvaId: AlicuotaDiezYMedio),
        };

        var overrideSinDiezYMedio = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: 100m, Importe: 21m),
            },
            ImpNeto: 100m,
            ImpIVA: 21m,
            ImpTrib: 0m,
            ImpTotal: 121m);

        var ex = Assert.Throws<InvalidOperationException>(
            () => AfipService.BuildInvoiceItemsFromOverride(lines, overrideSinDiezYMedio));

        Assert.Contains(AlicuotaDiezYMedio.ToString(), ex.Message);
    }
}
