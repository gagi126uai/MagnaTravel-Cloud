using System;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using TravelApi.Domain.Helpers;
using TravelApi.Infrastructure.Persistence;
using TravelApi.Infrastructure.Services.Reservations;
using TravelApi.Infrastructure.Time;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.4: dos piezas transversales.
///  - <see cref="AgencyTimezone.TodayWallClockUtc"/>: el corte "hoy" de las alertas usa la fecha LOCAL de
///    Argentina, no la UTC, asi un deadline no se marca "vencido" 3h antes (21:00 ART del dia anterior).
///  - <see cref="CatalogSaleUpsert"/>: cierre del pendiente F1.3 — un costo negativo (que ConvertToFile
///    podria alimentar desde un item de presupuesto) se saltea en el choke point, sin envenenar la tabla.
/// </summary>
public class CatalogTimezoneAndUpsertGuardTests
{
    // ===================== Zona horaria del corte =====================

    [Fact]
    public void TodayWallClockUtc_At9pmArgentina_ReturnsArgentinaDate_NotUtcDate()
    {
        // 2026-06-06 00:00 UTC == 2026-06-05 21:00 en Argentina (UTC-3). La fecha de la agencia es el 05,
        // aunque en UTC ya sea el 06. Si usaramos UtcNow.Date, un deadline del 05 se marcaria "vencido" aca.
        var utcNow = DateTime.SpecifyKind(new DateTime(2026, 6, 6, 0, 0, 0), DateTimeKind.Utc);

        var today = AgencyTimezone.TodayWallClockUtc(utcNow);

        Assert.Equal(new DateTime(2026, 6, 5), today.Date);
        Assert.Equal(DateTimeKind.Utc, today.Kind);
    }

    [Fact]
    public void TodayWallClockUtc_MiddayUtc_MatchesSameCalendarDay()
    {
        // 2026-06-06 12:00 UTC == 09:00 ART: mismo dia calendario en ambos husos.
        var utcNow = DateTime.SpecifyKind(new DateTime(2026, 6, 6, 12, 0, 0), DateTimeKind.Utc);

        var today = AgencyTimezone.TodayWallClockUtc(utcNow);

        Assert.Equal(new DateTime(2026, 6, 6), today.Date);
    }

    [Fact]
    public void DeadlineAt9pmArgentinaPreviousDay_IsNotConsideredOverdue()
    {
        // Caso del enunciado: un deadline del dia D-1 NO esta vencido a las 21:00 ART de D-1 (sigue siendo D-1).
        // El corte usa la fecha local de Argentina, asi que isOverdue (deadline < hoy) es false.
        var utcNow = DateTime.SpecifyKind(new DateTime(2026, 6, 6, 0, 0, 0), DateTimeKind.Utc); // 21:00 ART del 05
        var today = AgencyTimezone.TodayWallClockUtc(utcNow);
        var deadline = DateTime.SpecifyKind(new DateTime(2026, 6, 5), DateTimeKind.Utc);

        Assert.False(deadline < today); // no vencido
    }

    // ===================== Guarda de costo negativo en el upsert =====================

    private static AppDbContext CreateContext()
        => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    [Fact]
    public async Task Upsert_NegativeNetCost_SkippedWithoutWritingRow()
    {
        await using var context = CreateContext();
        var unit = new CatalogUnitization.Unitized(
            UnitNetCost: -5m, UnitTax: 0m, UnitSalePrice: 100m, Divisor: 1, PriceUnit: CatalogPriceUnits.Servicio);

        await CatalogSaleUpsert.UpsertAsync(context, rateId: 1, supplierId: 1, unit, "ARS", DateTime.UtcNow, CancellationToken.None);

        Assert.Empty(context.RateSupplierSales.ToList());
    }

    [Fact]
    public async Task Upsert_NegativeTax_SkippedWithoutWritingRow()
    {
        await using var context = CreateContext();
        var unit = new CatalogUnitization.Unitized(
            UnitNetCost: 10m, UnitTax: -1m, UnitSalePrice: 100m, Divisor: 1, PriceUnit: CatalogPriceUnits.Servicio);

        await CatalogSaleUpsert.UpsertAsync(context, rateId: 1, supplierId: 1, unit, "ARS", DateTime.UtcNow, CancellationToken.None);

        Assert.Empty(context.RateSupplierSales.ToList());
    }

    [Fact]
    public async Task Upsert_ZeroNetCost_IsValid_WritesRow()
    {
        // El 0 SI es valido (D8c "confirmar 0 vale"): no se saltea.
        await using var context = CreateContext();
        var unit = new CatalogUnitization.Unitized(
            UnitNetCost: 0m, UnitTax: 0m, UnitSalePrice: 100m, Divisor: 1, PriceUnit: CatalogPriceUnits.Servicio);

        await CatalogSaleUpsert.UpsertAsync(context, rateId: 1, supplierId: 1, unit, "ARS", DateTime.UtcNow, CancellationToken.None);

        var row = Assert.Single(context.RateSupplierSales.ToList());
        Assert.Equal(0m, row.LastNetCost);
    }
}
