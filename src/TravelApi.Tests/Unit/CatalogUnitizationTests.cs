using TravelApi.Domain.Helpers;
using Xunit;

namespace TravelApi.Tests.Unit;

/// <summary>
/// ADR-017 F1.3 (§2.1, R6): la sugerencia del tarifario se guarda UNITARIA para poder re-multiplicarla
/// por las noches/pasajeros de la proxima venta. Si la unidad estuviera mal, el vendedor cotizaria mal.
/// </summary>
public class CatalogUnitizationTests
{
    [Fact]
    public void Hotel_DividesByNightsTimesRooms_AndLabelsNochePorHabitacion()
    {
        // 7 noches x 2 habitaciones = 14 unidades. Total 1400 -> 100 por noche por habitacion (D4).
        var unit = CatalogUnitization.ForHotel(totalNet: 1400m, totalTax: 280m, totalSale: 2100m, nights: 7, rooms: 2);

        Assert.Equal(100m, unit.UnitNetCost);
        Assert.Equal(20m, unit.UnitTax);
        Assert.Equal(150m, unit.UnitSalePrice);
        Assert.Equal(CatalogPriceUnits.NocheHabitacion, unit.PriceUnit);
        Assert.Equal(14, unit.Divisor);
    }

    [Fact]
    public void Package_DividesByPax_ChildrenCountAsWholePerson()
    {
        // 2 adultos + 1 niño = 3 personas. Total 900 -> 300 por persona.
        var unit = CatalogUnitization.ForPackage(totalNet: 900m, totalTax: 0m, totalSale: 1500m, adults: 2, children: 1);

        Assert.Equal(300m, unit.UnitNetCost);
        Assert.Equal(500m, unit.UnitSalePrice);
        Assert.Equal(CatalogPriceUnits.Pasajero, unit.PriceUnit);
    }

    [Fact]
    public void Assistance_DividesByPaxTimesDays()
    {
        var days = CatalogUnitization.AssistanceDays(new DateTime(2026, 7, 1), new DateTime(2026, 7, 11)); // 10 dias
        Assert.Equal(10, days);

        // 2 pax x 10 dias = 20 unidades. Total 400 -> 20 por pasajero por dia.
        var unit = CatalogUnitization.ForAssistance(totalNet: 400m, totalTax: 0m, totalSale: 1000m, adults: 2, children: 0, days: days);

        Assert.Equal(20m, unit.UnitNetCost);
        Assert.Equal(50m, unit.UnitSalePrice);
        Assert.Equal(CatalogPriceUnits.PasajeroDia, unit.PriceUnit);
    }

    [Fact]
    public void Transfer_KeepsTotalAsUnit()
    {
        var unit = CatalogUnitization.ForTransfer(totalNet: 120m, totalTax: 12m, totalSale: 200m);

        Assert.Equal(120m, unit.UnitNetCost);
        Assert.Equal(200m, unit.UnitSalePrice);
        Assert.Equal(CatalogPriceUnits.Servicio, unit.PriceUnit);
        Assert.Equal(1, unit.Divisor);
    }

    [Theory]
    [InlineData(0, 0)]   // divisor cero por noches/habitaciones -> se trata como 1
    [InlineData(-3, -2)] // negativos -> 1
    public void Hotel_ZeroOrNegativeDivisor_TreatedAsOne(int nights, int rooms)
    {
        var unit = CatalogUnitization.ForHotel(totalNet: 500m, totalTax: 0m, totalSale: 800m, nights: nights, rooms: rooms);
        Assert.Equal(500m, unit.UnitNetCost); // total / 1
        Assert.Equal(1, unit.Divisor);
    }

    [Fact]
    public void Assistance_SameDayPolicy_CountsAsOneDay()
    {
        var sameDay = new DateTime(2026, 7, 1);
        Assert.Equal(1, CatalogUnitization.AssistanceDays(sameDay, sameDay));
    }

    [Fact]
    public void RoundTrip_UnitThenTotal_IsStableForExactDivisions()
    {
        var divisor = CatalogUnitization.HotelDivisor(7, 2); // 14
        var unit = CatalogUnitization.ToUnit(1400m, divisor); // 100
        Assert.Equal(1400m, CatalogUnitization.ToTotal(unit, divisor));
    }
}
