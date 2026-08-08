using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Entities;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;

namespace TravelApi.Infrastructure.Services.Reservations;

/// <summary>
/// ADR-017 F1.3 (§2.1, R7): HELPER UNICO de upsert de <c>RateSupplierSale</c> ("ultima venta por
/// producto y operador"). El ADR exige que TODO escritor de esa tabla pase por aca para que la
/// denormalizacion no se desincronice — lo usan el path transaccional de <c>BookingService</c> y el
/// path post-exito best-effort de <c>QuoteService.ConvertToFileAsync</c>.
///
/// <para>2026-08-07 (M-12): la clave de la memoria pasa a ser <b>(producto, operador, VARIANTE)</b> —
/// la habitacion del hotel, la cabina del aereo o el vehiculo del traslado. Vender una triple ya no
/// pisa el precio de la doble.</para>
///
/// <para>En Postgres es un <c>INSERT ... ON CONFLICT (RateId, SupplierId, VariantKey) DO UPDATE ... SalesCount + 1</c>
/// ATOMICO: nunca tira 23505 (lo absorbe el ON CONFLICT) ni hace read-modify-write (que perderia
/// incrementos concurrentes). En motores no relacionales (tests InMemory) cae a un find-then-upsert con
/// EF que NO es concurrency-safe (la carrera real se prueba contra Postgres en el VPS).</para>
/// </summary>
public static class CatalogSaleUpsert
{
    /// <summary>
    /// Upsertea la combinacion (producto, operador) con precios UNITARIOS. Se saltea silenciosamente si
    /// <paramref name="supplierId"/> &lt;= 0 (fallback 0 de la conversion de presupuesto: evita FK rota y
    /// filas basura). <paramref name="currency"/> puede ser null (path best-effort sin moneda).
    /// </summary>
    public static async Task UpsertAsync(
        AppDbContext db,
        int rateId,
        int supplierId,
        CatalogUnitization.Unitized unit,
        string? currency,
        DateTime soldAt,
        int? reservaId,
        (string Key, string Label) variant,
        CancellationToken ct)
    {
        if (supplierId <= 0) return;

        // ADR-017 F1.4 (cierre del pendiente F1.3, decision del dueño 1 "negativos invalidos"): un costo
        // (neto o impuesto) NEGATIVO no tiene sentido de negocio (no existe una compra a valor negativo) y
        // envenenaria LastNetCost para el proximo vendedor. El path de BookingService ya rechaza negativos
        // antes (EnsureNonNegativeCost -> 400), pero QuoteService.ConvertToFileAsync NO valida los costos de
        // los items del presupuesto. Como este es el UNICO escritor de RateSupplierSale, lo blindamos aca:
        // un negativo se saltea silenciosamente (la conversion ya quedo commiteada; la reconciliacion R7
        // detecta el faltante). El 0 SI es valido y se upsertea normal.
        if (unit.UnitNetCost < 0m || unit.UnitTax < 0m) return;

        var soldAtUtc = DateTime.SpecifyKind(soldAt, DateTimeKind.Utc);

        if (!db.Database.IsRelational())
        {
            await UpsertInMemoryAsync(db, rateId, supplierId, unit, currency, soldAtUtc, reservaId, variant, ct);
            return;
        }

        const string sql = @"
            INSERT INTO ""RateSupplierSales""
                (""RateId"", ""SupplierId"", ""LastSoldAt"", ""LastNetCost"", ""LastTax"",
                 ""LastSalePrice"", ""LastCurrency"", ""LastPriceUnit"", ""SalesCount"", ""LastReservaId"",
                 ""VariantKey"", ""VariantLabel"")
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, 1, {8}, {9}, {10})
            -- El WHERE del ON CONFLICT NO es un filtro de filas: le dice a Postgres CUAL indice mirar. El
            -- unico es PARCIAL (solo filas visibles), asi que sin esta linea el INSERT ni siquiera arranca.
            -- Efecto de negocio: una venta nueva jamas se aprende en una fila que una union dejo escondida
            -- —antes la pisaba en silencio y el precio quedaba donde nadie lo ve—; nace una fila visible.
            ON CONFLICT (""RateId"", ""SupplierId"", ""VariantKey"")
                WHERE ""AbsorbedByTidyUpActionId"" IS NULL
            DO UPDATE SET
                ""LastSoldAt""    = EXCLUDED.""LastSoldAt"",
                ""LastNetCost""   = EXCLUDED.""LastNetCost"",
                ""LastTax""       = EXCLUDED.""LastTax"",
                ""LastSalePrice"" = EXCLUDED.""LastSalePrice"",
                ""LastCurrency""  = EXCLUDED.""LastCurrency"",
                ""LastPriceUnit"" = EXCLUDED.""LastPriceUnit"",
                ""SalesCount""    = ""RateSupplierSales"".""SalesCount"" + 1,
                -- La reserva de origen se pisa con la de la venta nueva SOLO si vino; si el caller no la
                -- sabe, se conserva la anterior (mejor un enlace viejo que ninguno).
                ""LastReservaId"" = COALESCE(EXCLUDED.""LastReservaId"", ""RateSupplierSales"".""LastReservaId""),
                -- La etiqueta se refresca con la ultima venta (si le corrigieron la escritura, gana la nueva).
                ""VariantLabel"" = EXCLUDED.""VariantLabel"";";

