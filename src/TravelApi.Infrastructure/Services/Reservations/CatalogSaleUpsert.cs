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
/// <para>En Postgres es un <c>INSERT ... ON CONFLICT (RateId, SupplierId) DO UPDATE ... SalesCount + 1</c>
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
            await UpsertInMemoryAsync(db, rateId, supplierId, unit, currency, soldAtUtc, ct);
            return;
        }

        const string sql = @"
            INSERT INTO ""RateSupplierSales""
                (""RateId"", ""SupplierId"", ""LastSoldAt"", ""LastNetCost"", ""LastTax"",
                 ""LastSalePrice"", ""LastCurrency"", ""LastPriceUnit"", ""SalesCount"")
            VALUES
                ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, 1)
            ON CONFLICT (""RateId"", ""SupplierId"") DO UPDATE SET
                ""LastSoldAt""    = EXCLUDED.""LastSoldAt"",
                ""LastNetCost""   = EXCLUDED.""LastNetCost"",
                ""LastTax""       = EXCLUDED.""LastTax"",
                ""LastSalePrice"" = EXCLUDED.""LastSalePrice"",
                ""LastCurrency""  = EXCLUDED.""LastCurrency"",
                ""LastPriceUnit"" = EXCLUDED.""LastPriceUnit"",
                ""SalesCount""    = ""RateSupplierSales"".""SalesCount"" + 1;";

        await db.Database.ExecuteSqlRawAsync(
            sql, new object[]
            {
                rateId, supplierId, soldAtUtc, unit.UnitNetCost, unit.UnitTax,
                unit.UnitSalePrice, (object?)currency ?? DBNull.Value, unit.PriceUnit
            }, ct);
    }

    // Solo para tests InMemory (no concurrency-safe). En prod corre el ON CONFLICT atomico de arriba.
    private static async Task UpsertInMemoryAsync(
        AppDbContext db, int rateId, int supplierId, CatalogUnitization.Unitized unit, string? currency,
        DateTime soldAtUtc, CancellationToken ct)
    {
        var row = await db.RateSupplierSales
            .FirstOrDefaultAsync(s => s.RateId == rateId && s.SupplierId == supplierId, ct);

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
                SalesCount = 1
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
        }

        await db.SaveChangesAsync(ct);
    }
}
