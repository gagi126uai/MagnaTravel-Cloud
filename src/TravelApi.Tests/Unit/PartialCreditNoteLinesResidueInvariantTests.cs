using System.Collections.Generic;
using System.Linq;
using TravelApi.Application.DTOs.Cancellation;
using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Services;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// FC1.3 Fase 2 (Fase2_M2, 2026-05-28): test de regresion que CANDA el invariante de
/// absorcion de residuo de <see cref="BookingCancellationService.BuildPartialCreditNoteLines"/>.
///
/// <para><b>Que invariante candamos y por que importa</b>: las 3 ramas de
/// <c>BuildPartialCreditNoteLines</c> reparten <c>FiscalAmountToCredit</c> en una o varias
/// lineas, y la ULTIMA linea absorbe el residuo de redondeo para que
/// <c>Σ line.Total == FiscalAmountToCredit</c> EXACTO (a centavo, gap 0). Antes de Fase2_M2,
/// la idempotency key de la NC se RE-DERIVABA recalculando el hash desde
/// <c>creditNote.ImporteTotal</c> asumiendo que vale exactamente <c>FiscalAmountToCredit</c>.
/// Si alguien rompe la absorcion de residuo (ej. saca el "la ultima linea absorbe el sobrante"),
/// <c>Σ line.Total</c> dejaria de coincidir, el ImporteTotal de la NC divergiria del monto fiscal
/// y la re-derivacion del barrendero generaria un hash distinto -> podria re-POSTear una NC que
/// ya existe bajo otra key. La columna IdempotencyKey (Fase2_M2) ya blinda eso de raiz, pero este
/// test es la SEGUNDA linea de defensa: si la absorcion se rompe, falla fuerte aca y no en prod.</para>
///
/// <para><b>Sin DB, sin async</b>: <c>BuildPartialCreditNoteLines</c> es un metodo puro
/// (<c>internal static</c>, visible via InternalsVisibleTo). Cada test arma el escenario en
/// memoria y verifica numeros. Corre en milisegundos sin Docker.</para>
/// </summary>
public class PartialCreditNoteLinesResidueInvariantTests
{
    // Codigos de alicuota ARCA (ver InvoiceItem.AlicuotaIvaId): 3=0%, 4=10.5%, 5=21%.
    private const int AlicuotaDiezYMedio = 4;
    private const int AlicuotaVeintiuno = 5;

    /// <summary>
    /// Arma un <see cref="BookingCancellation"/> minimo para ejercitar el reparto. Solo poblamos
    /// lo que lee <c>BuildPartialCreditNoteLines</c> (+ <c>RenderPartialNcDescription</c> en el
    /// caso mono-alicuota, que accede a navegaciones con <c>?.</c> y tolera nulls).
    /// </summary>
    private static BookingCancellation BuildBc(decimal fiscalAmountToCredit, ReviewRequiredReason reason)
    {
        return new BookingCancellation
        {
            Reason = "Test de invariante de residuo",
            ReviewRequiredReason = reason,
            FiscalLiquidation = new FiscalLiquidation
            {
                FiscalAmountToCredit = fiscalAmountToCredit,
                Currency = "ARS",
            },
        };
    }

    private static InvoiceItem Item(decimal total, int alicuotaIvaId, bool refundable = true, string description = "Item")
    {
        return new InvoiceItem
        {
            Description = description,
            Quantity = 1m,
            Total = total,
            AlicuotaIvaId = alicuotaIvaId,
            IsRefundable = refundable,
        };
    }

    private static OperationalFinanceSettings DefaultSettings() => new();

    // ============================================================
    // RAMA 1: hay items NO reintegrables -> prorrateo sobre los refundables.
    // Elegimos un fiscalAmountToCredit que NO divide redondo entre los items refundable,
    // forzando residuo de redondeo que la ultima linea debe absorber.
    // ============================================================
    [Fact]
    public void NonRefundableBranch_SumOfLineTotals_EqualsFiscalAmountExactly()
    {
        // 3 items refundable (100, 100, 100 = 300 bruto) + 1 no reintegrable.
        // FiscalAmountToCredit = 200.05 -> factor 0.66683..., cada linea redondea y la
        // ultima absorbe el sobrante para cuadrar a 200.05 exacto.
        var fiscalAmount = 200.05m;
        var bc = BuildBc(fiscalAmount, ReviewRequiredReason.HasNonRefundableItems);
        var items = new List<InvoiceItem>
        {
            Item(total: 100m, alicuotaIvaId: AlicuotaVeintiuno, refundable: true),
            Item(total: 100m, alicuotaIvaId: AlicuotaVeintiuno, refundable: true),
            Item(total: 100m, alicuotaIvaId: AlicuotaVeintiuno, refundable: true),
            Item(total: 50m, alicuotaIvaId: AlicuotaVeintiuno, refundable: false),
        };

        var lines = BookingCancellationService.BuildPartialCreditNoteLines(bc, items, DefaultSettings());

        // EXACTO a centavo (gap 0), NO dentro de tolerancia: este es el invariante que candamos.
        Assert.Equal(fiscalAmount, lines.Sum(line => line.Total));
    }

    // ============================================================
    // RAMA 2: multi-alicuota (sin items no reintegrables) -> una linea por alicuota,
    // prorrateo proporcional, ultima linea absorbe residuo.
    // ============================================================
    [Fact]
    public void MultiAlicuotaBranch_SumOfLineTotals_EqualsFiscalAmountExactly()
    {
        // Dos alicuotas con pesos que generan residuo de redondeo (1/3 - 2/3 sobre un monto
        // que no divide exacto). FiscalAmountToCredit = 1000.01.
        var fiscalAmount = 1000.01m;
        var bc = BuildBc(fiscalAmount, ReviewRequiredReason.None);
        var items = new List<InvoiceItem>
        {
            Item(total: 100m, alicuotaIvaId: AlicuotaDiezYMedio),  // grupo A
            Item(total: 200m, alicuotaIvaId: AlicuotaVeintiuno),   // grupo B
        };

        var lines = BookingCancellationService.BuildPartialCreditNoteLines(bc, items, DefaultSettings());

        Assert.Equal(2, lines.Count); // una linea por alicuota
        Assert.Equal(fiscalAmount, lines.Sum(line => line.Total));
    }

    // ============================================================
    // RAMA 3: mono-alicuota (sin items no reintegrables) -> una sola linea con el monto entero.
    // Aca el cuadre es trivial (Total = fiscalAmount), pero lo candamos igual: si alguien
    // cambia esa rama (ej. recalcula el Total) el invariante debe seguir valiendo.
    // ============================================================
    [Fact]
    public void MonoAlicuotaBranch_SingleLineTotal_EqualsFiscalAmountExactly()
    {
        var fiscalAmount = 777.77m;
        var bc = BuildBc(fiscalAmount, ReviewRequiredReason.None);
        var items = new List<InvoiceItem>
        {
            Item(total: 500m, alicuotaIvaId: AlicuotaVeintiuno),
            Item(total: 300m, alicuotaIvaId: AlicuotaVeintiuno), // misma alicuota -> mono grupo
        };

        var lines = BookingCancellationService.BuildPartialCreditNoteLines(bc, items, DefaultSettings());

        Assert.Single(lines);
        Assert.Equal(fiscalAmount, lines.Sum(line => line.Total));
    }
}
