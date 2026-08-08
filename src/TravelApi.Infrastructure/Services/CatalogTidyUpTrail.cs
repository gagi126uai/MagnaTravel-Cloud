using TravelApi.Domain.Entities;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services;

/// <summary>
/// El MECANISMO ÚNICO de "esconder con rastro" del tarifario (orden del dueño 2026-08-03: nada se borra;
/// spec firmada 2026-08-07, §6 y §10).
///
/// <para><b>Por qué existe como pieza aparte</b>: hay DOS lugares donde dos filas de memoria de precio
/// pueden chocar y una tiene que dejar de verse — unir dos productos repetidos (el bibliotecario) y
/// corregir cómo se llama una habitación (M-18, si al corregirla queda igual que otra). Si cada uno lo
/// resolviera a su manera, uno de los dos terminaría borrando la fila y el Deshacer sería una promesa
/// vacía. Acá vive el único camino: <b>foto primero, esconder después</b>.</para>
///
/// <para><b>El orden importa</b>: la foto se saca ANTES de tocar la fila
/// (<see cref="Snapshot"/>), porque lo que el Deshacer necesita es cómo estaba, no cómo quedó. Por eso el
/// llamador saca la foto y recién después decide qué le pasó a la fila.</para>
/// </summary>
public static class CatalogTidyUpTrail
{
    /// <summary>
    /// La foto de una fila de precio TAL COMO ESTÁ AHORA, todavía sin decidir qué le pasó. El
    /// <c>Kind</c> lo pone después el llamador con uno de los tres métodos de abajo.
    /// </summary>
    public static CatalogTidyUpSaleChange Snapshot(RateSupplierSale sale, int actionId) => new()
    {
        TidyUpActionId = actionId,
        RateSupplierSaleId = sale.Id,
        PreviousRateId = sale.RateId,
        PreviousSupplierId = sale.SupplierId,
        PreviousVariantKey = sale.VariantKey,
        PreviousVariantLabel = sale.VariantLabel,
        PreviousSoldAt = sale.LastSoldAt,
        PreviousNetCost = sale.LastNetCost,
        PreviousTax = sale.LastTax,
        PreviousSalePrice = sale.LastSalePrice,
        PreviousCurrency = sale.LastCurrency,
        PreviousPriceUnit = sale.LastPriceUnit,
        PreviousReservaId = sale.LastReservaId,
        PreviousSalesCount = sale.SalesCount
    };

    /// <summary>
    /// La fila PERDIÓ contra otra igual (mismo operador, misma habitación): queda ESCONDIDA, nunca
    /// borrada. Deshacer la vuelve a mostrar exactamente como estaba.
    /// </summary>
    public static void Hide(
        AppDbContext db, RateSupplierSale losing, CatalogTidyUpSaleChange snapshotTakenBefore, int actionId)
    {
        snapshotTakenBefore.Kind = CatalogTidyUpSaleChangeKinds.Hidden;
        db.CatalogTidyUpSaleChanges.Add(snapshotTakenBefore);
        losing.AbsorbedByTidyUpActionId = actionId;
    }

    /// <summary>
    /// A la fila que se queda le PISARON los importes con los de una más nueva. La foto guarda los
    /// importes viejos: sin ella, esa plata no volvería nunca.
    /// </summary>
    public static void RecordOverwrite(AppDbContext db, CatalogTidyUpSaleChange snapshotTakenBefore)
    {
        snapshotTakenBefore.Kind = CatalogTidyUpSaleChangeKinds.Overwritten;
        db.CatalogTidyUpSaleChanges.Add(snapshotTakenBefore);
    }

    /// <summary>
    /// La fila cambió de producto o de habitación (unir / corregir la etiqueta). Deshacer le devuelve el
    /// producto, la clave y la etiqueta que tenía.
    /// </summary>
    public static void RecordMove(AppDbContext db, CatalogTidyUpSaleChange snapshotTakenBefore)
    {
        snapshotTakenBefore.Kind = CatalogTidyUpSaleChangeKinds.Moved;
        db.CatalogTidyUpSaleChanges.Add(snapshotTakenBefore);
    }
}
