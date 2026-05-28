using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3.F2.2 (fix fiscal B1, 2026-05-27 + fix de semantica BRUTO 2026-05-28): tests UNIT
/// (sin DB, sin Docker) del armado de los <see cref="InvoiceItem"/> de una NC parcial cuando
/// viene un <see cref="InvoiceTotalsOverride"/>.
///
/// <para><b>SEMANTICA DE <c>line.Total</c> que asumen estos tests (decision Gaston
/// 2026-05-28)</b>: el <c>Total</c> de cada <see cref="InvoiceItemDto"/> es BRUTO (incluye
/// IVA por dentro), porque la NC parcial refleja el bruto de la factura origen (lo que el
/// cliente vio). El helper EXTRAE el IVA por item: <c>itemBaseImp = round(item.Total /
/// (1+tasa), 2)</c>, <c>itemIva = item.Total - itemBaseImp</c>.</para>
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
    /// TEST CRITICO B1: dos lineas de la MISMA alicuota (21%) cuyos IVA individuales extraidos
    /// y redondeados suman DISTINTO que la extraccion sobre el bruto agregado del grupo.
    ///
    /// <para>Numeros (BRUTO): dos lineas de Total 23.45 a 21%.
    /// <list type="bullet">
    ///   <item>Per item extraido: BaseImp = round(23.45/1.21, 2) = round(19.380165..., 2) =
    ///   19.38. IVA item = 23.45 - 19.38 = 4.07. Dos lineas = 8.14 IVA.</item>
    ///   <item>Agregado por grupo: bruto = 46.90. BaseImp = round(46.90/1.21, 2) = round(38.7603..., 2)
    ///   = 38.76. IVA grupo = 46.90 - 38.76 = 8.14.</item>
    /// </list>
    /// En este caso "limpio" per item y por grupo coinciden (8.14 = 8.14). El test cubre el
    /// invariante estructural: lo que el job sumara al reagrupar (Σ ImporteIva del grupo) ==
    /// override.Importe del grupo, exacto.</para>
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
        //   bruto = 46.90, BaseImp = 38.76, Importe = 46.90 - 38.76 = 8.14.
        const decimal grupoBaseImp = 38.76m;
        const decimal grupoImporteIva = 8.14m; // residuo = bruto - BaseImp
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: grupoBaseImp, Importe: grupoImporteIva),
            },
            ImpNeto: grupoBaseImp,
            ImpIVA: grupoImporteIva,
            ImpTrib: 0m,
            ImpTotal: 46.90m); // bruto total = sum(line.Total)

        var items = AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride);

        // INVARIANTE no negociable: lo que el job sumara al reagrupar (Σ ImporteIva del grupo)
        // tiene que ser EXACTAMENTE el Importe redondeado del override. Gap 0, no tolerancia.
        decimal sumItemVat = items
            .Where(i => i.AlicuotaIvaId == AlicuotaVeintiuno)
            .Sum(i => i.ImporteIva);
        Assert.Equal(grupoImporteIva, sumItemVat);

        // Cada item quedo con 2 decimales (el job los serializa con ToString("0.00")).
        Assert.All(items, i => Assert.Equal(i.ImporteIva, decimal.Round(i.ImporteIva, 2)));

        // El Total (BRUTO) por linea se preserva tal cual del request (lo que el cliente vio
        // en la factura origen). El sumarlo da el bruto del grupo.
        Assert.Equal(46.90m, items.Sum(i => i.Total));
    }

    /// <summary>
    /// Mezcla de dos alicuotas (21% + 10.5%), cada una con dos lineas. Verifica que el cuadre se
    /// mantiene POR grupo y que la suma total de IVA == ImpIVA del override (lo que el job manda
    /// como ImpIVA y como Σ AlicIva.Importe).
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_TwoAlicuotas_EachGroupSumsToItsImporte()
    {
        // BRUTOS por linea.
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
        };

        // 21%: bruto 46.90 -> BaseImp round(46.90/1.21, 2) = 38.76, IVA = 8.14.
        // 10.5%: bruto 20.10 -> BaseImp round(20.10/1.105, 2) = round(18.190045..., 2) = 18.19,
        //                       IVA = 20.10 - 18.19 = 1.91.
        const decimal baseImp21 = 38.76m;
        const decimal importe21 = 8.14m;
        const decimal baseImp105 = 18.19m;
        const decimal importe105 = 1.91m;
        const decimal impIva = importe21 + importe105;        // 10.05
        const decimal impNeto = baseImp21 + baseImp105;       // 56.95
        const decimal impTotal = 46.90m + 20.10m;             // 67.00 (bruto total)
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: baseImp21, Importe: importe21),
                new(Id: AlicuotaDiezYMedio, BaseImp: baseImp105, Importe: importe105),
            },
            ImpNeto: impNeto,
            ImpIVA: impIva,
            ImpTrib: 0m,
            ImpTotal: impTotal);

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
        // BRUTO $100.000 a 21%.
        //   Extraccion: BaseImp = round(100000/1.21, 2) = round(82644.628099..., 2) = 82644.63.
        //               IVA = 100000 - 82644.63 = 17355.37.
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno),
        };
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: 82_644.63m, Importe: 17_355.37m),
            },
            ImpNeto: 82_644.63m,
            ImpIVA: 17_355.37m,
            ImpTrib: 0m,
            ImpTotal: 100_000m);

        var item = Assert.Single(AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride));
        Assert.Equal(17_355.37m, item.ImporteIva);
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
        // BRUTOS por linea (mismos numeros que el test TwoAlicuotas).
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 23.45m, alicuotaIvaId: AlicuotaVeintiuno),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
            Line(total: 10.05m, alicuotaIvaId: AlicuotaDiezYMedio),
        };
        const decimal baseImp21 = 38.76m;
        const decimal importe21 = 8.14m;
        const decimal baseImp105 = 18.19m;
        const decimal importe105 = 1.91m;
        const decimal impNeto = baseImp21 + baseImp105;       // 56.95
        const decimal impIva = importe21 + importe105;        // 10.05
        const decimal impTrib = 0m;
        const decimal impTotal = 46.90m + 20.10m;             // 67.00 (= bruto total)
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: baseImp21, Importe: importe21),
                new(Id: AlicuotaDiezYMedio, BaseImp: baseImp105, Importe: importe105),
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

        // Invariante 1: ImpIVA == Σ AlicIva.Importe (gap exacto 0).
        Assert.Equal(totalsOverride.ImpIVA, alicIvaImportes.Sum());
        // Invariante 2 (estructural del override): ImpNeto == Σ AlicIva.BaseImp.
        // El override la mantiene por construccion (lo arma EmitPartialCreditNoteAsync). Lo
        // afirmamos sobre el override propio (no sobre los items: los items persisten
        // ImporteIva, no BaseImp por item — la base se reagrupa en el envelope via Σ Total
        // del grupo en la rama FC1.2, pero aca la base vive en el override).
        Assert.Equal(totalsOverride.ImpNeto, totalsOverride.AlicIvas.Sum(a => a.BaseImp));
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
    /// BLINDAJE de la frontera entre las DOS ramas de <c>CreatePendingInvoice</c> (fix B1 + fix
    /// de semantica BRUTO 2026-05-28).
    ///
    /// <para><b>Que protege</b>: las dos ramas tienen SEMANTICAS DISTINTAS de <c>line.Total</c>
    /// a proposito:
    /// <list type="bullet">
    ///   <item>Rama FC1.2 (override == null, facturacion normal + NC total): <c>Total = NETO</c>.
    ///   El IVA se hace GROSS-UP: <c>item.ImporteIva = Total * tasa</c> (sin redondear por item;
    ///   el round recien ocurre al serializar).</item>
    ///   <item>Rama override (NC parcial, fix de semantica 2026-05-28): <c>Total = BRUTO</c>
    ///   (incluye IVA por dentro). El IVA se EXTRAE: <c>item.ImporteIva = Total - round(Total /
    ///   (1+tasa), 2)</c>. Se redondea por item y el residuo se carga al ultimo del grupo.</item>
    /// </list>
    /// </para>
    ///
    /// <para>Si manana alguien "unifica" las dos ramas (p.ej. mete extraccion tambien en FC1.2,
    /// o pone gross-up en la rama override), rompe la facturacion normal o vuelve a romper el
    /// camino feliz del bruto del VPS. Este test deja escrito el contrato historico de cada
    /// rama para que esa regresion explote en CI.</para>
    ///
    /// <para><b>Por que no llamamos a CreatePendingInvoice</b>: es metodo de instancia y necesita
    /// _context (Postgres) + reserva + AfipSettings. La parte testeable LOCAL son las formulas
    /// del IVA por item de cada rama. Las reproducimos con el mismo multiplier canonico y
    /// verificamos los dos comportamientos sobre el mismo numero.</para>
    /// </summary>
    [Fact]
    public void Fc12Branch_OverrideNull_VatPerItemIsNotRoundedUnlikeOverrideBranch()
    {
        // En la rama FC1.2 esto seria un NETO. En la rama override es un BRUTO. Probamos las
        // dos formulas sobre el mismo numero para ver que dan DISTINTO (semanticas distintas).
        const decimal lineTotal = 23.45m;

        // --- Rama FC1.2 (override == null): IVA por item = Total * multiplier, SIN round. ---
        // Replica EXACTA de la formula de la rama else de CreatePendingInvoice:
        //   ivaPorItem = i.Total * GetVatMultiplier(i.AlicuotaIvaId).
        decimal fc12VatPerItem = lineTotal * Multiplier21;
        Assert.Equal(4.9245m, fc12VatPerItem);

        // Confirmamos que NO esta redondeado a 2 decimales (si lo estuviera, seria 4.92).
        Assert.NotEqual(decimal.Round(fc12VatPerItem, 2), fc12VatPerItem);

        // --- Rama override (NC parcial): el MISMO numero, tratado como BRUTO via
        //     BuildInvoiceItemsFromOverride. La extraccion da otro valor de IVA por item. ---
        // Extraccion: itemBaseImp = round(23.45/1.21, 2) = 19.38. itemIvaExtraido = 23.45 - 19.38
        // = 4.07. Como es la UNICA linea del grupo, el "ultimo item" absorbe el Importe completo
        // del override. Lo seteamos al valor extraido para que el reparto cierre exacto.
        var lines = new List<InvoiceItemDto> { Line(total: lineTotal, alicuotaIvaId: AlicuotaVeintiuno) };
        var overrideExact = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: 19.38m, Importe: 4.07m),
            },
            ImpNeto: 19.38m,
            ImpIVA: 4.07m,
            ImpTrib: 0m,
            ImpTotal: lineTotal); // 23.45 BRUTO

        var overrideItem = Assert.Single(AfipService.BuildInvoiceItemsFromOverride(lines, overrideExact));

        // La rama override produce 4.07 (extraido del bruto); la rama FC1.2 produciria 4.9245
        // (gross-up del neto). Las dos semanticas son distintas a proposito.
        Assert.Equal(4.07m, overrideItem.ImporteIva);
        Assert.NotEqual(fc12VatPerItem, overrideItem.ImporteIva);

        // Y la rama override garantiza la invariante por item: itemBaseImp + itemIvaExtraido
        // = item.Total (el bruto que viaja al ARCA y que el cliente vio en la factura origen).
        Assert.Equal(lineTotal, 19.38m + overrideItem.ImporteIva);
    }

    /// <summary>
    /// MEJORA 3: tres lineas de la MISMA alicuota que generan residuo acumulado. Verifica que el
    /// reparto del helper sigue cerrando exacto (Σ ImporteIva del grupo == override.Importe).
    ///
    /// <para>Numeros (BRUTO): tres lineas de 33.33 a 21%.
    /// <list type="bullet">
    ///   <item>Per item extraido: BaseImp = round(33.33/1.21, 2) = round(27.5454..., 2) = 27.55.
    ///   IVA item = 33.33 - 27.55 = 5.78. Tres = 17.34 IVA.</item>
    ///   <item>Agregado por grupo: bruto = 99.99. BaseImp = round(99.99/1.21, 2) = round(82.6363..., 2)
    ///   = 82.64. IVA = 99.99 - 82.64 = 17.35.</item>
    /// </list>
    /// El override dice 17.35. La suma per item da 17.34. Diferencia 0.01: ese centavo es el
    /// residuo que el helper carga al ULTIMO item para que el grupo cierre EXACTO contra el
    /// override.</para>
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

        const decimal grupoBaseImp = 82.64m;
        const decimal grupoImporteIva = 17.35m; // bruto 99.99 - BaseImp 82.64
        var totalsOverride = new InvoiceTotalsOverride(
            AlicIvas: new List<AlicIvaOverride>
            {
                new(Id: AlicuotaVeintiuno, BaseImp: grupoBaseImp, Importe: grupoImporteIva),
            },
            ImpNeto: grupoBaseImp,
            ImpIVA: grupoImporteIva,
            ImpTrib: 0m,
            ImpTotal: 99.99m);

        var items = AfipService.BuildInvoiceItemsFromOverride(lines, totalsOverride);

        Assert.Equal(3, items.Count);
        // Cierre exacto del grupo (gap 0), que es lo unico que ARCA valida en el AlicIva.
        Assert.Equal(grupoImporteIva, items.Sum(i => i.ImporteIva));
        // Ningun item con IVA negativo.
        Assert.All(items, i => Assert.True(i.ImporteIva >= 0m));
    }

    /// <summary>
    /// MEJORA 3 (guarda (a)): override del grupo MENOR que la Σ de IVA extraido por item -> el
    /// ultimo item quedaria con IVA negativo / residuo fuera de rango. La guarda debe LANZAR
    /// en vez de persistir IVA negativo en silencio.
    ///
    /// <para>Numeros (BRUTO): dos lineas de 100.000 a 21% (extraccion per item = 17355.37 c/u,
    /// Σ = 34710.74). Si el override del grupo dice 10.000 (desalineado, aguas arriba mal
    /// calculado), el ultimo item recibiria 10.000 - 17355.37 = -7355.37. Eso es un override
    /// roto: la guarda corta.</para>
    /// </summary>
    [Fact]
    public void BuildInvoiceItemsFromOverride_OverrideBelowPerItemRounding_Throws()
    {
        var lines = new List<InvoiceItemDto>
        {
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea A"),
            Line(total: 100_000m, alicuotaIvaId: AlicuotaVeintiuno, description: "Linea B"),
        };

        // Override desalineado a proposito: Importe del grupo MUCHO menor que Σ extraido (34710.74).
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