        await db.Database.ExecuteSqlRawAsync(
            sql, new object[]
            {
                rateId, supplierId, soldAtUtc, unit.UnitNetCost, unit.UnitTax,
                unit.UnitSalePrice, (object?)currency ?? DBNull.Value, unit.PriceUnit,
                (object?)reservaId ?? DBNull.Value, variant.Key, variant.Label
            }, ct);
    }

    // Solo para tests InMemory (no concurrency-safe). En prod corre el ON CONFLICT atomico de arriba.
    private static async Task UpsertInMemoryAsync(
        AppDbContext db, int rateId, int supplierId, CatalogUnitization.Unitized unit, string? currency,
        DateTime soldAtUtc, int? reservaId, (string Key, string Label) variant, CancellationToken ct)
    {
        // Mismo criterio que el indice parcial de Postgres: las filas ESCONDIDAS por una union no existen
        // para la venta. Si no se filtraran, la venta nueva se aprenderia en una fila invisible.
        var row = await db.RateSupplierSales
            .FirstOrDefaultAsync(
                s => s.RateId == rateId
                     && s.SupplierId == supplierId
                     && s.VariantKey == variant.Key
                     && s.AbsorbedByTidyUpActionId == null, ct);

        if (row == null)
        {
            await db.RateSupplierSales.AddAsync(new RateSupplierSale
            {
                RateId = rateId,
                SupplierId = supplierId,
                LastSoldAt = soldAtUtc,
                LastNetCost = unit.UnitNetCost,
                LastTax = unit.UnitTax,
                LastSalePrice = unit.UnitSalePrice,
                LastCurrency = currency,
                LastPriceUnit = unit.PriceUnit,
                SalesCount = 1,
                LastReservaId = reservaId,
                VariantKey = variant.Key,
                VariantLabel = variant.Label
            }, ct);
        }
        else
        {
            row.LastSoldAt = soldAtUtc;
            row.LastNetCost = unit.UnitNetCost;
            row.LastTax = unit.UnitTax;
            row.LastSalePrice = unit.UnitSalePrice;
            row.LastCurrency = currency;
            row.LastPriceUnit = unit.PriceUnit;
            row.SalesCount += 1;
            // Mismo criterio que el ON CONFLICT de Postgres: si el caller no sabe la reserva, no se borra
            // el enlace que ya habia.
            row.LastReservaId = reservaId ?? row.LastReservaId;
            row.VariantLabel = variant.Label;
        }

        await db.SaveChangesAsync(ct);
    }
}
